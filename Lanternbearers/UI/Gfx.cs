using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Lanternbearers.UI
{
    /// <summary>Runs async continuations on the game thread, pumped once per Update.</summary>
    public sealed class MainThreadContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback, object)> _queue = new();
        public override void Post(SendOrPostCallback d, object state) => _queue.Enqueue((d, state));
        public override void Send(SendOrPostCallback d, object state) => d(state);
        public void Pump()
        {
            int guard = 0;
            while (guard++ < 10000 && _queue.TryDequeue(out var item)) item.Item1(item.Item2);
        }
    }

    public class Gfx
    {
        public SpriteBatch Batch;
        public Texture2D Pixel;
        public SpriteFont Small, Medium, Large, Title;

        public void Fill(Rectangle r, Color c) => Batch.Draw(Pixel, r, c);

        public void Border(Rectangle r, Color c, int t = 1)
        {
            Fill(new Rectangle(r.X, r.Y, r.Width, t), c);
            Fill(new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
            Fill(new Rectangle(r.X, r.Y, t, r.Height), c);
            Fill(new Rectangle(r.Right - t, r.Y, t, r.Height), c);
        }

        public void Box(Rectangle r, Color fill, Color border, int t = 1) { Fill(r, fill); Border(r, border, t); }

        public void Text(SpriteFont f, string s, Vector2 p, Color c) => Batch.DrawString(f, s ?? "", new Vector2((int)p.X, (int)p.Y), c);

        public void TextCentered(SpriteFont f, string s, Rectangle r, Color c)
        {
            var size = f.MeasureString(s);
            Text(f, s, new Vector2(r.X + (r.Width - size.X) / 2, r.Y + (r.Height - size.Y) / 2), c);
        }

        public List<string> Wrap(SpriteFont f, string text, float width)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            foreach (var para in text.Split('\n'))
            {
                var words = para.Split(' ');
                var sb = new StringBuilder();
                foreach (var w in words)
                {
                    var trial = sb.Length == 0 ? w : sb + " " + w;
                    if (f.MeasureString(trial).X > width && sb.Length > 0)
                    {
                        lines.Add(sb.ToString());
                        sb.Clear().Append(w);
                    }
                    else { sb.Clear().Append(trial); }
                }
                lines.Add(sb.ToString());
            }
            return lines;
        }

        /// <summary>Draws wrapped text; returns the Y after the last line. Stops at maxY.</summary>
        public float TextWrapped(SpriteFont f, string text, Vector2 p, float width, Color c, float maxY = float.MaxValue)
        {
            float y = p.Y;
            foreach (var l in Wrap(f, text, width))
            {
                if (y + f.LineSpacing > maxY) { Text(f, "...", new Vector2(p.X, y - 2), c); return y; }
                Text(f, l, new Vector2(p.X, y), c);
                y += f.LineSpacing;
            }
            return y;
        }

        public string Fit(SpriteFont f, string s, float width)
        {
            if (f.MeasureString(s).X <= width) return s;
            while (s.Length > 1 && f.MeasureString(s + "..").X > width) s = s[..^1];
            return s + "..";
        }
    }

    public class Button
    {
        public Rectangle Rect;
        public string Label;
        public bool Enabled = true;
        public Action OnClick;
        public bool Toggled;
        public Color? Accent;

        public void Draw(Gfx g, Point mouse)
        {
            bool hover = Enabled && Rect.Contains(mouse);
            var fill = !Enabled ? new Color(45, 45, 50) : Toggled ? new Color(70, 110, 70) : hover ? new Color(95, 85, 60) : new Color(65, 58, 45);
            g.Box(Rect, fill, Accent ?? (Enabled ? new Color(210, 180, 110) : new Color(90, 90, 90)), Toggled ? 2 : 1);
            g.TextCentered(g.Medium, g.Fit(g.Medium, Label, Rect.Width - 8), Rect, Enabled ? Color.White : Color.Gray);
        }
    }
}
