// FlockState.cs — read-only world snapshot handed to renderers, tests, and any external
// consumer that wants a coherent view of the entire bird population for the current frame.

using Unity.Collections;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Core
{
    /// <summary>
    /// Immutable read-only view of the entire bird population in <c>FlockWorld</c> for the
    /// current simulation frame. Aggregates positions, velocities, flock ids, and per-flock
    /// slice metadata so consumers (renderers, tests, gizmo drawers) can read the world
    /// without taking a write dependency on <c>FlockWorld</c>'s internal arrays.
    /// </summary>
    /// <remarks>
    /// All four <see cref="NativeArray{T}.ReadOnly"/> fields share the same indexing space:
    /// for any bird index <c>i</c> in <c>[0, Count)</c>, <c>Positions[i]</c>, <c>Velocities[i]</c>,
    /// and <c>FlockIds[i]</c> describe the same bird. The <see cref="Slices"/> array maps
    /// flock id → range and is sized to the number of currently registered flocks.
    /// <para/>
    /// <see cref="FlockState"/> is a Burst-compatible <c>readonly struct</c>. It is cheap to
    /// pass by value and is intended to be captured into job structs as a <c>[ReadOnly]</c>
    /// field. The struct does not own its native arrays — disposal remains the responsibility
    /// of whoever allocated them (typically <c>FlockWorld</c>).
    /// </remarks>
    public readonly struct FlockState
    {
        /// <summary>World-space positions, one per bird, indexed in <c>[0, Count)</c>.</summary>
        public readonly NativeArray<float3>.ReadOnly Positions;

        /// <summary>World-space velocities, one per bird, indexed in <c>[0, Count)</c>.</summary>
        public readonly NativeArray<float3>.ReadOnly Velocities;

        /// <summary>
        /// Per-bird flock identifier; matches the <c>FlockId</c> on the corresponding
        /// <see cref="FlockSlice"/> entry in <see cref="Slices"/>.
        /// </summary>
        public readonly NativeArray<byte>.ReadOnly FlockIds;

        /// <summary>
        /// Slice metadata for every flock currently registered with <c>FlockWorld</c>. The
        /// union of all slices covers <c>[0, Count)</c> with no overlaps.
        /// </summary>
        public readonly NativeArray<FlockSlice>.ReadOnly Slices;

        /// <summary>Total bird count across all registered flocks (sum of every slice's <c>Count</c>).</summary>
        public readonly int Count;

        /// <summary>
        /// Constructs a <see cref="FlockState"/> view over already-allocated native arrays.
        /// The struct stores read-only handles to the arrays; it does not copy or own them.
        /// </summary>
        /// <param name="positions">World-space positions, one per bird.</param>
        /// <param name="velocities">World-space velocities, one per bird.</param>
        /// <param name="flockIds">Per-bird flock id matching one of <paramref name="slices"/>.</param>
        /// <param name="slices">Slice metadata for each registered flock.</param>
        /// <param name="count">Total bird count (must equal <c>positions.Length</c>).</param>
        public FlockState(
            NativeArray<float3>.ReadOnly positions,
            NativeArray<float3>.ReadOnly velocities,
            NativeArray<byte>.ReadOnly flockIds,
            NativeArray<FlockSlice>.ReadOnly slices,
            int count)
        {
            Positions = positions;
            Velocities = velocities;
            FlockIds = flockIds;
            Slices = slices;
            Count = count;
        }
    }
}
