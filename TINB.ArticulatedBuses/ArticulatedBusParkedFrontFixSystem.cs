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
    /* TEMPORARY FIX (1.0.1 → 1.0.2 only; remove in a later version once players' 1.0.1 saves are healed).
       Published 1.0.1 deleted an articulated bus's trailer while the bus was parked, leaving a trailerless
       articulated FRONT bay-parked at the depot with a [front]-only LayoutElement buffer. Upgrading/extending a
       depot that owns such a bus crashes to desktop: Game.Vehicles.ParkedVehiclesSystem.DuplicateVehiclesJob
       (ModificationBarrier4B) re-creates every depot-owned PARKED vehicle's LayoutElement layout when a depot is
       modified, and our articulated front is the only depot-owned bay-parked vehicle that carries a LayoutElement
       buffer — that never-exercised path crashes on ECB playback.

       The minimal, non-destabilising neutraliser: just REMOVE the LayoutElement buffer from each leftover parked
       front. With no layout buffer the front is indistinguishable from a vanilla single bus, so DuplicateVehiclesJob
       treats it as a plain car (no layout duplication) and the depot upgrade is safe. We deliberately do NOT touch
       ParkedCar, do NOT flag any depot lane Updated, and do NOT spawn a trailer here — earlier attempts that
       force-garaged in place (removing ParkedCar + flagging the static depot surface lanes) destabilised the
       simulation and crashed seconds after un-pausing. The trailer is re-attached later, naturally, by
       ArticulatedBusTrailerRestoreSystem once the bus deploys (it detects articulated fronts missing a trailer by
       PREFAB, so a layout-less front is still healed). That deploy → trailer → return → garage cycle is the
       user-confirmed-safe path.

       One-shot: only the leftover 1.0.1 parked fronts ever match (inflation keeps healthy buses from bay-parking),
       and once their buffers are stripped they no longer match. We also latch m_Done so the fix can never interfere
       again later in the session. */
    public sealed partial class ArticulatedBusParkedFrontFixSystem : GameSystemBase
    {
        private EntityQuery m_ParkedFrontQuery;
        private bool m_Done;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_ParkedFrontQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<VehiclePublicTransport>(),
                    ComponentType.ReadOnly<ParkedCar>(),
                    ComponentType.ReadOnly<LayoutElement>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<CarTrailer>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            RequireForUpdate(m_ParkedFrontQuery);
        }

        protected override void OnUpdate()
        {
            if (m_Done || !Mod.ShouldRunRuntimeSystems())
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            NativeArray<Entity> fronts = m_ParkedFrontQuery.ToEntityArray(Allocator.Temp);
            bool diagnosticLogging = Mod.IsDiagnosticLoggingEnabled();

            try
            {
                for (int i = 0; i < fronts.Length; i++)
                {
                    Entity front = fronts[i];

                    /* Only our articulated buses, and only the trailerless leftovers (never orphan a live trailer
                       by stripping the layout out from under it — 1.0.1 leftovers have no trailer anyway) */
                    if (!IsArticulatedBusFront(entityManager, front) || HasLiveTrailer(entityManager, front))
                    {
                        continue;
                    }

                    entityManager.RemoveComponent<LayoutElement>(front);

                    if (diagnosticLogging)
                    {
                        Mod.Log.InfoFormat("Fixed leftover 1.0.1 parked articulated bus front {0} (stripped trailerless layout so a depot upgrade can't crash); trailer re-attaches on next deploy", front);
                    }
                }
            }
            finally
            {
                fronts.Dispose();
            }

            /* RequireForUpdate guarantees this ran only because leftovers existed; one pass heals them all */
            m_Done = true;
        }

        /* True if the front's prefab declares a fixed trailer (i.e. it is one of our articulated buses) */
        private static bool IsArticulatedBusFront(EntityManager em, Entity front)
        {
            Entity frontPrefab = em.GetComponentData<PrefabRef>(front).m_Prefab;
            return em.HasComponent<CarTractorData>(frontPrefab) &&
                   em.GetComponentData<CarTractorData>(frontPrefab).m_FixedTrailer != Entity.Null;
        }

        /* True if the front's layout still contains a live trailer member (other than the front itself) */
        private static bool HasLiveTrailer(EntityManager em, Entity front)
        {
            if (!em.HasBuffer<LayoutElement>(front))
            {
                return false;
            }

            DynamicBuffer<LayoutElement> layout = em.GetBuffer<LayoutElement>(front);
            for (int i = 0; i < layout.Length; i++)
            {
                Entity vehicle = layout[i].m_Vehicle;
                if (vehicle != Entity.Null && vehicle != front && em.Exists(vehicle))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
