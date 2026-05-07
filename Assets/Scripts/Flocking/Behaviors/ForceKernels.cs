// ForceKernels.cs — Slice 4 (M3) pure-function force primitives, Burst-compiled and
// callable from both inside the steering jobs and from the M6-2 EditMode unit tests.
//
// Every method is a pure function of its inputs (no captured state, no NativeArray
// arguments) and takes only blittable primitive / Unity.Mathematics types so that:
//   1. Tests can pin individual force terms in isolation (M6-2 spec).
//   2. The jobs can call them without crossing a managed boundary.
//   3. Burst can fully inline + vectorize the inner loop.
//
// Per FLOCKING_PLAN.md §6 M3-1..M3-3, these match the math NaiveSteering (Slice 2/3)
// performed on the main thread, with the same edge-case behaviour (zero-distance
// fallbacks, zero-neighbour fallbacks, zero-velocity perception fallback).

using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Behaviors
{
    /// <summary>
    /// Static, stateless, <see cref="BurstCompileAttribute"/>-decorated force kernels used
    /// by the Slice 4 steering jobs (<c>NeighborForcesJob</c>, <c>BoundsForcesJob</c>,
    /// <c>CursorForceJob</c>) and exercised in isolation by the M6-2 EditMode tests.
    /// </summary>
    /// <remarks>
    /// All methods are pure: they take only primitive / blittable inputs and return
    /// either a <see cref="float3"/> force or a small struct of intermediate values.
    /// No managed allocations; no NativeArray accesses (those happen in the calling
    /// job, which then feeds primitives into the kernels).
    /// </remarks>
    // ABI note (Burst restore): float3 / vector struct PARAMETERS are taken by `in`
    // (pass-by-readonly-ref) instead of by value. Without this, Burst rejects the
    // calling convention with BC1064/BC1067 ("vector type X cannot be passed by
    // value as a parameter to an external function") whenever the inliner declines
    // to fully inline a kernel into its caller. Returning a float3 by value is
    // fine — Burst's return register convention handles it.
    //
    // Tests and job hot loops continue to call the kernels with the same syntax —
    // C# 7.2+ implicitly synthesises a readonly reference for rvalue arguments
    // ("ForceKernels.ComputeSeparation(selfPos, nPos, ...)" still works).
    //
    // [MethodImpl(AggressiveInlining)] is kept as a hint to the IL inliner so the
    // C# compiler folds the bodies in where it can; Burst then has a stable
    // pointer-passing ABI for the cases where it doesn't.
    internal static class ForceKernels
    {
        // ── Tunable epsilons (kept as constants so Burst can fold them) ──────────────

        // Below this squared length, treat the vector as exactly zero.
        private const float ZeroLenSq    = 1e-12f;
        // Below this squared length, treat the velocity as exactly zero (perception 360°).
        private const float ZeroVelLenSq = 1e-8f;

        // ── Separation (per-pair contribution) ───────────────────────────────────────

        /// <summary>
        /// Per-neighbour-pair separation push: pushes <paramref name="selfPos"/> away from
        /// <paramref name="neighborPos"/> with magnitude <c>(1 / dist) * (1 - dist /
        /// separationRadius) * weight</c>. Returns zero when the pair is outside
        /// <paramref name="separationRadius"/>; returns a bounded fallback push along
        /// world-space +X when the two positions coincide (so the result never contains
        /// NaN / inf).
        /// </summary>
        /// <remarks>
        /// Edge cases:
        /// <list type="bullet">
        ///   <item>distance ≥ separationRadius → returns <see cref="float3.zero"/>.</item>
        ///   <item>distance == 0 → returns <c>(weight, 0, 0)</c> (deterministic, finite).</item>
        ///   <item>negative <paramref name="separationRadius"/> → returns <see cref="float3.zero"/>.</item>
        /// </list>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float3 ComputeSeparation(
            in float3 selfPos,
            in float3 neighborPos,
            float separationRadius,
            float weight)
        {
            if (separationRadius <= 0f)
            {
                return float3.zero;
            }

            float3 toN = neighborPos - selfPos;
            float distSq = math.lengthsq(toN);

            // Coincident pair → bounded fallback push so the result is never NaN.
            if (distSq <= ZeroLenSq)
            {
                return new float3(weight, 0f, 0f);
            }

            float sepSq = separationRadius * separationRadius;
            if (distSq >= sepSq)
            {
                return float3.zero;
            }

            float invDist = math.rsqrt(distSq);
            float dist    = distSq * invDist;                          // == sqrt(distSq), reuses rsqrt
            float falloff = 1f - dist / separationRadius;              // 0..1, stronger when closer
            // -toN/dist  →  unit vector away from neighbour. * falloff * weight = magnitude.
            return -toN * (invDist * falloff * weight);
        }

        // ── Alignment (whole-neighbourhood Reynolds steering) ────────────────────────

        /// <summary>
        /// Reynolds-style alignment steering: matches <paramref name="selfVel"/> to the
        /// neighbour-average velocity scaled to <paramref name="maxSpeed"/>. Returns
        /// <c>(desired - selfVel) * weight</c>. Returns <see cref="float3.zero"/> when
        /// <paramref name="neighborCount"/> ≤ 0 or when the average direction is zero.
        /// </summary>
        /// <param name="selfVel">Current velocity of the bird being steered.</param>
        /// <param name="neighborVelocitySum">Running sum of neighbours' velocities.</param>
        /// <param name="neighborCount">Number of neighbours summed (must match the sum length).</param>
        /// <param name="maxSpeed">Speed to scale the desired velocity to.</param>
        /// <param name="weight">Weight applied to the steering delta.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float3 ComputeAlignment(
            in float3 selfVel,
            in float3 neighborVelocitySum,
            int neighborCount,
            float maxSpeed,
            float weight)
        {
            if (neighborCount <= 0)
            {
                return float3.zero;
            }
            float3 avg = neighborVelocitySum / neighborCount;
            float avgSq = math.lengthsq(avg);
            if (avgSq < ZeroLenSq)
            {
                return float3.zero;
            }
            float3 desiredVel = avg * math.rsqrt(avgSq) * maxSpeed;
            return (desiredVel - selfVel) * weight;
        }

        // ── Cohesion (whole-neighbourhood centre-pull) ───────────────────────────────

        /// <summary>
        /// Cohesion force: pulls <paramref name="selfPos"/> toward the neighbour-average
        /// position with magnitude proportional to the offset and scaled by
        /// <paramref name="weight"/>. Returns <see cref="float3.zero"/> when
        /// <paramref name="neighborCount"/> ≤ 0 or when the bird is exactly at the
        /// neighbourhood centroid.
        /// </summary>
        /// <param name="selfPos">Position of the bird being steered.</param>
        /// <param name="neighborPositionSum">Running sum of neighbours' positions.</param>
        /// <param name="neighborCount">Number of neighbours summed (must match the sum length).</param>
        /// <param name="weight">Weight applied to the offset toward the centroid.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float3 ComputeCohesion(
            in float3 selfPos,
            in float3 neighborPositionSum,
            int neighborCount,
            float weight)
        {
            if (neighborCount <= 0)
            {
                return float3.zero;
            }
            float3 centroid = neighborPositionSum / neighborCount;
            float3 toCentre = centroid - selfPos;
            if (math.lengthsq(toCentre) < ZeroLenSq)
            {
                return float3.zero;
            }
            return toCentre * weight;
        }

        // ── World hard bounds (sharp inward push outside extents) ────────────────────

        /// <summary>
        /// Sharp inward push when <paramref name="selfPos"/> is outside the world AABB
        /// <c>[worldBoundsCenter ± (worldBoundsExtents - margin)]</c>. Force magnitude is
        /// proportional to how far the bird has overshot the (slightly inset) box.
        /// Returns <see cref="float3.zero"/> when the bird is comfortably inside.
        /// </summary>
        /// <param name="selfPos">World-space bird position.</param>
        /// <param name="worldBoundsCenter">Centre of the world AABB.</param>
        /// <param name="worldBoundsExtents">Half-extents of the world AABB (per-axis).</param>
        /// <param name="weight">Strength multiplier on the inward push.</param>
        /// <param name="margin">
        /// Per-axis inward inset. Use a small positive value (e.g. 5% of the smaller extent)
        /// so the push starts ramping up just before the bird actually reaches the wall.
        /// Clamped so the effective extents never go negative.
        /// </param>
        /// <remarks>
        /// Edge cases: at <paramref name="worldBoundsCenter"/> (zero offset) the force is
        /// zero. With <paramref name="weight"/>=0 the force is zero regardless of position.
        /// Margin larger than an extent is clamped to that extent (prevents the inset
        /// from inverting).
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float3 ComputeBoundsForceWorldHard(
            in float3 selfPos,
            in float3 worldBoundsCenter,
            in float3 worldBoundsExtents,
            float weight,
            float margin)
        {
            // Inset the AABB by `margin` per axis; never let the inset push extents below 0.
            float3 effExtents = math.max(worldBoundsExtents - new float3(margin, margin, margin),
                                         float3.zero);
            float3 off  = selfPos - worldBoundsCenter;
            // For each axis, how far past the inset extent we are (0 if inside).
            float3 over = math.max(math.abs(off) - effExtents, float3.zero) * math.sign(off);
            return -over * weight;
        }

        // ── Per-flock preferred zone (gentle pull back into the soft box) ────────────

        /// <summary>
        /// Gentle attraction back toward <paramref name="preferredCenter"/> when
        /// <paramref name="selfPos"/> drifts outside the soft preferred-zone AABB. Returns
        /// <see cref="float3.zero"/> when the bird is inside the preferred zone or when
        /// it is exactly at <paramref name="preferredCenter"/>.
        /// </summary>
        /// <remarks>
        /// Force = unit-vector-toward-centre × overshoot-magnitude × weight × (1 - falloff/2),
        /// where <c>falloff = saturate(1 - distance / max(preferredExtents))</c>. This matches
        /// the Slice 2/3 NaiveSteering main-thread implementation so visuals stay identical.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float3 ComputeBoundsForcePreferred(
            in float3 selfPos,
            in float3 preferredCenter,
            in float3 preferredExtents,
            float weight)
        {
            float maxExtent = math.max(math.cmax(preferredExtents), 1e-3f);
            float3 off  = selfPos - preferredCenter;
            float dist  = math.length(off);
            float falloff = math.saturate(1f - dist / maxExtent);
            float3 dir = dist > 1e-6f ? -off / dist : float3.zero;

            float3 over = math.max(math.abs(off) - preferredExtents, float3.zero);
            float overMag = math.length(over);
            if (overMag <= 0f)
            {
                return float3.zero;
            }
            return dir * (overMag * weight * (1f - falloff * 0.5f));
        }

        // ── Cursor (Slice 4 keeps the math callable; the live job stays a no-op) ─────

        /// <summary>
        /// Signed cursor force: returns a vector of magnitude
        /// <c>|strength| * smoothstep(falloff)</c> pointing toward
        /// <paramref name="cursorWorldPoint"/> when <paramref name="strength"/> &gt; 0,
        /// away when <paramref name="strength"/> &lt; 0. Returns
        /// <see cref="float3.zero"/> when the cursor is offscreen, when
        /// <paramref name="radius"/> ≤ 0, when <paramref name="strength"/> = 0, or when
        /// the bird is farther than <paramref name="radius"/> from the cursor.
        /// </summary>
        /// <remarks>
        /// Slice 4 ships this kernel for the unit tests + Burst Inspector but the live
        /// <c>CursorForceJob</c> is a no-op stub. Slice 7/8 will dispatch it from the
        /// real cursor job.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float3 ComputeCursorForce(
            in float3 selfPos,
            in float3 cursorWorldPoint,
            bool cursorOnScreen,
            float strength,
            float radius)
        {
            if (!cursorOnScreen || strength == 0f || radius <= 0f)
            {
                return float3.zero;
            }
            float3 toCursor = cursorWorldPoint - selfPos;
            float distSq = math.lengthsq(toCursor);
            float radSq  = radius * radius;
            if (distSq >= radSq || distSq <= ZeroLenSq)
            {
                return float3.zero;
            }
            float invDist = math.rsqrt(distSq);
            float dist    = distSq * invDist;
            // Smoothstep falloff: 1 at the bird (linear), 0 at the radius.
            float t = 1f - dist / radius;
            float falloff = t * t * (3f - 2f * t);
            float magnitude = math.abs(strength) * falloff;
            float sign = strength >= 0f ? 1f : -1f;
            return toCursor * (invDist * magnitude * sign);
        }

        // ── Perception cone test (shared by NeighborForcesJob's hot loop) ────────────

        /// <summary>
        /// Returns <c>true</c> iff the unit direction from <paramref name="selfPos"/> to
        /// <paramref name="neighborPos"/> lies inside the forward cone defined by
        /// <paramref name="selfVel"/> and <paramref name="coneCosHalfAngle"/>. Falls
        /// back to a 360° sphere (always true) when |selfVel| ≈ 0 so motionless birds
        /// aren't blind.
        /// </summary>
        /// <param name="selfPos">Position of the observer.</param>
        /// <param name="selfVel">Velocity of the observer (heading).</param>
        /// <param name="neighborPos">Position of the candidate neighbour.</param>
        /// <param name="coneCosHalfAngle">Pre-computed <c>cos(coneHalfAngleRadians)</c>.</param>
        /// <remarks>
        /// Caller is expected to have already filtered for distance &gt; 0 (a coincident
        /// pair has no defined direction; this method conservatively returns <c>true</c>
        /// in that case to match the calling convention used by NaiveSteering).
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool PerceptionConeAccepts(
            in float3 selfPos,
            in float3 selfVel,
            in float3 neighborPos,
            float coneCosHalfAngle)
        {
            float speedSq = math.lengthsq(selfVel);
            if (speedSq < ZeroVelLenSq)
            {
                return true; // 360° fallback for motionless observer.
            }
            float3 toN = neighborPos - selfPos;
            float toSq = math.lengthsq(toN);
            if (toSq < ZeroLenSq)
            {
                return true; // Coincident: no defined direction; accept.
            }
            // dot(unit selfVel, unit toN) ≥ cosHalfAngle  ↔  inside cone.
            float dotRaw = math.dot(selfVel, toN);
            // Compare squared form to avoid sqrt; preserve sign of dotRaw.
            // dotRaw >= cosHalfAngle * |selfVel| * |toN|
            //  ⇔  sign(dotRaw)*dotRaw² >= cosHalfAngle² * speedSq * toSq  (if cos≥0)
            // For simplicity (and because cos can be negative for half-angles > 90°),
            // do the explicit normalize via rsqrt — still allocation-free, still Burst.
            float invLens = math.rsqrt(speedSq * toSq);
            float cosTheta = dotRaw * invLens;
            return cosTheta >= coneCosHalfAngle;
        }
    }
}
