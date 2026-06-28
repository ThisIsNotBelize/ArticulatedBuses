using Unity.Mathematics;

namespace TINB.ArticulatedBuses
{
    /* Pure, game-independent geometry helpers for the articulated bus.

       Deliberately depends ONLY on Unity.Mathematics + primitives — no ECS (EntityManager/Entity), no UnityEngine,
       no Game types. That keeps these functions deterministic and unit-testable WITHOUT the running game: the test
       project compiles THIS source file directly (see TINB.ArticulatedBuses.Tests) so it never loads Game.dll.

       The ECS systems call these so production and tests share one implementation. Anything here must stay free of
       game/engine types — if a helper needs ColorSet/Color32/Entity it does NOT belong in this file. */
    public static class ArticulatedBusGeometry
    {
        /* World/parent-space position delta from a bus front's origin to its trailer's origin, given the front's
           rotation and the two prefabs' attach points. Mirrors ArticulatedBusTrailerSpawnSystem placement:
           rotate(front, frontAttach) - rotate(front, trailerAttach). */
        public static float3 ComputeTrailerOffset(quaternion frontRotation, float3 frontAttach, float3 trailerAttach)
        {
            return math.rotate(frontRotation, frontAttach) - math.rotate(frontRotation, trailerAttach);
        }

        /* The full assembled front+trailer extent along z and whether the front's parking bounds should be inflated
           to it (garage-forcing). Mirrors ArticulatedBusPrefabConstraintSystem.InflateFrontParkingLength: the
           trailer's z-bounds are shifted by trailerOffsetZ (= front.m_AttachPosition.z - trailer.m_AttachPosition.z)
           and merged with the front's; we only ever EXTEND (return false if the combined length isn't longer). */
        public static bool TryComputeInflatedBoundsZ(
            float frontMinZ, float frontMaxZ,
            float trailerMinZ, float trailerMaxZ,
            float trailerOffsetZ,
            out float combinedMinZ, out float combinedMaxZ, out float combinedLength)
        {
            combinedMinZ = math.min(frontMinZ, trailerOffsetZ + trailerMinZ);
            combinedMaxZ = math.max(frontMaxZ, trailerOffsetZ + trailerMaxZ);
            combinedLength = combinedMaxZ - combinedMinZ;

            float currentLength = frontMaxZ - frontMinZ;
            return combinedLength > currentLength;
        }

        /* The per-bone local rotation fraction for the rigid bellows fan: each of the N VehicleConnection bones in a
           submesh gets slerp(identity, neighborRotation, fraction), and the parent hierarchy accumulates the N of
           them into a fan reaching EXACTLY the half-angle ((q^(0.5/N))^N == q^0.5). N is clamped to >=1 so a reduced
           LOD with a single connection bone collapses to the vanilla half-angle solve. */
        public static float ConnectionBoneFraction(int connectionBoneCount)
        {
            return 0.5f / math.max(1, connectionBoneCount);
        }

        /* The tight-seam target: the midpoint of this section's and the neighbour's natural cap positions. Symmetric,
           so the front solve and the trailer solve compute the identical point and the caps stay flush at any bend. */
        public static float3 CapMidpoint(float3 myNaturalCapWorld, float3 neighborNaturalCapWorld)
        {
            return (myNaturalCapWorld + neighborNaturalCapWorld) * 0.5f;
        }
    }
}
