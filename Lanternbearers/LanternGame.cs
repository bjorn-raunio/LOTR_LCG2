using System;
using System.Threading;
using Lanternbearers.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Lanternbearers
{
    public interface IScreen
    {
        void Update(GameTime time, InputState input);
        void Draw(Gfx g, InputState input);
    }

    public class InputState
    {
        public Point Mouse;
        public bool Clicked;
        public bool RightClicked;
        public int Scroll;
    }

    public class LanternGame : Game
    {
        public const int VW = 1600, VH = 900;
        private readonly GraphicsDeviceManager _graphics;
        private readonly Gfx _gfx = new();
        private readonly MainThreadContext _ctx = new();
        private MouseState _prevMouse;
        private readonly InputState _input = new();
        private Matrix _scale = Matrix.Identity;
        private float _scaleF = 1f;
        private Vector2 _offset;
        public IScreen Screen;

        public LanternGame()
        {
            _graphics = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = VW,
                PreferredBackBufferHeight = VH,
                PreferMultiSampling = true
            };
            Content.RootDirectory = "Content";
            IsMouseVisible = true;
            Window.AllowUserResizing = true;
            Window.Title = "Lanternbearers - a cooperative questing card game";
        }

        protected override void Initialize()
        {
            SynchronizationContext.SetSynchronizationContext(_ctx);
            base.Initialize();
        }

        protected override void LoadContent()
        {
            _gfx.Batch = new SpriteBatch(GraphicsDevice);
            _gfx.Pixel = new Texture2D(GraphicsDevice, 1, 1);
            _gfx.Pixel.SetData(new[] { Color.White });
            _gfx.Small = Content.Load<SpriteFont>("Fonts/Small");
            _gfx.Medium = Content.Load<SpriteFont>("Fonts/Medium");
            _gfx.Large = Content.Load<SpriteFont>("Fonts/Large");
            _gfx.Title = Content.Load<SpriteFont>("Fonts/Title");
            Screen = new MenuScreen(this);
        }

        protected override void Update(GameTime gameTime)
        {
            if (SynchronizationContext.Current != _ctx) SynchronizationContext.SetSynchronizationContext(_ctx);

            var vp = GraphicsDevice.Viewport;
            _scaleF = Math.Min(vp.Width / (float)VW, vp.Height / (float)VH);
            _offset = new Vector2((vp.Width - VW * _scaleF) / 2f, (vp.Height - VH * _scaleF) / 2f);
            _scale = Matrix.CreateScale(_scaleF) * Matrix.CreateTranslation(_offset.X, _offset.Y, 0);

            var m = Mouse.GetState();
            _input.Mouse = new Point((int)((m.X - _offset.X) / _scaleF), (int)((m.Y - _offset.Y) / _scaleF));
            _input.Clicked = IsActive && m.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed;
            _input.RightClicked = IsActive && m.RightButton == ButtonState.Released && _prevMouse.RightButton == ButtonState.Pressed;
            _input.Scroll = m.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            _prevMouse = m;

            Screen?.Update(gameTime, _input);
            _ctx.Pump();
            base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
            GraphicsDevice.Clear(new Color(12, 12, 16));
            _gfx.Batch.Begin(samplerState: SamplerState.LinearClamp, transformMatrix: _scale);
            _gfx.Fill(new Rectangle(0, 0, VW, VH), new Color(24, 28, 26));
            Screen?.Draw(_gfx, _input);
            _gfx.Batch.End();
            base.Draw(gameTime);
        }
    }
}
