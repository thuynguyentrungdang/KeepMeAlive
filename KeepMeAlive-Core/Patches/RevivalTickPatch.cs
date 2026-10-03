//====================[ Imports ]====================
using System;
using System.Reflection;
using EFT;
using EFT.Communications;
using HarmonyLib;
using KeepMeAlive.Components;
using KeepMeAlive.Features;
using KeepMeAlive.Helpers;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KeepMeAlive.Patches
{
    //====================[ RevivalTickPatch ]====================
    // Advances post-revival timers for tracked players and runs the local downed-state machine
    // and resync from the per-player Player.UpdateTick callback.
    internal class RevivalTickPatch : ModulePatch
    {
        //====================[ Patching / Lifecycle ]====================
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.UpdateTick));

        [PatchPostfix]
        private static void PatchPostfix(Player __instance)
        {
            if (__instance == null || __instance.IsAI) return;

            try
            {
                if (RMSession.TryGetPlayerState(__instance.ProfileId, out var st))
                {
                    PostRevivalController.TickInvulnerability(__instance, st);
                    PostRevivalController.TickCooldown(__instance, st);
                }

                // Update ragdolls on remote copies and move the local body.
                DownedDragController.Tick(__instance);

                if (!__instance.IsYourPlayer) return;

                DownedStateController.TickResync(__instance);
                if (KeepMeAliveSettings.DEBUG_KEYBINDS.Value) CheckTestKeybinds(__instance);
                DownedStateController.TickDowned(__instance);

                // Re-read: TickDowned and the timers above may have changed the state this frame.
                RMSession.TryGetPlayerState(__instance.ProfileId, out var current);
                DownedMovementController.TickSpeedLimit(__instance, current);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error in RevivalTickPatch: {ex.Message}");
            }
        }

        //====================[ Keybinds ]====================
        private static void CheckTestKeybinds(Player player)
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F7)) ToggleGhost(player, true);
                else if (Input.GetKeyDown(KeyCode.F8)) ToggleGhost(player, false);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[TestKeybinds] Error: {ex.Message}");
            }
        }

        private static void ToggleGhost(Player player, bool enter)
        {
            if (enter) GhostMode.EnterGhostModeById(player.ProfileId);
            else GhostMode.ExitGhostModeById(player.ProfileId);

            NotificationManager.DisplayMessageNotification(
                PlayerFacingMessages.GhostMode.State(enter), ENotificationDurationType.Default, ENotificationIconType.Default, Color.cyan);
        }
    }
}
