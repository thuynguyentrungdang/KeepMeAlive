//====================[ Imports ]====================
using EFT;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Components
{
    //====================[ Enums ]====================
    public enum ReviveSource { Self = 0, Team = 1 }

    public enum RMState
    {
        None,
        CoolDown,
        BleedingOut,
        Reviving,
        Revived
    }

    //====================[ RMPlayer ]====================
    public class RMPlayer
    {
        //====================[ State ]====================
        public RMState State { get; set; } = RMState.None;
        public RMState LastObservedState { get; set; } = RMState.None;

        //====================[ Derived Flags ]====================
        public bool IsCritical => State is RMState.BleedingOut or RMState.Reviving;
        public bool IsInvulnerable => State == RMState.Revived;

        //====================[ Runtime Flags ]====================
        public bool KillOverride { get; set; }
        // Tracks whether the local revive progress timer is running.
        public bool IsReviveProgressActive => ReviveProgressCoroutine != null;
        public bool IsBeingRevived { get; set; }
        public bool IsSelfReviving { get; set; }
        public bool IsSilentInventoryAnimActive { get; set; }
        public bool IsSilentReviveBlurActive { get; set; }
        // True while RestorePlayerWeapon's async SetFirstAvailableItem callback is in-flight.
        // Causes the weapon-proceed-block patches to pass through so the equip can complete.
        public bool AllowWeaponEquipForReviveAnim { get; set; }

        //====================[ Session Info ]====================
        // 0 = Self, 1 = Team
        public int ReviveRequestedSource { get; set; }
        public string CurrentReviverId { get; set; } = string.Empty;
        // Profile id of the teammate currently dragging this (downed) player; empty when nobody is.
        public string CurrentDraggerId { get; set; } = string.Empty;
        public bool IsBeingDragged => !string.IsNullOrEmpty(CurrentDraggerId);
        // Time.time of the last local change to CurrentDraggerId; a resync from the owner older than the change
        // must not undo it while the change is still in flight.
        public float DragClaimTime { get; set; } = -100f;
        // Shown as a ragdoll. Set on going down (every peer) and while dragged; the owner clears it once they
        // move and its resync clears it everywhere else.
        public bool IsLimp { get; set; }
        // Time.time the owner went limp (owner only), for the minimum limp time.
        public float LimpStartTime { get; set; }
        // Server-authoritative per-raid lives counter, shared across self- and team-revive.
        public int LivesRemaining { get; set; }

        //====================[ Timers ]====================
        public float CriticalTimer { get; set; }
        public float InvulnerabilityTimer { get; set; }
        // True once StartInvulnerabilityPeriod has executed restore-side effects for this cycle.
        public bool HasInitializedInvulnerability { get; set; }
        public float CooldownTimer { get; set; }
        // Countdown until the next periodic state resync broadcast. -1 sends immediately.
        public float ResyncCooldown { get; set; }
        // Watchdog: counts down from a set value when IsBeingRevived is raised from a TeamHelp packet.
        // Countdown for the active team-revive request while State == BleedingOut.
        public float BeingRevivedWatchdogTimer { get; set; }

        //====================[ Stored Values ]====================
        public float OriginalAwareness { get; set; } = -1f;
        public bool HasStoredAwareness { get; set; }
        public EDamageType PlayerDamageType { get; set; } = EDamageType.Undefined;

        //====================[ UI Timers ]====================
        public CustomTimer CriticalStateMainTimer { get; set; }
        public CustomTimer RevivePromptTimer { get; set; }

        //====================[ Coroutine Handles ]====================
        // Stored so ExitDowned / EnterDowned can cancel the revive-progress coroutine.
        public Coroutine ReviveProgressCoroutine { get; set; }
        // Stored so ExitDowned / PrepareForDeath can cancel the heartbeat cue coroutine
        // as soon as the downed state ends (revived or died).
        public Coroutine HeartbeatCoroutine { get; set; }
        // Owned by RevivePresentation so a stop always kills pending inventory-open steps.
        public Coroutine ReviveEffectsCoroutine { get; set; }
        public Coroutine UnconsciousOpenCoroutine { get; set; }

        //====================[ Input Tracking ]====================
        // Self-revive flow state (hold -> auth -> reviving).
        public float SelfReviveHoldTime { get; set; }
        // Hold threshold reached; server authorization is pending.
        public bool SelfReviveAwaitingAuth { get; set; }
        public int SelfReviveAttemptId { get; set; }
        // -1 when not holding the give-up key; otherwise seconds held.
        public float GiveUpHoldTime { get; set; } = -1f;

        //====================[ Revive Flow Markers ]====================
        // Incremented whenever a new downed cycle begins.
        public int ReviveCycleId { get; set; }
        // Set to ReviveCycleId once restore/finalize side effects are committed.
        public int FinalizedReviveCycleId { get; set; } = -1;
        public bool IsReviveFinalizeCommittedForCurrentCycle => FinalizedReviveCycleId == ReviveCycleId;

        //====================[ Shared Reset Helpers ]====================
        // Clears local self-revive input/UI state. Used whenever a packet hands control of the
        // revive to something else (team help, a fresh reviving transition, a full state reset).
        public void ClearSelfReviveInput()
        {
            SelfReviveHoldTime = 0f;
            SelfReviveAwaitingAuth = false;
            IsSelfReviving = false;
        }

        // Clears the active revive-session bookkeeping (who is reviving whom, from where).
        // Used on a fresh BleedingOut transition and on a full PlayerStateReset.
        public void ClearReviveSession()
        {
            IsBeingRevived = false;
            IsSelfReviving = false;
            CurrentReviverId = string.Empty;
            ReviveRequestedSource = 0;
            BeingRevivedWatchdogTimer = 0f;
        }
    }
}
