// CursorInputController.cs — raycasts the screen cursor onto the horizontal plane through
// FlockWorld.WorldBoundsCenter each frame and writes the hit point + visibility flag back.
//
// Slice 2 only wires the value through; CursorForce reads it but applies no force (real
// implementation lands in Slice 8, M3-3). M5-7 in FLOCKING_PLAN.md.

using Bird_behiviour.Flocking.Simulation;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Bird_behiviour.Flocking.Tooling
{
    /// <summary>
    /// Each frame: raycasts from <see cref="Camera.main"/> through the screen-cursor position
    /// onto the horizontal plane through <c>FlockWorld.WorldBoundsCenter</c>, and writes the
    /// resulting world point (and a visibility flag) back into the <c>FlockWorld</c>.
    /// </summary>
    /// <remarks>
    /// Uses the new Input System (<c>Mouse.current</c>) for the screen-cursor position, matching
    /// <c>FlyCameraController</c>. No Input Actions asset binding is required.
    /// <para/>
    /// Sets the visibility flag to <c>false</c> (and clamps the world point to
    /// <c>WorldBoundsCenter</c>) whenever a valid hit cannot be produced — no main camera, ray
    /// parallel to the plane, hit behind the camera, or no mouse device. Slice 2's cursor force
    /// is a no-op so the visibility flag is informational; Slice 8 (M3-3) will skip the cursor
    /// influence when the flag is <c>false</c>.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class CursorInputController : MonoBehaviour
    {
        [Tooltip("FlockWorld to write the cursor world-point into. If null, the controller " +
                 "tries FindFirstObjectByType<FlockWorld>() in OnEnable.")]
        [SerializeField] private FlockWorld world;

        [Tooltip("Camera used for the cursor raycast. If null, falls back to Camera.main.")]
        [SerializeField] private Camera cursorCamera;

        private void OnEnable()
        {
            if (world == null)
            {
                world = FindFirstObjectByType<FlockWorld>();
            }
        }

        private void Update()
        {
            if (world == null)
            {
                return;
            }

            Camera cam = cursorCamera != null ? cursorCamera : Camera.main;
            if (cam == null || Mouse.current == null)
            {
                world.SetCursor(world.WorldBoundsCenter, false);
                return;
            }

            // Horizontal plane through WorldBoundsCenter, normal = +Y.
            float3 worldCenter = world.WorldBoundsCenter;
            float planeY = worldCenter.y;

            Vector2 screenPos = Mouse.current.position.ReadValue();
            Ray ray = cam.ScreenPointToRay(new Vector3(screenPos.x, screenPos.y, 0f));

            // dot(rayDir, planeNormal) == rayDir.y. If ~0, ray is parallel to plane.
            if (Mathf.Abs(ray.direction.y) < 1e-6f)
            {
                world.SetCursor(worldCenter, false);
                return;
            }

            float t = (planeY - ray.origin.y) / ray.direction.y;
            if (t <= 0f)
            {
                // Hit is behind the camera (or camera is below the plane looking down at the sky).
                world.SetCursor(worldCenter, false);
                return;
            }

            Vector3 hit = ray.origin + ray.direction * t;
            world.SetCursor(new float3(hit.x, hit.y, hit.z), true);
        }
    }
}
