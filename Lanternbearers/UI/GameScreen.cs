using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lanternbearers.Engine;
using Microsoft.Xna.Framework;

namespace Lanternbearers.UI
{
    public class GameScreen : IScreen, IPlayerInterface
    {
        private readonly LanternGame _game;
        private readonly GameEngine _engine;
        private Decision _decision;
        private readonly List<(double remaining, TaskCompletionSource<bool> tcs)> _pauses = new();
        private readonly List<(Card card, Rectangle rect)> _hits = new();
        private readonly List<(string label, Rectangle rect)> _pileHits = new();
        private readonly List<Button> _promptButtons = new();
        private readonly List<Button> _uiButtons = new();
        private Button _autoSkip;
        private Card _hover;
        private string _pileHover;
        private int _logScroll;
        private string _error;
        private Button _menuButton;

        // Layout constants
        const int CW = 110, CH = 150, TAB = 18;
        const int BoardRight = 1230;
        static readonly Rectangle PromptRect = new(8, 616, BoardRight - 8, 52);
        static readonly Rectangle DetailRect = new(1240, 8, 352, 420);
        static readonly Rectangle StatusRect = new(1240, 434, 352, 150);
        static readonly Rectangle LogRect = new(1240, 590, 352, 302);

        public GameScreen(LanternGame game, List<CardDef> heroes, List<CardDef> deck, Scenario scenario)
        {
            _game = game;
            _engine = new GameEngine(this);
            _autoSkip = new Button { Rect = new Rectangle(StatusRect.X + 8, StatusRect.Bottom - 34, 200, 28), Label = "Auto-skip empty windows", Toggled = true };
            _autoSkip.OnClick = () => { _engine.AutoSkipEmptyWindows = !_engine.AutoSkipEmptyWindows; _autoSkip.Toggled = _engine.AutoSkipEmptyWindows; };
            _menuButton = new Button { Rect = new Rectangle(StatusRect.Right - 130, StatusRect.Bottom - 34, 122, 28), Label = "Main menu" };
            _menuButton.OnClick = () => _game.Screen = new MenuScreen(_game);
            _uiButtons.Add(_autoSkip);
            _uiButtons.Add(_menuButton);

            var task = _engine.Run(heroes, deck, scenario);
            task.ContinueWith(t => { if (t.IsFaulted) _error = t.Exception?.GetBaseException().ToString(); }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // =====================================================================
        // IPlayerInterface
        // =====================================================================

        public Task<DecisionResult> Ask(Decision d)
        {
            _decision = d;
            BuildPromptButtons();
            return d.Tcs.Task;
        }

        public Task Pause(int ms)
        {
            var tcs = new TaskCompletionSource<bool>();
            _pauses.Add((ms, tcs));
            return tcs.Task;
        }

        private void Resolve(DecisionResult r)
        {
            var d = _decision;
            _decision = null;
            _promptButtons.Clear();
            d.Tcs.SetResult(r);
        }

        private void BuildPromptButtons()
        {
            _promptButtons.Clear();
            var d = _decision;
            int x = PromptRect.Right - 6;
            for (int i = d.Buttons.Count - 1; i >= 0; i--)
            {
                int idx = i;
                int w = Math.Max(110, (int)_gfxMeasure(d.Buttons[i]) + 24);
                x -= w;
                var b = new Button { Rect = new Rectangle(x, PromptRect.Y + 10, w, 32), Label = d.Buttons[i] };
                b.OnClick = () =>
                {
                    if (idx == d.ConfirmIndex) Resolve(new DecisionResult { Cards = d.Selected.ToList(), Button = -1 });
                    else Resolve(new DecisionResult { Cards = d.Selected.ToList(), Button = idx });
                };
                _promptButtons.Insert(0, b);
                x -= 6;
            }
        }

        private float _gfxMeasure(string s) => s.Length * 8.5f;

        // =====================================================================
        // Layout
        // =====================================================================

        private void AddCard(Card c, Rectangle r)
        {
            _hits.Add((c, r));
            int ty = r.Bottom + 1;
            foreach (var a in c.Attachments)
            {
                _hits.Add((a, new Rectangle(r.X, ty, r.Width, TAB - 1)));
                ty += TAB;
            }
            for (int i = 0; i < c.ShadowCards.Count; i++)
                _hits.Add((c.ShadowCards[i], new Rectangle(r.Right + 4 + i * 14, r.Y + 30 + i * 10, 58, 80)));
        }

        private void BuildLayout()
        {
            _hits.Clear();
            _pileHits.Clear();
            var e = _engine;
            // Row 1: quest, active location, decks, staging
            if (e.Quest != null) AddCard(e.Quest, new Rectangle(8, 22, CW, CH));
            if (e.ActiveLocation != null) AddCard(e.ActiveLocation, new Rectangle(126, 22, CW, CH));
            _pileHits.Add(("Encounter deck", new Rectangle(244, 22, 70, 98)));
            _pileHits.Add(("Encounter discard", new Rectangle(320, 22, 70, 98)));
            if (e.EncounterDiscard.Count > 0) _pileHits[^1] = ("Encounter discard", new Rectangle(320, 22, 70, 98));
            LayoutRow(e.Staging, 404, 22, BoardRight - 404);

            // Row 2: engaged enemies (with shadow card)
            int x = 8;
            foreach (var en in e.Engaged)
            {
                AddCard(en, new Rectangle(x, 212, CW, CH));
                x += CW + 70;
            }

            // Row 3: heroes and allies
            var chars = e.PlayArea.OrderBy(c => c.IsHero ? 0 : 1).ToList();
            LayoutRow(chars, 8, 402, BoardRight - 8);

            // Hand
            LayoutRow(e.Hand.ToList(), 8, 692, BoardRight - 8, 120, 168);

            // Popup candidates
            if (_decision != null && _decision.Popup)
            {
                int n = _decision.Candidates.Count;
                int w = 170, h = 235;
                int total = n * (w + 12);
                int px = Math.Max(20, (BoardRight - total) / 2);
                for (int i = 0; i < n; i++)
                    _hits.Add((_decision.Candidates[i], new Rectangle(px + i * (w + 12), 300, w, h)));
            }
        }

        private void LayoutRow(List<Card> cards, int x0, int y, int width, int w = CW, int h = CH)
        {
            if (cards.Count == 0) return;
            int step = w + 8;
            if (cards.Count > 1 && cards.Count * step > width) step = Math.Max(30, (width - w) / (cards.Count - 1));
            for (int i = 0; i < cards.Count; i++) AddCard(cards[i], new Rectangle(x0 + i * step, y, w, h));
        }

        // =====================================================================
        // Update
        // =====================================================================

        public void Update(GameTime time, InputState input)
        {
            double dt = time.ElapsedGameTime.TotalMilliseconds;
            for (int i = _pauses.Count - 1; i >= 0; i--)
            {
                var (rem, tcs) = _pauses[i];
                rem -= dt;
                if (rem <= 0 || input.Clicked) { _pauses.RemoveAt(i); tcs.TrySetResult(true); }
                else _pauses[i] = (rem, tcs);
            }

            BuildLayout();
            _hover = null;
            _pileHover = null;
            bool popup = _decision != null && _decision.Popup;
            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                if (popup && !_decision.Candidates.Contains(_hits[i].card)) continue;
                if (_hits[i].rect.Contains(input.Mouse)) { _hover = _hits[i].card; break; }
            }
            foreach (var p in _pileHits) if (p.rect.Contains(input.Mouse)) _pileHover = p.label;

            if (LogRect.Contains(input.Mouse) && input.Scroll != 0)
                _logScroll = Math.Max(0, _logScroll + Math.Sign(input.Scroll) * 3);

            if (_engine.IsOver)
            {
                if (input.Clicked && _menuButton.Rect.Contains(input.Mouse)) _menuButton.OnClick();
                if (input.Clicked && GameOverButton.Contains(input.Mouse)) _game.Screen = new MenuScreen(_game);
                return;
            }

            if (!input.Clicked) return;
            foreach (var b in _uiButtons) if (b.Enabled && b.Rect.Contains(input.Mouse)) { b.OnClick(); return; }
            if (_decision == null) return;
            foreach (var b in _promptButtons.ToList())
            {
                b.Enabled = ButtonEnabled(b);
                if (b.Enabled && b.Rect.Contains(input.Mouse)) { b.OnClick(); return; }
            }
            if (_hover != null && _decision.Candidates.Contains(_hover) && _decision.Max > 0)
            {
                var d = _decision;
                if (d.Max == 1 && d.ConfirmIndex < 0)
                {
                    d.Selected.Clear();
                    d.Selected.Add(_hover);
                    Resolve(new DecisionResult { Cards = d.Selected.ToList(), Button = -1 });
                }
                else if (d.Selected.Contains(_hover)) d.Selected.Remove(_hover);
                else if (d.Selected.Count < d.Max) d.Selected.Add(_hover);
            }
        }

        private bool ButtonEnabled(Button b)
        {
            int idx = _promptButtons.IndexOf(b);
            if (_decision == null) return false;
            if (idx == _decision.ConfirmIndex) return _decision.Selected.Count >= _decision.Min && _decision.Selected.Count <= _decision.Max;
            return true;
        }

        static readonly Rectangle GameOverButton = new(BoardRight / 2 - 110, 520, 220, 44);

        // =====================================================================
        // Draw
        // =====================================================================

        public void Draw(Gfx g, InputState input)
        {
            var e = _engine;
            var label = new Color(200, 190, 150);
            g.Text(g.Small, "QUEST", new Vector2(8, 5), label);
            g.Text(g.Small, "LOCATION", new Vector2(126, 5), label);
            g.Text(g.Small, "ENCOUNTER", new Vector2(244, 5), label);
            g.Text(g.Small, $"STAGING AREA  (threat {e.StagingThreat})", new Vector2(404, 5), label);
            g.Text(g.Small, $"ENGAGED WITH YOU ({e.Engaged.Count})", new Vector2(8, 194), label);
            g.Text(g.Small, "YOUR HEROES AND ALLIES", new Vector2(8, 384), label);
            g.Text(g.Small, $"HAND ({e.Hand.Count})    Deck: {e.Deck.Count}    Discard: {e.Discard.Count}", new Vector2(8, 674), label);
            g.Fill(new Rectangle(4, 188, BoardRight, 1), new Color(60, 60, 55));
            g.Fill(new Rectangle(4, 378, BoardRight, 1), new Color(60, 60, 55));

            // Encounter piles
            var deckR = new Rectangle(244, 22, 70, 98);
            if (e.EncounterDeck.Count > 0) CardRenderer.DrawBack(g, deckR, true, e.EncounterDeck.Count.ToString());
            else g.Border(deckR, new Color(80, 80, 80));
            var discR = new Rectangle(320, 22, 70, 98);
            if (e.EncounterDiscard.Count > 0)
            {
                g.Box(discR, new Color(50, 40, 40), new Color(120, 100, 80));
                g.TextWrapped(g.Small, e.EncounterDiscard[^1].Name, new Vector2(discR.X + 4, discR.Y + 4), discR.Width - 8, Color.LightGray, discR.Bottom - 18);
                g.Text(g.Small, $"({e.EncounterDiscard.Count})", new Vector2(discR.X + 4, discR.Bottom - 17), Color.Gray);
            }
            else g.Border(discR, new Color(80, 80, 80));
            g.Text(g.Small, "deck", new Vector2(244, 122), Color.Gray);
            g.Text(g.Small, "discard", new Vector2(320, 122), Color.Gray);
            if (e.Victory.Count > 0) g.Text(g.Small, $"Victory: {e.Victory.Sum(v => v.Def.Victory)}", new Vector2(244, 140), new Color(255, 220, 130));

            bool popup = _decision != null && _decision.Popup;
            foreach (var (card, rect) in _hits)
            {
                if (popup && _decision.Candidates.Contains(card) && card.Zone is Zone.Deck or Zone.EncounterDeck) continue;
                DrawHit(g, card, rect, rect.Height > 100 && card.Zone == Zone.Hand);
            }

            // Treachery being resolved
            if (e.UIReveal != null)
            {
                var rr = new Rectangle(BoardRight - 180, 196, 170, 176);
                CardRenderer.Draw(g, e.UIReveal.Def, e.UIReveal, e, rr, true);
                g.Border(rr, new Color(255, 200, 80), 3);
                g.Text(g.Small, "REVEALED", new Vector2(rr.X, rr.Y - 14), new Color(255, 200, 80));
            }

            // Prompt bar
            g.Box(PromptRect, new Color(40, 36, 28), new Color(200, 170, 100), 2);
            string prompt = _decision?.Prompt ?? (e.IsOver ? "The game is over." : "...");
            if (_decision != null && _decision.Max > 1)
                prompt += $"  [{_decision.Selected.Count} selected]";
            float btnLeft = _promptButtons.Count > 0 ? _promptButtons[0].Rect.X - 8 : PromptRect.Right - 8;
            g.TextWrapped(g.Medium, prompt, new Vector2(PromptRect.X + 10, PromptRect.Y + 6), btnLeft - PromptRect.X - 16, Color.White, PromptRect.Bottom - 2);
            foreach (var b in _promptButtons) { b.Enabled = ButtonEnabled(b); b.Draw(g, input.Mouse); }

            // Popup overlay
            if (popup)
            {
                g.Fill(new Rectangle(0, 250, BoardRight, 300), new Color(0, 0, 0, 200));
                g.Text(g.Large, "Choose", new Vector2(20, 260), new Color(240, 210, 130));
                foreach (var (card, rect) in _hits.Where(h => _decision.Candidates.Contains(h.card) && h.rect.Y == 300))
                {
                    CardRenderer.Draw(g, card.Def, card, e, rect, true);
                    if (_decision.Selected.Contains(card)) g.Border(rect, new Color(120, 255, 120), 3);
                    else if (_decision.Max > 0) g.Border(rect, new Color(255, 220, 60), 2);
                    if (card == _hover) g.Border(rect, Color.White, 2);
                }
            }

            DrawSidePanel(g, input);

            if (e.IsOver)
            {
                g.Fill(new Rectangle(0, 0, BoardRight + 4, LanternGame.VH), new Color(0, 0, 0, 170));
                var box = new Rectangle(BoardRight / 2 - 300, 330, 600, 260);
                g.Box(box, new Color(40, 34, 24), e.Won ? new Color(120, 255, 120) : new Color(255, 100, 100), 3);
                g.TextCentered(g.Title, e.Won ? "VICTORY" : "DEFEAT", new Rectangle(box.X, box.Y + 10, box.Width, 70), e.Won ? new Color(160, 255, 160) : new Color(255, 140, 140));
                g.TextWrapped(g.Medium, e.OverMessage, new Vector2(box.X + 20, box.Y + 90), box.Width - 40, Color.White);
                if (e.Won)
                {
                    int vp = e.Victory.Sum(v => v.Def.Victory);
                    g.Text(g.Medium, $"Score: {e.Dread + e.Round * 10 - vp} (lower is better)", new Vector2(box.X + 20, box.Y + 150), new Color(255, 220, 130));
                }
                new Button { Rect = GameOverButton, Label = "Return to menu" }.Draw(g, input.Mouse);
            }

            if (_error != null)
            {
                g.Box(new Rectangle(20, 20, BoardRight - 40, 400), new Color(60, 0, 0, 240), Color.Red, 2);
                g.TextWrapped(g.Small, "Engine error:\n" + _error, new Vector2(30, 30), BoardRight - 60, Color.White, 410);
            }
        }

        private void DrawHit(Gfx g, Card card, Rectangle rect, bool showText)
        {
            var e = _engine;
            if (card.Zone == Zone.Attached)
            {
                g.Box(rect, CardRenderer.HeaderColor(card.Def) * 0.9f, Color.Black);
                g.Text(g.Small, g.Fit(g.Small, card.Name + (card.Exhausted ? " (exh)" : ""), rect.Width - 6), new Vector2(rect.X + 3, rect.Y + 1), Color.White);
            }
            else if (card.Zone == Zone.Shadow)
            {
                if (card.FaceDown) CardRenderer.DrawBack(g, rect, true, "Shadow");
                else
                {
                    g.Box(rect, new Color(58, 34, 74), new Color(190, 160, 230));
                    g.TextWrapped(g.Small, card.Name, new Vector2(rect.X + 3, rect.Y + 3), rect.Width - 6, Color.White, rect.Bottom);
                }
            }
            else CardRenderer.Draw(g, card.Def, card, e, rect, showText);

            if (_decision != null && !_decision.Popup && _decision.Candidates.Contains(card) && _decision.Max > 0)
                g.Border(rect, _decision.Selected.Contains(card) ? new Color(120, 255, 120) : new Color(255, 220, 60), _decision.Selected.Contains(card) ? 4 : 2);
            if (card == _hover) g.Border(new Rectangle(rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2), Color.White, 1);
        }

        private void DrawSidePanel(Gfx g, InputState input)
        {
            var e = _engine;
            g.Fill(new Rectangle(1234, 0, 366, LanternGame.VH), new Color(18, 20, 20));
            if (_hover != null && !(_hover.FaceDown))
                CardRenderer.DrawDetail(g, _hover, _hover.Def, e, DetailRect);
            else if (_pileHover != null)
            {
                g.Box(DetailRect, new Color(30, 30, 34), new Color(120, 110, 80));
                string txt = _pileHover == "Encounter deck"
                    ? $"Encounter deck: {e.EncounterDeck.Count} cards."
                    : $"Encounter discard pile ({e.EncounterDiscard.Count}):\n" + string.Join("\n", e.EncounterDiscard.AsEnumerable().Reverse().Take(18).Select(c => c.Name));
                if (e.Victory.Count > 0 && _pileHover != "Encounter deck") txt += "\n\nVictory display: " + string.Join(", ", e.Victory.Select(v => v.Name));
                g.TextWrapped(g.Medium, txt, new Vector2(DetailRect.X + 10, DetailRect.Y + 10), DetailRect.Width - 20, Color.White, DetailRect.Bottom);
            }
            else
            {
                g.Box(DetailRect, new Color(26, 26, 30), new Color(70, 70, 60));
                g.TextWrapped(g.Medium, "Hover over a card to read it.\n\nHighlighted cards can be clicked. Use the buttons in the prompt bar to confirm, pass or continue.",
                    new Vector2(DetailRect.X + 12, DetailRect.Y + 12), DetailRect.Width - 24, Color.Gray);
            }

            // Status
            g.Box(StatusRect, new Color(30, 30, 26), new Color(120, 110, 80));
            var dreadCol = e.Dread >= 40 ? new Color(255, 90, 90) : e.Dread >= 30 ? new Color(255, 190, 90) : new Color(230, 230, 230);
            g.Text(g.Large, $"Dread {e.Dread}/{GameEngine.DreadLimit}", new Vector2(StatusRect.X + 10, StatusRect.Y + 6), dreadCol);
            g.Text(g.Medium, $"Round {e.Round}  -  {e.Phase}", new Vector2(StatusRect.X + 10, StatusRect.Y + 38), Color.White);
            g.Text(g.Small, g.Fit(g.Small, e.Step, StatusRect.Width - 20), new Vector2(StatusRect.X + 10, StatusRect.Y + 60), Color.LightGray);
            string q = e.Quest != null ? $"Quest: {e.Quest.Progress}/{e.Get(e.Quest, Stat.QuestPoints)} progress" : "";
            g.Text(g.Small, $"Questing resolve {e.QuestingResolve} vs threat {e.StagingThreat}   {q}", new Vector2(StatusRect.X + 10, StatusRect.Y + 78), new Color(180, 220, 255));
            string extra = (e.Prisoner != null ? $"  |  Prisoner: {e.Prisoner.Name}" : "")
                + (e.DeckConditions.Count > 0 ? $"  |  On your deck: {string.Join(", ", e.DeckConditions.Select(x => x.Name))}" : "");
            g.Text(g.Small, g.Fit(g.Small, (e.Scenario?.Name ?? "") + extra, StatusRect.Width - 20), new Vector2(StatusRect.X + 10, StatusRect.Y + 96), new Color(200, 190, 150));
            foreach (var b in _uiButtons) b.Draw(g, input.Mouse);

            // Log
            g.Box(LogRect, new Color(22, 22, 22), new Color(70, 70, 60));
            var lines = new List<string>();
            foreach (var l in e.Log) lines.AddRange(g.Wrap(g.Small, l, LogRect.Width - 16));
            int visible = (LogRect.Height - 10) / g.Small.LineSpacing;
            _logScroll = Math.Min(_logScroll, Math.Max(0, lines.Count - visible));
            int start = Math.Max(0, lines.Count - visible - _logScroll);
            float y = LogRect.Y + 5;
            for (int i = start; i < Math.Min(lines.Count, start + visible); i++)
            {
                var col = lines[i].StartsWith("=====") ? new Color(240, 210, 120) : new Color(205, 205, 205);
                g.Text(g.Small, lines[i], new Vector2(LogRect.X + 8, y), col);
                y += g.Small.LineSpacing;
            }
        }
    }
}
