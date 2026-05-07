// FlockWorld.cs — scene-singleton owner of all per-bird state and per-frame simulation.
// M1 in FLOCKING_PLAN.md. Slice 2 ships the naive O(n²) main-thread Tick; Slice 4 (M3) replaces
// the steering body with a job graph and Slice 5 (M2) adds the spatial grid.

using System.Collections.Generic;
using Bird_behiviour.Flocking.Core;
using Unity.Collections;
using Unity.Mathematics;
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
        /// <summary>Per-bird world matrix recomputed every <see cref="Tick"/> (translation + look-along-velocity).</summary>
        public NativeArray<float4x4>   Matrices;

        /// <summary>Sum of every registered flock's <c>BirdCount</c>.</summary>
        public int TotalBirdCount { get; private set; }

        private bool arraysAllocated;

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
            Matrices      = new NativeArray<float4x4>(allocLen, Allocator.Persistent, NativeArrayOptions.ClearMemory);

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

            // Notify each manager so it can spawn into its (possibly new) slice.
            for (int i = 0; i < registered.Count; i++)
            {
                registered[i].OnSliceAllocated(Slices[i]);
            }
        }

        private void DisposeArrays()
        {
            if (!arraysAllocated)
            {
                return;
            }
            if (Positions.IsCreated)     Positions.Dispose();
            if (Velocities.IsCreated)    Velocities.Dispose();
            if (Accelerations.IsCreated) Accelerations.Dispose();
            if (FlockIds.IsCreated)      FlockIds.Dispose();
            if (Slices.IsCreated)        Slices.Dispose();
            if (Matrices.IsCreated)      Matrices.Dispose();
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
        /// Slice-3-shaped extension point: Slice 3 (M2 Spatial) will store a
        /// <c>SpatialHashGrid</c> reference on this MonoBehaviour and dispose it here.
        /// Slice 10 ships the no-op so <see cref="Rebuild"/> already calls a stable hook.
        /// </summary>
        private void DisposeSpatialIndex()
        {
            // Intentional no-op until Slice 3 lands the spatial index field on FlockWorld.
        }

        // ── Unity lifecycle ──────────────────────────────────────────────────────────

        private void OnDestroy()
        {
            DisposeArrays();
            DisposeSpatialIndex();
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

            // 1. Compute accelerations (naïve O(n²); replaced by job graph in Slice 4).
            NaiveSteering.ComputeAccelerations(
                Positions, Velocities, FlockIds, Slices, TotalBirdCount,
                settingsByFlockId, this,
                CursorWorldPoint, CursorOnScreen,
                Accelerations);

            // 2. Integrate.
            NaiveSteering.Integrate(
                Positions, Velocities, FlockIds, Accelerations, TotalBirdCount,
                settingsByFlockId, dt);

            // 3. Build world matrices (translation + look-along-velocity).
            BuildMatrices();

            // 4. Dispatch per-flock rendering.
            DispatchRendering();
        }

        private void BuildMatrices()
        {
            for (int i = 0; i < TotalBirdCount; i++)
            {
                float3 pos = Positions[i];
                float3 vel = Velocities[i];
                quaternion rot = quaternion.LookRotationSafe(vel, math.up());
                Matrices[i] = float4x4.TRS(pos, rot, new float3(1f, 1f, 1f));
            }
        }

        private void DispatchRendering()
        {
            Camera cam = Camera.main;
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
                NativeArray<float4x4>.ReadOnly readOnlyMatrices = Matrices.AsReadOnly();

                // Slice 2: pass the full Matrices array but tell the renderer to start at
                // slice.StartIndex by giving it a sub-slice. NativeArray<T>.GetSubArray is
                // safe and free.
                NativeArray<float4x4> sub = Matrices.GetSubArray(slice.StartIndex, slice.Count);
                renderer.Render(slice, s.BirdMesh, s.BirdMaterial, sub.AsReadOnly(), slice.Count, cam);
            }
        }

        // ── Gizmos ────────────────────────────────────────────────────────────────────

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.white;
            Gizmos.DrawWireCube(worldBoundsCenter, worldBoundsExtents * 2f);
        }
    }
}
