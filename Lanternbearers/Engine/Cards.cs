using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Lanternbearers.Engine
{
    public enum CardType { Hero, Ally, Attachment, Event, Enemy, Location, Treachery, Quest, Objective }
    public enum Sphere { None, Crown, Blade, Sage, Hearth, Neutral }
    public enum Stat { Resolve, Might, Guard, Health, Threat, Engagement, QuestPoints }
    public enum Phase { Setup, Resource, Planning, Quest, Travel, Encounter, Combat, Refresh }
    public enum Duration { Attack, Phase, Round }

    public enum Zone
    {
        None, Deck, Hand, Discard, PlayArea, Attached, EncounterDeck, EncounterDiscard,
        Staging, ActiveLocation, Engaged, Shadow, QuestDeck, ActiveQuest, Victory, SetAside,
        /// <summary>A condition attached to the top of the player's deck.</summary>
        OnDeck,
        /// <summary>An enemy being revealed from the encounter deck, not yet in play.</summary>
        Revealing,
        /// <summary>A face-down player card acting as a token enemy (e.g. a Dungeon Guard); out of play.</summary>
        UnderToken
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
        EnemyRevealing,         // Subject = enemy being revealed, before it enters play and before its keywords / "when revealed" resolve
        BeforeShadow,           // Subject = shadow card, Enemy = attacker (cancel window)
        EnemyAttacks,           // Subject = attacking enemy, before defenders are declared
        AfterEnemyAttack,       // Subject = attacking enemy, Attack = the resolved attack
        EndOfCombatPhase,       // fired at the end of the combat phase
        QuestPhaseBegins,       // fired at the beginning of the quest phase
        QuestFailed,            // fired after questing unsuccessfully (threat higher than resolve)
        AfterQuestProgress,     // Subject = current quest, Amount = progress placed
        AfterShadowResolved,    // Subject = shadow card, Enemy = attacker (only if it had an effect)
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
        /// <summary>The subject was in play when the event happened (so its own "after ..." abilities can trigger).</summary>
        public bool WasInPlay;
    }

    public class AttackContext
    {
        public Card Enemy;
        public Card Defender;
        public List<Card> Defenders = new();
        public bool Undefended => Defender == null;
        public bool Cancelled;
        /// <summary>Shadow cards revealed for this attack (with or without a shadow effect).</summary>
        public List<Card> Shadows = new();
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
        /// <summary>(engine, source) -> true while the player cannot draw cards.</summary>
        public Func<GameEngine, Card, bool> PreventsDrawing;
        /// <summary>Attachment hook: (engine, attachment) -> whether the attached character readies in the refresh phase.</summary>
        public Func<GameEngine, Card, Task<bool>> RefreshReadyCheck;
        /// <summary>Location: if there is no active location, the player must travel here.</summary>
        public bool MustTravel;
        /// <summary>Enemy: (engine, enemy) -> whether it may be optionally engaged.</summary>
        public Func<GameEngine, Card, bool> CanOptionallyEngage;
        /// <summary>Enemy: excess combat damage from its attacks raises dread instead.</summary>
        public bool ExcessDamageToDread;
        /// <summary>Quest stage: extra encounter cards revealed in each staging step.</summary>
        public int ExtraStagingReveals;
        /// <summary>Quest stage: no engagement checks during the encounter phase.</summary>
        public bool NoEngagementChecks;
        /// <summary>Quest stage: if &gt; 0, at most this many allies can be played each round.</summary>
        public int AllyLimitPerRound;
        /// <summary>Objective: when it enters the staging area, the next encounter card guards it.</summary>
        public bool Guarded;
        /// <summary>A token created by an effect; it is removed from the game instead of being discarded.</summary>
        public bool Token;
        /// <summary>(engine, source, character) -> true if the character cannot attack or defend.</summary>
        public Func<GameEngine, Card, Card, bool> PreventsAttackOrDefend;

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
        /// <summary>Objective: the encounter card guarding it.</summary>
        public Card GuardedBy;
        /// <summary>Token: the face-down card it represents, placed in its owner's discard pile when the token leaves play.</summary>
        public Card FaceDownCard;
        public readonly List<Card> Attachments = new();
        /// <summary>Shadow cards dealt to this enemy (usually 0 or 1).</summary>
        public readonly List<Card> ShadowCards = new();
        public Card ShadowCard => ShadowCards.FirstOrDefault();
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

    /// <summary>A lasting encounter effect that is recalculated as the game state changes (e.g. "each location in the staging area gets +1 Threat until the end of the phase").</summary>
    public class LastingModifier
    {
        public Func<GameEngine, Card, Stat, int> Mod;
        public Duration Duration;
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
