// FlockManagerGizmos.cs — Slice 11 (M5-4) editor-only gizmo drawer for FlockManager.
// Draws the per-flock preferred-bounds AABB sourced from IFlockSettings whenever the
// FlockManager GameObject is selected. The runtime FlockManager class no longer carries
// an OnDrawGizmosSelected of its own — keeping all gizmo code under the Editor asmdef
// avoids leaking UnityEditor-style assumptions into the runtime build.

using Bird_behiviour.Flocking.Core;
using Bird_behiviour.Flocking.Simulation;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Bird_behiviour.Flocking.Editor
{
    /// <summary>
    /// Editor-only gizmo drawer for <see cref="FlockManager"/>. Renders the manager's
    /// preferred-bounds AABB (from <see cref="IFlockSettings.PreferredCenter"/> +
    /// <see cref="IFlockSettings.PreferredExtents"/>) whenever the manager is selected.
    /// Silently no-ops if no settings asset is bound.
    /// </summary>
    internal static class FlockManagerGizmos
    {
        [DrawGizmo(GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawManagerGizmos(FlockManager manager, GizmoType type)
        {
            if (manager == null) return;
            IFlockSettings s = manager.Settings;
            if (s == null) return;

            Color prev = Gizmos.color;
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.5f);
            Gizmos.DrawWireCube((Vector3)(float3)s.PreferredCenter,
                                (Vector3)(float3)s.PreferredExtents * 2f);
            Gizmos.color = prev;
        }
    }
}
