//====================[ Imports ]====================
using EFT;
using KeepMeAlive.Components;
using UnityEngine;

namespace KeepMeAlive.Helpers
{
    //====================[ PlayerFacingMessages ]====================
    // Centralized catalog for player-visible text.
    internal static class PlayerFacingMessages
    {
        private static string LifeWord(int count) => count == 1 ? "life" : "lives";

        //====================[ Interactions ]====================
        internal static class Interaction
        {
            public const string TeamReviveDisabled = "Team revive is disabled";
            public const string CannotReviveWhileMoving = "You can't revive a player while moving";
            public const string RevivingObjective = "Reviving {0:F1}";
            public const string ReviveAction = "Revive";
            public const string ReviveNoLongerPossible = "Revive no longer possible";
            public const string ReviveCancelled = "Revive cancelled!";

            public const string MedicBleeds = "Medic Bleeds";
            public const string MedicBreaks = "Medic Breaks";
            public const string MedicHealth = "Medic Health";
            public const string MedicComfort = "Medic Comfort";
            public const string MedicNutrition = "Medic Nutrition";

            public const string MedPickerName = "Med Picker";
            public const string CancelAction = "Cancel";
            public const string SearchAction = "Search";
            public const string DragAction = "Drag";
            public const string ReleaseAction = "Release";
            public const string DragHint = "Dragging teammate - stand up, draw a weapon or turn away to let go";
            public const string DragReleased = "Released teammate";
            public const string LootProtectedItem = "Equipped gear can't be taken from a downed teammate";
        }

        //====================[ Team Heal ]====================
        internal static class TeamHeal
        {
            public const string CannotHealWhileMoving = "You can't heal while moving";
            public const string HealingObjective = "Healing teammate {0:F1}";
            public const string HealingCancelled = "Healing cancelled!";
            public const string PatientUnavailable = "Patient is no longer available";
            public const string HealingTeammate = "Healing teammate...";
            public const string YouWereHealed = "You were healed!";
            public const string HealSucceeded = "Teammate healed";
            public const string HealNoResponse = "Heal not confirmed by teammate";
            public const string ItemUnavailable = "Med item no longer available";
            public const string PatientBusy = "Teammate could not use the item";

            public static string StartedBy(string healerDisplay, string patientDisplay) =>
                $"{healerDisplay} is healing {patientDisplay}";
        }

        //====================[ Revive Flow ]====================
        internal static class Revive
        {
            public const string NoReviveItemFound = "No revive item found! Unable to revive!";
            public const string HoldObjective = "Hold {0:F1}";
            public const string RevivingProgress = "REVIVING";
            public const string SelfReviveCanceled = "Self-revive canceled";
            public const string MissingItemCanceled = "Revive Item missing. Self-revive canceled.";
            public const string DefibrillatorRequired = "Defibrillator required to self-revive";
            public const string WaitForTeammate = "Wait for a teammate to revive you";

            public static string SelfReviveCost(int cost) =>
                $"Self-revive costs {cost} {LifeWord(cost)}";

            public static string NotEnoughLives(int cost) =>
                $"Not enough lives to self-revive (Costs {cost})";
        }

        //====================[ Revive Complete ]====================
        internal static class ReviveComplete
        {
            public const string LocalSelf = "Revive Item used successfully! You are temporarily invulnerable.";
            public const string LocalTeam = "Revived by teammate! You are temporarily invulnerable.";

            public static string ObserverBySource(string playerDisplay, ReviveSource source)
            {
                return source == ReviveSource.Self
                    ? $"{playerDisplay} self-revived"
                    : $"{playerDisplay} was revived";
            }
        }

        //====================[ Revive Denials ]====================
        internal static class ReviveDenied
        {
            public const string AuthorizationFailed = "Revive authorization failed";
            public const string TeamFallback = "Revive denied";
            public const string SelfFallback = "Revive denied by server";
            public const string Cooldown = "Player on cooldown";
            public const string OutOfLives = "Out of lives - can't be revived again";
        }

        //====================[ Network Revive ]====================
        internal static class NetworkRevive
        {
            public static string TeamHelpedBy(string reviverName, string reviveeName) =>
                $"{reviverName} is helping {reviveeName}";

            public static string RevivingYou(string reviverName) =>
                $"{reviverName} is reviving you...";

            public static string TeamCancelledBy(string reviverName) =>
                $"{reviverName} cancelled revival";

            public static string SelfReviving(string display) =>
                $"{display} is self-reviving";

            public static string TeamReviving(string reviverName, string reviveeName) =>
                $"{reviverName} is reviving {reviveeName}";

            public static string RevivePrompt(KeyCode key) =>
                $"Revive! [{key}]";
        }

        //====================[ Downed UI ]====================
        internal static class Downed
        {
            public const string DownedBanner = "DOWNED";
            public const string ReviverTimedOut = "Reviver disconnected or timed out.";
            public const string GiveUpObjective = "Giving up {0:F1}";
            public const string GiveUpCanceled = "Give up canceled";

            // Baked into the persistent middle-screen countdown panel so lives stay
            // visible for the player's entire time downed (see DownedStateController).
            // Only the lives-left line is centered (rich text tag) - the countdown line
            // stays left-aligned since it already fills the box width and would overflow
            // past the icon/edges if centered too.
            public static string BleedingOut(int livesRemaining) =>
                $"<align=\"center\">{livesRemaining} {LifeWord(livesRemaining)} left</align>\nBLEEDING OUT";
        }

        //====================[ Post-Revive ]====================
        internal static class PostRevive
        {
            public const string CooldownEnded = "Revival cooldown ended - you can now be revived";
            public const string InvulnerableObjective = "Invulnerable {0:F1}";

            public static string InvulnerabilityEnded(float cooldownSeconds) =>
                $"Invulnerability ended. Revival cooldown: {cooldownSeconds:F0}s";
        }

        //====================[ Death ]====================
        internal static class Death
        {
            public const string HeadshotCritical = "Headshot – critical";
            public const string HeadshotKilled = "Headshot – killed instantly";
            public const string YouDied = "You died";
        }

        //====================[ Ghost Mode ]====================
        internal static class GhostMode
        {
            public static string State(bool entered) =>
                $"GhostMode: {(entered ? "Entered (F7)" : "Exited (F8)")}";
        }
    }
}
