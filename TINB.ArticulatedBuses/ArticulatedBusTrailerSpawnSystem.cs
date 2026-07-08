using System;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using ObjectTransform = Game.Objects.Transform;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Spawns trailers
    /// </summary>
    public sealed partial class ArticulatedBusTrailerSpawnSystem : GameSystemBase
    {
        private EntityQuery m_BusQuery;

        /// <summary>
        /// Query articulated bus instances without spawned trailers
        /// </summary>
        /// <remarks>
        /// Temp and Deleted instances are excluded
        /// </remarks>
        protected override void OnCreate()
        {
            base.OnCreate();

            m_BusQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<VehiclePublicTransport>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<ObjectTransform>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<CarTrailer>(),
                    ComponentType.ReadOnly<LayoutElement>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            // run OnUpdate when matching instances
            RequireForUpdate(m_BusQuery);
        }

        /// <summary>
        /// Iterate bus fronts for spawning
        /// </summary>
        protected override void OnUpdate()
        {
            if (!Mod.IsInGame())
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            NativeArray<Entity> buses = m_BusQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < buses.Length; i++)
                {
                    Entity bus = buses[i];
                    Entity trailer = TrySpawnTrailer(entityManager, bus);
                    if (trailer != Entity.Null)
                    {
                        SessionLog.Event($"spawned trailer {trailer} for new bus front {bus}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                SessionLog.Exception($"{nameof(ArticulatedBusTrailerSpawnSystem)}.OnUpdate", ex);
            }
            finally
            {
                buses.Dispose();
            }
        }

        /// <summary>
        /// Spawn a trailer for a bus front
        /// </summary>
        /// <returns>The spawned trailer entity, or Entity.Null if the front is not an eligible articulated bus</returns>
        internal static Entity TrySpawnTrailer(EntityManager entityManager, Entity bus)
        {
            PrefabRef busPrefabRef = entityManager.GetComponentData<PrefabRef>(bus);
            Entity busPrefab = busPrefabRef.m_Prefab;

            // Only if car tractor component exists
            if (!entityManager.HasComponent<CarTractorData>(busPrefab))
            {
                return Entity.Null;
            }

            // Only if trailer prefab exists
            CarTractorData tractorData = entityManager.GetComponentData<CarTractorData>(busPrefab);
            Entity trailerPrefab = tractorData.m_FixedTrailer;
            if (trailerPrefab == Entity.Null)
            {
                return Entity.Null;
            }

            // Only fixed trailers
            if (tractorData.m_TrailerType != CarTrailerType.Fixed)
            {
                SessionLog.DiagnosticWarn($"Bus prefab {busPrefab} has fixed trailer {trailerPrefab}, but tractor trailer type is {tractorData.m_TrailerType} instead of Fixed");
                return Entity.Null;
            }

            // Only if trailer components exist
            if (!entityManager.HasComponent<CarTrailerData>(trailerPrefab) ||
                !entityManager.HasComponent<ObjectData>(trailerPrefab))
            {
                SessionLog.DiagnosticWarn($"Bus prefab {busPrefab} has fixed trailer {trailerPrefab}, but trailer lacks required data");
                return Entity.Null;
            }

            // Only fixed fronts
            CarTrailerData trailerData = entityManager.GetComponentData<CarTrailerData>(trailerPrefab);
            if (trailerData.m_TrailerType != CarTrailerType.Fixed)
            {
                SessionLog.DiagnosticWarn($"Bus prefab {busPrefab} has fixed trailer {trailerPrefab}, but trailer type is {trailerData.m_TrailerType} instead of Fixed");
                return Entity.Null;
            }

            // Only if trailer does not point to another tractor (e.g. truck)
            if (trailerData.m_FixedTractor != Entity.Null && trailerData.m_FixedTractor != busPrefab)
            {
                SessionLog.DiagnosticWarn($"Bus prefab {busPrefab} has fixed trailer {trailerPrefab}, but trailer points back to fixed tractor {trailerData.m_FixedTractor}");
                return Entity.Null;
            }

            // create trailer
            ObjectData trailerObjectData = entityManager.GetComponentData<ObjectData>(trailerPrefab);
            Entity trailer = entityManager.CreateEntity(trailerObjectData.m_Archetype);

            // To avoid orphans, possible exceptions lead to marking Deleted flag
            try
            {
                FinishTrailerSpawn(entityManager, bus, trailer, trailerPrefab, tractorData, trailerData);
            }
            catch (System.Exception ex)
            {
                entityManager.AddComponent<Deleted>(trailer);
                SessionLog.Exception($"{nameof(ArticulatedBusTrailerSpawnSystem)}.{nameof(TrySpawnTrailer)}", ex);
                return Entity.Null;
            }

            SessionLog.Diagnostic($"Spawned articulated bus trailer {trailer} from prefab {trailerPrefab} for bus {bus}");

            return trailer;
        }

        /// <summary>
        /// Finalize trailer spawn
        /// </summary>
        private static void FinishTrailerSpawn(EntityManager entityManager, Entity bus, Entity trailer, Entity trailerPrefab, CarTractorData tractorData, CarTrailerData trailerData)
        {
            // Apply front's transform
            ObjectTransform transform = entityManager.GetComponentData<ObjectTransform>(bus);
            ObjectTransform trailerTransform = transform;
            trailerTransform.m_Position += ArticulatedBusGeometryHelper.ComputeTrailerOffset(transform.m_Rotation, tractorData.m_AttachPosition, trailerData.m_AttachPosition);

            entityManager.SetComponentData(trailer, trailerTransform);

            // Apply reference and attach front controller
            entityManager.SetComponentData(trailer, new PrefabRef(trailerPrefab));
            entityManager.SetComponentData(trailer, new Controller(bus));

            // Sync to front (misc)

            // Seed
            if (entityManager.HasComponent<PseudoRandomSeed>(bus))
            {
                // Reuse the front's seed so MeshColorSystem gives the trailer the same per-vehicle color
                // variation (a different seed would randomize it to a different shade)
                PseudoRandomSeed busSeed = entityManager.GetComponentData<PseudoRandomSeed>(bus);
                entityManager.SetComponentData(trailer, busSeed);
            }

            // Trip
            if (entityManager.HasComponent<TripSource>(bus))
            {
                entityManager.AddComponentData(trailer, entityManager.GetComponentData<TripSource>(bus));
            }

            // Unspawned flag
            if (entityManager.HasComponent<Unspawned>(bus))
            {
                entityManager.AddComponent<Unspawned>(trailer);
            }

            // Get-or-add layout buffer of front bus
            DynamicBuffer<LayoutElement> layout = entityManager.HasComponent<LayoutElement>(bus)
                ? entityManager.GetBuffer<LayoutElement>(bus)
                : entityManager.AddBuffer<LayoutElement>(bus);
            layout.Clear();

            // Add trailer to layout (buffer)
            layout.Add(new LayoutElement(bus));
            layout.Add(new LayoutElement(trailer));

            // Flag updated for MeshColorSystem + the batch/instance uploader rendering/batching systems
            entityManager.AddComponent<BatchesUpdated>(bus);
            entityManager.AddComponent<BatchesUpdated>(trailer);
        }
    }
}
