// IFlockWorldSettings.cs — world-scope tuning surface implemented directly by FlockWorld.

using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Core
{
    /// <summary>
    /// World-scope tuning surface (one instance per scene). Implemented directly by the
    /// <c>FlockWorld</c> MonoBehaviour via serialized fields. Distinct from
    /// <see cref="IFlockSettings"/> which is per-flock.
    /// </summary>
    /// <remarks>
    /// Consumers (steering jobs, gizmo drawers, tests) take an <see cref="IFlockWorldSettings"/>
    /// reference rather than a concrete <c>FlockWorld</c> reference so they can be exercised
    /// in isolation against in-memory stubs.
    /// </remarks>
    public interface IFlockWorldSettings
    {
        /// <summary>Centre of the hard world-bounds AABB, in world space.</summary>
        float3 WorldBoundsCenter { get; }

        /// <summary>
        /// Half-extents of the hard world-bounds AABB, in world space. Steering pushes birds
        /// inward sharply when they exceed these extents (no bird ever escapes for long).
        /// </summary>
        float3 WorldBoundsExtents { get; }

        /// <summary>Strength of the inward steer applied when a bird is outside <see cref="WorldBoundsExtents"/>.</summary>
        float WorldBoundsWeight { get; }

        /// <summary>
        /// Upper bound on simulation <c>dt</c> per tick. Frame stutters that exceed this are
        /// clamped to prevent birds from tunneling through bounds. Default is <c>1/30 s</c>.
        /// </summary>
        float MaxSimDt { get; }

        /// <summary>
        /// Multiplier on simulation time. <c>1.0</c> = real-time; values &lt; 1 produce slow-mo
        /// for demos and inspection; <c>0</c> pauses the simulation.
        /// </summary>
        float SimSpeedMultiplier { get; }
    }
}
