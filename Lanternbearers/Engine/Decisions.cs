using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lanternbearers.Engine
{
    /// <summary>A question the engine asks the player. The UI resolves it.</summary>
    public class Decision
    {
        public string Prompt;
        public List<Card> Candidates = new();
        public int Min, Max;
        public List<string> Buttons = new();
        /// <summary>Index of the button that submits the selection (needs Min..Max selected). -1 for none.</summary>
        public int ConfirmIndex = -1;
        /// <summary>Show the candidates in an overlay (for cards not visible on the board).</summary>
        public bool Popup;
        public readonly List<Card> Selected = new();
        public readonly TaskCompletionSource<DecisionResult> Tcs = new();
    }

    public class DecisionResult
    {
        public List<Card> Cards = new();
        public int Button = -1;
    }

    public interface IPlayerInterface
    {
        Task<DecisionResult> Ask(Decision d);
        Task Pause(int milliseconds);
    }
}
