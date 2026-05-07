// GpuIndirectFlockRenderer.cs — Phase P3+ IFlockRenderer for the GPU-compute steering path.
//
// Why this exists alongside IndirectFlockRenderer:
//   The legacy IndirectFlockRenderer owns its own GraphicsBuffer<float4x4> and
//   uploads CPU-side matrices into it via GraphicsBuffer.SetData EVERY FRAME
//   (one PCI/Metal copy per flock per tick). That made sense while the
//   BuildMatricesJob produced matrices on the CPU.
//
//   In the new GPU-compute pipeline (feat/gpu-compute-steering) the SteeringPass
//   compute shader writes world matrices directly into a GraphicsBuffer that IS
//   the render-time matrix buffer. The renderer therefore must NOT allocate or
//   write to the buffer itself — it just borrows the reference from the
//   simulation and points the indirect-draw call at it.
//
// The "no SetData per frame" property is the entire point: a 50k-bird flock
// previously paid 50k * 64B = 3.2 MB of CPU→GPU upload per frame per flock; with
// this renderer that drops to zero. The same applies to the indirect-args
// buffer — once Phase P4 lands the GPU FrustumCullPass writes instanceCount
// directly into the args buffer, and even the args upload disappears.
//
// Buffer ownership:
//   matricesBuffer and argsBuffer are OWNED by the simulation (FlockManager /
//   the GPU steering pipeline). This renderer never disposes them and never
//   writes to them — it only reads via Material.SetBuffer + Graphics.RenderMeshIndirect.
//
// Apple Silicon note:
//   Material.SetBuffer is not free on Metal even when rebinding the same
//   reference (driver re-validates the descriptor). We cache the last-bound
//   GraphicsBuffer reference and skip the SetBuffer call if it hasn't changed.
//   The simulation is expected to call BindMatricesBuffer exactly once per
//   buffer lifetime (after init or after a size-grow reallocation).

using Bird_behiviour.Flocking.Core;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Bird_behiviour.Flocking.Rendering
{
    /// <summary>
    /// <see cref="IFlockRenderer"/> implementation for the GPU-compute steering path.
    /// Consumes a GPU-resident matrices buffer and a GPU-resident indirect-args buffer
    /// by reference (no per-frame upload) and issues a single
    /// <see cref="Graphics.RenderMeshIndirect(in RenderParams, Mesh, GraphicsBuffer, int, int)"/>
    /// per flock.
    /// </summary>
    /// <remarks>
    /// <b>Buffer borrowing.</b> Unlike <see cref="IndirectFlockRenderer"/> this renderer
    /// neither allocates nor disposes the matrices/args <see cref="GraphicsBuffer"/>s.
    /// They are owned by the simulation (the SteeringPass writes the matrices; either the
    /// simulation CPU stub or the Phase P4 FrustumCullPass writes the args). The renderer
    /// holds borrowed references which are nulled — but never disposed — in
    /// <see cref="Dispose"/>.
    /// <para/>
    /// <b>Material clone.</b> Same pattern as <see cref="IndirectFlockRenderer"/>: clone
    /// the caller's source material, retarget at <c>Bird_behiviour/FlockInstancedURP</c>,
    /// copy <c>_BaseColor</c>. The clone is owned and destroyed in <see cref="Dispose"/>.
    /// <para/>
    /// <b>Render hot-path allocations.</b> Zero. After warm-up the per-frame work is a
    /// reference comparison, two struct field writes, and the indirect dispatch.
    /// </remarks>
    public sealed class GpuIndirectFlockRenderer : IFlockRenderer
    {
        // Cached shader property id for the per-instance matrix StructuredBuffer.
        // Looked up once at type init so Render is allocation-free.
        private static readonly int MatricesPropertyId = Shader.PropertyToID("_Matrices");

        // Borrowed references (NOT owned). The simulation allocates and disposes these.
        private GraphicsBuffer matricesBuffer;
        private GraphicsBuffer argsBuffer;

        // Last GraphicsBuffer reference we pushed to clonedMaterial.SetBuffer. Used to
        // skip redundant Metal driver work on Apple Silicon — see file header.
        private GraphicsBuffer lastBoundMatricesBuffer;

        // Cached cloned material (owned). Re-clones when the source material reference
        // changes between Render calls (e.g. inspector swaps the bird material at runtime).
        private Material clonedMaterial;
        private Material cachedSourceMaterial;

        // Reused per-call render params. Mutating fields on a struct value is fine
        // because we pass it `in` to RenderMeshIndirect — no per-frame box.
        private RenderParams renderParams;
        private bool         renderParamsInitialised;

        /// <summary>
        /// Bind the GPU-resident matrices buffer the SteeringPass writes into.
        /// Caller owns the buffer; renderer just borrows the reference. Caller
        /// must call this once after the buffer is allocated and again only if
        /// the buffer is reallocated (size change).
        /// </summary>
        /// <param name="matricesBuffer">
        /// A <see cref="GraphicsBuffer"/> with <see cref="GraphicsBuffer.Target.Structured"/>
        /// whose elements are <c>float4x4</c> (stride 64). Layout must match the shader's
        /// <c>StructuredBuffer&lt;float4x4&gt; _Matrices</c>.
        /// </param>
        public void BindMatricesBuffer(GraphicsBuffer matricesBuffer)
        {
            this.matricesBuffer = matricesBuffer;

            // If the cloned material exists, push the binding now — and update the
            // "last bound" cache so the per-frame skip works on the next Render call.
            // If the material doesn't exist yet (first Render hasn't run), the binding
            // will happen lazily inside EnsureClonedMaterial / the Render path.
            if (clonedMaterial != null && matricesBuffer != null)
            {
                clonedMaterial.SetBuffer(MatricesPropertyId, matricesBuffer);
                lastBoundMatricesBuffer = matricesBuffer;
            }
            else
            {
                // Force the next Render to (re)bind once the material clone exists.
                lastBoundMatricesBuffer = null;
            }
        }

        /// <summary>
        /// Bind the GPU-resident indirect-draw-args buffer the GPU FrustumCullPass
        /// (Phase P4) writes into. Until P4 lands the simulation will write a
        /// constant args struct here from the CPU.
        /// </summary>
        /// <param name="argsBuffer">
        /// A <see cref="GraphicsBuffer"/> with <see cref="GraphicsBuffer.Target.IndirectArguments"/>
        /// containing one <see cref="GraphicsBuffer.IndirectDrawIndexedArgs"/> entry.
        /// </param>
        public void BindIndirectArgsBuffer(GraphicsBuffer argsBuffer)
        {
            this.argsBuffer = argsBuffer;
        }

        /// <summary>
        /// P3 multi-flock hook. Pre-clones the source material if needed and binds the
        /// per-instance flock-id <see cref="GraphicsBuffer"/> + the per-flock RGBA
        /// palette so the vertex shader can tint per bird via
        /// <c>_FlockColors[_InstanceFlockIds[SV_InstanceID]]</c>. Caller invokes once
        /// after init; the cloned material caches both bindings.
        /// </summary>
        public void BindShaderFlockData(Material source, GraphicsBuffer instanceFlockIds, Vector4[] palette)
        {
            EnsureClonedMaterial(source);
            if (clonedMaterial == null) return;
            clonedMaterial.SetBuffer(IdInstanceFlockIds, instanceFlockIds);
            clonedMaterial.SetVectorArray(IdFlockColors, palette);
            clonedMaterial.SetFloat(IdUsePerFlockColor, 1f);
        }

        // Cached property ids for the P3 shader fields (mirrors GpuFlockSimulation).
        private static readonly int IdInstanceFlockIds = Shader.PropertyToID("_InstanceFlockIds");
        private static readonly int IdFlockColors      = Shader.PropertyToID("_FlockColors");
        private static readonly int IdUsePerFlockColor = Shader.PropertyToID("_UsePerFlockColor");

        /// <inheritdoc/>
        /// <remarks>
        /// <paramref name="visibleMatrices"/> and <paramref name="visibleCount"/> are
        /// IGNORED on the GPU path — the matrices are already on the GPU in the buffer
        /// bound via <see cref="BindMatricesBuffer"/>, and the instance count lives in
        /// the indirect-args buffer bound via <see cref="BindIndirectArgsBuffer"/>.
        /// </remarks>
        public void Render(
            FlockSlice slice,
            Mesh mesh,
            Material material,
            NativeArray<float4x4> visibleMatrices,
            int visibleCount,
            Camera camera)
        {
            // Silence unused-parameter warnings without an attribute. These are part of
            // the IFlockRenderer contract but irrelevant to the GPU path.
            _ = slice;
            _ = visibleMatrices;
            _ = visibleCount;

            if (mesh == null || material == null ||
                matricesBuffer == null || argsBuffer == null)
            {
                return;
            }

            // ── 1. Ensure the cloned material exists and is wired to the matrices buffer. ─
            EnsureClonedMaterial(material);
            if (clonedMaterial == null) return; // shader-not-found path; EnsureClonedMaterial logged.

            // Apple-Silicon optimisation: only call SetBuffer when the reference actually
            // changed. The simulation calls BindMatricesBuffer once per buffer lifetime,
            // so in steady state this branch is taken zero times per frame.
            if (!ReferenceEquals(lastBoundMatricesBuffer, matricesBuffer))
            {
                clonedMaterial.SetBuffer(MatricesPropertyId, matricesBuffer);
                lastBoundMatricesBuffer = matricesBuffer;
            }

            // ── 2. Configure RenderParams (cached; only camera/material mutate). ───────
            if (!renderParamsInitialised)
            {
                renderParams = new RenderParams(clonedMaterial)
                {
                    // Conservative AABB — birds can be anywhere inside WorldBounds *
                    // some safety margin. The GPU FrustumCullPass already discarded
                    // off-screen birds at the instance level, so a large CPU-side bound
                    // prevents Unity from re-culling the already-culled set.
                    worldBounds       = new Bounds(Vector3.zero, Vector3.one * 1e6f),
                    shadowCastingMode = ShadowCastingMode.On,
                    receiveShadows    = true,
                    layer             = 0,
                };
                renderParamsInitialised = true;
            }
            renderParams.material = clonedMaterial;
            renderParams.camera   = camera;

            // ── 3. Issue the indirect draw. submeshIndex=0 (placeholder mesh has 1). ──
            // instanceCount comes from the GPU-resident args buffer — we never touch it.
            Graphics.RenderMeshIndirect(in renderParams, mesh, argsBuffer, 1, 0);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
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

            // Borrowed references — clear, do NOT dispose. The simulation owns these
            // and will dispose them in its own teardown path.
            matricesBuffer          = null;
            argsBuffer              = null;
            lastBoundMatricesBuffer = null;
            cachedSourceMaterial    = null;
            renderParamsInitialised = false;
        }

        // ── Material clone management ────────────────────────────────────────────────

        private void EnsureClonedMaterial(Material source)
        {
            // Re-clone if the source reference changed (e.g. inspector swapped the
            // BirdMaterial at runtime). Otherwise the existing clone is reused.
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
                    "[GpuIndirectFlockRenderer] Shader 'Bird_behiviour/FlockInstancedURP' not found — " +
                    "GPU indirect rendering won't work. Is FlockInstancedURP.shader in the project " +
                    "and the URP package installed?");
                return;
            }

            clonedMaterial = new Material(indirectShader)
            {
                name             = source.name + " (GpuIndirect)",
                hideFlags        = HideFlags.HideAndDontSave,
                enableInstancing = true,
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

            // Re-bind the matrices buffer to the freshly cloned material if we already
            // have one. Force the per-frame SetBuffer-skip cache to invalidate so the
            // Render path's ReferenceEquals check fires once and sets the cache.
            if (matricesBuffer != null)
            {
                clonedMaterial.SetBuffer(MatricesPropertyId, matricesBuffer);
                lastBoundMatricesBuffer = matricesBuffer;
            }
            else
            {
                lastBoundMatricesBuffer = null;
            }
        }
    }
}
