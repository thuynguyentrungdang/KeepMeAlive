//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.UI;
using KeepMeAlive.Components;
using KeepMeAlive.Helpers;

namespace KeepMeAlive.Features
{
    //====================[ BodyInteractableRuntime ]====================
    // Central runtime orchestration for body interactable lifecycle and cache.
    internal static class BodyInteractableRuntime
    {
        //====================[ Cache ]====================
        private static readonly Dictionary<string, BodyInteractable> Cache = new Dictionary<string, BodyInteractable>();

        public static bool HasAny => Cache.Count > 0;

        public static bool Has(string profileId) =>
            !string.IsNullOrEmpty(profileId) && Cache.TryGetValue(profileId, out var bi) && bi != null;

        public static bool TryGet(string profileId, out BodyInteractable interactable)
        {
            interactable = null;
            return !string.IsNullOrEmpty(profileId) && Cache.TryGetValue(profileId, out interactable) && interactable != null;
        }

        //====================[ Public API ]====================
        public static bool TryRouteActions(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState result)
        {
            return TryRouteInteractive(owner, interactive, ref result);
        }

        // Routes actions for BodyInteractable and MedPickerInteractable instances.
        private static bool TryRouteInteractive(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState result)
        {
            if (interactive is BodyInteractable body)
            {
                result = body.GetActions(owner);
                return true;
            }

            if (interactive is MedPickerInteractable picker)
            {
                result = picker.GetActions(owner);
                return true;
            }

            return false;
        }

        //====================[ Lifecycle ]====================
        // Attaches a BodyInteractable to every already-spawned remote human at raid start.
        // Called from FikaRaidLifecycle.OnRaidStarted (skipped entirely on a headless host,
        // which has no human at the keyboard to revive/heal/loot anyone).
        public static void AttachToRemoteHumans()
        {
            if (!global::Fika.Core.Main.Components.CoopHandler.TryGetCoopHandler(out var coopHandler))
            {
                Plugin.LogSource.LogWarning("[AttachToRemoteHumans] CoopHandler not available");
                return;
            }

            RevivalDebugLog.LogDebug($"[AttachToRemoteHumans] HumanPlayers count={coopHandler.HumanPlayers.Count}");
            foreach (var player in coopHandler.HumanPlayers)
            {
                if (player is not global::Fika.Core.Main.Players.ObservedPlayer)
                {
                    RevivalDebugLog.LogDebug($"[AttachToRemoteHumans] SKIP local player {player.Id} ({player.GetType().Name})");
                    continue;
                }

                RevivalDebugLog.LogDebug($"[AttachToRemoteHumans] Attaching to remote player {player.Id} ({player.Profile?.Nickname})");
                AttachToPlayer(player);
            }
        }

        public static void AttachToPlayer(Player player)
        {
            try
            {
                if (player == null || player.gameObject == null)
                {
                    Plugin.LogSource.LogError("AttachToPlayer: Player or transform is null");
                    return;
                }

                BodyInteractable.AttachToPlayer(player);
                Plugin.LogSource.LogInfo($"Initiated BodyInteractable attachment routine for PlayerId {player.Id}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"AttachToPlayer error for player {(player != null ? player.Id.ToString() : "null")}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public static void Register(string profileId, BodyInteractable interactable)
        {
            if (string.IsNullOrEmpty(profileId) || interactable == null) return;
            Cache[profileId] = interactable;
        }

        //====================[ Cleanup ]====================
        public static void Remove(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            Cache.Remove(playerId);
        }

        // Only drops the entry if it still points at this instance (a newer one may have replaced it).
        public static void Remove(string playerId, BodyInteractable instance)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            if (Cache.TryGetValue(playerId, out var cached) && ReferenceEquals(cached, instance))
                Cache.Remove(playerId);
        }

        public static void Clear()
        {
            var live = new List<BodyInteractable>(Cache.Values);
            Cache.Clear();
            foreach (var bi in live)
            {
                if (bi != null) UnityEngine.Object.Destroy(bi.gameObject);
            }
        }

        public static void ForceClosePicker(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;

            try
            {
                if (Cache.TryGetValue(playerId, out var interactable) && interactable != null)
                {
                    interactable.ForceClosePicker();
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[BodyInteractableRuntime] ForceClosePicker error: {ex.Message}");
            }
        }
    }
}
