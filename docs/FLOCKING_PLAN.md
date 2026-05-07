# Bird Flocking Simulation — Engineering Plan (v2)

Owner: TBD (tech lead)
Audience: 3–4 Unity devs, all comfortable with DOTS (Burst, Jobs, Native collections).
Workflow: GitHub PRs + issues, squash-merge to `main`, 1 approval required, CI green.
Plan history: v1 was sequential single-developer plan; v2 (current) is parallel-team plan after design grilling that resolved 20 architectural and operational decisions. See `git log docs/FLOCKING_PLAN.md` for changes.

---

## 1. Goals & Non-Goals

### Goals
- **Interactive demo.** Player flies (WASD + mouse-look) through a flock of 5,000–50,000 birds in a bounded sky volume. Mouse cursor acts as a soft attractor or repulsor; each flock decides per-`FlockSettings` how to react.
- **Multi-flock from day 1** with binary self-vs-other interaction (predator/prey is the headline behavioral demo). Flocks are visually distinct (own mesh + material).
- **Performance gate.** 50k total birds across all flocks, ≥60 fps median, on the lowest-spec team M-series Mac (target M3 base / 10-core GPU as conservative reference; named precisely once team is set).
- **Hot path is Burst.** Every per-bird per-frame loop runs as a `[BurstCompile]` `IJobParallelFor`.
- **Live tunable.** Behavioral weights edit live in Play mode via `ScriptableObject` + custom inspector. Structural changes (bird count, world bounds, perception radius) commit via an explicit "Apply" button to avoid slider-drag churn.

### Non-Goals (v1)
- ECS / Entities. We use the Job System without Entities to keep cognitive load down. Migration later is a refactor we can afford.
- Animated meshes — placeholder cone is fine; designers can swap in a real mesh per flock.
- Obstacles, terrain, fog walls — out of scope; we leave hooks for them.
- More than 2 flocks in the demo. The contracts support N flocks; the inspector UX for tuning N×N relationships is v1.1.
- Across-machine determinism. Tests promise within-machine, single-Unity-version reproducibility only. Across-machine would force `math.precise` mode and single-threaded tests; not worth the cost.
- GPU compute-shader culling. CPU `FrustumCullJob` is sufficient at 50k.
- Networking, multiplayer, scripted cinematics, runtime bird spawn/despawn (counts fixed at scene init).

---

## 2. Architecture Overview

Two-tier composition:

- **One `FlockWorld` MonoBehaviour per scene.** Owns all per-bird state arrays (flat, sized to total bird count across flocks), the shared cell-list spatial grid, world bounds, the sim clock, the cursor world-point, and the camera frustum cache.
- **N `FlockManager` MonoBehaviour per scene** (one per flock). Holds `(StartIndex, Count)` slice into the world arrays and a `FlockSettings` reference. Registers with `FlockWorld` in `OnEnable`; deregisters in `OnDisable`.

Per-frame, in `FlockWorld.LateUpdate`:

1. `simDt = math.min(Time.deltaTime, MaxSimDt) * SimSpeedMultiplier` (clamp protects against tunneling on stutters).
2. Update cursor world-point (raycast from screen cursor to horizontal plane through `WorldBounds.center`).
3. Cache camera frustum planes (`GeometryUtility.CalculateFrustumPlanes(Camera.main)`).
4. Schedule the job graph (below); complete chain.
5. Dispatch render calls — one `Graphics.RenderMeshIndirect` per registered flock with that flock's mesh + material + visible-matrices slice.

Job graph (per frame):

```
BuildGridJob              FrustumCullJob
(3-pass cell-list:        (6-plane test;
 count→prefix→scatter)     atomic-write visibleIndices)
        │                          │
        ├──► NeighborForcesJob ──┐ │
        │   (sep/align/cohere;   │ │
        │    in-flock vs out-of- │ │
        │    flock branch)       │ │
        │                        │ │
        ├──► BoundsForcesJob ────┤ │
        │   (world hard +        │ │
        │    per-flock soft)     │ │
        │                        │ │
        └──► CursorForceJob ─────┤ │
            (signed strength ×   │ │
             distance falloff)   │ │
                                 ▼ │
                          IntegrateJob
                          (sum 3 accel arrays,
                           MaxAcceleration cap,
                           speed clamp,
                           pos += vel*dt)
                                 │
                                 ▼
                          BuildMatricesJob
                          (visible birds only;
                           pos+vel→Matrix4x4)
                                 │
                                 ▼
                  foreach (flock in registered)
                    RenderMeshIndirect(
                      flock.Mesh, flock.Material,
                      visibleMatrices[flock.SliceRange],
                      visibleCount)
```

Note the parallel branches: `NeighborForcesJob`, `BoundsForcesJob`, `CursorForceJob` all depend on `BuildGridJob` (only Neighbor actually reads it — the others are scheduled in parallel for free wall-clock win). `FrustumCullJob` is fully independent and runs in parallel with the entire steering chain.

---

## 3. Module Map

Each module is its own `.asmdef`. M5 grew to absorb input + camera (the interactive deliverable's UX surface).

| Module | asmdef | Owner | Depends on |
|---|---|---|---|
| **M0 Foundation** | `Bird_behiviour.Flocking.Core` | _shared_ | — |
| **M1 Simulation** | `Bird_behiviour.Flocking.Simulation` | Dev A (lead) | Core |
| **M2 Spatial** | `Bird_behiviour.Flocking.Spatial` | Dev B | Core |
| **M3 Behaviors** | `Bird_behiviour.Flocking.Behaviors` | Dev B (or C) | Core, Spatial |
| **M4 Rendering** | `Bird_behiviour.Flocking.Rendering` | Dev C (or D) | Core |
| **M5 Tooling + UX** | `Bird_behiviour.Flocking.Tooling` (+ `.Editor`) | Dev D | Core, Simulation |
| **M6 Tests** | `Bird_behiviour.Flocking.Tests.{EditMode,PlayMode}` | _shared_ | all |

Folder layout under `Assets/Scripts/Flocking/`: `Core/`, `Simulation/`, `Spatial/`, `Behaviors/`, `Rendering/`, `Tooling/`, `Editor/`, `Tests.EditMode/`, `Tests.PlayMode/`.

---

## 4. Public Contracts (Core asmdef)

These types live in `Bird_behiviour.Flocking.Core` and **must not change** without RFC + sign-off from all module owners. Everything else is module-private.

```csharp
// FlockSlice.cs — a flock's range within FlockWorld's flat arrays
public readonly struct FlockSlice {
    public readonly int  StartIndex;
    public readonly int  Count;
    public readonly byte FlockId;       // 0..255
}

// FlockState.cs — read-only world view passed to renderer & tests
public readonly struct FlockState {
    public readonly NativeArray<float3>.ReadOnly Positions;
    public readonly NativeArray<float3>.ReadOnly Velocities;
    public readonly NativeArray<byte>.ReadOnly   FlockIds;
    public readonly NativeArray<FlockSlice>.ReadOnly Slices;
    public readonly int Count;          // sum across all flocks
}

// IFlockSettings.cs — stable surface a ScriptableObject implements
public interface IFlockSettings {
    // Self-flock weights
    float InSeparationWeight   { get; }
    float InAlignmentWeight    { get; }
    float InCohesionWeight     { get; }
    // Cross-flock weights (binary: applied uniformly to all OTHER flocks)
    float OutSeparationWeight  { get; }
    float OutAlignmentWeight   { get; }
    float OutCohesionWeight    { get; }
    // Bounds (per-flock soft preferred zone)
    float3 PreferredCenter     { get; }
    float3 PreferredExtents    { get; }
    float  PreferredAttractionWeight { get; }
    // Perception
    float  PerceptionRadius    { get; }
    float  SeparationRadius    { get; }
    float  PerceptionConeHalfAngleRadians { get; }   // ~2.36 (135°) default
    // Motion
    float  MinSpeed            { get; }
    float  MaxSpeed            { get; }
    float  MaxAcceleration     { get; }
    // Cursor reaction (always-on signed strength)
    float  CursorReactionStrength { get; }   // +attract, -repel, 0 ignore
    float  CursorReactionRadius   { get; }   // falloff distance
    // Visual + lifecycle
    int    BirdCount           { get; }
    Mesh   BirdMesh            { get; }
    Material BirdMaterial      { get; }
    uint   RandomSeed          { get; }      // 0 = auto from time
}

// IFlockWorldSettings.cs — world-level config exposed by FlockWorld
public interface IFlockWorldSettings {
    float3 WorldBoundsCenter   { get; }
    float3 WorldBoundsExtents  { get; }      // hard bounds
    float  WorldBoundsWeight   { get; }      // strength of inward steer
    float  MaxSimDt            { get; }      // clamp to avoid tunneling, default 1/30
    float  SimSpeedMultiplier  { get; }      // 1.0 normal; useful for slow-mo demos
}

// ISpatialIndex.cs — cell-list grid, exposed by Spatial module
public interface ISpatialIndex {
    // Schedule the 3-pass build: count → prefix sum → scatter
    JobHandle ScheduleBuild(
        NativeArray<float3>.ReadOnly positions,
        int count,
        JobHandle deps);

    // Burst-friendly read view, used inside other jobs via [ReadOnly]
    SpatialIndexReadOnly AsReadOnly();
}

// SpatialIndexReadOnly — passed by value into jobs; iterates one cell + 26 neighbors
public readonly struct SpatialIndexReadOnly {
    // Used as: foreach via NeighborEnumerator (a contiguous range read,
    // NOT IEnumerable — Burst-compatible struct enumerator).
    public NeighborEnumerator GetNeighbors(float3 queryPosition);
    public int CellCount { get; }
    public float CellSize { get; }
}

// IFlockRenderer.cs — per-flock rendering
public interface IFlockRenderer {
    void Render(
        FlockSlice slice,
        Mesh mesh,
        Material material,
        NativeArray<float4x4>.ReadOnly visibleMatrices,
        int visibleCount,
        Camera camera);
    void Dispose();
}
```

**Rule:** modules talk through these contracts. No module references another's concrete class. `IFlockSettings` is implemented by `FlockSettings : ScriptableObject` in M5; everything else consumes the interface so tests can stub it.

---

## 5. Phasing & Milestones

### Phase 0 — Foundation (Days 1–3, all hands)
Days 1–2: M0-1 through M0-5 collaboratively. Day 3: contracts (M0-2) review pass — all owners sign off before parallel work begins.

### Phase 1 — Vertical slice (Days 4–9, **one dev**)
Dev A solo. Builds FlockWorld + 1 FlockManager + stubs of all other modules running end-to-end with 100 birds, naive O(n²), main-thread, default URP material, 5-vert cone, fly-cam working, cursor working but ignored. Other devs review the slice on Day 7. Contract changes negotiated here, never in Phase 2.

### Phase 2 — Module hardening (Weeks 2–3, parallel)
Each owner replaces their stub with the real implementation behind the same contract. Stubs live behind a `RendererMode`/`SpatialMode`/etc. enum on settings so old + new can run side-by-side during integration.

### Phase 3 — Performance & polish (Week 4)
Profiler-driven. Diagnostics owner (Dev D) drives. Perf gate (M6-5) is the exit criterion: 50k birds at ≤16.6ms median on lowest-spec team M-series Mac, captured for 10s in `Flocking_Sandbox` with the default fly-cam pose.

---

## 6. Work Packages (GitHub issues)

Each module gets one **epic** issue with linked sub-issues. Sub-issues sized for one PR (≤400 LOC, ≤2 days).

### M0 — Foundation (sequential, blocks everything)
1. **M0-1 Repo skeleton.** Folder layout, all asmdefs (empty), `docs/CODING_CONVENTIONS.md`, `.editorconfig`, `.unity-version` file containing `6000.1.15f1`. **Delete `Assets/TutorialInfo/`** (and its `.meta`s). Update `CLAUDE.md` with new folder structure. *Acceptance:* all asmdefs compile; tutorial gone; pre-existing scene still opens.
2. **M0-2 Core contracts.** Define every type in §4. Full XML doc comments on every public field/method. *Acceptance:* compiles, comments rendered correctly in Rider/VS, contracts review meeting held with all module owners signing off via PR approval.
3. **M0-3 (DEFERRED to v1.1) — Automated CI.** Initially planned as `game-ci/unity-test-runner@v4` on Linux runners with Unity license secrets. **Cut from v1** because the team has local Unity + MCP-assisted workflows; tests run locally pre-merge instead, captured in PR description. Re-add when team grows or honor-system breaks. Self-hosted Mac runner is the lighter re-add path (no secrets, reuses local activation).
4. **M0-4 Reference scene.** `Assets/Scenes/Flocking_Sandbox.unity` containing: one `FlockWorld` GameObject, two `FlockManager` GameObjects (predator + prey) referencing two `FlockSettings` assets, one fly-cam, default lighting (URP). Camera positioned outside `WorldBounds` looking inward. *Acceptance:* scene opens without errors; bounds gizmos visible (world AABB + 2 preferred zones).
5. **M0-5 Burst cache .gitignore.** Add `Library/PackageCache/` and `Temp/` already-covered, plus explicit `BurstCache/` and `**/*.bclib` (Burst's local artifacts). *Acceptance:* no Burst artifacts staged after a clean build.

### M1 — Simulation Core (Dev A)
**Epic:** Manage all per-bird state, the per-frame job graph, and flock registration.

1. **M1-1 `FlockWorld` lifecycle.** MonoBehaviour singleton-per-scene. On `Awake`, allocate world arrays sized to sum of registered `FlockManager.RequestedCount` (which is 0 until M1-2 lands; for now, parameterize on a serialized `int InitialCapacity`). On `OnDestroy`, dispose all NativeArrays. Implement `IFlockWorldSettings` directly on the MonoBehaviour with serialized fields.
2. **M1-2 `FlockManager` slice management.** On `OnEnable`, call `FlockWorld.RegisterFlock(this)` which assigns a `FlockId` (next free 0..255) and a `(StartIndex, Count)` slice. `OnDisable` deregisters and shifts later slices down (or marks slot free). Slice metadata exposed as `FlockSlice` struct.
3. **M1-3 Job graph orchestration.** In `FlockWorld.LateUpdate`, schedule jobs per the §2 graph using `JobHandle.CombineDependencies` for the parallel branches. Complete the chain. Pass `[ReadOnly]` views of world arrays into jobs. Use `Allocator.TempJob` for per-frame intermediate arrays (`accelNeighbor`, `accelBounds`, `accelCursor`, `visibleIndices`, `visibleCount`).
4. **M1-4 `Tick(float dt)` entry point.** Refactor `LateUpdate` to call `Tick(simDt)` where simDt = clamped/scaled `Time.deltaTime`. Tests bypass `LateUpdate` and call `Tick(1/60f)` directly for determinism.
5. **M1-5 Spawn + per-flock RNG.** Each `FlockManager`, on register, spawns its slice using `Unity.Mathematics.Random` seeded from `FlockSettings.RandomSeed` (or `(uint)(Time.realtimeSinceStartup*1e6)` if seed=0). Random positions inside `PreferredCenter ± PreferredExtents`; random velocities of length in `[MinSpeed, MaxSpeed]`.
6. **M1-6 `Rebuild()`.** Exposed by `FlockWorld` and `FlockManager`. Disposes + reallocates affected arrays, re-registers flocks, re-spawns birds (reuses RandomSeed). Called by M5's Apply button. **Must not allocate any managed memory** outside the realloc itself.

### M2 — Spatial Index (Dev B)
**Epic:** Cell-list grid via counting sort. Bounded by `FlockWorld.WorldBoundsExtents`. Cell size = max `PerceptionRadius` across all registered `FlockSettings`.

1. **M2-1 Cell-list data structure.** `SpatialHashGrid` struct holding three `NativeArray<int>`: `cellCount` (size = total cells), `cellOffset` (size = total cells + 1), `cellBirds` (size = bird count). All `Allocator.Persistent`. Hash function: `int3(floor((pos - boundsMin) / cellSize)) → int` via Z-order or row-major; row-major is fine.
2. **M2-2 `BuildGridJob` (3 passes).** Pass 1: `IJobParallelFor` per bird — compute cellHash, `Interlocked.Increment(ref cellCount[cellHash])`. Pass 2: single-threaded `IJob` — prefix sum `cellCount → cellOffset`. Pass 3: `IJobParallelFor` per bird — compute cellHash, atomic-fetch-add an offset slot in a temporary `cellWriteCursor` array, write bird index to `cellBirds[cellOffset[cellHash] + slot]`. *Acceptance:* benchmark shows ≥3× speedup over a naive O(n²) baseline at 5k birds; deterministic given fixed input.
3. **M2-3 `NeighborEnumerator`.** Burst-friendly struct returned by `SpatialIndexReadOnly.GetNeighbors(queryPos)`. Internal state: current cell index in 27-cell range, current bird offset within cell. `MoveNext()` advances; `Current` returns bird index. **No managed allocation, no `IEnumerable<T>`** — used via `for` loops.
4. **M2-4 Cell-size auto-derivation.** `FlockWorld.RegisterFlock` recomputes `cellSize = max(perceptionRadius across all flocks)` and triggers `Rebuild()` if changed. Edge case: if max changes but bird count doesn't, only the grid arrays realloc — world arrays don't.

### M3 — Steering Behaviors (Dev B or C)
**Epic:** Per-bird acceleration from neighbor scan, bounds (world hard + per-flock soft), cursor influence.

1. **M3-1 `NeighborForcesJob`.** `[BurstCompile]` `IJobParallelFor`. For each bird, iterate its `NeighborEnumerator`. For each neighbor: compute distance; skip if > `perceptionRadius`; compute `dot(velNorm, toNeighborNorm)` and skip if outside cone half-angle (with zero-velocity fallback to 360°). Accumulate sep/align/cohere using **in-flock weights** if `neighbor.flockId == self.flockId`, else **out-of-flock weights**. Output: `accelNeighbor[i]`. Guard: skip cohesion/alignment normalization if neighbor count == 0.
2. **M3-2 `BoundsForcesJob`.** `[BurstCompile]` `IJobParallelFor`. No grid. For each bird: compute world hard bounds force (sharp inward ramp when `|pos - WorldBoundsCenter|` exceeds `WorldBoundsExtents - margin`); compute per-flock preferred zone force (gentle attraction toward `PreferredCenter` weighted by `PreferredAttractionWeight × (1 - distance/PreferredExtents.maxComponent)`). Output: `accelBounds[i]`.
3. **M3-3 `CursorForceJob`.** `[BurstCompile]` `IJobParallelFor`. For each bird: distance to `cursorWorldPoint`; if distance > `CursorReactionRadius` for this bird's flock, force = 0; else `force = sign(CursorReactionStrength) * |CursorReactionStrength| * falloff(distance) * normalizedDirToCursor` (positive sign = toward, negative = away). Output: `accelCursor[i]`.
4. **M3-4 `IntegrateJob`.** `[BurstCompile]` `IJobParallelFor`. `accel = accelNeighbor[i] + accelBounds[i] + accelCursor[i]`; cap `length(accel) ≤ MaxAcceleration` via `normalizesafe` pattern; `vel += accel * dt`; clamp `length(vel)` to `[MinSpeed, MaxSpeed]` using `lengthsq` comparison; `pos += vel * dt`.
5. **M3-5 EditMode property tests.** Spawn small fixed configurations (2 birds, 3 birds, etc.); assert: zero-neighbor bird only feels bounds + cursor; two birds at exactly `SeparationRadius` get equal-and-opposite separation; cone test rejects neighbor directly behind; in-flock vs out-of-flock weights apply correctly.

### M4 — Rendering (Dev C or D)
**Epic:** Per-flock GPU-instanced indirect rendering with CPU frustum culling.

1. **M4-1 `BuildMatricesJob`.** `[BurstCompile]` `IJobParallelFor` over `visibleIndices[0..visibleCount]`. For each visible bird: build `float4x4` from position + look-along-velocity rotation. Writes to `visibleMatrices` array sized to `visibleCount`. Use `quaternion.LookRotationSafe` to handle zero-velocity.
2. **M4-2 `FrustumCullJob`.** `[BurstCompile]` `IJobParallelFor` per bird. For each: test position against the 6 cached frustum planes (with a small radius pad ~2× bird size to prevent edge-popping). If visible: atomic-write bird index to `visibleIndices[atomicInc(visibleCount)]`. Output is unsorted but that's fine — `BuildMatricesJob` doesn't care.
3. **M4-3 `ProceduralBirdMesh.Build()`.** Returns a 5-vertex cone mesh (apex + 4 base vertices, 4 triangles). One-time allocation in `FlockWorld.Awake`. Designers can override per `FlockSettings.BirdMesh`.
4. **M4-4 Per-flock dispatch loop.** `IFlockRenderer.Render` called once per registered flock from `FlockWorld`. Internally: compute that flock's visible matrices range (from `visibleIndices` filtered by `flockId == flock.FlockId`, or — preferred — run `FrustumCullJob` *per flock* so visibleIndices are already segmented). Issue `Graphics.RenderMeshIndirect` with that flock's `Mesh` + `Material` + indirect args buffer. Material must have `Enable GPU Instancing` checked.
5. **M4-5 GraphicsBuffer pool per flock.** Each flock owns: a `GraphicsBuffer` for instance data (matrices), a `GraphicsBuffer` for indirect args (`GraphicsBuffer.IndirectDrawIndexedArgs`). Allocated on `RegisterFlock`, disposed on `Deregister` or `Rebuild`. Sized to `flock.Count` (worst-case all visible).

### M5 — Tooling & UX (Dev D)
**Epic:** Make this tunable, observable, and *playable*. M5 owns all UX-facing surfaces.

1. **M5-1 `FlockSettings : ScriptableObject, IFlockSettings`.** Inspector-friendly grouped headers (Self Behavior, Cross-Flock Behavior, Bounds, Perception, Motion, Cursor, Visual, Lifecycle). `OnValidate` clamps weights ≥ 0 and asserts `SeparationRadius ≤ PerceptionRadius`. Tag fields with `[FlockTunable]` (live) vs `[FlockStructural]` (Apply-button) attributes for M5-2.
2. **M5-2 Custom `FlockSettings` inspector.** Two-column layout. Tunable fields edit live (write-through). Structural fields edit a *staging* copy; an "Apply Structural Changes" button at top commits via `FlockManager.Rebuild()`. Button enabled iff staging differs from applied. "Randomize Seed" + "Copy Seed" mini-buttons next to `RandomSeed`.
3. **M5-3 `FlockWorld` custom inspector.** Sliders for `MaxSimDt`, `SimSpeedMultiplier`. World bounds editable (structural — Apply button). "Restart Sim" big button at bottom. Read-only display: total bird count, number of registered flocks, current `cellSize`.
4. **M5-4 `FlockGizmoDrawer`.** Scene view. Draws world bounds (white wire AABB), per-flock preferred zones (color-coded translucent AABB per flock). Optional toggle: spatial grid cells (shows occupancy density via cell color). Off by default; per-flock visibility toggle in inspector.
5. **M5-5 `FlockHud`.** IMGUI overlay (top-left). Shows: fps (smoothed), total bird count, frame ms, per-job ms (read from `ProfilerMarker` deltas: `Flock.BuildGrid`, `Flock.Neighbor`, `Flock.Bounds`, `Flock.Cursor`, `Flock.Cull`, `Flock.Integrate`, `Flock.Matrices`, `Flock.Render`). Toggle with F3 (key configurable on `FlockWorld`).
6. **M5-6 `FlyCameraController`.** WASD = local-axis movement scaled by `WorldBoundsExtents.maxComponent / 30`; mouse-look = pitch (clamped ±85°) + yaw; Shift = ×4 speed; Space/Ctrl = up/down world-space. Soft tether: when camera position exceeds `WorldBounds.extents × 1.5`, spring-pull velocity inward. Reads `Mouse.current` + `Keyboard.current` directly (no InputSystem actions for v1).
7. **M5-7 `CursorInputController`.** Each frame: raycast from `Camera.main` through screen cursor onto the horizontal plane through `WorldBoundsCenter`. Write hit point + `cursorOnScreen` bool to `FlockWorld.CursorWorldPoint`. When camera is below or parallel to the plane, set `cursorOnScreen = false` and `CursorForceJob` skips the influence.

### M6 — Tests & gates (shared, single owner for the asmdef)
1. **M6-1 Test asmdef setup.** `Bird_behiviour.Flocking.Tests.EditMode.asmdef` and `.PlayMode.asmdef`, both with TestAssemblies reference. One sample test green per assembly.
2. **M6-2 EditMode math tests.** Pure-function tests for each force kernel (extract from jobs into static helpers if needed). Property-style assertions: 2-bird symmetry, zero-neighbor edge cases, cone rejection, in-vs-out weight branching.
3. **M6-3 PlayMode integration test.** Spawn 1000 birds across 2 flocks. Run 60 frames at fixed `dt=1/60` via `FlockWorld.Tick`. Assert: no NaN positions; no exceptions logged; every bird inside its `WorldBounds × 1.5` (allow tunneling through soft preferred zone but not world hard).
4. **M6-4 Allocation regression.** PlayMode test, 5000 birds, 60 frames. Use `Profiler.GetMonoUsedSizeLong()` snapshot before/after. Assert delta ≤ 1KB (allow noise, fail hard on actual leaks).
5. **M6-5 Manual perf gate (release-blocking).** Procedure documented here, **not** automated:
   - Open `Flocking_Sandbox` with default 2-flock 50k-bird config.
   - Position camera at default fly-cam pose.
   - Run for 10 seconds (no input).
   - Read `FrameTimingManager` percentiles; record p50, p95.
   - **Pass:** p50 ≤ 16.6 ms.
   - Recorded in PR description for any perf-sensitive PR; release blocked until passing on lowest-spec team Mac.

---

## 7. Definition of Done (every PR)

A PR is mergeable iff **all** of these are true. Reviewer checks each:

- [ ] All new jobs are `[BurstCompile]`. Verified via Burst Inspector.
- [ ] No managed allocations in jobs or per-frame methods. Verified by Profiler "GC Alloc" column == 0 over 60 frames in `Flocking_Sandbox`.
- [ ] Every `NativeArray`/`SpatialHashGrid`/`GraphicsBuffer` allocated has a matching `Dispose()` in `OnDestroy`/`Deregister`/test teardown. `NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace` set in tests.
- [ ] Public APIs (anything exported from the module's asmdef) carry XML doc comments.
- [ ] EditMode test for any new pure math; PlayMode test for any new visible behavior.
- [ ] **Tests pass locally** (compile + EditMode + PlayMode); paste tests-run summary in PR description.
- [ ] PR ≤ 400 LOC of production code where possible. If larger, tag in description with reason.
- [ ] Touches only files within the PR's owning module — cross-module changes need a "contracts" PR (touching `Core` only) first.
- [ ] **Perf-sensitive PRs only:** M6-5 perf gate run locally, p50 captured in PR description, ≤ 16.6 ms.

**One-time setup** (do once, before first PR of any module):
- `.unity-version` file committed at repo root.

---

## 8. Coding Conventions (lives at `docs/CODING_CONVENTIONS.md`)

**Allocator hygiene.** `Allocator.Persistent` for arrays that live the lifetime of `FlockWorld`/`FlockManager`. `Allocator.TempJob` for per-frame scratch (must be disposed within 4 frames; in our case, completed within the same frame). `Allocator.Temp` only inside Burst jobs.

**No reflection, no LINQ, no `foreach` over `NativeArray`** in hot paths — they all allocate or defeat Burst.

**Math types.** `Unity.Mathematics` (`float3`, `float4x4`, `math.normalize`) everywhere in jobs. `UnityEngine.Vector3` only at the boundary with classic Unity APIs (transforms, gizmos, raycasts).

**Naming.** Jobs end in `Job`. ScriptableObjects end in `Settings` or `Asset`. Public Burst-friendly value types end in `Data`/`Slice`/`State`.

**File scope.** One public type per file. Private helpers in the same file are fine.

**Branch naming.** `feat/m{N}-{slug}` for module work, `fix/{slug}` for bugs, `chore/{slug}` for tooling, `docs/{slug}` for docs.

**Burst safety check setting.** `Safety Checks` ON in Editor (catches bugs); OFF in Player builds (perf). This is Unity's default; do not change.

---

## 9. Risks & Open Questions

- **Risk:** `cellWriteCursor` atomic contention in `BuildGridJob` Pass 3 if many birds in one cell. *Mitigation:* M2-4 cell-size policy keeps avg occupancy ~10; max realistic ~50. `Interlocked.Increment` at 50 contenders is fine on Apple Silicon.
- **Risk:** `Graphics.RenderMeshIndirect` per-flock dispatch may hit driver overhead at >10 flocks. *Mitigation:* v1 demo uses 2 flocks. >10 flocks is a v1.1 concern (would consolidate to one indirect call with shader-side flockId variation).
- **Risk:** Mac `FrameTimingManager` API has historically been spotty; perf measurement may need fallback to raw `Time.deltaTime` averaging. *Mitigation:* M6-5 procedure tries `FrameTimingManager` first, falls back to `Time.deltaTime` median over 600 samples.
- **Risk:** Cursor force "always-on" UX makes passive flock observation impossible. *Mitigation:* designers can set `CursorReactionStrength = 0` per flock during demos meant to show passive behavior. Document in M5-7.
- **Open:** Bird intra-flock visual variation (per-bird color/scale jitter). Default: none in v1; trivial v1.1 polish.
- **Open:** Multi-flock UX for >2 flocks (cross-flock relationship inspector). v1 ships binary self-vs-other; matrix UX is a v1.1 if the demo expands.
- **Open:** Across-machine perf gate on CI. Currently local-manual; could add a self-hosted Mac runner later (operational burden).

---

## 10. Suggested Sprint Layout

| Week | Goal | Owner activity |
|---|---|---|
| 1 (days 1–3) | M0 collaborative + contracts review | All hands on M0; Day 3 contracts sign-off |
| 1 (days 4–9) | Phase 1 vertical slice | Dev A solo; B/C/D review on Day 7 |
| 2 | M2, M3 stubs replaced with real impls | Dev B owns M2+M3, Dev C unblocks M4 prep |
| 3 | M4 + M5 hardening; M6 grows | Dev C/D on M4, Dev D on M5, owner-rotated M6 |
| 4 | M6-5 perf gate + polish | All hands on perf; Diagnostics owner drives |
