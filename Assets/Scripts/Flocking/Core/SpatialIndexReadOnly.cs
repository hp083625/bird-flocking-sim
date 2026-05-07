// SpatialIndexReadOnly.cs — Burst-compatible read view of the cell-list spatial grid.
// Slice 3 (M2) implementation: holds NativeArray.ReadOnly views of the cellOffset and
// cellBirds arrays plus the grid metadata needed to map a world-space query position
// onto its 27-cell (3×3×3) neighbourhood.

using Unity.Collections;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Core
{
    /// <summary>
    /// Burst-compatible value-type read view of the cell-list spatial grid built each frame
    /// by <see cref="ISpatialIndex"/>. Captured by jobs as a <c>[ReadOnly]</c> field and used
    /// to enumerate neighbour candidates for steering forces.
    /// </summary>
    /// <remarks>
    /// Holds <see cref="NativeArray{T}.ReadOnly"/> views of the spatial grid's
    /// <c>cellOffset</c> and <c>cellBirds</c> arrays plus the grid metadata
    /// (<c>boundsMin</c>, <c>cellsPerAxis</c>, <c>cellSize</c>). Cheap to copy by value.
    /// <para/>
    /// Construct via <see cref="ISpatialIndex.AsReadOnly"/>; consumers should not allocate
    /// these directly.
    /// </remarks>
    public readonly struct SpatialIndexReadOnly
    {
        // Internal fields holding the read-only NativeArray views and grid metadata.
        // Marked internal to discourage user code from poking at them directly while
        // still allowing the Spatial module's tests/diagnostics to inspect when needed.
        internal readonly NativeArray<int>.ReadOnly CellOffset;
        internal readonly NativeArray<int>.ReadOnly CellBirds;
        internal readonly float3 BoundsMin;
        internal readonly int3   CellsPerAxis;
        internal readonly float  CellSizeValue;
        internal readonly float  InvCellSize;

        /// <summary>Number of cells in the grid (product of per-axis cell counts).</summary>
        public int CellCount => CellsPerAxis.x * CellsPerAxis.y * CellsPerAxis.z;

        /// <summary>
        /// Edge length of one grid cell, in world units. Auto-derived by
        /// <c>FlockWorld.RegisterFlock</c> as <c>max(perceptionRadius)</c> across all flocks.
        /// </summary>
        public float CellSize => CellSizeValue;

        /// <summary>
        /// Constructs a read-only view of a built spatial grid. Called by the Spatial
        /// module's <c>SpatialHashGrid.AsReadOnly</c>; consumer code should not call this
        /// directly.
        /// </summary>
        /// <param name="cellOffset">Read-only view of the per-cell offset array (length = totalCells + 1).</param>
        /// <param name="cellBirds">Read-only view of the bird-index array (length ≥ totalBirds).</param>
        /// <param name="boundsMin">World-space minimum corner of the grid AABB.</param>
        /// <param name="cellsPerAxis">Cells along each axis (each component ≥ 1).</param>
        /// <param name="cellSize">Edge length of one cell, world units (must be &gt; 0).</param>
        public SpatialIndexReadOnly(
            NativeArray<int>.ReadOnly cellOffset,
            NativeArray<int>.ReadOnly cellBirds,
            float3 boundsMin,
            int3 cellsPerAxis,
            float cellSize)
        {
            CellOffset    = cellOffset;
            CellBirds     = cellBirds;
            BoundsMin     = boundsMin;
            CellsPerAxis  = cellsPerAxis;
            CellSizeValue = cellSize;
            InvCellSize   = cellSize > 0f ? 1f / cellSize : 0f;
        }

        /// <summary>
        /// Returns an enumerator over bird indices in the 27 cells (3×3×3) surrounding
        /// the cell containing <paramref name="queryPosition"/>. Use with
        /// <c>while (e.MoveNext()) { var i = e.Current; … }</c>.
        /// </summary>
        /// <param name="queryPosition">World-space position to look up.</param>
        public NeighborEnumerator GetNeighbors(float3 queryPosition)
        {
            int3 centerCell = (int3)math.floor((queryPosition - BoundsMin) * InvCellSize);
            return new NeighborEnumerator(CellOffset, CellBirds, centerCell, CellsPerAxis);
        }
    }
}
