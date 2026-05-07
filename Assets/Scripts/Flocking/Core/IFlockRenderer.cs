// IFlockRenderer.cs — per-flock rendering contract. M4 Rendering implements; FlockWorld dispatches.

using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Bird_behiviour.Flocking.Core
{
    /// <summary>
    /// Per-flock rendering contract. <c>FlockWorld</c> calls <see cref="Render"/> once per
    /// registered flock per frame, after the simulation step has produced the world matrices
    /// for every bird in the flock's slice.
    /// </summary>
    /// <remarks>
    /// Slice 2 ships <c>InstancedFlockRenderer</c> (uses <c>Graphics.RenderMeshInstanced</c>
    /// in 1023-instance batches). Slice 9 ships <c>IndirectFlockRenderer</c>
    /// (uses <c>Graphics.RenderMeshIndirect</c> with a per-flock <c>GraphicsBuffer</c>
    /// pool of world matrices). The renderer is owned by the <c>FlockManager</c>: created
    /// in <c>OnEnable</c>, disposed in <c>OnDisable</c>.
    /// <para/>
    /// <b>Slice 9 contract change.</b> <c>visibleMatrices</c> is now passed as a
    /// <see cref="NativeArray{T}"/> rather than a <c>NativeArray.ReadOnly</c> so that
    /// indirect-draw renderers can call <c>GraphicsBuffer.SetData(NativeArray, ...)</c>
    /// directly without a per-frame intermediate copy. Implementations are still expected
    /// to treat the array as read-only — write contention with the producing
    /// <c>BuildMatricesJob</c> is undefined behaviour.
    /// </remarks>
    public interface IFlockRenderer
    {
        /// <summary>
        /// Renders one flock for the current frame.
        /// </summary>
        /// <param name="slice">The flock's slice within the world's flat arrays.</param>
        /// <param name="mesh">Per-flock mesh (typically from <c>IFlockSettings.BirdMesh</c>).</param>
        /// <param name="material">
        /// Per-flock material (typically from <c>IFlockSettings.BirdMaterial</c>).
        /// Must have GPU instancing enabled in the inspector.
        /// </param>
        /// <param name="visibleMatrices">
        /// World matrices for this flock's birds. Sized to at least
        /// <paramref name="visibleCount"/>; only the prefix
        /// <c>[0, visibleCount)</c> is meaningful. The array is owned by
        /// <c>FlockWorld</c>; implementations must not write to it or dispose it.
        /// </param>
        /// <param name="visibleCount">Number of valid matrices to render from the start of the array.</param>
        /// <param name="camera">Active camera for any per-camera renderer state.</param>
        void Render(
            FlockSlice slice,
            Mesh mesh,
            Material material,
            NativeArray<float4x4> visibleMatrices,
            int visibleCount,
            Camera camera);

        /// <summary>
        /// Releases any GraphicsBuffers, command buffers, or other unmanaged resources held
        /// by the renderer. Called by the owning <c>FlockManager</c> in <c>OnDisable</c>.
        /// </summary>
        void Dispose();
    }
}
