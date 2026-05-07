// IFlockSettings.cs — stable contract surface that any per-flock tuning asset must implement.
// FlockSettings : ScriptableObject in M5 implements this; tests stub it with an in-memory
// POCO. The interface is the only thing simulation/behaviors/rendering ever consume.

using Unity.Mathematics;
using UnityEngine;

namespace Bird_behiviour.Flocking.Core
{
    /// <summary>
    /// Per-flock tuning surface consumed by every other module. The concrete authoring
    /// type (a <c>ScriptableObject</c> in production, a stub class in tests) is irrelevant
    /// to consumers — only the property values matter.
    /// </summary>
    /// <remarks>
    /// Properties are partitioned into the same logical groups used by the M5 inspector:
    /// self-flock weights, cross-flock weights, bounds, perception, motion, cursor,
    /// and visual / lifecycle. <c>ScriptableObject</c> implementers should validate
    /// invariants in <c>OnValidate</c> (weights ≥ 0, <see cref="SeparationRadius"/> ≤
    /// <see cref="PerceptionRadius"/>, etc.) — this interface intentionally does not enforce
    /// them so that test stubs remain trivial.
    /// </remarks>
    public interface IFlockSettings
    {
        // ── Self-flock weights ────────────────────────────────────────────────────────

        /// <summary>Weight applied to separation force from neighbours within the same flock.</summary>
        float InSeparationWeight { get; }

        /// <summary>Weight applied to alignment force from neighbours within the same flock.</summary>
        float InAlignmentWeight { get; }

        /// <summary>Weight applied to cohesion force from neighbours within the same flock.</summary>
        float InCohesionWeight { get; }

        // ── Cross-flock weights (binary self-vs-other) ────────────────────────────────

        /// <summary>Weight applied to separation force from neighbours in any other flock.</summary>
        float OutSeparationWeight { get; }

        /// <summary>Weight applied to alignment force from neighbours in any other flock.</summary>
        float OutAlignmentWeight { get; }

        /// <summary>Weight applied to cohesion force from neighbours in any other flock.</summary>
        float OutCohesionWeight { get; }

        // ── Bounds (per-flock soft preferred zone) ────────────────────────────────────

        /// <summary>Centre of the per-flock soft preferred zone, in world space.</summary>
        float3 PreferredCenter { get; }

        /// <summary>
        /// Half-extents of the per-flock soft preferred zone, in world space.
        /// Birds spawn inside this box and are gently pulled back to <see cref="PreferredCenter"/>
        /// when they drift away (strength scaled by <see cref="PreferredAttractionWeight"/>).
        /// </summary>
        float3 PreferredExtents { get; }

        /// <summary>Strength of the gentle pull toward <see cref="PreferredCenter"/>.</summary>
        float PreferredAttractionWeight { get; }

        // ── Perception ────────────────────────────────────────────────────────────────

        /// <summary>Radius within which neighbours influence steering, in world units.</summary>
        float PerceptionRadius { get; }

        /// <summary>
        /// Radius within which separation force kicks in (must be ≤ <see cref="PerceptionRadius"/>).
        /// </summary>
        float SeparationRadius { get; }

        /// <summary>
        /// Half-angle of the forward-facing perception cone, in radians (≈ 2.36 = 135° default).
        /// Neighbours outside the cone are ignored. With zero velocity the cone test falls back
        /// to a full 360° sphere.
        /// </summary>
        float PerceptionConeHalfAngleRadians { get; }

        // ── Motion ────────────────────────────────────────────────────────────────────

        /// <summary>Lower bound on bird speed; velocities below this magnitude are clamped up.</summary>
        float MinSpeed { get; }

        /// <summary>Upper bound on bird speed; velocities above this magnitude are clamped down.</summary>
        float MaxSpeed { get; }

        /// <summary>Maximum acceleration magnitude per integration step.</summary>
        float MaxAcceleration { get; }

        // ── Cursor reaction (signed strength, always-on) ──────────────────────────────

        /// <summary>
        /// Signed strength of the cursor reaction: positive attracts toward the cursor,
        /// negative repels from it, zero ignores the cursor entirely.
        /// </summary>
        float CursorReactionStrength { get; }

        /// <summary>
        /// Falloff radius for cursor influence. Birds farther than this distance from
        /// the cursor world-point feel no cursor force.
        /// </summary>
        float CursorReactionRadius { get; }

        // ── Visual + lifecycle ────────────────────────────────────────────────────────

        /// <summary>Number of birds in this flock. Fixed at scene init (no runtime spawn / despawn in v1).</summary>
        int BirdCount { get; }

        /// <summary>Per-flock instanced mesh. Designers may override the procedural cone built by Rendering.</summary>
        Mesh BirdMesh { get; }

        /// <summary>Per-flock instanced material. Must have GPU instancing enabled in its inspector.</summary>
        Material BirdMaterial { get; }

        /// <summary>
        /// RNG seed for spawn placement and any other per-flock randomness.
        /// A value of <c>0</c> means "auto-derive from <c>Time.realtimeSinceStartup</c>" so each
        /// run is unique; any non-zero value pins the seed for deterministic reproduction.
        /// </summary>
        uint RandomSeed { get; }
    }
}
