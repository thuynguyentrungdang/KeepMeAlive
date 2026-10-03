//====================[ Imports ]====================
using EFT;
using KeepMeAlive.Components;
using KeepMeAlive.Fika;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ DownedDragController ]====================
    // Handles drag claims, release conditions, and movement of downed players.
    internal static class DownedDragController
    {
        //====================[ Tuning ]====================
        // How far behind the dragger the body's head/shoulders trail (m).
        private const float TrailDistance = 1.2f;
        // How fast the body swings to line up with the pull (deg/s).
        private const float TurnSpeed = 140f;
        // Follow strength (1/s) and maximum catch-up speed (m/s).
        private const float FollowGain = 12f;
        private const float MaxCatchUpSpeed = 14f;
        private const float FollowDeadZone = 0.03f;
        // Closer than this the pull direction is too unstable to steer by (dragger standing over the body).
        private const float MinTurnDistance = 0.5f;
        private const float MinTurnStepDegrees = 0.01f;
        // Ignore resyncs that may predate a recent local claim change.
        private const float ClaimSettleTime = 2f;
        // Past this the dragger lets go (teleport, a wall snag, or the body being left behind).
        private const float MaxLeashDistance = 7f;
        // Maximum angle away from the body before releasing.
        private const float MaxTurnAwayDegrees = 50f;
        // Wait for the crouch and hands changes before checking the drag stance.
        private const float StanceSettleTimeout = 2f;
        // The game's own line between crouching and standing (Player.TogglePose).
        private const float StandingPoseLevel = 0.5f;
        private const float StartRange = 3f;
        // Keeps the character controller grounded while the body is pulled.
        private const float GroundStickSpeed = 2f;
        // Root-to-chest distance of a remote dragged body (roughly a prone body's).
        private const float RemotePullOffset = 0.5f;

        //====================[ Local State ]====================
        // Whom the local player is dragging (null when nobody).
        private static string _draggingId;
        private static float _dragStartTime;
        private static bool _crouchSettled;
        private static bool _handsSettled;

        public static bool IsLocalDragging => !string.IsNullOrEmpty(_draggingId);

        //====================[ Eligibility ]====================
        public static bool CanStartDrag(Player dragger, Player revivee, RMPlayer reviveeState)
        {
            if (IsLocalDragging) return false;
            if (reviveeState.State != RMState.BleedingOut) return false;
            if (reviveeState.IsBeingDragged || reviveeState.IsBeingRevived || reviveeState.IsSelfReviving || reviveeState.SelfReviveAwaitingAuth) return false;
            if (RMSession.IsPlayerCritical(dragger.ProfileId)) return false;
            // Dragging is done crouched; going prone is one of the ways to let go.
            if (dragger.MovementContext.IsInPronePose) return false;
            return (dragger.Position - revivee.Position).sqrMagnitude <= StartRange * StartRange;
        }

        //====================[ Shared State Mutation ]====================
        // Applies a drag start or release when the state allows it.
        public static bool ApplyDragState(string reviveeId, string draggerId, bool active)
        {
            if (string.IsNullOrEmpty(reviveeId) || string.IsNullOrEmpty(draggerId)) return false;
            if (active && reviveeId == draggerId) return false;

            var st = RMSession.GetPlayerState(reviveeId);

            if (!active)
            {
                if (st.CurrentDraggerId != draggerId) return false;
                SetDragger(st, string.Empty);
                return true;
            }

            if (st.State != RMState.BleedingOut) return false;
            if (st.IsBeingRevived || st.IsSelfReviving || st.SelfReviveAwaitingAuth) return false;
            // First dragger keeps the claim; the revivee's own resync settles any claims that crossed.
            if (st.IsBeingDragged && st.CurrentDraggerId != draggerId) return false;

            SetDragger(st, draggerId);
            return true;
        }

        // Applies the revivee owner's latest drag state unless a newer local change exists.
        public static void ApplyAuthoritativeDragger(string reviveeId, RMPlayer st, string draggerId)
        {
            if (st == null || string.IsNullOrEmpty(reviveeId)) return;
            draggerId ??= string.Empty;
            if (st.CurrentDraggerId == draggerId) return;
            if (Time.time - st.DragClaimTime < ClaimSettleTime) return;

            // Resend a release if the owner still thinks this player is dragging.
            var local = ModUtils.GetYourPlayer();
            if (local != null && draggerId == local.ProfileId && !IsLocalDragging)
            {
                FikaBridge.SendDragStatePacket(reviveeId, draggerId, false);
                return;
            }

            RevivalDebugLog.LogNetworkTrace($"[Drag] resync: dragger of {reviveeId} '{st.CurrentDraggerId}' -> '{draggerId}'");
            SetDragger(st, draggerId);
        }

        // Every change to the claim goes through here so its time is recorded.
        private static void SetDragger(RMPlayer st, string draggerId)
        {
            st.CurrentDraggerId = draggerId ?? string.Empty;
            st.DragClaimTime = Time.time;
        }

        //====================[ Pull Geometry ]====================
        // Position the body behind the dragger without pushing it when they move closer.
        private static Vector3 TrailPoint(Vector3 draggerPosition, Vector3 chest)
        {
            Vector3 toBody = chest - draggerPosition;
            toBody.y = 0f;
            float distance = toBody.magnitude;
            if (distance <= TrailDistance || distance < 1e-3f) return chest;

            Vector3 held = draggerPosition + toBody / distance * TrailDistance;
            held.y = chest.y;
            return held;
        }

        // Heading toward the dragger, or null when they are too close.
        private static float? PullYaw(Vector3 chest, Vector3 draggerPosition)
        {
            Vector3 toDragger = draggerPosition - chest;
            toDragger.y = 0f;
            if (toDragger.sqrMagnitude < MinTurnDistance * MinTurnDistance) return null;
            return Mathf.Atan2(toDragger.x, toDragger.z) * Mathf.Rad2Deg;
        }

        //====================[ Dragger Actions ]====================
        public static void StartDrag(GamePlayerOwner owner, Player revivee)
        {
            var dragger = owner.Player;
            // The menu action can run after the revivee has left the raid.
            if (revivee == null) return;

            var st = RMSession.GetPlayerState(revivee.ProfileId);
            if (!CanStartDrag(dragger, revivee, st)) return;
            if (!ApplyDragState(revivee.ProfileId, dragger.ProfileId, true)) return;

            _draggingId = revivee.ProfileId;
            FikaBridge.SendDragStatePacket(revivee.ProfileId, dragger.ProfileId, true);
            EnterDragStance(dragger);

            VFX_UI.Text(Color.cyan, PlayerFacingMessages.Interaction.DragHint);
            RevivalDebugLog.LogDebug($"[Drag] {dragger.ProfileId} started dragging {revivee.ProfileId}");
        }

        // Dragger-initiated release (key, distance, own downing). Broadcasts to everyone.
        public static void StopDrag(Player dragger, string reason)
        {
            if (!IsLocalDragging) return;

            string reviveeId = _draggingId;
            ApplyDragState(reviveeId, dragger.ProfileId, false);
            FikaBridge.SendDragStatePacket(reviveeId, dragger.ProfileId, false);

            EndLocalDraggerSession();
            RevivalDebugLog.LogDebug($"[Drag] released {reviveeId} ({reason})");
        }

        // Crouch and stow the weapon. Either action can be undone to release.
        private static void EnterDragStance(Player dragger)
        {
            var mc = dragger.MovementContext;
            if (mc.PoseLevel > 0f) dragger.CurrentManagedState.ChangePose(-mc.PoseLevel);
            if (!IsEmptyHanded(dragger)) dragger.SetEmptyHands(null);

            _dragStartTime = Time.time;
            _crouchSettled = false;
            _handsSettled = false;
        }

        private static bool IsEmptyHanded(Player player) => player.HandsController is Player.EmptyHandsController;

        // Returns the release reason when the drag stance is broken.
        private static string BrokenStance(Player dragger, Player revivee)
        {
            var mc = dragger.MovementContext;
            bool settling = Time.time - _dragStartTime < StanceSettleTimeout;

            // Stood up (sprinting does that too) or went prone.
            bool crouched = !mc.IsInPronePose && mc.PoseLevel < StandingPoseLevel;
            if (!_crouchSettled) _crouchSettled = crouched || !settling;
            if (_crouchSettled && !crouched) return "stance";

            // Took anything into their hands: weapon, grenade, knife, meds.
            bool empty = IsEmptyHanded(dragger);
            if (!_handsSettled) _handsSettled = empty || !settling;
            if (_handsSettled && !empty) return "hands";

            Vector3 toBody = revivee.Position - dragger.Position;
            toBody.y = 0f;
            if (toBody.sqrMagnitude > TrailDistance * TrailDistance)
            {
                float bearing = Mathf.Atan2(toBody.x, toBody.z) * Mathf.Rad2Deg;
                float off = Mathf.Abs(Mathf.DeltaAngle(mc.Rotation.x, bearing));
                if (Mathf.Min(off, 180f - off) > MaxTurnAwayDegrees) return "turned";
            }

            return null;
        }

        // Local cleanup only; the shared state is cleared by the caller.
        private static void EndLocalDraggerSession()
        {
            _draggingId = null;
        }

        // Hard reset for raid boundaries; nothing is broadcast because the session is gone.
        public static void Clear()
        {
            EndLocalDraggerSession();
            BodyRagdoll.ClearAll();
        }

        //====================[ Per-Player Tick ]====================
        // Runs for every non-AI player from Player.UpdateTick (see RevivalTickPatch).
        public static void Tick(Player player)
        {
            RMSession.TryGetPlayerState(player.ProfileId, out var st);

            if (st != null && st.IsBeingDragged) ValidateDrag(player, st);

            bool dragged = st != null && st.IsBeingDragged && st.State == RMState.BleedingOut;
            Player dragger = dragged ? ModUtils.GetPlayerById(st.CurrentDraggerId) : null;

            if (!player.IsYourPlayer)
            {
                TickRemoteBody(player, st, dragged, dragger);
                return;
            }

            if (dragged) TickDragged(player, st, dragger);
            TickDragger(player);
        }

        // Drops a drag that can no longer hold. Every peer reaches the same conclusion from replicated state.
        private static void ValidateDrag(Player player, RMPlayer st)
        {
            bool ended = st.State != RMState.BleedingOut || st.IsBeingRevived;
            bool selfReviving = st.IsSelfReviving || st.SelfReviveAwaitingAuth;
            if (!ended && !selfReviving) return;

            string draggerId = st.CurrentDraggerId;
            SetDragger(st, string.Empty);

            // Only the owner of the revivee announces a self-revive release; everything else is
            // already derivable by every peer.
            if (selfReviving && player.IsYourPlayer)
                FikaBridge.SendDragStatePacket(player.ProfileId, draggerId, false);
        }

        //====================[ Observers: Remote Copy ]====================
        // Updates the remote ragdoll from Fika's synced position and the current drag state.
        private static void TickRemoteBody(Player player, RMPlayer st, bool dragged, Player dragger)
        {
            Vector3? hold = null;
            if (dragged && dragger != null)
            {
                Vector3 root = player.Position;
                Vector3 toDragger = dragger.Position - root;
                toDragger.y = 0f;
                float distance = toDragger.magnitude;
                hold = distance > 1e-3f ? root + toDragger / distance * Mathf.Min(RemotePullOffset, distance) : root;
            }

            // Dragged as well as limp: the owner's limp flag can arrive a resync after the drag claim.
            BodyRagdoll.Sync(player, dragged || DownedRagdollController.WantsRagdoll(st), hold);
        }

        //====================[ Revivee: Move Real Position ]====================
        private static void TickDragged(Player revivee, RMPlayer st, Player dragger)
        {
            var mc = revivee.MovementContext;

            if (dragger == null || !dragger.HealthController.IsAlive)
            {
                // Dragger left or died: only the body's owner announces it so exactly one release is sent.
                string draggerId = st.CurrentDraggerId;
                SetDragger(st, string.Empty);
                FikaBridge.SendDragStatePacket(revivee.ProfileId, draggerId, false);
                return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 root = revivee.Position;
            float yaw = mc.TransformRotation.eulerAngles.y;
            float pullOffset = MeasurePullOffset(revivee, root, yaw);
            Vector3 chest = root + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * pullOffset;

            Vector3 trail = TrailPoint(dragger.Position, chest);

            // Turn the body's head toward the dragger while keeping the chest in place.
            Vector3 motion = Vector3.zero;
            if (PullYaw(trail, dragger.Position) is float targetYaw)
            {
                float newYaw = Mathf.MoveTowardsAngle(yaw, targetYaw, TurnSpeed * dt);
                float delta = Mathf.DeltaAngle(yaw, newYaw);

                if (Mathf.Abs(delta) > MinTurnStepDegrees && CanRotateProne(revivee, mc, delta))
                {
                    RotateBody(mc, delta);
                    Vector3 chestAfter = root + Quaternion.Euler(0f, newYaw, 0f) * Vector3.forward * pullOffset;
                    motion = chest - chestAfter;
                    motion.y = 0f;
                }
            }

            // Move the chest toward the trail point with proportional catch-up.
            Vector3 gap = trail - chest;
            gap.y = 0f;
            float distance = gap.magnitude;
            if (distance > FollowDeadZone)
            {
                float step = Mathf.Min(distance * Mathf.Min(1f, FollowGain * dt), MaxCatchUpSpeed * dt);
                motion += gap / distance * step;
            }

            if (motion.sqrMagnitude < 1e-8f) return;
            motion.y = -GroundStickSpeed * dt;

            var cc = mc.CharacterController;
            float savedLimit = cc.SpeedLimit;
            try
            {
                cc.SpeedLimit = -1f;
                mc.DirectApplyMotion(motion, dt);
            }
            finally
            {
                cc.SpeedLimit = savedLimit;
            }
        }

        // The game's own check for turning while prone (would the body swing into a wall or off a ledge).
        private static bool CanRotateProne(Player player, MovementContext mc, float deltaYaw) =>
            mc.IsAbleToRotateProne(Vector3.zero, deltaYaw, Quaternion.Euler(0f, 2f * deltaYaw, 0f), player.PlayerBones.Spine1, out _);

        private static void RotateBody(MovementContext mc, float deltaYaw)
        {
            Vector2 look = mc.Rotation;
            mc.Rotation = new Vector2(look.x + deltaYaw, look.y);
            mc.ApplyRotation(Quaternion.AngleAxis(deltaYaw, Vector3.up) * mc.TransformRotation);
        }

        // Distance from the player origin to the chest bone, along the body.
        private static float MeasurePullOffset(Player player, Vector3 root, float yaw)
        {
            Transform chest = player.PlayerBones.BodyPartCollidersDictionary[EBodyPartColliderType.RibcageUp].Collider.transform;

            Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            return Mathf.Max(0f, Vector3.Dot(chest.position - root, forward));
        }

        //====================[ Dragger: Release ]====================
        private static void TickDragger(Player dragger)
        {
            if (!IsLocalDragging) return;

            var reviveeState = RMSession.GetPlayerState(_draggingId);
            var revivee = ModUtils.GetPlayerById(_draggingId);

            bool gone = revivee == null || !revivee.HealthController.IsAlive;
            if (gone || reviveeState.CurrentDraggerId != dragger.ProfileId
                || reviveeState.State != RMState.BleedingOut || reviveeState.IsBeingRevived)
            {
                if (reviveeState.CurrentDraggerId == dragger.ProfileId) SetDragger(reviveeState, string.Empty);
                EndLocalDraggerSession();
                return;
            }

            if (RMSession.IsPlayerCritical(dragger.ProfileId) || !dragger.HealthController.IsAlive)
            {
                StopDrag(dragger, "dragger-downed");
                return;
            }

            string reason = (dragger.Position - revivee.Position).sqrMagnitude > MaxLeashDistance * MaxLeashDistance
                ? "leash"
                : BrokenStance(dragger, revivee);
            if (reason == null) return;

            StopDrag(dragger, reason);
            VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.DragReleased);
        }
    }
}
