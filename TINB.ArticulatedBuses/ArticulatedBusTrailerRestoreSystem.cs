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
    /// Restore trailers in case they are missing
    /// </summary>
    public sealed partial class ArticulatedBusTrailerRestoreSystem : GameSystemBase
    {
        private EntityQuery m_BusQuery;

        /// <summary>
        /// Build the query for un-parked bus fronts
        /// </summary>
        /// <remarks>
        /// Parked is excluded on purpose. Restoring a trailer onto a bay-parked front recreates a bay-parked
        /// articulated bus, which leads to depot-upgrade CTD conditions (bug in 1.0.1)
        /// </remarks>
        protected override void OnCreate()
        {
            base.OnCreate();

            m_BusQuery = GetEntityQuery(new EntityQueryDesc
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
                    ComponentType.ReadOnly<ParkedCar>(),
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            // run OnUpdate when matching instances
            RequireForUpdate(m_BusQuery);
        }

        /// <summary>
        /// Re-spawn a trailer for any un-parked articulated front that is missing one
        /// </summary>
        protected override void OnUpdate()
        {
            if (!Mod.IsInGame())
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            NativeArray<Entity> fronts = m_BusQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < fronts.Length; i++)
                {
                    Entity front = fronts[i];
                    if (!ArticulatedBusPrefabHelper.IsArticulatedBusFrontEntity(entityManager, front) || ArticulatedBusPrefabHelper.HasSpawnedTrailer(entityManager, front))
                    {
                        continue;
                    }

                    Entity restored = ArticulatedBusTrailerSpawnSystem.TrySpawnTrailer(entityManager, front);
                    if (restored != Entity.Null)
                    {
                        SessionLog.Event($"restored missing trailer {restored} for articulated bus front {front} (integrity)");
                        SessionLog.Diagnostic($"Restored missing trailer {restored} for articulated bus front {front} (integrity)");
                    }
                }
            }
            catch (System.Exception ex)
            {
                SessionLog.Exception($"{nameof(ArticulatedBusTrailerRestoreSystem)}.OnUpdate", ex);
            }
            finally
            {
                fronts.Dispose();
            }
        }
    }
}
