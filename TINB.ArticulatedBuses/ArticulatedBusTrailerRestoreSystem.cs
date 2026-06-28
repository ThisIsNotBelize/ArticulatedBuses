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
    /* Integrity invariant: guarantees that every live, un-parked articulated-bus FRONT has its trailer, re-spawning
       one if it is missing. This is distinct from ArticulatedBusTrailerSpawnSystem, which keys on the Created tag
       and so only ever trailers BRAND-NEW bus fronts the frame they are born — it can never re-process an
       already-existing front that lacks a trailer. Restore covers exactly that gap: an existing front with no
       trailer (e.g. one whose [front]-only layout was stripped by the one-shot ArticulatedBusParkedFrontFixSystem
       while healing a 1.0.1 save, or any other case a trailer goes missing).
       Detection is by PREFAB (CarTractorData.m_FixedTrailer), so it heals a front with no layout buffer at all as
       well as a [front]-only one — TrySpawnTrailer get-or-adds the buffer. Creation-only, so it CANNOT cause a
       trailer-deletion dangling-lane-ref issue; the new trailer inherits the front's Unspawned state (so a garaged
       front gets a garaged trailer), via TrySpawnTrailer.
       IMPORTANT — we deliberately SKIP parked fronts (ParkedCar): restoring a trailer onto a bay-parked front
       would re-create a bay-parked articulated bus, the depot-upgrade CTD condition. By waiting until the bus is
       active (un-parked), the restored trailer joins a moving bus that inflation then garages on its next return,
       so it never bay-parks — keeping an immediate post-load depot upgrade safe. */
    public sealed partial class ArticulatedBusTrailerRestoreSystem : GameSystemBase
    {
        private EntityQuery m_FrontQuery;

        /* Query: live, un-parked public-transport vehicles (filtered to our articulated fronts in code). We
           intentionally do NOT require LayoutElement: a front that lost its trailer may have had its layout buffer
           removed entirely (e.g. by ArticulatedBusParkedFrontFixSystem), and must still be re-trailered on deploy. */
        protected override void OnCreate()
        {
            base.OnCreate();

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
                    ComponentType.ReadOnly<ParkedCar>(),
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            RequireForUpdate(m_FrontQuery);
        }

        protected override void OnUpdate()
        {
            if (!Mod.ShouldRunRuntimeSystems())
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            NativeArray<Entity> fronts = m_FrontQuery.ToEntityArray(Allocator.Temp);
            bool diagnosticLogging = Mod.IsDiagnosticLoggingEnabled();

            try
            {
                for (int i = 0; i < fronts.Length; i++)
                {
                    Entity front = fronts[i];
                    if (!IsArticulatedBusFront(entityManager, front) || HasTrailer(entityManager, front))
                    {
                        continue;
                    }

                    Entity restored = ArticulatedBusTrailerSpawnSystem.TrySpawnTrailer(entityManager, front, diagnosticLogging);
                    if (restored != Entity.Null)
                    {
                        ArticulatedBusSessionStats.TrailerRestored();
                        SessionLog.Event($"restored missing trailer {restored} for articulated bus front {front} (integrity)");

                        if (diagnosticLogging)
                        {
                            Mod.Log.InfoFormat("Restored missing trailer {0} for articulated bus front {1} (integrity)", restored, front);
                        }
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

        /* True if the front's prefab declares a fixed trailer (i.e. it is one of our articulated buses) */
        private static bool IsArticulatedBusFront(EntityManager entityManager, Entity front)
        {
            Entity frontPrefab = entityManager.GetComponentData<PrefabRef>(front).m_Prefab;
            return entityManager.HasComponent<CarTractorData>(frontPrefab) &&
                   entityManager.GetComponentData<CarTractorData>(frontPrefab).m_FixedTrailer != Entity.Null;
        }

        /* True if the front's layout already contains a live trailer. A front whose layout buffer was stripped has
           none, so a missing buffer means "needs a trailer". */
        private static bool HasTrailer(EntityManager entityManager, Entity front)
        {
            if (!entityManager.HasBuffer<LayoutElement>(front))
            {
                return false;
            }

            DynamicBuffer<LayoutElement> layout = entityManager.GetBuffer<LayoutElement>(front);
            for (int i = 1; i < layout.Length; i++)
            {
                Entity vehicle = layout[i].m_Vehicle;
                if (vehicle != Entity.Null && vehicle != front && entityManager.Exists(vehicle))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
