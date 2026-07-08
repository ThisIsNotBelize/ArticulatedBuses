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
    /// Legacy fix for leftover trailerless articulated bus fronts
    /// </summary>
    /// <remarks>
    /// Temporary fix (1.0.1 to 1.0.2 only, will remain until any new release after 31 July 2026) so a depot upgrade can't crash to desktop; remove in a later version once
    /// players' 1.0.1 saves are healed
    /// Published 1.0.1 deleted an articulated bus's trailer while the bus was parked, leaving a trailerless articulated
    /// FRONT bay-parked at the depot with a [front]-only LayoutElement buffer. Upgrading/extending a depot that owns
    /// such a bus crashes to desktop: Game.Vehicles.ParkedVehiclesSystem.DuplicateVehiclesJob (ModificationBarrier4B)
    /// re-creates every depot-owned PARKED vehicle's LayoutElement layout when a depot is modified, and our articulated
    /// front is the only depot-owned bay-parked vehicle that carries a LayoutElement buffer; that never-exercised path
    /// crashes on ECB playback
    /// - Solution: just remove the LayoutElement buffer from each leftover parked front. With no layout buffer the front
    ///   is indistinguishable from a vanilla single bus, so DuplicateVehiclesJob treats it as a plain car (no layout
    ///   duplication) and the depot upgrade is safe
    /// - One-shot: only the leftover 1.0.1 parked fronts ever match (inflation keeps healthy buses from bay-parking),
    ///   and once their buffers are stripped they no longer match. We also latch m_Done so the fix can never interfere
    ///   again later in the session
    /// </remarks>
    public sealed partial class ArticulatedBusParkedFrontFixSystem : GameSystemBase
    {
        private EntityQuery m_ParkedFrontQuery;
        private bool m_Done;

        /// <summary>
        /// Build the query for bay-parked articulated fronts that still carry a layout buffer
        /// </summary>
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

        /// <summary>
        /// Strip the stale layout buffer from each leftover 1.0.1 parked front, then latch off
        /// </summary>
        protected override void OnUpdate()
        {
            if (m_Done || !Mod.IsInGame())
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            NativeArray<Entity> fronts = m_ParkedFrontQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < fronts.Length; i++)
                {
                    Entity front = fronts[i];

                    // Only articulated buses, and only the trailerless leftovers (never orphan a live trailer
                    // by stripping the layout out from under it, 1.0.1 leftovers have no trailer anyway)
                    if (!ArticulatedBusPrefabHelper.IsArticulatedBusFrontEntity(entityManager, front) || ArticulatedBusPrefabHelper.HasSpawnedTrailer(entityManager, front))
                    {
                        continue;
                    }

                    entityManager.RemoveComponent<LayoutElement>(front);
                    SessionLog.Event($"Applied 1.0.1 legacy fix on parked front {front}");
                    SessionLog.Diagnostic($"Fixed leftover 1.0.1 parked articulated bus front {front} (stripped trailerless layout so a depot upgrade can't crash); trailer re-attaches on next deploy");
                }
            }
            catch (System.Exception ex)
            {
                SessionLog.Exception($"{nameof(ArticulatedBusParkedFrontFixSystem)}.OnUpdate", ex);
            }
            finally
            {
                fronts.Dispose();
            }

            // RequireForUpdate guarantees this ran only because leftovers existed; one pass fixes them all
            m_Done = true;
        }

    }
}
