//====================[ Imports ]====================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using KeepMeAlive.Components;
using KeepMeAlive.Features;
using KeepMeAlive.Fika;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Components
{
    //====================[ BodyInteractable ]====================
    public class BodyInteractable : InteractableObject
    {
        private static SyncedGameplayConfig Cfg => SyncedServerConfigStore.Config.Gameplay;

        //====================[ Nested Types ]====================
        // Marker on each bone-parented collider child.
        // BodyProxyFindInteractablePatch routes raycast hits on it to the owner.
        public sealed class BodyInteractableProxy : MonoBehaviour
        {
            public BodyInteractable Owner;
        }

        //====================[ Fields ]====================
        public Player Revivee { get; set; }
        public bool HasActivePicker => _activeMedPicker != null;

        // What a proxy hit resolves to: the open med picker, otherwise this body.
        public GameObject InteractionTarget => _activeMedPicker != null ? _activeMedPicker.gameObject : gameObject;
        
        private readonly List<Collider> _colliders = new List<Collider>();
        // Proxy GameObjects live under the revivee's bone transforms, not under this object,
        // so they must be destroyed explicitly.
        private readonly List<GameObject> _proxyObjects = new List<GameObject>();
        private bool _collidersEnabled;
        private MedPickerInteractable _activeMedPicker;
        private bool _isLootScreenOpen;
        private GamePlayerOwner _lootOwner;

        // Track action changes to avoid resetting the selection.
        private GamePlayerOwner _viewerOwner;
        private int _shownSignature;
        private float _nextSignaturePoll;
        private const float SignaturePollInterval = 0.25f;

        public static float ReviveHoldTime => RevivePolicy.GetHoldDuration(ReviveSource.Team);

        //====================[ Bone Collider Filter ]====================
        // Covered by HeadCommon, so not mirrored.
        private static readonly HashSet<EBodyPartColliderType> HeadSubParts = new HashSet<EBodyPartColliderType>
        {
            EBodyPartColliderType.ParietalHead,
            EBodyPartColliderType.BackHead,
            EBodyPartColliderType.Ears,
            EBodyPartColliderType.Eyes,
            EBodyPartColliderType.Jaw,
        };

        // Fixed world-space padding keeps thin hitboxes easy to select.
        private const float MirroredColliderPadding = 0.05f;

        //====================[ Unity Lifecycle ]====================
        private void Awake()
        {
            gameObject.layer = LayerMask.NameToLayer("Interactive");
        }

        private void Update()
        {
            if (Revivee == null || _colliders.Count == 0) return;

            // Bots never expose revival/team-heal interactables, and a dead teammate's corpse
            // (same GameObject) must fall back to vanilla looting.
            if (Revivee.IsAI || Revivee.AIData?.IsAI == true || Revivee.HealthController == null || !Revivee.HealthController.IsAlive)
            {
                Destroy(gameObject);
                return;
            }

            // Disable bone colliders while the loot screen is open.
            if (_isLootScreenOpen)
            {
                SetCollidersEnabled(false);
                return;
            }

            SetCollidersEnabled(true);

            // While the picker is open, proxy hits resolve to it and it refreshes its own list.
            if (!HasActivePicker) RefreshListIfActionsChanged();
        }

        // Refresh the list when its actions change.
        private void RefreshListIfActionsChanged()
        {
            if (_viewerOwner == null || Time.time < _nextSignaturePoll) return;
            _nextSignaturePoll = Time.time + SignaturePollInterval;

            var viewer = _viewerOwner.Player;
            if (viewer == null || !ReferenceEquals(viewer.InteractableObject, this)) return;

            if (Signature(BuildActions(_viewerOwner)) != _shownSignature) SetStateUpdateTime();
        }

        internal static int Signature(AvailableInteractionState state)
        {
            unchecked
            {
                int hash = 17;
                foreach (var action in state.Actions)
                {
                    hash = hash * 31 + (action.Name?.GetHashCode() ?? 0);
                    hash = hash * 31 + (action.Disabled ? 1 : 0);
                }
                return hash * 31 + state.Actions.Count;
            }
        }

        //====================[ Setup / Attachment ]====================
        public static BodyInteractable AttachToPlayer(Player player)
        {
            if (player == null) return null; 
            
            // Prevent multiple attachments if PlayerId is set multiple times
            if (player.gameObject.GetComponentInChildren<BodyInteractable>() != null) return null;

            // We use a coroutine to wait for bones, and also to wait until after Fika attaches
            Plugin.StaticCoroutineRunner.StartCoroutine(WaitForBonesAndBuild(player));
            return null; // Return null synchronously, we cache it dynamically later anyway
        }

        private static IEnumerator WaitForBonesAndBuild(Player player)
        {
            // Wait for bones to be ready and Profile to be fully assigned; stop if the player
            // object is destroyed (left the raid) while we wait.
            while (player != null && (player.PlayerBones == null || player.PlayerBones.RootJoint == null || player.Profile == null || string.IsNullOrEmpty(player.ProfileId)))
            {
                yield return null;
            }
            if (player == null) yield break;
            
            // Wait slightly after bones are ready (allow Fika UI to spawn)
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            if (player == null) yield break;

            // Confirm the player type and owner before attaching the interactable.
            if (player.IsYourPlayer || player.IsAI || player.AIData?.IsAI == true) yield break;

            // Another attach may have finished during the wait.
            if (player.gameObject.GetComponentInChildren<BodyInteractable>() != null) yield break;

            // Root GO holds the BodyInteractable component and serves as MedPicker anchor
            var go = new GameObject("Body Interactable");
            go.transform.SetParent(player.gameObject.transform, false);
            go.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            go.transform.localRotation = Quaternion.identity;
            go.layer = LayerMask.NameToLayer("Interactive");

            var bi = go.AddComponent<BodyInteractable>();
            bi.Revivee = player;

            int interactiveLayer = LayerMask.NameToLayer("Interactive");

            var bodyParts = player.PlayerBones.BodyPartColliders;
            bool hasHeadCommon = false;
            foreach (BodyPartCollider bpc in bodyParts)
            {
                if (bpc != null && bpc.Collider != null && bpc.BodyPartColliderType == EBodyPartColliderType.HeadCommon)
                {
                    hasHeadCommon = true;
                    break;
                }
            }

            // Clone body hitboxes onto their bone transforms (excluding armor plates).
            var mirrored = new HashSet<Collider>();
            foreach (BodyPartCollider bpc in bodyParts)
            {
                if (bpc == null || bpc.Collider == null) continue;
                if (bpc is ArmorPlateCollider) continue;
                if (hasHeadCommon && HeadSubParts.Contains(bpc.BodyPartColliderType)) continue;
                if (!mirrored.Add(bpc.Collider)) continue;

                var childGo = new GameObject($"BI_{bpc.BodyPartColliderType}");
                childGo.transform.SetParent(bpc.Collider.transform, false);
                childGo.transform.localPosition = Vector3.zero;
                childGo.transform.localRotation = Quaternion.identity;
                childGo.layer = interactiveLayer;

                Collider cloned = CloneColliderShape(bpc.Collider, childGo);
                if (cloned == null) { UnityEngine.Object.Destroy(childGo); continue; }
                cloned.isTrigger = false;
                cloned.enabled = false;

                var proxy = childGo.AddComponent<BodyInteractableProxy>();
                proxy.Owner = bi;

                bi._colliders.Add(cloned);
                bi._proxyObjects.Add(childGo);
            }

            Features.BodyInteractableRuntime.Register(player.ProfileId, bi);
            go.SetActive(true);

            RevivalDebugLog.LogDebug($"[BodyInteractable] Created {bi._colliders.Count} mirrored colliders for player {player.ProfileId}");
        }

        //====================[ Collider Helpers ]====================
        private static Collider CloneColliderShape(Collider source, GameObject target)
        {
            // Padding is in world metres; convert to the bone's local space.
            Vector3 scale = source.transform.lossyScale;
            float sx = Mathf.Max(Mathf.Abs(scale.x), 0.0001f);
            float sy = Mathf.Max(Mathf.Abs(scale.y), 0.0001f);
            float sz = Mathf.Max(Mathf.Abs(scale.z), 0.0001f);
            float maxScale = Mathf.Max(sx, Mathf.Max(sy, sz));
            float pad = MirroredColliderPadding;

            if (source is BoxCollider box)
            {
                var clone = target.AddComponent<BoxCollider>();
                clone.center = box.center;
                clone.size = box.size + new Vector3(2f * pad / sx, 2f * pad / sy, 2f * pad / sz);
                return clone;
            }
            if (source is SphereCollider sphere)
            {
                var clone = target.AddComponent<SphereCollider>();
                clone.center = sphere.center;
                clone.radius = sphere.radius + pad / maxScale;
                return clone;
            }
            if (source is CapsuleCollider capsule)
            {
                var clone = target.AddComponent<CapsuleCollider>();
                clone.center = capsule.center;
                // Capsule radius uses the larger cross-axis scale.
                float axisScale = capsule.direction == 0 ? sx : capsule.direction == 1 ? sy : sz;
                float radialScale = capsule.direction == 0 ? Mathf.Max(sy, sz)
                                  : capsule.direction == 1 ? Mathf.Max(sx, sz)
                                  : Mathf.Max(sx, sy);
                clone.radius = capsule.radius + pad / radialScale;
                clone.height = capsule.height + 2f * pad / axisScale;
                clone.direction = capsule.direction;
                return clone;
            }
            return null;
        }

        // Use triggers while the body is ragdolled to avoid proxy collisions with its bones.
        public void SetRagdollInert(bool inert)
        {
            for (int i = _colliders.Count - 1; i >= 0; i--)
            {
                if (_colliders[i] == null) { _colliders.RemoveAt(i); continue; }
                _colliders[i].isTrigger = inert;
            }
        }

        // Skips the collider pass when the state is unchanged.
        private void SetCollidersEnabled(bool enabled)
        {
            if (_collidersEnabled == enabled) return;
            _collidersEnabled = enabled;

            for (int i = _colliders.Count - 1; i >= 0; i--)
            {
                if (_colliders[i] == null) { _colliders.RemoveAt(i); continue; }
                _colliders[i].enabled = enabled;
            }
        }

        //====================[ Logic & Interaction ]====================

        public void OnRevive(GamePlayerOwner owner)
        {
            if (Revivee is null || owner?.Player is null) return;

            if (!RevivePolicy.IsEnabled(ReviveSource.Team))
            {
                VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.TeamReviveDisabled);
                return;
            }

            // A hold is already running. PlantPlayerState.Plant just exits the plant without firing
            // the running hold's callback, so a second press would kill it with no cancel sent.
            if (owner.Player.CurrentManagedState is PlantPlayerState) return;

            if (owner.Player.CurrentState is not IdlePlayerState)
            {
                VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.CannotReviveWhileMoving);
                return;
            }

            VFX_UI.ObjectivePanel(Color.cyan, PlayerFacingMessages.Interaction.RevivingObjective, ReviveHoldTime);

            var handler = new ReviveCompleteHandler
            {
                owner = owner,
                targetId = Revivee.ProfileId,
                reviverId = owner.Player.ProfileId
            };

            owner.Player.CurrentManagedState.Plant(true, false, ReviveHoldTime, handler.Complete);
            FikaBridge.SendTeamHelpPacket(Revivee.ProfileId, owner.Player.ProfileId);
        }

        // Called on each list build; records what the list shows.
        public AvailableInteractionState GetActions(GamePlayerOwner owner)
        {
            var actions = BuildActions(owner);
            _viewerOwner = owner;
            _shownSignature = Signature(actions);
            _nextSignaturePoll = Time.time + SignaturePollInterval;
            return actions;
        }

        private AvailableInteractionState BuildActions(GamePlayerOwner owner)
        {
            var actions = new AvailableInteractionState();

            if (Revivee == null || owner?.Player == null) return actions;
            if (Revivee.IsAI || Revivee.AIData?.IsAI == true) return actions;
            // Empty result -> TryRouteActions returns false -> vanilla corpse interactions.
            if (Revivee.HealthController == null || !Revivee.HealthController.IsAlive) return actions;
            if (RMSession.IsPlayerCritical(owner.Player.ProfileId)) return actions;

            RMSession.TryGetPlayerState(Revivee.ProfileId, out var reviveeState);

            // During active revive progress we intentionally expose no actions.
            // This prevents both revive/search and category picker interactions while settling.
            if (reviveeState?.State == RMState.Reviving) return actions;

            if (reviveeState?.State == RMState.BleedingOut)
            {
                if (RevivePolicy.IsEnabled(ReviveSource.Team))
                {
                    bool canRevive = Cfg.Development.NoReviveItemRequired || ModUtils.HasReviveItem(owner.Player);
                    bool hasLives = reviveeState.LivesRemaining >= Cfg.Revival.TeamReviveLivesCost;
                    actions.Actions.Add(new InteractionAction
                    {
                        Action = () => OnRevive(owner),
                        Name = PlayerFacingMessages.Interaction.ReviveAction,
                        Disabled = !canRevive || !hasLives
                    });
                }

                if (reviveeState.CurrentDraggerId == owner.Player.ProfileId)
                {
                    actions.Actions.Add(new InteractionAction
                    {
                        Action = () => DownedDragController.StopDrag(owner.Player, "menu"),
                        Name = PlayerFacingMessages.Interaction.ReleaseAction,
                        Disabled = false
                    });
                }
                else if (!reviveeState.IsBeingDragged)
                {
                    actions.Actions.Add(new InteractionAction
                    {
                        Action = () => DownedDragController.StartDrag(owner, Revivee),
                        Name = PlayerFacingMessages.Interaction.DragAction,
                        Disabled = !DownedDragController.CanStartDrag(owner.Player, Revivee, reviveeState)
                    });
                }

                if (Cfg.TeamHealing.AllowLootDownedPlayers)
                {
                    actions.Actions.Add(new InteractionAction
                    {
                        Action = () => OnLootDowned(owner),
                        Name = PlayerFacingMessages.Interaction.SearchAction,
                        Disabled = false
                    });
                }
            }
            else if (Cfg.TeamHealing.Enabled)
            {
                foreach (MedCategory cat in Enum.GetValues(typeof(MedCategory)))
                {
                    MedCategory captured = cat;
                    bool patientNeeds = TeamMedical.PatientNeedsCategory(Revivee, captured);
                    if (!patientNeeds) continue; // Only show category if the patient needs healing for it

                    bool hasMeds = TeamMedical.HealerHasMedForCategory(owner.Player, captured);
                    actions.Actions.Add(new InteractionAction
                    {
                        Action = () => OpenFilteredMedPicker(owner, captured),
                        Name = CategoryLabel(captured),
                        Disabled = !hasMeds
                    });
                }
            }

            return actions;
        }

        //====================[ Loot Downed ]====================
        public void OnLootDowned(GamePlayerOwner owner)
        {
            if (Revivee == null || owner?.Player == null || _isLootScreenOpen) return;
            if (!RMSession.TryGetPlayerState(Revivee.ProfileId, out var st) || st.State != RMState.BleedingOut) return;

            _isLootScreenOpen = true;
            _lootOwner = owner;
            SetCollidersEnabled(false);

            // Mark all downed player items as searched/known in the viewer's search controller
            // so the loot screen allows interaction (bypasses ISearchableContainer search gate).
            var searchCtrl = owner.Player.InventoryController.SearchController;
            var playerSearch = searchCtrl as PlayerSearchController;
            var playerSearchCtrl = searchCtrl as ActiveSearchController;
            foreach (Item item in Revivee.Equipment.GetAllItemsFromCollection())
            {
                // Mark searchable containers (rigs, backpacks) as searched
                if (item is SearchableItem searchable)
                    playerSearch?._searchedItems.Add(searchable);

                // Mark individual items as temporarily known
                playerSearchCtrl?.SetItemAsTemporaryKnown(item);
            }

            DownedLootGuard.Begin(owner.Player, Revivee);
            RMSession.PlayerStateChanged += OnReviveeStateChanged;

            owner.ShowInventoryScreenLoot(Revivee.Equipment, () =>
            {
                EndLootSession();
                owner.Player.SetInventoryOpened(false);
            });
        }

        private void OnReviveeStateChanged(string playerId, RMState oldState, RMState newState)
        {
            if (Revivee == null || playerId != Revivee.ProfileId) return;
            if (newState == RMState.BleedingOut) return;

            // Teammate is getting up (or died) - the loot session must end immediately.
            var owner = _lootOwner;
            EndLootSession();
            try { owner?.CloseInventoryIfOpen(); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[BodyInteractable] CloseInventoryIfOpen failed: {ex.Message}"); }
        }

        private void EndLootSession()
        {
            if (!_isLootScreenOpen) return;
            RMSession.PlayerStateChanged -= OnReviveeStateChanged;
            DownedLootGuard.End();
            _isLootScreenOpen = false;
            _lootOwner = null;
        }

        private void OnDestroy()
        {
            var owner = _lootOwner;
            EndLootSession();
            try { owner?.CloseInventoryIfOpen(); } catch { }

            ForceClosePicker();
            for (int i = 0; i < _proxyObjects.Count; i++)
            {
                if (_proxyObjects[i] != null) Destroy(_proxyObjects[i]);
            }
            _proxyObjects.Clear();
            _colliders.Clear();

            if (Revivee != null)
            {
                BodyInteractableRuntime.Remove(Revivee.ProfileId, this);

                // Clear cached revival state when the remote Player object is removed.
                RMSession.RemovePlayer(Revivee.ProfileId);
            }
        }

        public void OpenFilteredMedPicker(GamePlayerOwner owner, MedCategory category)
        {
            if (Revivee == null || owner?.Player == null || RMSession.IsPlayerCritical(Revivee.ProfileId)) return;

            ForceClosePicker();

            // No collider: proxy hits on the body route to the picker (see InteractionTarget).
            var pickerGo = new GameObject(PlayerFacingMessages.Interaction.MedPickerName);
            pickerGo.transform.SetParent(transform, false);
            var picker = pickerGo.AddComponent<MedPickerInteractable>();
            picker.Init(owner.Player, Revivee, this, category);
            _activeMedPicker = picker;
        }

        // Only the picker currently open may clear it; a stale picker's late close must not.
        public void RestoreFromPicker(MedPickerInteractable picker)
        {
            if (ReferenceEquals(_activeMedPicker, picker)) _activeMedPicker = null;
        }

        public void ForceClosePicker()
        {
            if (_activeMedPicker != null) Destroy(_activeMedPicker.gameObject);
            _activeMedPicker = null;
        }

        private static string CategoryLabel(MedCategory cat)
        {
            return cat switch
            {
                MedCategory.Bleeds => PlayerFacingMessages.Interaction.MedicBleeds,
                MedCategory.Breaks => PlayerFacingMessages.Interaction.MedicBreaks,
                MedCategory.Health => PlayerFacingMessages.Interaction.MedicHealth,
                MedCategory.Comfort => PlayerFacingMessages.Interaction.MedicComfort,
                MedCategory.Nutrition => PlayerFacingMessages.Interaction.MedicNutrition,
                _ => cat.ToString()
            };
        }

        //====================[ ReviveCompleteHandler ]====================
        internal class ReviveCompleteHandler
        {
            public GamePlayerOwner owner;
            public string targetId;
            public string reviverId;

            public void Complete(bool result)
            {
                VFX_UI.HideObjectivePanel();

                if (result)
                {
                    var reviveeState = RMSession.GetPlayerState(targetId);
                    if (reviveeState.State != RMState.BleedingOut)
                    {
                        FikaBridge.SendTeamCancelPacket(targetId, reviverId);
                        VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.ReviveNoLongerPossible);
                        return;
                    }

                    RevivalController.BeginTeamReviveStart(owner.Player, targetId);
                }
                else
                {
                    FikaBridge.SendTeamCancelPacket(targetId, reviverId);
                    VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.ReviveCancelled);
                }
            }
        }
    }
}
