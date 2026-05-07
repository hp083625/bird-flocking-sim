// FlockWorld.cs — scene-singleton owner of all per-bird state and per-frame simulation.
// M1 in FLOCKING_PLAN.md. Slice 3 (M2) wires in the cell-list spatial grid:
// FlockWorld owns a CellListSpatialIndex, rebuilds it each Tick before steering, and the
// steering helpers consume it via SpatialIndexReadOnly.GetNeighbors instead of the old
// O(n²) loop. Slice 4 (M3) will jobify + Burst-compile the steering itself.

using System.Collections.Generic;
using Bird_behiviour.Flocking.Behaviors;
using Bird_behiviour.Flocking.Core;
using Bird_behiviour.Flocking.Rendering;
using Bird_behiviour.Flocking.Spatial;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace Bird_behiviour.Flocking.Simulation
{
    /// <summary>
    /// One-per-scene MonoBehaviour that owns every per-bird <see cref="NativeArray{T}"/>
    /// (positions, velocities, accelerations, flock ids, world matrices), drives the
    /// per-frame simulation step, and dispatches per-flock rendering.
    /// </summary>
    /// <remarks>
    /// <b>Lifecycle.</b> Arrays are allocated lazily on first <see cref="RegisterFlock"/>
    /// and re-allocated whenever the registered flock set changes; <see cref="OnDestroy"/>
    /// disposes everything. Slice 2 keeps the registration model simple — registering
    /// after the first <see cref="Tick"/> is allowed but triggers a full re-allocation.
    /// <para/>
    /// <b>Tick entry point.</b> Production calls <see cref="Tick"/> from
    /// <see cref="LateUpdate"/> with <c>simDt = min(Time.deltaTime, MaxSimDt) *
    /// SimSpeedMultiplier</c>. Tests call <see cref="Tick"/> directly with a fixed
    /// <c>1/60 s</c> step for determinism (M6-3).
    /// <para/>
    /// <b>Implements</b> <see cref="IFlockWorldSettings"/> via serialized fields so jobs and
    /// gizmo drawers can hold an interface reference instead of a concrete one.
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public sealed class FlockWorld : MonoBehaviour, IFlockWorldSettings
    {
        // ── World settings (serialized backing for IFlockWorldSettings) ───────────────

        [Header("World Bounds")]
        [SerializeField] private Vector3 worldBoundsCenter  = Vector3.zero;
        [SerializeField] private Vector3 worldBoundsExtents = new Vector3(50f, 25f, 50f);
        [SerializeField, Min(0f)] private float worldBoundsWeight = 8f;

        [Header("Sim Time")]
        [Tooltip("Upper bound on per-tick dt. Frame stutters above this are clamped to prevent tunneling.")]
        [SerializeField, Min(1f / 240f)] private float maxSimDt = 1f / 30f;
        [Tooltip("Time-scale multiplier on the simulation. 1 = real-time.")]
        [SerializeField, Min(0f)] private float simSpeedMultiplier = 1f;

        [Header("Job Graph")]
        [Tooltip("IJobParallelFor inner-loop batch size. ~64-128 generally maximises worker-thread utilisation; larger reduces scheduling overhead, smaller improves load-balancing.")]
        [SerializeField, Min(1)] private int steeringBatchSize = 64;

        /// <summary>Inner-loop batch size used when scheduling steering IJobParallelFors (Slice 4 / M3).</summary>
        internal int SteeringBatchSize => steeringBatchSize;

        [Header("Rendering")]
        [Tooltip("Per-bird sphere radius used by FrustumCullJob to pad the 6 frustum-plane tests so birds don't pop in/out at the edge. ~2× visual bird size is a safe default.")]
        [SerializeField, Min(0f)] private float birdCullRadius = 0.5f;

        /// <summary>Per-bird padding radius (world units) used by <see cref="Bird_behiviour.Flocking.Rendering.FrustumCullJob"/>.</summary>
        public float BirdCullRadius => birdCullRadius;

        // ── Profiler markers (Slice 4 / M3 — read by M5 HUD + Unity Profiler) ─────────
        private static readonly ProfilerMarker BuildGridMarker = new ProfilerMarker("Flock.BuildGrid");
        // Slice 8 / M4 — cull + matrices job markers (per FLOCKING_PLAN §6 M5-5 names).
        private static readonly ProfilerMarker CullMarker      = new ProfilerMarker("Flock.Cull");
        private static readonly ProfilerMarker MatricesMarker  = new ProfilerMarker("Flock.Matrices");

        /// <inheritdoc/>
        public float3 WorldBoundsCenter   => worldBoundsCenter;
        /// <inheritdoc/>
        public float3 WorldBoundsExtents  => worldBoundsExtents;
        /// <inheritdoc/>
        public float  WorldBoundsWeight   => worldBoundsWeight;
        /// <inheritdoc/>
        public float  MaxSimDt            => maxSimDt;
        /// <inheritdoc/>
        public float  SimSpeedMultiplier  => simSpeedMultiplier;

        // ── Cursor (written by CursorInputController, read by steering) ───────────────

        /// <summary>Last cursor-world-point written by a <c>CursorInputController</c>.</summary>
        public float3 CursorWorldPoint { get; private set; }

        /// <summary>True iff the cursor was successfully projected onto the horizontal plane this frame.</summary>
        public bool CursorOnScreen { get; private set; }

        /// <summary>Updates the cursor world-point + visibility flag. Called by <c>CursorInputController</c>.</summary>
        public void SetCursor(float3 worldPoint, bool onScreen)
        {
            CursorWorldPoint = worldPoint;
            CursorOnScreen = onScreen;
        }

        // ── Registered flocks ─────────────────────────────────────────────────────────

        // Linear list of registered managers (insertion order = FlockId).
        private readonly List<FlockManager> registered = new List<FlockManager>(8);

        // Indexed by FlockId: settings + slice. Sized to registered.Count.
        private IFlockSettings[] settingsByFlockId = System.Array.Empty<IFlockSettings>();

        /// <summary>Registered flock count (0 ≤ count ≤ 256).</summary>
        public int RegisteredFlockCount => registered.Count;

        // ── Per-bird arrays (Allocator.Persistent; sized to TotalBirdCount) ──────────

        /// <summary>Per-bird world-space positions. Indices in <c>[0, TotalBirdCount)</c>.</summary>
        public NativeArray<float3>     Positions;
        /// <summary>Per-bird world-space velocities.</summary>
        public NativeArray<float3>     Velocities;
        /// <summary>Per-bird steering accelerations recomputed every <see cref="Tick"/>.</summary>
        public NativeArray<float3>     Accelerations;
        /// <summary>Per-bird flock id (matches one of <see cref="Slices"/>).</summary>
        public NativeArray<byte>       FlockIds;
        /// <summary>One <see cref="FlockSlice"/> per registered flock.</summary>
        public NativeArray<FlockSlice> Slices;

        // ── Per-flock visible-render buffers (Slice 8 / M4) ──────────────────────────
        //
        // Slice 8 picks the **per-flock cull** strategy from FLOCKING_PLAN §6 M4-4:
        // every registered flock owns a NativeList<int> of post-cull global bird indices
        // and a packed NativeArray<float4x4> of matrices for those visible birds. The
        // alternative — global cull + per-flock filter pass — would force the renderer
        // to either iterate everyone or maintain a flock-id parallel array; per-flock
        // cull jobs are independent (no cross-flock contention), the per-flock list
        // capacity is the flock's BirdCount (worst case all visible) so AddNoResize is
        // wait-free, and downstream the renderer just consumes [0, list.Length) directly.
        //
        // Cost: N parallel cull dispatches instead of one. With v1's N ≤ 2 this is a
        // rounding-error overhead next to the steering chain.
        private NativeList<int>[]      visibleIndicesPerFlock     = System.Array.Empty<NativeList<int>>();
        private NativeArray<float4x4>[] visibleMatricesPerFlock   = System.Array.Empty<NativeArray<float4x4>>();

        /// <summary>Sum of every registered flock's <c>BirdCount</c>.</summary>
        public int TotalBirdCount { get; private set; }

        private bool arraysAllocated;

        // Tracks the tail of the most recent Tick's job graph. DisposeArrays /
        // OnDestroy drains it before deallocating the NativeArrays the jobs touched —
        // without this, exiting Play mid-tick throws "JobHandle.Complete() before
        // you can deallocate ... safely".
        private JobHandle pendingTickHandle;

        // ── Camera frustum cache (Slice 8 / M4) ──────────────────────────────────────
        //
        // 6 planes encoded as float4 (xyz = inward-facing normal, w = signed distance);
        // a point p is inside the frustum iff dot(plane.xyz, p) + plane.w >= 0 for every
        // plane. We allocate Persistent + length 6 once in Awake (re-used every frame),
        // and refresh from Camera.main inside Tick. Tests can override via
        // <see cref="SetCameraFrustumPlanesForTest"/> which writes the array directly.
        //
        // Default (zero-initialised) state means EVERY plane test passes — i.e. no
        // culling — so a missing camera or pre-Awake state degrades gracefully to "render
        // all birds" rather than to a black screen.
        private NativeArray<float4> cameraFrustumPlanes;

        // Reusable Plane[6] scratch for GeometryUtility.CalculateFrustumPlanes(camera, planes).
        // The overload that takes a Plane[] avoids the per-call allocation that the
        // returning-Plane[] overload would otherwise incur.
        private readonly UnityEngine.Plane[] frustumPlaneScratch = new UnityEngine.Plane[6];

        // When true (set by <see cref="SetCameraFrustumPlanesForTest"/>), Tick skips the
        // Camera.main lookup + plane recompute — tests own the cache for the rest of the
        // run. Cleared by <see cref="ClearCameraFrustumPlanesOverride"/>.
        private bool frustumPlanesOverridden;

        /// <summary>Read-only view of the cached 6 camera frustum planes (xyz = inward normal, w = distance).</summary>
        public NativeArray<float4>.ReadOnly CameraFrustumPlanes => cameraFrustumPlanes.AsReadOnly();

        // ── Spatial index (Slice 3 / M2) ─────────────────────────────────────────────

        // Owned by FlockWorld; allocated lazily in ReallocateForCurrentRegistration once
        // we know the max perception radius across registered flocks. Disposed in
        // OnDestroy. Cell size is auto-derived as max(perceptionRadius); rebuilt each
        // Tick via ScheduleBuild.
        private CellListSpatialIndex spatialIndex;

        /// <summary>Current grid cell size (max <c>PerceptionRadius</c> across registered flocks).</summary>
        public float CellSize => spatialIndex != null ? spatialIndex.CellSize : 0f;

        /// <summary>The cell-list spatial grid this world owns. Used by Slice 11 gizmos for cell-occupancy readback. Null until the first flock registers.</summary>
        public CellListSpatialIndex SpatialIndex => spatialIndex;

        // ── Registration API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Registers a <see cref="FlockManager"/> and returns its assigned slice. Causes the
        /// world's per-bird arrays to be (re-)allocated to fit the new total bird count.
        /// </summary>
        /// <remarks>
        /// FlockId is the next available index (0..255). Slice 2 does not support live
        /// rebalancing — registering / deregistering during a running sim wipes positions
        /// and velocities of all flocks on re-allocation.
        /// </remarks>
        /// <exception cref="System.ArgumentNullException">If <paramref name="manager"/> is null.</exception>
        /// <exception cref="System.InvalidOperationException">If 256 flocks are already registered.</exception>
        public FlockSlice RegisterFlock(FlockManager manager)
        {
            if (manager == null) throw new System.ArgumentNullException(nameof(manager));
            if (registered.Count >= 256)
            {
                throw new System.InvalidOperationException(
                    "FlockWorld supports at most 256 flocks (FlockId is a byte).");
            }
            if (manager.Settings == null)
            {
                throw new System.InvalidOperationException(
                    $"FlockManager '{manager.name}' must have a FlockSettings asset assigned before registration.");
            }

            byte flockId = (byte)registered.Count;
            registered.Add(manager);

            // Recompute slices over all registered flocks.
            ReallocateForCurrentRegistration();
            return Slices[flockId];
        }

        /// <summary>
        /// Deregisters a <see cref="FlockManager"/>. Triggers a full reallocation. Order of the
        /// remaining flocks is preserved; remaining FlockIds are left unchanged where possible
        /// — but indices after the removed slot shift down by one, so callers should treat
        /// FlockId as opaque and re-read it from the returned slice on the next Register call.
        /// </summary>
        public void DeregisterFlock(FlockManager manager)
        {
            int idx = registered.IndexOf(manager);
            if (idx < 0)
            {
                return;
            }
            registered.RemoveAt(idx);
            ReallocateForCurrentRegistration();
        }

        // ── Allocation ────────────────────────────────────────────────────────────────

        private void ReallocateForCurrentRegistration()
        {
            DisposeArrays();

            int total = 0;
            for (int i = 0; i < registered.Count; i++)
            {
                total += math.max(0, registered[i].Settings.BirdCount);
            }
            TotalBirdCount = total;

            settingsByFlockId = new IFlockSettings[registered.Count];
            for (int i = 0; i < registered.Count; i++)
            {
                settingsByFlockId[i] = registered[i].Settings;
            }

            // Always allocate Slices (even if total == 0 → length-0 NativeArray is fine).
            Slices = new NativeArray<FlockSlice>(registered.Count, Allocator.Persistent);
            int cursor = 0;
            for (int i = 0; i < registered.Count; i++)
            {
                int count = math.max(0, registered[i].Settings.BirdCount);
                Slices[i] = new FlockSlice(cursor, count, (byte)i);
                cursor += count;
            }

            int allocLen = math.max(1, total); // Allocator.Persistent rejects 0-length on some Unity versions.
            Positions     = new NativeArray<float3>(allocLen, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            Velocities    = new NativeArray<float3>(allocLen, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            Accelerations = new NativeArray<float3>(allocLen, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            FlockIds      = new NativeArray<byte>  (allocLen, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            // Per-flock visible buffers. Each list's capacity = flock.Count (worst case
            // all birds visible) so the cull job's AddNoResize is wait-free; matrices
            // array is sized to the same upper bound, with only [0, list.Length) populated.
            visibleIndicesPerFlock   = new NativeList<int>[registered.Count];
            visibleMatricesPerFlock  = new NativeArray<float4x4>[registered.Count];
            for (int f = 0; f < registered.Count; f++)
            {
                int cap = math.max(1, Slices[f].Count);
                visibleIndicesPerFlock[f]  = new NativeList<int>(cap, Allocator.Persistent);
                visibleMatricesPerFlock[f] = new NativeArray<float4x4>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            // Stamp FlockIds across each slice once.
            for (int f = 0; f < registered.Count; f++)
            {
                FlockSlice s = Slices[f];
                for (int i = 0; i < s.Count; i++)
                {
                    FlockIds[s.StartIndex + i] = s.FlockId;
                }
            }

            arraysAllocated = true;

            // (Re-)size the spatial grid for the new registration. Cell size is auto-
            // derived as max(perceptionRadius across registered flocks); per FLOCKING_PLAN
            // §6 M2-4. World bounds drive the per-axis cell counts.
            ResizeSpatialIndex();

            // Notify each manager so it can spawn into its (possibly new) slice.
            for (int i = 0; i < registered.Count; i++)
            {
                registered[i].OnSliceAllocated(Slices[i]);
            }
        }

        private void ResizeSpatialIndex()
        {
            if (spatialIndex == null)
            {
                spatialIndex = new CellListSpatialIndex();
            }

            if (TotalBirdCount == 0 || registered.Count == 0)
            {
                // Nothing to index — release any prior allocation so we don't hold cells
                // we won't query.
                spatialIndex.Dispose();
                return;
            }

            float maxPerception = 0f;
            for (int i = 0; i < registered.Count; i++)
            {
                IFlockSettings s = registered[i].Settings;
                if (s == null) continue;
                maxPerception = math.max(maxPerception, s.PerceptionRadius);
            }

            // Defensive default: fall back to a 1m cell if every flock reports 0 (mis-
            // configured asset). Keeps the build from dividing by zero.
            float cellSize = maxPerception > 0f ? maxPerception : 1f;

            spatialIndex.Resize(WorldBoundsCenter, WorldBoundsExtents, cellSize, TotalBirdCount);
        }

        private void DisposeArrays()
        {
            if (!arraysAllocated)
            {
                return;
            }
            // Drain any in-flight Tick before deallocating arrays the jobs touched —
            // necessary when Play exits mid-tick, on domain reload, or if Tick threw
            // before reaching its own Complete() call.
            pendingTickHandle.Complete();
            pendingTickHandle = default;
            if (Positions.IsCreated)     Positions.Dispose();
            if (Velocities.IsCreated)    Velocities.Dispose();
            if (Accelerations.IsCreated) Accelerations.Dispose();
            if (FlockIds.IsCreated)      FlockIds.Dispose();
            if (Slices.IsCreated)        Slices.Dispose();

            // Per-flock visible buffers (Slice 8 / M4) — disposed alongside the per-bird
            // arrays so they share the same lifetime contract.
            for (int f = 0; f < visibleIndicesPerFlock.Length; f++)
            {
                if (visibleIndicesPerFlock[f].IsCreated)  visibleIndicesPerFlock[f].Dispose();
            }
            for (int f = 0; f < visibleMatricesPerFlock.Length; f++)
            {
                if (visibleMatricesPerFlock[f].IsCreated) visibleMatricesPerFlock[f].Dispose();
            }
            visibleIndicesPerFlock  = System.Array.Empty<NativeList<int>>();
            visibleMatricesPerFlock = System.Array.Empty<NativeArray<float4x4>>();

            arraysAllocated = false;
        }

        /// <summary>
        /// Tears down all per-bird native arrays + any spatial index, then re-registers every
        /// currently-bound <see cref="FlockManager"/> from scratch. Used by Slice 10's
        /// "Apply Structural Changes" / "Restart Sim" buttons after a structural change
        /// (BirdCount, PerceptionRadius, world bounds) has been committed.
        /// </summary>
        /// <remarks>
        /// Safe to call from EditMode (when <see cref="Tick"/> isn't running) and from PlayMode.
        /// Implementation snapshots the registered list because each
        /// <see cref="FlockManager.Rebuild"/> deregisters + re-registers, which mutates the
        /// list mid-iteration. Per <c>FLOCKING_PLAN.md §6 M1-6</c>, the only managed
        /// allocations are the realloc itself + the single snapshot array.
        /// <para/>
        /// The spatial index dispose path is a stub guarded by a null check — Slice 3 will
        /// flesh it out when <c>FlockWorld</c> grows a <c>SpatialHashGrid</c> field.
        /// </remarks>
        public void Rebuild()
        {
            // No registered managers → just clear stale arrays so a later Register starts clean.
            if (registered.Count == 0)
            {
                DisposeArrays();
                DisposeSpatialIndex();
                TotalBirdCount = 0;
                settingsByFlockId = System.Array.Empty<IFlockSettings>();
                return;
            }

            // Spatial index (if any) lives across the whole sim — tear it down so the next
            // tick rebuilds it against the new world arrays. Slice 3 wires this in.
            DisposeSpatialIndex();

            // Snapshot — manager.Rebuild() Deregister+Register mutates `registered`.
            FlockManager[] snapshot = registered.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                FlockManager mgr = snapshot[i];
                if (mgr != null)
                {
                    mgr.Rebuild();
                }
            }
        }

        /// <summary>
        /// Disposes the cell-list spatial grid (if allocated). Called from
        /// <see cref="Rebuild"/> so the next <see cref="Tick"/> re-allocates against the
        /// freshly-registered flock set, and from <see cref="OnDestroy"/>.
        /// </summary>
        private void DisposeSpatialIndex()
        {
            if (spatialIndex != null)
            {
                spatialIndex.Dispose();
                spatialIndex = null;
            }
        }

        // ── Unity lifecycle ──────────────────────────────────────────────────────────

        private void Awake()
        {
            // Allocate the 6-plane frustum cache once per FlockWorld lifetime so Tick is
            // alloc-free. Default contents are zero → every plane test passes (visible),
            // which is the desired fail-safe (no Camera.main yet → no culling).
            if (!cameraFrustumPlanes.IsCreated)
            {
                cameraFrustumPlanes = new NativeArray<float4>(6, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
        }

        private void OnDestroy()
        {
            DisposeArrays();
            DisposeSpatialIndex();
            if (cameraFrustumPlanes.IsCreated)
            {
                cameraFrustumPlanes.Dispose();
            }
        }

        /// <summary>
        /// Refreshes <see cref="CameraFrustumPlanes"/> from the supplied camera. Called from
        /// <see cref="Tick"/> with <see cref="Camera.main"/> in production; tests use
        /// <see cref="SetCameraFrustumPlanesForTest"/> instead.
        /// </summary>
        /// <remarks>
        /// Uses the <c>GeometryUtility.CalculateFrustumPlanes(Camera, Plane[])</c> overload
        /// that writes into a pre-allocated buffer to avoid the per-frame managed alloc the
        /// returning-Plane[] overload would otherwise incur.
        /// <para/>
        /// GeometryUtility's planes have <em>inward-facing</em> normals, so a point <c>p</c>
        /// is inside the frustum iff <c>dot(n, p) + d &gt;= 0</c> for every plane — which is
        /// exactly what <c>FrustumCullJob</c> assumes.
        /// </remarks>
        private void UpdateCameraFrustumPlanesFrom(Camera cam)
        {
            if (cam == null || !cameraFrustumPlanes.IsCreated)
            {
                return;
            }
            GeometryUtility.CalculateFrustumPlanes(cam, frustumPlaneScratch);
            for (int i = 0; i < 6; i++)
            {
                UnityEngine.Plane p = frustumPlaneScratch[i];
                cameraFrustumPlanes[i] = new float4(p.normal.x, p.normal.y, p.normal.z, p.distance);
            }
        }

        /// <summary>
        /// Test hook: writes the 6 frustum planes directly into the cache and pins them so
        /// subsequent <see cref="Tick"/> calls do <em>not</em> overwrite them from
        /// <see cref="Camera.main"/>. Intended for headless PlayMode tests that don't want
        /// to set up a real <c>MainCamera</c>.
        /// </summary>
        /// <param name="planes">Source array of length 6 (xyz = inward normal, w = distance).</param>
        public void SetCameraFrustumPlanesForTest(NativeArray<float4> planes)
        {
            if (!cameraFrustumPlanes.IsCreated)
            {
                cameraFrustumPlanes = new NativeArray<float4>(6, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
            int n = math.min(6, planes.Length);
            for (int i = 0; i < n; i++)
            {
                cameraFrustumPlanes[i] = planes[i];
            }
            frustumPlanesOverridden = true;
        }

        /// <summary>Clears the test-only override set by <see cref="SetCameraFrustumPlanesForTest"/>; subsequent ticks resume reading from <see cref="Camera.main"/>.</summary>
        public void ClearCameraFrustumPlanesOverride()
        {
            frustumPlanesOverridden = false;
        }

        private void LateUpdate()
        {
            float simDt = math.min(Time.deltaTime, maxSimDt) * simSpeedMultiplier;
            if (simDt <= 0f)
            {
                return;
            }
            Tick(simDt);
        }

        // ── Tick (public so tests can drive it directly) ─────────────────────────────

        /// <summary>
        /// Runs one simulation step at the supplied <paramref name="dt"/>. Updates positions,
        /// velocities, world matrices, then dispatches per-flock rendering.
        /// </summary>
        /// <remarks>
        /// Tests call this directly with a fixed step (e.g. <c>1f/60f</c>) to bypass
        /// <see cref="LateUpdate"/>'s variable / clamped / scaled <c>dt</c> for determinism.
        /// </remarks>
        public void Tick(float dt)
        {
            if (!arraysAllocated || TotalBirdCount == 0)
            {
                return;
            }

            // Drain any tail of the previous Tick before scheduling new jobs against
            // the same arrays. The end-of-Tick Complete() should already have done this,
            // but if the prior frame was interrupted (PlayMode pause, recompile,
            // exception) the safety system can still see outstanding writes — be defensive.
            pendingTickHandle.Complete();

            // ── 1. Schedule the cell-list spatial grid build (Slice 3 / M2). ──────────
            //
            // We DO NOT immediately Complete the build here any more — Slice 4 (M3)
            // chains NeighborForcesJob off this handle so the grid build runs on a
            // worker thread in parallel with BoundsForcesJob + CursorForceJob. The
            // whole chain is completed by IntegrateJob below.
            JobHandle gridHandle = default;
            SpatialIndexReadOnly spatial = default;
            if (spatialIndex != null && spatialIndex.IsAllocated)
            {
                using (BuildGridMarker.Auto())
                {
                    gridHandle = spatialIndex.ScheduleBuild(
                        Positions.AsReadOnly(), TotalBirdCount, default);
                }
                spatial = spatialIndex.AsReadOnly();
            }

            // ── 2. Allocate per-frame intermediate buffers (TempJob, frame-scoped) ─
            //
            // These are unmanaged NativeArrays (Profiler.GetMonoUsedSizeLong is flat)
            // and disposed right after IntegrateJob.Complete() below.
            var accelNeighbor = new NativeArray<float3>(TotalBirdCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var accelBounds   = new NativeArray<float3>(TotalBirdCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var accelCursor   = new NativeArray<float3>(TotalBirdCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var kernelSettings = SteeringJobGraph.BuildKernelSettings(settingsByFlockId, Allocator.TempJob);

            // World-bounds margin: 5% of the smaller extent (per FLOCKING_PLAN.md §6 M3-2).
            float worldMargin = math.cmin(WorldBoundsExtents) * 0.05f;

            var spec = new SteeringJobGraph.DispatchSpec
            {
                Positions          = Positions,
                Velocities         = Velocities,
                Accelerations      = Accelerations,
                FlockIds           = FlockIds,
                AccelNeighbor      = accelNeighbor,
                AccelBounds        = accelBounds,
                AccelCursor        = accelCursor,
                KernelSettings     = kernelSettings,
                Spatial            = spatial,
                WorldBoundsCenter  = WorldBoundsCenter,
                WorldBoundsExtents = WorldBoundsExtents,
                WorldBoundsWeight  = WorldBoundsWeight,
                WorldBoundsMargin  = worldMargin,
                CursorWorldPoint   = CursorWorldPoint,
                CursorOnScreen     = CursorOnScreen,
                BirdCount          = TotalBirdCount,
                BatchSize          = SteeringBatchSize,
                Dt                 = dt,
                GridHandle         = gridHandle,
            };

            // Refresh the camera frustum cache once per Tick (alloc-free; reuses Plane[6]
            // scratch and the Persistent NativeArray<float4>(6) allocated in Awake).
            // Tests can override via SetCameraFrustumPlanesForTest in which case we skip.
            Camera cam = Camera.main;
            if (!frustumPlanesOverridden)
            {
                UpdateCameraFrustumPlanesFrom(cam);
            }

            // Snapshot positions so FrustumCullJob can run in parallel with IntegrateJob
            // without tripping the safety system (cull is [ReadOnly] on its position
            // input; integrate is read-write on Positions). MUST happen BEFORE Dispatch
            // schedules IntegrateJob — otherwise the synchronous main-thread copy ctor
            // would read Positions while a writer is pending. The snapshot reflects
            // pre-integrate positions; BirdCullRadius (~2× bird size) absorbs the
            // MaxSpeed*dt drift between snapshot and final integrated positions.
            var cullPositions = new NativeArray<float3>(Positions, Allocator.TempJob);

            // ── 3. Schedule the steering chain (cell-list → 3 force jobs → Integrate)
            //      AND the per-flock cull jobs in parallel — cull has no grid/steering
            //      dependency, so both branches share worker-thread time. ─────────────
            JobHandle integrateH = SteeringJobGraph.Dispatch(in spec);

            // Per-flock cull → matrices chain. Cull reads `cullPositions` (snapshot),
            // matrices reads `Positions` (post-integrate) — so matrices for flock f
            // fans in on CombineDependencies(cullH[f], integrateH).
            JobHandle matricesAllH = ScheduleCullAndMatrices(integrateH, BirdCullRadius, cullPositions);
            pendingTickHandle = matricesAllH; // tracked for safe Dispose if Play stops mid-tick

            // Single sync point: drains steering chain AND every flock's cull + matrices.
            matricesAllH.Complete();

            // Dispose per-frame TempJob buffers now that consumers have completed.
            accelNeighbor.Dispose();
            accelBounds.Dispose();
            accelCursor.Dispose();
            kernelSettings.Dispose();
            cullPositions.Dispose();

            // ── 4. Dispatch per-flock rendering off the now-populated visible buffers.
            DispatchRendering(cam);
        }

        /// <summary>
        /// Schedules one <see cref="FrustumCullJob"/> + one <see cref="BuildMatricesJob"/>
        /// per registered flock, returning a combined <see cref="JobHandle"/> the caller
        /// completes alongside the steering chain.
        /// </summary>
        /// <remarks>
        /// Cull jobs read the supplied <paramref name="cullPositions"/> snapshot (so they
        /// can run in parallel with IntegrateJob's writes to <see cref="Positions"/>) and
        /// write into per-flock <c>NativeList&lt;int&gt;</c>s pre-sized to the flock's
        /// bird count. The matrices job for flock <c>f</c> uses
        /// <c>visibleIndicesPerFlock[f].AsDeferredJobArray()</c> so its iteration count is
        /// resolved at job-start from the cull's output length — no main-thread sync
        /// between cull and matrices. The matrices job reads the *post-integration*
        /// <see cref="Positions"/> and <see cref="Velocities"/>, so it depends on
        /// CombineDependencies(cullH, integrateH).
        /// </remarks>
        private JobHandle ScheduleCullAndMatrices(JobHandle integrateH, float birdRadius, NativeArray<float3> cullPositions)
        {
            JobHandle combinedH = integrateH; // ensures the caller's single Complete() drains everything
            int batch = math.max(1, SteeringBatchSize);

            for (int f = 0; f < registered.Count; f++)
            {
                FlockSlice slice = Slices[f];
                if (slice.Count == 0) continue;

                NativeList<int> visList = visibleIndicesPerFlock[f];
                visList.Clear(); // reset Length to 0; capacity (= slice.Count) is preserved.

                JobHandle cullH;
                using (CullMarker.Auto())
                {
                    cullH = new FrustumCullJob
                    {
                        Positions             = cullPositions,
                        CameraFrustumPlanes   = cameraFrustumPlanes,
                        StartIndex            = slice.StartIndex,
                        BirdRadius            = birdRadius,
                        VisibleIndicesWriter  = visList.AsParallelWriter(),
                    }.Schedule(slice.Count, batch, default);
                }

                JobHandle matricesH;
                using (MatricesMarker.Auto())
                {
                    var matricesJob = new BuildMatricesJob
                    {
                        VisibleIndices  = visList.AsDeferredJobArray(),
                        Positions       = Positions,
                        Velocities      = Velocities,
                        VisibleMatrices = visibleMatricesPerFlock[f],
                    };
                    // IJobParallelForDefer.Schedule resolves the iteration count from
                    // visList's length at job-start time — no need to Complete cullH.
                    matricesH = matricesJob.Schedule(visList, batch,
                        JobHandle.CombineDependencies(cullH, integrateH));
                }

                combinedH = JobHandle.CombineDependencies(combinedH, matricesH);
            }

            return combinedH;
        }

        private void DispatchRendering(Camera cam)
        {
            for (int f = 0; f < registered.Count; f++)
            {
                FlockManager mgr = registered[f];
                IFlockRenderer renderer = mgr.Renderer;
                IFlockSettings s = mgr.Settings;
                if (renderer == null || s == null || s.BirdMesh == null || s.BirdMaterial == null)
                {
                    continue;
                }

                FlockSlice slice = Slices[f];
                if (slice.Count == 0) continue;

                int visibleCount = visibleIndicesPerFlock[f].Length;
                if (visibleCount <= 0) continue;

                // Slice 9: pass the NativeArray directly (no AsReadOnly) so the
                // IndirectFlockRenderer can call GraphicsBuffer.SetData(NativeArray, ...)
                // without a per-frame intermediate copy. Implementations contract is
                // read-only; we don't enforce it via the type system to keep the hot
                // path zero-overhead.
                renderer.Render(
                    slice,
                    s.BirdMesh,
                    s.BirdMaterial,
                    visibleMatricesPerFlock[f],
                    visibleCount,
                    cam);
            }
        }

        // ── Visible-bird-count accessor (Slice 8 / M4) ───────────────────────────────
        /// <summary>
        /// Returns the count of birds in flock <paramref name="flockId"/> that survived
        /// frustum culling on the most recent <see cref="Tick"/>. Returns 0 if the flock
        /// id is out of range or no Tick has run yet. Used by the runtime HUD (Slice 11)
        /// and by PlayMode tests.
        /// </summary>
        public int GetVisibleCount(int flockId)
        {
            if (flockId < 0 || flockId >= visibleIndicesPerFlock.Length) return 0;
            NativeList<int> list = visibleIndicesPerFlock[flockId];
            return list.IsCreated ? list.Length : 0;
        }

        /// <summary>Backwards-compatible alias for tests that predate Slice 11's HUD.</summary>
        public int GetVisibleCountForTest(int flockId) => GetVisibleCount(flockId);

        /// <summary>
        /// Returns a copy of the global bird indices visible for flock <paramref name="flockId"/>
        /// after the most recent <see cref="Tick"/>. Caller owns the returned array. PlayMode-test only.
        /// </summary>
        public int[] GetVisibleIndicesSnapshotForTest(int flockId)
        {
            if (flockId < 0 || flockId >= visibleIndicesPerFlock.Length) return System.Array.Empty<int>();
            NativeList<int> list = visibleIndicesPerFlock[flockId];
            if (!list.IsCreated || list.Length == 0) return System.Array.Empty<int>();
            int n = list.Length;
            int[] copy = new int[n];
            for (int i = 0; i < n; i++) copy[i] = list[i];
            return copy;
        }

        // ── Gizmos ────────────────────────────────────────────────────────────────────

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.white;
            Gizmos.DrawWireCube(worldBoundsCenter, worldBoundsExtents * 2f);
        }
    }
}
