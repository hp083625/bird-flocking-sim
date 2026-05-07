// FlockKernelSettings.cs — Burst-blittable per-flock parameter pack consumed by the
// Slice 4 (M3) steering jobs. FlockWorld builds a NativeArray<FlockKernelSettings>
// each Tick (sized = registered flock count) via SteeringJobGraph.BuildKernelSettings,
// reading from each flock's IFlockSettings, and the jobs index into that array via
// flockIds[i].
//
// Public because it appears in SteeringJobGraph's public DispatchSpec; FlockWorld
// (in the Simulation asmdef) needs to construct + dispose it each tick.
//
// NOTE: Burst-friendly value type. No managed references. No float4x4. No object
// references. All fields are blittable so the jobs can be [BurstCompile]d safely.

using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Behaviors
{
    /// <summary>
    /// Burst-blittable snapshot of one flock's tunable values, flattened from
    /// <see cref="Bird_behiviour.Flocking.Core.IFlockSettings"/> at the start of every
    /// <c>FlockWorld.Tick</c>. The Slice 4 steering jobs consume a
    /// <c>NativeArray&lt;FlockKernelSettings&gt;</c> indexed by <c>flockIds[i]</c> so that
    /// every property read in the job's hot loop is a struct field load (Burst-friendly)
    /// instead of an interface call (not Burst-friendly).
    /// </summary>
    /// <remarks>
    /// Layout target: keep fields close so a single per-bird load fits in a couple of
    /// cache lines. We do not pack to ≤64 bytes — the natural size is ~96 bytes which is
    /// still cheap given each entry is read O(neighbour-count) times per frame and the
    /// flock count is tiny (≤256).
    /// <para/>
    /// Per-flock weights all come from <b>self's</b> settings (per FLOCKING_PLAN.md §4 —
    /// cross-flock weights are "applied uniformly to all OTHER flocks"); only the in-flock
    /// vs out-of-flock branch needs to inspect the neighbour's flock id.
    /// </remarks>
    public readonly struct FlockKernelSettings
    {
        // ── Self-flock weights ───────────────────────────────────────────────────────
        public readonly float InSeparationWeight;
        public readonly float InAlignmentWeight;
        public readonly float InCohesionWeight;

        // ── Cross-flock weights (binary self-vs-other) ───────────────────────────────
        public readonly float OutSeparationWeight;
        public readonly float OutAlignmentWeight;
        public readonly float OutCohesionWeight;

        // ── Bounds (per-flock soft preferred zone) ───────────────────────────────────
        public readonly float3 PreferredCenter;
        public readonly float3 PreferredExtents;
        public readonly float  PreferredAttractionWeight;

        // ── Perception ───────────────────────────────────────────────────────────────
        public readonly float PerceptionRadius;
        public readonly float SeparationRadius;
        /// <summary>Pre-computed <c>cos(perceptionConeHalfAngleRadians)</c> so the hot loop avoids a trig call.</summary>
        public readonly float PerceptionConeCos;

        // ── Motion ───────────────────────────────────────────────────────────────────
        public readonly float MinSpeed;
        public readonly float MaxSpeed;
        public readonly float MaxAcceleration;

        // ── Cursor reaction (signed strength, always-on) ─────────────────────────────
        public readonly float CursorReactionStrength;
        public readonly float CursorReactionRadius;

        /// <summary>Builds a kernel-settings snapshot from a managed <see cref="Bird_behiviour.Flocking.Core.IFlockSettings"/>.</summary>
        public FlockKernelSettings(Bird_behiviour.Flocking.Core.IFlockSettings s)
        {
            InSeparationWeight  = s.InSeparationWeight;
            InAlignmentWeight   = s.InAlignmentWeight;
            InCohesionWeight    = s.InCohesionWeight;

            OutSeparationWeight = s.OutSeparationWeight;
            OutAlignmentWeight  = s.OutAlignmentWeight;
            OutCohesionWeight   = s.OutCohesionWeight;

            PreferredCenter           = s.PreferredCenter;
            PreferredExtents          = s.PreferredExtents;
            PreferredAttractionWeight = s.PreferredAttractionWeight;

            PerceptionRadius  = s.PerceptionRadius;
            SeparationRadius  = s.SeparationRadius;
            PerceptionConeCos = math.cos(s.PerceptionConeHalfAngleRadians);

            MinSpeed        = s.MinSpeed;
            MaxSpeed        = s.MaxSpeed;
            MaxAcceleration = s.MaxAcceleration;

            CursorReactionStrength = s.CursorReactionStrength;
            CursorReactionRadius   = s.CursorReactionRadius;
        }
    }
}
