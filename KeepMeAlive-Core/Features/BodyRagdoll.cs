//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using KeepMeAlive.Components;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ BodyRagdoll ]====================
    // Ragdolls a living remote player while they are downed. The pose returns to animation on exit.
    internal static class BodyRagdoll
    {
        //====================[ Tuning ]====================
        // Height the dragger holds the chest at above the floor (shoulders lifted, legs on the ground).
        private const float HoldHeight = 0.45f;
        // How far the hold may stretch before the chest is pulled hard back to it (m). Small = firm grip.
        private const float HoldSlack = 0.03f;
        // A hold target that jumps further than this in one tick is a teleport, not a pull: the whole body is
        // moved with it instead of being yanked across the map by the joint.
        private const float TeleportDistance = 2.5f;
        // If the chest ends up this far from the hold (stuck on geometry), the body is moved back to it.
        private const float MaxHoldError = 2f;
        // Fastest the hand moves toward the hold (m/s). Faster than any crouched dragger, so it only eases the
        // first pull on a body lying away from the hold.
        private const float MaxHandSpeed = 6f;
        // A body lying free (no hold) further than this from the player root (off a ledge, through a gap) is
        // given up and put back on the root.
        private const float MaxFreeDrift = 4f;

        private const float MaxJointStretch = 0.08f;
        private const float SnagReleaseMin = 0.3f;
        private const float SnagReleaseMax = 2f;
        // Penetration deeper than this counts as still inside geometry (resting contact is a few mm).
        private const float SnagOverlapTolerance = 0.02f;

        // How hard the ground slows a sliding limb (m/sÂ²); ~6 is ordinary floor friction. Higher makes the legs
        // and arms drag and trail more and a falling body slide less.
        private const float GroundFriction = 6f;

        private const int SolverIterations = 12;
        private const int SolverVelocityIterations = 4;

        private const float FallTipRate = 1f;

        // Ragdoll-to-animation blend on exit (seconds).
        private const float BlendOutDuration = 0.35f;

        private const string ContactObjectName = "KMA_RagdollContact";
        // Unity's built-in "Ignore Raycast" layer; in the project's collision matrix it collides with nothing,
        // so a contact collider moved onto it passes through the world while keeping its shape and bounds.
        private const int ReleasedLayer = 2;

        //====================[ Session ]====================
        private sealed class Session
        {
            public ObservedPlayer Player;
            public Rigidbody Chest;

            // Restored on exit.
            public Player.EAnimatorMask EnabledAnimators;
            public bool ComponentWasEnabled;
            public bool BodyAnimatorWasEnabled;
            public bool ArmsAnimatorWasEnabled;
            public bool ControllerWasEnabled;
            public BodyInteractable Interactable;

            public readonly List<RigidbodyState> Bodies = new List<RigidbodyState>();
            public readonly List<JointState> Joints = new List<JointState>();
            public readonly List<Detached> Tops = new List<Detached>();
            public readonly List<Collider> Contacts = new List<Collider>();

            public GameObject Hand;
            public Rigidbody HandBody;
            public Vector3 LastHoldTarget;

            // Contact colliders currently passing through the world after a snag.
            public readonly Dictionary<Collider, Release> Released = new Dictionary<Collider, Release>();
        }

        // When a released contact was first let go, and the earliest it may collide again.
        private struct Release
        {
            public float Since;
            public float Until;
        }

        // What the bones do when the ragdoll ends.
        private enum ExitPose
        {
            // Ease back into the animation (the normal end).
            Blend,
            // Straight back to the pre-ragdoll pose (a simulation that went bad is not worth blending from).
            Rest,
            // Stay where the simulation left them: the player died, and the corpse ragdoll carries on from there.
            Keep
        }

        private struct RigidbodyState
        {
            public Rigidbody Body;
            public bool WasKinematic;
            public CollisionDetectionMode CollisionMode;
            public float MaxDepenetrationVelocity;
            public float SleepThreshold;
            public int SolverIterations;
            public int SolverVelocityIterations;
            // The bone's animated local pose when the ragdoll started: where it goes back to.
            public Vector3 RestLocalPosition;
            public Quaternion RestLocalRotation;
        }

        private struct JointState
        {
            public CharacterJoint Joint;
            // The body the joint sits on (the child bone; connectedBody is its parent).
            public Rigidbody Body;
            public bool EnablePreprocessing;
            public float MassScale;
            public float ConnectedMassScale;
            public bool EnableProjection;
        }

        // A ragdoll root taken out from under the player while it simulates.
        private struct Detached
        {
            public Transform Transform;
            public Transform Parent;
            public int SiblingIndex;
        }

        private static readonly Dictionary<string, Session> Active = new Dictionary<string, Session>();
        // Players whose ragdoll could not be built or went bad; not retried until it is no longer wanted.
        private static readonly HashSet<string> Failed = new HashSet<string>();

        private static PhysicMaterial _contactMaterial;
        // Scratch buffers for UpdateReleased / IsInsideWorld.
        private static readonly List<Collider> _restored = new List<Collider>();
        private static readonly Collider[] _overlaps = new Collider[16];

        public static bool IsActive(string profileId) => !string.IsNullOrEmpty(profileId) && Active.ContainsKey(profileId);

        //====================[ Public API ]====================
        // Updates the ragdoll pose and optional drag hold.
        public static void Sync(Player player, bool want, Vector3? hold)
        {
            if (player == null || string.IsNullOrEmpty(player.ProfileId)) return;
            // A headless host has no visuals to ragdoll.
            if (FikaBackendUtils.IsHeadless) return;

            string id = player.ProfileId;
            bool active = Active.TryGetValue(id, out var session);

            if (active && session.Player == null)
            {
                Active.Remove(id);
                active = false;
            }

            if (!want) Failed.Remove(id);

            if (want && !active)
            {
                if (!Failed.Contains(id)) Enter(player, hold.HasValue);
            }
            else if (want)
            {
                Drive(session, hold);
            }
            else if (active)
            {
                Exit(session);
            }
        }

        public static void ReleaseForDeath(Player player) => EndForPlayer(player, ExitPose.Keep);

        private static void OnDeadOrUnspawn(Player player) => EndForPlayer(player, ExitPose.Rest);

        private static void EndForPlayer(Player player, ExitPose pose)
        {
            if (player == null || string.IsNullOrEmpty(player.ProfileId)) return;

            if (Active.TryGetValue(player.ProfileId, out var session))
                Exit(session, pose);
            Failed.Remove(player.ProfileId);

            var blend = player.GetComponent<BlendOut>();
            if (blend != null)
            {
                // Disabled as well: Destroy only takes effect at the end of the frame.
                blend.enabled = false;
                UnityEngine.Object.Destroy(blend);
            }
        }

        // Raid boundary: the player objects are gone, so there is nothing to restore. The detached ragdoll
        // roots and hand objects live at the scene root and go with the scene.
        public static void ClearAll()
        {
            Active.Clear();
            Failed.Clear();
        }

        //====================[ Enter ]====================
        // Starts the ragdoll, optionally with a drag hold.
        private static void Enter(Player target, bool held)
        {
            // Only ever the replicated copy of someone else (under Fika always an ObservedPlayer); the local
            // body is moved, not ragdolled.
            if (target.IsYourPlayer || target.IsAI) return;
            var player = (ObservedPlayer)target;
            var pool = player.GetComponent<PlayerPoolObject>();

            // A blend left over from the previous ragdoll would fight the new simulation.
            var staleBlend = player.GetComponent<BlendOut>();
            if (staleBlend != null) UnityEngine.Object.Destroy(staleBlend);

            var session = new Session { Player = player };
            // Registered before anything is mutated so a mid-way failure can be rolled back by Exit.
            Active[player.ProfileId] = session;
            player.OnPlayerDeadOrUnspawn += OnDeadOrUnspawn;

            try
            {
                session.EnabledAnimators = player.EnabledAnimators;
                session.ComponentWasEnabled = player.enabled;
                session.BodyAnimatorWasEnabled = player.BodyAnimatorCommon.enabled;
                session.ArmsAnimatorWasEnabled = player.ArmsAnimatorCommon.enabled;
                session.ControllerWasEnabled = player._characterController.isEnabled;

                // The same things Player.OnDead parks before handing the body to the corpse ragdoll.
                var mc = player.MovementContext;
                mc.OnStateChanged -= player.MovementContextOnStateChanged;
                mc.PhysicalConditionChanged -= player.ProceduralWeaponAnimation.PhysicalConditionUpdated;

                player.EnabledAnimators = 0;
                player.BodyAnimatorCommon.enabled = false;
                player.ArmsAnimatorCommon.enabled = false;

                player._characterController.isEnabled = false;
                if (player.POM != null) player.POM.Off();

                player.ProceduralWeaponAnimation.OnPreCollision -= player.IkStoreRaw;

                // The whole IK/head pass runs from the component's LateUpdate; disabling it stops that.
                // Position updates come from Fika's network loop, so the player root still follows the owner.
                player.enabled = false;

                if (BodyInteractableRuntime.TryGet(player.ProfileId, out var interactable))
                {
                    session.Interactable = interactable;
                    interactable.SetRagdollInert(true);
                }

                SpawnPhysics(pool, session);
                DetachFromRoot(session);
                if (!held) TipForward(player, session);

                RevivalDebugLog.LogDebug($"[BodyRagdoll] Entered for {player.ProfileId} ({session.Bodies.Count} bodies, {session.Joints.Count} joints, {session.Contacts.Count} contacts, {session.Tops.Count} roots)");
            }
            catch (Exception ex)
            {
                Failed.Add(player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Enter failed for {player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }

        // Creates (idempotently) and arms the joints and rigidbodies, the way CorpseRagdoll.Start does.
        private static void SpawnPhysics(PlayerPoolObject pool, Session session)
        {
            float maxDepenetration = EFTHardSettings.Instance.CorpseMaxDepenetrationVelocity;
            PhysicMaterial material = ContactMaterial();

            foreach (var spawner in pool.JointSpawners)
            {
                // CharacterJointSpawner always creates a CharacterJoint.
                var joint = (CharacterJoint)spawner.Create();
                var own = joint.GetComponent<Rigidbody>();

                session.Joints.Add(new JointState
                {
                    Joint = joint,
                    Body = own,
                    EnablePreprocessing = joint.enablePreprocessing,
                    MassScale = joint.massScale,
                    ConnectedMassScale = joint.connectedMassScale,
                    EnableProjection = joint.enableProjection
                });

                // CorpseRagdoll.Start: no preprocessing, projection on so a stretched joint is pulled back
                // instead of springing, mass-ratio scaling.
                joint.enablePreprocessing = false;
                joint.enableProjection = true;
                joint.massScale = joint.connectedBody.mass / own.mass;
                joint.connectedMassScale = 1f;
            }

            foreach (var spawner in pool.RigidbodySpawners)
            {
                Rigidbody body = spawner.Create();

                session.Bodies.Add(new RigidbodyState
                {
                    Body = body,
                    WasKinematic = body.isKinematic,
                    CollisionMode = body.collisionDetectionMode,
                    MaxDepenetrationVelocity = body.maxDepenetrationVelocity,
                    SleepThreshold = body.sleepThreshold,
                    SolverIterations = body.solverIterations,
                    SolverVelocityIterations = body.solverVelocityIterations,
                    RestLocalPosition = body.transform.localPosition,
                    RestLocalRotation = body.transform.localRotation
                });

                // The dragger holds the upper chest.
                if (spawner.GetComponent<BodyPartCollider>().BodyPartColliderType == EBodyPartColliderType.RibcageUp)
                    session.Chest = body;

                AddContact(body, material, session);

                body.isKinematic = false;
                body.maxDepenetrationVelocity = maxDepenetration;
                // Continuous against the static world: a limb whipped round by a turning dragger is fast and
                // small enough to step through a thin floor otherwise.
                body.collisionDetectionMode = CollisionDetectionMode.Continuous;
                body.solverIterations = SolverIterations;
                body.solverVelocityIterations = SolverVelocityIterations;
                // A body that falls asleep stops following the hold.
                body.sleepThreshold = 0f;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                // The game steps physics manually; only while a registered rigidbody exists does it run.
                PhysicsExtensions.UpdateController.SupportRigidbody(body, 0f);
            }
        }

        // Tips the body forward as it begins to fall.
        private static void TipForward(Player player, Session session)
        {
            Vector3 forward = player.Transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) return;
            forward.Normalize();

            float feet = player.Position.y;
            foreach (var state in session.Bodies)
            {
                var body = state.Body;
                float height = Mathf.Max(0f, body.position.y - feet);
                body.velocity = forward * (FallTipRate * height);
            }
        }

        //====================[ Contact Colliders ]====================
        // Adds a ground-contact copy without changing the hit collider.
        private static void AddContact(Rigidbody body, PhysicMaterial material, Session session)
        {
            var host = new GameObject(ContactObjectName) { layer = LayersMaskController.ShellsLayer };
            host.transform.SetParent(body.transform, false);

            var copy = CopyShape(body.GetComponent<Collider>(), host);
            copy.sharedMaterial = material;
            session.Contacts.Add(copy);
        }

        // Every ragdoll bone of the player prefab carries exactly one capsule, box or sphere.
        private static Collider CopyShape(Collider source, GameObject host)
        {
            switch (source)
            {
                case CapsuleCollider c:
                    var capsule = host.AddComponent<CapsuleCollider>();
                    capsule.center = c.center;
                    capsule.radius = c.radius;
                    capsule.height = c.height;
                    capsule.direction = c.direction;
                    return capsule;
                case BoxCollider b:
                    var box = host.AddComponent<BoxCollider>();
                    box.center = b.center;
                    box.size = b.size;
                    return box;
                case SphereCollider s:
                    var sphere = host.AddComponent<SphereCollider>();
                    sphere.center = s.center;
                    sphere.radius = s.radius;
                    return sphere;
                default:
                    throw new InvalidOperationException($"unexpected ragdoll collider {source.GetType().Name} on {source.name}");
            }
        }

        // Ground grip for the limbs, from GroundFriction (sliding friction decelerates at mu * g).
        private static PhysicMaterial ContactMaterial()
        {
            if (_contactMaterial == null)
            {
                const float mu = GroundFriction / 9.81f;
                _contactMaterial = new PhysicMaterial("KMA_RagdollContact")
                {
                    dynamicFriction = mu,
                    staticFriction = mu,
                    bounciness = 0f,
                    bounceCombine = PhysicMaterialCombine.Minimum,
                    frictionCombine = PhysicMaterialCombine.Average
                };
            }
            return _contactMaterial;
        }

        //====================[ Detach From Root ]====================
        // Takes the top of the ragdoll (every rigidbody with no ragdoll rigidbody above it - normally just the
        // pelvis) out from under the player, so the network moving the player root no longer drags the bones.
        // World pose is kept; the skinned meshes follow their bones wherever they are parented.
        private static void DetachFromRoot(Session session)
        {
            var bodies = new HashSet<Transform>();
            foreach (var state in session.Bodies)
                if (state.Body != null) bodies.Add(state.Body.transform);

            foreach (var t in bodies)
            {
                bool top = true;
                for (var p = t.parent; p != null; p = p.parent)
                {
                    if (bodies.Contains(p)) { top = false; break; }
                }
                if (!top) continue;

                session.Tops.Add(new Detached { Transform = t, Parent = t.parent, SiblingIndex = t.GetSiblingIndex() });
                t.SetParent(null, true);
            }
        }

        private static void ReattachToRoot(Session session)
        {
            foreach (var top in session.Tops)
            {
                top.Transform.SetParent(top.Parent, true);
                top.Transform.SetSiblingIndex(top.SiblingIndex);
            }
            session.Tops.Clear();
        }

        //====================[ Hand ]====================
        // Creates the kinematic hand that pulls the chest while dragging.
        private static void CreateHand(Session session)
        {
            var chest = session.Chest;
            var hand = new GameObject("KMA_RagdollHand");
            hand.transform.position = chest.position;

            var body = hand.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            var joint = hand.AddComponent<ConfigurableJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = Vector3.zero;
            joint.connectedAnchor = Vector3.zero;
            joint.connectedBody = chest;
            joint.xMotion = ConfigurableJointMotion.Limited;
            joint.yMotion = ConfigurableJointMotion.Limited;
            joint.zMotion = ConfigurableJointMotion.Limited;
            joint.linearLimit = new SoftJointLimit { limit = HoldSlack };
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.projectionDistance = 0.1f;
            joint.enablePreprocessing = false;

            session.Hand = hand;
            session.HandBody = body;
            session.LastHoldTarget = chest.position;
        }

        private static void DestroyHand(Session session)
        {
            if (session.Hand != null)
            {
                // Deactivated first: Destroy only takes effect at the end of the frame, and the joint would
                // keep pulling the chest through any physics step before then.
                session.Hand.SetActive(false);
                UnityEngine.Object.Destroy(session.Hand);
            }
            session.Hand = null;
            session.HandBody = null;
        }

        //====================[ Drive ]====================
        // Moves the hand toward the hold point and updates the ragdoll physics.
        private static void Drive(Session session, Vector3? hold)
        {
            var player = session.Player;
            Vector3 chestPosition = session.Chest.position;
            if (!IsFinite(chestPosition))
            {
                AbortRagdoll(session, "simulation produced an invalid position");
                return;
            }

            // The revive/heal proxies are attached by a coroutine and may only appear after the ragdoll started.
            if (session.Interactable == null && BodyInteractableRuntime.TryGet(player.ProfileId, out var interactable))
            {
                session.Interactable = interactable;
                interactable.SetRagdollInert(true);
            }

            FixSnags(session);
            UpdateReleased(session);

            if (!hold.HasValue)
            {
                if (session.Hand != null) DestroyHand(session);
                if ((chestPosition - player.Position).sqrMagnitude > MaxFreeDrift * MaxFreeDrift)
                    AbortRagdoll(session, "body drifted away from the player", ExitPose.Blend);
                return;
            }

            // The hold is on the ground (the player root's height); the hand holds the chest above it.
            Vector3 target = hold.Value + Vector3.up * HoldHeight;
            if (session.Hand == null) CreateHand(session);

            // Teleport of the dragger (or of the body by the network): move the whole ragdoll with the hold
            // instead of letting the joint fling it across the gap.
            Vector3 jump = target - session.LastHoldTarget;
            if (jump.sqrMagnitude > TeleportDistance * TeleportDistance)
                ShiftBody(session, jump);
            else if ((chestPosition - target).sqrMagnitude > MaxHoldError * MaxHoldError)
                ShiftBody(session, target - chestPosition);

            session.LastHoldTarget = target;
            // MovePosition, not the transform: the hand then has a velocity for the step, so the joint pulls
            // smoothly instead of being teleported.
            Vector3 next = Vector3.MoveTowards(session.HandBody.position, target, MaxHandSpeed * Time.deltaTime);
            session.HandBody.MovePosition(next);
        }

        // Moves every body of the ragdoll by the same offset and stops it, keeping its shape.
        private static void ShiftBody(Session session, Vector3 offset)
        {
            foreach (var state in session.Bodies)
            {
                var body = state.Body;
                body.position += offset;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            if (session.HandBody != null) session.HandBody.position += offset;
        }

        //====================[ Snags ]====================
        // Repositions stretched limbs and briefly releases their contact colliders.
        private static void FixSnags(Session session)
        {
            bool moved = false;

            foreach (var state in session.Joints)
            {
                var joint = state.Joint;
                var limb = state.Body;
                var parent = joint.connectedBody;

                Vector3 limbAnchor = limb.transform.TransformPoint(joint.anchor);
                Vector3 parentAnchor = parent.transform.TransformPoint(joint.connectedAnchor);
                Vector3 gap = parentAnchor - limbAnchor;
                if (gap.sqrMagnitude < MaxJointStretch * MaxJointStretch) continue;

                // Through the transform, so the bones below follow in the same move.
                limb.transform.position += gap;
                limb.velocity = parent.velocity;
                limb.angularVelocity = parent.angularVelocity;
                ReleaseFromWorld(session, limb.transform);
                moved = true;
            }

            // The game drives physics itself with transform auto-sync off; without this the next step would
            // still see the stretched pose and write it back.
            if (moved) PhysicsExtensions.SyncTransforms();
        }

        // Moves the contact colliders of a limb and everything below it onto a layer that collides with nothing.
        // The collider stays enabled (its bounds and shape stay valid for the overlap test that brings it back).
        private static void ReleaseFromWorld(Session session, Transform limb)
        {
            float until = Time.time + SnagReleaseMin;
            foreach (var collider in session.Contacts)
            {
                if (!collider.transform.IsChildOf(limb)) continue;

                collider.gameObject.layer = ReleasedLayer;
                // A limb that snags again restarts its minimum but keeps its first release time, so the hard
                // maximum still counts from the original snag.
                float since = session.Released.TryGetValue(collider, out var existing) ? existing.Since : Time.time;
                session.Released[collider] = new Release { Since = since, Until = until };
            }
        }

        // Brings released contacts back once they are no longer inside anything they would collide with.
        private static void UpdateReleased(Session session)
        {
            if (session.Released.Count == 0) return;

            _restored.Clear();
            foreach (var pair in session.Released)
            {
                var collider = pair.Key;
                if (Time.time < pair.Value.Until) continue;

                bool overdue = Time.time >= pair.Value.Since + SnagReleaseMax;
                if (overdue || !IsInsideWorld(collider))
                {
                    collider.gameObject.layer = LayersMaskController.ShellsLayer;
                    _restored.Add(collider);
                }
            }
            foreach (var collider in _restored) session.Released.Remove(collider);
        }

        private static bool IsInsideWorld(Collider collider)
        {
            Bounds bounds = collider.bounds;
            int count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, _overlaps, Quaternion.identity,
                LayersMaskController.ShellsCollisionsMask, QueryTriggerInteraction.Ignore);

            Transform t = collider.transform;
            for (int i = 0; i < count; i++)
            {
                var other = _overlaps[i];
                if (Physics.ComputePenetration(collider, t.position, t.rotation,
                        other, other.transform.position, other.transform.rotation, out _, out float depth)
                    && depth > SnagOverlapTolerance)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        // Gives up on the ragdoll until it is no longer wanted; the body carries on as the plain (animated)
        // version.
        private static void AbortRagdoll(Session session, string reason, ExitPose pose = ExitPose.Rest)
        {
            Failed.Add(session.Player.ProfileId);
            Plugin.LogSource.LogWarning($"[BodyRagdoll] Aborting for {session.Player.ProfileId}: {reason}");
            Exit(session, pose);
        }

        //====================[ Exit ]====================
        private static void Exit(Session session, ExitPose pose = ExitPose.Blend)
        {
            var player = session.Player;
            Active.Remove(player.ProfileId);
            player.OnPlayerDeadOrUnspawn -= OnDeadOrUnspawn;

            try
            {
                DestroyHand(session);

                foreach (var contact in session.Contacts)
                    UnityEngine.Object.Destroy(contact.gameObject);
                session.Contacts.Clear();
                session.Released.Clear();

                foreach (var state in session.Bodies)
                {
                    var body = state.Body;
                    PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
                    // Velocity can only be cleared while dynamic; kinematic bodies reject the write. A body
                    // handed to the corpse keeps its velocity.
                    if (!body.isKinematic && pose != ExitPose.Keep)
                    {
                        body.velocity = Vector3.zero;
                        body.angularVelocity = Vector3.zero;
                    }
                    body.isKinematic = state.WasKinematic;
                    body.collisionDetectionMode = state.CollisionMode;
                    body.maxDepenetrationVelocity = state.MaxDepenetrationVelocity;
                    body.sleepThreshold = state.SleepThreshold;
                    body.solverIterations = state.SolverIterations;
                    body.solverVelocityIterations = state.SolverVelocityIterations;
                }

                foreach (var state in session.Joints)
                {
                    state.Joint.enablePreprocessing = state.EnablePreprocessing;
                    state.Joint.enableProjection = state.EnableProjection;
                    state.Joint.massScale = state.MassScale;
                    state.Joint.connectedMassScale = state.ConnectedMassScale;
                }

                // Back under the player, keeping the world pose so the blend starts from what was on screen.
                var tops = new List<Transform>();
                foreach (var top in session.Tops) tops.Add(top.Transform);
                ReattachToRoot(session);

                if (session.Interactable != null) session.Interactable.SetRagdollInert(false);

                if (pose == ExitPose.Blend)
                {
                    var snapshot = player.gameObject.AddComponent<BlendOut>();
                    foreach (var state in session.Bodies)
                    {
                        var t = state.Body.transform;
                        snapshot.Capture(t, tops.Contains(t), state.RestLocalPosition, state.RestLocalRotation);
                    }
                }
                else if (pose == ExitPose.Rest)
                {
                    foreach (var state in session.Bodies)
                    {
                        state.Body.transform.localRotation = state.RestLocalRotation;
                        if (tops.Contains(state.Body.transform)) state.Body.transform.localPosition = state.RestLocalPosition;
                    }
                }

                // With the animator back on it rewrites every bone transform next frame; BlendOut eases into it.
                player.BodyAnimatorCommon.enabled = session.BodyAnimatorWasEnabled;
                player.ArmsAnimatorCommon.enabled = session.ArmsAnimatorWasEnabled;
                player.EnabledAnimators = session.EnabledAnimators;

                player._characterController.isEnabled = session.ControllerWasEnabled;
                if (player.POM != null) player.POM.On();

                player.enabled = session.ComponentWasEnabled;

                // -= first so a handler cannot end up subscribed twice.
                var mc = player.MovementContext;
                player.ProceduralWeaponAnimation.OnPreCollision -= player.IkStoreRaw;
                player.ProceduralWeaponAnimation.OnPreCollision += player.IkStoreRaw;
                mc.OnStateChanged -= player.MovementContextOnStateChanged;
                mc.OnStateChanged += player.MovementContextOnStateChanged;
                mc.PhysicalConditionChanged -= player.ProceduralWeaponAnimation.PhysicalConditionUpdated;
                mc.PhysicalConditionChanged += player.ProceduralWeaponAnimation.PhysicalConditionUpdated;

                RevivalDebugLog.LogDebug($"[BodyRagdoll] Exited for {player.ProfileId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[BodyRagdoll] Exit failed: {ex}");
            }
        }

        //====================[ Blend Out ]====================
        // Blends the simulated pose back into the animation after the ragdoll ends.
        [DefaultExecutionOrder(32000)]
        private sealed class BlendOut : MonoBehaviour
        {
            private sealed class Entry
            {
                public Transform Bone;
                public bool BlendPosition;
                public Quaternion FromRotation;
                public Vector3 FromPosition;
                public Quaternion RestRotation;
                public Vector3 RestPosition;
                public bool HasWritten;
                public Quaternion WrittenRotation;
                public Vector3 WrittenPosition;
            }

            private readonly List<Entry> _entries = new List<Entry>();
            private float _t;

            public void Capture(Transform bone, bool includePosition, Vector3 restPosition, Quaternion restRotation)
            {
                _entries.Add(new Entry
                {
                    Bone = bone,
                    BlendPosition = includePosition,
                    FromRotation = bone.localRotation,
                    FromPosition = bone.localPosition,
                    RestRotation = restRotation,
                    RestPosition = restPosition
                });
            }

            private void LateUpdate()
            {
                _t += Time.deltaTime / BlendOutDuration;
                bool done = _t >= 1f;
                // The last frame lands exactly on the target.
                float weight = done ? 0f : 1f - Mathf.SmoothStep(0f, 1f, _t);

                foreach (var e in _entries)
                {
                    var bone = e.Bone;
                    if (bone == null) continue;

                    Quaternion targetRotation = bone.localRotation;
                    if (e.HasWritten && Quaternion.Angle(targetRotation, e.WrittenRotation) < 0.01f)
                        targetRotation = e.RestRotation;
                    bone.localRotation = Quaternion.Slerp(targetRotation, e.FromRotation, weight);
                    e.WrittenRotation = bone.localRotation;

                    if (e.BlendPosition)
                    {
                        Vector3 targetPosition = bone.localPosition;
                        if (e.HasWritten && (targetPosition - e.WrittenPosition).sqrMagnitude < 1e-8f)
                            targetPosition = e.RestPosition;
                        bone.localPosition = Vector3.Lerp(targetPosition, e.FromPosition, weight);
                        e.WrittenPosition = bone.localPosition;
                    }

                    e.HasWritten = true;
                }

                if (done) Destroy(this);
            }
        }
    }
}
