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
    /* Keeps the trailer's colour in sync with the front, front-authoritative:
       - user-painted colour (CustomMeshColor): when the front's paint changes (painted or reset) it is pushed to
         the trailer; a trailer-only edit survives until the front changes again.
       - line colour (vanilla Brand/route path): when the front's CurrentRoute changes (e.g. a returning bus is
         reassigned to a differently-coloured line) we flag the trailer BatchesUpdated so vanilla MeshColorSystem
         re-resolves its line colour through the Controller->front->CurrentRoute hop. (A route's own colour being
         edited is already handled by vanilla's ColorUpdated path, which walks LayoutElement; this covers the
         reassignment case it misses, where the front changes route but no ColorUpdated fires.)
       Only Brand-sourced trailers are synced (others stay independent) */
    public sealed partial class ArticulatedBusColorSyncSystem : GameSystemBase
    {
        /* Snapshot of a front's colour-relevant state, to detect changes between frames */
        private struct FrontColorState
        {
            public bool m_HasCustom;
            public ColorSet m_ColorSet;
            public Entity m_Route;
            public UnityEngine.Color32 m_RouteColor;
        }

        private readonly Dictionary<Entity, FrontColorState> m_LastSyncedFront = new Dictionary<Entity, FrontColorState>();
        private readonly Dictionary<Entity, bool> m_TrailerPrefabIsBrand = new Dictionary<Entity, bool>();
        /* Scratch list for pruning, reused to avoid per-prune allocations */
        private readonly List<Entity> m_PruneScratch = new List<Entity>();

        /* Prune dead bus entries this often, so the per-front dictionary can't grow forever in a long session */
        private const int PruneIntervalFrames = 4096;
        private int m_PruneCountdown = PruneIntervalFrames;

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

            if (--m_PruneCountdown <= 0)
            {
                m_PruneCountdown = PruneIntervalFrames;
                PruneDeadEntries(entityManager);
            }
        }

        /* Drops per-front state for buses that no longer exist (the prefab-keyed cache is bounded and stays) */
        private void PruneDeadEntries(EntityManager entityManager)
        {
            m_PruneScratch.Clear();
            foreach (Entity front in m_LastSyncedFront.Keys)
            {
                if (!entityManager.Exists(front))
                {
                    m_PruneScratch.Add(front);
                }
            }

            for (int i = 0; i < m_PruneScratch.Count; i++)
            {
                m_LastSyncedFront.Remove(m_PruneScratch[i]);
            }

            m_PruneScratch.Clear();
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

            FrontColorState current = ReadFrontColor(entityManager, root);
            bool haveLast = m_LastSyncedFront.TryGetValue(root, out FrontColorState last);

            bool customChanged = !haveLast ||
                last.m_HasCustom != current.m_HasCustom ||
                (current.m_HasCustom && !ColorSetEquals(last.m_ColorSet, current.m_ColorSet));
            bool routeChanged = !haveLast ||
                last.m_Route != current.m_Route ||
                !RouteColorEquals(last.m_RouteColor, current.m_RouteColor);

            if (!customChanged && !routeChanged)
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
                    Mod.Log.Info($"{nameof(ArticulatedBusColorSyncSystem)} diagnostics: front={root} customChanged={customChanged}, routeChanged={routeChanged}, hasCustom={current.m_HasCustom}, frontCh0=({c.r:F3},{c.g:F3},{c.b:F3}), route={current.m_Route}, trailer={trailer}, brand={brand}, trailerHadCustom={entityManager.HasComponent<CustomMeshColor>(trailer) && entityManager.IsComponentEnabled<CustomMeshColor>(trailer)}");
                }

                if (!brand)
                {
                    continue;
                }

                if (customChanged)
                {
                    // rewrites/clears the trailer's custom override and flags BatchesUpdated
                    ApplyCustomColor(entityManager, commandBuffer, trailer, current);
                }
                else
                {
                    // line colour (route) changed only: trigger a vanilla recompute so the trailer re-resolves the
                    // front's new line colour via the Controller hop (no custom edit to push)
                    commandBuffer.AddComponent<BatchesUpdated>(trailer);
                }
            }

            m_LastSyncedFront[root] = current;
        }

        /* Reads a front's current colour-relevant state: its custom-paint override (if enabled), and the line
           colour source it currently resolves to (its CurrentRoute and that route's colour). The route fields let
           us detect a mid-return line reassignment, which changes the front's line colour without firing vanilla's
           route-side ColorUpdated path that would otherwise refresh the trailer. */
        private static FrontColorState ReadFrontColor(EntityManager entityManager, Entity entity)
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

            if (entityManager.HasComponent<Game.Routes.CurrentRoute>(entity))
            {
                Entity route = entityManager.GetComponentData<Game.Routes.CurrentRoute>(entity).m_Route;
                state.m_Route = route;
                if (route != Entity.Null && entityManager.HasComponent<Game.Routes.Color>(route))
                {
                    state.m_RouteColor = entityManager.GetComponentData<Game.Routes.Color>(route).m_Color;
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

        /* Compares two route colours (Color32 has no value-equality operator) */
        private static bool RouteColorEquals(UnityEngine.Color32 a, UnityEngine.Color32 b)
        {
            return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
        }
    }
}
