//====================[ Imports ]====================
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using KeepMeAlive.Components;
using KeepMeAlive.Fika.Packets;
using KeepMeAlive.Features;
using KeepMeAlive.Helpers;

namespace KeepMeAlive.Fika
{
    //====================[ DragPacketHandlers ]====================
    // Receives drag claims and updates the shared player state.
    internal static class DragPacketHandlers
    {
        public static void Register(IFikaNetworkManager manager)
        {
            manager.RegisterPacket<DragStatePacket, NetPeer>(OnDragState);
        }

        private static void OnDragState(DragStatePacket packet, NetPeer peer)
        {
            bool applied = DownedDragController.ApplyDragState(packet.reviveeId, packet.draggerId, packet.active);
            RevivalDebugLog.LogNetworkTrace(
                $"[Packet] Drag: {packet.draggerId} {(packet.active ? "started dragging" : "released")} {packet.reviveeId} (applied={applied})");

            // Resync immediately so competing drag claims settle quickly.
            var local = ModUtils.GetYourPlayer();
            if (local != null && local.ProfileId == packet.reviveeId)
                RMSession.GetPlayerState(packet.reviveeId).ResyncCooldown = -1f;
        }
    }
}
