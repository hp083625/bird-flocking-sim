# Bird Flocking Simulation — Engineering Plan

Owner: TBD (tech lead)
Audience: 3–4 Unity devs, all comfortable with DOTS (Burst, Jobs, Native collections).
Workflow: GitHub PRs + issues, squash-merge to `main`, 1 approval required, CI green.

---

## 1. Goals & Non-Goals

### Goals
- Render a flock of **5,000–50,000 birds** in a bounded sky volume at ≥60 fps on a modern laptop GPU.
- Behavior driven by classic **boids** (Reynolds 1986): separation, alignment, cohesion + bounds containment + speed clamping.
- All hot-path code is `[BurstCompile]`d and runs on worker threads via `IJobParallelFor`.
- Tunable weights live in a `ScriptableObject` so designers can iterate without code changes.

### Non-Goals (v1)
- Animated meshes — placeholder cone is fine.
- Obstacles, terrain, predators — out of scope; we leave hooks for them but don't ship them.
- ECS/Entities. We use the **Job System without Entities** to keep cognitive load down. Switching later is a refactor we can afford.
- Multiplayer / determinism. Single-player, frame-rate dependent timestep is fine.

---

## 2. Architecture Overview

Single `MonoBehaviour` (`FlockManager`) owns all bird state in `NativeArray`s. Each frame it schedules a job graph, completes it, then issues one (or a few) instanced draw calls. **No GameObject per bird.**

```
                    Update tick
                         │
                         ▼
              ┌──────────────────────┐
              │   FlockManager       │  owns NativeArrays
              │   (Simulation Core)  │
              └──────────┬───────────┘
                         │ schedules
        ┌────────────────┼────────────────┬─────────────────┐
        ▼                ▼                ▼                 ▼
  BuildGridJob ─► SteeringJob ─►  IntegrateJob ─►  BuildMatricesJob
   (Spatial)      (Behaviors)      (Behaviors)        (Rendering)
        │                │                │                 │
        └────────────────┴────────────────┴────────► Graphics.RenderMeshIndirect
```

Data flow: positions+velocities → grid → per-bird neighbor scan → accel → integrated pos+vel → render matrices → draw.

---

## 3. Module Map

Each module is its own assembly definition (`.asmdef`). This isolates compile times, makes ownership unambiguous, and prevents accidental cross-cutting changes.

| Module | asmdef | Owner | Depends on |
|---|---|---|---|
| **M0 Foundation** | `Bird_behiviour.Flocking.Core` | _shared_ | — |
| **M1 Simulation** | `Bird_behiviour.Flocking.Simulation` | Dev A (lead) | Core |
| **M2 Spatial** | `Bird_behiviour.Flocking.Spatial` | Dev B | Core |
| **M3 Behaviors** | `Bird_behiviour.Flocking.Behaviors` | Dev B (or C) | Core, Spatial |
| **M4 Rendering** | `Bird_behiviour.Flocking.Rendering` | Dev C (or D) | Core |
| **M5 Tooling** | `Bird_behiviour.Flocking.Tooling` (+ `.Editor`) | Dev D | Core, Simulation |
| **M6 Tests** | `Bird_behiviour.Flocking.Tests.{EditMode,PlayMode}` | _shared_ | all of above |

Folder layout:
```
Assets/Scripts/Flocking/
  Core/           # M0 — contracts only, no logic
  Simulation/     # M1
  Spatial/        # M2
  Behaviors/      # M3
  Rendering/      # M4
  Tooling/        # M5  (runtime portion)
  Editor/         # M5  (editor-only)
  Tests.EditMode/ # M6
  Tests.PlayMode/ # M6
```

---

## 4. Public Contracts (Core asmdef)

These are the **only** types modules see across boundaries. They live in `Bird_behiviour.Flocking.Core` and **must not change** without RFC + sign-off from all module owners. Everything else is module-private.

```csharp
// FlockState.cs — read-only view passed to renderer & tests
public readonly struct FlockState {
    public readonly NativeArray<float3>.ReadOnly Positions;
    public readonly NativeArray<float3>.ReadOnly Velocities;
    public readonly int Count;
}

// IFlockSettings.cs — stable surface ScriptableObject implements
public interface IFlockSettings {
    int   BirdCount             { get; }
    float PerceptionRadius      { get; }
    float SeparationRadius      { get; }
    float SeparationWeight      { get; }
    float AlignmentWeight       { get; }
    float CohesionWeight        { get; }
    float BoundsWeight          { get; }
    float MinSpeed              { get; }
    float MaxSpeed              { get; }
    float3 BoundsCenter         { get; }
    float3 BoundsExtents        { get; }
}

// ISpatialIndex.cs — Spatial module exposes this; Behaviors consumes it
public interface ISpatialIndex {
    JobHandle ScheduleBuild(NativeArray<float3>.ReadOnly positions, JobHandle deps);
    // Burst-friendly query struct returned by AsReadOnly() pattern; details below.
}

// IFlockRenderer.cs — Rendering module implements; Simulation calls
public interface IFlockRenderer {
    void Render(FlockState state, Camera camera);
    void Dispose();
}
```

**Rule:** modules talk through these contracts. No module references another's concrete class.

---

## 5. Phasing & Milestones

### Phase 0 — Foundation (Days 1–2, all hands)
Everyone aligned on: repo layout, asmdefs, contracts, conventions, branch model. Output: M0 merged.

### Phase 1 — Vertical slice (Days 3–7, **one dev**)
One person (Dev A) builds a working end-to-end with stub modules: 100 birds, naive O(n²) on the main thread, default URP material, basic cone mesh. Validates contracts. Other devs review the slice on Day 5 → contract changes negotiated before parallel work begins.

### Phase 2 — Module hardening (Weeks 2–3, parallel)
Each owner replaces their stub with the real implementation. Stubs ship behind a feature flag in `FlockSettings` so we can A/B compare during integration.

### Phase 3 — Performance & polish (Week 4)
Profiler-driven. Whoever owns Diagnostics drives this. Targets: 50k birds @ 60fps, zero per-frame GC.

---

## 6. Work Packages (GitHub issues)

Each module gets one **epic** issue with linked sub-issues. Sub-issues are sized for one PR (≤400 LOC, ≤2 days).

### M0 — Foundation (sequential, blocks everything)
1. **M0-1 Repo skeleton:** create folder layout, all asmdefs (empty), `docs/CODING_CONVENTIONS.md`, `.editorconfig`. *Acceptance:* all asmdefs compile; CLAUDE.md updated to reflect the new structure.
2. **M0-2 Core contracts:** define the four types in §4. *Acceptance:* compiles, has XML doc comments, included in Core asmdef.
3. **M0-3 CI:** GitHub Action that runs Unity in batchmode, executes EditMode tests, fails on any error/warning from the Flocking assemblies. *Acceptance:* failing test fails the action; passing PRs are green.
4. **M0-4 Reference scene:** `Assets/Scenes/Flocking_Sandbox.unity` with camera positioned for the bounded volume. *Acceptance:* opens without errors; bounds gizmo visible.

### M1 — Simulation Core (Dev A)
**Epic:** Manages bird state lifecycle and the per-frame job graph.
1. **M1-1** `FlockManager` MonoBehaviour. Allocates `pos`, `vel`, `accel`, `matrices` NativeArrays sized to `settings.BirdCount`. `OnDestroy` disposes (assert no leak with `NativeLeakDetection.Mode = Full` in tests).
2. **M1-2** Job graph orchestration: in `LateUpdate`, build → steer → integrate → buildMatrices, complete chain, hand off to renderer. JobHandles tracked, `Dependency` chains validated.
3. **M1-3** Spawn strategy: random positions inside `BoundsExtents`, random velocities clamped to `[MinSpeed, MaxSpeed]`. Deterministic seed exposed for tests.
4. **M1-4** Settings hot-reload: detect `FlockSettings` asset change (use `OnValidate` callback from M5), reallocate arrays only if `BirdCount` changed.

### M2 — Spatial Index (Dev B)
**Epic:** O(1)-amortized neighbor queries via uniform spatial hash.
1. **M2-1** `SpatialHashGrid` struct wrapping `NativeParallelMultiHashMap<int,int>` (cell hash → bird index). Hash = `int3(floor(pos/cellSize)) → int`. `cellSize == perceptionRadius`.
2. **M2-2** `BuildGridJob` (`[BurstCompile]`, `IJobParallelFor`, ParallelWriter). Clears + populates the map each frame.
3. **M2-3** `NeighborEnumerator` — Burst-friendly struct that iterates the 27 neighbor cells (3³) for a query position, yielding bird indices. Used by Behaviors module via `for` loop, not IEnumerator (allocates).
4. **M2-4** Capacity policy: map sized to `2 * birdCount`. If load factor exceeds 0.75, log warning. *Acceptance:* benchmark shows >10× speedup vs. naive O(n²) at 5k birds.

### M3 — Steering Behaviors (Dev B or C)
**Epic:** Per-bird acceleration from neighbor scan + bounds.
1. **M3-1** Single `SteeringJob` (`[BurstCompile]`, `IJobParallelFor`) computing all four forces in one pass. *Why one job, not four:* shares the neighbor iteration, which dominates cost.
2. **M3-2** Bounds containment: soft inward steer when bird is within `boundsMargin` of an extent face. Smooth (cosine), not bang-bang.
3. **M3-3** `IntegrateJob` (`[BurstCompile]`, `IJobParallelFor`): `vel += accel*dt`; clamp speed; `pos += vel*dt`. Speed clamp uses `math.lengthsq` to avoid sqrt when within range.
4. **M3-4** Property-based EditMode tests: zero-neighbor bird only feels bounds force; two birds at separation distance get equal-and-opposite separation force.

### M4 — Rendering (Dev C or D)
**Epic:** Draw N instanced cones from native data, no per-bird GC.
1. **M4-1** `BuildMatricesJob` (`[BurstCompile]`, `IJobParallelFor`): `(pos, vel.normalized) → float4x4` (translation + look-along-velocity). Output: `NativeArray<float4x4>`.
2. **M4-2** `ProceduralBirdMesh.Build()`: returns a 5-vertex cone `Mesh` (apex + 4 base verts). One-time allocation in `Awake`.
3. **M4-3** `InstancedFlockRenderer`: implements `IFlockRenderer` using `Graphics.RenderMeshInstanced` in chunks of 1023. Uses URP/Lit (or custom unlit) with **GPU instancing enabled**. Materials property block reused, never reallocated.
4. **M4-4** `IndirectFlockRenderer`: implements `IFlockRenderer` using `Graphics.RenderMeshIndirect` + `GraphicsBuffer` for matrix data. Required for >~5k birds. Custom URP shader reads matrices from StructuredBuffer.
5. **M4-5** Renderer selection switch in `FlockSettings` (`enum RendererMode { Instanced, Indirect }`) so we can A/B in profiler.

### M5 — Tooling & Diagnostics (Dev D)
**Epic:** Make this tunable, observable, and not-a-black-box.
1. **M5-1** `FlockSettings : ScriptableObject, IFlockSettings`. Inspector-friendly headers. `OnValidate` clamps weights ≥ 0 and `SeparationRadius ≤ PerceptionRadius`.
2. **M5-2** Custom inspector with **runtime sliders** that take effect during Play mode (no reallocation needed for weights).
3. **M5-3** `FlockGizmoDrawer`: draws bounds box + (optionally) spatial grid cells in Scene view. Off by default; toggle in inspector.
4. **M5-4** `FlockHud`: IMGUI overlay showing fps, bird count, frame ms, job ms (via `ProfilerMarker` deltas). Toggle with F3.
5. **M5-5** `ProfilerMarker`s on every job and the render call, named `Flock.Build`, `Flock.Steer`, `Flock.Integrate`, `Flock.Matrices`, `Flock.Render`. Show up in the Profiler window for free.

### M6 — Tests & CI gates (shared)
1. **M6-1** `Bird_behiviour.Flocking.Tests.EditMode.asmdef` with TestAssemblies reference. One sample test green.
2. **M6-2** Math unit tests for each force (separation, alignment, cohesion, bounds) — pure functions, no Unity types.
3. **M6-3** PlayMode integration test: spawn 1000 birds, run 60 frames, assert no NaN positions, no exceptions, every bird inside `BoundsExtents * 1.5`.
4. **M6-4** Allocation regression test: `Profiler.GetMonoUsedSizeLong` delta over 60 frames at 5000 birds == 0. If this fails, someone allocated in a hot path.
5. **M6-5** Perf budget test (PlayMode, optional in CI, mandatory before release): 50k birds median frame time < 16.6ms on a reference machine.

---

## 7. Definition of Done (every PR)

A PR is mergeable iff **all** of these are true. Reviewer checks each:

- [ ] All new jobs are `[BurstCompile]`. Verified by checking Burst Inspector shows them.
- [ ] No managed allocations in jobs or `Update`/`LateUpdate` (verified by Profiler "GC Alloc" column == 0 over 60 frames in the sandbox scene).
- [ ] Every `NativeArray`/`NativeParallelMultiHashMap` allocated has a matching `Dispose()` in `OnDestroy` or test teardown. `NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace` in tests.
- [ ] Public APIs (anything exported from the module's asmdef) carry XML doc comments.
- [ ] EditMode test for any pure math added; PlayMode test for any new visible behavior.
- [ ] CI green: compiles clean (no warnings in Flocking assemblies), all tests pass.
- [ ] PR ≤ 400 LOC of production code where possible. If larger, tag in description with reason.
- [ ] Touches only files within the PR's owning module — cross-module changes need a "contracts" PR first.

---

## 8. Coding Conventions (lives at `docs/CODING_CONVENTIONS.md`)

**Allocator hygiene.** `Allocator.Persistent` for arrays that live the lifetime of `FlockManager`. `Allocator.TempJob` for per-frame scratch (must be disposed within 4 frames). `Allocator.Temp` only inside Burst jobs.

**No reflection, no LINQ, no `foreach` over `NativeArray`** in hot paths — they all allocate or defeat Burst.

**Math types.** Use `Unity.Mathematics` (`float3`, `float4x4`, `math.normalize`) everywhere in jobs. `UnityEngine.Vector3` is allowed only at the boundary with classic Unity APIs (transforms, gizmos).

**Naming.** Jobs end in `Job`. ScriptableObjects end in `Settings` or `Asset`. Public Burst-friendly structs end in `Data`.

**File scope.** One public type per file. Private helpers in the same file are fine.

**Branch naming.** `feat/m{N}-{slug}` for module work, `fix/{slug}` for bugs, `chore/{slug}` for tooling.

---

## 9. Risks & Open Questions

- **Risk:** `NativeParallelMultiHashMap` capacity overflow at high densities. *Mitigation:* M2-4 capacity policy + warning.
- **Risk:** `Graphics.RenderMeshInstanced` with chunked draws may show as a CPU bottleneck at 50k. *Mitigation:* M4-4 indirect path is mandatory before perf gate.
- **Risk:** Frame-time variance from job scheduling on M-series Macs. *Mitigation:* M5-5 markers; perf gate on a reference machine, not arbitrary laptops.
- **Open question:** Do we need a flock-of-flocks (multiple `FlockManager`s)? Affects whether `BoundsCenter` lives on settings (one flock) or a per-instance field (multiple). **Default: single flock for v1.**
- **Open question:** Wraparound bounds vs. soft repel? Soft repel for v1 (more natural); wraparound easy to add later.

---

## 10. Suggested Sprint Layout

| Week | Goal | Owner activity |
|---|---|---|
| 1 | M0 + Phase 1 vertical slice | All hands collaborate on M0 (1 day), then Dev A solo on slice; B/C/D review by Day 5 |
| 2 | M2, M3, M4 stubs replaced with real implementations | Each owner on their module; daily standup |
| 3 | Module hardening continues; M5 starts | M5 was deliberately last — needs M1 surface stable |
| 4 | M6 perf + allocation gates green; release | Diagnostics owner drives perf pass |

