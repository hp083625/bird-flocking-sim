// SpatialIndexReadOnly.cs — Burst-compatible read view of the cell-list spatial grid.
// Slice 2 defines the contract shape only; Slice 3 (M2 Spatial) fleshes out the internals.

using System;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Core
{
    /// <summary>
    /// Burst-compatible value-type read view of the cell-list spatial grid built each frame
    /// by <see cref="ISpatialIndex"/>. Captured by jobs as a <c>[ReadOnly]</c> field and used
    /// to enumerate neighbour candidates for steering forces.
    /// </summary>
    /// <remarks>
    /// <b>Slice 2 status:</b> placeholder only. The struct shape (signature of
    /// <see cref="GetNeighbors(float3)"/>, <see cref="CellCount"/>, <see cref="CellSize"/>)
    /// is the load-bearing contract; the internal cell arrays land in Slice 3 (M2 Spatial).
    /// Slice 2's naive O(n²) steering does not consume <see cref="SpatialIndexReadOnly"/>.
    /// </remarks>
    public readonly struct SpatialIndexReadOnly
    {
        /// <summary>Number of cells in the grid (product of per-axis cell counts).</summary>
        public int CellCount => 0;

        /// <summary>
        /// Edge length of one grid cell, in world units. Auto-derived by
        /// <c>FlockWorld.RegisterFlock</c> as <c>max(perceptionRadius)</c> across all flocks.
        /// </summary>
        public float CellSize => 0f;

        /// <summary>
        /// Returns an enumerator over bird indices in the 27 cells (3×3×3) surrounding
        /// the cell containing <paramref name="queryPosition"/>. Use with
        /// <c>while (e.MoveNext()) { var i = e.Current; … }</c>.
        /// </summary>
        /// <param name="queryPosition">World-space position to look up.</param>
        /// <exception cref="NotImplementedException">
        /// Always, in Slice 2 — the spatial grid lands in Slice 3 (M2). Naive O(n²) steering
        /// in Slice 2 does not call this method.
        /// </exception>
        public NeighborEnumerator GetNeighbors(float3 queryPosition)
        {
            throw new NotImplementedException(
                "SpatialIndexReadOnly.GetNeighbors is a Slice 2 placeholder; implementation lands in Slice 3 (M2).");
        }
    }
}
