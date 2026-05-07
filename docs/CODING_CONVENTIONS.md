# Coding Conventions

Canonical source for the bird-flocking-sim codebase. Mirrors §8 of `FLOCKING_PLAN.md` verbatim — when the two disagree, this file is wrong; reconcile by re-syncing from the plan.

---

**Allocator hygiene.** `Allocator.Persistent` for arrays that live the lifetime of `FlockWorld`/`FlockManager`. `Allocator.TempJob` for per-frame scratch (must be disposed within 4 frames; in our case, completed within the same frame). `Allocator.Temp` only inside Burst jobs.

**No reflection, no LINQ, no `foreach` over `NativeArray`** in hot paths — they all allocate or defeat Burst.

**Math types.** `Unity.Mathematics` (`float3`, `float4x4`, `math.normalize`) everywhere in jobs. `UnityEngine.Vector3` only at the boundary with classic Unity APIs (transforms, gizmos, raycasts).

**Naming.** Jobs end in `Job`. ScriptableObjects end in `Settings` or `Asset`. Public Burst-friendly value types end in `Data`/`Slice`/`State`.

**File scope.** One public type per file. Private helpers in the same file are fine.

**Branch naming.** `feat/m{N}-{slug}` for module work, `fix/{slug}` for bugs, `chore/{slug}` for tooling, `docs/{slug}` for docs.

**Burst safety check setting.** `Safety Checks` ON in Editor (catches bugs); OFF in Player builds (perf). This is Unity's default; do not change.
