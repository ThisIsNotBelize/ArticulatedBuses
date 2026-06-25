using System;
using System.Reflection;
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
    /* Spawns the rear trailer for a newly created bus front (before the vanilla vehicle InitializeSystem):
       creates and positions the trailer, links it to the front, and flags both for a render refresh */
    public sealed partial class ArticulatedBusTrailerSpawnSystem : GameSystemBase
    {
        /* Cached EntityManager.CreateEntity(EntityArchetype), invoked via reflection in CreateEntity() */
        private static readonly MethodInfo CreateEntityFromArchetypeMethod =
            typeof(EntityManager).GetMethod(nameof(EntityManager.CreateEntity), new[] { typeof(EntityArchetype) }) ??
            throw new MissingMethodException(nameof(EntityManager), nameof(EntityManager.CreateEntity));

        private EntityQuery m_BusQuery;

        /* Query: fresh public-transport bus fronts without a trailer/layout yet (and not temp/deleted) */
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

            RequireForUpdate(m_BusQuery);
        }

        /* Spawns a trailer for each matching bus front */
        protected override void OnUpdate()
        {
            if (!Mod.ShouldRunRuntimeSystems())
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            NativeArray<Entity> buses = m_BusQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < buses.Length; i++)
                {
                    TrySpawnTrailer(entityManager, buses[i], Mod.IsDiagnosticLoggingEnabled());
                }
            }
            finally
            {
                buses.Dispose();
            }
        }

        /* Creates and attaches the fixed trailer for a bus front; shared by the in-game and Asset Icon Creator
           paths. Returns the trailer entity, or Entity.Null if the bus has no valid fixed trailer */
        internal static Entity TrySpawnTrailer(EntityManager entityManager, Entity bus, bool diagnosticLogging)
        {
            PrefabRef busPrefabRef = entityManager.GetComponentData<PrefabRef>(bus);
            Entity busPrefab = busPrefabRef.m_Prefab;

            if (!entityManager.HasComponent<CarTractorData>(busPrefab))
            {
                return Entity.Null;
            }

            CarTractorData tractorData = entityManager.GetComponentData<CarTractorData>(busPrefab);
            Entity trailerPrefab = tractorData.m_FixedTrailer;
            if (trailerPrefab == Entity.Null)
            {
                return Entity.Null;
            }

            if (tractorData.m_TrailerType != CarTrailerType.Fixed)
            {
                if (diagnosticLogging)
                {
                    Mod.Log.WarnFormat("Bus prefab {0} has fixed trailer {1}, but tractor trailer type is {2} instead of Fixed", busPrefab, trailerPrefab, tractorData.m_TrailerType);
                }

                return Entity.Null;
            }

            if (!entityManager.HasComponent<CarTrailerData>(trailerPrefab) ||
                !entityManager.HasComponent<ObjectData>(trailerPrefab))
            {
                if (diagnosticLogging)
                {
                    Mod.Log.WarnFormat("Bus prefab {0} has fixed trailer {1}, but trailer lacks required data", busPrefab, trailerPrefab);
                }

                return Entity.Null;
            }

            CarTrailerData trailerData = entityManager.GetComponentData<CarTrailerData>(trailerPrefab);
            if (trailerData.m_TrailerType != CarTrailerType.Fixed)
            {
                if (diagnosticLogging)
                {
                    Mod.Log.WarnFormat("Bus prefab {0} has fixed trailer {1}, but trailer type is {2} instead of Fixed", busPrefab, trailerPrefab, trailerData.m_TrailerType);
                }

                return Entity.Null;
            }

            if (trailerData.m_FixedTractor != Entity.Null && trailerData.m_FixedTractor != busPrefab)
            {
                if (diagnosticLogging)
                {
                    Mod.Log.WarnFormat("Bus prefab {0} has fixed trailer {1}, but trailer points back to fixed tractor {2}", busPrefab, trailerPrefab, trailerData.m_FixedTractor);
                }

                return Entity.Null;
            }

            ObjectData trailerObjectData = entityManager.GetComponentData<ObjectData>(trailerPrefab);
            Entity trailer = CreateEntity(entityManager, trailerObjectData.m_Archetype);

            ObjectTransform transform = entityManager.GetComponentData<ObjectTransform>(bus);
            ObjectTransform trailerTransform = transform;
            trailerTransform.m_Position += Unity.Mathematics.math.rotate(transform.m_Rotation, tractorData.m_AttachPosition);
            trailerTransform.m_Position -= Unity.Mathematics.math.rotate(transform.m_Rotation, trailerData.m_AttachPosition);

            entityManager.SetComponentData(trailer, trailerTransform);
            entityManager.SetComponentData(trailer, new PrefabRef(trailerPrefab));
            entityManager.SetComponentData(trailer, new Controller(bus));

            if (entityManager.HasComponent<PseudoRandomSeed>(bus))
            {
                /* Reuse the front's seed so MeshColorSystem gives the trailer the same per-vehicle colour
                   variation (a different seed would randomise it to a different shade) */
                PseudoRandomSeed busSeed = entityManager.GetComponentData<PseudoRandomSeed>(bus);
                entityManager.SetComponentData(trailer, busSeed);
            }

            if (entityManager.HasComponent<TripSource>(bus))
            {
                entityManager.AddComponentData(trailer, entityManager.GetComponentData<TripSource>(bus));
            }

            if (entityManager.HasComponent<Unspawned>(bus))
            {
                entityManager.AddComponent<Unspawned>(trailer);
            }

            /* Get-or-add then rebuild so this works for the initial spawn (no buffer yet) AND for the migration
               restore path, where a 1.0.1-contaminated front still carries a [front]-only LayoutElement (its
               trailer was deleted by the old parked logic and ReferencesSystem shrank the buffer) */
            DynamicBuffer<LayoutElement> layout = entityManager.HasComponent<LayoutElement>(bus)
                ? entityManager.GetBuffer<LayoutElement>(bus)
                : entityManager.AddBuffer<LayoutElement>(bus);
            layout.Clear();
            layout.Add(new LayoutElement(bus));
            layout.Add(new LayoutElement(trailer));

            /* Flag both sections so MeshColorSystem + the batch uploader (re)apply their colour; without this our
               restructured front and archetype-created trailer only show colour transiently on hover */
            entityManager.AddComponent<BatchesUpdated>(bus);
            entityManager.AddComponent<BatchesUpdated>(trailer);

            if (diagnosticLogging)
            {
                Mod.Log.InfoFormat("Spawned articulated bus trailer {0} from prefab {1} for bus {2}", trailer, trailerPrefab, bus);
            }

            return trailer;
        }

        /* Creates an entity from the archetype via the cached CreateEntity(EntityArchetype) */
        private static Entity CreateEntity(EntityManager entityManager, EntityArchetype archetype)
        {
            object boxedEntityManager = entityManager;
            return (Entity)CreateEntityFromArchetypeMethod.Invoke(boxedEntityManager, new object[] { archetype });
        }
    }
}
