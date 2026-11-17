using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lanternbearers.Engine;

namespace Lanternbearers.Cards
{
    /// <summary>Player card pool, loaded from the JSON card data.</summary>
    public static class PlayerCards
    {
        public static IEnumerable<CardDef> All => CardDatabase.All.Where(c => c.IsPlayerCard).OrderBy(c => c.Number);
        public static IEnumerable<CardDef> Heroes => All.Where(c => c.Type == CardType.Hero);

        /// <summary>Builds a deck from the spheres of the chosen heroes (plus neutral cards).</summary>
        public static List<CardDef> BuildDeck(IEnumerable<CardDef> heroes)
        {
            var spheres = heroes.Select(h => h.Sphere).Distinct().ToList();
            var pool = All.Where(c => c.Type != CardType.Hero && (spheres.Contains(c.Sphere) || c.Sphere == Sphere.Neutral)).ToList();
            int copies = spheres.Count switch { 1 => 3, 2 => 2, _ => 1 };
            var deck = new List<CardDef>();
            foreach (var c in pool)
            {
                int n = c.Unique ? System.Math.Min(copies, 2) : copies;
                for (int i = 0; i < n; i++) deck.Add(c);
            }
            // With three spheres, pad with extra copies of the cheapest cards to reach a reasonable size.
            foreach (var c in pool.Where(x => !x.Unique).OrderBy(x => x.Cost))
            {
                if (deck.Count >= 40) break;
                deck.Add(c);
            }
            return deck;
        }
    }

    public static partial class CardScripts
    {
        // ---- helpers for player card scripts ----
        private static IEnumerable<Card> EnemiesInPlay(GameEngine e) => e.Staging.Where(x => x.IsEnemy).Concat(e.Engaged).ToList();

        private static IEnumerable<Card> Locations(GameEngine e) =>
            e.Staging.Where(x => x.IsLocation).Concat(e.ActiveLocation != null ? new[] { e.ActiveLocation } : new Card[0]).ToList();

        private static IEnumerable<Card> AttackTargetsFor(GameEngine e, Card ch) =>
            e.Engaged.Concat(ch.Def.AttacksStaging ? e.Staging.Where(x => x.IsEnemy) : Enumerable.Empty<Card>()).ToList();

        private static async Task PeekEncounter(GameEngine e, string who, bool mayBottom)
        {
            var top = e.EncounterDeck[0];
            if (!mayBottom)
            {
                await e.Ask($"{who}: the top encounter card is {top.Name}.", new[] { top }, 0, 0, new[] { "OK" }, -1, true);
                return;
            }
            var r = await e.Ask($"{who}: the top encounter card is {top.Name}.", new[] { top }, 0, 0, new[] { "Put on bottom", "Leave on top" }, -1, true);
            if (r.Button == 0) { e.EncounterDeck.RemoveAt(0); e.EncounterDeck.Add(top); e.Write($"{top.Name} is put on the bottom of the encounter deck."); }
        }

        private static void HeroOnly(CardDef d) => d.AttachFilter = (e, a, t) => t.IsHero && t.Zone == Zone.PlayArea;

        private static void RegisterPlayerCards()
        {
            // ================================================================ CROWN
            On("Corwin-Ashvale-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterCommit, Prompt = "Spend 1 supply to ready Corwin Ashvale?",
                Condition = (e, c, a) => a.Subject == c && c.Resources > 0 && c.Exhausted,
                Resolve = (e, c, a) => { c.Resources--; e.Ready(c); e.Write("Corwin Ashvale readies."); return Task.CompletedTask; }
            }));

            On("Edric-Marrow-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterCommit, Prompt = "Add 1 supply to a committed hero's pool?",
                Condition = (e, c, a) => a.Subject == c,
                Resolve = async (e, c, a) =>
                {
                    var h = await e.ChooseOne("Edric Marrow: choose a committed hero to gain 1 supply.", e.Heroes.Where(x => x.Committed));
                    if (h != null) { h.Resources++; e.Write($"{h.Name} gains 1 supply."); }
                }
            }));

            On("Borra-Flintmantle-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterDamaged, Prompt = "Gain 1 supply for each damage just suffered?",
                Condition = (e, c, a) => a.Subject == c,
                Resolve = (e, c, a) => { c.Resources += a.Amount; e.Write($"Borra Flintmantle gains {a.Amount} supply."); return Task.CompletedTask; }
            }));

            On("Oren-Valecrest-LB", d => d.Actions.Add(new ActionAbility
            {
                Label = "Exhaust: your characters get +1 Resolve",
                CanUse = (e, c) => InPlay(c) && !c.Exhausted,
                Use = (e, c) =>
                {
                    e.Exhaust(c);
                    foreach (var ch in e.Characters) e.AddTemp(ch, Stat.Resolve, 1, Duration.Phase);
                    e.Write("Oren Valecrest: each character you control gets +1 Resolve this phase.");
                    return Task.CompletedTask;
                }
            }));

            On("Heir-of-the-Waystones-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterEnterPlay, Prompt = "Engage an enemy in the staging area?",
                Condition = (e, c, a) => a.Subject == c && e.Staging.Any(x => x.IsEnemy),
                Resolve = async (e, c, a) =>
                {
                    var t = await e.ChooseOne("Heir of the Waystones: choose an enemy to engage.", e.Staging.Where(x => x.IsEnemy));
                    if (t != null) await e.Engage(t);
                }
            }));

            On("Marchland-Outrider-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterEnterPlay, Prompt = "Place 1 progress on a location?",
                Condition = (e, c, a) => a.Subject == c && Locations(e).Any(),
                Resolve = async (e, c, a) =>
                {
                    var t = await e.ChooseOne("Marchland Outrider: choose a location.", Locations(e));
                    if (t != null) await e.AddProgressToLocation(t, 1);
                }
            }));

            On("Stonehold-Raidbreaker-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterEnterPlay, Prompt = "Deal 1 damage to each Raider enemy in play?",
                Condition = (e, c, a) => a.Subject == c && EnemiesInPlay(e).Any(x => x.HasTrait("Raider")),
                Resolve = async (e, c, a) => { foreach (var t in EnemiesInPlay(e).Where(x => x.HasTrait("Raider"))) await e.DealDamage(t, 1); }
            }));

            On("Hroth-Emberfist-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterCharacterLeavesPlay, FromHand = true, Prompt = "Put Hroth Emberfist into play from your hand?",
                Condition = (e, c, a) => a.Subject.IsHero && a.Subject.HasTrait("Hillfolk") && !e.UniqueInPlay(c.Def),
                Resolve = async (e, c, a) => await e.PutIntoPlay(c)
            }));

            On("Keep-Watch-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Allies.Any(a => a.Exhausted);
                d.PlayEvent = async (e, c) => { var t = await e.ChooseOne("Keep Watch: ready which ally?", e.Allies.Where(a => a.Exhausted)); e.Ready(t); e.Write($"{t.Name} readies."); };
            });

            On("Shared-Burden-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Heroes.Any(h => !h.Exhausted) && e.Heroes.Any(h => h.Exhausted);
                d.PlayEvent = async (e, c) =>
                {
                    var x = await e.ChooseOne("Shared Burden: exhaust which hero?", e.Heroes.Where(h => !h.Exhausted));
                    var r = await e.ChooseOne("Shared Burden: ready which hero?", e.Heroes.Where(h => h.Exhausted && h != x));
                    e.Exhaust(x); e.Ready(r);
                    e.Write($"{x.Name} exhausts to ready {r.Name}.");
                };
            });

            On("For-Greywall-LB", d => d.PlayEvent = (e, c) =>
            {
                foreach (var ch in e.Characters)
                {
                    e.AddTemp(ch, Stat.Might, 1, Duration.Phase);
                    if (ch.HasTrait("Greywall")) e.AddTemp(ch, Stat.Guard, 1, Duration.Phase);
                }
                e.Write("For Greywall! Each character gets +1 Might this phase; Greywall characters also get +1 Guard.");
                return Task.CompletedTask;
            });

            On("Sudden-Sally-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Hand.Any(x => x != c && x.IsAlly && !e.UniqueInPlay(x.Def));
                d.PlayEvent = async (e, c) =>
                {
                    var ally = await e.ChooseOne("Sudden Sally: choose an ally to put into play.", e.Hand.Where(x => x.IsAlly && !e.UniqueInPlay(x.Def)));
                    if (ally == null) return;
                    await e.PutIntoPlay(ally);
                    e.AtEndOfPhase(async () =>
                    {
                        if (ally.Zone != Zone.PlayArea) return;
                        await e.LeavePlay(ally, Zone.Hand);
                        e.Write($"Sudden Sally: {ally.Name} returns to your hand.");
                    });
                };
            });

            On("Remembered-Deeds-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterCharacterLeavesPlay, Prompt = "Draw 2 cards.",
                Condition = (e, c, a) => a.Subject.IsAlly && e.Deck.Count > 0,
                Resolve = (e, c, a) => { e.Draw(2); return Task.CompletedTask; }
            }));

            On("Iron-Will-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Characters.Any(x => x.Exhausted);
                d.PlayEvent = (e, c) => { foreach (var ch in e.Characters) e.Ready(ch); e.Write("Each character you control readies."); return Task.CompletedTask; };
            });

            On("Seneschal-of-Greywall-LB", d =>
            {
                HeroOnly(d);
                d.GrantsTrait = "Greywall";
                d.Actions.Add(new ActionAbility
                {
                    Label = "Exhaust: add 2 supply",
                    CanUse = (e, c) => !c.Exhausted && c.AttachedTo != null,
                    Use = (e, c) => { e.Exhaust(c); c.AttachedTo.Resources += 2; e.Write($"Seneschal of Greywall: {c.AttachedTo.Name} gains 2 supply."); return Task.CompletedTask; }
                });
            });

            On("Moonwell-Pendant-LB", d =>
            {
                HeroOnly(d);
                d.Modifier = (e, src, t, s) => AttachedBonus(src, t, s, Stat.Resolve, 2);
                d.GrantsSphere = (a, h, s) => s == Sphere.Hearth && h.Def.Id == "Corwin-Ashvale-LB";
            });

            // ================================================================ BLADE
            On("Durgan-Hammerfell-LB", d =>
                d.Modifier = (e, src, t, s) => InPlay(src) && t == src && s == Stat.Might ? src.Damage : 0);

            On("Faelan-Swiftbough-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterEnemyDestroyed, Prompt = "Place 2 progress on the current quest?",
                Condition = (e, c, a) => a.Attackers.Contains(c) && e.Quest != null,
                Resolve = async (e, c, a) => await e.AddQuestProgress(2)
            }));

            On("Tobrik-Stonejaw-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.EnemyRevealing, Forced = true,
                Condition = (e, c, a) => c.Committed && a.Subject.IsEnemy && a.Subject.Zone == Zone.Revealing,
                Resolve = async (e, c, a) => { e.Write($"Tobrik Stonejaw strikes {a.Subject.Name} as it is revealed."); await e.DealDamage(a.Subject, 1); }
            }));

            On("Greywall-Pikeman-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.DefenderDeclared, Prompt = "Deal 1 damage to the attacking enemy?",
                Condition = (e, c, a) => a.Subject == c && a.Enemy != null && a.Enemy.Zone == Zone.Engaged,
                Resolve = async (e, c, a) => await e.DealDamage(a.Enemy, 1)
            }));

            On("Ulfgar-the-Bearskin-LB", d => d.Actions.Add(new ActionAbility
            {
                Label = "+5 Might (shuffle into deck at end of phase)",
                CanUse = (e, c) => InPlay(c) && c.UsedThisRound("rage") == 0,
                Use = (e, c) =>
                {
                    c.MarkRound("rage");
                    e.AddTemp(c, Stat.Might, 5, Duration.Phase);
                    e.Write("Ulfgar the Bearskin gets +5 Might this phase.");
                    e.AtEndOfPhase(async () =>
                    {
                        if (c.Zone != Zone.PlayArea) return;
                        await e.LeavePlay(c, Zone.Deck);
                        e.Write("Ulfgar the Bearskin is shuffled back into your deck.");
                    });
                    return Task.CompletedTask;
                }
            }));

            On("Oathsworn-Blade-LB", d =>
            {
                HeroOnly(d);
                d.Modifier = (e, src, t, s) =>
                    src.Zone == Zone.Attached && src.AttachedTo == t && s == Stat.Might &&
                    e.AttackTarget != null && e.AttackTarget.HasTrait("Raider") && e.AttackingCharacters.Contains(t) ? 1 : 0;
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterEnemyDestroyed, Prompt = "Place 1 progress on the current quest?",
                    Condition = (e, c, a) => c.AttachedTo != null && a.Attackers.Contains(c.AttachedTo) && e.Quest != null,
                    Resolve = async (e, c, a) => await e.AddQuestProgress(1)
                });
            });

            On("Greywall-Plate-LB", d => { HeroOnly(d); d.Modifier = (e, src, t, s) => AttachedBonus(src, t, s, Stat.Health, 4); });

            On("Hillfolk-War-Axe-LB", d =>
            {
                HeroOnly(d);
                d.Modifier = (e, src, t, s) => AttachedBonus(src, t, s, Stat.Might, t.HasTrait("Hillfolk") ? 2 : 1);
            });

            On("Warhorn-of-the-Watch-LB", d =>
            {
                HeroOnly(d);
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterCharacterLeavesPlay, Prompt = "Add 1 supply to the attached hero's pool?",
                    Condition = (e, c, a) => a.Destroyed && c.AttachedTo != null,
                    Resolve = (e, c, a) => { c.AttachedTo.Resources++; e.Write($"Warhorn of the Watch: {c.AttachedTo.Name} gains 1 supply."); return Task.CompletedTask; }
                });
            });

            On("Practiced-Form-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Characters.Any();
                d.PlayEvent = async (e, c) =>
                {
                    var t = await e.ChooseOne("Practiced Form: choose a character.", e.Characters);
                    e.AddTemp(t, Stat.Might, 1, Duration.Phase); e.AddTemp(t, Stat.Guard, 1, Duration.Phase);
                    e.Write($"{t.Name} gets +1 Might and +1 Guard this phase.");
                };
            });

            On("Volley-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Engaged.Any() && e.Characters.Any(x => !x.Exhausted && x.Def.Ranged);
                d.PlayEvent = async (e, c) =>
                {
                    var r = await e.ChooseOne("Volley: exhaust which Ranged character?", e.Characters.Where(x => !x.Exhausted && x.Def.Ranged));
                    e.Exhaust(r);
                    foreach (var t in e.Engaged.ToList()) await e.DealDamage(t, 1);
                };
            });

            On("False-Opening-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Phase == Phase.Combat && e.Engaged.Any(x => !e.CannotAttackThisPhase.Contains(x));
                d.PlayEvent = async (e, c) =>
                {
                    var t = await e.ChooseOne("False Opening: choose an engaged enemy.", e.Engaged.Where(x => !e.CannotAttackThisPhase.Contains(x)));
                    e.CannotAttackThisPhase.Add(t);
                    e.Write($"{t.Name} cannot attack you this phase.");
                };
            });

            On("Strike-First-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Characters.Any(x => !x.Exhausted && e.CanAttackOrDefend(x) && AttackTargetsFor(e, x).Any());
                d.PlayEvent = async (e, c) =>
                {
                    var ch = await e.ChooseOne("Strike First: choose a character to attack.", e.Characters.Where(x => !x.Exhausted && e.CanAttackOrDefend(x) && AttackTargetsFor(e, x).Any()));
                    var t = await e.ChooseOne($"Strike First: choose an enemy for {ch.Name} to attack.", AttackTargetsFor(e, ch));
                    await e.ResolvePlayerAttack(t, new List<Card> { ch });
                };
            });

            On("Hedge-of-Pikes-LB", d =>
            {
                d.PayFromDistinctHeroes = 3;
                d.CanPlayEvent = (e, c) => e.Engaged.Any();
                d.PlayEvent = (e, c) => { foreach (var t in e.Engaged) e.CannotAttackThisPhase.Add(t); e.Write("Enemies engaged with you cannot attack this phase."); return Task.CompletedTask; };
            });

            On("Punishing-Blow-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.DefenderDeclared, Prompt = "Deal 2 damage to the attacking enemy.",
                Condition = (e, c, a) => a.Enemy != null && a.Enemy.Zone == Zone.Engaged,
                Resolve = async (e, c, a) => await e.DealDamage(a.Enemy, 2)
            }));

            On("Close-Ranks-LB", d =>
            {
                d.CanPlayEvent = (e, c) => !e.MultipleDefendersThisPhase;
                d.PlayEvent = (e, c) => { e.MultipleDefendersThisPhase = true; e.Write("This phase, any number of characters may defend each attack."); return Task.CompletedTask; };
            });

            // ================================================================ HEARTH
            On("Maren-Haldis-LB", d => d.Actions.Add(new ActionAbility
            {
                Label = "Discard a card: +1 Resolve",
                CanUse = (e, c) => InPlay(c) && e.Hand.Count > 0 && c.UsedThisRound("resolve") == 0,
                Use = async (e, c) =>
                {
                    var x = await e.ChooseOneOrNone("Maren Haldis: discard which card?", e.Hand.ToList(), "Cancel");
                    if (x == null) return;
                    c.MarkRound("resolve");
                    e.MoveTo(x, Zone.Discard);
                    e.AddTemp(c, Stat.Resolve, 1, Duration.Phase);
                    e.Write($"Maren Haldis discards {x.Name} for +1 Resolve.");
                }
            }));

            On("Ysolde-Carrow-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.BeforeWhenRevealed, Prompt = "Exhaust Ysolde Carrow to cancel this treachery and replace it?",
                Condition = (e, c, a) => !c.Exhausted && a.Subject != null && a.Subject.Type == CardType.Treachery && a.Subject.Def.WhenRevealed != null,
                Resolve = (e, c, a) =>
                {
                    e.Exhaust(c);
                    a.Cancelled = true;
                    e.GainSurge();
                    e.Write($"Ysolde Carrow cancels {a.Subject.Name}; it is replaced by the next encounter card.");
                    return Task.CompletedTask;
                }
            }));

            On("Garrick-Thane-LB", d =>
            {
                d.AttacksStaging = true;
                d.Modifier = (e, src, t, s) =>
                    InPlay(src) && t == src && s == Stat.Might && e.AttackTarget != null && e.AttackTarget.Zone == Zone.Staging &&
                    e.AttackingCharacters.Count == 1 && e.AttackingCharacters[0] == src ? 1 : 0;
            });

            On("Woodland-Guide-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterCommit, Prompt = "Place 1 progress on the active location?",
                Condition = (e, c, a) => a.Subject == c && e.ActiveLocation != null,
                Resolve = async (e, c, a) => await e.AddProgressToLocation(e.ActiveLocation, 1)
            }));

            On("Waystone-Tracker-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterCommit, Prompt = "Place 1 progress on each location in the staging area?",
                Condition = (e, c, a) => a.Subject == c && e.Staging.Any(x => x.IsLocation),
                Resolve = async (e, c, a) => { foreach (var l in e.Staging.Where(x => x.IsLocation).ToList()) await e.AddProgressToLocation(l, 1); }
            }));

            On("Blessing-of-the-Hearth-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Dread > 0;
                d.PlayEvent = (e, c) => { e.ReduceDread(6, c.Name); return Task.CompletedTask; };
            });

            On("Unbowed-Spirit-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterTravel, Prompt = "Exhaust a Hearth character to place 2 progress on that location.",
                Condition = (e, c, a) => a.Subject != null && a.Subject.Zone == Zone.ActiveLocation && e.Characters.Any(x => !x.Exhausted && x.Def.Sphere == Sphere.Hearth),
                Resolve = async (e, c, a) =>
                {
                    var ch = await e.ChooseOne("Unbowed Spirit: exhaust which Hearth character?", e.Characters.Where(x => !x.Exhausted && x.Def.Sphere == Sphere.Hearth));
                    e.Exhaust(ch);
                    await e.AddProgressToLocation(a.Subject, 2);
                }
            }));

            On("Quick-Parry-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.BeforeShadow, Prompt = "Cancel this shadow effect.",
                Resolve = (e, c, a) => { a.Cancelled = true; return Task.CompletedTask; }
            }));

            On("Rekindled-Hope-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Discard.Any(x => x != c);
                d.PlayEvent = (e, c) =>
                {
                    e.MoveTo(c, Zone.None); // removed from the game
                    foreach (var x in e.Discard.ToList()) e.MoveTo(x, Zone.Deck);
                    e.Shuffle(e.Deck);
                    e.Write("Your discard pile is shuffled back into your deck. Rekindled Hope is removed from the game.");
                    return Task.CompletedTask;
                };
            });

            On("Steel-the-Heart-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.BeforeWhenRevealed, Prompt = "Cancel this card's 'when revealed' effects.",
                Condition = (e, c, a) => a.Subject != null && a.Subject.Def.WhenRevealed != null,
                Resolve = (e, c, a) => { a.Cancelled = true; return Task.CompletedTask; }
            }));

            On("Answer-the-Call-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Discard.Any(x => x.IsAlly && x.Def.Cost <= e.AvailableSupply(Sphere.Hearth) && !e.UniqueInPlay(x.Def));
                d.PlayEvent = async (e, c) =>
                {
                    var ally = await e.ChooseOneOrNone("Answer the Call: choose an ally in your discard pile (X = its cost).",
                        e.Discard.Where(x => x.IsAlly && x.Def.Cost <= e.AvailableSupply(Sphere.Hearth) && !e.UniqueInPlay(x.Def)), "Cancel", popup: true);
                    if (ally == null || !await e.PayAmount(Sphere.Hearth, ally.Def.Cost, $"{c.Name} (X = {ally.Def.Cost})"))
                    {
                        e.MoveTo(c, Zone.Hand);
                        e.Write("Answer the Call is cancelled.");
                        return;
                    }
                    await e.PutIntoPlay(ally);
                };
            });

            On("Warding-Light-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Engaged.Any();
                d.PlayEvent = async (e, c) =>
                {
                    var t = await e.ChooseOne("Warding Light: return which enemy to the staging area?", e.Engaged);
                    e.DiscardShadowCards(t);
                    e.MoveTo(t, Zone.Staging);
                    e.Write($"{t.Name} is returned to the staging area.");
                };
            });

            On("Honored-Memory-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Discard.Any(x => x != c && x.Def.Sphere == Sphere.Hearth);
                d.PlayEvent = async (e, c) =>
                {
                    var x = await e.ChooseOne("Honored Memory: return which Hearth card to your hand?", e.Discard.Where(x => x != c && x.Def.Sphere == Sphere.Hearth), popup: true);
                    e.MoveTo(x, Zone.Hand);
                    e.Write($"{x.Name} returns to your hand.");
                };
            });

            On("Twist-of-Fate-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Discard.Any(x => x.IsHero);
                d.PlayEvent = async (e, c) =>
                {
                    var h = await e.ChooseOne("Twist of Fate: choose a hero in your discard pile.", e.Discard.Where(x => x.IsHero), popup: true);
                    await e.PutIntoPlay(h);
                };
            });

            On("Grace-of-the-Lantern-LB", d => { HeroOnly(d); d.Modifier = (e, src, t, s) => AttachedBonus(src, t, s, Stat.Resolve, 1); });

            On("Quieted-Ground-LB", d =>
            {
                d.AttachFilter = (e, a, t) => t.IsLocation && (t.Zone == Zone.Staging || t.Zone == Zone.ActiveLocation);
                d.Modifier = (e, src, t, s) => AttachedBonus(src, t, s, Stat.Threat, -1);
            });

            On("Surge-of-Courage-LB", d =>
            {
                HeroOnly(d);
                d.Actions.Add(new ActionAbility
                {
                    Label = "Exhaust: ready attached hero",
                    CanUse = (e, c) => !c.Exhausted && c.AttachedTo != null && c.AttachedTo.Exhausted,
                    Use = (e, c) => { e.Exhaust(c); e.Ready(c.AttachedTo); e.Write($"{c.AttachedTo.Name} readies."); return Task.CompletedTask; }
                });
            });

            // ================================================================ SAGE
            On("Aldous-Greymantle-LB", d => d.Actions.Add(new ActionAbility
            {
                Label = "Exhaust: scout the encounter deck",
                CanUse = (e, c) => InPlay(c) && !c.Exhausted && e.EncounterDeck.Count > 0,
                Use = async (e, c) => { e.Exhaust(c); await PeekEncounter(e, "Aldous Greymantle", true); }
            }));

            On("Caelith-the-Radiant-LB", d => d.Actions.Add(new ActionAbility
            {
                Label = "Spend 1 supply: heal 1",
                CanUse = (e, c) => InPlay(c) && c.Resources > 0 && c.UsedThisRound("heal") == 0 && e.Characters.Any(x => x.Damage > 0),
                Use = async (e, c) =>
                {
                    var t = await e.ChooseOneOrNone("Caelith the Radiant: heal which character?", e.Characters.Where(x => x.Damage > 0), "Cancel");
                    if (t == null) return;
                    c.Resources--; c.MarkRound("heal"); e.Heal(t, 1);
                }
            }));

            On("Wren-Halloway-LB", d => d.Actions.Add(new ActionAbility
            {
                Label = "Exhaust: draw 2",
                CanUse = (e, c) => InPlay(c) && !c.Exhausted && c.UsedThisRound("draw") == 0 && e.Deck.Count > 0,
                Use = (e, c) => { e.Exhaust(c); c.MarkRound("draw"); e.Draw(2); e.Write("Wren Halloway: you draw 2 cards."); return Task.CompletedTask; }
            }));

            On("Springwater-Healer-LB", d => d.Actions.Add(new ActionAbility
            {
                Label = "Exhaust: heal 2 from a hero",
                CanUse = (e, c) => InPlay(c) && !c.Exhausted && e.Heroes.Any(x => x.Damage > 0),
                Use = async (e, c) => { var t = await e.ChooseOne("Springwater Healer: heal which hero?", e.Heroes.Where(x => x.Damage > 0)); e.Exhaust(c); e.Heal(t, 2); }
            }));

            On("Stonehold-Smith-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterEnterPlay, Prompt = "Return the topmost attachment in your discard pile to your hand?",
                Condition = (e, c, a) => a.Subject == c && e.Discard.Any(x => x.Type == CardType.Attachment),
                Resolve = (e, c, a) =>
                {
                    var x = e.Discard.Last(x => x.Type == CardType.Attachment);
                    e.MoveTo(x, Zone.Hand);
                    e.Write($"Stonehold Smith returns {x.Name} to your hand.");
                    return Task.CompletedTask;
                }
            }));

            On("Ilvan-Reedsong-LB", d => d.Actions.Add(new ActionAbility
            {
                Label = "Exhaust: look at the top encounter card",
                CanUse = (e, c) => InPlay(c) && !c.Exhausted && e.EncounterDeck.Count > 0,
                Use = async (e, c) => { e.Exhaust(c); await PeekEncounter(e, "Ilvan Reedsong", false); }
            }));

            On("Deepvein-Miner-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterEnterPlay, Prompt = "Discard a Condition attachment in play?",
                Condition = (e, c, a) => a.Subject == c && e.AllInPlay().Any(x => x.Zone == Zone.Attached && x.HasTrait("Condition")),
                Resolve = async (e, c, a) =>
                {
                    var x = await e.ChooseOne("Deepvein Miner: discard which Condition attachment?", e.AllInPlay().Where(x => x.Zone == Zone.Attached && x.HasTrait("Condition")).ToList());
                    if (x != null) { e.DiscardCard(x); e.Write($"{x.Name} is discarded."); }
                }
            }));

            On("Cadoc-Brightlay-LB", d => d.Actions.Add(new ActionAbility
            {
                Label = "Exhaust: draw 1",
                CanUse = (e, c) => InPlay(c) && !c.Exhausted && e.Deck.Count > 0,
                Use = (e, c) => { e.Exhaust(c); e.Draw(1); e.Write("Cadoc Brightlay: you draw 1 card."); return Task.CompletedTask; }
            }));

            On("Old-Remedies-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Characters.Any(x => x.Damage > 0);
                d.PlayEvent = async (e, c) => { var t = await e.ChooseOne("Old Remedies: heal which character?", e.Characters.Where(x => x.Damage > 0)); e.Heal(t, t.Damage); };
            });

            On("Hoarded-Knowledge-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Deck.Count > 0;
                d.PlayEvent = (e, c) => { e.Draw(3); return Task.CompletedTask; };
            });

            On("Beast-Lore-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Phase == Phase.Quest && e.Staging.Any(x => x.IsEnemy);
                d.PlayEvent = async (e, c) =>
                {
                    var t = await e.ChooseOne("Beast-Lore: choose an enemy in the staging area.", e.Staging.Where(x => x.IsEnemy));
                    e.AddTemp(t, Stat.Threat, -100, Duration.Phase);
                    e.Write($"{t.Name} does not contribute its Threat this phase.");
                };
            });

            On("Hidden-Ways-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Phase == Phase.Quest && e.Staging.Any(x => x.IsLocation);
                d.PlayEvent = async (e, c) =>
                {
                    var t = await e.ChooseOne("Hidden Ways: choose a location in the staging area.", e.Staging.Where(x => x.IsLocation));
                    e.AddTemp(t, Stat.Threat, -100, Duration.Phase);
                    e.Write($"{t.Name} does not contribute its Threat this phase.");
                };
            });

            On("Seek-the-Signs-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Deck.Count > 0 && e.AvailableSupply(Sphere.Sage) > 0;
                d.PlayEvent = async (e, c) =>
                {
                    int max = System.Math.Min(System.Math.Min(e.AvailableSupply(Sphere.Sage), e.Deck.Count), 8);
                    var opts = Enumerable.Range(1, max).Select(i => $"X = {i}").Append("Cancel").ToArray();
                    int pick = await e.Option("Seek the Signs: choose X (pay X supply, look at the top X cards).", opts);
                    int x = pick + 1;
                    if (pick < 0 || pick >= max || !await e.PayAmount(Sphere.Sage, x, $"{c.Name} (X = {x})"))
                    {
                        e.MoveTo(c, Zone.Hand);
                        e.Write("Seek the Signs is cancelled.");
                        return;
                    }
                    var top = e.Deck.Take(x).ToList();
                    var card = await e.ChooseOne("Seek the Signs: choose a card to add to your hand.", top, popup: true);
                    e.MoveTo(card, Zone.Hand);
                    e.Write($"{card.Name} added to your hand.");
                };
            });

            On("Hearthfire-Rest-LB", d =>
            {
                d.CanPlayEvent = (e, c) => e.Heroes.Any(x => x.Damage > 0);
                d.PlayEvent = (e, c) => { foreach (var h in e.Heroes) e.Heal(h, h.Damage); return Task.CompletedTask; };
            });

            On("Pit-Trap-LB", d =>
            {
                d.AttachFilter = (e, a, t) => t.IsEnemy && t.Zone == Zone.Engaged;
                d.PreventsAttack = (e, src, enemy) => src.Zone == Zone.Attached && src.AttachedTo == enemy;
            });

            On("Warden-of-the-Wood-LB", d =>
            {
                HeroOnly(d);
                d.Actions.Add(new ActionAbility
                {
                    Label = "Discard a card: +1 Guard or +1 Resolve",
                    CanUse = (e, c) => c.AttachedTo != null && e.Hand.Count > 0 && c.UsedThisPhase("ward") < 3,
                    Use = async (e, c) =>
                    {
                        var x = await e.ChooseOneOrNone("Warden of the Wood: discard which card?", e.Hand.ToList(), "Cancel");
                        if (x == null) return;
                        int o = await e.Option($"Give {c.AttachedTo.Name}:", "+1 Guard", "+1 Resolve");
                        c.MarkPhase("ward");
                        e.MoveTo(x, Zone.Discard);
                        var stat = o == 0 ? Stat.Guard : Stat.Resolve;
                        e.AddTemp(c.AttachedTo, stat, 1, Duration.Phase);
                        e.Write($"{c.AttachedTo.Name} gets +1 {stat} this phase.");
                    }
                });
            });

            On("Grim-Foresight-LB", d =>
            {
                HeroOnly(d);
                d.Modifier = (e, src, t, s) => AttachedBonus(src, t, s, Stat.Resolve, -1);
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.EnemyAttacks, Prompt = "Exhaust Grim Foresight to look at the attacker's shadow card?",
                    Condition = (e, c, a) => !c.Exhausted && a.Subject?.ShadowCard != null,
                    Resolve = async (e, c, a) =>
                    {
                        e.Exhaust(c);
                        var s = a.Subject.ShadowCard;
                        string text = s.Def.Shadow != null ? s.Def.ShadowText : "no shadow effect";
                        s.FaceDown = false;
                        await e.Ask($"Grim Foresight: {a.Subject.Name}'s shadow card is {s.Name} ({text}).", new[] { s }, 0, 0, new[] { "OK" }, -1, true);
                        s.FaceDown = true;
                    }
                });
            });

            On("Field-Dressing-LB", d => d.Actions.Add(new ActionAbility
            {
                Label = "Exhaust: heal 2 from attached character",
                CanUse = (e, c) => !c.Exhausted && c.AttachedTo != null && c.AttachedTo.Damage > 0,
                Use = (e, c) => { e.Exhaust(c); e.Heal(c.AttachedTo, 2); return Task.CompletedTask; }
            }));

            // ================================================================ NEUTRAL
            On("Orlen-the-Lamplighter-LB", d =>
            {
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterEnterPlay, Forced = true,
                    Condition = (e, c, a) => a.Subject == c,
                    Resolve = async (e, c, a) =>
                    {
                        var enemies = EnemiesInPlay(e).ToList();
                        var opts = new List<string> { "Draw 3 cards", "Reduce your dread by 5" };
                        if (enemies.Count > 0) opts.Add("Deal 4 damage to an enemy");
                        int o = await e.Option("Orlen the Lamplighter: choose one.", opts.ToArray());
                        if (o == 0) e.Draw(3);
                        else if (o == 1) e.ReduceDread(5, c.Name);
                        else
                        {
                            var t = await e.ChooseOne("Orlen the Lamplighter: deal 4 damage to which enemy?", enemies);
                            await e.DealDamage(t, 4);
                        }
                    }
                });
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.EndOfRound, Forced = true,
                    Condition = (e, c, a) => InPlay(c),
                    Resolve = async (e, c, a) => { e.Write("Orlen the Lamplighter departs."); await e.LeavePlay(c); }
                });
            });
        }
    }
}
