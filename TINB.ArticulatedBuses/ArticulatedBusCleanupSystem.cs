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
    /* One-shot "remove all articulated buses" requested from the options page. Deletes every articulated bus front
       together with its layout members, plus any stray articulated trailer, the vanilla way (Deleted tag). Lines and
       depots are untouched and dispatch replacement buses. Lets a user produce a clean save before removing the mod,
       or repair a save by re-installing the mod once, cleaning up, saving and removing it again. */
    public sealed partial class ArticulatedBusCleanupSystem : GameSystemBase
    {
        private static volatile bool s_CleanupRequested;

        private EntityQuery m_FrontQuery;
        private EntityQuery m_TrailerQuery;

        /* Called by the options button. Only accepted while actually in a game, so a click in the main menu can't
           arm a deletion that would fire on the next city load. */
        public static void RequestCleanup()
        {
            if (!Mod.ShouldRunRuntimeSystems())
            {
                SessionLog.Event("cleanup request ignored (not in a game)");
                return;
            }

            s_CleanupRequested = true;
            SessionLog.Event("cleanup of all articulated buses requested via options");

            /* Also to the dev log: it is append-only across the launch, so this survives the reloads that truncate
               the session log — keeping the cleanup verifiable after the user saves and reloads */
            if (Mod.IsDiagnosticLoggingEnabled())
            {
                Mod.Log.Info("Articulated bus cleanup requested via options");
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            // Live articulated fronts, parked or driving (filtered to our buses in code).
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

            // All trailer instances (catches orphans and layout-less strays too).
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

        protected override void OnUpdate()
        {
            if (!s_CleanupRequested || !Mod.ShouldRunRuntimeSystems())
            {
                return;
            }

            s_CleanupRequested = false;

            EntityManager entityManager = EntityManager;
            int frontsDeleted = 0;
            int trailersDeleted = 0;

            try
            {
                /* Fronts first: delete the front and every layout member, mirroring vanilla VehicleUtils.DeleteVehicle */
                NativeArray<Entity> fronts = m_FrontQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < fronts.Length; i++)
                    {
                        Entity front = fronts[i];
                        if (!IsArticulatedBusFront(entityManager, front))
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

                /* Then any articulated trailer not caught above (orphans, strays) */
                NativeArray<Entity> trailers = m_TrailerQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < trailers.Length; i++)
                    {
                        Entity trailer = trailers[i];
                        if (entityManager.HasComponent<Deleted>(trailer) || !IsArticulatedBusTrailer(entityManager, trailer))
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

                SessionLog.Event($"cleanup done: removed {frontsDeleted} articulated bus front(s) and {trailersDeleted} trailer(s); lines/depots untouched");
                if (Mod.IsDiagnosticLoggingEnabled())
                {
                    Mod.Log.InfoFormat("Articulated bus cleanup done: removed {0} front(s) and {1} trailer(s); lines/depots untouched", frontsDeleted, trailersDeleted);
                }
            }
            catch (System.Exception ex)
            {
                SessionLog.Exception($"{nameof(ArticulatedBusCleanupSystem)}.OnUpdate", ex);
            }
        }

        /* True if the front's prefab declares a fixed trailer (i.e. it is one of our articulated buses) */
        private static bool IsArticulatedBusFront(EntityManager entityManager, Entity front)
        {
            Entity frontPrefab = entityManager.GetComponentData<PrefabRef>(front).m_Prefab;
            return entityManager.HasComponent<CarTractorData>(frontPrefab) &&
                   entityManager.GetComponentData<CarTractorData>(frontPrefab).m_FixedTrailer != Entity.Null;
        }

        /* True only for our trailers: a Fixed trailer whose (runtime-repaired) fixed tractor is a public-transport
           bus prefab */
        private static bool IsArticulatedBusTrailer(EntityManager entityManager, Entity trailer)
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
    }
}
