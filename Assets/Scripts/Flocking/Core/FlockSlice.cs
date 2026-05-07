// FlockSlice.cs — a flock's contiguous range within FlockWorld's flat per-bird arrays.
// Defined in Core so every other module (Simulation, Spatial, Behaviors, Rendering, Tooling, Tests)
// can talk about a flock's index range without touching the concrete FlockManager type.

namespace Bird_behiviour.Flocking.Core
{
    /// <summary>
    /// Identifies a flock's contiguous slice inside <c>FlockWorld</c>'s flat per-bird arrays
    /// (positions, velocities, flockIds, matrices, …). All birds belonging to the flock with
    /// id <see cref="FlockId"/> live at indices <c>[StartIndex, StartIndex + Count)</c> in those
    /// arrays. Slices never overlap; the union of all registered slices covers <c>[0, totalBirdCount)</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="FlockSlice"/> is an immutable, blittable, Burst-friendly value type — it can be
    /// passed by value into jobs and stored in <c>NativeArray&lt;FlockSlice&gt;</c>. It contains
    /// no managed references and no behaviour; it is purely a metadata record.
    /// </remarks>
    public readonly struct FlockSlice
    {
        /// <summary>First bird index belonging to this flock (inclusive).</summary>
        public readonly int StartIndex;

        /// <summary>Number of consecutive birds in the flock starting at <see cref="StartIndex"/>.</summary>
        public readonly int Count;

        /// <summary>
        /// Stable per-flock identifier in <c>[0, 255]</c>, assigned by <c>FlockWorld</c> at
        /// registration time. Stored alongside each bird in the world's <c>NativeArray&lt;byte&gt;</c>
        /// flock-id array so steering jobs can branch on in-flock vs out-of-flock weights without
        /// needing to know slice metadata.
        /// </summary>
        public readonly byte FlockId;

        /// <summary>
        /// Constructs a new <see cref="FlockSlice"/>. Intended to be called by <c>FlockWorld</c>
        /// when a <c>FlockManager</c> registers; consumer code should treat instances as opaque.
        /// </summary>
        /// <param name="startIndex">First bird index (must be ≥ 0).</param>
        /// <param name="count">Number of birds in the slice (must be ≥ 0).</param>
        /// <param name="flockId">Unique flock id in <c>[0, 255]</c>.</param>
        public FlockSlice(int startIndex, int count, byte flockId)
        {
            StartIndex = startIndex;
            Count = count;
            FlockId = flockId;
        }
    }
}
