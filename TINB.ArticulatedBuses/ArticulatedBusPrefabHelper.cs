using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Provide helper methods dealing with prefabs and their instances
    /// </summary>
    /// <remarks>
    /// Re-used identity checks and prefab-name lookup for articulated buses, shared across the systems
    /// </remarks>
    public static class ArticulatedBusPrefabHelper
    {
        /// <summary>
        /// Check whether an instance is an articulated bus front
        /// </summary>
        /// <returns>True if the instance's prefab is an articulated bus front</returns>
        public static bool IsArticulatedBusFrontEntity(EntityManager entityManager, Entity front)
        {
            Entity frontPrefab = entityManager.GetComponentData<PrefabRef>(front).m_Prefab;
            return IsArticulatedBusFrontPrefab(entityManager, frontPrefab);
        }

        /// <summary>
        /// Check whether a prefab is one of our articulated buses
        /// </summary>
        /// <returns>True if the prefab declares a fixed trailer</returns>
        public static bool IsArticulatedBusFrontPrefab(EntityManager entityManager, Entity frontPrefab)
        {
            return entityManager.HasComponent<CarTractorData>(frontPrefab) &&
                   entityManager.GetComponentData<CarTractorData>(frontPrefab).m_FixedTrailer != Entity.Null;
        }

        /// <summary>
        /// Check whether an instance is one of our articulated bus trailers
        /// </summary>
        /// <returns>True only for a Fixed trailer whose fixed tractor is a public-transport bus prefab</returns>
        public static bool IsArticulatedBusTrailerEntity(EntityManager entityManager, Entity trailer)
        {
            Entity trailerPrefab = entityManager.GetComponentData<PrefabRef>(trailer).m_Prefab;
            if (!entityManager.HasComponent<CarTrailerData>(trailerPrefab))
            {
                return false;
            }

            CarTrailerData trailerData = entityManager.GetComponentData<CarTrailerData>(trailerPrefab);
            return trailerData.m_TrailerType == CarTrailerType.Fixed &&
                   trailerData.m_FixedTractor != Entity.Null &&
                   entityManager.HasComponent<PublicTransportVehicleData>(trailerData.m_FixedTractor);
        }

        /// <summary>
        /// Check whether the front already has a spawned trailer
        /// </summary>
        /// <returns>True if the front's layout contains a live member other than the front</returns>
        public static bool HasSpawnedTrailer(EntityManager entityManager, Entity front)
        {
            if (!entityManager.HasBuffer<LayoutElement>(front))
            {
                return false;
            }

            DynamicBuffer<LayoutElement> layout = entityManager.GetBuffer<LayoutElement>(front);
            for (int i = 0; i < layout.Length; i++)
            {
                Entity vehicle = layout[i].m_Vehicle;
                if (vehicle != Entity.Null && vehicle != front && entityManager.Exists(vehicle))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Get a prefab's display name for logs
        /// </summary>
        /// <returns>The prefab name, or the entity id if it can't be resolved</returns>
        public static string GetPrefabName(PrefabSystem prefabSystem, Entity prefabEntity)
        {
            return prefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out PrefabBase prefab)
                ? prefab.name
                : prefabEntity.ToString();
        }
    }
}
