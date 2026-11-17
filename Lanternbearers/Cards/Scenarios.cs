using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lanternbearers.Engine;

namespace Lanternbearers.Cards
{
    /// <summary>Scenarios listed in Data/scenarios.json, built from the JSON card data.</summary>
    public static class Scenarios
    {
        public static List<Scenario> All => CardDatabase.ScenarioInfo.Select(CardDatabase.BuildScenario).ToList();
    }

    /// <summary>
    /// Scripts for the quest-specific encounter sets. Shared encounter sets (used by several
    /// scenarios) are scripted in EncounterSets.cs.
    /// </summary>
    public static partial class CardScripts
    {
        private static Task Done => Task.CompletedTask;

        // =====================================================================
        // The Fenwatch Crossing
        // =====================================================================
        private const string FenMother = "The Fen Mother";

        private static void RegisterFenwatch()
        {
            // ---- quest ----
            On("Into-the-Mire-LB", d => d.Setup = (e, c) =>
            {
                e.SearchEncounterToStaging("Bog Spider");
                e.SearchEncounterToStaging("Old Causeway");
                return Done;
            });

            // "A Fork in the Fen": the engine picks one of the two stage 3 cards at random.

            On("The-Drowned-Path-LB", d =>
            {
                d.WhenRevealed = async (e, c) =>
                {
                    var spiders = e.EncounterDeck.Concat(e.EncounterDiscard).Where(x => x.HasTrait("Spider"))
                        .GroupBy(x => x.Name).Select(g => g.First()).ToList();
                    var t = await e.ChooseOne("The Drowned Path: choose a Spider card to add to the staging area.", spiders, popup: true);
                    if (t != null) { e.MoveTo(t, Zone.Staging); e.Write($"{t.Name} is added to the staging area."); }
                    e.Shuffle(e.EncounterDeck);
                };
                d.CanCompleteStage = (e, c) => e.Flags.Contains(FenMother);
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterEnemyDestroyed, Forced = true,
                    Condition = (e, c, a) => c.Zone == Zone.ActiveQuest && a.Subject.Name == FenMother,
                    Resolve = (e, c, a) => { e.Flags.Add(FenMother); return Done; }
                });
            });

            On("The-Watchtower-Road-LB", d =>
                d.CanCompleteStage = (e, c) => !e.AllInPlay().Any(x => x.Name == FenMother));

            // ---- enemies ----
            On("Bog-Spider-LB", d =>
            {
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterEngaged, Forced = true, Condition = (e, c, a) => a.Subject == c,
                    Resolve = (e, c, a) => { e.AddTemp(c, Stat.Might, 1, Duration.Round); e.Write("Bog Spider gets +1 Might this round."); return Done; }
                });
                d.Shadow = async (e, s, ctx) => await DiscardAttachments(e, PlayerAttachments(e), 1, s.Name);
            });

            On("Reedwater-Patrol-LB", d => d.Shadow = (e, s, ctx) =>
            {
                e.AddTemp(ctx.Enemy, Stat.Might, 1, Duration.Attack);
                if (ctx.Undefended) e.RaiseDread(3, s.Name);
                return Done;
            });

            On("Marsh-Bats-LB", d => d.WhenRevealed = async (e, c) => await RemoveFromQuest(e, c.Name));

            // ---- locations ----
            On("Old-Causeway-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterTravel, Prompt = "Ready 1 character you control?",
                Condition = (e, c, a) => a.Subject == c && e.Characters.Any(x => x.Exhausted),
                Resolve = async (e, c, a) =>
                {
                    var t = await e.ChooseOne("Old Causeway: choose a character to ready.", e.Characters.Where(x => x.Exhausted));
                    if (t != null) { e.Ready(t); e.Write($"{t.Name} readies."); }
                }
            }));

            On("Fen-Gate-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterTravel, Prompt = "Draw 2 cards?",
                Condition = (e, c, a) => a.Subject == c && e.Deck.Count > 0,
                Resolve = (e, c, a) => { e.Draw(2); e.Write("Fen Gate: you draw 2 cards."); return Done; }
            }));
        }

        // =====================================================================
        // The Burning of Harrowmere
        // =====================================================================
        private static void RegisterHarrowmere()
        {
            // ---- quest ----
            On("Raiders-on-the-Road-LB", d =>
            {
                d.Setup = async (e, c) =>
                {
                    await e.RevealEncounterCard();
                    if (!e.AllInPlay().Any(x => x.Name == "Hill Giant")) e.SearchEncounterToStaging("Hill Giant");
                    e.Shuffle(e.EncounterDeck);
                };
                d.CanCompleteStage = (e, c) => !e.AllInPlay().Any(x => x.Name == "Hill Giant");
            });

            On("Smoke-on-the-Horizon-LB", d =>
            {
                d.ExtraStagingReveals = 1;
                d.NoEngagementChecks = true;
            });

            On("Ambush-at-the-Gate-LB", d =>
            {
                d.WhenRevealed = async (e, c) =>
                {
                    for (int i = 0; i < 2; i++) await e.RevealEncounterCard();
                    e.SkipStagingStep = true;
                };
                d.CanCompleteStage = (e, c) => !e.Staging.Any(x => x.IsEnemy) && e.Engaged.Count == 0;
            });

            // ---- enemies ----
            On("Raider-Torchbearer-LB", d =>
            {
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterEnemyAttack, Forced = true, Condition = (e, c, a) => a.Subject == c,
                    Resolve = (e, c, a) => { e.RemoveQuestProgress(1, c.Name); return Done; }
                });
                d.Shadow = (e, s, ctx) => { e.RemoveQuestProgress(ctx.Undefended ? 3 : 1, s.Name); return Done; };
            });

            // ---- treacheries ----
            On("Gathering-at-Dusk-LB", d =>
            {
                d.WhenRevealed = (e, c) => { e.Write("Gathering at Dusk: 1 additional card is revealed."); e.GainSurge(); return Done; };
                d.Shadow = (e, s, ctx) => { if (e.DealShadowCard(ctx.Enemy)) e.Write($"{ctx.Enemy.Name} is dealt 1 additional shadow card."); return Done; };
            });

            // ---- locations ----
            On("Banks-of-the-Harrow-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterLocationExplored, Forced = true, Condition = (e, c, a) => a.Subject == c,
                Resolve = (e, c, a) => { e.ReturnToEncounterDeck(c, true); e.Write($"{c.Name} returns to the top of the encounter deck."); return Done; }
            }));

            On("Smouldering-Fields-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.EndOfRound, Forced = true, Condition = (e, c, a) => e.ActiveLocation == c,
                Resolve = (e, c, a) => { e.RaiseDread(1, c.Name); return Done; }
            }));
        }

        // =====================================================================
        // Escape from Cinderhold
        // =====================================================================
        private const string EmberWraith = "The Ember Wraith";
        private const string PrisonerRescued = "PrisonerRescued";

        private static int ObjectivesHeld(GameEngine e) =>
            e.Characters.SelectMany(c => c.Attachments).Count(a => a.Type == CardType.Objective);

        /// <summary>
        /// Takes the top card of your deck face down and puts it into play engaged with you as a Dungeon Guard.
        /// The card is out of play (not in the deck or discard pile) until the guard leaves play.
        /// </summary>
        private static async Task PlaceDungeonGuard(GameEngine e)
        {
            if (e.Deck.Count == 0) { e.Write("Your deck is empty: no Dungeon Guard appears."); return; }
            var top = e.Deck[0];
            e.MoveTo(top, Zone.UnderToken);
            var guard = new Card(CardDatabase.Get("Dungeon-Guard-LB")) { FaceDownCard = top };
            e.MoveTo(guard, Zone.Engaged);
            e.Write("The top card of your deck becomes a face-down Dungeon Guard engaged with you.");
            await e.Fire(Trigger.AfterEngaged, new TriggerArgs { Subject = guard });
        }

        private static void ObjectiveCard(CardDef d)
        {
            d.AttachFilter = (e, a, t) => t.IsHero && t.Zone == Zone.PlayArea;
            d.Actions.Add(new ActionAbility
            {
                Label = "Raise dread by 2: claim this objective",
                CanUse = (e, c) => c.Zone == Zone.Staging && !e.IsGuarded(c) && e.Heroes.Any(h => e.IsValidAttachTarget(c, h)),
                Use = async (e, c) =>
                {
                    var h = await e.ChooseOneOrNone($"Claim {c.Name}: attach it to which hero?", e.Heroes.Where(x => e.IsValidAttachTarget(c, x)), "Cancel");
                    if (h == null) return;
                    e.RaiseDread(2, $"claim {c.Name}");
                    c.GuardedBy = null;
                    e.Attach(c, h);
                    e.Write($"You claim {c.Name} and attach it to {h.Name}.");
                }
            });
        }

        private static void RegisterCinderhold()
        {
            // ---- quest ----
            On("The-Pyrelord's-Keep-LB", d =>
            {
                d.AllyLimitPerRound = 1;
                d.Setup = async (e, c) =>
                {
                    var objs = e.EncounterDeck.Where(x => x.Type == CardType.Objective).ToList();
                    foreach (var o in objs) { e.MoveTo(o, Zone.Staging); e.Write($"{o.Name} is placed in the staging area."); }
                    e.Shuffle(e.EncounterDeck);
                    foreach (var o in objs) await e.GuardObjective(o);

                    // Stage 1B "When revealed": a random hero is taken prisoner.
                    var heroes = e.Heroes.ToList();
                    if (heroes.Count < 2) { e.Flags.Add(PrisonerRescued); e.Write("You have too few heroes for one to be taken prisoner."); return; }
                    var h = heroes[e.Rng.Next(heroes.Count)];
                    e.MoveTo(h, Zone.SetAside);
                    h.Exhausted = false;
                    e.Prisoner = h;
                    e.Write($"{h.Name} has been taken prisoner in the dungeons of Cinderhold!");
                };
                d.CanCompleteStage = (e, c) => ObjectivesHeld(e) >= 1;
            });

            On("Through-the-Undercrofts-LB", d =>
            {
                d.AllyLimitPerRound = 1;
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterQuestProgress, Prompt = "Rescue the prisoner? (The Ember Wraith will enter the staging area.)",
                    Condition = (e, c, a) => c.Zone == Zone.ActiveQuest && e.Prisoner != null,
                    Resolve = async (e, c, a) =>
                    {
                        var h = e.Prisoner;
                        e.Prisoner = null;
                        e.Flags.Add(PrisonerRescued);
                        e.MoveTo(h, Zone.PlayArea);
                        e.Write($"{h.Name} is rescued!");
                        await e.DealDamage(h, 1);
                        e.SearchEncounterToStaging(EmberWraith);
                    }
                });
                d.CanCompleteStage = (e, c) => e.Flags.Contains(PrisonerRescued) && ObjectivesHeld(e) >= 3;
            });

            On("Flight-from-Cinderhold-LB", d =>
            {
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.QuestPhaseBegins, Forced = true, Condition = (e, c, a) => c.Zone == Zone.ActiveQuest,
                    Resolve = async (e, c, a) => await PlaceDungeonGuard(e)
                });
                d.CanCompleteStage = (e, c) => !e.AllInPlay().Any(x => x.Name == EmberWraith);
            });

            // ---- enemies ----
            On("Cinderhold-Jailer-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.QuestFailed, Forced = true,
                Condition = (e, c, a) => c.Zone == Zone.Staging && e.Staging.Any(x => x.Type == CardType.Objective),
                Resolve = async (e, c, a) =>
                {
                    var o = await e.ChooseOne("Cinderhold Jailer: choose an objective to shuffle into the encounter deck.", e.Staging.Where(x => x.Type == CardType.Objective));
                    e.ReturnToEncounterDeck(o, false);
                    e.Write($"Cinderhold Jailer: {o.Name} is shuffled back into the encounter deck.");
                }
            }));

            On("The-Ember-Wraith-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterShadowResolved, Forced = true, Condition = (e, c, a) => a.Enemy == c && e.Characters.Any(),
                Resolve = async (e, c, a) =>
                {
                    var t = await e.ChooseOne("The Ember Wraith: choose a character you control to discard.", e.Characters);
                    e.Write($"The Ember Wraith: {t.Name} is discarded.");
                    await e.LeavePlay(t);
                }
            }));

            On("Crypt-Warden-LB", d => d.Shadow = async (e, s, ctx) =>
            {
                var all = PlayerAttachments(e);
                if (!ctx.Undefended) { await DiscardAttachments(e, all, 1, s.Name); return; }
                foreach (var a in all) e.DiscardCard(a);
                if (all.Count > 0) e.Write($"Discarded: {string.Join(", ", all.Select(a => a.Name))}.");
            });

            On("Dungeon-Guard-LB", d => d.Token = true);

            // ---- treacheries ----
            On("Smothering-Dark-LB", d =>
            {
                d.WhenRevealed = (e, c) => { e.StagingThreatBonus += 1; e.Write("Smothering Dark: +1 total threat in the staging area this phase."); return Done; };
                d.Shadow = (e, s, ctx) => { e.RaiseDread(e.Engaged.Count, s.Name); return Done; };
            });

            On("Iron-Fetters-LB", d =>
            {
                d.WhenRevealed = (e, c) => { e.MoveTo(c, Zone.OnDeck); e.Write("Iron Fetters is attached to the top of your deck."); return Done; };
                d.Shadow = (e, s, ctx) => { e.MoveTo(s, Zone.OnDeck); e.Write("Iron Fetters is attached to the top of your deck."); return Done; };
            });

            // ---- locations ----
            On("Keep-Gate-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterTravel, Forced = true, Condition = (e, c, a) => a.Subject == c,
                Resolve = async (e, c, a) => await PlaceDungeonGuard(e)
            }));

            // ---- objectives ----
            On("Wayfinder's-Map-LB", d =>
            {
                ObjectiveCard(d);
                d.PreventsAttackOrDefend = (e, src, ch) => src.Zone == Zone.Attached && src.AttachedTo == ch;
            });

            On("Ember-Torch-LB", d =>
            {
                ObjectiveCard(d);
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.EndOfRound, Forced = true, Condition = (e, c, a) => c.Zone == Zone.Attached,
                    Resolve = (e, c, a) => { e.RaiseDread(2, c.Name); return Done; }
                });
            });

            On("Cinder-Key-LB", d =>
            {
                ObjectiveCard(d);
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.EndOfRound, Forced = true, Condition = (e, c, a) => c.Zone == Zone.Attached,
                    Resolve = async (e, c, a) => await e.DealDamage(c.AttachedTo, 1)
                });
            });
        }
    }
}
