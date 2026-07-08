using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Remove all articulated buses
    /// </summary>
    /// <remarks>
    /// Manually fired via the settings button
    /// </remarks>
    public sealed partial class ArticulatedBusCleanupSystem : GameSystemBase
    {
        private static volatile bool s_CleanupRequested;

        private EntityQuery m_FrontQuery;
        private EntityQuery m_TrailerQuery;

        /// <summary>
        /// Request a cleanup of all articulated buses
        /// </summary>
        /// <remarks>
        /// Called by the settings button
        /// </remarks>
        public static void RequestCleanup()
        {
            if (!Mod.IsInGame())
            {
                SessionLog.Event("cleanup request ignored (not in a game)");
                return;
            }

            s_CleanupRequested = true;
            SessionLog.Event("cleanup of all articulated buses requested via settings");
            SessionLog.Diagnostic("Articulated bus cleanup requested via settings");
        }

        /// <summary>
        /// Build the queries for all articulated fronts and trailers
        /// </summary>
        protected override void OnCreate()
        {
            base.OnCreate();

            // Live articulated fronts, parked or driving
            m_FrontQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<VehiclePublicTransport>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<CarTrailer>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            // All trailer instances (catches orphans and layout-less strays too)
            m_TrailerQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CarTrailer>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });
        }

        /// <summary>
        /// Filter to articulated buses and remove their fronts, layout members, and orphan trailers
        /// </summary>
        protected override void OnUpdate()
        {
            if (!s_CleanupRequested || !Mod.IsInGame())
            {
                return;
            }

            s_CleanupRequested = false; // Reset once fired

            EntityManager entityManager = EntityManager;
            // Stats
            int frontsDeleted = 0;
            int trailersDeleted = 0;

            try
            {
                // Delete the front and trailers (as layout members of fronts), mirroring vanilla VehicleUtils.DeleteVehicle
                NativeArray<Entity> fronts = m_FrontQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < fronts.Length; i++)
                    {
                        Entity front = fronts[i];
                        // Only if is articulated bus front
                        if (!ArticulatedBusPrefabHelper.IsArticulatedBusFrontEntity(entityManager, front))
                        {
                            continue;
                        }

                        if (entityManager.HasBuffer<LayoutElement>(front))
                        {
                            DynamicBuffer<LayoutElement> layout = entityManager.GetBuffer<LayoutElement>(front);
                            for (int j = 0; j < layout.Length; j++)
                            {
                                Entity member = layout[j].m_Vehicle;
                                if (member != Entity.Null && member != front && entityManager.Exists(member) &&
                                    !entityManager.HasComponent<Deleted>(member))
                                {
                                    entityManager.AddComponent<Deleted>(member);
                                    trailersDeleted++;
                                }
                            }
                        }

                        entityManager.AddComponent<Deleted>(front);
                        frontsDeleted++;
                    }
                }
                finally
                {
                    fronts.Dispose();
                }

                // Delete orphan trailers
                NativeArray<Entity> trailers = m_TrailerQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < trailers.Length; i++)
                    {
                        Entity trailer = trailers[i];
                        if (entityManager.HasComponent<Deleted>(trailer) || !ArticulatedBusPrefabHelper.IsArticulatedBusTrailerEntity(entityManager, trailer))
                        {
                            continue;
                        }

                        entityManager.AddComponent<Deleted>(trailer);
                        trailersDeleted++;
                    }
                }
                finally
                {
                    trailers.Dispose();
                }

                SessionLog.Event($"cleanup done: removed {frontsDeleted} articulated bus front(s) and {trailersDeleted} trailer(s)");
                SessionLog.Diagnostic($"Articulated bus cleanup done: removed {frontsDeleted} front(s) and {trailersDeleted} trailer(s)");
            }
            catch (System.Exception ex)
            {
                SessionLog.Exception($"{nameof(ArticulatedBusCleanupSystem)}.OnUpdate", ex);
            }
        }

    }
}
