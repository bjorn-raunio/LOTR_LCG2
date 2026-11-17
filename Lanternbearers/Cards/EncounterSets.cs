using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lanternbearers.Engine;

namespace Lanternbearers.Cards
{
    /// <summary>
    /// Scripts for the shared encounter sets. A scenario includes them through
    /// "IncludedEncounterSets" in Data/scenarios.json, so a set can be reused by several quests.
    /// </summary>
    public static partial class CardScripts
    {
        // ---- helpers for encounter scripts ----
        /// <summary>Attachments you control: your player-card attachments and claimed objectives.</summary>
        private static List<Card> PlayerAttachments(GameEngine e) =>
            e.Characters.SelectMany(c => c.Attachments).Where(a => a.Def.IsPlayerCard || a.Type == CardType.Objective).ToList();

        private static async Task DiscardAttachments(GameEngine e, List<Card> from, int count, string source)
        {
            for (int i = 0; i < count && from.Count > 0; i++)
            {
                var a = await e.ChooseOne($"{source}: choose an attachment to discard.", from);
                if (a == null) return;
                from.Remove(a);
                e.DiscardCard(a);
                e.Write($"{a.Name} is discarded.");
            }
        }

        private static async Task ExhaustCharacters(GameEngine e, int count, string source)
        {
            for (int i = 0; i < count; i++)
            {
                var t = await e.ChooseOne($"{source}: choose a character to exhaust.", e.Characters.Where(x => !x.Exhausted));
                if (t == null) return;
                e.Exhaust(t);
                e.Write($"{t.Name} is exhausted.");
            }
        }

        private static async Task RemoveFromQuest(GameEngine e, string source)
        {
            var t = await e.ChooseOne($"{source}: choose a questing character to remove from the quest.", e.Characters.Where(x => x.Committed));
            if (t == null) return;
            t.Committed = false;
            e.Write($"{t.Name} is removed from the quest.");
        }

        private static Task MightShadow(GameEngine e, AttackContext ctx, int amount)
        {
            e.AddTemp(ctx.Enemy, Stat.Might, amount, Duration.Attack);
            return Done;
        }

        // =====================================================================
        // Fen Spiders
        // =====================================================================
        private static void RegisterFenSpiders()
        {
            On("Brood-Spider-LB", d =>
            {
                d.WhenRevealed = async (e, c) => await ExhaustCharacters(e, 1, c.Name);
                d.Shadow = async (e, s, ctx) => await ExhaustCharacters(e, ctx.Undefended ? 2 : 1, s.Name);
            });

            On("Bloodflies-LB", d =>
            {
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterEngaged, Forced = true, Condition = (e, c, a) => a.Subject == c,
                    Resolve = async (e, c, a) =>
                    {
                        var h = await e.ChooseOne("Bloodflies: choose a hero to take 5 damage.", e.Heroes);
                        await e.DealDamage(h, 5);
                    }
                });
                d.Shadow = async (e, s, ctx) =>
                {
                    int n = ctx.Undefended ? 2 : 1;
                    foreach (var ch in e.Characters.ToList()) await e.DealDamage(ch, n);
                };
            });

            On("The-Fen-Mother-LB", d =>
            {
                d.WhenRevealed = (e, c) => { foreach (var ch in e.Characters.Where(x => x.Committed)) e.AddTemp(ch, Stat.Resolve, -1, Duration.Phase); return Done; };
                d.Shadow = (e, s, ctx) => { e.RaiseDread(ctx.Undefended ? 8 : 4, s.Name); return Done; };
            });

            On("Great-Marsh-Web-LB", d =>
            {
                d.CanTravel = (e, c) => e.Heroes.Any(h => !h.Exhausted);
                d.TravelCost = async (e, c) =>
                {
                    var h = await e.ChooseOneOrNone("Great Marsh Web: exhaust a hero to travel here.", e.Heroes.Where(x => !x.Exhausted), "Cancel");
                    if (h == null) return false;
                    e.Exhaust(h);
                    return true;
                };
            });

            On("Barrow-Hills-LB", d =>
            {
                d.TravelCost = async (e, c) => { await e.RevealEncounterCard(); return true; };
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterLocationExplored, Prompt = "Search the top 5 cards of your deck for 1 card?",
                    Condition = (e, c, a) => a.Subject == c && e.Deck.Count > 0,
                    Resolve = async (e, c, a) =>
                    {
                        var top = e.Deck.Take(5).ToList();
                        var pick = await e.ChooseOneOrNone("Barrow Hills: choose 1 card to add to your hand.", top, "None", popup: true);
                        if (pick != null) { e.MoveTo(pick, Zone.Hand); e.Write($"You add {pick.Name} to your hand."); }
                        e.Shuffle(e.Deck);
                    }
                });
            });

            On("Eyes-in-the-Reeds-LB", d => d.WhenRevealed = (e, c) =>
            {
                var events = e.Hand.Where(x => x.Type == CardType.Event).ToList();
                foreach (var ev in events) e.MoveTo(ev, Zone.Discard);
                e.Write(events.Count == 0 ? "You have no events to discard." : $"You discard {string.Join(", ", events.Select(x => x.Name))}.");
                return Done;
            });

            On("Caught-in-Silk-LB", d =>
            {
                d.WhenRevealed = async (e, c) =>
                {
                    var h = await e.ChooseOne("Caught in Silk: choose a hero to attach it to.", e.Heroes);
                    if (h == null) return;
                    e.Attach(c, h);
                    e.Write($"Caught in Silk is attached to {h.Name}.");
                };
                // Each copy is a separate condition: with 2 copies the hero must pay 4 to ready.
                // The first copy on the hero handles the payment for all copies.
                d.RefreshReadyCheck = async (e, a) =>
                {
                    var h = a.AttachedTo;
                    var copies = h.Attachments.Where(x => x.Def == a.Def).ToList();
                    if (copies[0] != a) return true;
                    int cost = 2 * copies.Count;
                    if (h.Resources >= cost && await e.YesNo($"Caught in Silk: pay {cost} supply from {h.Name}'s pool to ready {h.Name}?"))
                    {
                        h.Resources -= cost;
                        return true;
                    }
                    e.Write($"{h.Name} is caught in silk and does not ready.");
                    return false;
                };
            });
        }

        // =====================================================================
        // Ashen Raiders
        // =====================================================================
        private static void RegisterAshenRaiders()
        {
            On("Ashen-Raider-LB", d =>
            {
                d.WhenRevealed = async (e, c) =>
                {
                    var t = await e.ChooseOne("Ashen Raider: choose a questing character to take 2 damage.", e.Characters.Where(x => x.Committed));
                    if (t != null) await e.DealDamage(t, 2);
                };
                d.Shadow = (e, s, ctx) => MightShadow(e, ctx, ctx.Undefended ? 3 : 1);
            });

            On("Ashen-Chieftain-LB", d =>
            {
                d.Modifier = (e, src, t, s) => t == src && s == Stat.Might ? 2 * src.Resources : 0;
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterEnemyAttack, Forced = true, Condition = (e, c, a) => a.Subject == c,
                    Resolve = (e, c, a) => { c.Resources++; e.Write("Ashen Chieftain grows stronger (+1 supply token)."); return Done; }
                });
            });

            On("Ashen-Beastmaster-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.EnemyAttacks, Forced = true, Condition = (e, c, a) => a.Subject == c,
                Resolve = (e, c, a) => { if (e.DealShadowCard(c)) e.Write("Ashen Beastmaster is dealt 1 additional shadow card."); return Done; }
            }));

            On("Driven-by-Ash-LB", d =>
            {
                d.WhenRevealed = (e, c) =>
                {
                    if (e.Staging.Count == 0) { e.Write("Driven by Ash gains surge."); e.GainSurge(); return Done; }
                    foreach (var x in e.Staging.Where(x => x.IsEnemy || x.IsLocation)) e.AddTemp(x, Stat.Threat, 1, Duration.Phase);
                    return Done;
                };
                d.Shadow = async (e, s, ctx) =>
                {
                    if (ctx.Undefended)
                    {
                        var all = PlayerAttachments(e);
                        foreach (var a in all) e.DiscardCard(a);
                        if (all.Count > 0) e.Write($"Discarded: {string.Join(", ", all.Select(a => a.Name))}.");
                    }
                    else await DiscardAttachments(e, ctx.Defenders.SelectMany(x => x.Attachments).ToList(), 1, s.Name);
                };
            });

            On("The-Pyrelord's-Reach-LB", d =>
                d.WhenRevealed = async (e, c) => { foreach (var ch in e.Characters.Where(x => x.Exhausted).ToList()) await e.DealDamage(ch, 1); });

            On("Ashen-Pass-LB", d =>
            {
                d.CanTravel = (e, c) => e.Hand.Count >= 2;
                d.TravelCost = async (e, c) =>
                {
                    if (!await e.YesNo("Ashen Pass: discard 2 cards from your hand at random to travel here?")) return false;
                    for (int i = 0; i < 2; i++)
                    {
                        var x = e.Hand[e.Rng.Next(e.Hand.Count)];
                        e.MoveTo(x, Zone.Discard);
                        e.Write($"You discard {x.Name}.");
                    }
                    return true;
                };
            });

            On("Drowsing-Stream-LB", d => d.PreventsDrawing = (e, src) => e.ActiveLocation == src);
        }

        // =====================================================================
        // The Wildlands
        // =====================================================================
        private static void RegisterWildlands()
        {
            On("Hound-Rider-LB", d => d.Shadow = (e, s, ctx) =>
            {
                e.MoveTo(s, Zone.Engaged);
                e.Write("Hound Rider joins the fight and attacks you!");
                e.QueueAttack(s);
                e.AtEndOfPhase(() =>
                {
                    if (s.Zone == Zone.Engaged) { e.ReturnToEncounterDeck(s, true); e.Write("Hound Rider returns to the top of the encounter deck."); }
                    return Done;
                });
                return Done;
            });

            On("Hill-Giant-LB", d => d.ExcessDamageToDread = true);

            On("Hillside-Archer-LB", d =>
            {
                d.CanOptionallyEngage = (e, c) => !e.Staging.Any(x => x.IsEnemy && x != c);
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.EndOfCombatPhase, Forced = true, Condition = (e, c, a) => c.Zone == Zone.Staging && e.Characters.Any(),
                    Resolve = async (e, c, a) =>
                    {
                        var t = await e.ChooseOne("Hillside Archer: choose a character to take 1 damage.", e.Characters);
                        await e.DealDamage(t, 1);
                    }
                });
            });

            On("Reed-Adder-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.EnemyAttacks, Forced = true, Condition = (e, c, a) => a.Subject == c,
                Resolve = (e, c, a) => { e.RaiseDread(1, c.Name); return Done; }
            }));

            On("Hill-Wolves-LB", d =>
            {
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterEnemyAttack, Forced = true,
                    Condition = (e, c, a) => a.Subject == c && a.Attack.Shadows.Any(s => s.Def.Shadow == null),
                    Resolve = (e, c, a) =>
                    {
                        e.DiscardShadowCards(c);
                        e.MoveTo(c, Zone.Staging);
                        e.Write("Hill Wolves return to the staging area.");
                        return Done;
                    }
                });
                d.Shadow = (e, s, ctx) => MightShadow(e, ctx, ctx.Undefended ? 2 : 1);
            });

            On("Failing-Hope-LB", d =>
            {
                d.WhenRevealed = (e, c) => { e.RemoveQuestProgress(4, c.Name); return Done; };
                d.Shadow = (e, s, ctx) =>
                {
                    foreach (var x in ctx.Defenders) e.AddTemp(x, Stat.Guard, -99, Duration.Attack);
                    return Done;
                };
            });

            On("The-Barren-Moor-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterTravel, Forced = true, Condition = (e, c, a) => a.Subject == c,
                Resolve = async (e, c, a) => await e.AddProgressToLocation(c, 1)
            }));

            On("The-Empty-Downs-LB", d => d.MustTravel = true);
        }

        // =====================================================================
        // Gathering Dark
        // =====================================================================
        private static void RegisterGatheringDark()
        {
            On("Carrion-Crows-LB", d =>
            {
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterEnemyDestroyed, Forced = true, Condition = (e, c, a) => a.Subject == c && c.Zone == Zone.EncounterDiscard,
                    Resolve = (e, c, a) => { e.ReturnToEncounterDeck(c, false); e.Write("Carrion Crows are shuffled back into the encounter deck."); return Done; }
                });
                d.Shadow = (e, s, ctx) => MightShadow(e, ctx, e.Dread >= 35 ? 2 : 1);
            });

            On("Ill-Storm-LB", d => d.WhenRevealed = async (e, c) =>
            {
                if (e.Dread < 35) { e.Write("Ill Storm: your dread is below 35."); return; }
                foreach (var ch in e.Characters.ToList()) await e.DealDamage(ch, 1);
            });

            On("Hunted-by-Shadow-LB", d =>
            {
                d.WhenRevealed = (e, c) => { e.RaiseDread(e.Characters.Count(x => !x.Committed), c.Name); return Done; };
                d.Shadow = async (e, s, ctx) =>
                {
                    var t = await e.ChooseOne("Hunted by Shadow: choose an exhausted ally to return to your hand.", e.Allies.Where(x => x.Exhausted));
                    if (t == null) { e.RaiseDread(3, s.Name); return; }
                    e.Write($"{t.Name} returns to your hand.");
                    await e.LeavePlay(t, Zone.Hand);
                };
            });

            On("Creeping-Fog-LB", d => d.WhenRevealed = async (e, c) =>
            {
                // Lasting effect: also applies to locations that enter the staging area later this phase.
                e.AddLasting((g, t, s) => s == Stat.Threat && t.IsLocation && t.Zone == Zone.Staging ? 1 : 0, Duration.Phase);
                if (e.Dread < 35 || e.Hand.Count == 0) return;
                var x = await e.ChooseOne("Creeping Fog: choose a card to discard from your hand.", e.Hand, popup: true);
                e.MoveTo(x, Zone.Discard);
                e.Write($"You discard {x.Name}.");
            });
        }
    }
}
