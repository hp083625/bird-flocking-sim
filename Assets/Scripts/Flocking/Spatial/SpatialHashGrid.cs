// SpatialHashGrid.cs — cell-list spatial grid backing storage for the Spatial module (M2).
// Three NativeArrays form the counting-sort cell list:
//   cellCount [totalCells]      — how many birds fall in each cell (Pass 1 output).
//   cellOffset[totalCells + 1]  — exclusive prefix sum of cellCount; cell i owns
//                                 cellBirds[cellOffset[i] .. cellOffset[i+1]).
//   cellBirds [birdCapacity]    — bird indices, grouped by cell (Pass 3 output).
//
// All three are Allocator.Persistent and re-allocated whenever the grid dimensions
// (cellSize, world bounds, bird capacity) change. The grid is owned by
// CellListSpatialIndex; FlockWorld asks the index to (re-)allocate when registration
// changes.

using Bird_behiviour.Flocking.Core;
using Unity.Collections;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Spatial
{
    /// <summary>
    /// Backing storage for the cell-list spatial grid built each frame by
    /// <see cref="BuildGridJob"/> and queried via <see cref="SpatialIndexReadOnly"/>.
    /// Holds three persistent <see cref="NativeArray{T}"/>s plus the grid dimensions
    /// (bounds origin, cells-per-axis, cell size).
    /// </summary>
    /// <remarks>
    /// The grid is sized to fit <see cref="WorldBoundsExtents"/> on each axis using the
    /// auto-derived <see cref="CellSize"/> (max <c>PerceptionRadius</c> across registered
    /// flocks). Birds whose positions fall outside the grid AABB are silently excluded
    /// from the build (they contribute nothing to <c>cellCount</c>); steering will treat
    /// them as having zero neighbours until the next frame moves them back inside.
    /// <para/>
    /// The struct itself is a tiny header — the heavy NativeArrays live behind it. Call
    /// <see cref="Allocate"/> once when dimensions change and <see cref="Dispose"/> on
    /// teardown. Within-frame use: schedule <see cref="BuildGridJob"/> → after completion,
    /// build a <see cref="SpatialIndexReadOnly"/> via <see cref="AsReadOnly"/>.
    /// </remarks>
    internal struct SpatialHashGrid
    {
        /// <summary>World-space minimum corner of the grid's AABB (= world center − extents).</summary>
        public float3 BoundsMin;

        /// <summary>Number of cells along each axis (≥ 1 each when allocated).</summary>
        public int3 CellsPerAxis;

        /// <summary>Edge length of one cell, in world units.</summary>
        public float CellSize;

        /// <summary>Per-cell bird counts. Length = <see cref="TotalCells"/>.</summary>
        public NativeArray<int> CellCount;

        /// <summary>Exclusive prefix-sum of <see cref="CellCount"/>. Length = <see cref="TotalCells"/> + 1.</summary>
        public NativeArray<int> CellOffset;

        /// <summary>Bird indices grouped by cell. Length = <see cref="BirdCapacity"/>.</summary>
        public NativeArray<int> CellBirds;

        /// <summary>Maximum number of birds the <see cref="CellBirds"/> array was sized for.</summary>
        public int BirdCapacity;

        /// <summary>True iff the three NativeArrays have been allocated.</summary>
        public bool IsCreated => CellCount.IsCreated && CellOffset.IsCreated && CellBirds.IsCreated;

        /// <summary>Total cell count = product of <see cref="CellsPerAxis"/>.</summary>
        public int TotalCells => CellsPerAxis.x * CellsPerAxis.y * CellsPerAxis.z;

        /// <summary>
        /// Allocates the three NativeArrays for the supplied grid dimensions. Disposes any
        /// previously-allocated arrays first. Safe to call repeatedly.
        /// </summary>
        /// <param name="boundsMin">World-space minimum corner of the grid AABB.</param>
        /// <param name="cellsPerAxis">Cells along each axis (each component clamped ≥ 1).</param>
        /// <param name="cellSize">Edge length of one cell (must be &gt; 0).</param>
        /// <param name="birdCapacity">Maximum bird count the grid will store (≥ 0).</param>
        public void Allocate(float3 boundsMin, int3 cellsPerAxis, float cellSize, int birdCapacity)
        {
            Dispose();

            BoundsMin    = boundsMin;
            CellsPerAxis = math.max(cellsPerAxis, new int3(1, 1, 1));
            CellSize     = math.max(cellSize, 1e-6f);
            BirdCapacity = math.max(0, birdCapacity);

            int totalCells = CellsPerAxis.x * CellsPerAxis.y * CellsPerAxis.z;
            CellCount  = new NativeArray<int>(totalCells,         Allocator.Persistent, NativeArrayOptions.ClearMemory);
            CellOffset = new NativeArray<int>(totalCells + 1,     Allocator.Persistent, NativeArrayOptions.ClearMemory);
            // Allocator.Persistent rejects zero-length on some Unity versions — pad to 1.
            CellBirds  = new NativeArray<int>(math.max(1, BirdCapacity), Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        /// <summary>Disposes the three NativeArrays (no-op if not allocated).</summary>
        public void Dispose()
        {
            if (CellCount.IsCreated)  CellCount.Dispose();
            if (CellOffset.IsCreated) CellOffset.Dispose();
            if (CellBirds.IsCreated)  CellBirds.Dispose();
        }

        /// <summary>
        /// Returns a Burst-friendly read view of the grid suitable for capture into jobs
        /// or main-thread iteration. Must only be called after <see cref="BuildGridJob"/>
        /// has completed.
        /// </summary>
        public SpatialIndexReadOnly AsReadOnly()
        {
            return new SpatialIndexReadOnly(
                CellOffset.AsReadOnly(),
                CellBirds.AsReadOnly(),
                BoundsMin,
                CellsPerAxis,
                CellSize);
        }
    }
}
