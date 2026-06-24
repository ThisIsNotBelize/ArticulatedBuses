using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace TINB.ArticulatedBuses
{
    /* Keeps the trailer's user-painted colour (CustomMeshColor) in sync with the front, front-authoritative:
       when the front's colour changes (painted or reset) it is pushed to the trailer; a trailer-only edit
       survives until the front changes again. Only Brand-sourced trailers are synced (others stay independent) */
    public sealed partial class ArticulatedBusColorSyncSystem : GameSystemBase
    {
        /* Snapshot of a front's custom-colour state, to detect changes between frames */
        private struct FrontColorState
        {
            public bool m_HasCustom;
            public ColorSet m_ColorSet;
        }

        private readonly Dictionary<Entity, FrontColorState> m_LastSyncedFront = new Dictionary<Entity, FrontColorState>();
        private readonly Dictionary<Entity, bool> m_TrailerPrefabIsBrand = new Dictionary<Entity, bool>();

        /* Drops the cached colour state for a front so the next sync re-pushes its colour to the trailer(s).
           Called after a trailer is respawned on depot deploy, so the fresh trailer immediately gets the
           front's current livery and any custom repaint instead of waiting for the next front colour change. */
        internal void ForgetFront(Entity front)
        {
            m_LastSyncedFront.Remove(front);
        }

        private EntityQuery m_BusQuery;
        private EndFrameBarrier m_Barrier = null!;

        /* Caches the EndFrameBarrier and builds the query for articulated bus fronts (layout leads) */
        protected override void OnCreate()
        {
            base.OnCreate();

            m_Barrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

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

        /* Pushes any changed front colour to its trailer(s) */
        protected override void OnUpdate()
        {
            if (!Mod.ShouldRunRuntimeSystems())
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            EntityCommandBuffer commandBuffer = m_Barrier.CreateCommandBuffer();
            NativeArray<Entity> roots = m_BusQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < roots.Length; i++)
                {
                    SyncBus(entityManager, commandBuffer, roots[i]);
                }
            }
            finally
            {
                roots.Dispose();
            }
        }

        /* Pushes the front's colour to each Brand-sourced trailer, but only when the front changed since the
           last sync (so a manual trailer-only edit survives until the front is repainted/reset) */
        private void SyncBus(EntityManager entityManager, EntityCommandBuffer commandBuffer, Entity root)
        {
            DynamicBuffer<LayoutElement> layout = entityManager.GetBuffer<LayoutElement>(root);
            if (layout.Length < 2 || layout[0].m_Vehicle != root)
            {
                return;
            }

            /* Only our articulated buses (front prefab declares a fixed trailer) */
            Entity rootPrefab = entityManager.GetComponentData<PrefabRef>(root).m_Prefab;
            if (!entityManager.HasComponent<CarTractorData>(rootPrefab) ||
                entityManager.GetComponentData<CarTractorData>(rootPrefab).m_FixedTrailer == Entity.Null)
            {
                return;
            }

            FrontColorState current = ReadCustomColor(entityManager, root);
            if (m_LastSyncedFront.TryGetValue(root, out FrontColorState last) &&
                last.m_HasCustom == current.m_HasCustom &&
                (!current.m_HasCustom || ColorSetEquals(last.m_ColorSet, current.m_ColorSet)))
            {
                return; // front unchanged since last sync -> leave trailer (and any manual trailer edit) alone
            }

            bool diagnosticLogging = Mod.IsDiagnosticLoggingEnabled();
            for (int i = 1; i < layout.Length; i++)
            {
                Entity trailer = layout[i].m_Vehicle;
                if (trailer == Entity.Null)
                {
                    continue;
                }

                bool brand = TrailerIsBrand(entityManager, trailer);
                if (diagnosticLogging)
                {
                    UnityEngine.Color c = current.m_ColorSet.m_Channel0;
                    Mod.Log.Info($"{nameof(ArticulatedBusColorSyncSystem)} diagnostics: front={root} hasCustom={current.m_HasCustom}, frontCh0=({c.r:F3},{c.g:F3},{c.b:F3}), trailer={trailer}, brand={brand}, trailerHadCustom={entityManager.HasComponent<CustomMeshColor>(trailer) && entityManager.IsComponentEnabled<CustomMeshColor>(trailer)}");
                }

                if (!brand)
                {
                    continue;
                }

                ApplyCustomColor(entityManager, commandBuffer, trailer, current);
            }

            m_LastSyncedFront[root] = current;
        }

        /* Reads an entity's current custom-colour state (whether an override is enabled, and its colour set) */
        private static FrontColorState ReadCustomColor(EntityManager entityManager, Entity entity)
        {
            FrontColorState state = default(FrontColorState);
            if (entityManager.HasComponent<CustomMeshColor>(entity) &&
                entityManager.IsComponentEnabled<CustomMeshColor>(entity))
            {
                DynamicBuffer<CustomMeshColor> buffer = entityManager.GetBuffer<CustomMeshColor>(entity);
                if (buffer.Length > 0)
                {
                    state.m_HasCustom = true;
                    state.m_ColorSet = buffer[0].m_ColorSet;
                }
            }

            return state;
        }

        /* Writes/enables the trailer's override when the front has one, disables it when the front is back on the
           line colour, then forces a batch refresh */
        private static void ApplyCustomColor(EntityManager entityManager, EntityCommandBuffer commandBuffer, Entity trailer, FrontColorState state)
        {
            if (state.m_HasCustom)
            {
                if (!entityManager.HasComponent<CustomMeshColor>(trailer))
                {
                    entityManager.AddBuffer<CustomMeshColor>(trailer);
                }

                DynamicBuffer<CustomMeshColor> buffer = entityManager.GetBuffer<CustomMeshColor>(trailer);
                buffer.ResizeUninitialized(1);
                buffer[0] = new CustomMeshColor { m_ColorSet = state.m_ColorSet };
                entityManager.SetComponentEnabled<CustomMeshColor>(trailer, true);
            }
            else if (entityManager.HasComponent<CustomMeshColor>(trailer))
            {
                // front reset to line colour -> drop the trailer's override too
                entityManager.SetComponentEnabled<CustomMeshColor>(trailer, false);
            }

            /* Refresh via the EndFrameBarrier: adding BatchesUpdated directly here (Rendering phase) lands too
               late and only refreshes on hover; the barrier applies it for next frame's colour + batch upload */
            commandBuffer.AddComponent<BatchesUpdated>(trailer);
        }

        /* True if the trailer prefab's livery is Brand-sourced (cached per prefab so the scan runs once) */
        private bool TrailerIsBrand(EntityManager entityManager, Entity trailer)
        {
            if (!entityManager.HasComponent<PrefabRef>(trailer))
            {
                return false;
            }

            Entity trailerPrefab = entityManager.GetComponentData<PrefabRef>(trailer).m_Prefab;
            if (m_TrailerPrefabIsBrand.TryGetValue(trailerPrefab, out bool isBrand))
            {
                return isBrand;
            }

            isBrand = false;
            if (entityManager.HasBuffer<SubMesh>(trailerPrefab))
            {
                DynamicBuffer<SubMesh> subMeshes = entityManager.GetBuffer<SubMesh>(trailerPrefab);
                for (int i = 0; i < subMeshes.Length && !isBrand; i++)
                {
                    Entity renderPrefab = subMeshes[i].m_SubMesh;
                    if (!entityManager.HasBuffer<ColorVariation>(renderPrefab))
                    {
                        continue;
                    }

                    DynamicBuffer<ColorVariation> variations = entityManager.GetBuffer<ColorVariation>(renderPrefab);
                    for (int j = 0; j < variations.Length; j++)
                    {
                        if (variations[j].m_ColorSourceType == ColorSourceType.Brand && variations[j].hasExternalChannels)
                        {
                            isBrand = true;
                            break;
                        }
                    }
                }
            }

            m_TrailerPrefabIsBrand[trailerPrefab] = isBrand;
            return isBrand;
        }

        /* Compares the three colour channels of two colour sets */
        private static bool ColorSetEquals(ColorSet a, ColorSet b)
        {
            return a.m_Channel0 == b.m_Channel0 && a.m_Channel1 == b.m_Channel1 && a.m_Channel2 == b.m_Channel2;
        }
    }
}
