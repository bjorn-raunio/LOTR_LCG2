using System.Linq;
using Lanternbearers.Engine;
using Microsoft.Xna.Framework;

namespace Lanternbearers.UI
{
    public static class CardRenderer
    {
        public static Color SphereColor(Sphere s) => s switch
        {
            Sphere.Crown => new Color(105, 70, 140),
            Sphere.Blade => new Color(150, 45, 45),
            Sphere.Sage => new Color(45, 120, 70),
            Sphere.Hearth => new Color(45, 90, 155),
            Sphere.Neutral => new Color(105, 105, 105),
            _ => Color.Gray
        };

        public static Color BodyColor(CardDef d)
        {
            if (d.IsPlayerCard) return Color.Lerp(SphereColor(d.Sphere), new Color(20, 20, 24), 0.55f);
            return d.Type switch
            {
                CardType.Enemy => new Color(70, 26, 26),
                CardType.Location => new Color(72, 58, 36),
                CardType.Treachery => new Color(58, 34, 74),
                CardType.Quest => new Color(88, 78, 46),
                _ => new Color(50, 50, 50)
            };
        }

        public static Color HeaderColor(CardDef d)
        {
            if (d.IsPlayerCard) return SphereColor(d.Sphere);
            return d.Type switch
            {
                CardType.Enemy => new Color(150, 40, 35),
                CardType.Location => new Color(140, 110, 60),
                CardType.Treachery => new Color(115, 60, 140),
                CardType.Quest => new Color(185, 160, 90),
                _ => Color.Gray
            };
        }

        public static string SphereName(Sphere s) => s == Sphere.None ? "" : s.ToString();

        static string StatValue(GameEngine e, Card c, Stat s, int baseV, out Color col)
        {
            col = Color.White;
            if (e == null || c == null) return baseV.ToString();
            int v = e.Get(c, s);
            if (v > baseV) col = new Color(130, 255, 130);
            else if (v < baseV) col = new Color(255, 130, 130);
            return v.ToString();
        }

        public static void DrawBack(Gfx g, Rectangle r, bool encounter, string label = null)
        {
            g.Box(r, encounter ? new Color(55, 30, 30) : new Color(40, 45, 70), new Color(160, 140, 90), 2);
            var inner = new Rectangle(r.X + 6, r.Y + 6, r.Width - 12, r.Height - 12);
            g.Border(inner, new Color(110, 95, 60));
            g.TextCentered(g.Small, label ?? (encounter ? "ENCOUNTER" : "LANTERN"), inner, new Color(200, 180, 120));
        }

        /// <summary>Draws a compact card face. e/card may be null for definition previews.</summary>
        public static void Draw(Gfx g, CardDef d, Card c, GameEngine e, Rectangle r, bool showText = false)
        {
            if (c != null && c.FaceDown) { DrawBack(g, r, true, "SHADOW"); return; }
            g.Box(r, BodyColor(d), new Color(20, 20, 20), 1);
            var header = new Rectangle(r.X, r.Y, r.Width, 34);
            g.Fill(header, HeaderColor(d));

            int x = r.X + 4;
            bool hasCost = d.Type is CardType.Ally or CardType.Attachment or CardType.Event or CardType.Hero;
            if (hasCost)
            {
                var cr = new Rectangle(r.X + 3, r.Y + 3, 22, 22);
                g.Box(cr, new Color(30, 25, 20), new Color(230, 200, 120));
                g.TextCentered(g.Small, d.Cost.ToString(), cr, Color.White);
                x = r.X + 28;
            }
            string name = (d.Unique ? "*" : "") + d.Name;
            var lines = g.Wrap(g.Small, name, r.Right - x - 3);
            if (lines.Count > 2) lines = lines.Take(2).ToList();
            float ny = r.Y + (lines.Count == 1 ? 9 : 2);
            foreach (var l in lines) { g.Text(g.Small, g.Fit(g.Small, l, r.Right - x - 3), new Vector2(x, ny), Color.White); ny += g.Small.LineSpacing - 2; }

            float y = r.Y + 36;
            string typeLine = d.Type == CardType.Quest ? $"Stage {d.StageLabel}" :
                d.Type == CardType.Hero ? "Hero" : d.Type.ToString();
            if (d.Type == CardType.Hero) typeLine += $"  Dread {d.Cost}";
            g.Text(g.Small, g.Fit(g.Small, typeLine, r.Width - 8), new Vector2(r.X + 4, y), new Color(230, 220, 190));
            y += g.Small.LineSpacing;

            // Stats
            switch (d.Type)
            {
                case CardType.Hero:
                case CardType.Ally:
                    StatRow(g, e, c, r.X + 4, y, r.Width - 8,
                        ("R", Stat.Resolve, d.Resolve), ("M", Stat.Might, d.Might), ("G", Stat.Guard, d.Guard), ("H", Stat.Health, d.Health));
                    y += g.Small.LineSpacing;
                    break;
                case CardType.Enemy:
                    StatRow(g, e, c, r.X + 4, y, r.Width - 8, ("E", Stat.Engagement, d.Engagement), ("T", Stat.Threat, d.Threat));
                    y += g.Small.LineSpacing;
                    StatRow(g, e, c, r.X + 4, y, r.Width - 8, ("M", Stat.Might, d.Might), ("G", Stat.Guard, d.Guard), ("H", Stat.Health, d.Health));
                    y += g.Small.LineSpacing;
                    break;
                case CardType.Location:
                    StatRow(g, e, c, r.X + 4, y, r.Width - 8, ("T", Stat.Threat, d.Threat), ("QP", Stat.QuestPoints, d.QuestPoints));
                    y += g.Small.LineSpacing;
                    break;
                case CardType.Quest:
                    StatRow(g, e, c, r.X + 4, y, r.Width - 8, ("QP", Stat.QuestPoints, d.QuestPoints));
                    y += g.Small.LineSpacing;
                    break;
            }
            if (d.Victory > 0) { g.Text(g.Small, $"Victory {d.Victory}", new Vector2(r.X + 4, y), new Color(255, 230, 140)); y += g.Small.LineSpacing; }

            if (showText)
            {
                string t = d.Text;
                if (!string.IsNullOrEmpty(d.ShadowText)) t += (t.Length > 0 ? "\n" : "") + d.ShadowText;
                g.TextWrapped(g.Small, t, new Vector2(r.X + 4, y + 2), r.Width - 8, new Color(220, 220, 220), r.Bottom - 22);
            }
            else if (d.Traits.Length > 0)
            {
                g.Text(g.Small, g.Fit(g.Small, string.Join(". ", d.Traits) + ".", r.Width - 8), new Vector2(r.X + 4, y), new Color(190, 190, 160));
            }

            if (c != null) DrawTokens(g, c, r);
        }

        static void StatRow(Gfx g, GameEngine e, Card c, float x, float y, float width, params (string label, Stat stat, int baseV)[] stats)
        {
            float step = width / stats.Length;
            for (int i = 0; i < stats.Length; i++)
            {
                var (label, stat, b) = stats[i];
                var v = StatValue(e, c, stat, b, out var col);
                g.Text(g.Small, label, new Vector2(x + i * step, y), new Color(200, 190, 150));
                float lw = g.Small.MeasureString(label).X + 2;
                g.Text(g.Small, v, new Vector2(x + i * step + lw, y), col);
            }
        }

        static void DrawTokens(Gfx g, Card c, Rectangle r)
        {
            int tx = r.X + 3;
            int ty = r.Bottom - 20;
            void Token(int n, Color col, string label)
            {
                if (n <= 0) return;
                var tr = new Rectangle(tx, ty, 34, 17);
                g.Box(tr, col, Color.Black);
                g.TextCentered(g.Small, label + n, tr, Color.White);
                tx += 37;
            }
            Token(c.Damage, new Color(170, 30, 30), "D");
            Token(c.Resources, new Color(170, 130, 30), "S");
            Token(c.Progress, new Color(40, 130, 60), "P");

            if (c.Committed)
            {
                var qr = new Rectangle(r.Right - 48, r.Y + 36, 45, 15);
                g.Box(qr, new Color(40, 90, 170), Color.Black);
                g.TextCentered(g.Small, "QUEST", qr, Color.White);
            }
            if (c.Exhausted)
            {
                g.Fill(r, new Color(0, 0, 0, 120));
                var er = new Rectangle(r.X + 4, r.Center.Y - 9, r.Width - 8, 18);
                g.Box(er, new Color(60, 60, 60, 220), new Color(140, 140, 140));
                g.TextCentered(g.Small, "EXHAUSTED", er, new Color(220, 220, 220));
            }
        }

        /// <summary>Large detail view with full text.</summary>
        public static void DrawDetail(Gfx g, Card c, CardDef d, GameEngine e, Rectangle r)
        {
            g.Box(r, BodyColor(d), new Color(200, 180, 110), 2);
            var header = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, 40);
            g.Fill(header, HeaderColor(d));
            g.Text(g.Large, g.Fit(g.Large, (d.Unique ? "* " : "") + d.Name, r.Width - 16), new Vector2(r.X + 8, r.Y + 8), Color.White);
            float y = r.Y + 48;
            string sub = d.Type.ToString();
            if (d.IsPlayerCard) sub = $"{SphereName(d.Sphere)} {d.Type}" + (d.Type == CardType.Hero ? $" - Starting dread {d.Cost}" : $" - Cost {d.Cost}");
            if (d.Type == CardType.Quest) sub = $"Quest stage {d.StageLabel} - {d.Set}";
            else if (!d.IsPlayerCard) sub += $" - {d.Set}";
            g.Text(g.Medium, g.Fit(g.Medium, sub, r.Width - 16), new Vector2(r.X + 8, y), new Color(240, 225, 180)); y += g.Medium.LineSpacing;
            if (d.Traits.Length > 0) { g.Text(g.Medium, string.Join(". ", d.Traits) + ".", new Vector2(r.X + 8, y), new Color(200, 200, 160)); y += g.Medium.LineSpacing; }

            string stats = d.Type switch
            {
                CardType.Hero or CardType.Ally => $"Resolve {V(e, c, Stat.Resolve, d.Resolve)}   Might {V(e, c, Stat.Might, d.Might)}   Guard {V(e, c, Stat.Guard, d.Guard)}   Health {V(e, c, Stat.Health, d.Health)}",
                CardType.Enemy => $"Engage {V(e, c, Stat.Engagement, d.Engagement)}  Threat {V(e, c, Stat.Threat, d.Threat)}  Might {V(e, c, Stat.Might, d.Might)}  Guard {V(e, c, Stat.Guard, d.Guard)}  Health {V(e, c, Stat.Health, d.Health)}",
                CardType.Location => $"Threat {V(e, c, Stat.Threat, d.Threat)}   Quest points {V(e, c, Stat.QuestPoints, d.QuestPoints)}",
                CardType.Quest => $"Quest points {V(e, c, Stat.QuestPoints, d.QuestPoints)}",
                _ => ""
            };
            if (stats.Length > 0) { y = g.TextWrapped(g.Medium, stats, new Vector2(r.X + 8, y + 4), r.Width - 16, Color.White); }
            var kw = new System.Collections.Generic.List<string>();
            if (d.Surge) kw.Add("Surge");
            if (d.Doomed > 0) kw.Add($"Doomed {d.Doomed}");
            if (d.Restricted) kw.Add("Restricted");
            if (d.Ranged) kw.Add("Ranged");
            if (d.Victory > 0) kw.Add($"Victory {d.Victory}");
            if (kw.Count > 0) { g.Text(g.Medium, string.Join(". ", kw) + ".", new Vector2(r.X + 8, y + 2), new Color(255, 220, 130)); y += g.Medium.LineSpacing + 2; }
            y += 6;
            if (!string.IsNullOrEmpty(d.Text)) y = g.TextWrapped(g.Medium, d.Text, new Vector2(r.X + 8, y), r.Width - 16, new Color(235, 235, 235)) + 6;
            if (!string.IsNullOrEmpty(d.ShadowText)) y = g.TextWrapped(g.Medium, d.ShadowText, new Vector2(r.X + 8, y), r.Width - 16, new Color(190, 160, 230)) + 6;

            if (c != null)
            {
                var info = new System.Collections.Generic.List<string>();
                if (c.Damage > 0) info.Add($"Damage {c.Damage}");
                if (c.Resources > 0) info.Add($"Supply {c.Resources}");
                if (c.Progress > 0) info.Add($"Progress {c.Progress}");
                if (c.Exhausted) info.Add("Exhausted");
                if (c.Committed) info.Add("Questing");
                if (info.Count > 0) { g.Text(g.Medium, string.Join("  |  ", info), new Vector2(r.X + 8, y), new Color(255, 200, 120)); y += g.Medium.LineSpacing; }
                if (c.Attachments.Count > 0)
                    y = g.TextWrapped(g.Medium, "Attachments: " + string.Join(", ", c.Attachments.Select(a => a.Name)), new Vector2(r.X + 8, y), r.Width - 16, new Color(180, 220, 255));
                if (c.AttachedTo != null)
                    g.Text(g.Medium, "Attached to " + c.AttachedTo.Name, new Vector2(r.X + 8, y), new Color(180, 220, 255));
            }
        }

        static string V(GameEngine e, Card c, Stat s, int b) => e != null && c != null ? e.Get(c, s).ToString() : b.ToString();
    }
}
