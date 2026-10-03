//====================[ Imports ]====================
using System;
using EFT;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using KeepMeAlive.Components;
using KeepMeAlive.Fika.Packets;
using KeepMeAlive.Features;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Fika
{
    //====================[ RevivePacketHandlers ]====================
    // Receivers for every core revival-state packet (BleedingOut, TeamHelp/Cancel, SelfRevive/
    // TeamRevive start, Revived, PlayerStateReset, PlayerStateResync). Team healing has its own
    // file (TeamHealPacketHandlers) since it is a separate concern.
    internal static class RevivePacketHandlers
    {
        //====================[ Registration ]====================
        public static void Register(IFikaNetworkManager manager)
        {
            manager.RegisterPacket<BleedingOutPacket, NetPeer>(OnBleedingOut);
            manager.RegisterPacket<TeamHelpPacket, NetPeer>(OnTeamHelp);
            manager.RegisterPacket<TeamCancelPacket, NetPeer>(OnTeamCancel);
            manager.RegisterPacket<SelfReviveStartPacket, NetPeer>(OnSelfReviveStart);
            manager.RegisterPacket<TeamReviveStartPacket, NetPeer>(OnTeamReviveStart);
            manager.RegisterPacket<RevivedPacket, NetPeer>(OnRevived);
            manager.RegisterPacket<PlayerStateResetPacket, NetPeer>(OnPlayerStateReset);
            manager.RegisterPacket<PlayerStateResyncPacket, NetPeer>(OnPlayerStateResync);
        }

        //====================[ Shared Helpers ]====================
        private static bool IsLocalPlayer(string playerId)
        {
            var local = ModUtils.GetYourPlayer();
            return local != null && local.ProfileId == playerId;
        }

        // Wraps player-facing notifications for packet handlers.
        private static void NotifySafe(string tag, Color color, Func<string> buildMessage, string actorId, string targetId)
        {
            try
            {
                string msg = buildMessage();
                PlayerMessageRouter.Notify(color, msg, MessageAudience.InvolvedPlayers, actorId, targetId);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[VFX_UI] {tag} notify failed: {ex.Message}");
            }
        }

        //====================[ BleedingOut ]====================
        private static void OnBleedingOut(BleedingOutPacket packet, NetPeer peer)
        {
            // The local player owns this state; ignore packets addressed to that player.
            if (IsLocalPlayer(packet.playerId))
            {
                RevivalDebugLog.LogNetworkTrace($"[StateTrace:BleedingOutPacket] {packet.playerId}: ignored local-owner packet (timeRemaining={packet.timeRemaining:F2})");
                return;
            }

            var playerState = RMSession.GetPlayerState(packet.playerId);
            // Apply rollback packets only before revival starts.
            if (playerState.State is RMState.Reviving or RMState.Revived)
            {
                RevivalDebugLog.LogNetworkTrace($"[StateTrace:BleedingOutPacket] {packet.playerId}: ignored stale rollback into BleedingOut (current={playerState.State}, timeRemaining={packet.timeRemaining:F2})");
                return;
            }

            var prevState = playerState.State;
            RMSession.SetPlayerState(packet.playerId, RMState.BleedingOut);
            playerState.FinalizedReviveCycleId = -1;
            playerState.CriticalTimer = packet.timeRemaining;
            playerState.LivesRemaining = packet.livesRemaining;
            // Limp until the owner's resync says they moved.
            playerState.IsLimp = true;
            playerState.ClearReviveSession();
            playerState.KillOverride = false;

            RevivalDebugLog.LogStateTrace("BleedingOutPacket", packet.playerId, prevState, playerState.State,
                $"| timer={packet.timeRemaining:F2} reviver='{playerState.CurrentReviverId}' source={playerState.ReviveRequestedSource}");

            // Ghost mode is host-driven (bots only exist on the host); keyed by profileId because
            // observed players have no ActiveHealthController. God mode is applied by the owner.
            if (SyncedServerConfigStore.Config.Gameplay.Protection.EnableGhostMode) GhostMode.EnterGhostModeById(packet.playerId);
        }

        //====================[ TeamHelp ]====================
        private static void OnTeamHelp(TeamHelpPacket packet, NetPeer peer)
        {
            if (!RevivePolicy.IsEnabled(ReviveSource.Team))
            {
                RevivalDebugLog.LogNetworkTrace($"[Packet] TeamHelp ignored (team revive disabled): {packet.reviverId} -> {packet.reviveeId}");
                return;
            }

            RevivalDebugLog.LogNetworkTrace($"[Packet] TeamHelp: {packet.reviverId} started helping {packet.reviveeId}");

            var playerState = RMSession.GetPlayerState(packet.reviveeId);
            if (playerState.State != RMState.BleedingOut)
            {
                RevivalDebugLog.LogNetworkTrace($"[StateTrace:TeamHelpPacket] {packet.reviveeId}: ignored help while state={playerState.State}");
                return;
            }
            // First helper keeps the claim; a second helper overwriting it would let that helper's
            // cancel drop IsBeingRevived while the first is still holding.
            if (playerState.IsBeingRevived && !string.IsNullOrEmpty(playerState.CurrentReviverId)
                && playerState.CurrentReviverId != packet.reviverId)
            {
                RevivalDebugLog.LogNetworkTrace($"[StateTrace:TeamHelpPacket] {packet.reviveeId}: ignored help from {packet.reviverId}; already helped by {playerState.CurrentReviverId}");
                return;
            }
            // Only ever set on the revivee's own machine: their self-revive hold finished and the
            // server is authorizing (and may be consuming the item). That attempt is committed, so
            // the helper's own start will be denied server-side and send a cancel.
            if (playerState.SelfReviveAwaitingAuth)
            {
                RevivalDebugLog.LogNetworkTrace($"[StateTrace:TeamHelpPacket] {packet.reviveeId}: ignored help from {packet.reviverId}; self-revive authorization in progress");
                return;
            }
            playerState.CurrentReviverId = packet.reviverId;
            playerState.IsBeingRevived = true;
            playerState.IsSelfReviving = false;
            // Start watchdog: if the reviver disconnects before ReviveStart arrives, TickDowned will clear IsBeingRevived (#1).
            playerState.BeingRevivedWatchdogTimer = 10f;

            try
            {
                string reviverName = ModUtils.GetPlayerDisplayName(packet.reviverId);
                string reviveeName = ModUtils.GetPlayerDisplayName(packet.reviveeId);
                PlayerMessageRouter.Notify(
                    Color.cyan,
                    PlayerFacingMessages.NetworkRevive.TeamHelpedBy(reviverName, reviveeName),
                    MessageAudience.InvolvedPlayers,
                    packet.reviverId,
                    packet.reviveeId);

                // Replace the local revivee's self-revive prompt with a "being revived" indicator
                Player reviveePlayer = ModUtils.GetPlayerById(packet.reviveeId);
                if (reviveePlayer != null && reviveePlayer.IsYourPlayer)
                {
                    // Clear self-revive inputs when team revival takes control.
                    playerState.ClearSelfReviveInput();

                    VFX_UI.HideObjectivePanel();
                    playerState.RevivePromptTimer?.Stop();
                    playerState.RevivePromptTimer = null;
                    VFX_UI.ObjectivePanel(Color.cyan, PlayerFacingMessages.NetworkRevive.RevivingYou(reviverName));
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[VFX_UI] TeamHelp notify failed: {ex.Message}");
            }
        }

        //====================[ TeamCancel ]====================
        private static void OnTeamCancel(TeamCancelPacket packet, NetPeer peer)
        {
            RevivalDebugLog.LogNetworkTrace($"[Packet] TeamCancel: {packet.reviverId} cancelled helping {packet.reviveeId}");

            var playerState = RMSession.GetPlayerState(packet.reviveeId);
            var prevState = playerState.State;
            // A cancel only releases an active help claim; it never changes state. A late cancel
            // (revivee already died, got up, or is in cooldown) must not drag them back into BleedingOut.
            if (playerState.State != RMState.BleedingOut)
            {
                RevivalDebugLog.LogNetworkTrace($"[StateTrace:TeamCancelPacket] {packet.reviveeId}: cancel from {packet.reviverId} ignored while state={playerState.State}");
                return;
            }
            // Only the recorded helper may release the claim. No claim (never helped, or the local
            // watchdog already cleared it) means there is nothing to cancel - and proceeding would
            // also wipe an in-progress self-revive hold.
            if (playerState.CurrentReviverId != packet.reviverId)
            {
                RevivalDebugLog.LogNetworkTrace($"[StateTrace:TeamCancelPacket] {packet.reviveeId}: cancel from {packet.reviverId} ignored; active helper is {playerState.CurrentReviverId}");
                return;
            }
            playerState.IsBeingRevived = false;
            playerState.IsSelfReviving = false;
            playerState.CurrentReviverId = string.Empty;
            RevivalDebugLog.LogStateTrace("TeamCancelPacket", packet.reviveeId, prevState, playerState.State,
                $"| cancelledBy='{packet.reviverId}'");

            // If we are the revivee, restore the self-revive prompt
            Player reviveePlayer = ModUtils.GetPlayerById(packet.reviveeId);
            if (reviveePlayer != null && reviveePlayer.IsYourPlayer)
            {
                VFX_UI.HideObjectivePanel();
                playerState.RevivePromptTimer?.Stop();
                playerState.RevivePromptTimer = null;

                if (RevivePolicy.IsEnabled(ReviveSource.Self) && (!RevivePolicy.RequiresItem || ModUtils.HasReviveItem(reviveePlayer)))
                {
                    VFX_UI.ObjectivePanel(Color.blue, PlayerFacingMessages.NetworkRevive.RevivePrompt(KeepMeAliveSettings.SELF_REVIVAL_KEY.Value));
                }
            }

            string reviverName = ModUtils.GetPlayerDisplayName(packet.reviverId);
            NotifySafe("TeamCancel", Color.yellow,
                () => PlayerFacingMessages.NetworkRevive.TeamCancelledBy(reviverName),
                packet.reviverId, packet.reviveeId);
        }

        //====================[ SelfReviveStart ]====================
        private static void OnSelfReviveStart(SelfReviveStartPacket packet, NetPeer peer)
        {
            bool isLocal = IsLocalPlayer(packet.playerId);
            ReviveDebug.Log("Packet_SelfReviveStart_Enter", packet.playerId, isLocal, null);

            RevivalDebugLog.LogNetworkTrace($"[Packet] SelfReviveStart: {packet.playerId} started self-revival");

            var playerState = RMSession.GetPlayerState(packet.playerId);
            if (playerState.State == RMState.Revived)
            {
                ReviveDebug.Log("Packet_SelfReviveStart_StaleRevived", packet.playerId, isLocal, null);
                RevivalDebugLog.LogNetworkTrace($"[StateTrace:SelfReviveStartPacket] {packet.playerId}: ignored stale start while already Revived");
                return;
            }

            if (playerState.State == RMState.CoolDown || playerState.State == RMState.None)
            {
                ReviveDebug.Log("Packet_SelfReviveStart_InvalidState", packet.playerId, isLocal, $"state={playerState.State}");
                RevivalDebugLog.LogNetworkTrace($"[StateTrace:SelfReviveStartPacket] {packet.playerId}: ignored start while state={playerState.State}");
                return;
            }

            if (playerState.State == RMState.Reviving && playerState.IsReviveProgressActive)
            {
                // Re-arm the start state for this revive cycle.
                ReviveDebug.Log("Packet_SelfReviveStart_Duplicate", packet.playerId, isLocal, "rearm");
            }

            var prevState = playerState.State;
            ApplyRevivingState(packet.playerId, playerState, ReviveSource.Self, packet.playerId);
            RevivalDebugLog.LogStateTrace("SelfReviveStartPacket", packet.playerId, prevState, playerState.State,
                "| source=Self");
            ReviveDebug.Log("Packet_SelfReviveStart_StateUpdate", packet.playerId, isLocal, $"prevState={prevState}");

            string display = ModUtils.GetPlayerDisplayName(packet.playerId);
            NotifySafe("SelfReviveStart", Color.cyan,
                () => PlayerFacingMessages.NetworkRevive.SelfReviving(display),
                packet.playerId, packet.playerId);
        }

        //====================[ TeamReviveStart ]====================
        private static void OnTeamReviveStart(TeamReviveStartPacket packet, NetPeer peer)
        {
            if (!RevivePolicy.IsEnabled(ReviveSource.Team))
            {
                RevivalDebugLog.LogNetworkTrace($"[Packet] TeamReviveStart ignored (team revive disabled): {packet.reviverId} -> {packet.reviveeId}");
                return;
            }

            RevivalDebugLog.LogNetworkTrace($"[Packet] TeamReviveStart: {packet.reviverId} started reviving {packet.reviveeId}");

            var playerState = RMSession.GetPlayerState(packet.reviveeId);
            if (playerState.State == RMState.Revived)
            {
                RevivalDebugLog.LogNetworkTrace($"[StateTrace:TeamReviveStartPacket] {packet.reviveeId}: ignored stale start while already Revived");
                return;
            }

            if (playerState.State == RMState.CoolDown || playerState.State == RMState.None)
            {
                RevivalDebugLog.LogNetworkTrace($"[StateTrace:TeamReviveStartPacket] {packet.reviveeId}: ignored start while state={playerState.State}");
                return;
            }

            var prevState = playerState.State;
            ApplyRevivingState(packet.reviveeId, playerState, ReviveSource.Team, packet.reviverId);

            RevivalDebugLog.LogStateTrace("TeamReviveStartPacket", packet.reviveeId, prevState, playerState.State,
                $"| source=Team reviver='{packet.reviverId}'");

            string reviverName = ModUtils.GetPlayerDisplayName(packet.reviverId);
            NotifySafe("TeamReviveStart", Color.cyan,
                () => PlayerFacingMessages.NetworkRevive.TeamReviving(reviverName, ModUtils.GetPlayerDisplayName(packet.reviveeId)),
                packet.reviverId, packet.reviveeId);
        }

        // Also called by the local self-reviver, since Fika never echoes its own packet back.
        internal static void ApplyRevivingState(string playerId, RMPlayer playerState, ReviveSource source, string reviverId)
        {
            RMSession.SetPlayerState(playerId, RMState.Reviving);
            playerState.ReviveRequestedSource = (int)source;
            playerState.CurrentReviverId = source == ReviveSource.Self ? string.Empty : (reviverId ?? string.Empty);
            playerState.IsBeingRevived = source == ReviveSource.Team;
            playerState.ClearSelfReviveInput();
            playerState.BeingRevivedWatchdogTimer = 0f;
        }

        //====================[ Revived ]====================
        private static void OnRevived(RevivedPacket packet, NetPeer peer)
        {
            RevivalDebugLog.LogNetworkTrace($"[Packet] Revived: {packet.playerId} was revived by {packet.reviverId}");

            Player player = ModUtils.GetPlayerById(packet.playerId);
            // Keep going without a Player object: returning here would leave this machine's view stuck
            // in Reviving and, on the host, the ghost flag set (bots ignoring a revived player).
            if (player == null)
            {
                Plugin.LogSource.LogWarning($"[Packet] Revived: Player {packet.playerId} not found; applying state only");
            }

            bool isSelfRevive = string.IsNullOrEmpty(packet.reviverId) || packet.reviverId == packet.playerId;
            var playerState = RMSession.GetPlayerState(packet.playerId);
            var prevState = playerState.State;

            RevivalDebugLog.LogStateTrace("RevivedPacket", packet.playerId, prevState, RMState.Revived,
                $"| reviver='{packet.reviverId}' invul={PostReviveEffects.GetInvulnDuration(isSelfRevive ? ReviveSource.Self : ReviveSource.Team):F2}");

            RevivalController.FinalizeRevivalFromPacket(player, packet.playerId, packet.reviverId);

            if (IsLocalPlayer(packet.playerId))
            {
                return;
            }

            string display = ModUtils.GetPlayerDisplayName(packet.playerId);
            var source = isSelfRevive ? ReviveSource.Self : ReviveSource.Team;
            NotifySafe("Revived", Color.green,
                () => PlayerFacingMessages.ReviveComplete.ObserverBySource(display, source),
                packet.reviverId, packet.playerId);
        }

        //====================[ PlayerStateReset ]====================
        private static void OnPlayerStateReset(PlayerStateResetPacket packet, NetPeer peer)
        {
            var playerState = RMSession.GetPlayerState(packet.playerId);
            var prevState = playerState.State;
            playerState.ClearReviveSession();
            playerState.ClearSelfReviveInput();
            playerState.FinalizedReviveCycleId = -1;

            if (packet.isDead)
            {
                RevivalDebugLog.LogNetworkTrace($"[Packet] StateReset: {packet.playerId} died");
                RMSession.SetPlayerState(packet.playerId, RMState.None);
                // Keep the ragdoll pose until Fika's death sync creates the corpse.
                playerState.KillOverride = true;
                playerState.InvulnerabilityTimer = 0f;
                playerState.CriticalTimer = 0f;
            }
            else
            {
                RevivalDebugLog.LogNetworkTrace($"[Packet] StateReset: {packet.playerId} entered cooldown ({packet.cooldownSeconds:F0}s)");
                RMSession.SetPlayerState(packet.playerId, RMState.CoolDown);
                playerState.CooldownTimer = packet.cooldownSeconds;
                playerState.KillOverride = false;
                playerState.InvulnerabilityTimer = 0f;
                playerState.CriticalTimer = 0f;
                playerState.IsLimp = false;
            }

            RevivalDebugLog.LogStateTrace("PlayerStateResetPacket", packet.playerId, prevState, playerState.State,
                $"| isDead={packet.isDead} cooldown={packet.cooldownSeconds:F2}");

            // The owner's own Kill() is synced by Fika; observers only drop the ghost flag here.
            // On death the flag is cleared without re-adding bots (the body is dead anyway).
            if (packet.isDead) GhostMode.ClearGhostFlag(packet.playerId);
            else GhostMode.ExitGhostModeById(packet.playerId);
        }

        //====================[ PlayerStateResync ]====================
        private static void OnPlayerStateResync(PlayerStateResyncPacket packet, NetPeer peer)
        {
            // Preserve local ownership of the local player's state during resync.
            if (IsLocalPlayer(packet.playerId)) return;

            var st       = RMSession.GetPlayerState(packet.playerId);
            var incoming = (RMState)packet.state;
            var prev     = st.State;

            // Apply backwards transitions only when they match the current state.
            if (prev is RMState.Reviving or RMState.Revived)
            {
                bool staleRegression = incoming == RMState.BleedingOut || incoming == RMState.Reviving;
                if (staleRegression && incoming != prev)
                {
                    RevivalDebugLog.LogNetworkTrace($"[StateTrace:ResyncPacket] {packet.playerId}: ignored stale transition {prev} -> {incoming} | crit={packet.criticalTimer:F2} invul={packet.invulTimer:F2} cd={packet.cooldownTimer:F2} reviver='{packet.reviverId}' source={packet.reviveRequestedSource}");
                    return;
                }
            }

            // Refresh timers even when the state is unchanged.
            st.CriticalTimer         = packet.criticalTimer;
            st.InvulnerabilityTimer  = packet.invulTimer;
            st.CooldownTimer         = packet.cooldownTimer;
            st.CurrentReviverId      = packet.reviverId;
            st.ReviveRequestedSource = packet.reviveRequestedSource;
            st.LivesRemaining        = packet.livesRemaining;
            st.IsLimp                = packet.limp;
            DownedDragController.ApplyAuthoritativeDragger(packet.playerId, st, packet.draggerId);

            if (st.State == incoming) return;

            // Remote-only path (local packets are rejected above), so nothing here may touch
            // local input/health; only bookkeeping and the host-side ghost flag.
            RMSession.SetPlayerState(packet.playerId, incoming);
            Player player = ModUtils.GetPlayerById(packet.playerId);

            try
            {
                switch (incoming)
                {
                    case RMState.BleedingOut:
                    case RMState.Reviving:
                        st.KillOverride = false;
                        if (SyncedServerConfigStore.Config.Gameplay.Protection.EnableGhostMode) GhostMode.EnterGhostModeById(packet.playerId);
                        break;

                    case RMState.Revived:
                        PostRevivalController.BeginPostRevival(player, packet.playerId, st, applyFinalize: false);
                        st.InvulnerabilityTimer = packet.invulTimer;
                        break;

                    case RMState.CoolDown:
                    case RMState.None:
                        GhostMode.ExitGhostModeById(packet.playerId);
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[Resync] Apply-state error for {packet.playerId}: {ex.Message}");
            }

            RevivalDebugLog.LogStateTrace("ResyncPacket", packet.playerId, prev, incoming,
                $"| crit={packet.criticalTimer:F2} invul={packet.invulTimer:F2} cd={packet.cooldownTimer:F2} reviver='{packet.reviverId}' source={packet.reviveRequestedSource}");
        }
    }
}
