// ForceKernelTests.cs — Slice 4 / M6-2 EditMode unit tests for the pure
// ForceKernels.* static methods. Each test pins a single force term in isolation so
// regressions in one term don't get masked by the integration result.
//
// All tests run in EditMode (no scene, no FlockWorld); they call the kernels through
// the Behaviors asmdef's InternalsVisibleTo grant.

using Bird_behiviour.Flocking.Behaviors;
using NUnit.Framework;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Tests.EditMode
{
    /// <summary>
    /// Pure-math unit tests for <see cref="ForceKernels"/>. Each test exercises one
    /// kernel at a known geometric configuration so failures point at the specific
    /// force term that regressed.
    /// </summary>
    public sealed class ForceKernelTests
    {
        private const float Eps = 1e-4f;

        // ── ComputeSeparation ───────────────────────────────────────────────────────

        [Test]
        public void Separation_AtExactlySeparationRadius_IsZero()
        {
            // At distance == separationRadius the falloff term (1 - dist/sep) is exactly 0,
            // so the per-pair push is zero. (This is also where the strict-less-than guard
            // on the squared distance kicks in, depending on rounding — accept either.)
            var self = new float3(0f, 0f, 0f);
            var other = new float3(2f, 0f, 0f);
            float3 f = ForceKernels.ComputeSeparation(self, other, separationRadius: 2f, weight: 1f);
            Assert.That(math.length(f), Is.LessThanOrEqualTo(Eps),
                $"Expected ~0 separation at exactly the separation radius, got {f} (|f|={math.length(f)}).");
        }

        [Test]
        public void Separation_TwoBirds_EqualAndOpposite()
        {
            // Slightly inside the separation radius so the kernel returns a non-zero push.
            // Exchanging the operands must produce the negated vector — Newton's third law.
            var a = new float3(0f, 0f, 0f);
            var b = new float3(0.5f, 0f, 0f);
            float sepR = 2f;
            float w = 1f;

            float3 fAB = ForceKernels.ComputeSeparation(a, b, sepR, w);  // push on A from B
            float3 fBA = ForceKernels.ComputeSeparation(b, a, sepR, w);  // push on B from A

            Assert.That(math.length(fAB + fBA), Is.LessThanOrEqualTo(Eps),
                $"Expected fAB + fBA ≈ 0 (Newton 3rd law); got fAB={fAB}, fBA={fBA}.");
            Assert.That(fAB.x, Is.LessThan(0f), "A should be pushed away from B (–X direction).");
            Assert.That(fBA.x, Is.GreaterThan(0f), "B should be pushed away from A (+X direction).");
        }

        [Test]
        public void Separation_AtZeroDistance_IsBoundedAndFinite()
        {
            // The coincident-pair fallback returns a deterministic finite vector — never NaN/inf.
            var p = new float3(7f, -3f, 12f);
            float3 f = ForceKernels.ComputeSeparation(p, p, separationRadius: 1f, weight: 5f);
            Assert.IsFalse(math.any(math.isnan(f)), $"Separation at zero distance must not produce NaN: {f}");
            Assert.IsFalse(math.any(math.isinf(f)), $"Separation at zero distance must not produce inf: {f}");
            Assert.That(math.length(f), Is.LessThanOrEqualTo(10f),
                $"Coincident-pair fallback should be bounded (≈ weight); got {f}.");
        }

        // ── ComputeAlignment ────────────────────────────────────────────────────────

        [Test]
        public void Alignment_ZeroNeighbors_IsZero()
        {
            float3 f = ForceKernels.ComputeAlignment(
                selfVel: new float3(1f, 0f, 0f),
                neighborVelocitySum: float3.zero,
                neighborCount: 0,
                maxSpeed: 5f,
                weight: 1f);
            Assert.AreEqual(float3.zero, f, "Alignment with zero neighbours must return float3.zero.");
        }

        [Test]
        public void Alignment_MatchesNeighborAverage_WhenSelfVelDiffers()
        {
            // Two neighbours both heading +X at speed 5; self heading +Z at speed 5.
            // Desired velocity = (+X normalized) * maxSpeed = (5,0,0); steering = desired - selfVel.
            var selfVel = new float3(0f, 0f, 5f);
            var sum     = new float3(5f, 0f, 0f) + new float3(5f, 0f, 0f);
            float3 f = ForceKernels.ComputeAlignment(selfVel, sum, neighborCount: 2, maxSpeed: 5f, weight: 1f);
            Assert.That(math.distance(f, new float3(5f, 0f, -5f)), Is.LessThanOrEqualTo(Eps),
                $"Expected steering ≈ (5,0,-5); got {f}.");
        }

        // ── ComputeCohesion ─────────────────────────────────────────────────────────

        [Test]
        public void Cohesion_ZeroNeighbors_IsZero()
        {
            float3 f = ForceKernels.ComputeCohesion(
                selfPos: new float3(1f, 2f, 3f),
                neighborPositionSum: float3.zero,
                neighborCount: 0,
                weight: 1f);
            Assert.AreEqual(float3.zero, f, "Cohesion with zero neighbours must return float3.zero.");
        }

        [Test]
        public void Cohesion_PullsTowardCentroid()
        {
            // Two neighbours at (10,0,0) and (10,10,0). Centroid = (10,5,0). Self at origin.
            // Cohesion = (centroid - self) * weight = (10,5,0) * 0.5 = (5,2.5,0).
            var self = new float3(0f, 0f, 0f);
            var sum  = new float3(10f, 0f, 0f) + new float3(10f, 10f, 0f);
            float3 f = ForceKernels.ComputeCohesion(self, sum, neighborCount: 2, weight: 0.5f);
            Assert.That(math.distance(f, new float3(5f, 2.5f, 0f)), Is.LessThanOrEqualTo(Eps),
                $"Expected (5, 2.5, 0); got {f}.");
        }

        // ── ComputeBoundsForceWorldHard ────────────────────────────────────────────

        [Test]
        public void BoundsHard_AtCenter_IsZero()
        {
            float3 f = ForceKernels.ComputeBoundsForceWorldHard(
                selfPos: new float3(0f, 0f, 0f),
                worldBoundsCenter: new float3(0f, 0f, 0f),
                worldBoundsExtents: new float3(50f, 25f, 50f),
                weight: 8f,
                margin: 2.5f);
            Assert.AreEqual(float3.zero, f, $"Bird at the world centre must feel zero hard-bounds force; got {f}.");
        }

        [Test]
        public void BoundsHard_OutsideExtents_PushesInward()
        {
            // Bird is 5m past the +X wall; expect a strong –X push proportional to weight × overshoot.
            float3 center  = new float3(0f, 0f, 0f);
            float3 extents = new float3(50f, 25f, 50f);
            float3 self    = new float3(55f, 0f, 0f);

            float3 f = ForceKernels.ComputeBoundsForceWorldHard(self, center, extents, weight: 8f, margin: 0f);
            Assert.That(f.x, Is.LessThan(0f), $"Expected –X push past +X wall, got {f}.");
            Assert.That(f.y, Is.EqualTo(0f).Within(Eps));
            Assert.That(f.z, Is.EqualTo(0f).Within(Eps));
            // Magnitude = weight × overshoot = 8 × 5 = 40.
            Assert.That(math.length(f), Is.EqualTo(40f).Within(Eps));
        }

        [Test]
        public void BoundsHard_MarginPullsForceInward()
        {
            // Inset the wall by 10m; the bird is just past the inset edge but still inside the
            // raw extent → force is non-zero (the margin starts pulling it inward early).
            float3 center  = float3.zero;
            float3 extents = new float3(50f, 50f, 50f);
            float3 self    = new float3(45f, 0f, 0f);

            float3 fNoMargin = ForceKernels.ComputeBoundsForceWorldHard(self, center, extents, 1f, margin: 0f);
            float3 fMargin   = ForceKernels.ComputeBoundsForceWorldHard(self, center, extents, 1f, margin: 10f);

            Assert.That(math.length(fNoMargin), Is.LessThanOrEqualTo(Eps),
                "Without margin, a bird inside the raw extents should feel zero hard-bounds force.");
            Assert.That(fMargin.x, Is.LessThan(0f),
                $"With a 10m margin (effective extent = 40), a bird at x=45 should feel an inward push; got {fMargin}.");
        }

        // ── ComputeBoundsForcePreferred ─────────────────────────────────────────────

        [Test]
        public void BoundsPreferred_InsideZone_IsZero()
        {
            float3 f = ForceKernels.ComputeBoundsForcePreferred(
                selfPos: new float3(5f, 3f, -2f),
                preferredCenter: float3.zero,
                preferredExtents: new float3(20f, 20f, 20f),
                weight: 1f);
            Assert.AreEqual(float3.zero, f, $"Preferred-zone force inside the box must be zero; got {f}.");
        }

        [Test]
        public void BoundsPreferred_OutsideZone_PullsTowardCenter()
        {
            float3 center = float3.zero;
            float3 ext    = new float3(10f, 10f, 10f);
            float3 self   = new float3(20f, 0f, 0f);
            float3 f = ForceKernels.ComputeBoundsForcePreferred(self, center, ext, weight: 1f);
            Assert.That(f.x, Is.LessThan(0f), $"Bird past the +X face should be pulled toward –X; got {f}.");
        }

        // ── ComputeCursorForce ──────────────────────────────────────────────────────

        [Test]
        public void CursorForce_OffScreen_IsZero()
        {
            float3 f = ForceKernels.ComputeCursorForce(
                selfPos: float3.zero,
                cursorWorldPoint: new float3(5f, 0f, 0f),
                cursorOnScreen: false,
                strength: 10f,
                radius: 20f);
            Assert.AreEqual(float3.zero, f, $"Off-screen cursor must produce zero force; got {f}.");
        }

        [Test]
        public void CursorForce_PositiveStrength_PullsToward()
        {
            float3 f = ForceKernels.ComputeCursorForce(
                selfPos: float3.zero,
                cursorWorldPoint: new float3(5f, 0f, 0f),
                cursorOnScreen: true,
                strength: 10f,
                radius: 20f);
            Assert.That(f.x, Is.GreaterThan(0f), $"Positive strength → force toward cursor (+X); got {f}.");
        }

        [Test]
        public void CursorForce_NegativeStrength_PushesAway()
        {
            float3 f = ForceKernels.ComputeCursorForce(
                selfPos: float3.zero,
                cursorWorldPoint: new float3(5f, 0f, 0f),
                cursorOnScreen: true,
                strength: -10f,
                radius: 20f);
            Assert.That(f.x, Is.LessThan(0f), $"Negative strength → force away from cursor (–X); got {f}.");
        }

        [Test]
        public void CursorForce_OutsideRadius_IsZero()
        {
            float3 f = ForceKernels.ComputeCursorForce(
                selfPos: float3.zero,
                cursorWorldPoint: new float3(50f, 0f, 0f),
                cursorOnScreen: true,
                strength: 10f,
                radius: 5f);
            Assert.AreEqual(float3.zero, f, $"Bird outside cursor radius must feel zero force; got {f}.");
        }

        // ── PerceptionConeAccepts ───────────────────────────────────────────────────

        [Test]
        public void PerceptionCone_NeighborBehind_IsRejected_ForFrontHalfCone()
        {
            // 90° half-angle ⇒ cos = 0. Self moving +X; neighbour at –X is directly behind ⇒ dot = -1 < 0 ⇒ reject.
            var self = float3.zero;
            var vel  = new float3(1f, 0f, 0f);
            var n    = new float3(-5f, 0f, 0f);
            bool ok = ForceKernels.PerceptionConeAccepts(self, vel, n, coneCosHalfAngle: 0f);
            Assert.IsFalse(ok, "Neighbour directly behind a +X-moving observer should be rejected by a 90° front-cone.");
        }

        [Test]
        public void PerceptionCone_NeighborAhead_IsAccepted_ForFrontHalfCone()
        {
            var self = float3.zero;
            var vel  = new float3(1f, 0f, 0f);
            var n    = new float3(5f, 0f, 0f);
            bool ok = ForceKernels.PerceptionConeAccepts(self, vel, n, coneCosHalfAngle: 0f);
            Assert.IsTrue(ok, "Neighbour directly ahead must be accepted by a 90° front-cone.");
        }

        [Test]
        public void PerceptionCone_ZeroVelocity_FallsBackTo360Degrees()
        {
            var self = float3.zero;
            var vel  = float3.zero;                 // motionless observer
            var n    = new float3(-5f, 0f, 0f);     // neighbour "behind" — no defined behind
            // Even with a near-180° cone (cos ≈ -1) the explicit zero-velocity branch must accept.
            bool ok = ForceKernels.PerceptionConeAccepts(self, vel, n, coneCosHalfAngle: 0.99f);
            Assert.IsTrue(ok, "Motionless observer must fall back to a 360° (always-accept) perception sphere.");
        }
    }
}
