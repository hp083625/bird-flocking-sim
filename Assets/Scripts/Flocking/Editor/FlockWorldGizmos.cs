// FlockWorldGizmos.cs — Slice 11 (M5-4) editor-only gizmo drawer for FlockWorld.
// Draws the world-bounds AABB whenever the FlockWorld is selected, and on top of that
// renders per-cell occupancy of the cell-list spatial grid (each non-empty cell shaded
// by how many birds it currently holds). All gizmo code lives in the Editor asmdef so
// the runtime build doesn't drag in UnityEditor types.

using Bird_behiviour.Flocking.Simulation;
using Bird_behiviour.Flocking.Spatial;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Bird_behiviour.Flocking.Editor
{
    /// <summary>
    /// Editor-only gizmo drawer for <see cref="FlockWorld"/>. Renders:
    /// <list type="bullet">
    /// <item><description>The world-bounds wireframe AABB (white).</description></item>
    /// <item><description>Per-cell occupancy of the cell-list spatial grid: each non-empty
    /// cell drawn as a translucent cube whose alpha + hue scale with the cell's bird
    /// count (cool → warm). Capped at <see cref="MaxCellsToDraw"/> cells per frame so
    /// huge grids don't tank the Scene-view framerate.</description></item>
    /// </list>
    /// Both passes only fire when the FlockWorld GameObject is selected — see
    /// <see cref="GizmoType.Selected"/> | <see cref="GizmoType.InSelectionHierarchy"/>.
    /// </summary>
    /// <remarks>
    /// The cell-grid pass reads <see cref="FlockWorld.SpatialIndex"/> directly. Outside
    /// PlayMode (or before any flock has been registered) the index is null / unallocated
    /// and only the world-bounds outline is drawn.
    /// </remarks>
    internal static class FlockWorldGizmos
    {
        // Hard cap on the number of non-empty cells we'll draw per frame. The Scene
        // view re-evaluates gizmos every paint, so an N-cell grid means N draw calls;
        // 4096 is enough to visualise dense regions without freezing the editor on
        // worst-case configurations (e.g. 100×100×100 grid = 1M cells).
        private const int MaxCellsToDraw = 4096;

        [DrawGizmo(GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawWorldGizmos(FlockWorld world, GizmoType type)
        {
            if (world == null) return;

            // ── World-bounds AABB ────────────────────────────────────────────────────
            Color prev = Gizmos.color;
            Gizmos.color = Color.white;
            Gizmos.DrawWireCube((Vector3)(float3)world.WorldBoundsCenter,
                                (Vector3)(float3)world.WorldBoundsExtents * 2f);

            // ── Spatial-grid occupancy ───────────────────────────────────────────────
            // Skip silently if the grid hasn't been allocated yet (no flock registered,
            // EditMode without a Tick, etc.) — the bounds outline is still informative.
            CellListSpatialIndex spatial = world.SpatialIndex;
            if (spatial != null && spatial.IsAllocated)
            {
                DrawSpatialGridOccupancy(spatial);
            }

            Gizmos.color = prev;
        }

        private static void DrawSpatialGridOccupancy(CellListSpatialIndex spatial)
        {
            NativeArray<int>.ReadOnly offsets = spatial.CellOffsetReadOnly;
            int totalCells = spatial.CellsPerAxis.x * spatial.CellsPerAxis.y * spatial.CellsPerAxis.z;
            // CellOffset length = totalCells + 1 when allocated; defensive guard avoids an
            // IndexOutOfRange if somehow we land here while the build is mid-flight (the
            // Scene view paints on the main thread but a Tick scheduled from LateUpdate
            // could in theory race a domain reload).
            if (offsets.Length < totalCells + 1)
            {
                return;
            }

            float3 cellSize = new float3(spatial.CellSize, spatial.CellSize, spatial.CellSize);
            float3 cellHalf = cellSize * 0.5f;
            float3 boundsMin = spatial.BoundsMin;
            int3 cpa = spatial.CellsPerAxis;

            // First pass: discover the maximum occupancy for colour normalisation. Cheap
            // (a single sweep over int offsets) and we cap the loop at the same per-frame
            // limit we'll use for drawing so very large grids don't pay an O(n) tax twice.
            int maxOccupancy = 1;
            for (int i = 0; i < totalCells; i++)
            {
                int occ = offsets[i + 1] - offsets[i];
                if (occ > maxOccupancy) maxOccupancy = occ;
            }
            float invMax = 1f / maxOccupancy;

            int drawn = 0;
            for (int z = 0; z < cpa.z && drawn < MaxCellsToDraw; z++)
            {
                for (int y = 0; y < cpa.y && drawn < MaxCellsToDraw; y++)
                {
                    for (int x = 0; x < cpa.x && drawn < MaxCellsToDraw; x++)
                    {
                        int idx = (z * cpa.y + y) * cpa.x + x;
                        int occupancy = offsets[idx + 1] - offsets[idx];
                        if (occupancy <= 0) continue;

                        float t = occupancy * invMax;
                        // Cool (cyan, low) → warm (red, high). Alpha scales with t but never
                        // drops below 0.08 so a single-bird cell is still faintly visible.
                        Color c = Color.Lerp(new Color(0.2f, 0.8f, 1.0f), new Color(1.0f, 0.3f, 0.2f), t);
                        c.a = math.lerp(0.08f, 0.55f, t);
                        Gizmos.color = c;

                        float3 center = boundsMin + new float3(x, y, z) * cellSize + cellHalf;
                        Gizmos.DrawCube((Vector3)center, (Vector3)cellSize * 0.95f);
                        drawn++;
                    }
                }
            }
        }
    }
}
