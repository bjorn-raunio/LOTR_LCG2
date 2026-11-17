using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Lanternbearers.Engine;

namespace Lanternbearers.Cards
{
    // ------------------------------------------------------------------ JSON schema
    // Mirrors the layout of Cards/core-set.json (Hall of Beorn style). Stat names use
    // Lanternbearers terms, but the original names are accepted as aliases:
    //   DreadCost|ThreatCost|ResourceCost -> Cost, Resolve|Willpower, Might|Attack,
    //   Guard|Defense, Health|HitPoints, EngagementCost, Threat, QuestPoints,
    //   VictoryPoints, StageNumber.

    public class CardJson
    {
        public string Title { get; set; }
        public string Slug { get; set; }
        public bool IsUnique { get; set; }
        public string CardType { get; set; }
        public string CardSubType { get; set; }
        public string Sphere { get; set; }
        public CardSideJson Front { get; set; }
        public CardSideJson Back { get; set; }
        public string CardSet { get; set; }
        public EncounterInfoJson EncounterInfo { get; set; }
        public int Number { get; set; }
        public int? Quantity { get; set; }
        public string Artist { get; set; }
    }

    public class CardSideJson
    {
        public string Subtitle { get; set; }
        public string ImagePath { get; set; }
        public Dictionary<string, string> Stats { get; set; }
        public List<string> Traits { get; set; }
        public List<string> Keywords { get; set; }
        public List<string> Text { get; set; }
        public string Shadow { get; set; }
        public string FlavorText { get; set; }
    }

    public class EncounterInfoJson
    {
        public string EncounterSet { get; set; }
        public int EasyModeQuantity { get; set; }
        public List<string> IncludedEncounterSets { get; set; }
        public int? StageNumber { get; set; }
        public string StageLetter { get; set; }
    }

    public class ScenarioJson
    {
        public string Name { get; set; }
        public string EncounterSet { get; set; }
        public string Description { get; set; }
        public List<string> IncludedEncounterSets { get; set; }
        /// <summary>Slugs of cards that start set aside instead of in the encounter deck.</summary>
        public List<string> SetAside { get; set; }
    }

    /// <summary>Loads card definitions from Data/Cards/*.json and scenarios from Data/scenarios.json.</summary>
    public static class CardDatabase
    {
        public static string DataDir => Path.Combine(AppContext.BaseDirectory, "Data");

        private static readonly Lazy<Loaded> _data = new(Load);
        public static IReadOnlyList<CardDef> All => _data.Value.Cards;
        public static CardDef Get(string slug) => _data.Value.BySlug[slug];
        public static IReadOnlyList<ScenarioJson> ScenarioInfo => _data.Value.Scenarios;

        private class Loaded
        {
            public List<CardDef> Cards = new();
            public Dictionary<string, CardDef> BySlug = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<CardDef, CardJson> Source = new();
            public List<ScenarioJson> Scenarios = new();
        }

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static Loaded Load()
        {
            var data = new Loaded();
            var cardDir = Path.Combine(DataDir, "Cards");
            if (!Directory.Exists(cardDir)) throw new DirectoryNotFoundException($"Card data folder not found: {cardDir}");

            foreach (var file in Directory.GetFiles(cardDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                List<CardJson> cards;
                try { cards = JsonSerializer.Deserialize<List<CardJson>>(File.ReadAllText(file), Options) ?? new(); }
                catch (JsonException ex) { throw new InvalidDataException($"{Path.GetFileName(file)}: {ex.Message}", ex); }

                foreach (var j in cards)
                {
                    CardDef d;
                    try { d = ToDef(j); }
                    catch (Exception ex) { throw new InvalidDataException($"{Path.GetFileName(file)}: card '{j.Title}': {ex.Message}", ex); }
                    if (!data.BySlug.TryAdd(d.Id, d)) throw new InvalidDataException($"{Path.GetFileName(file)}: duplicate slug '{d.Id}'.");
                    data.Cards.Add(d);
                    data.Source[d] = j;
                }
            }

            CardScripts.Apply(data.BySlug);

            var scenFile = Path.Combine(DataDir, "scenarios.json");
            if (File.Exists(scenFile))
                data.Scenarios = JsonSerializer.Deserialize<List<ScenarioJson>>(File.ReadAllText(scenFile), Options) ?? new();
            return data;
        }

        /// <summary>Builds a fresh Scenario from the cards of its encounter set(s).</summary>
        public static Scenario BuildScenario(ScenarioJson info)
        {
            var data = _data.Value;
            var sets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { info.EncounterSet ?? info.Name };
            foreach (var s in info.IncludedEncounterSets ?? new()) sets.Add(s);

            var cards = data.Cards.Where(c => data.Source[c].EncounterInfo?.EncounterSet is string es && sets.Contains(es)).ToList();
            var quests = cards.Where(c => c.Type == CardType.Quest).ToList();
            foreach (var q in quests.ToList())
                foreach (var s in data.Source[q].EncounterInfo.IncludedEncounterSets ?? new())
                    if (sets.Add(s)) cards.AddRange(data.Cards.Where(c => data.Source[c].EncounterInfo?.EncounterSet == s));

            var setAside = (info.SetAside ?? new()).Select(slug =>
                data.BySlug.TryGetValue(slug, out var d) ? d : throw new InvalidDataException($"Scenario '{info.Name}': set-aside card '{slug}' not found.")).ToList();

            var sc = new Scenario { Name = info.Name, Description = info.Description ?? "" };
            sc.QuestStages.AddRange(quests.OrderBy(q => data.Source[q].EncounterInfo.StageNumber ?? ParseInt(q.StageLabel)).ThenBy(q => q.StageLabel));
            sc.SetAside.AddRange(setAside);
            sc.EncounterCards.AddRange(cards.Where(c => c.Type != CardType.Quest && !setAside.Contains(c)));
            if (sc.QuestStages.Count == 0) throw new InvalidDataException($"Scenario '{info.Name}' has no quest cards.");
            return sc;
        }

        // ------------------------------------------------------------------ mapping
        private static CardDef ToDef(CardJson j)
        {
            if (string.IsNullOrWhiteSpace(j.Title)) throw new InvalidDataException("missing Title");
            if (!Enum.TryParse<CardType>(j.CardType, true, out var type)) throw new InvalidDataException($"unsupported CardType '{j.CardType}'");

            var front = j.Front ?? new CardSideJson();
            var d = new CardDef
            {
                Id = string.IsNullOrWhiteSpace(j.Slug) ? j.Title.Replace(' ', '-') : j.Slug,
                Name = j.Title,
                Type = type,
                Unique = j.IsUnique,
                Set = j.EncounterInfo?.EncounterSet ?? j.CardSet ?? "",
                Copies = j.Quantity ?? 1,
                Number = j.Number,
            };

            if (!string.IsNullOrWhiteSpace(j.Sphere) && !string.Equals(j.Sphere, "None", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse<Sphere>(j.Sphere, true, out var sphere)) throw new InvalidDataException($"unknown Sphere '{j.Sphere}'");
                d.Sphere = sphere;
            }

            // Stats: front first, back overrides (e.g. quest points on a quest's B side).
            var stats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var side in new[] { front, j.Back })
                if (side?.Stats != null) foreach (var kv in side.Stats) stats[kv.Key] = kv.Value;

            d.Cost = Stat(stats, "DreadCost", "ThreatCost", "ResourceCost");
            d.XCost = stats.TryGetValue("ResourceCost", out var rc) && rc?.Trim().Equals("X", StringComparison.OrdinalIgnoreCase) == true;
            d.Resolve = Stat(stats, "Resolve", "Willpower");
            d.Might = Stat(stats, "Might", "Attack");
            d.Guard = Stat(stats, "Guard", "Defense");
            d.Health = Stat(stats, "Health", "HitPoints");
            d.Threat = Stat(stats, "Threat");
            d.Engagement = Stat(stats, "EngagementCost");
            d.QuestPoints = Stat(stats, "QuestPoints");
            d.Victory = Stat(stats, "VictoryPoints");
            d.StageLabel = j.EncounterInfo?.StageNumber?.ToString()
                ?? (stats.TryGetValue("StageNumber", out var stage) ? stage.TrimEnd('A', 'B', 'a', 'b') : "");

            d.Traits = (front.Traits ?? new()).Select(t => t.Trim().TrimEnd('.').Trim()).Where(t => t.Length > 0).ToArray();
            foreach (var kw in (front.Keywords ?? new()).Select(k => k.Trim().TrimEnd('.').Trim()))
                ApplyKeyword(d, kw);

            var text = (front.Text ?? new()).Concat(j.Back?.Text ?? new()).Select(t => t.Trim()).Where(t => t.Length > 0);
            d.Text = string.Join("\n", text);
            d.ShadowText = front.Shadow?.Trim() ?? "";
            return d;
        }

        private static void ApplyKeyword(CardDef d, string kw)
        {
            if (kw.Length == 0) return;
            var parts = kw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int n = parts.Length > 1 ? ParseInt(parts[^1]) : 0;
            switch (parts[0].ToLowerInvariant())
            {
                case "ranged": d.Ranged = true; break;
                case "sentinel": d.Sentinel = true; break;
                case "restricted": d.Restricted = true; break;
                case "surge": d.Surge = true; break;
                case "guarded": d.Guarded = true; break;
                case "doomed": d.Doomed = n; break;
                case "victory": d.Victory = n; break;
                case "cannot" when kw.Equals("Cannot have attachments", StringComparison.OrdinalIgnoreCase): d.CannotHaveAttachments = true; break;
                default: throw new InvalidDataException($"unknown keyword '{kw}'");
            }
        }

        private static int Stat(Dictionary<string, string> stats, params string[] keys)
        {
            foreach (var k in keys)
                if (stats.TryGetValue(k, out var v)) return ParseInt(v);
            return 0;
        }

        private static int ParseInt(string s) => int.TryParse(s?.Trim(), out var v) ? v : 0;
    }
}
