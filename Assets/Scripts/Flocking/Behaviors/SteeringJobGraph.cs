// SteeringJobGraph.cs — Slice 4 (M3) main-thread orchestration helper that lives in the
// Behaviors asmdef so FlockWorld can dispatch the four steering jobs without taking a
// dependency on every internal struct.
//
// FlockWorld owns the per-bird arrays + the spatial index; this helper builds the
// per-flock kernel-settings snapshot, schedules the parallel branches, combines
// dependencies, and returns the IntegrateJob handle. Per-frame intermediate arrays
// (AccelNeighbor / AccelBounds / AccelCursor / KernelSettings) use Allocator.TempJob
// and are disposed by the caller once IntegrateJob completes.

using Bird_behiviour.Flocking.Core;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;

namespace Bird_behiviour.Flocking.Behaviors
{
    /// <summary>
    /// Single entry-point invoked by <c>FlockWorld.Tick</c> to dispatch the Slice 4
    /// steering job graph (BuildGrid → Neighbor || Bounds || Cursor → Integrate). The
    /// helper is internal to the Behaviors asmdef; <c>FlockWorld</c> calls
    /// <see cref="Schedule"/> via the public re-export <see cref="Dispatch"/>.
    /// </summary>
    /// <remarks>
    /// <b>Per-frame allocations.</b> Three <c>NativeArray&lt;float3&gt;</c> (per-bird
    /// accel arrays) and one <c>NativeArray&lt;FlockKernelSettings&gt;</c> are
    /// <c>Allocator.TempJob</c>; the caller disposes them after completing the returned
    /// <see cref="JobHandle"/>. None of them count against the managed (Mono) heap, so
    /// the M6-4 allocation regression test holds at zero.
    /// </remarks>
    public static class SteeringJobGraph
    {
        // Profiler markers label the schedule sites so the Unity Profiler timeline can
        // attribute schedule overhead to each branch. Job *execution* time on worker
        // threads is visible natively under the "Jobs" track. M5-5 will wire these
        // markers (or finer-grained Begin/End calls inside the jobs) into the on-screen
        // HUD. Names match FLOCKING_PLAN.md §6 M5-5: "Flock.Neighbor", "Flock.Bounds",
        // "Flock.Cursor", "Flock.Integrate" (plus "Flock.BuildGrid" added in FlockWorld).
        private static readonly ProfilerMarker NeighborMarker  = new ProfilerMarker("Flock.Neighbor");
        private static readonly ProfilerMarker BoundsMarker    = new ProfilerMarker("Flock.Bounds");
        private static readonly ProfilerMarker CursorMarker    = new ProfilerMarker("Flock.Cursor");
        private static readonly ProfilerMarker IntegrateMarker = new ProfilerMarker("Flock.Integrate");

        /// <summary>
        /// Inputs + per-frame buffers handed to <see cref="Dispatch"/>. Caller is
        /// responsible for allocating each <c>NativeArray</c> field at the right size and
        /// disposing the per-frame buffers (<see cref="AccelNeighbor"/>,
        /// <see cref="AccelBounds"/>, <see cref="AccelCursor"/>,
        /// <see cref="KernelSettings"/>) after completing the returned handle.
        /// </summary>
        public struct DispatchSpec
        {
            public NativeArray<float3> Positions;     // R/W: IntegrateJob writes
            public NativeArray<float3> Velocities;    // R/W: IntegrateJob writes
            public NativeArray<float3> Accelerations; // W:    IntegrateJob writes (debug mirror)
            public NativeArray<byte>   FlockIds;      // R only

            public NativeArray<float3> AccelNeighbor; // TempJob, frame-scoped
            public NativeArray<float3> AccelBounds;   // TempJob, frame-scoped
            public NativeArray<float3> AccelCursor;   // TempJob, frame-scoped

            public NativeArray<FlockKernelSettings> KernelSettings; // TempJob, frame-scoped
            public SpatialIndexReadOnly Spatial;

            public float3 WorldBoundsCenter;
            public float3 WorldBoundsExtents;
            public float  WorldBoundsWeight;
            public float  WorldBoundsMargin;

            /// <summary>Slice 7: cursor world-point read by <c>CursorForceJob</c> each tick.</summary>
            public float3 CursorWorldPoint;
            /// <summary>Slice 7: when false, <c>CursorForceJob</c> writes zero for every bird.</summary>
            public bool   CursorOnScreen;

            public int    BirdCount;
            public int    BatchSize;
            public float  Dt;
            public JobHandle GridHandle;
        }

        /// <summary>
        /// Schedules NeighborForcesJob ‖ BoundsForcesJob ‖ CursorForceJob, combines
        /// dependencies, and chains IntegrateJob on the result. Returns the IntegrateJob
        /// handle so the caller can <c>Complete()</c> within the same frame.
        /// </summary>
        public static JobHandle Dispatch(in DispatchSpec spec)
        {
            int n = spec.BirdCount;
            int batch = math.max(1, spec.BatchSize);

            // ── Branch 1: Neighbor (depends on the freshly-built grid) ──────────────
            JobHandle neighborH;
            using (NeighborMarker.Auto())
            {
                neighborH = new NeighborForcesJob
                {
                    Positions      = spec.Positions,
                    Velocities     = spec.Velocities,
                    FlockIds       = spec.FlockIds,
                    KernelSettings = spec.KernelSettings,
                    Spatial        = spec.Spatial,
                    AccelNeighbor  = spec.AccelNeighbor,
                }.Schedule(n, batch, spec.GridHandle);
            }

            // ── Branch 2: Bounds (no grid dep — fully independent) ──────────────────
            JobHandle boundsH;
            using (BoundsMarker.Auto())
            {
                boundsH = new BoundsForcesJob
                {
                    Positions          = spec.Positions,
                    FlockIds           = spec.FlockIds,
                    KernelSettings     = spec.KernelSettings,
                    WorldBoundsCenter  = spec.WorldBoundsCenter,
                    WorldBoundsExtents = spec.WorldBoundsExtents,
                    WorldBoundsWeight  = spec.WorldBoundsWeight,
                    WorldBoundsMargin  = spec.WorldBoundsMargin,
                    AccelBounds        = spec.AccelBounds,
                }.Schedule(n, batch, default);
            }

            // ── Branch 3: Cursor (Slice 7 — real signed-strength impl) ──────────────
            JobHandle cursorH;
            using (CursorMarker.Auto())
            {
                cursorH = new CursorForceJob
                {
                    Positions        = spec.Positions,
                    FlockIds         = spec.FlockIds,
                    KernelSettings   = spec.KernelSettings,
                    CursorWorldPoint = spec.CursorWorldPoint,
                    CursorOnScreen   = spec.CursorOnScreen,
                    AccelCursor      = spec.AccelCursor,
                }.Schedule(n, batch, default);
            }

            // ── Combine + Integrate ─────────────────────────────────────────────────
            JobHandle accelDeps = JobHandle.CombineDependencies(neighborH, boundsH, cursorH);

            JobHandle integrateH;
            using (IntegrateMarker.Auto())
            {
                integrateH = new IntegrateJob
                {
                    AccelNeighbor    = spec.AccelNeighbor,
                    AccelBounds      = spec.AccelBounds,
                    AccelCursor      = spec.AccelCursor,
                    FlockIds         = spec.FlockIds,
                    KernelSettings   = spec.KernelSettings,
                    Positions        = spec.Positions,
                    Velocities       = spec.Velocities,
                    AccelerationsOut = spec.Accelerations,
                    Dt               = spec.Dt,
                }.Schedule(n, batch, accelDeps);
            }

            return integrateH;
        }

        /// <summary>
        /// Allocates a <c>NativeArray&lt;FlockKernelSettings&gt;</c> sized to the
        /// supplied <paramref name="settingsByFlockId"/> and fills it in main-thread.
        /// Caller disposes after completing the steering job graph.
        /// </summary>
        /// <remarks>
        /// Each entry is built by reading every property off <see cref="IFlockSettings"/>
        /// once. The reads themselves return primitives (no boxing), so the only
        /// allocation here is the unmanaged NativeArray storage — Mono heap stays flat.
        /// </remarks>
        public static NativeArray<FlockKernelSettings> BuildKernelSettings(
            IFlockSettings[] settingsByFlockId,
            Allocator allocator)
        {
            int count = settingsByFlockId.Length;
            var arr = new NativeArray<FlockKernelSettings>(count, allocator, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < count; i++)
            {
                arr[i] = new FlockKernelSettings(settingsByFlockId[i]);
            }
            return arr;
        }
    }
}
