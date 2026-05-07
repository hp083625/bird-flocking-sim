// NeighborEnumerator.cs — Burst-friendly struct enumerator returned by SpatialIndexReadOnly
// for iterating bird indices within a 27-cell (3×3×3) neighbourhood of a query position.
//
// Slice 3 (M2) implementation. Mutable struct, intentionally not IEnumerable<T> — driven
// via hand-written `while (e.MoveNext()) { var i = e.Current; … }` loops so it has zero
// managed allocation cost and Burst can inline the entire iteration.

using Unity.Collections;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Core
{
    /// <summary>
    /// Burst-friendly enumerator over bird indices within a 27-cell (3×3×3) neighbourhood
    /// of a query position. Returned by value from
    /// <see cref="SpatialIndexReadOnly.GetNeighbors(float3)"/>; intended for use with a
    /// hand-written <c>while (e.MoveNext())</c> loop, not <c>foreach</c>.
    /// </summary>
    /// <remarks>
    /// This is deliberately a <c>struct</c> (not a class) and deliberately not
    /// <c>IEnumerable&lt;int&gt;</c> — both choices avoid managed allocations and are
    /// required for Burst-compatibility. <see cref="Current"/> returns a bird index
    /// suitable for indexing into <c>FlockWorld</c>'s flat per-bird arrays.
    /// <para/>
    /// <b>Iteration order.</b> Cells are visited in flat row-major order (z-major within
    /// the 3×3×3 block) and birds within each cell are visited in their
    /// <see cref="SpatialIndexReadOnly.CellBirds"/> storage order. Cells outside the
    /// grid AABB are skipped so the same enumerator works at edge / corner positions
    /// without bounds checks at the call site.
    /// </remarks>
    public struct NeighborEnumerator
    {
        // Read-only views of the underlying spatial grid arrays. Captured by value at
        // construction; safe to store in a struct field — they carry their own safety
        // handles for the Unity job system.
        private readonly NativeArray<int>.ReadOnly cellOffset;
        private readonly NativeArray<int>.ReadOnly cellBirds;

        // Origin of the 3×3×3 block (= centerCell - 1) and per-axis grid dimensions.
        private readonly int3 baseCell;
        private readonly int3 cellsPerAxis;

        // Iteration state.
        private int slotIndex;        // Next 3×3×3 slot to visit, in [0, 27].
        private int birdCursor;       // Next index into cellBirds inside the current cell.
        private int birdSliceEnd;     // Exclusive end of the current cell's slice in cellBirds.

        /// <summary>The bird index at the current enumeration position.</summary>
        public int Current { get; private set; }

        /// <summary>
        /// Constructs a fresh enumerator centred on <paramref name="centerCell"/>. The
        /// 27-cell block spans <c>[centerCell - 1, centerCell + 1]</c> on each axis,
        /// clamped to the grid AABB. Construct via
        /// <see cref="SpatialIndexReadOnly.GetNeighbors(float3)"/>.
        /// </summary>
        public NeighborEnumerator(
            NativeArray<int>.ReadOnly cellOffset,
            NativeArray<int>.ReadOnly cellBirds,
            int3 centerCell,
            int3 cellsPerAxis)
        {
            this.cellOffset   = cellOffset;
            this.cellBirds    = cellBirds;
            this.baseCell     = centerCell - new int3(1, 1, 1);
            this.cellsPerAxis = cellsPerAxis;
            this.slotIndex    = 0;
            this.birdCursor   = 0;
            this.birdSliceEnd = 0;
            Current           = -1;
        }

        /// <summary>
        /// Advances to the next neighbour. Returns <c>false</c> when the 27-cell range
        /// has been fully traversed.
        /// </summary>
        public bool MoveNext()
        {
            // Drain the current cell, then advance to the next non-empty in-bounds cell.
            while (true)
            {
                if (birdCursor < birdSliceEnd)
                {
                    Current = cellBirds[birdCursor];
                    birdCursor++;
                    return true;
                }

                // Advance to the next slot, skipping any cells that lie outside the grid.
                if (!AdvanceToNextNonEmptyCell())
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Advances <see cref="slotIndex"/> through the 27-cell block until it finds an
        /// in-bounds cell that contains at least one bird. Returns false when all 27 slots
        /// have been consumed without finding more birds.
        /// </summary>
        private bool AdvanceToNextNonEmptyCell()
        {
            while (slotIndex < 27)
            {
                int s = slotIndex;
                slotIndex++;

                int dx = s % 3;
                int dy = (s / 3) % 3;
                int dz = s / 9;

                int cx = baseCell.x + dx;
                int cy = baseCell.y + dy;
                int cz = baseCell.z + dz;

                if (cx < 0 || cy < 0 || cz < 0 ||
                    cx >= cellsPerAxis.x || cy >= cellsPerAxis.y || cz >= cellsPerAxis.z)
                {
                    continue;
                }

                int hash = cx + cy * cellsPerAxis.x + cz * cellsPerAxis.x * cellsPerAxis.y;
                int sliceStart = cellOffset[hash];
                int sliceEnd   = cellOffset[hash + 1];

                if (sliceStart >= sliceEnd)
                {
                    continue;
                }

                birdCursor   = sliceStart;
                birdSliceEnd = sliceEnd;
                return true;
            }
            return false;
        }

        /// <summary>Resets the enumerator to its initial state (re-iterates the same 27-cell block).</summary>
        public void Reset()
        {
            slotIndex    = 0;
            birdCursor   = 0;
            birdSliceEnd = 0;
            Current      = -1;
        }
    }
}
