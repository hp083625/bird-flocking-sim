// ProceduralBirdMesh.cs — 5-vertex placeholder bird mesh built once at startup. Designers can
// override per FlockSettings.BirdMesh; the procedural mesh is the fallback. Forward axis = +Z.

using UnityEngine;

namespace Bird_behiviour.Flocking.Rendering
{
    /// <summary>
    /// Builds the 5-vertex placeholder bird mesh used when a flock has no override
    /// <c>BirdMesh</c> assigned. Forward axis = <c>+Z</c>; the mesh is a flattened
    /// tetrahedron with the apex at <c>(0, 0, 0.5)</c> and four base vertices on the
    /// X / Y axes at <c>z = -0.5</c>.
    /// </summary>
    /// <remarks>
    /// One-time allocation; callers should cache the returned <see cref="Mesh"/> and
    /// destroy it via <c>Object.Destroy</c> when the owning subsystem shuts down.
    /// The mesh has 4 triangles, computed normals, and no UVs.
    /// </remarks>
    public static class ProceduralBirdMesh
    {
        /// <summary>Builds and returns a fresh procedural bird mesh.</summary>
        public static Mesh Build()
        {
            var mesh = new Mesh
            {
                name = "ProceduralBird",
            };

            // 5 vertices: apex (forward) + 4 base (+x, -x, +y, -y) at z = -0.5.
            var vertices = new[]
            {
                new Vector3( 0f,    0f,    0.5f), // 0 apex (forward, +Z)
                new Vector3( 0.2f,  0f,   -0.5f), // 1 +X base
                new Vector3(-0.2f,  0f,   -0.5f), // 2 -X base
                new Vector3( 0f,    0.2f, -0.5f), // 3 +Y base
                new Vector3( 0f,   -0.2f, -0.5f), // 4 -Y base
            };

            // 4 triangles, each one a fin from the apex to two adjacent base vertices.
            // Winding chosen so outward-facing normals point away from the body axis.
            var triangles = new[]
            {
                0, 3, 1,   // top-right fin
                0, 1, 4,   // bottom-right fin
                0, 4, 2,   // bottom-left fin
                0, 2, 3,   // top-left fin
            };

            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
