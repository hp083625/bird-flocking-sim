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
    /// in 1023-instance batches). Later slices may swap this for an indirect-draw renderer
    /// behind the same interface. The renderer is owned by the <c>FlockManager</c>: created
    /// in <c>OnEnable</c>, disposed in <c>OnDisable</c>.
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
        /// Read-only view of the world matrices for this flock's birds. Sized to at least
        /// <paramref name="visibleCount"/>.
        /// </param>
        /// <param name="visibleCount">Number of valid matrices to render from the start of the array.</param>
        /// <param name="camera">Active camera for any per-camera renderer state.</param>
        void Render(
            FlockSlice slice,
            Mesh mesh,
            Material material,
            NativeArray<float4x4>.ReadOnly visibleMatrices,
            int visibleCount,
            Camera camera);

        /// <summary>
        /// Releases any GraphicsBuffers, command buffers, or other unmanaged resources held
        /// by the renderer. Called by the owning <c>FlockManager</c> in <c>OnDisable</c>.
        /// </summary>
        void Dispose();
    }
}
