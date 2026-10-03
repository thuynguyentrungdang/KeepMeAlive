//====================[ Imports ]====================
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using EFT.HealthSystem;
using Fika.Core.Main.Utils;
using KeepMeAlive.Features;
using KeepMeAlive.Fika;
using KeepMeAlive.Helpers;
using KeepMeAlive.Patches;
using System;
using System.Reflection;
using UnityEngine;

namespace KeepMeAlive
{
    //====================[ Plugin ]====================
    [BepInDependency("com.fika.core")]
    [BepInDependency("com.fika.headless", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft dependency so SAIN (if installed) is already in Chainloader.PluginInfos when Awake runs.
    [BepInDependency("me.sol.sain", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin("com.awnova.keepmealive", "KeepMeAlive", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        //====================[ Shared State ]====================
        public static ManualLogSource LogSource;
        public static MonoBehaviour StaticCoroutineRunner;

        // Pass-through to the canonical Fika flag set by FikaHeadlessPlugin.Awake().
        public static bool IAmDedicatedClient => FikaBackendUtils.IsHeadless;
        public static bool SAINInstalled { get; private set; }

        //====================[ Unity Lifecycle ]====================
        private void Awake()
        {
            SAINInstalled = Chainloader.PluginInfos.ContainsKey("me.sol.sain");

            LogSource = Logger;
            StaticCoroutineRunner = this;

            LogAssemblyInfo();
            LogSource.LogInfo("KeepMeAlive plugin loaded!");

            KeepMeAliveSettings.Init(Config);
            SyncedServerConfigStore.Load();

            // Register ReviveItemCooldown effect types into EFT's reflection-based type registries.
            // Must run before any health controllers are instantiated.
            RegisterReviveItemCooldownEffectTypes();

            EnableCorePatches();
            EnableGhostModePatches();
        }

        private void OnEnable() => FikaRaidLifecycle.Init();

        //====================[ Patch Registration ]====================
        private static void EnableCorePatches()
        {
            new RevivalTickPatch().Enable();
            new DeathPatch().Enable();
            new RagdollDeathHandoffPatch().Enable();
            new RaidExtractSafetyPatch().Enable();
            new RaidCoopStopSafetyPatch().Enable();
            new DownedFikaWeaponProceedBlockPatch().Enable();
            new AvailableActionsPatch().Enable();
            new BodyProxyFindInteractablePatch().Enable();
            new DownedInventoryScreenBlockPatch().Enable();
            new DownedApplyItemBlockPatch().Enable();
            new DownedQuickSlotSelectorBlockPatch().Enable();
            new SpecialSlotReviveItemPatch().Enable();
            new FikaOverlayPatch().Enable();
            new FikaHealthBarUpdateHealthPatch().Enable();
            new DownedPlayerLootPatch().Enable();
            new DownedLootGuardPatch().Enable();
            new TeamHealDoMedEffectPatch().Enable();
            new TeamHealUseTimePatch().Enable();
            new SilentInventoryCommandBlockPatch().Enable();
            new TinnitusCapPatch().Enable();
            new DownedBloodApplyVignettePatch().Enable();
            new DownedBloodRenderPatch().Enable();
            new DownedVignetteFlickerPatch().Enable();
        }

        private static void EnableGhostModePatches()
        {
            new GhostModeGroupPatch().Enable();
            new GhostModeMemoryPatch().Enable();
            LogSource.LogInfo("GhostMode patches enabled (BotsGroup.AddEnemy + BotMemory.AddEnemy).");

            if (SAINInstalled)
            {
                new GhostModeSAINPatch().Enable();
                LogSource.LogInfo("GhostMode SAIN patch enabled (EnemyListController.tryAddEnemy).");
            }
        }

        //====================[ Diagnostics ]====================
        private static void LogAssemblyInfo()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                LogSource.LogInfo($"Revival assembly: {asm.GetName().Name} v{asm.GetName().Version}");
            }
            catch (Exception ex)
            {
                LogSource.LogWarning($"Could not retrieve assembly info: {ex.Message}");
            }
        }

        //====================[ ReviveItemCooldown Type Registration ]====================
        // Registers ReviveItemCooldown effect types into EFT's three reflection-based registries
        // so the effect can be serialized/deserialized over Fika's health sync network.
        private static void RegisterReviveItemCooldownEffectTypes()
        {
            try
            {
                LogSource.LogInfo("[Plugin] Registering ReviveItemCooldown effect types...");
                RegisterEffectSenderType(typeof(ReviveItemCooldownEffect));
                RegisterEffectReceiverType(senderType: typeof(ReviveItemCooldownEffect), receiverType: typeof(ReviveItemCooldownNetworkEffect));
                HealthHelper._effectNames[typeof(IReviveItemCooldown)] = "ReviveItemCooldown";

                // Verify
                var dict0 = (Dictionary<string, byte>)AccessTools.Field(typeof(HealthHelper.EffectTypeCode), "_typeToByte").GetValue(null);
                var closedType = typeof(HealthHelper.EffectActivator<>).MakeGenericType(typeof(NetworkHealthController));
                var recvDict = (Dictionary<string, Func<object>>)AccessTools.Field(closedType, "_constructors").GetValue(null);
                LogSource.LogInfo(
                    $"[Plugin] ReviveItemCooldown registered - sender: {dict0.ContainsKey(nameof(ReviveItemCooldownEffect))}, " +
                    $"receiver: {recvDict.ContainsKey(nameof(ReviveItemCooldownEffect))}, " +
                    $"interface: {HealthHelper._effectNames.ContainsKey(typeof(IReviveItemCooldown))}");
            }
            catch (Exception ex)
            {
                LogSource.LogError($"[Plugin] ReviveItemCooldown type registration failed: {ex}");
            }
        }

        // The effect byte travels over Fika's health sync, so every peer must map it to the same
        // effect. A fixed reserved byte (instead of "highest + 1") keeps that mapping identical
        // regardless of which other mods register effects or in what order they load.
        private const byte ReservedEffectByte = 230;

        // HealthHelper.EffectTypeCode: Don't modify _effectTypes (it contains only nested types from ActiveHealthController).
        // Only add to the dictionaries - receiver side has explicit factories via RegisterEffectReceiverType.
        private static void RegisterEffectSenderType(Type effectType)
        {
            var typeCode = typeof(HealthHelper.EffectTypeCode);
            var typeToByte = (Dictionary<string, byte>)AccessTools.Field(typeCode, "_typeToByte").GetValue(null);
            var byteToType = (Dictionary<byte, string>)AccessTools.Field(typeCode, "_byteToType").GetValue(null);

            if (typeToByte.ContainsKey(effectType.Name))
                return;

            // Deterministic fallback: walk down from the reserved byte to the first free slot.
            int index = ReservedEffectByte;
            while (index > 0 && byteToType.ContainsKey((byte)index)) index--;
            if (index <= 0 || index <= byteToType.Keys.Max())
            {
                LogSource.LogError($"[Plugin] No free effect byte for {effectType.Name}; cooldown icon will not sync to teammates.");
                return;
            }

            if (index != ReservedEffectByte)
                LogSource.LogWarning($"[Plugin] Effect byte {ReservedEffectByte} is taken by '{byteToType[ReservedEffectByte]}'; using {index}. All players must run the same mod set.");

            typeToByte[effectType.Name] = (byte)index;
            byteToType[(byte)index] = effectType.Name;
        }

        // HealthHelper.EffectActivator<NetworkHealthController>._constructors: name -> factory.
        // Key must be the SENDER type's name because HealthHelper.EffectTypeCode resolves byte->senderName,
        // then HealthHelper.EffectActivator looks up that name to instantiate the receiver.
        private static void RegisterEffectReceiverType(Type senderType, Type receiverType)
        {
            var closedType = typeof(HealthHelper.EffectActivator<>).MakeGenericType(typeof(NetworkHealthController));
            var dict0 = (Dictionary<string, Func<object>>)AccessTools.Field(closedType, "_constructors").GetValue(null);
            dict0[senderType.Name] = () => Activator.CreateInstance(receiverType);
        }
    }
}
