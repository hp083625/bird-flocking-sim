// IndirectFlockRenderer.cs — Slice 9 / M4 IFlockRenderer using Graphics.RenderMeshIndirect.
//
// Replaces InstancedFlockRenderer's per-frame managed Matrix4x4[1023] copy + N
// chained Graphics.RenderMeshInstanced calls with a single per-flock indirect
// draw backed by a persistent GraphicsBuffer<float4x4>. The buffer is the
// "pool" — one per renderer instance, lazily allocated, grown geometrically
// when visibleCount exceeds capacity, and disposed in Dispose().
//
// Why this is faster than the Slice 2 path (per FLOCKING_PLAN §6 M4-5 + §9):
//   * Zero CPU→managed array copy. The cull/build pipeline already produces a
//     contiguous NativeArray<float4x4>; we hand it straight to GraphicsBuffer.SetData.
//   * One driver call per flock instead of ceil(N/1023). At 10k birds that's
//     1 call vs 10; at 50k it's 1 vs 49.
//   * Per-instance world matrix is read by the shader from a StructuredBuffer
//     indexed by SV_InstanceID — no need to push a per-instance matrix array
//     through Unity's instancing constant-buffer plumbing every frame.
//
// Material wiring: the renderer accepts a "source" material (from
// FlockSettings.BirdMaterial) and clones it once, swapping the shader to
// FlockInstancedURP. The clone copies the source's _BaseColor so the existing
// authored color survives. The clone is owned and Object.Destroy-d in Dispose().
//
// Allocation hygiene (per AllocationRegressionTest):
//   * After warm-up the only per-frame work is: read mesh.GetIndexCount(0), an
//     indirect-args struct stamp into a cached length-1 array, GraphicsBuffer
//     SetData calls (unmanaged), and a Graphics.RenderMeshIndirect dispatch.
//     None of that allocates managed memory.
//   * Buffer resize triggers a Dispose+new GraphicsBuffer; this happens at most
//     log2(N) times across the lifetime of a flock and is excluded from the
//     allocation-regression steady-state window.

using Bird_behiviour.Flocking.Core;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Bird_behiviour.Flocking.Rendering
{
    /// <summary>
    /// <see cref="IFlockRenderer"/> implementation that uses
    /// <see cref="Graphics.RenderMeshIndirect(in RenderParams, Mesh, GraphicsBuffer, int, int)"/>
    /// with a persistent per-flock <see cref="GraphicsBuffer"/> of world matrices.
    /// </summary>
    /// <remarks>
    /// <b>Buffer pool.</b> One <see cref="GraphicsBuffer"/> of <c>float4x4</c> instances and
    /// one length-1 <see cref="GraphicsBuffer"/> of <see cref="GraphicsBuffer.IndirectDrawIndexedArgs"/>
    /// per renderer (= per flock). The instance buffer is allocated lazily on the
    /// first <see cref="Render"/> call and grown geometrically (×2) when
    /// <c>visibleCount</c> exceeds the current capacity. Both are released in
    /// <see cref="Dispose"/>.
    /// <para/>
    /// <b>Material clone.</b> The first <see cref="Render"/> call clones the
    /// caller's source material, retargets it at <c>Bird_behiviour/FlockInstancedURP</c>,
    /// copies <c>_BaseColor</c>, and binds the per-instance matrix buffer via
    /// <see cref="Material.SetBuffer(int, GraphicsBuffer)"/>. The clone is owned by this
    /// renderer; <see cref="Dispose"/> calls <see cref="Object.Destroy(Object)"/> on it.
    /// Subsequent calls re-validate the cached source reference and re-clone if the
    /// caller swaps materials at runtime (e.g. via the Slice 10 inspector).
    /// <para/>
    /// <b>Allocation contract.</b> After the first <see cref="Render"/> call, the only
    /// allocations on the hot path are unmanaged GraphicsBuffer uploads —
    /// <see cref="AllocationRegressionTest"/> verifies the managed-heap delta is &lt; 1 KB
    /// over 60 ticks at 1000 birds.
    /// </remarks>
    public sealed class IndirectFlockRenderer : IFlockRenderer
    {
        // Cached shader property id for the per-instance matrix StructuredBuffer.
        // Looked up once at type init so Render is allocation-free.
        private static readonly int MatricesPropertyId = Shader.PropertyToID("_Matrices");

        // Initial buffer capacity — small enough that a 0-bird flock doesn't waste
        // much GPU memory, large enough that small demos never trigger a grow.
        private const int InitialCapacity = 256;

        // Per-flock instance-data GraphicsBuffer (StructuredBuffer<float4x4>).
        // Capacity is in elements (matrices), not bytes; stride is 64 (= sizeof(float4x4)).
        private GraphicsBuffer instanceBuffer;
        private int            instanceCapacity;

        // Indirect draw arguments — Graphics.RenderMeshIndirect requires a
        // GraphicsBuffer whose element type is GraphicsBuffer.IndirectDrawIndexedArgs
        // (5 uints: indexCountPerInstance, instanceCount, startIndex, baseVertexIndex, startInstance).
        private GraphicsBuffer argsBuffer;

        // Reusable length-1 staging array for the indirect args upload. NativeArray
        // would be lighter but the GraphicsBuffer.SetData(T[], ...) overload is the
        // simplest one and the one-element array is allocated exactly once.
        private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] argsScratch =
            new GraphicsBuffer.IndirectDrawIndexedArgs[1];

        // Cached cloned material (owned). Re-clones when the source material reference
        // changes between Render calls (e.g. Slice 10 swaps the bird material at runtime).
        private Material  clonedMaterial;
        private Material  cachedSourceMaterial;

        // Reused per-call render params. Mutating fields on a struct value is fine
        // because we pass it `in` to RenderMeshIndirect — no per-frame box.
        private RenderParams renderParams;
        private bool         renderParamsInitialised;

        /// <inheritdoc/>
        public void Render(
            FlockSlice slice,
            Mesh mesh,
            Material material,
            NativeArray<float4x4> visibleMatrices,
            int visibleCount,
            Camera camera)
        {
            if (mesh == null || material == null || visibleCount <= 0)
            {
                return;
            }

            // ── 1. Ensure the cloned material is bound to this mesh + source. ────────
            EnsureClonedMaterial(material);

            // ── 2. Ensure the instance GraphicsBuffer is ≥ visibleCount. ────────────
            EnsureInstanceCapacity(visibleCount);

            // ── 3. Upload the visible-matrix prefix [0, visibleCount) directly from
            //       FlockWorld's NativeArray. The Slice 9 contract change passes the
            //       array (not a ReadOnly view) precisely so we can hit the
            //       NativeArray<T> SetData overload here — zero managed copy. The
            //       backing NativeArray is owned by FlockWorld and stays alive across
            //       the entire Tick + Render dispatch.
            instanceBuffer.SetData(visibleMatrices, 0, 0, visibleCount);

            // ── 4. Stamp the indirect draw args (instance count = visibleCount). ───
            argsScratch[0] = new GraphicsBuffer.IndirectDrawIndexedArgs
            {
                indexCountPerInstance = mesh.GetIndexCount(0),
                instanceCount         = (uint)visibleCount,
                startIndex            = mesh.GetIndexStart(0),
                baseVertexIndex       = mesh.GetBaseVertex(0),
                startInstance         = 0u,
            };
            argsBuffer.SetData(argsScratch);

            // ── 5. Configure RenderParams (cached; mutate camera + worldBounds). ───
            if (!renderParamsInitialised)
            {
                renderParams = new RenderParams(clonedMaterial)
                {
                    // Conservative AABB — birds can be anywhere inside WorldBounds *
                    // some safety margin. We don't have a tight cull-results bound at
                    // this point in the pipeline (FrustumCullJob already discarded
                    // off-screen birds), so a large bound prevents Unity from re-
                    // culling our already-culled set.
                    worldBounds       = new Bounds(Vector3.zero, Vector3.one * 1e6f),
                    shadowCastingMode = ShadowCastingMode.On,
                    receiveShadows    = true,
                    layer             = 0,
                };
                renderParamsInitialised = true;
            }
            renderParams.material = clonedMaterial;
            renderParams.camera   = camera;

            // ── 6. Issue the indirect draw. submeshIndex=0 (placeholder mesh has 1). ─
            Graphics.RenderMeshIndirect(in renderParams, mesh, argsBuffer, 1, 0);

            // Mark slice param as used (silences the IDE-level "unused parameter"
            // warning without an attribute — the slice is informational for future
            // multi-slice-per-flock dispatches).
            _ = slice;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (instanceBuffer != null)
            {
                instanceBuffer.Dispose();
                instanceBuffer = null;
            }
            if (argsBuffer != null)
            {
                argsBuffer.Dispose();
                argsBuffer = null;
            }
            if (clonedMaterial != null)
            {
                // Object.Destroy is the right call at runtime; DestroyImmediate is for
                // Editor-only paths. FlockManager.Dispose runs in PlayMode + Editor
                // teardown, both of which honour Destroy.
                if (Application.isPlaying)
                {
                    Object.Destroy(clonedMaterial);
                }
                else
                {
                    Object.DestroyImmediate(clonedMaterial);
                }
                clonedMaterial = null;
            }
            cachedSourceMaterial = null;
            instanceCapacity = 0;
            renderParamsInitialised = false;
        }

        // ── Material clone management ────────────────────────────────────────────────

        private void EnsureClonedMaterial(Material source)
        {
            // Re-clone if the source reference changed (e.g. Slice 10 swapped the
            // BirdMaterial in the inspector at runtime) OR if our buffer was
            // reallocated and needs to be re-bound (handled in EnsureInstanceCapacity).
            if (clonedMaterial != null && cachedSourceMaterial == source)
            {
                return;
            }

            // Tear down the previous clone (if any) before allocating the new one.
            if (clonedMaterial != null)
            {
                if (Application.isPlaying) Object.Destroy(clonedMaterial);
                else                       Object.DestroyImmediate(clonedMaterial);
                clonedMaterial = null;
            }

            // Find the indirect shader; warn loudly if it didn't ship.
            Shader indirectShader = Shader.Find("Bird_behiviour/FlockInstancedURP");
            if (indirectShader == null)
            {
                Debug.LogError(
                    "[IndirectFlockRenderer] Shader 'Bird_behiviour/FlockInstancedURP' not found — " +
                    "Slice 9 indirect rendering won't work. Is FlockInstancedURP.shader in the project " +
                    "and the URP package installed?");
                return;
            }

            clonedMaterial = new Material(indirectShader)
            {
                name           = source.name + " (Indirect)",
                hideFlags      = HideFlags.HideAndDontSave,
                enableInstancing = true, // GPU instancing flag (matches the brief — set programmatically).
            };

            // Copy the bird's _BaseColor across so the cloned material reproduces the
            // authored look (light blue for PreyA, red for PreyB).
            if (source.HasProperty("_BaseColor"))
            {
                clonedMaterial.SetColor("_BaseColor", source.GetColor("_BaseColor"));
            }
            else if (source.HasProperty("_Color"))
            {
                clonedMaterial.SetColor("_BaseColor", source.GetColor("_Color"));
            }

            cachedSourceMaterial = source;

            // Re-bind the instance buffer if we already have one (capacity bump path
            // disposes the buffer before this clone is rebuilt — the EnsureInstance
            // capacity rebind covers that case).
            if (instanceBuffer != null)
            {
                clonedMaterial.SetBuffer(MatricesPropertyId, instanceBuffer);
            }
        }

        // ── Instance buffer pool ─────────────────────────────────────────────────────

        private void EnsureInstanceCapacity(int requiredCount)
        {
            // First-time allocation: use the larger of InitialCapacity and the
            // first frame's required count to avoid an immediate resize.
            if (instanceBuffer == null)
            {
                instanceCapacity = math.max(InitialCapacity, requiredCount);
                instanceBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    instanceCapacity,
                    UnsafeUtilityMatrixSize); // sizeof(float4x4) = 64 bytes
                BindInstanceBuffer();
                EnsureArgsBuffer();
                return;
            }

            if (requiredCount <= instanceCapacity)
            {
                EnsureArgsBuffer();
                return;
            }

            // Geometric grow (×2 from required count to amortise reallocs). Worst
            // case for a flock at its declared BirdCount, this triggers at most
            // log2(N/InitialCapacity) times across the flock's lifetime.
            int newCapacity = math.max(instanceCapacity * 2, requiredCount);
            instanceBuffer.Dispose();
            instanceCapacity = newCapacity;
            instanceBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                instanceCapacity,
                UnsafeUtilityMatrixSize);
            BindInstanceBuffer();
            EnsureArgsBuffer();
        }

        private void EnsureArgsBuffer()
        {
            if (argsBuffer != null) return;
            argsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments,
                1,
                GraphicsBuffer.IndirectDrawIndexedArgs.size);
        }

        private void BindInstanceBuffer()
        {
            if (clonedMaterial != null && instanceBuffer != null)
            {
                clonedMaterial.SetBuffer(MatricesPropertyId, instanceBuffer);
            }
        }

        // sizeof(float4x4) — written out as a constant so the .Rendering asmdef
        // doesn't need to reference Unity.Collections.LowLevel.Unsafe for one number.
        private const int UnsafeUtilityMatrixSize = 64;

    }
}
