// NeighborEnumerator.cs — Burst-friendly struct enumerator returned by SpatialIndexReadOnly
// for iterating bird indices within a 27-cell (3×3×3) neighbourhood of a query position.
//
// Slice 2 only DEFINES the contract; a real implementation lands in Slice 3 (M2 Spatial).
// For now MoveNext throws, which is fine because Slice 2's naive O(n²) steering never
// touches the spatial index — it iterates the whole population directly.

using System;
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
    /// <b>Slice 2 status:</b> placeholder only. The real implementation lands in Slice 3
    /// (M2 Spatial). Calling <see cref="MoveNext"/> in Slice 2 throws
    /// <see cref="NotImplementedException"/>; nothing in Slice 2's naive O(n²) steering
    /// path ever invokes it.
    /// </remarks>
    public struct NeighborEnumerator
    {
        /// <summary>The bird index at the current enumeration position.</summary>
        public int Current { get; private set; }

        /// <summary>
        /// Advances to the next neighbour. Returns <c>false</c> when the 27-cell range
        /// has been fully traversed.
        /// </summary>
        /// <exception cref="NotImplementedException">
        /// Always, in Slice 2 — the spatial grid is implemented in Slice 3. Slice 2's
        /// naive steering path does not call this method.
        /// </exception>
        public bool MoveNext()
        {
            throw new NotImplementedException(
                "NeighborEnumerator is a Slice 2 placeholder; the spatial grid lands in Slice 3 (M2).");
        }

        /// <summary>Resets the enumerator to its initial state.</summary>
        /// <exception cref="NotImplementedException">Always, in Slice 2 — see <see cref="MoveNext"/>.</exception>
        public void Reset()
        {
            throw new NotImplementedException(
                "NeighborEnumerator is a Slice 2 placeholder; the spatial grid lands in Slice 3 (M2).");
        }
    }
}
