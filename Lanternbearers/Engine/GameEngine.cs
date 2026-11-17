using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Lanternbearers.Engine
{
    public class Scenario
    {
        public string Name;
        public string Description;
        public List<CardDef> QuestStages = new();
        public List<CardDef> EncounterCards = new(); // each def's Copies are used
        public List<CardDef> SetAside = new();
    }

    public class GameAction
    {
        public Card Card;
        public string Label;
        public Func<Task> Execute;
    }

    /// <summary>
    /// Solo rules engine. Runs as an async state machine; it awaits the player interface
    /// for every decision. All logic runs on the game's main thread.
    /// </summary>
    public class GameEngine
    {
        public const int DreadLimit = 50;
        public readonly IPlayerInterface UI;
        public readonly Random Rng = new();

        // Player zones
        public readonly List<Card> Deck = new();
        public readonly List<Card> Hand = new();
        public readonly List<Card> Discard = new();
        public readonly List<Card> PlayArea = new();
        public readonly List<Card> Engaged = new();
        public int Dread;

        // Encounter zones
        public readonly List<Card> EncounterDeck = new();
        public readonly List<Card> EncounterDiscard = new();
        public readonly List<Card> Staging = new();
        public readonly List<Card> QuestDeck = new();
        public readonly List<Card> Victory = new();
        public readonly List<Card> SetAside = new();
        public Card ActiveLocation;
        public Card Quest;

        public int Round;
        public Phase Phase = Phase.Setup;
        public string Step = "";
        public readonly List<TempModifier> TempMods = new();
        public readonly List<LastingModifier> Lasting = new();
        public readonly List<string> Log = new();
        public bool IsOver;
        public bool Won;
        public string OverMessage;
        public Scenario Scenario;
        public bool AutoSkipEmptyWindows = true;

        // Temporary combat/phase state used by player card effects
        public readonly HashSet<Card> CannotAttackThisPhase = new();
        public bool MultipleDefendersThisPhase;
        public Card AttackTarget;
        public List<Card> AttackingCharacters = new();
        private readonly List<Func<Task>> _endOfPhase = new();

        /// <summary>Schedules an effect to resolve at the end of the current phase.</summary>
        public void AtEndOfPhase(Func<Task> effect) => _endOfPhase.Add(effect);

        private bool _pendingSurge;
        private int _questStageIndex;
        private readonly List<Card> _queuedAttacks = new();

        /// <summary>Named flags scripts can set for the rest of the game.</summary>
        public readonly HashSet<string> Flags = new();
        /// <summary>When set, the staging step of the quest phase is skipped.</summary>
        public bool SkipStagingStep;
        /// <summary>Conditions attached to the top of the player's deck (they cancel the next draw).</summary>
        public readonly List<Card> DeckConditions = new();
        /// <summary>Added to the total staging threat until the end of the phase.</summary>
        public int StagingThreatBonus;
        public int AlliesPlayedThisRound;
        /// <summary>A hero taken prisoner by a scenario effect (out of play until rescued).</summary>
        public Card Prisoner;

        public GameEngine(IPlayerInterface ui) { UI = ui; }

        public IEnumerable<Card> Heroes => PlayArea.Where(c => c.IsHero);
        public IEnumerable<Card> Allies => PlayArea.Where(c => c.IsAlly);
        public IEnumerable<Card> Characters => PlayArea.Where(c => c.IsCharacter);

        public void Write(string s)
        {
            Log.Add(s);
            if (Log.Count > 300) Log.RemoveAt(0);
        }

        // =====================================================================
        // Stats
        // =====================================================================

        public IEnumerable<Card> AllInPlay()
        {
            foreach (var c in PlayArea) { yield return c; foreach (var a in c.Attachments) yield return a; }
            foreach (var c in Engaged) { yield return c; foreach (var a in c.Attachments) yield return a; }
            foreach (var c in Staging) { yield return c; foreach (var a in c.Attachments) yield return a; }
            if (ActiveLocation != null) { yield return ActiveLocation; foreach (var a in ActiveLocation.Attachments) yield return a; }
            if (Quest != null) yield return Quest;
        }

        public int BaseStat(Card c, Stat s) => s switch
        {
            Stat.Resolve => c.Def.Resolve,
            Stat.Might => c.Def.Might,
            Stat.Guard => c.Def.Guard,
            Stat.Health => c.Def.Health,
            Stat.Threat => c.Def.Threat,
            Stat.Engagement => c.Def.Engagement,
            Stat.QuestPoints => c.Def.QuestPoints,
            _ => 0
        };

        public int Get(Card c, Stat s)
        {
            int v = BaseStat(c, s);
            foreach (var src in AllInPlay().ToList())
                if (src.Def.Modifier != null) v += src.Def.Modifier(this, src, c, s);
            foreach (var m in TempMods)
                if (m.Target == c && m.Stat == s) v += m.Amount;
            foreach (var l in Lasting) v += l.Mod(this, c, s);
            return Math.Max(0, v);
        }

        /// <summary>Adds a lasting effect that applies to whatever currently matches it, until the duration ends.</summary>
        public void AddLasting(Func<GameEngine, Card, Stat, int> mod, Duration d)
            => Lasting.Add(new LastingModifier { Mod = mod, Duration = d });

        public void AddTemp(Card target, Stat stat, int amount, Duration d)
            => TempMods.Add(new TempModifier { Target = target, Stat = stat, Amount = amount, Duration = d });

        private void ClearTemp(Duration d)
        {
            TempMods.RemoveAll(m => m.Duration == d);
            Lasting.RemoveAll(m => m.Duration == d);
        }

        public int StagingThreat => Staging.Sum(c => Get(c, Stat.Threat)) + StagingThreatBonus;

        /// <summary>False if an effect prevents the character from attacking or defending.</summary>
        public bool CanAttackOrDefend(Card ch) =>
            !AllInPlay().Any(src => src.Def.PreventsAttackOrDefend?.Invoke(this, src, ch) == true);
        public int QuestingResolve => Characters.Where(c => c.Committed).Sum(c => Get(c, Stat.Resolve));

        // =====================================================================
        // Decision helpers
        // =====================================================================

        public async Task<DecisionResult> Ask(string prompt, IEnumerable<Card> cands, int min, int max,
            IEnumerable<string> buttons = null, int confirmIndex = -1, bool popup = false)
        {
            var d = new Decision
            {
                Prompt = prompt, Min = min, Max = max, ConfirmIndex = confirmIndex, Popup = popup,
                Candidates = cands?.ToList() ?? new List<Card>(),
                Buttons = buttons?.ToList() ?? new List<string>()
            };
            return await UI.Ask(d);
        }

        public async Task<Card> ChooseOne(string prompt, IEnumerable<Card> cands, bool popup = false)
        {
            var list = cands.ToList();
            if (list.Count == 0) return null;
            var r = await Ask(prompt, list, 1, 1, null, -1, popup);
            return r.Cards.FirstOrDefault();
        }

        public async Task<Card> ChooseOneOrNone(string prompt, IEnumerable<Card> cands, string noneLabel, bool popup = false)
        {
            var list = cands.ToList();
            if (list.Count == 0) return null;
            var r = await Ask(prompt, list, 0, 1, new[] { noneLabel }, -1, popup);
            return r.Button >= 0 ? null : r.Cards.FirstOrDefault();
        }

        public async Task<List<Card>> ChooseMany(string prompt, IEnumerable<Card> cands, int min, int max, string doneLabel)
        {
            var list = cands.ToList();
            if (list.Count == 0) return new List<Card>();
            var r = await Ask(prompt, list, min, max, new[] { doneLabel }, 0);
            return r.Cards;
        }

        public async Task<int> Option(string prompt, params string[] options)
        {
            var r = await Ask(prompt, null, 0, 0, options);
            return r.Button;
        }

        public async Task<bool> YesNo(string prompt) => await Option(prompt, "Yes", "No") == 0;

        // =====================================================================
        // Basic game actions
        // =====================================================================

        public void Shuffle(List<Card> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        public bool CanDraw => !AllInPlay().Any(src => src.Def.PreventsDrawing?.Invoke(this, src) == true);

        public void Draw(int n)
        {
            if (n > 0 && Phase != Phase.Setup && !CanDraw) { Write("You cannot draw cards."); return; }
            if (n > 0 && DeckConditions.Count > 0)
            {
                var cond = DeckConditions[0];
                MoveTo(cond, Zone.EncounterDiscard);
                Write($"{cond.Name} is discarded instead of drawing.");
                return;
            }
            for (int i = 0; i < n; i++)
            {
                if (Deck.Count == 0) { Write("Your deck is empty."); return; }
                var c = Deck[0];
                Deck.RemoveAt(0);
                c.Zone = Zone.Hand;
                Hand.Add(c);
            }
        }

        public void RaiseDread(int n, string why = null)
        {
            if (n <= 0) return;
            Dread += n;
            Write($"Dread +{n}{(why != null ? " (" + why + ")" : "")} -> {Dread}");
            if (Dread >= DreadLimit) throw new GameOverException(false, $"Your dread reached {DreadLimit}. The lanterns go dark.");
        }

        public void ReduceDread(int n, string why = null)
        {
            if (n <= 0) return;
            Dread = Math.Max(0, Dread - n);
            Write($"Dread -{n}{(why != null ? " (" + why + ")" : "")} -> {Dread}");
        }

        public void Exhaust(Card c) => c.Exhausted = true;
        public void Ready(Card c) => c.Exhausted = false;

        public void Heal(Card c, int n)
        {
            int h = Math.Min(n, c.Damage);
            c.Damage -= h;
            if (h > 0) Write($"{c.Name} heals {h}.");
        }

        /// <summary>Deals damage. Returns true if the card was destroyed.</summary>
        public async Task<bool> DealDamage(Card c, int n, List<Card> attackers = null)
        {
            if (n <= 0 || c == null) return false;
            if (c.Zone is not (Zone.PlayArea or Zone.Engaged or Zone.Staging or Zone.Revealing)) return false;
            c.Damage += n;
            Write($"{c.Name} takes {n} damage ({c.Damage}/{Get(c, Stat.Health)}).");
            if (c.Damage >= Get(c, Stat.Health))
            {
                await Destroy(c, attackers);
                return true;
            }
            if (c.IsCharacter && c.Zone == Zone.PlayArea)
                await Fire(Trigger.AfterDamaged, new TriggerArgs { Subject = c, Amount = n });
            return false;
        }

        private void RemoveFromCurrentZone(Card c)
        {
            switch (c.Zone)
            {
                case Zone.Deck: Deck.Remove(c); break;
                case Zone.Hand: Hand.Remove(c); break;
                case Zone.Discard: Discard.Remove(c); break;
                case Zone.PlayArea: PlayArea.Remove(c); break;
                case Zone.Engaged: Engaged.Remove(c); break;
                case Zone.Staging: Staging.Remove(c); break;
                case Zone.EncounterDeck: EncounterDeck.Remove(c); break;
                case Zone.EncounterDiscard: EncounterDiscard.Remove(c); break;
                case Zone.Victory: Victory.Remove(c); break;
                case Zone.SetAside: SetAside.Remove(c); break;
                case Zone.OnDeck: DeckConditions.Remove(c); break;
                case Zone.ActiveLocation: if (ActiveLocation == c) ActiveLocation = null; break;
                case Zone.Shadow:
                    foreach (var en in Engaged.Concat(Staging)) en.ShadowCards.Remove(c);
                    c.FaceDown = false;
                    break;
                case Zone.Attached:
                    c.AttachedTo?.Attachments.Remove(c);
                    c.AttachedTo = null;
                    break;
            }
            TempMods.RemoveAll(m => m.Target == c);
        }

        public void MoveTo(Card c, Zone z, bool toTop = false)
        {
            RemoveFromCurrentZone(c);
            c.Zone = z;
            switch (z)
            {
                case Zone.Deck: if (toTop) Deck.Insert(0, c); else Deck.Add(c); break;
                case Zone.Hand: Hand.Add(c); break;
                case Zone.Discard: Discard.Add(c); break;
                case Zone.PlayArea: PlayArea.Add(c); break;
                case Zone.Engaged: Engaged.Add(c); break;
                case Zone.Staging: Staging.Add(c); break;
                case Zone.EncounterDeck: if (toTop) EncounterDeck.Insert(0, c); else EncounterDeck.Add(c); break;
                case Zone.EncounterDiscard: EncounterDiscard.Add(c); break;
                case Zone.Victory: Victory.Add(c); break;
                case Zone.SetAside: SetAside.Add(c); break;
                case Zone.OnDeck: DeckConditions.Add(c); break;
                case Zone.ActiveLocation: ActiveLocation = c; break;
            }
            // A guard that leaves play no longer guards its objective.
            if (z is not (Zone.Staging or Zone.Engaged or Zone.ActiveLocation))
                foreach (var o in Staging.Where(x => x.GuardedBy == c)) o.GuardedBy = null;
        }

        /// <summary>Puts a card into its owner's discard pile, cleaning up tokens and attachments.</summary>
        public void DiscardCard(Card c)
        {
            foreach (var a in c.Attachments.ToList()) DiscardCard(a);
            DiscardShadowCards(c);
            c.Damage = c.Resources = c.Progress = 0;
            c.Exhausted = c.Committed = false;
            c.FaceDown = false;
            if (c.Def.Token)
            {
                RemoveFromCurrentZone(c);
                c.Zone = Zone.None;
                if (c.FaceDownCard != null)
                {
                    var under = c.FaceDownCard;
                    c.FaceDownCard = null;
                    MoveTo(under, under.Def.IsPlayerCard ? Zone.Discard : Zone.EncounterDiscard);
                    Write($"The {c.Name} leaves play: {under.Name} is placed in your discard pile.");
                }
                return;
            }
            if (c.Type == CardType.Objective)
            {
                c.GuardedBy = null;
                MoveTo(c, Zone.Staging);
                Write($"{c.Name} returns to the staging area.");
                return;
            }
            if (c.Def.IsPlayerCard) MoveTo(c, Zone.Discard);
            else MoveTo(c, c.Def.Victory > 0 && (c.IsEnemy || c.IsLocation) && c.Zone != Zone.Shadow ? Zone.Victory : Zone.EncounterDiscard);
        }

        /// <summary>Discards all shadow cards dealt to an enemy.</summary>
        public void DiscardShadowCards(Card enemy)
        {
            foreach (var s in enemy.ShadowCards.ToList()) MoveTo(s, Zone.EncounterDiscard);
            enemy.ShadowCards.Clear();
        }

        /// <summary>Deals the top encounter card to an enemy as a face-down shadow card. Returns false if the deck is empty.</summary>
        public bool DealShadowCard(Card enemy)
        {
            if (EncounterDeck.Count == 0) return false;
            var s = EncounterDeck[0];
            RemoveFromCurrentZone(s);
            s.Zone = Zone.Shadow;
            s.FaceDown = true;
            enemy.ShadowCards.Add(s);
            return true;
        }

        /// <summary>Returns an encounter card to the encounter deck (top, or shuffled in), clearing tokens and attachments.</summary>
        public void ReturnToEncounterDeck(Card c, bool toTop)
        {
            foreach (var a in c.Attachments.ToList()) DiscardCard(a);
            DiscardShadowCards(c);
            c.Damage = c.Resources = c.Progress = 0;
            c.Exhausted = c.Committed = c.FaceDown = false;
            c.GuardedBy = null;
            MoveTo(c, Zone.EncounterDeck, toTop);
            if (!toTop) Shuffle(EncounterDeck);
        }

        /// <summary>True while an objective's guard is still in play.</summary>
        public bool IsGuarded(Card obj) => obj.GuardedBy != null && obj.GuardedBy.Zone is Zone.Staging or Zone.Engaged or Zone.ActiveLocation;

        /// <summary>
        /// Reveals the next encounter card to guard an objective. An enemy or location that ends up in
        /// play guards it; a treachery resolves normally and leaves the objective unguarded.
        /// </summary>
        public async Task GuardObjective(Card obj)
        {
            if (EncounterDeck.Count == 0) return;
            var top = EncounterDeck[0];
            Write($"{obj.Name} is guarded: an encounter card is revealed.");
            bool surge = _pendingSurge;
            await RevealEncounterCard();
            _pendingSurge = surge;
            if (top.Zone == Zone.Staging && (top.IsEnemy || top.IsLocation))
            {
                obj.GuardedBy = top;
                Write($"{top.Name} guards {obj.Name}.");
            }
            else Write($"{obj.Name} is left unguarded.");
        }

        /// <summary>Queues an extra enemy attack, resolved right after the current attack.</summary>
        public void QueueAttack(Card enemy) => _queuedAttacks.Add(enemy);

        private async Task Destroy(Card c, List<Card> attackers)
        {
            bool wasEnemy = c.IsEnemy;
            bool wasHero = c.IsHero;
            bool wasInPlay = c.Zone is Zone.PlayArea or Zone.Engaged or Zone.Staging or Zone.ActiveLocation;
            Write(wasEnemy ? $"{c.Name} is destroyed!" : $"{c.Name} has fallen.");
            DiscardCard(c);
            if (wasHero && !Heroes.Any())
                throw new GameOverException(false, "All of your heroes have fallen.");
            if (c.IsCharacter)
                await Fire(Trigger.AfterCharacterLeavesPlay, new TriggerArgs { Subject = c, Destroyed = true });
            if (wasEnemy)
            {
                await Fire(Trigger.AfterEnemyDestroyed, new TriggerArgs { Subject = c, Attackers = attackers ?? new List<Card>(), WasInPlay = wasInPlay });
                await CheckQuestComplete();
            }
        }

        /// <summary>Removes a player character from play (without destroying it) to the given zone.</summary>
        public async Task LeavePlay(Card c, Zone to = Zone.Discard)
        {
            if (c.Zone != Zone.PlayArea) return;
            c.PhaseCounters.Clear();
            c.RoundCounters.Clear();
            if (to == Zone.Discard) DiscardCard(c);
            else
            {
                foreach (var a in c.Attachments.ToList()) DiscardCard(a);
                c.Damage = c.Resources = c.Progress = 0;
                c.Exhausted = c.Committed = false;
                MoveTo(c, to);
                if (to == Zone.Deck) Shuffle(Deck);
            }
            if (c.IsHero && !Heroes.Any())
                throw new GameOverException(false, "All of your heroes have left play.");
            await Fire(Trigger.AfterCharacterLeavesPlay, new TriggerArgs { Subject = c });
        }

        /// <summary>True if no effect currently prevents this enemy from attacking.</summary>
        public bool CanAttack(Card enemy) =>
            !CannotAttackThisPhase.Contains(enemy) &&
            !AllInPlay().Any(src => src.Def.PreventsAttack?.Invoke(this, src, enemy) == true);

        public void Attach(Card attachment, Card target)
        {
            RemoveFromCurrentZone(attachment);
            attachment.Zone = Zone.Attached;
            attachment.AttachedTo = target;
            target.Attachments.Add(attachment);
        }

        // =====================================================================
        // Paying and playing player cards
        // =====================================================================

        public IEnumerable<Card> HeroesThatCanPay(Sphere s) =>
            Heroes.Where(h => s == Sphere.Neutral || h.Def.Sphere == s || h.Attachments.Any(a => a.Def.GrantsSphere?.Invoke(a, h, s) == true));

        /// <summary>Total supply available to pay for cards of a sphere.</summary>
        public int AvailableSupply(Sphere s) => HeroesThatCanPay(s).Sum(h => h.Resources);

        public bool CanAfford(Card c, int extraCost = 0)
        {
            var payers = HeroesThatCanPay(c.Def.Sphere).ToList();
            if (payers.Count == 0) return false;
            if (c.Def.PayFromDistinctHeroes > 0 && payers.Count(h => h.Resources > 0) < c.Def.PayFromDistinctHeroes) return false;
            return payers.Sum(h => h.Resources) >= c.Def.Cost + extraCost;
        }

        /// <summary>Pays for a card from matching heroes' pools. Returns false if cancelled.</summary>
        public async Task<bool> PayCost(Card c)
        {
            int n = c.Def.PayFromDistinctHeroes;
            if (n > 0)
            {
                var avail = HeroesThatCanPay(c.Def.Sphere).Where(h => h.Resources > 0).ToList();
                if (avail.Count < n) return false;
                var pick = avail.Count == n ? avail
                    : await ChooseMany($"Pay for {c.Name}: choose {n} different heroes to spend 1 supply each.", avail, n, n, "Pay");
                if (pick.Count != n) return false;
                foreach (var h in pick) h.Resources--;
                return true;
            }
            return await PayAmount(c.Def.Sphere, c.Def.Cost, c.Name);
        }

        /// <summary>Pays an amount of supply from heroes able to pay for the given sphere. Returns false if cancelled.</summary>
        public async Task<bool> PayAmount(Sphere sphere, int cost, string forName)
        {
            if (cost <= 0) return true;
            var payers = HeroesThatCanPay(sphere).ToList();
            if (payers.Sum(h => h.Resources) < cost) return false;
            var taken = new List<Card>();
            int remaining = cost;
            bool auto = payers.Count(h => h.Resources > 0) <= 1;
            while (remaining > 0)
            {
                var avail = payers.Where(h => h.Resources > 0).ToList();
                if (avail.Count == 0) break;
                Card pick;
                if (auto || avail.Count == 1)
                    pick = avail.OrderByDescending(h => h.Resources).First();
                else
                {
                    var r = await Ask($"Pay for {forName}: choose a hero to spend 1 supply ({remaining} left to pay).",
                        avail, 1, 1, new[] { "Auto-pay", "Cancel" });
                    if (r.Button == 1)
                    {
                        foreach (var h in taken) h.Resources++;
                        return false;
                    }
                    if (r.Button == 0) { auto = true; continue; }
                    pick = r.Cards[0];
                }
                pick.Resources--;
                taken.Add(pick);
                remaining--;
            }
            return remaining == 0;
        }

        public bool UniqueInPlay(CardDef d) =>
            d.Unique && AllInPlay().Any(c => c.Def.Name == d.Name);

        public bool IsValidAttachTarget(Card att, Card target)
        {
            if (target.Def.CannotHaveAttachments) return false;
            if (att.Def.Restricted && target.Attachments.Count(a => a.Def.Restricted) >= 2) return false;
            if (att.Def.Unique && target.Attachments.Any(a => a.Def.Name == att.Def.Name)) return false;
            return att.Def.AttachFilter?.Invoke(this, att, target) ?? (target.IsCharacter && target.Zone == Zone.PlayArea);
        }

        public IEnumerable<Card> AttachTargets(Card att) => AllInPlay().Where(t => t.Zone != Zone.Attached && t.Type != CardType.Quest && IsValidAttachTarget(att, t));

        public async Task PutIntoPlay(Card ally)
        {
            MoveTo(ally, Zone.PlayArea);
            Write($"{ally.Name} enters play.");
            await Fire(Trigger.AfterEnterPlay, new TriggerArgs { Subject = ally });
        }

        public List<GameAction> AvailableActions()
        {
            var list = new List<GameAction>();
            foreach (var c in Hand.ToList())
            {
                var d = c.Def;
                if (Phase == Phase.Planning && d.Type == CardType.Ally && CanAfford(c) && !UniqueInPlay(d) && AllyLimitAllows)
                {
                    list.Add(new GameAction
                    {
                        Card = c, Label = $"Play {d.Name} ({d.Cost})",
                        Execute = async () =>
                        {
                            if (!await PayCost(c)) return;
                            AlliesPlayedThisRound++;
                            await PutIntoPlay(c);
                        }
                    });
                }
                if (Phase == Phase.Planning && d.Type == CardType.Attachment && CanAfford(c) && !UniqueInPlay(d) && AttachTargets(c).Any())
                {
                    list.Add(new GameAction
                    {
                        Card = c, Label = $"Play {d.Name} ({d.Cost})",
                        Execute = async () =>
                        {
                            var t = await ChooseOneOrNone($"Attach {d.Name} to which card?", AttachTargets(c), "Cancel");
                            if (t == null) return;
                            if (!await PayCost(c)) return;
                            Attach(c, t);
                            Write($"{d.Name} attached to {t.Name}.");
                        }
                    });
                }
                if (d.Type == CardType.Event && d.PlayEvent != null && CanAfford(c) && (d.CanPlayEvent?.Invoke(this, c) ?? true))
                {
                    list.Add(new GameAction
                    {
                        Card = c, Label = $"Play {d.Name} ({d.Cost})",
                        Execute = async () =>
                        {
                            if (!await PayCost(c)) return;
                            Write($"You play {d.Name}.");
                            MoveTo(c, Zone.Discard);
                            await d.PlayEvent(this, c);
                        }
                    });
                }
            }
            foreach (var c in AllInPlay().ToList())
            {
                foreach (var a in c.Def.Actions)
                {
                    if (a.CanUse(this, c))
                        list.Add(new GameAction { Card = c, Label = a.Label, Execute = () => a.Use(this, c) });
                }
            }
            return list;
        }

        private bool AllyLimitAllows => Quest == null || Quest.Def.AllyLimitPerRound <= 0 || AlliesPlayedThisRound < Quest.Def.AllyLimitPerRound;

        public async Task ActionWindow(string where)
        {
            Step = where;
            while (true)
            {
                var acts = AvailableActions();
                if (acts.Count == 0 && AutoSkipEmptyWindows) return;
                var cards = acts.Select(a => a.Card).Distinct().ToList();
                var r = await Ask($"{where}: play a card or use an ability (highlighted), or continue.", cards, 0, 1, new[] { "Continue" });
                if (r.Button >= 0 || r.Cards.Count == 0) return;
                var mine = acts.Where(a => a.Card == r.Cards[0]).ToList();
                GameAction act = mine[0];
                if (mine.Count > 1)
                {
                    int i = await Option($"{r.Cards[0].Name}: choose an ability.", mine.Select(a => a.Label).Append("Cancel").ToArray());
                    if (i >= mine.Count) continue;
                    act = mine[i];
                }
                await act.Execute();
            }
        }

        // =====================================================================
        // Triggers
        // =====================================================================

        public async Task Fire(Trigger t, TriggerArgs args)
        {
            // Cards in play (player and encounter). An explored location or destroyed enemy
            // still resolves its own "after ..." abilities although it has just left play - but only
            // if it was in play when that happened (RRG 1.58: a Forced effect must be in play to trigger).
            var sources = AllInPlay().ToList();
            var leftPlay = t is Trigger.AfterLocationExplored or Trigger.AfterEnemyDestroyed && args.WasInPlay && args.Subject != null && !sources.Contains(args.Subject)
                ? args.Subject : null;
            if (leftPlay != null) sources.Add(leftPlay);
            foreach (var src in sources)
            {
                foreach (var ab in src.Def.Triggers.Where(x => x.On == t && !x.FromHand))
                {
                    if (args.Cancelled) return;
                    if (src != leftPlay && !InPlayStill(src)) break;
                    if (ab.Condition != null && !ab.Condition(this, src, args)) continue;
                    if (ab.Forced)
                    {
                        await ab.Resolve(this, src, args);
                    }
                    else if (await YesNo($"Response - {src.Name}: {ab.Prompt}"))
                    {
                        await ab.Resolve(this, src, args);
                    }
                }
            }
            // Abilities usable from hand (non-event cards)
            foreach (var c in Hand.ToList())
            {
                if (c.Def.Type == CardType.Event) continue;
                foreach (var ab in c.Def.Triggers.Where(x => x.On == t && x.FromHand))
                {
                    if (args.Cancelled) return;
                    if (c.Zone != Zone.Hand) break;
                    if (ab.Condition != null && !ab.Condition(this, c, args)) continue;
                    if (await YesNo($"Response - {c.Name} (in hand): {ab.Prompt}")) await ab.Resolve(this, c, args);
                }
            }
            // Response events in hand
            foreach (var c in Hand.ToList())
            {
                if (c.Def.Type != CardType.Event) continue;
                foreach (var ab in c.Def.Triggers.Where(x => x.On == t))
                {
                    if (args.Cancelled) return;
                    if (c.Zone != Zone.Hand || !CanAfford(c)) break;
                    if (ab.Condition != null && !ab.Condition(this, c, args)) continue;
                    if (await YesNo($"Play {c.Name} ({c.Def.Cost})? {ab.Prompt}"))
                    {
                        if (!await PayCost(c)) continue;
                        Write($"You play {c.Name}.");
                        MoveTo(c, Zone.Discard);
                        await ab.Resolve(this, c, args);
                        break;
                    }
                }
            }
        }

        private bool InPlayStill(Card c) => c.Zone is Zone.PlayArea or Zone.Attached or Zone.Engaged or Zone.Staging or Zone.ActiveLocation or Zone.ActiveQuest;

        // =====================================================================
        // Setup
        // =====================================================================

        public async Task Run(List<CardDef> heroes, List<CardDef> deck, Scenario scenario)
        {
            try
            {
                Scenario = scenario;
                await Setup(heroes, deck, scenario);
                while (true)
                {
                    Round++;
                    Write($"===== Round {Round} =====");
                    await ResourcePhase();
                    await PlanningPhase();
                    await QuestPhase();
                    await TravelPhase();
                    await EncounterPhase();
                    await CombatPhase();
                    await RefreshPhase();
                }
            }
            catch (GameOverException e)
            {
                IsOver = true;
                Won = e.Won;
                OverMessage = e.Message;
                Write(e.Message);
                if (e.Won)
                {
                    int vp = Victory.Sum(c => c.Def.Victory);
                    Write($"Final score: dread {Dread} + {Round * 10} (rounds) - {vp} (victory) = {Dread + Round * 10 - vp}");
                }
            }
        }

        private async Task Setup(List<CardDef> heroes, List<CardDef> deck, Scenario scenario)
        {
            Phase = Phase.Setup;
            Step = "Setup";
            foreach (var h in heroes)
            {
                var c = new Card(h);
                MoveTo(c, Zone.PlayArea);
                Dread += h.Cost;
            }
            Write($"Your heroes set out. Starting dread: {Dread}.");
            foreach (var d in deck) { var c = new Card(d); c.Zone = Zone.Deck; Deck.Add(c); }
            Shuffle(Deck);

            foreach (var d in scenario.EncounterCards)
                for (int i = 0; i < d.Copies; i++) { var c = new Card(d); c.Zone = Zone.EncounterDeck; EncounterDeck.Add(c); }
            foreach (var d in scenario.SetAside) { var c = new Card(d); c.Zone = Zone.SetAside; SetAside.Add(c); }
            foreach (var d in scenario.QuestStages) { var c = new Card(d); c.Zone = Zone.QuestDeck; QuestDeck.Add(c); }
            Shuffle(EncounterDeck);

            Draw(6);
            if (await Option("Opening hand drawn. Keep it or take a mulligan (shuffle back and draw 6 new cards)?", "Keep", "Mulligan") == 1)
            {
                foreach (var c in Hand.ToList()) MoveTo(c, Zone.Deck);
                Shuffle(Deck);
                Draw(6);
                Write("You take a mulligan.");
            }

            _questStageIndex = 0;
            Quest = TakeNextStage();
            Quest.Zone = Zone.ActiveQuest;
            Write($"Quest: {Quest.Name}");
            if (Quest.Def.Setup != null) await Quest.Def.Setup(this, Quest);
            Shuffle(EncounterDeck);
        }

        /// <summary>Searches the encounter deck (then discard) for a card by name and puts it into the staging area.</summary>
        public bool SearchEncounterToStaging(string name)
        {
            var c = EncounterDeck.FirstOrDefault(x => x.Name == name) ?? EncounterDiscard.FirstOrDefault(x => x.Name == name)
                ?? SetAside.FirstOrDefault(x => x.Name == name);
            if (c == null) return false;
            MoveTo(c, Zone.Staging);
            Write($"{c.Name} is added to the staging area.");
            return true;
        }

        // =====================================================================
        // Phases
        // =====================================================================

        private async Task EndPhase()
        {
            while (_endOfPhase.Count > 0)
            {
                var effects = _endOfPhase.ToList();
                _endOfPhase.Clear();
                foreach (var f in effects) await f();
            }
            ClearTemp(Duration.Phase);
            StagingThreatBonus = 0;
            foreach (var c in AllInPlay()) c.PhaseCounters.Clear();
            CannotAttackThisPhase.Clear();
            MultipleDefendersThisPhase = false;
            await CheckQuestComplete();
        }

        private async Task ResourcePhase()
        {
            Phase = Phase.Resource;
            Step = "Resource phase";
            AlliesPlayedThisRound = 0;
            foreach (var h in Heroes) h.Resources++;
            Write("Each hero gathers 1 supply.");
            await Fire(Trigger.ResourcePhase, new TriggerArgs());
            Draw(1);
            await ActionWindow("Resource phase");
            await EndPhase();
        }

        private async Task PlanningPhase()
        {
            Phase = Phase.Planning;
            await ActionWindow("Planning phase");
            await EndPhase();
        }

        private async Task QuestPhase()
        {
            Phase = Phase.Quest;
            Step = "Quest phase";
            await Fire(Trigger.QuestPhaseBegins, new TriggerArgs());
            Step = "Quest phase: commit";
            var ready = Characters.Where(c => !c.Exhausted).ToList();
            var chosen = await ChooseMany($"Quest phase: choose characters to commit to the quest (they exhaust). Staging threat: {StagingThreat}.",
                ready, 0, ready.Count, "Commit");
            foreach (var c in chosen) { Exhaust(c); c.Committed = true; }
            Write(chosen.Count == 0 ? "No characters commit to the quest." : $"Committed: {string.Join(", ", chosen.Select(c => c.Name))}");
            foreach (var c in chosen) await Fire(Trigger.AfterCommit, new TriggerArgs { Subject = c });
            await ActionWindow("Quest phase (after commit)");

            Step = "Quest phase: staging";
            if (SkipStagingStep) Write("The staging step is skipped.");
            else
            {
                int reveals = 1 + (Quest?.Def.ExtraStagingReveals ?? 0);
                for (int i = 0; i < reveals; i++) await RevealEncounterCard();
            }
            await ActionWindow("Quest phase (after staging)");

            Step = "Quest phase: resolution";
            int res = QuestingResolve, thr = StagingThreat;
            Write($"Quest resolution: resolve {res} vs threat {thr}.");
            if (res > thr) await PlaceQuestingProgress(res - thr);
            else if (thr > res)
            {
                RaiseDread(thr - res, "failed to overcome the threat");
                await Fire(Trigger.QuestFailed, new TriggerArgs { Amount = thr - res });
            }
            foreach (var c in Characters) c.Committed = false;
            await EndPhase();
        }

        public async Task RevealEncounterCard()
        {
            // Safety cap: a surge chain that can never end (e.g. only shuffle-back surge cards left) stops here.
            int chain = 0;
            do
            {
                if (++chain > 20) { _pendingSurge = false; Write("The surge chain fizzles out."); break; }
                _pendingSurge = false;
                if (EncounterDeck.Count == 0)
                {
                    if (EncounterDiscard.Count == 0) { Write("The encounter deck is empty."); return; }
                    foreach (var c in EncounterDiscard.ToList()) MoveTo(c, Zone.EncounterDeck);
                    Shuffle(EncounterDeck);
                    Write("Encounter discard pile shuffled into a new encounter deck.");
                }
                var card = EncounterDeck[0];
                Write($"Revealed: {card.Name} ({card.Type})");
                if (card.Type == CardType.Treachery)
                {
                    if (card.Def.Surge) _pendingSurge = true;
                    RemoveFromCurrentZone(card);
                    card.Zone = Zone.None;
                    var args = new TriggerArgs { Subject = card };
                    UIReveal = card;
                    await UI.Pause(700);
                    await Fire(Trigger.BeforeWhenRevealed, args);
                    if (card.Def.Doomed > 0) RaiseDread(card.Def.Doomed, "doomed");
                    if (args.Cancelled) Write($"{card.Name}'s effects are cancelled.");
                    else if (card.Def.WhenRevealed != null) await card.Def.WhenRevealed(this, card);
                    UIReveal = null;
                    if (card.Zone == Zone.None) MoveTo(card, Zone.EncounterDiscard);
                    await Fire(Trigger.AfterTreacheryRevealed, new TriggerArgs { Subject = card });
                }
                else
                {
                    if (card.IsEnemy)
                    {
                        // Effects like Tobrik Stonejaw's resolve as the enemy is revealed, before its keywords
                        // and "when revealed" effects. An enemy destroyed here never enters play, so none of
                        // its effects (surge, doomed, when revealed, forced) resolve.
                        RemoveFromCurrentZone(card);
                        card.Zone = Zone.Revealing;
                        UIReveal = card;
                        await Fire(Trigger.EnemyRevealing, new TriggerArgs { Subject = card });
                        UIReveal = null;
                        if (card.Zone != Zone.Revealing)
                        {
                            Write($"{card.Name} is destroyed before it enters play; none of its effects resolve.");
                            continue;
                        }
                    }
                    if (card.Def.Surge) _pendingSurge = true;
                    MoveTo(card, Zone.Staging);
                    await UI.Pause(500);
                    await Fire(Trigger.AfterEncounterRevealed, new TriggerArgs { Subject = card });
                    if (card.Type == CardType.Objective && card.Def.Guarded) await GuardObjective(card);
                    if (card.Def.Doomed > 0) RaiseDread(card.Def.Doomed, "doomed");
                    if (card.Def.WhenRevealed != null)
                    {
                        var args = new TriggerArgs { Subject = card };
                        await Fire(Trigger.BeforeWhenRevealed, args);
                        if (args.Cancelled) Write($"{card.Name}'s 'when revealed' effects are cancelled.");
                        else await card.Def.WhenRevealed(this, card);
                    }
                }
                if (_pendingSurge) Write("Surge! Another card is revealed.");
            } while (_pendingSurge);
        }

        /// <summary>A treachery being resolved, shown by the UI while it resolves.</summary>
        public Card UIReveal;

        public void GainSurge() => _pendingSurge = true;

        private async Task PlaceQuestingProgress(int n)
        {
            if (ActiveLocation != null)
            {
                var loc = ActiveLocation;
                int need = Get(loc, Stat.QuestPoints) - loc.Progress;
                int put = Math.Min(n, need);
                n -= put;
                await AddProgressToLocation(loc, put);
            }
            if (n > 0) await AddQuestProgress(n);
        }

        public async Task AddProgressToLocation(Card loc, int n)
        {
            if (n <= 0) return;
            loc.Progress += n;
            Write($"{n} progress placed on {loc.Name}.");
            if (loc.Progress >= Get(loc, Stat.QuestPoints))
            {
                Write($"{loc.Name} is explored.");
                DiscardCard(loc);
                await Fire(Trigger.AfterLocationExplored, new TriggerArgs { Subject = loc, WasInPlay = true });
                await CheckQuestComplete();
            }
        }

        public async Task AddQuestProgress(int n)
        {
            if (n <= 0 || Quest == null) return;
            Quest.Progress += n;
            Write($"{n} progress placed on the quest ({Quest.Progress}/{Get(Quest, Stat.QuestPoints)}).");
            await Fire(Trigger.AfterQuestProgress, new TriggerArgs { Subject = Quest, Amount = n });
            await CheckQuestComplete();
        }

        public async Task CheckQuestComplete()
        {
            if (Quest == null) return;
            if (Quest.Progress < Get(Quest, Stat.QuestPoints)) return;
            if (Quest.Def.CanCompleteStage != null && !Quest.Def.CanCompleteStage(this, Quest)) return;
            Write($"Quest stage complete: {Quest.Name}");
            _questStageIndex++;
            if (QuestDeck.Count == 0)
            {
                Quest = null;
                throw new GameOverException(true, "The final stage is complete. Victory! Your lanterns burn bright.");
            }
            Quest.Zone = Zone.None;
            Quest = TakeNextStage();
            Quest.Zone = Zone.ActiveQuest;
            Write($"Advancing to: {Quest.Name}");
            await UI.Pause(500);
            if (Quest.Def.WhenRevealed != null) await Quest.Def.WhenRevealed(this, Quest);
            await CheckQuestComplete();
        }

        /// <summary>
        /// Takes the next quest stage. Several stages with the same stage number are alternatives:
        /// one of them is chosen at random and the others are removed from the game.
        /// </summary>
        private Card TakeNextStage()
        {
            var label = QuestDeck[0].Def.StageLabel;
            var alts = QuestDeck.Where(q => q.Def.StageLabel == label).ToList();
            var pick = alts[Rng.Next(alts.Count)];
            foreach (var a in alts) QuestDeck.Remove(a);
            if (alts.Count > 1) Write($"The path forks... (stage {label} chosen at random from {alts.Count} options)");
            return pick;
        }

        public void RemoveQuestProgress(int n, string why = null)
        {
            if (Quest == null || n <= 0 || Quest.Progress == 0) return;
            int r = Math.Min(n, Quest.Progress);
            Quest.Progress -= r;
            Write($"{r} progress removed from the quest{(why != null ? " (" + why + ")" : "")}.");
        }

        private async Task TravelPhase()
        {
            Phase = Phase.Travel;
            Step = "Travel phase";
            if (ActiveLocation == null)
            {
                var locs = Staging.Where(c => c.IsLocation && (c.Def.CanTravel?.Invoke(this, c) ?? true)).ToList();
                var forced = locs.Where(c => c.Def.MustTravel).ToList();
                if (locs.Count > 0)
                {
                    var loc = forced.Count > 0
                        ? await ChooseOne("Travel phase: you must travel to this location.", forced)
                        : await ChooseOneOrNone("Travel phase: you may travel to a location in the staging area.", locs, "Don't travel");
                    if (loc != null)
                    {
                        bool ok = loc.Def.TravelCost == null || await loc.Def.TravelCost(this, loc);
                        if (ok)
                        {
                            MoveTo(loc, Zone.ActiveLocation);
                            Write($"You travel to {loc.Name}.");
                            await Fire(Trigger.AfterTravel, new TriggerArgs { Subject = loc });
                        }
                    }
                }
            }
            await ActionWindow("Travel phase");
            await EndPhase();
        }

        private async Task EncounterPhase()
        {
            Phase = Phase.Encounter;
            Step = "Encounter phase";
            var enemies = Staging.Where(c => c.IsEnemy && (c.Def.CanOptionallyEngage?.Invoke(this, c) ?? true)).ToList();
            if (enemies.Count > 0)
            {
                var e = await ChooseOneOrNone($"Encounter phase: you may optionally engage one enemy in the staging area (your dread: {Dread}).", enemies, "Engage none");
                if (e != null) await Engage(e);
            }
            // Engagement checks
            if (Quest?.Def.NoEngagementChecks == true) Write("No engagement checks are made this phase.");
            else while (true)
            {
                var cand = Staging.Where(c => c.IsEnemy && Get(c, Stat.Engagement) <= Dread)
                    .OrderByDescending(c => Get(c, Stat.Engagement)).FirstOrDefault();
                if (cand == null) break;
                Write($"Engagement check: {cand.Name} (engagement {Get(cand, Stat.Engagement)}) engages you.");
                await Engage(cand);
            }
            await ActionWindow("Encounter phase");
            await EndPhase();
        }

        public async Task Engage(Card enemy)
        {
            if (enemy.Zone != Zone.Staging) return;
            MoveTo(enemy, Zone.Engaged);
            Write($"{enemy.Name} is engaged with you.");
            await Fire(Trigger.AfterEngaged, new TriggerArgs { Subject = enemy });
        }

        private async Task CombatPhase()
        {
            Phase = Phase.Combat;
            Step = "Combat phase";
            if (Engaged.Count == 0)
            {
                await Fire(Trigger.EndOfCombatPhase, new TriggerArgs());
                await EndPhase();
                return;
            }

            // Deal shadow cards
            foreach (var e in Engaged.OrderByDescending(x => Get(x, Stat.Engagement)).ToList())
                if (!DealShadowCard(e)) break;
            await ActionWindow("Combat phase (shadow cards dealt)");

            // Enemy attacks
            var attackers = Engaged.OrderByDescending(x => Get(x, Stat.Engagement)).ToList();
            foreach (var e in attackers)
            {
                if (e.Zone != Zone.Engaged) continue;
                if (!CanAttack(e)) { Write($"{e.Name} cannot attack this phase."); continue; }
                await ResolveEnemyAttack(e, true);
                await ActionWindow($"Combat phase (after {e.Name}'s attack)");
            }

            // Player attacks
            var attacked = new HashSet<Card>();
            while (true)
            {
                var readyChars = Characters.Where(c => !c.Exhausted && CanAttackOrDefend(c)).ToList();
                var targets = Engaged.Where(x => !attacked.Contains(x)).ToList();
                if (readyChars.Any(c => c.Def.AttacksStaging))
                    targets.AddRange(Staging.Where(x => x.IsEnemy && !attacked.Contains(x)));
                if (targets.Count == 0 || readyChars.Count == 0) break;
                Step = "Combat phase: your attacks";
                var target = await ChooseOneOrNone("Combat: choose an enemy to attack, or finish attacking.", targets, "Done attacking");
                if (target == null) break;
                bool staging = target.Zone == Zone.Staging;
                var eligible = staging ? readyChars.Where(c => c.Def.AttacksStaging).ToList() : readyChars;
                var atk = await ChooseMany($"Choose {(staging ? "a character" : "characters")} to attack {target.Name} (guard {Get(target, Stat.Guard)}). They exhaust.",
                    eligible, 0, staging ? 1 : eligible.Count, "Attack");
                if (atk.Count == 0) continue;
                attacked.Add(target);
                await ResolvePlayerAttack(target, atk);
            }

            // Discard shadow cards
            foreach (var e in Engaged.ToList()) DiscardShadowCards(e);
            await ActionWindow("Combat phase (end)");
            await Fire(Trigger.EndOfCombatPhase, new TriggerArgs());
            await EndPhase();
        }

        /// <summary>Resolves an attack by the player's characters against an enemy (engaged or in staging).</summary>
        public async Task ResolvePlayerAttack(Card target, List<Card> atk)
        {
            foreach (var c in atk) Exhaust(c);
            AttackTarget = target;
            AttackingCharacters = atk;
            try
            {
                int total = atk.Sum(c => Get(c, Stat.Might));
                int dmg = Math.Max(0, total - Get(target, Stat.Guard));
                Write($"{string.Join(", ", atk.Select(c => c.Name))} attack {target.Name}: might {total} vs guard {Get(target, Stat.Guard)}.");
                if (dmg > 0) await DealDamage(target, dmg, atk);
                else Write("The attack deals no damage.");
            }
            finally
            {
                AttackTarget = null;
                AttackingCharacters = new List<Card>();
                ClearTemp(Duration.Attack);
            }
        }

        public async Task ResolveEnemyAttack(Card enemy, bool withShadow)
        {
            Step = $"{enemy.Name} attacks";
            var ctx = new AttackContext { Enemy = enemy };
            await Fire(Trigger.EnemyAttacks, new TriggerArgs { Subject = enemy, Enemy = enemy, Attack = ctx });
            var ready = Characters.Where(c => !c.Exhausted && CanAttackOrDefend(c)).ToList();
            var defs = new List<Card>();
            if (MultipleDefendersThisPhase)
                defs = await ChooseMany($"{enemy.Name} attacks (might {Get(enemy, Stat.Might)}). Choose any number of defenders, or none for an undefended attack.",
                    ready, 0, ready.Count, "Defend");
            else
            {
                var def = await ChooseOneOrNone($"{enemy.Name} attacks (might {Get(enemy, Stat.Might)}). Choose a defender, or take it undefended.",
                    ready, "No defender");
                if (def != null) defs.Add(def);
            }
            if (defs.Count > 0)
            {
                ctx.Defender = defs[0];
                ctx.Defenders.AddRange(defs);
                foreach (var def in defs) Exhaust(def);
                Write($"{string.Join(", ", defs.Select(d => d.Name))} defend{(defs.Count == 1 ? "s" : "")} against {enemy.Name}.");
                foreach (var def in defs)
                    await Fire(Trigger.DefenderDeclared, new TriggerArgs { Subject = def, Enemy = enemy, Attack = ctx });
            }
            else Write($"{enemy.Name}'s attack is undefended!");

            // Shadow effects may deal further shadow cards, which are resolved too.
            while (withShadow && enemy.Zone == Zone.Engaged)
            {
                var s = enemy.ShadowCards.FirstOrDefault(x => !ctx.Shadows.Contains(x));
                if (s == null) break;
                ctx.Shadows.Add(s);
                s.FaceDown = false;
                Write($"Shadow card: {s.Name}{(s.Def.Shadow != null ? " - " + s.Def.ShadowText : " (no shadow effect)")}");
                await UI.Pause(700);
                if (s.Def.Shadow != null)
                {
                    var sa = new TriggerArgs { Subject = s, Enemy = enemy, Attack = ctx };
                    await Fire(Trigger.BeforeShadow, sa);
                    if (sa.Cancelled) Write($"The shadow effect of {s.Name} is cancelled.");
                    else
                    {
                        await s.Def.Shadow(this, s, ctx);
                        await Fire(Trigger.AfterShadowResolved, new TriggerArgs { Subject = s, Enemy = enemy, Attack = ctx });
                    }
                }
            }

            if (!ctx.Cancelled && enemy.Zone == Zone.Engaged)
            {
                int might = Get(enemy, Stat.Might);
                var standing = ctx.Defenders.Where(d => d.Zone == Zone.PlayArea).ToList();
                if (ctx.Defender != null && ctx.Defender.Zone == Zone.PlayArea && !standing.Contains(ctx.Defender)) standing.Insert(0, ctx.Defender);
                if (standing.Count > 0)
                {
                    int guard = standing.Sum(d => Get(d, Stat.Guard));
                    int dmg = Math.Max(0, might - guard);
                    Write($"Attack: might {might} vs guard {guard}.");
                    var defender = standing.Count == 1 || dmg == 0 ? standing[0]
                        : await ChooseOne($"Choose a defender to take {dmg} damage.", standing);
                    if (dmg > 0) await DealCombatDamage(enemy, defender, dmg);
                    else Write("The attack is blocked completely.");
                    foreach (var d in standing)
                        await Fire(Trigger.AfterDefend, new TriggerArgs { Subject = d, Enemy = enemy, Attack = ctx });
                }
                else if (ctx.Defender == null && ctx.Defenders.Count == 0)
                {
                    var hero = await ChooseOne($"Undefended attack: choose a hero to take {might} damage.", Heroes);
                    if (hero != null) await DealCombatDamage(enemy, hero, might);
                }
                if (enemy.Zone == Zone.Engaged)
                    await Fire(Trigger.AfterEnemyAttack, new TriggerArgs { Subject = enemy, Enemy = enemy, Attack = ctx });
            }
            ClearTemp(Duration.Attack);

            while (_queuedAttacks.Count > 0)
            {
                var next = _queuedAttacks[0];
                _queuedAttacks.RemoveAt(0);
                if (next.Zone != Zone.Engaged) continue;
                DealShadowCard(next);
                await ResolveEnemyAttack(next, true);
            }
        }

        private async Task DealCombatDamage(Card enemy, Card target, int dmg)
        {
            int remaining = Math.Max(0, Get(target, Stat.Health) - target.Damage);
            await DealDamage(target, dmg);
            if (enemy.Def.ExcessDamageToDread && dmg > remaining)
                RaiseDread(dmg - remaining, $"{enemy.Name}'s excess damage");
        }

        private async Task RefreshPhase()
        {
            Phase = Phase.Refresh;
            Step = "Refresh phase";
            foreach (var c in PlayArea.ToList())
            {
                bool ready = true;
                foreach (var a in c.Attachments.ToList())
                    if (c.Exhausted && a.Def.RefreshReadyCheck != null && !await a.Def.RefreshReadyCheck(this, a)) ready = false;
                if (ready) c.Exhausted = false;
                foreach (var a in c.Attachments) a.Exhausted = false;
            }
            foreach (var c in Engaged) foreach (var a in c.Attachments) a.Exhausted = false;
            Write("All your cards ready.");
            RaiseDread(1, "refresh");
            await ActionWindow("Refresh phase");
            await Fire(Trigger.EndOfRound, new TriggerArgs());
            await EndPhase();
            ClearTemp(Duration.Round);
            foreach (var c in AllInPlay()) c.RoundCounters.Clear();
        }
    }
}
