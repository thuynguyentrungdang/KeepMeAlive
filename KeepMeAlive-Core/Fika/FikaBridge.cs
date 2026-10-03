//====================[ Imports ]====================
using System;
using Comfort.Common;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using KeepMeAlive.Components;
using KeepMeAlive.Fika.Packets;
using KeepMeAlive.Helpers;

namespace KeepMeAlive.Fika
{
    //====================[ FikaBridge ]====================
    // The only outbound Fika API used by the rest of the mod. Every Send* method here logs a
    // network trace and pushes the packet over the wire in one step - there is no separate
    // pass-through layer.
    //
    // Note on `broadcast: true`: on a client this only sends to the host (FikaClient.SendData);
    // the host then relays to every other peer except the original sender (FikaServer.OnNetworkReceive).
    // A sender never receives its own packet back, so every Send* caller is responsible for
    // applying its own state locally before or after calling here - see each call site.
    internal static class FikaBridge
    {
        //====================[ Network Utilities ]====================
        public static bool IAmHost()
        {
            return FikaBackendUtils.IsServer;
        }

        //====================[ Send Helper ]====================
        private static void SendPacket<T>(ref T packet) where T : struct, INetSerializable
        {
            if (!Singleton<IFikaNetworkManager>.Instantiated)
            {
                return;
            }

            try
            {
                bool broadcast = true;
                Singleton<IFikaNetworkManager>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered, broadcast);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError(ex);
            }
        }

        //====================[ Revival Packet Senders ]====================
        public static void SendBleedingOutPacket(string playerId, float timeRemaining, int livesRemaining = 0)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending bleeding out packet for {playerId}");
            BleedingOutPacket packet = new() { playerId = playerId, timeRemaining = timeRemaining, livesRemaining = livesRemaining };
            SendPacket(ref packet);
        }

        public static void SendTeamHelpPacket(string reviveeId, string reviverId)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending team help packet: {reviverId} helping {reviveeId}");
            TeamHelpPacket packet = new() { reviveeId = reviveeId, reviverId = reviverId };
            SendPacket(ref packet);
        }

        public static void SendTeamCancelPacket(string reviveeId, string reviverId)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending team cancel packet: {reviverId} cancelled helping {reviveeId}");
            TeamCancelPacket packet = new() { reviveeId = reviveeId, reviverId = reviverId };
            SendPacket(ref packet);
        }

        public static void SendSelfReviveStartPacket(string playerId)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending self revive start packet for {playerId}");
            SelfReviveStartPacket packet = new() { playerId = playerId };
            SendPacket(ref packet);
        }

        public static void SendTeamReviveStartPacket(string reviveeId, string reviverId)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending team revive start packet: {reviverId} reviving {reviveeId}");
            TeamReviveStartPacket packet = new() { reviveeId = reviveeId, reviverId = reviverId };
            SendPacket(ref packet);
        }

        public static void SendRevivedPacket(string playerId, string reviverId = "")
        {
            RevivalDebugLog.LogNetworkTrace($"Sending revived packet for {playerId}");
            RevivedPacket packet = new() { playerId = playerId, reviverId = reviverId };
            SendPacket(ref packet);
        }

        public static void SendPlayerStateResetPacket(string playerId, bool isDead, float cooldownSeconds = 0f)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending state reset packet for {playerId} (isDead={isDead}, cooldown={cooldownSeconds:F0}s)");
            PlayerStateResetPacket packet = new() { playerId = playerId, isDead = isDead, cooldownSeconds = cooldownSeconds };
            SendPacket(ref packet);
        }

        //====================[ Periodic State Resync ]====================
        // Broadcasts local player's full revival state to all peers. Called periodically while active, and immediately on transitions.
        public static void SendPlayerStateResyncPacket(string playerId, RMPlayer st)
        {
            PlayerStateResyncPacket packet = new()
            {
                playerId              = playerId,
                state                 = (int)st.State,
                criticalTimer         = st.CriticalTimer,
                invulTimer            = st.InvulnerabilityTimer,
                cooldownTimer         = st.CooldownTimer,
                reviverId             = st.CurrentReviverId ?? "",
                reviveRequestedSource = st.ReviveRequestedSource,
                livesRemaining        = st.LivesRemaining,
                draggerId             = st.CurrentDraggerId ?? "",
                limp                  = st.IsLimp
            };
            SendPacket(ref packet);
        }

        //====================[ Drag Packet Senders ]====================
        public static void SendDragStatePacket(string reviveeId, string draggerId, bool active)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending drag packet: {draggerId} {(active ? "dragging" : "released")} {reviveeId}");
            DragStatePacket packet = new() { reviveeId = reviveeId, draggerId = draggerId, active = active };
            SendPacket(ref packet);
        }

        //====================[ Team Healing Packet Senders ]====================
        public static void SendTeamHealPacket(string patientId, string healerId, string itemId)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending team heal packet: {healerId} healing {patientId} with {itemId}");
            TeamHealPacket packet = new() { patientId = patientId, healerId = healerId, itemId = itemId };
            SendPacket(ref packet);
        }

        public static void SendTeamHealResultPacket(string patientId, string healerId, string itemId, bool success, string reason)
        {
            TeamHealResultPacket packet = new() { patientId = patientId, healerId = healerId, itemId = itemId, success = success, reason = reason };
            SendPacket(ref packet);
        }
    }
}
