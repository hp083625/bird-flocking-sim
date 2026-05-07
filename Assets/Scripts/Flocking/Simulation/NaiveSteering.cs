// NaiveSteering.cs — main-thread O(n²) steering used by Slice 2's FlockWorld.Tick.
// Slice 4 (M3) replaces this with [BurstCompile] IJobParallelFor variants that consume
// the spatial grid; the per-bird math stays the same, only the dispatch changes.
//
// Internal to the Simulation asmdef.

using Bird_behiviour.Flocking.Core;
using Unity.Collections;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Simulation
{
    /// <summary>
    /// Pure-static, main-thread, naïve O(n²) implementation of every steering force used
    /// in Slice 2: separation / alignment / cohesion (with in-flock vs out-of-flock
    /// weights), world hard bounds, per-flock soft preferred zone, and a no-op cursor
    /// stub. <b>NOT</b> Burst-compiled — Slice 4 (M3) jobifies + Burst-compiles the same
    /// math into <c>NeighborForcesJob</c> / <c>BoundsForcesJob</c> / <c>CursorForceJob</c>.
    /// </summary>
    /// <remarks>
    /// All math uses <see cref="Unity.Mathematics"/> types so the Slice-4 port is mechanical.
    /// Methods take read-only views of the world arrays and write into per-bird accel arrays.
    /// </remarks>
    internal static class NaiveSteering
    {
        // ── Public entry point ────────────────────────────────────────────────────────

        /// <summary>
        /// Computes the total acceleration vector (sum of neighbour, bounds, cursor) for
        /// every bird in <paramref name="positions"/>, capped by each flock's
        /// <see cref="IFlockSettings.MaxAcceleration"/>, and writes the result into
        /// <paramref name="accelerations"/>.
        /// </summary>
        /// <remarks>
        /// O(n²) over <paramref name="count"/>. Used in Slice 2 only; Slice 4 replaces
        /// this with a chain of jobs that share neighbour iteration via the spatial grid.
        /// </remarks>
        internal static void ComputeAccelerations(
            NativeArray<float3> positions,
            NativeArray<float3> velocities,
            NativeArray<byte> flockIds,
            NativeArray<FlockSlice> slices,
            int count,
            IFlockSettings[] settingsByFlockId,
            IFlockWorldSettings worldSettings,
            float3 cursorWorldPoint,
            bool cursorOnScreen,
            NativeArray<float3> accelerations)
        {
            for (int i = 0; i < count; i++)
            {
                byte fid = flockIds[i];
                IFlockSettings s = settingsByFlockId[fid];

                float3 pos = positions[i];
                float3 vel = velocities[i];

                float3 aNeighbor = ComputeNeighborForces(i, pos, vel, fid, positions, velocities, flockIds, count, s, settingsByFlockId);
                float3 aBounds   = ComputeBoundsForces(pos, s, worldSettings);
                float3 aCursor   = ComputeCursorForce(pos, s, cursorWorldPoint, cursorOnScreen);

                float3 a = aNeighbor + aBounds + aCursor;

                // Cap |a| ≤ MaxAcceleration via a normalize-safe pattern (no branches on length=0).
                float aLenSq = math.lengthsq(a);
                float maxA   = s.MaxAcceleration;
                if (aLenSq > maxA * maxA && aLenSq > 0f)
                {
                    a = math.normalize(a) * maxA;
                }

                accelerations[i] = a;
            }
        }

        /// <summary>
        /// Integrates velocity (clamped to <c>[MinSpeed, MaxSpeed]</c> via a length-squared
        /// comparison) and position from accelerations, in place.
        /// </summary>
        internal static void Integrate(
            NativeArray<float3> positions,
            NativeArray<float3> velocities,
            NativeArray<byte> flockIds,
            NativeArray<float3> accelerations,
            int count,
            IFlockSettings[] settingsByFlockId,
            float dt)
        {
            for (int i = 0; i < count; i++)
            {
                IFlockSettings s = settingsByFlockId[flockIds[i]];

                float3 vel = velocities[i] + accelerations[i] * dt;

                float speedSq = math.lengthsq(vel);
                float minSq   = s.MinSpeed * s.MinSpeed;
                float maxSq   = s.MaxSpeed * s.MaxSpeed;

                if (speedSq > maxSq && speedSq > 0f)
                {
                    vel = math.normalize(vel) * s.MaxSpeed;
                }
                else if (speedSq < minSq)
                {
                    if (speedSq > 1e-12f)
                    {
                        vel = math.normalize(vel) * s.MinSpeed;
                    }
                    else
                    {
                        // Truly zero velocity — nudge along +Z so LookRotationSafe has a hint.
                        vel = new float3(0f, 0f, s.MinSpeed);
                    }
                }

                velocities[i] = vel;
                positions[i]  = positions[i] + vel * dt;
            }
        }

        // ── Neighbour forces (separation + alignment + cohesion, in vs out weights) ──

        private static float3 ComputeNeighborForces(
            int self,
            float3 selfPos,
            float3 selfVel,
            byte selfFlockId,
            NativeArray<float3> positions,
            NativeArray<float3> velocities,
            NativeArray<byte> flockIds,
            int count,
            IFlockSettings selfSettings,
            IFlockSettings[] settingsByFlockId)
        {
            float perception = selfSettings.PerceptionRadius;
            float perceptionSq = perception * perception;
            float separation = selfSettings.SeparationRadius;
            float separationSq = separation * separation;
            float coneCos = math.cos(selfSettings.PerceptionConeHalfAngleRadians);

            // Falls back to a 360° sphere when |selfVel| ≈ 0 so still birds aren't blind.
            float selfSpeedSq = math.lengthsq(selfVel);
            bool useCone = selfSpeedSq > 1e-8f;
            float3 selfDir = useCone ? selfVel * math.rsqrt(selfSpeedSq) : float3.zero;

            float3 sepAccumIn   = float3.zero;
            float3 alignAccumIn = float3.zero;
            float3 cohAccumIn   = float3.zero;
            int countIn = 0;

            float3 sepAccumOut   = float3.zero;
            float3 alignAccumOut = float3.zero;
            float3 cohAccumOut   = float3.zero;
            int countOut = 0;

            for (int j = 0; j < count; j++)
            {
                if (j == self)
                {
                    continue;
                }

                float3 toN = positions[j] - selfPos;
                float distSq = math.lengthsq(toN);
                if (distSq > perceptionSq || distSq <= 1e-12f)
                {
                    continue;
                }

                if (useCone)
                {
                    // dot(selfDir, toNNorm) >= cos(half-angle) → inside cone.
                    float toLen = math.sqrt(distSq);
                    float dotFwd = math.dot(selfDir, toN / toLen);
                    if (dotFwd < coneCos)
                    {
                        continue;
                    }
                }

                bool sameFlock = flockIds[j] == selfFlockId;

                // Separation: inverse-distance push away (only inside SeparationRadius).
                if (distSq < separationSq)
                {
                    // Scale by (1 - dist/sepRadius) so close birds push harder.
                    float invDist = math.rsqrt(distSq);
                    float falloff = 1f - math.sqrt(distSq) / separation;
                    float3 push = -toN * (invDist * falloff);

                    if (sameFlock) sepAccumIn  += push;
                    else            sepAccumOut += push;
                }

                // Alignment / cohesion always within perception radius.
                if (sameFlock)
                {
                    alignAccumIn += velocities[j];
                    cohAccumIn   += positions[j];
                    countIn++;
                }
                else
                {
                    alignAccumOut += velocities[j];
                    cohAccumOut   += positions[j];
                    countOut++;
                }
            }

            float3 totalIn = float3.zero;
            if (countIn > 0)
            {
                float invN = 1f / countIn;
                float3 alignDir = SafeSteer(alignAccumIn * invN, selfVel, selfSettings.MaxSpeed);
                float3 cohDir   = SafeSteer(cohAccumIn   * invN - selfPos, selfVel, selfSettings.MaxSpeed);
                totalIn =
                    sepAccumIn   * selfSettings.InSeparationWeight +
                    alignDir     * selfSettings.InAlignmentWeight +
                    cohDir       * selfSettings.InCohesionWeight;
            }
            else
            {
                totalIn = sepAccumIn * selfSettings.InSeparationWeight;
            }

            float3 totalOut = float3.zero;
            if (countOut > 0)
            {
                float invN = 1f / countOut;
                float3 alignDir = SafeSteer(alignAccumOut * invN, selfVel, selfSettings.MaxSpeed);
                float3 cohDir   = SafeSteer(cohAccumOut   * invN - selfPos, selfVel, selfSettings.MaxSpeed);
                totalOut =
                    sepAccumOut * selfSettings.OutSeparationWeight +
                    alignDir    * selfSettings.OutAlignmentWeight +
                    cohDir      * selfSettings.OutCohesionWeight;
            }
            else
            {
                totalOut = sepAccumOut * selfSettings.OutSeparationWeight;
            }

            return totalIn + totalOut;
        }

        // ── Bounds forces (world hard + per-flock preferred soft) ────────────────────

        private static float3 ComputeBoundsForces(
            float3 pos,
            IFlockSettings s,
            IFlockWorldSettings world)
        {
            // World hard bounds: sharp inward push when outside [center ± extents].
            float3 worldCenter  = world.WorldBoundsCenter;
            float3 worldExtents = world.WorldBoundsExtents;
            float3 wOff = pos - worldCenter;
            float3 wOver = math.max(math.abs(wOff) - worldExtents, 0f) * math.sign(wOff);
            float3 wForce = -wOver * world.WorldBoundsWeight;

            // Per-flock preferred zone: gentle attraction toward PreferredCenter.
            float3 prefCenter = s.PreferredCenter;
            float3 prefExtents = s.PreferredExtents;
            float prefMaxExtent = math.max(math.cmax(prefExtents), 1e-3f);
            float3 prefOff = pos - prefCenter;
            float prefDist = math.length(prefOff);
            float falloff = math.saturate(1f - prefDist / prefMaxExtent);
            float3 prefDir = prefDist > 1e-6f ? -prefOff / prefDist : float3.zero;
            // Only attract once outside the preferred box (otherwise ramp toward zero inside).
            float3 prefOver = math.max(math.abs(prefOff) - prefExtents, 0f);
            float prefMag = math.length(prefOver);
            float3 pForce = prefDir * (prefMag * s.PreferredAttractionWeight * (1f - falloff * 0.5f));

            return wForce + pForce;
        }

        // ── Cursor force (Slice 2 stub — wiring only, no influence) ──────────────────

        private static float3 ComputeCursorForce(
            float3 pos,
            IFlockSettings s,
            float3 cursorWorldPoint,
            bool cursorOnScreen)
        {
            // Slice 2: no-op stub. Slice 8 (M3-3) will use cursorWorldPoint + s.CursorReactionStrength
            // / s.CursorReactionRadius / cursorOnScreen to compute the real signed force.
            _ = pos; _ = s; _ = cursorWorldPoint; _ = cursorOnScreen;
            return float3.zero;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reynolds-style "steer toward desired" — produces a desired velocity along
        /// <paramref name="desired"/> at <paramref name="maxSpeed"/> and returns the delta
        /// from current velocity. Returns zero if <paramref name="desired"/> is zero.
        /// </summary>
        private static float3 SafeSteer(float3 desired, float3 currentVel, float maxSpeed)
        {
            float dSq = math.lengthsq(desired);
            if (dSq < 1e-12f)
            {
                return float3.zero;
            }
            float3 desiredVel = desired * math.rsqrt(dSq) * maxSpeed;
            return desiredVel - currentVel;
        }
    }
}
