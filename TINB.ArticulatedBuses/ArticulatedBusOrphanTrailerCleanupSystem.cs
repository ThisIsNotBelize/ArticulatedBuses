using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Deletes orphan trailers spawned by the mod
    /// </summary>
    /// <remarks>
    /// The bus AI's in-place delete (stuck bus, or returning with no reachable depot parking) adds Deleted to the FRONT
    /// only; vanilla ReferencesSystem then just nulls the trailer's Controller and no stock system ever deletes a
    /// controller-less trailer, rendering a ghost trailer
    /// </remarks>
    public sealed partial class ArticulatedBusOrphanTrailerCleanupSystem : GameSystemBase
    {
        private EntityQuery m_TrailerQuery;
        private PrefabSystem m_PrefabSystem = null!;

        /// <summary>
        /// Build the query of trailer instances that could be checked for an orphaned controller
        /// </summary>
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

        /// <summary>
        /// Delete orphaned trailers
        /// </summary>
        protected override void OnUpdate()
        {
            if (!Mod.IsInGame())
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            NativeArray<Entity> trailers = m_TrailerQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < trailers.Length; i++)
                {
                    Entity trailer = trailers[i];

                    // Skip not orphaned and all other trailers (not in scope of our mod's work)
                    if (!IsOrphaned(entityManager, trailer) || !ArticulatedBusPrefabHelper.IsArticulatedBusTrailerEntity(entityManager, trailer))
                    {
                        continue;
                    }

                    if (Mod.IsDiagnosticLoggingEnabled())
                    {
                        Entity front = entityManager.GetComponentData<Controller>(trailer).m_Controller;
                        Entity trailerPrefab = entityManager.GetComponentData<PrefabRef>(trailer).m_Prefab;
                        string cause = front == Entity.Null ? "null"
                            : !entityManager.Exists(front) ? "missing(destroyed)"
                            : "has-Deleted";
                        SessionLog.Diagnostic($"Deleting orphaned articulated bus trailer {trailer} prefab={ArticulatedBusPrefabHelper.GetPrefabName(m_PrefabSystem, trailerPrefab)} (controller {front} cause={cause})");
                    }

                    // Flag deleted
                    entityManager.AddComponent<Deleted>(trailer);
                    SessionLog.Event($"deleted orphan trailer {trailer}");
                }
            }
            catch (System.Exception ex)
            {
                SessionLog.Exception($"{nameof(ArticulatedBusOrphanTrailerCleanupSystem)}.OnUpdate", ex);
            }
            finally
            {
                trailers.Dispose();
            }
        }

        /// <summary>
        /// Check whether a trailer is orphaned
        /// </summary>
        /// <returns>True when the trailer's controlling front is null, already gone, or being deleted</returns>
        private static bool IsOrphaned(EntityManager entityManager, Entity trailer)
        {
            Entity front = entityManager.GetComponentData<Controller>(trailer).m_Controller;
            return front == Entity.Null || !entityManager.Exists(front) || entityManager.HasComponent<Deleted>(front);
        }

    }
}
