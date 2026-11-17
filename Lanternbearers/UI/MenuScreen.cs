using System.Collections.Generic;
using System.Linq;
using Lanternbearers.Cards;
using Lanternbearers.Engine;
using Microsoft.Xna.Framework;

namespace Lanternbearers.UI
{
    public class MenuScreen : IScreen
    {
        private readonly LanternGame _game;
        private readonly List<CardDef> _heroes;
        private readonly List<CardDef> _selected = new();
        private readonly List<Scenario> _scenarios = Scenarios.All;
        private int _scenario;
        private readonly Dictionary<CardDef, Rectangle> _heroRects = new();
        private readonly List<Button> _buttons = new();
        private Button _start;
        private CardDef _hover;

        public MenuScreen(LanternGame game)
        {
            _game = game;
            _heroes = PlayerCards.Heroes.OrderBy(h => h.Sphere).ToList();
            // Default selection: one of each of three spheres
            _selected.Add(_heroes.First(h => h.Name == "Corwin Ashvale"));
            _selected.Add(_heroes.First(h => h.Name == "Caelith the Radiant"));
            _selected.Add(_heroes.First(h => h.Name == "Maren Haldis"));

            int col = 0;
            foreach (var grp in _heroes.GroupBy(h => h.Sphere))
            {
                int row = 0;
                foreach (var h in grp)
                {
                    _heroRects[h] = new Rectangle(40 + (col % 2) * 410 + row * 126, 172 + (col / 2) * 200, 120, 165);
                    row++;
                }
                col++;
            }
            for (int i = 0; i < _scenarios.Count; i++)
            {
                int idx = i;
                _buttons.Add(new Button { Rect = new Rectangle(40 + i * 420, 632, 400, 44), Label = _scenarios[i].Name, OnClick = () => _scenario = idx });
            }
            _start = new Button { Rect = new Rectangle(40, 836, 300, 50), Label = "Begin the Journey", OnClick = Start };
            _buttons.Add(_start);
        }

        private void Start()
        {
            var deck = PlayerCards.BuildDeck(_selected);
            _game.Screen = new GameScreen(_game, _selected.ToList(), deck, _scenarios[_scenario]);
        }

        public void Update(GameTime time, InputState input)
        {
            _hover = null;
            // topmost first: reverse order of drawing
            foreach (var kv in _heroRects.Reverse())
                if (kv.Value.Contains(input.Mouse)) { _hover = kv.Key; break; }

            for (int i = 0; i < _scenarios.Count; i++) _buttons[i].Toggled = i == _scenario;
            _start.Enabled = _selected.Count == 3;

            if (!input.Clicked) return;
            foreach (var b in _buttons)
                if (b.Enabled && b.Rect.Contains(input.Mouse)) { b.OnClick(); return; }
            if (_hover != null)
            {
                if (_selected.Contains(_hover)) _selected.Remove(_hover);
                else if (_selected.Count < 3) _selected.Add(_hover);
            }
        }

        public void Draw(Gfx g, InputState input)
        {
            g.Text(g.Title, "LANTERNBEARERS", new Vector2(40, 20), new Color(240, 200, 110));
            g.Text(g.Medium, "A cooperative questing card game for one player. Choose 3 heroes and a scenario.", new Vector2(44, 80), new Color(220, 220, 200));
            

            int col = 0;
            foreach (var grp in _heroes.GroupBy(h => h.Sphere))
            {
                g.Text(g.Medium, CardRenderer.SphereName(grp.Key), new Vector2(40 + (col % 2) * 410, 148 + (col / 2) * 200), CardRenderer.SphereColor(grp.Key) * 1.6f);
                col++;
            }
            foreach (var kv in _heroRects)
            {
                CardRenderer.Draw(g, kv.Key, null, null, kv.Value);
                if (_selected.Contains(kv.Key)) g.Border(kv.Value, new Color(120, 255, 120), 3);
                else if (kv.Key == _hover) g.Border(kv.Value, Color.White, 2);
            }

            int dread = _selected.Sum(h => h.Cost);
            g.Text(g.Medium, $"Selected: {string.Join(", ", _selected.Select(h => h.Name))}   (starting dread {dread})", new Vector2(40, 548), Color.White);
            var deck = PlayerCards.BuildDeck(_selected);
            g.Text(g.Small, $"Your deck will contain {deck.Count} cards from the {string.Join(" / ", _selected.Select(h => h.Sphere).Distinct())} spheres plus neutral cards.", new Vector2(40, 572), new Color(200, 200, 200));

            g.Text(g.Large, "Scenario", new Vector2(40, 598), Color.White);
            foreach (var b in _buttons) b.Draw(g, input.Mouse);
            g.TextWrapped(g.Medium, _scenarios[_scenario].Description, new Vector2(40, 686), 820, new Color(220, 220, 200));

            g.TextWrapped(g.Small,
                "How to play: follow the prompt bar in the middle of the screen. Highlighted (yellow) cards can be clicked. " +
                "Hover any card to read it in full on the right. Each round: Resource, Planning, Quest, Travel, Encounter, Combat, Refresh. " +
                "Quest by committing characters: if their total Resolve beats the threat in the staging area you make progress, otherwise your dread rises. " +
                "Enemies whose engagement value is at or below your dread engage you. If your dread reaches 50 or all your heroes fall, you lose. Complete the final quest stage to win.",
                new Vector2(40, 740), 1100, new Color(190, 190, 190));

            var detail = new Rectangle(1180, 120, 390, 420);
            if (_hover != null) CardRenderer.DrawDetail(g, null, _hover, null, detail);
            else if (_selected.Count > 0) CardRenderer.DrawDetail(g, null, _selected.Last(), null, detail);
        }
    }
}
