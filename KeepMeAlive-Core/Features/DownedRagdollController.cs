//====================[ Imports ]====================
using EFT;
using KeepMeAlive.Components;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ DownedRagdollController ]====================
    // Keeps a downed player limp until movement or a revive ends the state.
    internal static class DownedRagdollController
    {
        //====================[ Tuning ]====================
        // Minimum time before movement can end the limp state.
        private const float MinLimpSeconds = 1.5f;
        // Movement input below this is stick noise, not a deliberate move.
        private const float MoveInputThreshold = 0.1f;
        // Ignore stale input when the game skips movement-axis updates.
        private const float MoveInputMaxAge = 0.2f;

        //====================[ Shared ]====================
        // Whether the player should stay ragdolled, including during the death handoff.
        public static bool WantsRagdoll(RMPlayer st) =>
            st != null && st.IsLimp && (st.State == RMState.BleedingOut || (st.State == RMState.None && st.KillOverride));

        //====================[ Owner ]====================
        public static void Begin(RMPlayer st)
        {
            st.IsLimp = true;
            st.LimpStartTime = Time.time;
        }

        // Per tick for the local player while critical.
        public static void TickLocal(Player player, RMPlayer st)
        {
            // Keep the revivee limp while dragged and restart the minimum time on release.
            if (st.IsBeingDragged && st.State == RMState.BleedingOut)
            {
                if (!st.IsLimp)
                {
                    st.IsLimp = true;
                    st.ResyncCooldown = -1f;
                }
                st.LimpStartTime = Time.time;
                return;
            }

            if (!st.IsLimp) return;

            // A revive (or anything else that ends the bleed-out) gets the body up.
            if (st.State != RMState.BleedingOut)
            {
                End(player, st, $"state {st.State}");
                return;
            }

            if (Time.time - st.LimpStartTime < MinLimpSeconds) return;
            // Unconscious: the player is out and cannot pick themselves up.
            if (st.IsSilentInventoryAnimActive) return;
            if (player.InputDirection.sqrMagnitude < MoveInputThreshold * MoveInputThreshold) return;
            if (player.MovementIdlingTime > MoveInputMaxAge) return;

            End(player, st, "moved");
        }

        private static void End(Player player, RMPlayer st, string reason)
        {
            st.IsLimp = false;
            // Sends the resync this tick so observers get the body up straight away.
            st.ResyncCooldown = -1f;
            RevivalDebugLog.LogDebug($"[DownedRagdoll] {player.ProfileId} no longer limp ({reason})");
        }
    }
}
