using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lanternbearers.Engine;

namespace Lanternbearers.Cards
{
    /// <summary>
    /// Card behaviour that cannot be expressed as data. Card text, stats, traits and keywords
    /// come from the JSON files; scripts are attached here by the card's Slug.
    /// </summary>
    public static partial class CardScripts
    {
        private static readonly Dictionary<string, Action<CardDef>> Scripts = new(StringComparer.OrdinalIgnoreCase);

        static CardScripts()
        {
            RegisterPlayerCards();
            RegisterFenwatch();
            RegisterHarrowmere();
            RegisterCinderhold();
            RegisterFenSpiders();
            RegisterAshenRaiders();
            RegisterWildlands();
            RegisterGatheringDark();
        }

        private static void On(string slug, Action<CardDef> script)
        {
            if (!Scripts.TryAdd(slug, script)) throw new InvalidOperationException($"Duplicate card script for '{slug}'.");
        }

        /// <summary>Attaches scripts to the loaded definitions. Fails if a script names a card that is not in the data.</summary>
        public static void Apply(IReadOnlyDictionary<string, CardDef> bySlug)
        {
            var missing = Scripts.Keys.Where(k => !bySlug.ContainsKey(k)).ToList();
            if (missing.Count > 0)
                throw new InvalidDataException("Card scripts refer to cards missing from the JSON data: " + string.Join(", ", missing));
            foreach (var (slug, script) in Scripts) script(bySlug[slug]);
        }

        // ---- helpers shared by scripts ----
        private static int AttachedBonus(Card src, Card target, Stat stat, Stat want, int amount)
            => src.Zone == Zone.Attached && src.AttachedTo == target && stat == want ? amount : 0;

        private static bool InPlay(Card c) => c.Zone == Zone.PlayArea;
    }
}
