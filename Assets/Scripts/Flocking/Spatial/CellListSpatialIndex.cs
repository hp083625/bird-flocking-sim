// CellListSpatialIndex.cs — concrete ISpatialIndex implementation backed by a
// SpatialHashGrid + the three-pass BuildGridJob. Owned by FlockWorld; allocated and
// re-allocated on registration (when world bounds, max perception radius, or bird count
// change). Disposed by FlockWorld.OnDestroy.

using Bird_behiviour.Flocking.Core;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Spatial
{
    /// <summary>
    /// Cell-list backed implementation of <see cref="ISpatialIndex"/>. Owns a
    /// <see cref="SpatialHashGrid"/> and rebuilds it each frame via the three-pass
    /// counting-sort <see cref="BuildGridJob"/>.
    /// </summary>
    /// <remarks>
    /// <b>Lifecycle.</b> Construct once per <c>FlockWorld</c>. Call <see cref="Resize"/>
    /// whenever the world's bounds, the max perception radius across registered flocks,
    /// or the total bird capacity changes; the underlying <see cref="SpatialHashGrid"/>
    /// re-allocates its persistent NativeArrays. Call <see cref="Dispose"/> on teardown.
    /// <para/>
    /// <b>Per-frame use.</b> <see cref="ScheduleBuild"/> chains the three-pass build off
    /// the supplied dependency. After completion, <see cref="AsReadOnly"/> hands out a
    /// <see cref="SpatialIndexReadOnly"/> for steering / query code.
    /// </remarks>
    public sealed class CellListSpatialIndex : ISpatialIndex, System.IDisposable
    {
        private SpatialHashGrid grid;

        /// <summary>True iff the underlying grid has been allocated to non-zero dimensions.</summary>
        public bool IsAllocated => grid.IsCreated;

        /// <summary>Current cell edge length (0 if not allocated).</summary>
        public float CellSize => grid.IsCreated ? grid.CellSize : 0f;

        /// <summary>Current per-axis cell count (zero if not allocated).</summary>
        public int3 CellsPerAxis => grid.IsCreated ? grid.CellsPerAxis : int3.zero;

        /// <summary>Bird capacity the cellBirds storage was sized for.</summary>
        public int BirdCapacity => grid.IsCreated ? grid.BirdCapacity : 0;

        /// <summary>
        /// Allocates (or re-allocates) the underlying <see cref="SpatialHashGrid"/> for
        /// the supplied world dimensions and bird capacity. Idempotent — if the requested
        /// dimensions match the current allocation, this is a no-op.
        /// </summary>
        /// <param name="worldCenter">World AABB centre (from <c>IFlockWorldSettings</c>).</param>
        /// <param name="worldExtents">World AABB half-extents.</param>
        /// <param name="cellSize">Cell edge length (max <c>PerceptionRadius</c> across registered flocks).</param>
        /// <param name="birdCapacity">Total bird capacity.</param>
        public void Resize(float3 worldCenter, float3 worldExtents, float cellSize, int birdCapacity)
        {
            float clampedCellSize = math.max(cellSize, 1e-3f);
            float3 boundsMin = worldCenter - worldExtents;
            float3 boundsSize = worldExtents * 2f;
            int3 cellsPerAxis = math.max(new int3(1, 1, 1),
                                         (int3)math.ceil(boundsSize / clampedCellSize));

            if (grid.IsCreated &&
                grid.BirdCapacity == birdCapacity &&
                math.all(grid.CellsPerAxis == cellsPerAxis) &&
                math.all(grid.BoundsMin == boundsMin) &&
                math.abs(grid.CellSize - clampedCellSize) < 1e-6f)
            {
                // Same dimensions — keep the existing allocation.
                return;
            }

            grid.Allocate(boundsMin, cellsPerAxis, clampedCellSize, birdCapacity);
        }

        /// <inheritdoc/>
        public JobHandle ScheduleBuild(
            NativeArray<float3>.ReadOnly positions,
            int count,
            JobHandle deps)
        {
            if (!grid.IsCreated || count <= 0)
            {
                return deps;
            }
            return BuildGridJob.Schedule(grid, positions, count, deps);
        }

        /// <inheritdoc/>
        public SpatialIndexReadOnly AsReadOnly()
        {
            if (!grid.IsCreated)
            {
                // Return a default-empty view; CellCount == 0 is the caller's signal that
                // the grid isn't built. GetNeighbors on a default view yields no birds.
                return default;
            }
            return grid.AsReadOnly();
        }

        /// <summary>Releases the underlying <see cref="SpatialHashGrid"/>'s persistent allocations.</summary>
        public void Dispose()
        {
            grid.Dispose();
        }

        // ── Gizmo / tooling readback (Slice 11) ──────────────────────────────────────
        //
        // World-space minimum corner of the grid AABB. Editor-only gizmo drawers use this
        // alongside <see cref="CellsPerAxis"/> + <see cref="CellSize"/> to position cell
        // wireframes. Returns float3.zero when the grid isn't allocated.
        /// <summary>World-space min corner of the grid AABB; <c>float3.zero</c> if unallocated.</summary>
        public float3 BoundsMin => grid.IsCreated ? grid.BoundsMin : float3.zero;

        /// <summary>
        /// Read-only view of the per-cell offset array (length = TotalCells + 1). Empty
        /// slice when the grid isn't allocated. Each cell <c>i</c>'s occupancy is
        /// <c>CellOffset[i+1] - CellOffset[i]</c>. Intended for editor-only gizmo / HUD
        /// readers — do <em>not</em> hold this view across a <see cref="ScheduleBuild"/>.
        /// </summary>
        public NativeArray<int>.ReadOnly CellOffsetReadOnly =>
            grid.IsCreated ? grid.CellOffset.AsReadOnly() : default;
    }
}
