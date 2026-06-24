using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace TINB.ArticulatedBuses
{
    /* Makes the trailer exist only while the bus is active, so a parked bus never blocks the depot driveway.
       A parked bus stays a depot-owned, reused entity (it carries the bus's paint/name/odometer and the depot
       re-dispatches the SAME entity), so we must NOT delete the front. But the long trailer overhangs the
       driveway and its lane reservation is tied to CarTrailerLane (Unspawned does not free it — see
       Game.Net.FixLaneObjectsSystem), so the only way to stop the blocking is for the trailer to not exist while
       parked. The trailer is our reconstructible add-on (no persistent state — colours/identity derive from the
       front), so:
         - On park (front ParkedCar): delete the trailer (frees the lane) and Unspawn the front (hides the lone
           front; the front fits its own slot so it never blocked).
         - On deploy: vanilla removes the front's Unspawned once it is on a road lane (VehicleUtils.CheckUnspawned),
           and we respawn the trailer behind the now-active front.
       Vanilla-data-only and save-safe (a save taken while parked just stores a front with a [front] layout; the
       trailer is recreated on the next deploy; with the mod removed it loads as an ordinary single bus). */
    public sealed partial class ArticulatedBusParkedTrailerSystem : GameSystemBase
    {
        private EntityQuery m_BusQuery;
        private ArticulatedBusColorSyncSystem m_ColorSync = null!;
        private PrefabSystem m_PrefabSystem = null!;

        /* Query: articulated bus fronts (layout leads, public transport), not temp/deleted */
        protected override void OnCreate()
        {
            base.OnCreate();

            m_ColorSync = World.GetOrCreateSystemManaged<ArticulatedBusColorSyncSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            m_BusQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<VehiclePublicTransport>(),
                    ComponentType.ReadOnly<LayoutElement>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<CarTrailer>()
                }
            });

            RequireForUpdate(m_BusQuery);
        }

        protected override void OnUpdate()
        {
            if (!Mod.ShouldRunRuntimeSystems())
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            NativeArray<Entity> buses = m_BusQuery.ToEntityArray(Allocator.Temp);
            bool diagnosticLogging = Mod.IsDiagnosticLoggingEnabled();

            try
            {
                for (int i = 0; i < buses.Length; i++)
                {
                    UpdateBus(entityManager, buses[i], diagnosticLogging);
                }
            }
            finally
            {
                buses.Dispose();
            }
        }

        private void UpdateBus(EntityManager entityManager, Entity front, bool diagnosticLogging)
        {
            /* Only our articulated buses (front prefab declares a fixed trailer) */
            Entity frontPrefab = entityManager.GetComponentData<PrefabRef>(front).m_Prefab;
            if (!entityManager.HasComponent<CarTractorData>(frontPrefab) ||
                entityManager.GetComponentData<CarTractorData>(frontPrefab).m_FixedTrailer == Entity.Null)
            {
                return;
            }

            Entity trailer = FindTrailer(entityManager, front);
            bool frontParked = entityManager.HasComponent<ParkedCar>(front);
            bool frontUnspawned = entityManager.HasComponent<Unspawned>(front);

            if (frontParked)
            {
                /* Hide the whole bus: delete the (lane-blocking) trailer and unspawn the front */
                if (trailer != Entity.Null && !entityManager.HasComponent<Deleted>(trailer))
                {
                    if (diagnosticLogging)
                    {
                        Entity trailerPrefab = entityManager.GetComponentData<PrefabRef>(trailer).m_Prefab;
                        Mod.Log.InfoFormat("Deleting parked articulated bus trailer {0} prefab={1} (front {2} parked)", trailer, TryGetPrefabName(trailerPrefab), front);
                    }

                    entityManager.AddComponent<Deleted>(trailer);
                }

                if (!frontUnspawned)
                {
                    entityManager.AddComponent<Unspawned>(front);
                    entityManager.AddComponent<BatchesUpdated>(front);
                }

                return;
            }

            if (frontUnspawned)
            {
                return; // not parked but still hidden -> mid-emerge on deploy; wait for vanilla to spawn the front
            }

            /* Active, visible front */
            if (trailer == Entity.Null)
            {
                /* Respawn the trailer that was removed while parked */
                Entity spawned = ArticulatedBusTrailerSpawnSystem.TrySpawnTrailer(entityManager, front, diagnosticLogging);
                if (spawned != Entity.Null)
                {
                    m_ColorSync.ForgetFront(front); // re-push livery/custom colour onto the fresh trailer
                }
            }
            else if (entityManager.HasComponent<Unspawned>(trailer))
            {
                /* Self-heal: a trailer left hidden behind a live front (e.g. an older save) -> show it */
                entityManager.RemoveComponent<Unspawned>(trailer);
                entityManager.AddComponent<BatchesUpdated>(trailer);
            }
        }

        /* Returns the first live trailer in the front's layout, or Entity.Null if none */
        private static Entity FindTrailer(EntityManager entityManager, Entity front)
        {
            DynamicBuffer<LayoutElement> layout = entityManager.GetBuffer<LayoutElement>(front);
            for (int i = 1; i < layout.Length; i++)
            {
                Entity vehicle = layout[i].m_Vehicle;
                if (vehicle != Entity.Null && vehicle != front && entityManager.Exists(vehicle))
                {
                    return vehicle;
                }
            }

            return Entity.Null;
        }

        /* Prefab display name for diagnostics (falls back to the entity id) */
        private string TryGetPrefabName(Entity prefabEntity)
        {
            return m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out PrefabBase prefab)
                ? prefab.name
                : prefabEntity.ToString();
        }
    }
}
