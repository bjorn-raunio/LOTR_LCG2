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

    public static partial class CardScripts
    {
        private static Task Done => Task.CompletedTask;

        // =====================================================================
        // The Fenwatch Crossing
        // =====================================================================
        private static void RegisterFenwatch()
        {
            // ---- quest ----
            On("Into-the-Mire-LB", d => d.Setup = (e, c) => { e.SearchEncounterToStaging("Sunken Path"); return Done; });

            On("The-Drowned-Watchtower-LB", d =>
            {
                d.WhenRevealed = (e, c) => { e.SearchEncounterToStaging("Drowned Watchtower"); return Done; };
                d.CanCompleteStage = (e, c) => e.Victory.Any(v => v.Name == "Drowned Watchtower");
            });

            On("The-Mire-Hag-Wakes-LB", d =>
            {
                d.WhenRevealed = (e, c) => { e.SearchEncounterToStaging("Mire Hag"); return Done; };
                d.CanCompleteStage = (e, c) => e.Victory.Any(v => v.Name == "Mire Hag");
            });

            // ---- set aside ----
            On("Drowned-Watchtower-LB", d =>
                d.TravelCost = (e, c) => { e.RaiseDread(2, "travel to the Watchtower"); return Task.FromResult(true); });

            On("Mire-Hag-LB", d =>
                d.Modifier = (e, src, t, s) => t == src && s == Stat.Might ? e.Staging.Count(x => x.IsLocation) : 0);

            // ---- enemies ----
            On("Bog-Lurker-LB", d =>
            {
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterEngaged, Forced = true,
                    Condition = (e, c, a) => a.Subject == c && e.Characters.Any(),
                    Resolve = async (e, c, a) => { var t = await e.ChooseOne("Bog Lurker: choose a character to take 1 damage.", e.Characters); await e.DealDamage(t, 1); }
                });
                d.Shadow = (e, s, ctx) => { e.AddTemp(ctx.Enemy, Stat.Might, 1, Duration.Attack); return Done; };
            });

            On("Marsh-Leech-LB", d =>
                d.Shadow = (e, s, ctx) => { if (ctx.Undefended) e.AddTemp(ctx.Enemy, Stat.Might, 2, Duration.Attack); return Done; });

            On("Fen-Wight-LB", d =>
                d.Shadow = (e, s, ctx) => { if (ctx.Defender != null) e.AddTemp(ctx.Defender, Stat.Guard, -1, Duration.Attack); return Done; });

            // ---- locations ----
            On("Reed-Maze-LB", d =>
                d.Modifier = (e, src, t, s) => t == src && s == Stat.Threat && e.ActiveLocation == null ? 1 : 0);

            On("Old-Fen-Village-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterLocationExplored, Forced = true, Condition = (e, c, a) => a.Subject == c,
                Resolve = (e, c, a) => { e.Draw(1); e.Write("Old Fen Village: you draw 1 card."); return Done; }
            }));

            // ---- treacheries ----
            On("Choking-Mists-LB", d =>
            {
                d.WhenRevealed = (e, c) => { foreach (var ch in e.Characters.Where(x => x.Committed)) e.AddTemp(ch, Stat.Resolve, -1, Duration.Phase); return Done; };
                d.Shadow = (e, s, ctx) => { e.RaiseDread(1, s.Name); return Done; };
            });

            On("Sinking-Ground-LB", d => d.WhenRevealed = async (e, c) =>
            {
                var q = e.Characters.Where(x => x.Committed).ToList();
                if (q.Count == 0) { e.RaiseDread(2, c.Name); return; }
                var t = await e.ChooseOne("Sinking Ground: choose a questing character to remove from the quest.", q);
                t.Committed = false;
                e.Write($"{t.Name} is removed from the quest.");
            });

            On("Will-o'-Wisps-LB", d =>
                d.WhenRevealed = (e, c) => { e.RaiseDread(System.Math.Max(1, e.Staging.Count(x => x.IsLocation)), c.Name); return Done; });
        }

        // =====================================================================
        // The Burning of Harrowmere
        // =====================================================================
        private static void RegisterHarrowmere()
        {
            // ---- quest ----
            On("Raiders-on-the-Road-LB", d => d.Setup = (e, c) => { e.SearchEncounterToStaging("Raider Outrider"); return Done; });

            On("Smoke-on-the-Horizon-LB", d =>
            {
                d.WhenRevealed = (e, c) => { e.SearchEncounterToStaging("Harrowmere Gate"); return Done; };
                d.Modifier = (e, src, t, s) => src == e.Quest && s == Stat.Threat && t.IsEnemy && t.Zone == Zone.Staging ? 1 : 0;
            });

            On("The-Warchief's-Banner-LB", d =>
            {
                d.WhenRevealed = (e, c) => { e.SearchEncounterToStaging("Raider Warchief"); return Done; };
                d.CanCompleteStage = (e, c) => e.Victory.Any(v => v.Name == "Raider Warchief");
            });

            // ---- set aside ----
            On("Raider-Warchief-LB", d =>
                d.Modifier = (e, src, t, s) => t == src && s == Stat.Might && src.Zone == Zone.Engaged ? e.Engaged.Count - 1 : 0);

            On("Harrowmere-Gate-LB", d =>
            {
                d.CanTravel = (e, c) => e.Heroes.Any(h => !h.Exhausted);
                d.TravelCost = async (e, c) =>
                {
                    var h = await e.ChooseOneOrNone("Harrowmere Gate: exhaust a hero to travel here.", e.Heroes.Where(x => !x.Exhausted), "Cancel");
                    if (h == null) return false;
                    e.Exhaust(h);
                    return true;
                };
            });

            // ---- enemies ----
            On("Raider-Outrider-LB", d =>
            {
                d.Triggers.Add(new TriggeredAbility
                {
                    On = Trigger.AfterEngaged, Forced = true, Condition = (e, c, a) => a.Subject == c,
                    Resolve = async (e, c, a) => { e.Write("Raider Outrider charges!"); await e.ResolveEnemyAttack(c, false); }
                });
                d.Shadow = (e, s, ctx) => { e.AddTemp(ctx.Enemy, Stat.Might, 1, Duration.Attack); return Done; };
            });

            On("Raider-Brute-LB", d =>
                d.Shadow = async (e, s, ctx) => { if (ctx.Defender != null) await e.DealDamage(ctx.Defender, 1); });

            On("Hill-Wolf-LB", d =>
                d.Shadow = (e, s, ctx) => { e.RaiseDread(1, s.Name); return Done; });

            // ---- locations ----
            On("Burning-Farmstead-LB", d => d.Triggers.Add(new TriggeredAbility
            {
                On = Trigger.AfterTravel, Forced = true, Condition = (e, c, a) => a.Subject == c,
                Resolve = (e, c, a) => { e.RaiseDread(1, c.Name); return Done; }
            }));

            On("Watch-Hill-LB", d =>
                d.Modifier = (e, src, t, s) => src == e.ActiveLocation && t.IsEnemy && t.Zone == Zone.Staging && s == Stat.Threat ? -1 : 0);

            // ---- treacheries ----
            On("Raiders'-Ambush-LB", d => d.WhenRevealed = async (e, c) =>
            {
                var en = e.Engaged.OrderByDescending(x => e.Get(x, Stat.Might)).FirstOrDefault();
                if (en == null) { e.GainSurge(); return; }
                await e.ResolveEnemyAttack(en, false);
            });

            On("Smoke-and-Ash-LB", d =>
            {
                d.WhenRevealed = async (e, c) => { foreach (var ch in e.Characters.Where(x => x.Exhausted).ToList()) await e.DealDamage(ch, 1); };
                d.Shadow = (e, s, ctx) => { e.RaiseDread(1, s.Name); return Done; };
            });

            On("Call-to-Arms-LB", d =>
            {
                d.WhenRevealed = async (e, c) =>
                {
                    var en = e.Staging.Where(x => x.IsEnemy).OrderBy(x => e.Get(x, Stat.Engagement)).FirstOrDefault();
                    if (en == null) { e.RaiseDread(2, c.Name); return; }
                    await e.Engage(en);
                };
                d.Shadow = (e, s, ctx) => { if (ctx.Enemy.HasTrait("Raider")) e.AddTemp(ctx.Enemy, Stat.Might, 2, Duration.Attack); return Done; };
            });
        }
    }
}
