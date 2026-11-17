using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Lanternbearers.Engine
{
    public enum CardType { Hero, Ally, Attachment, Event, Enemy, Location, Treachery, Quest }
    public enum Sphere { None, Crown, Blade, Sage, Hearth, Neutral }
    public enum Stat { Resolve, Might, Guard, Health, Threat, Engagement, QuestPoints }
    public enum Phase { Setup, Resource, Planning, Quest, Travel, Encounter, Combat, Refresh }
    public enum Duration { Attack, Phase, Round }

    public enum Zone
    {
        None, Deck, Hand, Discard, PlayArea, Attached, EncounterDeck, EncounterDiscard,
        Staging, ActiveLocation, Engaged, Shadow, QuestDeck, ActiveQuest, Victory, SetAside
    }

    public enum Trigger
    {
        ResourcePhase,          // forced "collect" style effects
        AfterEnterPlay,         // Subject = card that entered play
        AfterCommit,            // Subject = committed character
        AfterTravel,            // Subject = location travelled to
        AfterLocationExplored,  // Subject = explored location
        BeforeWhenRevealed,     // Subject = revealed encounter card (cancel window)
        AfterTreacheryRevealed, // Subject = treachery
        AfterEngaged,           // Subject = enemy that engaged
        DefenderDeclared,       // Subject = defender, Enemy = attacker
        AfterDefend,            // Subject = defender, Enemy = attacker
        AfterEnemyDestroyed,    // Subject = enemy, Attackers = characters that attacked it (if any)
        AfterDamaged,           // Subject = character that survived damage, Amount = damage dealt
        AfterCharacterLeavesPlay, // Subject = character (now out of play), Destroyed = whether it was destroyed
        AfterEncounterRevealed, // Subject = non-treachery encounter card just put into the staging area
        BeforeShadow,           // Subject = shadow card, Enemy = attacker (cancel window)
        EnemyAttacks,           // Subject = attacking enemy, before defenders are declared
        EndOfRound,             // fired at the end of the refresh phase
    }

    public class TriggerArgs
    {
        public Card Subject;
        public Card Enemy;
        public List<Card> Attackers = new();
        public AttackContext Attack;
        public bool Cancelled;
        public int Amount;
        public bool Destroyed;
    }

    public class AttackContext
    {
        public Card Enemy;
        public Card Defender;
        public List<Card> Defenders = new();
        public bool Undefended => Defender == null;
        public bool Cancelled;
    }

    public class ActionAbility
    {
        public string Label;
        public Func<GameEngine, Card, bool> CanUse;
        public Func<GameEngine, Card, Task> Use;
    }

    public class TriggeredAbility
    {
        public Trigger On;
        public bool Forced;
        /// <summary>The ability can be used while the (non-event) card is in hand.</summary>
        public bool FromHand;
        public string Prompt;
        public Func<GameEngine, Card, TriggerArgs, bool> Condition;
        public Func<GameEngine, Card, TriggerArgs, Task> Resolve;
    }

    /// <summary>Static, shared definition of a card.</summary>
    public class CardDef
    {
        public string Id;
        public string Name;
        public CardType Type;
        public Sphere Sphere = Sphere.None;
        public string Set = "";
        public int Number;

        /// <summary>Play cost (allies/attachments/events) or starting dread (heroes).</summary>
        public int Cost;
        public int Resolve, Might, Guard, Health;
        public int Threat, Engagement, QuestPoints, Victory;
        public int Doomed;
        public bool Unique, Restricted, Surge, Ranged, Sentinel, CannotHaveAttachments;
        public string[] Traits = Array.Empty<string>();
        public string Text = "";
        public string ShadowText = "";
        public string StageLabel = "";
        public int Copies = 1;
        /// <summary>Cost is X: the card's script chooses X and pays it.</summary>
        public bool XCost;
        /// <summary>If &gt; 0, the cost must be paid 1 supply each from this many different heroes.</summary>
        public int PayFromDistinctHeroes;
        /// <summary>This character can attack enemies in the staging area (alone).</summary>
        public bool AttacksStaging;
        /// <summary>Trait gained by the card this is attached to.</summary>
        public string GrantsTrait;
        /// <summary>(attachment, hero, sphere) -> attached hero can also pay for cards of that sphere.</summary>
        public Func<Card, Card, Sphere, bool> GrantsSphere;
        /// <summary>(engine, source, enemy) -> true if the enemy cannot attack.</summary>
        public Func<GameEngine, Card, Card, bool> PreventsAttack;

        // ---- behaviour hooks ----
        public Func<GameEngine, Card, Task> WhenRevealed;
        public Func<GameEngine, Card, Task> Setup;                    // quest stage 1 setup
        public Func<GameEngine, Card, AttackContext, Task> Shadow;
        public Func<GameEngine, Card, bool> CanPlayEvent;
        public Func<GameEngine, Card, Task> PlayEvent;
        public Func<GameEngine, Card, Card, bool> AttachFilter;      // (engine, attachment, target)
        public Func<GameEngine, Card, Card, Stat, int> Modifier;      // (engine, source, target, stat)
        public Func<GameEngine, Card, bool> CanTravel;
        public Func<GameEngine, Card, Task<bool>> TravelCost;
        public Func<GameEngine, Card, bool> CanCompleteStage;
        public List<ActionAbility> Actions = new();
        public List<TriggeredAbility> Triggers = new();

        public bool HasTrait(string t) => Traits.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase));
        public bool IsCharacter => Type == CardType.Hero || Type == CardType.Ally;
        public bool IsPlayerCard => Type is CardType.Hero or CardType.Ally or CardType.Attachment or CardType.Event;
    }

    /// <summary>A physical copy of a card in a game.</summary>
    public class Card
    {
        private static int _next;
        public readonly int Uid = ++_next;
        public readonly CardDef Def;

        public Zone Zone = Zone.None;
        public bool Exhausted;
        public bool Committed;
        public bool FaceDown;
        public int Damage, Resources, Progress;
        public Card AttachedTo;
        public readonly List<Card> Attachments = new();
        public Card ShadowCard;
        public readonly Dictionary<string, int> PhaseCounters = new();
        public readonly Dictionary<string, int> RoundCounters = new();

        public Card(CardDef def) { Def = def; }

        public string Name => Def.Name;
        public CardType Type => Def.Type;
        public bool IsHero => Def.Type == CardType.Hero;
        public bool IsAlly => Def.Type == CardType.Ally;
        public bool IsCharacter => Def.IsCharacter;
        public bool IsEnemy => Def.Type == CardType.Enemy;
        public bool IsLocation => Def.Type == CardType.Location;
        public bool HasTrait(string t) => Def.HasTrait(t) || Attachments.Any(a => string.Equals(a.Def.GrantsTrait, t, StringComparison.OrdinalIgnoreCase));

        public int UsedThisPhase(string key) => PhaseCounters.TryGetValue(key, out var v) ? v : 0;
        public int UsedThisRound(string key) => RoundCounters.TryGetValue(key, out var v) ? v : 0;
        public void MarkPhase(string key) => PhaseCounters[key] = UsedThisPhase(key) + 1;
        public void MarkRound(string key) => RoundCounters[key] = UsedThisRound(key) + 1;

        public override string ToString() => Name;
    }

    public class TempModifier
    {
        public Card Target;
        public Stat Stat;
        public int Amount;
        public Duration Duration;
    }

    public class GameOverException : Exception
    {
        public bool Won;
        public GameOverException(bool won, string message) : base(message) { Won = won; }
    }
}
