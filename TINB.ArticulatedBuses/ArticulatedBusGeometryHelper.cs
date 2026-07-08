using Unity.Mathematics;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Geometry math helpers for articulated bus prefabs
    /// </summary>
    public static class ArticulatedBusGeometryHelper
    {
        /// <summary>
        /// Calculate the trailer offset that lines up the front and trailer attach points
        /// </summary>
        /// <returns>The offset from the front origin to the trailer origin, in world space</returns>
        public static float3 ComputeTrailerOffset(quaternion frontRotation, float3 frontAttach, float3 trailerAttach)
        {
            return math.rotate(frontRotation, frontAttach) - math.rotate(frontRotation, trailerAttach);
        }

        /// <summary>
        /// Calculate the combined z-bounds of front and trailer
        /// </summary>
        /// <returns>True if the combined bus is longer than the front alone; the out params hold the union bounds and length</returns>
        public static bool TryComputeInflatedBoundsZ(
            float frontMinZ, float frontMaxZ,
            float trailerMinZ, float trailerMaxZ,
            float trailerOffsetZ,
            out float combinedMinZ, out float combinedMaxZ, out float combinedLength)
        {
            // Union the front and offset trailer bounds
            combinedMinZ = math.min(frontMinZ, trailerOffsetZ + trailerMinZ);
            combinedMaxZ = math.max(frontMaxZ, trailerOffsetZ + trailerMaxZ);
            combinedLength = combinedMaxZ - combinedMinZ;

            // Only stretch if longer than the front alone
            float currentLength = frontMaxZ - frontMinZ;
            return combinedLength > currentLength;
        }

        /// <summary>
        /// Calculate each connection bone's share of the bend
        /// </summary>
        /// <remarks>
        /// Half the turn split across N bones
        /// </remarks>
        /// <returns>The per-bone bend fraction</returns>
        public static float ConnectionBoneFraction(int connectionBoneCount)
        {
            return 0.5f / math.max(1, connectionBoneCount);
        }

        /// <summary>
        /// Calculate the midpoint of two cap bones
        /// </summary>
        /// <returns>The world-space midpoint of the two cap bones</returns>
        public static float3 CapMidpoint(float3 myCapWorld, float3 neighborCapWorld)
        {
            return (myCapWorld + neighborCapWorld) * 0.5f;
        }
    }
}
