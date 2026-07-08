using System;
using NUnit.Framework;
using TINB.ArticulatedBuses;
using Unity.Mathematics;

namespace TINB.ArticulatedBuses.Tests
{
    [TestFixture]
    public class ArticulatedBusGeometryTests
    {
        private const float Tol = 1e-4f;

        // Two rotations are equal up to sign (q and -q are the same orientation)
        private static void AssertSameRotation(quaternion expected, quaternion actual, string msg = "")
        {
            float dot = math.abs(math.dot(expected.value, actual.value));
            Assert.That(dot, Is.GreaterThan(1f - 1e-3f), $"rotations differ ({dot}). {msg}");
        }

        // ---- ComputeTrailerOffset --------------------------------------------------------------------------

        [Test]
        public void TrailerOffset_Identity_IsAttachDifference()
        {
            float3 frontAttach = new float3(0f, 0f, 3.0f);
            float3 trailerAttach = new float3(0f, 0f, -4.0f);

            float3 offset = ArticulatedBusGeometryHelper.ComputeTrailerOffset(quaternion.identity, frontAttach, trailerAttach);

            Assert.That(offset.x, Is.EqualTo(0f).Within(Tol));
            Assert.That(offset.y, Is.EqualTo(0f).Within(Tol));
            Assert.That(offset.z, Is.EqualTo(7.0f).Within(Tol)); // 3 - (-4)
        }

        [Test]
        public void TrailerOffset_IsLinearUnderRotation()
        {
            // rotate(q,a) - rotate(q,b) == rotate(q, a-b): the offset rotates rigidly with the front
            float3 frontAttach = new float3(0.2f, 0.0f, 3.1f);
            float3 trailerAttach = new float3(-0.1f, 0.0f, -4.3f);
            quaternion q = quaternion.AxisAngle(math.up(), math.radians(37f));

            float3 offset = ArticulatedBusGeometryHelper.ComputeTrailerOffset(q, frontAttach, trailerAttach);
            float3 expected = math.rotate(q, frontAttach - trailerAttach);

            Assert.That(math.distance(offset, expected), Is.LessThan(Tol));
        }

        [Test]
        public void TrailerOffset_NinetyDegYaw_MapsZToX()
        {
            // attach difference is purely +Z; a +90° yaw about Y maps +Z onto +X (Unity left-handed forward=+Z)
            float3 frontAttach = new float3(0f, 0f, 1f);
            float3 trailerAttach = float3.zero;
            quaternion yaw90 = quaternion.AxisAngle(math.up(), math.radians(90f));

            float3 offset = ArticulatedBusGeometryHelper.ComputeTrailerOffset(yaw90, frontAttach, trailerAttach);

            Assert.That(math.abs(offset.x), Is.EqualTo(1f).Within(1e-3f));
            Assert.That(offset.z, Is.EqualTo(0f).Within(1e-3f));
        }

        // ---- TryComputeInflatedBoundsZ --------------------------------------------------------------------

        [Test]
        public void Inflate_ExtendsRearwardForTrailerBehindFront()
        {
            // front spans z in [-6, 6] (len 12); trailer [-7, 7] shifted back by offset -12 -> [-19, -5]
            bool inflate = ArticulatedBusGeometryHelper.TryComputeInflatedBoundsZ(
                frontMinZ: -6f, frontMaxZ: 6f,
                trailerMinZ: -7f, trailerMaxZ: 7f,
                trailerOffsetZ: -12f,
                out float minZ, out float maxZ, out float length);

            Assert.That(inflate, Is.True);
            Assert.That(minZ, Is.EqualTo(-19f).Within(Tol)); // min(-6, -12 + -7)
            Assert.That(maxZ, Is.EqualTo(6f).Within(Tol));   // max(6, -12 + 7)
            Assert.That(length, Is.EqualTo(25f).Within(Tol));
            Assert.That(length, Is.GreaterThan(12f));         // strictly longer than the front alone
        }

        [Test]
        public void Inflate_NoOpWhenFrontAlreadySpansWholeBus()
        {
            // trailer fully inside the front's z-extent -> combined length == front length -> do not inflate
            bool inflate = ArticulatedBusGeometryHelper.TryComputeInflatedBoundsZ(
                frontMinZ: -10f, frontMaxZ: 10f,
                trailerMinZ: -1f, trailerMaxZ: 1f,
                trailerOffsetZ: 0f,
                out _, out _, out float length);

            Assert.That(inflate, Is.False);
            Assert.That(length, Is.EqualTo(20f).Within(Tol));
        }

        [Test]
        public void Inflate_NeverShrinks()
        {
            // even a tiny trailer can never produce a shorter combined length than the front
            bool inflate = ArticulatedBusGeometryHelper.TryComputeInflatedBoundsZ(
                frontMinZ: -5f, frontMaxZ: 5f,
                trailerMinZ: 0f, trailerMaxZ: 0.1f,
                trailerOffsetZ: 0f,
                out _, out _, out float length);

            Assert.That(length, Is.GreaterThanOrEqualTo(10f));
            Assert.That(inflate, Is.False);
        }

        // ---- ConnectionBoneFraction -----------------------------------------------------------------------

        [Test]
        public void Fraction_IsHalfOverN()
        {
            Assert.That(ArticulatedBusGeometryHelper.ConnectionBoneFraction(1), Is.EqualTo(0.5f).Within(Tol));
            Assert.That(ArticulatedBusGeometryHelper.ConnectionBoneFraction(2), Is.EqualTo(0.25f).Within(Tol));
            Assert.That(ArticulatedBusGeometryHelper.ConnectionBoneFraction(5), Is.EqualTo(0.1f).Within(Tol));
        }

        [Test]
        public void Fraction_ClampsNonPositiveCountToOne()
        {
            Assert.That(ArticulatedBusGeometryHelper.ConnectionBoneFraction(0), Is.EqualTo(0.5f).Within(Tol));
            Assert.That(ArticulatedBusGeometryHelper.ConnectionBoneFraction(-3), Is.EqualTo(0.5f).Within(Tol));
        }

        [TestCase(10f, 1)]
        [TestCase(45f, 2)]
        [TestCase(90f, 3)]
        [TestCase(170f, 5)]
        public void Fraction_FanAccumulatesToExactHalfAngle(float degrees, int n)
        {
            // The core bellows guarantee: N bones each rotated by slerp(identity, q, 0.5/N) accumulate to q^0.5
            // (the half-angle / seam bisector), independent of N. This is why a reduced LOD self-adapts
            quaternion q = quaternion.AxisAngle(math.normalize(new float3(0.3f, 1f, 0.2f)), math.radians(degrees));
            float frac = ArticulatedBusGeometryHelper.ConnectionBoneFraction(n);
            quaternion perBone = math.slerp(quaternion.identity, q, frac);

            quaternion accumulated = quaternion.identity;
            for (int i = 0; i < n; i++)
            {
                accumulated = math.mul(accumulated, perBone);
            }

            quaternion halfAngle = math.slerp(quaternion.identity, q, 0.5f);
            AssertSameRotation(halfAngle, accumulated, $"degrees={degrees}, n={n}");
        }

        // ---- CapMidpoint ----------------------------------------------------------------------------------

        [Test]
        public void CapMidpoint_IsAverageAndSymmetric()
        {
            float3 a = new float3(1f, 2f, 3f);
            float3 b = new float3(5f, -4f, 9f);

            float3 mid = ArticulatedBusGeometryHelper.CapMidpoint(a, b);

            Assert.That(math.distance(mid, new float3(3f, -1f, 6f)), Is.LessThan(Tol));
            // symmetric: both sections compute the same seam point regardless of argument order
            Assert.That(math.distance(mid, ArticulatedBusGeometryHelper.CapMidpoint(b, a)), Is.LessThan(Tol));
        }
    }
}
