// FlyCameraController.cs — WASD + mouse-look fly camera, scaled to the world bounds, with
// a soft tether that springs the camera back toward the centre when it strays too far.
// M5-6 in FLOCKING_PLAN.md.

using Bird_behiviour.Flocking.Simulation;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Bird_behiviour.Flocking.Tooling
{
    /// <summary>
    /// Free-fly debug camera reading <c>Mouse.current</c> + <c>Keyboard.current</c> directly
    /// (no Input Action asset binding required).
    /// </summary>
    /// <remarks>
    /// <b>Controls.</b>
    /// <list type="bullet">
    ///   <item>WASD — local-axis movement (X/Z on the camera's own basis).</item>
    ///   <item>Space / Left-Ctrl — world-space up / down.</item>
    ///   <item>Mouse — pitch (clamped to ±85°) + yaw. Mouse-look is active while the right
    ///         mouse button is held; the cursor is locked + hidden during the hold and
    ///         restored on release. This keeps the cursor available for the
    ///         <c>CursorInputController</c> raycast when the user isn't actively flying.</item>
    ///   <item>Left-Shift — ×4 movement speed multiplier.</item>
    /// </list>
    /// <b>Speed scale.</b> Base movement speed is
    /// <c>WorldBoundsExtents.maxComponent / 30</c> per second so the camera traverses the
    /// volume in ≈30 seconds regardless of world size.
    /// <para/>
    /// <b>Soft tether.</b> When the camera leaves the box <c>WorldBoundsExtents × 1.5</c>
    /// around <c>WorldBoundsCenter</c>, a spring force pulls velocity inward proportional to
    /// how far past the boundary the camera has drifted. This is a velocity nudge, not a hard
    /// teleport — designers who want to leave the volume to inspect from outside can do so
    /// briefly without being yanked.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class FlyCameraController : MonoBehaviour
    {
        [Tooltip("FlockWorld whose bounds drive movement-speed scaling and the soft tether. " +
                 "If null, the controller tries FindFirstObjectByType<FlockWorld>() in OnEnable.")]
        [SerializeField] private FlockWorld world;

        [Header("Look")]
        [Tooltip("Mouse-look sensitivity in degrees per pixel of mouse delta.")]
        [SerializeField, Min(0f)] private float lookSensitivity = 0.15f;

        [Tooltip("Maximum pitch in degrees (camera is clamped to ±this around the horizontal).")]
        [SerializeField, Range(0f, 89f)] private float maxPitchDegrees = 85f;

        [Header("Move")]
        [Tooltip("Multiplier applied while Left-Shift is held.")]
        [SerializeField, Min(1f)] private float sprintMultiplier = 4f;

        [Header("Soft Tether")]
        [Tooltip("Scale on WorldBoundsExtents at which the tether begins to spring inward.")]
        [SerializeField, Min(1f)] private float tetherExtentScale = 1.5f;

        [Tooltip("Tether spring strength (units of velocity-change per second per unit-overshoot).")]
        [SerializeField, Min(0f)] private float tetherStrength = 4f;

        // Internal pitch / yaw in degrees, integrated from mouse delta.
        private float pitchDegrees;
        private float yawDegrees;

        // Tracked so we can restore the cursor lock state when right-mouse releases.
        private CursorLockMode cachedLockMode;
        private bool cachedCursorVisible;
        private bool isLooking;

        private void OnEnable()
        {
            if (world == null)
            {
                world = FindFirstObjectByType<FlockWorld>();
            }

            // Seed pitch/yaw from current transform so toggling the component doesn't snap.
            Vector3 e = transform.eulerAngles;
            pitchDegrees = NormalizeSignedDegrees(e.x);
            yawDegrees   = e.y;
        }

        private void OnDisable()
        {
            if (isLooking)
            {
                RestoreCursor();
                isLooking = false;
            }
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            HandleLook(dt);
            HandleMove(dt);
            ApplySoftTether(dt);
        }

        // ── Look ──────────────────────────────────────────────────────────────────────

        private void HandleLook(float dt)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                if (isLooking)
                {
                    RestoreCursor();
                    isLooking = false;
                }
                return;
            }

            bool wantLook = mouse.rightButton.isPressed;
            if (wantLook && !isLooking)
            {
                CaptureCursor();
                isLooking = true;
            }
            else if (!wantLook && isLooking)
            {
                RestoreCursor();
                isLooking = false;
            }

            if (!isLooking)
            {
                return;
            }

            Vector2 delta = mouse.delta.ReadValue();
            yawDegrees   += delta.x * lookSensitivity;
            pitchDegrees -= delta.y * lookSensitivity;
            pitchDegrees  = Mathf.Clamp(pitchDegrees, -maxPitchDegrees, maxPitchDegrees);

            transform.rotation = Quaternion.Euler(pitchDegrees, yawDegrees, 0f);
        }

        // ── Move ──────────────────────────────────────────────────────────────────────

        private void HandleMove(float dt)
        {
            Keyboard kb = Keyboard.current;
            if (kb == null)
            {
                return;
            }

            // Local WASD axes.
            float strafe  = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
            float forward = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
            // World-up Space / Ctrl axes.
            float vertical = (kb.spaceKey.isPressed ? 1f : 0f) - (kb.leftCtrlKey.isPressed ? 1f : 0f);

            float boundsScale = world != null
                ? math.max(math.cmax(world.WorldBoundsExtents), 1f) / 30f
                : 1f;
            float speed = boundsScale;
            if (kb.leftShiftKey.isPressed)
            {
                speed *= sprintMultiplier;
            }

            Vector3 move =
                transform.right   * strafe   +
                transform.forward * forward  +
                Vector3.up        * vertical;

            transform.position += move * (speed * dt);
        }

        // ── Soft tether ───────────────────────────────────────────────────────────────

        private void ApplySoftTether(float dt)
        {
            if (world == null)
            {
                return;
            }

            float3 center  = world.WorldBoundsCenter;
            float3 extents = (float3)world.WorldBoundsExtents * tetherExtentScale;
            float3 pos     = transform.position;
            float3 offset  = pos - center;

            // Per-axis overshoot beyond the tether box.
            float3 overshoot = math.max(math.abs(offset) - extents, 0f) * math.sign(offset);
            if (math.lengthsq(overshoot) <= 0f)
            {
                return;
            }

            // Pull position inward proportional to overshoot. Frame-rate independent via dt.
            float3 pull = -overshoot * tetherStrength * dt;
            transform.position = (Vector3)(pos + pull);
        }

        // ── Cursor lock helpers ───────────────────────────────────────────────────────

        private void CaptureCursor()
        {
            cachedLockMode      = Cursor.lockState;
            cachedCursorVisible = Cursor.visible;
            Cursor.lockState    = CursorLockMode.Locked;
            Cursor.visible      = false;
        }

        private void RestoreCursor()
        {
            Cursor.lockState = cachedLockMode;
            Cursor.visible   = cachedCursorVisible;
        }

        private static float NormalizeSignedDegrees(float deg)
        {
            deg %= 360f;
            if (deg >  180f) deg -= 360f;
            if (deg < -180f) deg += 360f;
            return deg;
        }
    }
}
