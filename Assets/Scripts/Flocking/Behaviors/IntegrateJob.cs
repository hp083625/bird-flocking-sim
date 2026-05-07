// IntegrateJob.cs — Slice 4 (M3-4) Burst-compiled IJobParallelFor that sums the three
// acceleration components, caps the total via MaxAcceleration with a normalize-safe
// pattern, integrates velocity (clamped to [MinSpeed, MaxSpeed] using length-squared
// comparisons), and integrates position. Final job in the per-tick steering chain.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Behaviors
{
    /// <summary>
    /// Final per-tick steering job. Sums neighbour + bounds + cursor accelerations,
    /// caps the total via the bird's <see cref="FlockKernelSettings.MaxAcceleration"/>,
    /// integrates velocity (clamped to <c>[MinSpeed, MaxSpeed]</c>), and integrates
    /// position. Runs after <see cref="JobHandle.CombineDependencies(JobHandle, JobHandle, JobHandle)"/>
    /// of the three force jobs.
    /// </summary>
    /// <remarks>
    /// Edge cases:
    /// <list type="bullet">
    ///   <item>Total |accel| ≤ MaxAcceleration → passes through (no normalize).</item>
    ///   <item>|vel| inside <c>[MinSpeed, MaxSpeed]</c> → passes through (no normalize).</item>
    ///   <item>|vel| ≈ 0 below MinSpeed → nudged to <c>(0, 0, MinSpeed)</c> so
    ///   <c>quaternion.LookRotationSafe</c> downstream has a well-defined heading.</item>
    /// </list>
    /// </remarks>
    [BurstCompile]
    internal struct IntegrateJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> AccelNeighbor;
        [ReadOnly] public NativeArray<float3> AccelBounds;
        [ReadOnly] public NativeArray<float3> AccelCursor;
        [ReadOnly] public NativeArray<byte>   FlockIds;
        [ReadOnly] public NativeArray<FlockKernelSettings> KernelSettings;

        public NativeArray<float3> Positions;
        public NativeArray<float3> Velocities;
        // Persistent debug-friendly mirror of the capped acceleration (used by gizmos /
        // future profiling). Optional — caller passes the FlockWorld.Accelerations array.
        [WriteOnly] public NativeArray<float3> AccelerationsOut;

        public float Dt;

        public void Execute(int i)
        {
            FlockKernelSettings s = KernelSettings[FlockIds[i]];

            float3 a = AccelNeighbor[i] + AccelBounds[i] + AccelCursor[i];

            // ── Cap acceleration via the normalize-safe pattern (compare length²) ───
            float maxA   = s.MaxAcceleration;
            float maxASq = maxA * maxA;
            float aLenSq = math.lengthsq(a);
            if (aLenSq > maxASq && aLenSq > 0f)
            {
                a = a * (maxA * math.rsqrt(aLenSq));
            }
            AccelerationsOut[i] = a;

            // ── Integrate velocity, clamp speed via length² comparison ─────────────
            float3 vel = Velocities[i] + a * Dt;

            float speedSq = math.lengthsq(vel);
            float minSq = s.MinSpeed * s.MinSpeed;
            float maxSq = s.MaxSpeed * s.MaxSpeed;

            if (speedSq > maxSq && speedSq > 0f)
            {
                vel = vel * (s.MaxSpeed * math.rsqrt(speedSq));
            }
            else if (speedSq < minSq)
            {
                if (speedSq > 1e-12f)
                {
                    vel = vel * (s.MinSpeed * math.rsqrt(speedSq));
                }
                else
                {
                    // Truly zero velocity — nudge along +Z so LookRotationSafe has a hint.
                    vel = new float3(0f, 0f, s.MinSpeed);
                }
            }

            Velocities[i] = vel;
            Positions[i]  = Positions[i] + vel * Dt;
        }
    }
}
