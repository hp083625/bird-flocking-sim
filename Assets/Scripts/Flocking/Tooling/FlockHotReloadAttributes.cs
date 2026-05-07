// FlockHotReloadAttributes.cs — marker attributes consumed by Slice 10's custom
// FlockSettings inspector to partition fields into "live-edit" (tunable) and
// "Apply-to-commit" (structural) sections. See FLOCKING_PLAN.md §6 M5-1 / M5-2.
//
// Both attributes are pure marker types: they carry no payload. The custom inspector
// (in the Editor asmdef) reflects over a FlockSettings instance, looks for these
// attributes on each serialized field, and routes the field into the matching section.
//
// They live in the Tooling asmdef (not Editor) because the FlockSettings asset itself
// — which carries the attributes on its fields — is in Tooling. Putting the attributes
// next to the asset means the asset compiles in player builds without dragging the
// Editor asmdef along.

using System;

namespace Bird_behiviour.Flocking.Tooling
{
    /// <summary>
    /// Marks a serialized <c>FlockSettings</c> field as <b>tunable</b>: edits in the custom
    /// inspector are written through to the asset immediately and read live by the running
    /// simulation on the next frame, no rebuild required.
    /// </summary>
    /// <remarks>
    /// Use for fields the steering jobs read every tick (weights, perception cone half-angle,
    /// motion clamps, cursor strength/radius, preferred-zone center/extents). Anything that
    /// would invalidate the per-bird array layout, the spatial grid cell size, or require
    /// re-spawning birds is <see cref="FlockStructuralAttribute"/> instead.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class FlockTunableAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a serialized <c>FlockSettings</c> field as <b>structural</b>: edits in the custom
    /// inspector are staged separately from the asset and only commit when the designer
    /// presses the "Apply Structural Changes" button, which calls <c>FlockManager.Rebuild()</c>
    /// (or <c>FlockWorld.Rebuild()</c> for world-level fields).
    /// </summary>
    /// <remarks>
    /// Use for fields that change array sizing or spatial-index topology: <c>BirdCount</c>,
    /// <c>PerceptionRadius</c> (drives the cell-list cell size), <c>SeparationRadius</c>.
    /// Mutating these mid-tick without a rebuild would either out-of-bounds index a stale
    /// array or silently desynchronise the spatial grid from the bird positions.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class FlockStructuralAttribute : Attribute
    {
    }
}
