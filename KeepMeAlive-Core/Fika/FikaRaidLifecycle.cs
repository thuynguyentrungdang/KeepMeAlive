//====================[ Imports ]====================
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using KeepMeAlive.Features;
using KeepMeAlive.Helpers;

namespace KeepMeAlive.Fika
{
    //====================[ FikaRaidLifecycle ]====================
    // Owns the raid lifecycle: plugin-enable subscription, network-manager creation (packet
    // registration), and the raid-started/game-world-started/game-ended events. Per-raid setup
    // that isn't about the network itself (lives fetch, UI warmup) lives in RaidStartupTasks;
    // body-interactable attachment lives in BodyInteractableRuntime.
    internal static class FikaRaidLifecycle
    {
        //====================[ Init ]====================
        // Event subscriptions are installed once for the plugin lifetime.
        private static bool _initialized;

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnFikaNetManagerCreated);
            FikaEventDispatcher.SubscribeEvent<GameWorldStartedEvent>(OnGameWorldStarted);
            FikaEventDispatcher.SubscribeEvent<FikaRaidStartedEvent>(OnRaidStarted);
            FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(OnGameEnded);

            Plugin.LogSource.LogInfo("Fika integration initialized!");
        }

        //====================[ Network Manager Created — Packet Registration ]====================
        private static void OnFikaNetManagerCreated(FikaNetworkManagerCreatedEvent managerCreatedEvent)
        {
            RevivePacketHandlers.Register(managerCreatedEvent.Manager);
            TeamHealPacketHandlers.Register(managerCreatedEvent.Manager);
            DragPacketHandlers.Register(managerCreatedEvent.Manager);
        }

        //====================[ Raid Started — Local Setup + Attach Body Interactables ]====================
        private static void OnRaidStarted(FikaRaidStartedEvent e)
        {
            SyncedServerConfigStore.EnsureLoaded("raid-started");
            RevivalDebugLog.LogDebug($"[OnRaidStarted] IsScav={FikaBackendUtils.IsScav}");

            // A headless host has no human at the keyboard to revive/heal/loot anyone.
            if (FikaBackendUtils.IsHeadless) return;

            var localPlayer = ModUtils.GetYourPlayer();
            if (localPlayer != null)
            {
                RaidStartupTasks.OnLocalRaidStart(localPlayer);
            }

            BodyInteractableRuntime.AttachToRemoteHumans();
        }

        // GameWorldStartedEvent fires before players spawn; clearing here guarantees a clean
        // slate even if the previous raid ended without FikaGameEndedEvent (crash/disconnect).
        private static void OnGameWorldStarted(GameWorldStartedEvent e)
        {
            DownedStateController.IsRaidEnding = false;
            DownedStateController.ResetForRaidBoundary("game-world-started");
        }

        private static void OnGameEnded(FikaGameEndedEvent e)
        {
            DownedStateController.IsRaidEnding = true;
            DownedStateController.ResetForRaidBoundary("game-ended");
        }
    }
}
