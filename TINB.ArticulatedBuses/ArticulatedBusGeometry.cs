using Unity.Mathematics;

namespace TINB.ArticulatedBuses
{
    /* Geometry helper class for all mathmatical operations related to the actual prefab meshes */
    public static class ArticulatedBusGeometry
    {
        /* Where to drop the trailer relative to the front. Lines the two prefabs' attach points up at the same spot,
           so the bus and trailer meet at the joint. Used when a trailer is first spawned. */
        public static float3 ComputeTrailerOffset(quaternion frontRotation, float3 frontAttach, float3 trailerAttach)
        {
            return math.rotate(frontRotation, frontAttach) - math.rotate(frontRotation, trailerAttach);
        }

        /* How long the parked bus really is once the trailer is counted. The game only knows about the front, so it
           tries to garage the bus in a front-sized slot; this stretches that slot to cover front + trailer. Returns
           false when nothing needs stretching (the combined length isn't any longer than the front alone). */
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

        /* How much each bellows bone bends. The fold between bus and trailer is split evenly across the N connection
           bones in the chain, so each takes a 1/N share of half the turn and they add up to a smooth accordion.
           Works for any N — more bones just means a smoother fan, and a single bone still bends the right amount. */
        public static float ConnectionBoneFraction(int connectionBoneCount)
        {
            return 0.5f / math.max(1, connectionBoneCount);
        }

        /* Where the two halves of the bellows meet: halfway between the front's edge and the trailer's edge. Both
           sections work this out the same way, so they always agree on the seam and it stays closed however the bus
           bends. */
        public static float3 CapMidpoint(float3 myNaturalCapWorld, float3 neighborNaturalCapWorld)
        {
            return (myNaturalCapWorld + neighborNaturalCapWorld) * 0.5f;
        }
    }
}
