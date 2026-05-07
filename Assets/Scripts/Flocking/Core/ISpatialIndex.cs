// ISpatialIndex.cs — contract for the cell-list spatial grid built each frame from bird positions.
// Implemented in M2 Spatial; consumed by M3 Behaviors (NeighborForcesJob) and FlockWorld.
//
// Slice 2 only DEFINES the contract; the implementation lands in Slice 3. Slice 2's naive
// O(n²) steering bypasses ISpatialIndex entirely.

using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Core
{
    /// <summary>
    /// Cell-list spatial grid over the per-bird position array. Built every frame in three
    /// passes (count → prefix sum → scatter) and queried by steering jobs via
    /// <see cref="SpatialIndexReadOnly"/>.
    /// </summary>
    /// <remarks>
    /// Implemented by the M2 Spatial module; consumers reference the interface (in Core)
    /// so the rest of the codebase never takes a build-time dependency on the concrete
    /// grid type. Cell size is auto-derived by <c>FlockWorld.RegisterFlock</c> from the
    /// maximum <c>PerceptionRadius</c> across all registered flocks.
    /// <para/>
    /// <b>Slice 2 status:</b> contract defined; no production implementation yet. Slice 2's
    /// naive O(n²) steering does not take an <see cref="ISpatialIndex"/> dependency.
    /// </remarks>
    public interface ISpatialIndex
    {
        /// <summary>
        /// Schedules the 3-pass build of the cell-list grid for the supplied positions.
        /// </summary>
        /// <param name="positions">Read-only view of the world's per-bird position array.</param>
        /// <param name="count">Number of valid bird entries in <paramref name="positions"/>.</param>
        /// <param name="deps">JobHandle to chain after (typically the previous frame's render dep).</param>
        /// <returns>A JobHandle that completes when the grid is fully built and ready to query.</returns>
        JobHandle ScheduleBuild(
            NativeArray<float3>.ReadOnly positions,
            int count,
            JobHandle deps);

        /// <summary>
        /// Returns a Burst-friendly read view that can be captured into other jobs as a
        /// <c>[ReadOnly]</c> field. Must only be called once <see cref="ScheduleBuild"/>
        /// has completed (or after the returned handle is forced).
        /// </summary>
        SpatialIndexReadOnly AsReadOnly();
    }
}
