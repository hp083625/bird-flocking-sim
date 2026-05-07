// InstancedFlockRenderer.cs — Slice 2 IFlockRenderer implementation using
// Graphics.RenderMeshInstanced in chunks of 1023 (Unity's per-call instance cap).
// Slice 6 (M4) will replace this with Graphics.RenderMeshIndirect for bigger flocks.

using Bird_behiviour.Flocking.Core;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Bird_behiviour.Flocking.Rendering
{
    /// <summary>
    /// <see cref="IFlockRenderer"/> implementation that uses
    /// <see cref="Graphics.RenderMeshInstanced{T}(in RenderParams, Mesh, int, T[], int, int)"/>
    /// in batches of <c>1023</c> (Unity's per-call instance cap).
    /// </summary>
    /// <remarks>
    /// <b>Material requirement.</b> The supplied material <em>must have</em>
    /// <c>Enable GPU Instancing</c> checked in its inspector — without it Unity will fall
    /// back to one draw call per bird and performance collapses. The Slice 2 sandbox scene
    /// wires this up; lead is responsible for verifying it in the M0 scene-wiring step.
    /// <para/>
    /// <b>Allocation profile.</b> Holds a pooled <c>Matrix4x4[1023]</c> CPU staging buffer
    /// reused across frames (no per-frame managed alloc). The Slice 6 indirect-draw renderer
    /// will replace this with a persistent <see cref="GraphicsBuffer"/>.
    /// </remarks>
    public sealed class InstancedFlockRenderer : IFlockRenderer
    {
        private const int BatchSize = 1023;

        // Reusable staging buffer to avoid per-frame allocations. Sized to the per-call cap.
        private readonly Matrix4x4[] batchBuffer = new Matrix4x4[BatchSize];

        // Cached RenderParams reset each Render call for the current camera + material.
        private RenderParams renderParams;

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

            renderParams = new RenderParams(material)
            {
                worldBounds       = new Bounds(Vector3.zero, Vector3.one * 1e6f),
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows    = true,
                camera            = camera,
                layer             = 0,
            };

            int remaining = visibleCount;
            int sourceIndex = 0;
            while (remaining > 0)
            {
                int thisBatch = remaining < BatchSize ? remaining : BatchSize;

                // Copy native float4x4 → managed Matrix4x4 staging buffer.
                for (int i = 0; i < thisBatch; i++)
                {
                    batchBuffer[i] = (Matrix4x4)visibleMatrices[sourceIndex + i];
                }

                Graphics.RenderMeshInstanced(in renderParams, mesh, 0, batchBuffer, thisBatch);

                sourceIndex += thisBatch;
                remaining   -= thisBatch;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // No unmanaged resources held in Slice 2; method exists to satisfy the contract
            // and so that Slice 6's indirect-draw replacement is a drop-in swap.
        }
    }
}
