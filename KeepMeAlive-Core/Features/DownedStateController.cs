//====================[ Imports ]====================
using System;
using System.Collections;
using EFT;
using EFT.UI.Screens;
using UnityEngine;
using KeepMeAlive.Helpers;
using KeepMeAlive.Components;
using KeepMeAlive.Fika;
using KeepMeAlive.Patches;

namespace KeepMeAlive.Features
{
    //====================[ DownedStateController ]====================
    // Slim state-machine coordinator. Domain logic lives in:
    //   BodyInteractableRuntime, DownedHealthAndEffectsManager,
    //   DownedMovementController, PostRevivalController, RevivalController
    internal static class DownedStateController
    {
        private static SyncedGameplayConfig Cfg => SyncedServerConfigStore.Config.Gameplay;

        //====================[ Shared Helpers ]====================
        internal static IEnumerator DelayedActionAfterSeconds(float seconds, Action action)
        {
            yield return new WaitForSeconds(seconds);
            try { action?.Invoke(); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedStateController] delayed action error: {ex.Message}"); }
        }

        internal static void ClearTimers(RMPlayer st)
        {
            st.CriticalStateMainTimer?.Stop(); st.CriticalStateMainTimer = null;
            st.RevivePromptTimer?.Stop(); st.RevivePromptTimer = null;
        }

        internal static void ClearRevivePromptTimer(RMPlayer st)
        {
            st.RevivePromptTimer?.Stop();
            st.RevivePromptTimer = null;
        }

        // Shows the current self-revive requirement or the reason self-revive is unavailable.
        internal static void ShowSelfRevivePromptIfEligible(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;

            if (!RevivePolicy.IsEnabled(ReviveSource.Self))
            {
                VFX_UI.ObjectivePanel(Color.red, PlayerFacingMessages.Revive.WaitForTeammate);
                return;
            }

            if (RevivePolicy.RequiresItem && !ModUtils.HasReviveItem(player))
            {
                VFX_UI.ObjectivePanel(Color.red, PlayerFacingMessages.Revive.DefibrillatorRequired);
                return;
            }

            int cost = Cfg.Revival.SelfReviveLivesCost;
            var st = RMSession.GetPlayerState(player.ProfileId);

            if (st.LivesRemaining < cost)
            {
                VFX_UI.ObjectivePanel(Color.red, PlayerFacingMessages.Revive.NotEnoughLives(cost));
                return;
            }

            KeyCode key = KeepMeAliveSettings.SELF_REVIVAL_KEY.Value;
            string label = $"{PlayerFacingMessages.Revive.SelfReviveCost(cost)}\n{PlayerFacingMessages.NetworkRevive.RevivePrompt(key)}";
            VFX_UI.ObjectivePanel(Color.blue, label);
        }

        private const float InventoryCloseRetrySeconds = 0.5f;
        private static float _nextInventoryCloseAttempt;

        // An inventory or loot screen open when the hit landed would otherwise stay usable while
        // downed (DownedInventoryScreenBlockPatch only stops new ones opening).
        private static void CloseInventoryIfOpen(Player player)
        {
            _nextInventoryCloseAttempt = Time.time + InventoryCloseRetrySeconds;
            try
            {
                if (!EftScreenManager.Instance.CheckCurrentScreen(EEftScreenType.Inventory)) return;
                player.GetComponent<GamePlayerOwner>()?.CloseInventoryIfOpen();
            }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedStateController] CloseInventoryIfOpen failed: {ex.Message}"); }
        }

        private static void HideAllPanelsAndStop(RMPlayer st)
        {
            VFX_UI.HideTransitPanel();
            VFX_UI.HideObjectivePanel();
            ClearTimers(st);
        }

        internal static void CancelReviveState(Player player, RMPlayer st, string message = null, Color? messageColor = null)
        {
            if (st.State == RMState.Reviving)
            {
                Plugin.LogSource.LogInfo($"[SelfReviveTrace] CancelReviveState ignored while already Reviving for {player?.ProfileId}");
                return;
            }

            // Preserve unconscious state when canceling a self-revive attempt that
            // didn't complete — the player is still downed and should stay locked out.
            bool preserveUnconscious = st.State == RMState.BleedingOut
                                       && Cfg.Revival.UnconsciousOnDowned
                                       && st.IsSilentInventoryAnimActive;

            if (!preserveUnconscious)
                RevivePresentation.Stop(player, st, "CancelReviveState");

            st.IsBeingRevived = false;
            st.ClearSelfReviveInput();
            st.AllowWeaponEquipForReviveAnim = false;
            VFX_UI.HideObjectivePanel();
            ClearRevivePromptTimer(st);
            ShowSelfRevivePromptIfEligible(player);

            if (!string.IsNullOrEmpty(message))
            {
                VFX_UI.Text(messageColor ?? Color.yellow, message);
            }
        }

        private static void BeginNewReviveCycle(RMPlayer st)
        {
            st.ReviveCycleId++;
            st.FinalizedReviveCycleId = -1;
            st.HasInitializedInvulnerability = false;
        }

        internal static bool TryCommitReviveFinalizeForCycle(string source, string playerId, RMPlayer st)
        {
            if (st.IsReviveFinalizeCommittedForCurrentCycle)
            {
                Plugin.LogSource.LogInfo($"[ReviveFinalize] Suppressed duplicate finalize ({source}) for {playerId} in cycle {st.ReviveCycleId}");
                return false;
            }

            st.FinalizedReviveCycleId = st.ReviveCycleId;
            return true;
        }

        //====================[ Enter / Exit Downed ]====================
        public static void EnterDowned(Player player, EDamageType damageType)
        {
            if (player == null) return;

            try
            {
                string id = player.ProfileId;
                var st = RMSession.GetPlayerState(id);

                RevivePresentation.Stop(player, st, "EnterDowned_Reset");

                if (st.State is RMState.BleedingOut or RMState.Reviving or RMState.Revived) return;

                if (st.State == RMState.CoolDown)
                {
                    BodyInteractableRuntime.Remove(id);
                    DeathMode.ForceBleedout(player);
                    return;
                }

                st.CooldownTimer = 0f;
                st.KillOverride = false;
                st.PlayerDamageType = damageType;
                RMSession.SetPlayerState(id, RMState.BleedingOut);
                BeginNewReviveCycle(st);
                st.CriticalTimer = Cfg.Revival.CriticalStateSeconds;
                RevivalController.StopReviveProgress(st);
                st.ClearReviveSession();
                st.ClearSelfReviveInput();
                st.IsSilentInventoryAnimActive = false;
                st.IsSilentReviveBlurActive = false;
                st.AllowWeaponEquipForReviveAnim = false;
                st.SelfReviveAttemptId = 0;
                st.GiveUpHoldTime = -1f;
                st.LastObservedState = st.State;

                BodyInteractableRuntime.ForceClosePicker(id);
                if (Cfg.Revival.RestoreVitalsOnDowned)
                    DownedHealthAndEffectsManager.RestoreVitalsToMinimum(player);

                if (Cfg.Protection.EnableGhostMode) GhostMode.EnterGhostModeById(id);
                if (Cfg.Protection.EnableGodMode) GodMode.Enable(player);

                if (player.IsYourPlayer)
                {
                    if (Cfg.Revival.BlockUiWhenDowned) DownedUiBlocker.SetBlocked(true);
                    // LivesRemaining is updated at raid start and after a revive.
                    DownedRagdollController.Begin(st);
                    FikaBridge.SendBleedingOutPacket(id, st.CriticalTimer, st.LivesRemaining);
                    RevivalAuthority.NotifyBeginCritical(id);
                    st.ResyncCooldown = -1f;

                    CloseInventoryIfOpen(player);

                    DownedHealthAndEffectsManager.ApplyCriticalEffects(player);
                    DownedMovementController.ApplyRevivableState(player);
                    if (Cfg.Revival.UnconsciousOnDowned) RevivePresentation.ApplyUnconscious(player);
                    HeartbeatEffect.Start(player, st);
                    DownedScreenEffects.Start(player);
                    ShowCriticalStateUI(player, st);
                }

                Plugin.LogSource.LogInfo($"[Downed] Player {id} entered critical state (local={player.IsYourPlayer})");
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedStateController] EnterDowned error: {ex.Message}"); }
        }

        // True between FikaGameEndedEvent and the next GameWorldStartedEvent; death blocking is
        // disabled during this interval.
        public static bool IsRaidEnding { get; set; }

        // Called from Harmony patches on raid-terminating engine calls (LocalGame.Stop,
        // CoopGame.Extract) and from FikaGameEndedEvent.
        public static void HandleRaidTerminating(string reason)
        {
            try
            {
                var local = ModUtils.GetYourPlayer();
                if (local == null || !RMSession.TryGetPlayerState(local.ProfileId, out var st) || st.State == RMState.None)
                    return;

                IsRaidEnding = true;
                ResetForRaidBoundary(reason);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedStateController] HandleRaidTerminating({reason}) error: {ex.Message}");
            }
        }

        // Clears every piece of per-raid state. Called on raid start and raid end so nothing
        // (input locks, ghost flags, UI, coroutines) can leak from one raid into the next.
        public static void ResetForRaidBoundary(string reason)
        {
            try
            {
                var local = ModUtils.GetYourPlayer();
                if (local != null && RMSession.TryGetPlayerState(local.ProfileId, out var st))
                {
                    RevivePresentation.Stop(local, st, reason);
                    RevivalController.StopReviveProgress(st);
                    HeartbeatEffect.Stop(st);
                    ClearTimers(st);
                }

                TinnitusScope.Clear();
                TinnitusRing.Stop();

                DownedScreenEffects.Stop();
                VFX_UI.HideAll();
                DownedUiBlocker.SetBlocked(false);
                DownedMovementController.ScrubDownedInputLocks();
                DownedDragController.Clear();
                GhostMode.ClearAll();
                BodyInteractableRuntime.Clear();
                DeathMode.ClearCaches();
                TeamMedical.ClearReservations();
                TeamHealUseTime.Clear();
                RMSession.Clear();
                RevivalDebugLog.LogDebug($"[Downed] Raid-boundary reset ({reason})");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedStateController] ResetForRaidBoundary({reason}) error: {ex}");
            }
        }

        public static void PrepareForDeath(Player player, string reason = "PrepareForDeath")
        {
            if (player == null) return;

            try
            {
                var st = RMSession.GetPlayerState(player.ProfileId);

                RevivePresentation.Stop(player, st, reason);
                RevivalController.StopReviveProgress(st);
                HeartbeatEffect.Stop(st);
                if (player.IsYourPlayer)
                {
                    DownedScreenEffects.Stop();
                    TinnitusRing.Stop();
                }

                st.IsBeingRevived = false;
                st.ClearSelfReviveInput();
                st.AllowWeaponEquipForReviveAnim = false;
                st.GiveUpHoldTime = -1f;
                st.CurrentReviverId = string.Empty;

                if (player.IsYourPlayer)
                {
                    HideAllPanelsAndStop(st);
                    DownedUiBlocker.SetBlocked(false);
                    DownedMovementController.ReleaseProne(player);
                    DownedMovementController.ReleaseEmptyHands(player);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedStateController] PrepareForDeath error: {ex.Message}");
            }
        }

        //====================[ Per-Frame Ticks ]====================
        public static void TickDowned(Player player)
        {
            var st = RMSession.GetPlayerState(player.ProfileId);

            if (st.State != st.LastObservedState)
            {
                if (st.State == RMState.Reviving)
                {
                    RevivalController.StartRevive(player, st, "StateTransition");
                }

                st.LastObservedState = st.State;
            }

            // Check this before critical-state cleanup so movement can end the limp state.
            DownedRagdollController.TickLocal(player, st);

            if (!st.IsCritical)
            {
                if (player.IsYourPlayer && DownedUiBlocker.IsBlocked)
                {
                    DownedUiBlocker.SetBlocked(false);
                }
                // Safety net: any exit from the critical state clears the screen effects.
                if (player.IsYourPlayer && DownedScreenEffects.IsActive) DownedScreenEffects.Stop();
                return;
            }

            ReviveDebug.Log("TickDowned_Enter", player.ProfileId, player.IsYourPlayer, $"state={st.State}");

            // While Reviving the transit panel shows revive progress, not bleed-out, so CriticalTimer
            // is left frozen at its value when the revive started (it drives the screen effects).
            if (st.State == RMState.BleedingOut)
            {
                if (st.CriticalStateMainTimer is { IsRunning: true })
                {
                    st.CriticalTimer = (float)st.CriticalStateMainTimer.GetTimeSpan().TotalSeconds;
                }
                else
                {
                    st.CriticalTimer -= Time.deltaTime;
                    if (st.CriticalStateMainTimer == null) TryLazyShowTransitTimer(player, st);
                }
            }

            if (player.IsYourPlayer) DownedScreenEffects.Tick(st);

            // The game's close can be refused (e.g. while another close is still in flight), so keep
            // retrying until the screen is actually gone.
            if (player.IsYourPlayer && Time.time >= _nextInventoryCloseAttempt) CloseInventoryIfOpen(player);

            SelfReviveInput.Tick(player, st);

            if (st.IsBeingRevived && st.State == RMState.BleedingOut && !string.IsNullOrEmpty(st.CurrentReviverId))
            {
                st.BeingRevivedWatchdogTimer -= Time.deltaTime;
                if (st.BeingRevivedWatchdogTimer <= 0f)
                {
                    Plugin.LogSource.LogWarning($"[Downed] Reviver watchdog expired for {player.ProfileId}; clearing IsBeingRevived");
                    st.CurrentReviverId = string.Empty;
                    CancelReviveState(player, st, PlayerFacingMessages.Downed.ReviverTimedOut, Color.yellow);
                }
            }

            bool gaveUp = TickGiveUp(player, st);
            if (st.State == RMState.BleedingOut && !st.IsBeingRevived && !st.IsSelfReviving && (st.CriticalTimer <= 0f || gaveUp))
            {
                st.GiveUpHoldTime = -1f;
                RevivePresentation.Stop(player, st, "BleedoutFallback");
                ClearTimers(st);
                if (player.IsYourPlayer)
                {
                    DownedUiBlocker.SetBlocked(false);
                }
                BodyInteractableRuntime.Remove(player.ProfileId);
                DeathMode.ForceBleedout(player);
            }
        }

        private const float GiveUpHoldSeconds = 2f;

        // Requires a hold to confirm the give-up action.
        private static bool TickGiveUp(Player player, RMPlayer st)
        {
            bool eligible = player.IsYourPlayer && st.State == RMState.BleedingOut && !st.IsBeingRevived && !st.IsSelfReviving;
            if (!eligible)
            {
                // Another flow (team help / self-revive) now owns the objective panel; just drop the hold.
                st.GiveUpHoldTime = -1f;
                return false;
            }

            KeyCode key = KeepMeAliveSettings.GIVE_UP_KEY.Value;

            if (st.GiveUpHoldTime < 0f)
            {
                if (!Input.GetKeyDown(key)) return false;

                st.GiveUpHoldTime = 0f;
                VFX_UI.HideObjectivePanel();
                ClearRevivePromptTimer(st);
                st.RevivePromptTimer = VFX_UI.ObjectivePanel(VFX_UI.Gradient(Color.red, Color.black), PlayerFacingMessages.Downed.GiveUpObjective, GiveUpHoldSeconds);
                return false;
            }

            if (!Input.GetKey(key))
            {
                st.GiveUpHoldTime = -1f;
                VFX_UI.HideObjectivePanel();
                ClearRevivePromptTimer(st);
                ShowSelfRevivePromptIfEligible(player);
                VFX_UI.Text(Color.yellow, PlayerFacingMessages.Downed.GiveUpCanceled);
                return false;
            }

            st.GiveUpHoldTime += Time.deltaTime;
            return st.GiveUpHoldTime >= GiveUpHoldSeconds;
        }

        public static void TickResync(Player player)
        {
            if (!player.IsYourPlayer) return;

            var st = RMSession.GetPlayerState(player.ProfileId);
            if (st.State == RMState.None || (st.ResyncCooldown -= Time.deltaTime) > 0f) return;

            st.ResyncCooldown = 5f;
            FikaBridge.SendPlayerStateResyncPacket(player.ProfileId, st);
        }

        //====================[ UI ]====================
        private static void ShowCriticalStateUI(Player player, RMPlayer st)
        {
            if (!player.IsYourPlayer) return;
            try
            {
                VFX_UI.Text(Color.red, PlayerFacingMessages.Downed.DownedBanner);
                st.CriticalStateMainTimer = VFX_UI.TransitPanel(VFX_UI.Gradient(Color.red, Color.black), PlayerFacingMessages.Downed.BleedingOut(st.LivesRemaining), Cfg.Revival.CriticalStateSeconds);
                ShowSelfRevivePromptIfEligible(player);
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedStateController] ShowCriticalStateUI error: {ex.Message}"); }
        }

        private static void TryLazyShowTransitTimer(Player player, RMPlayer st)
        {
            if (!player.IsYourPlayer) return;
            if (st.CriticalTimer <= 0.5f) return;
            st.CriticalStateMainTimer = VFX_UI.TransitPanel(VFX_UI.Gradient(Color.red, Color.black), PlayerFacingMessages.Downed.BleedingOut(st.LivesRemaining), st.CriticalTimer);
        }
    }
}
