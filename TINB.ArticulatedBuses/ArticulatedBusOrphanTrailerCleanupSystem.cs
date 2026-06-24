using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace TINB.ArticulatedBuses
{
    /* Safety net for the rare despawn path that orphans a trailer. Most despawns cascade correctly (the depot
       deletes the whole LayoutElement group), but the bus AI's in-place delete (stuck bus, or returning with no
       reachable depot parking) adds Deleted to the FRONT only; vanilla ReferencesSystem then just nulls the
       trailer's Controller and no stock system ever deletes a controller-less trailer, so it freezes as a
       rendered ghost and persists in the save. This system deletes such an orphan as soon as it appears (and,
       on load, cleans up any already saved into the file) so removing/re-adding the mod stays save-safe. */
    public sealed partial class ArticulatedBusOrphanTrailerCleanupSystem : GameSystemBase
    {
        private EntityQuery m_TrailerQuery;
        private PrefabSystem m_PrefabSystem = null!;

        /* Query: trailer instances that could be checked for an orphaned controller (never a layout front) */
        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            m_TrailerQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CarTrailer>(),
                    ComponentType.ReadOnly<Controller>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<LayoutElement>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            RequireForUpdate(m_TrailerQuery);
        }

        /* Deletes each orphaned articulated-bus trailer (front gone). Uses direct EntityManager structural
           changes — the EndFrameBarrier rejects CreateCommandBuffer outside its own late window.  */
        protected override void OnUpdate()
        {
            if (!Mod.ShouldRunRuntimeSystems())
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            NativeArray<Entity> trailers = m_TrailerQuery.ToEntityArray(Allocator.Temp);
            bool diagnosticLogging = Mod.IsDiagnosticLoggingEnabled();

            try
            {
                for (int i = 0; i < trailers.Length; i++)
                {
                    Entity trailer = trailers[i];
                    if (!IsOrphaned(entityManager, trailer) || !IsArticulatedBusTrailer(entityManager, trailer))
                    {
                        continue;
                    }

                    if (diagnosticLogging)
                    {
                        Entity front = entityManager.GetComponentData<Controller>(trailer).m_Controller;
                        Entity trailerPrefab = entityManager.GetComponentData<PrefabRef>(trailer).m_Prefab;
                        string cause = front == Entity.Null ? "null"
                            : !entityManager.Exists(front) ? "missing(destroyed)"
                            : "has-Deleted";
                        Mod.Log.InfoFormat("Deleting orphaned articulated bus trailer {0} prefab={1} (controller {2} cause={3})", trailer, TryGetPrefabName(trailerPrefab), front, cause);
                    }

                    entityManager.AddComponent<Deleted>(trailer);
                }
            }
            finally
            {
                trailers.Dispose();
            }
        }

        /* True when the trailer's controlling front is null, already gone, or being deleted (covers every timing
           relative to vanilla ReferencesSystem nulling the link) */
        private static bool IsOrphaned(EntityManager entityManager, Entity trailer)
        {
            Entity front = entityManager.GetComponentData<Controller>(trailer).m_Controller;
            return front == Entity.Null || !entityManager.Exists(front) || entityManager.HasComponent<Deleted>(front);
        }

        /* True only for trailers: a Fixed trailer whose (runtime-repaired) fixed tractor is a public-transport
           bus prefab. Keeps us from ever touching vanilla truck/work-vehicle or other mods' trailers. */
        private static bool IsArticulatedBusTrailer(EntityManager entityManager, Entity trailer)
        {
            Entity trailerPrefab = entityManager.GetComponentData<PrefabRef>(trailer).m_Prefab;
            if (!entityManager.HasComponent<CarTrailerData>(trailerPrefab))
            {
                return false;
            }

            CarTrailerData trailerData = entityManager.GetComponentData<CarTrailerData>(trailerPrefab);
            if (trailerData.m_TrailerType != CarTrailerType.Fixed || trailerData.m_FixedTractor == Entity.Null)
            {
                return false;
            }

            return entityManager.HasComponent<PublicTransportVehicleData>(trailerData.m_FixedTractor);
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
