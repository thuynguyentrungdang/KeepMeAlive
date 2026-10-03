//====================[ Imports ]====================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.HealthSystem;
using EFT.InputSystem;
using KeepMeAlive.Components;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ DownedMovementController ]====================
    internal static class DownedMovementController
    {
        //====================[ Ignored-Command Infrastructure ]====================
        private static HashSet<ECommand> _ignoredCommands;
        private static bool _reflectionFailed;

        //====================[ Stance Commands ]====================
        private static readonly ECommand[] StanceCommands =
        {
            ECommand.ToggleDuck,
            ECommand.ToggleProne,
            ECommand.NextWalkPose,
            ECommand.PreviousWalkPose,
            ECommand.Jump,
            ECommand.RestorePose
        };

        //====================[ Weapon-Select Commands ]====================
        private static readonly ECommand[] WeaponSelectCommands =
        {
            ECommand.SelectFirstPrimaryWeapon,
            ECommand.SelectSecondPrimaryWeapon,
            ECommand.SelectSecondaryWeapon,
            ECommand.SelectKnife,
            ECommand.QuickSelectSecondaryWeapon,
            ECommand.SelectFastSlot4,
            ECommand.SelectFastSlot5,
            ECommand.SelectFastSlot6,
            ECommand.SelectFastSlot7,
            ECommand.SelectFastSlot8,
            ECommand.SelectFastSlot9,
            ECommand.SelectFastSlot0,
            ECommand.QuickKnifeKick,
            // Quick-throw puts a grenade in hand via SetItemInHands, which the firearm Proceed block
            // does not cover.
            ECommand.ThrowGrenade,
            ECommand.PressThrowGrenade
        };

        //====================[ Reflection Accessor ]====================
        private static HashSet<ECommand> GetIgnoredCommands()
        {
            if (_ignoredCommands != null) return _ignoredCommands;
            if (_reflectionFailed) return null;

            try
            {
                var field = typeof(PlayerOwner).GetField("ignoredCommands",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (field == null)
                {
                    Plugin.LogSource.LogError("[DownedMovement] ignoredCommands field not found via reflection");
                    _reflectionFailed = true;
                    return null;
                }
                _ignoredCommands = field.GetValue(null) as HashSet<ECommand>;
                if (_ignoredCommands == null)
                {
                    Plugin.LogSource.LogError("[DownedMovement] ignoredCommands field was null");
                    _reflectionFailed = true;
                }
                return _ignoredCommands;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedMovement] ignoredCommands reflection error: {ex.Message}");
                _reflectionFailed = true;
                return null;
            }
        }

        private static void AddIgnored(ECommand[] commands)
        {
            var set = GetIgnoredCommands();
            if (set == null) return;
            for (int i = 0; i < commands.Length; i++) set.Add(commands[i]);
        }

        private static void RemoveIgnored(ECommand[] commands)
        {
            var set = GetIgnoredCommands();
            if (set == null) return;
            for (int i = 0; i < commands.Length; i++) set.Remove(commands[i]);
        }

        //====================[ Public API ]====================

        // Sets the player prone and blocks stance-changing inputs.
        public static void ForceProne(Player player)
        {
            try
            {
                if (player?.MovementContext == null || !player.IsYourPlayer) return;

                var mc = player.MovementContext;
                mc.EnableSprint(false);

                ApplyPronePose(player, mc);

                AddIgnored(StanceCommands);
                HookProneGuard(player, mc);

                // The setter silently refuses when CanProne is false (e.g. on a pipe or slope).
                if (!IsProne(mc)) StartProneRestore();

                RevivalDebugLog.LogDebug($"[DownedMovement] ForceProne applied for {player.ProfileId}");
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] ForceProne error: {ex.Message}"); }
        }

        // Removes stance-blocking commands; the caller restores the pose if needed.
        public static void ReleaseProne(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;
            try
            {
                UnhookProneGuard();
                RemoveIgnored(StanceCommands);
                RevivalDebugLog.LogDebug($"[DownedMovement] ReleaseProne applied for {player.ProfileId}");
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] ReleaseProne error: {ex.Message}"); }
        }

        //====================[ Prone Guard ]====================
        // Event-driven: pose/state changes queue a restore. The restore only writes when the player is
        // not prone, and falls back to a throttled retry while the game refuses prone (CanProne false).
        private const float ProneRetryInterval = 0.25f;
        private static Player _guardPlayer;
        private static MovementContext _guardContext;
        private static Coroutine _restoreRoutine;

        private static bool IsProne(MovementContext mc) => mc.IsInPronePose && mc.PoseLevel <= 0.001f;

        // Same sequence as the initial force; the Prone() call is skipped when already prone because it toggles.
        private static void ApplyPronePose(Player player, MovementContext mc)
        {
            try
            {
                if (!mc.IsInPronePose) player.CurrentManagedState?.Prone();
            }
            catch { }

            mc.SetPoseLevel(0f, true);
            mc.IsInPronePose = true;
        }

        private static void HookProneGuard(Player player, MovementContext mc)
        {
            if (ReferenceEquals(_guardContext, mc)) return;
            UnhookProneGuard();

            _guardPlayer = player;
            _guardContext = mc;
            mc.OnPoseChanged += OnGuardPoseChanged;
            mc.OnStateChanged += OnGuardStateChanged;
        }

        private static void UnhookProneGuard()
        {
            if (_guardContext != null)
            {
                _guardContext.OnPoseChanged -= OnGuardPoseChanged;
                _guardContext.OnStateChanged -= OnGuardStateChanged;
            }
            _guardContext = null;
            _guardPlayer = null;

            if (_restoreRoutine != null && Plugin.StaticCoroutineRunner != null)
                Plugin.StaticCoroutineRunner.StopCoroutine(_restoreRoutine);
            _restoreRoutine = null;
        }

        private static void OnGuardPoseChanged(int _)
        {
            if (_guardContext != null && !IsProne(_guardContext)) StartProneRestore();
        }

        private static void OnGuardStateChanged(EPlayerState previous, EPlayerState next)
        {
            if (_guardContext != null && !IsProne(_guardContext)) StartProneRestore();
        }

        private static void StartProneRestore()
        {
            if (_restoreRoutine != null || _guardPlayer == null || Plugin.StaticCoroutineRunner == null) return;
            _restoreRoutine = Plugin.StaticCoroutineRunner.StartCoroutine(RestoreProneRoutine());
        }

        // Deferred so we never write inside the game's own pose/state transition.
        private static IEnumerator RestoreProneRoutine()
        {
            yield return null;

            var wait = new WaitForSeconds(ProneRetryInterval);
            while (_guardPlayer != null && _guardContext != null)
            {
                var player = _guardPlayer;
                var mc = _guardContext;

                if (player.MovementContext != mc || !RMSession.TryGetPlayerState(player.ProfileId, out var st) || !st.IsCritical)
                    break;

                if (IsProne(mc)) break;

                try { ApplyPronePose(player, mc); }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogWarning($"[DownedMovement] Prone restore failed: {ex.Message}");
                    break;
                }

                if (IsProne(mc)) break;
                yield return wait; // still refused: retry until CanProne allows it
            }

            _restoreRoutine = null;
        }

        // Empties the player's hands and blocks weapon-select inputs.
        public static void ForceEmptyHands(Player player)
        {
            try
            {
                if (player == null || !player.IsYourPlayer) return;

                player.SetEmptyHands(null);
                AddIgnored(WeaponSelectCommands);

                RevivalDebugLog.LogDebug($"[DownedMovement] ForceEmptyHands applied for {player.ProfileId}");
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] ForceEmptyHands error: {ex.Message}"); }
        }

        // Removes weapon-select blocks; the caller equips a weapon if needed.
        public static void ReleaseEmptyHands(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;
            try
            {
                RemoveIgnored(WeaponSelectCommands);
                RevivalDebugLog.LogDebug($"[DownedMovement] ReleaseEmptyHands applied for {player.ProfileId}");
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] ReleaseEmptyHands error: {ex.Message}"); }
        }

        //====================[ Downed State Application ]====================
        // Full downed-entry orchestration: settle active actions, prone, empty hands, vocalize, release turrets.
        public static void ApplyRevivableState(Player player)
        {
            try
            {
                if (player == null || !player.IsYourPlayer) return;

                CancelLocalManagedActions(player);

                ForceProne(player);
                ForceEmptyHands(player);

                if (player.ShouldVocalizeDeath(player.LastDamagedBodyPart))
                {
                    var trig = player.LastDamageType.IsWeaponInduced() ? EPhraseTrigger.OnDeath : EPhraseTrigger.OnAgony;
                    try { player.Speaker.Play(trig, player.HealthStatus, true, null); } catch { }
                }

                var mc = player.MovementContext;
                if (mc == null) return;

                mc.ReleaseDoorIfInteractingWithOne();

                if (mc.StationaryWeapon != null)
                {
                    mc.StationaryWeapon.Unlock(player.ProfileId);
                    if (mc.StationaryWeapon.Item == player.HandsController.Item)
                    {
                        mc.StationaryWeapon.Show();
                        player.ReleaseHand();
                    }
                }

                if (player.IsYourPlayer && Plugin.StaticCoroutineRunner != null)
                {
                    Plugin.StaticCoroutineRunner.StartCoroutine(ReassertProneNextFrame(player));
                }
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] ApplyRevivableState error: {ex.Message}"); }
        }

        // Restores movement and animation state.
        public static void ReattachMovementHooks(Player player)
        {
            if (player.MovementContext == null) return;
            try
            {
                var mc = player.MovementContext;
                mc.OnStateChanged -= player.StateChangedHandler;
                mc.OnStateChanged += player.StateChangedHandler;
                mc.PhysicalConditionChanged -= player.ProceduralWeaponAnimation.PhysicalConditionUpdated;
                mc.PhysicalConditionChanged += player.ProceduralWeaponAnimation.PhysicalConditionUpdated;
            }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedMovement] Re-hook movement events error: {ex.Message}"); }
        }

        // Ensure leaked local input locks from previous raids are cleared on raid start.
        public static void ScrubDownedInputLocks()
        {
            try
            {
                UnhookProneGuard();
                RemoveIgnored(StanceCommands);
                RemoveIgnored(WeaponSelectCommands);
                // The previous raid's MovementContext is gone; forget the limit it held.
                _appliedSpeedLimit = -1f;
            }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedMovement] ScrubDownedInputLocks error: {ex.Message}"); }
        }

        private static void CancelLocalManagedActions(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;

            try
            {
                bool hasOverride = !ReferenceEquals(player.CurrentManagedState, player.CurrentState);
                player.CurrentManagedState?.Cancel();
                if (hasOverride)
                {
                    player.MovementContext?.ExitOverridenState();
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedMovement] Cancel movement override failed: {ex.Message}");
            }

            try { player.HealthController?.CancelApplyingItem(); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedMovement] CancelApplyingItem failed: {ex.Message}"); }

            try { player.ActiveHealthController?.RemoveMedEffect(); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedMovement] RemoveMedEffect failed: {ex.Message}"); }

            try
            {
                if (player.HandsController is Player.MedsController medsController)
                {
                    medsController.Remove();
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedMovement] MedsController.Remove failed: {ex.Message}");
            }
        }

        private static IEnumerator ReassertProneNextFrame(Player player)
        {
            yield return null;

            if (player?.MovementContext == null || !player.IsYourPlayer) yield break;

            var st = RMSession.GetPlayerState(player.ProfileId);
            if (!st.IsCritical) yield break;

            try
            {
                var mc = player.MovementContext;
                if (!mc.IsInPronePose || mc.PoseLevel > 0.001f)
                {
                    mc.SetPoseLevel(0f, true);
                    mc.IsInPronePose = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedMovement] ReassertProneNextFrame failed: {ex.Message}");
            }
        }

        //====================[ Speed Limit ]====================
        // Use the game's state speed limit, which is not recalculated from carried weight.
        // BarbedWire is cleared on exit, so re-apply the limit periodically.
        private const Player.ESpeedLimit SpeedLimitCause = Player.ESpeedLimit.BarbedWire;
        private const float SpeedLimitReassertInterval = 0.25f;
        private static float _appliedSpeedLimit = -1f; // -1 = no limit applied
        private static float _nextSpeedLimitReassert;

        // Apply downed and post-revive speed limits each tick.
        public static void TickSpeedLimit(Player player, RMPlayer st)
        {
            if (player?.MovementContext == null || !player.IsYourPlayer) return;

            float target = st != null ? GetTargetSpeedLimit(st) : -1f;
            if (target < 0f)
            {
                ClearSpeedLimit(player);
                return;
            }

            if (Mathf.Approximately(target, _appliedSpeedLimit) && Time.time < _nextSpeedLimitReassert) return;

            try
            {
                var mc = player.MovementContext;
                mc.RemoveStateSpeedLimit(SpeedLimitCause);
                mc.AddStateSpeedLimit(target * mc.MaxSpeed, SpeedLimitCause);
                _appliedSpeedLimit = target;
                _nextSpeedLimitReassert = Time.time + SpeedLimitReassertInterval;
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] TickSpeedLimit error: {ex.Message}"); }
        }

        public static void ClearSpeedLimit(Player player)
        {
            if (player == null || !player.IsYourPlayer || _appliedSpeedLimit < 0f) return;
            _appliedSpeedLimit = -1f;

            try { player.MovementContext?.RemoveStateSpeedLimit(SpeedLimitCause); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedMovement] ClearSpeedLimit error: {ex.Message}"); }
        }

        // Fraction of max speed, or -1 for no limit.
        private static float GetTargetSpeedLimit(RMPlayer st)
        {
            switch (st.State)
            {
                case RMState.BleedingOut:
                    bool frozen = st.IsLimp || st.IsBeingRevived || st.IsSelfReviving || st.SelfReviveAwaitingAuth;
                    return frozen ? 0f : Mathf.Clamp01(SyncedServerConfigStore.Config.Gameplay.Revival.DownedMovementSpeedPercent / 100f);
                case RMState.Reviving:
                    return 0f;
                case RMState.Revived:
                    float mult = PostReviveEffects.GetInvulnSpeedMultiplier((ReviveSource)st.ReviveRequestedSource);
                    return mult < 1f ? Mathf.Clamp01(mult) : -1f;
                default:
                    return -1f;
            }
        }
    }
}
