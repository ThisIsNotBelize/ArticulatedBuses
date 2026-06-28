using System;
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
    /* Low-rate, always-on runtime invariant checker. It watches for the states that precede the known crashes and
       writes any violation to the user SessionLog, so a player who is about to (or just did) hit a CTD leaves us a
       record even without the developer diagnostics build.

       SAFETY CONTRACT (do not weaken): this system reads ONLY cold / structural data and component-TAG presence —
       LayoutElement (buffer), Controller, PrefabRef, and HasComponent<ParkedCar/Unspawned/Deleted>. It must NEVER
       read the value of a hot, Burst-written component (CarTrailerLane, CarCurrentLane, BlockedLane, ParkingLane,
       InterpolatedTransform). Reading those on the main thread is the very data-race we are hunting; tag-presence
       checks and LayoutElement/Controller reads don't touch per-frame job output, so this stays safe.

       Volume is kept tiny: the scan runs once every ~ScanIntervalFrames sim frames, and a result line is written
       only when the "danger signature" changes (so a steady clean city is silent after one OK line), plus a rare
       heartbeat. */
    public sealed partial class ArticulatedBusSelfCheckSystem : GameSystemBase
    {
        private const int ScanIntervalFrames = 512; // ~8.5s at 60 fps; low-rate
        private const int HeartbeatEveryScans = 32;  // ~ every 4–5 min, emit a stats heartbeat

        private EntityQuery m_FrontQuery;
        private EntityQuery m_TrailerQuery;

        private int m_Countdown;
        private int m_ScanCount;
        private string m_LastDangerSignature = string.Empty;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Live public-transport car fronts (filtered to our articulated fronts in code).
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
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            // Trailer instances (filtered to our articulated trailers in code).
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

            RequireForUpdate(m_FrontQuery);
        }

        protected override void OnUpdate()
        {
            if (!Mod.ShouldRunRuntimeSystems())
            {
                return;
            }

            if (--m_Countdown > 0)
            {
                return;
            }
            m_Countdown = ScanIntervalFrames;

            try
            {
                Scan();
            }
            catch (Exception ex)
            {
                SessionLog.Exception($"{nameof(ArticulatedBusSelfCheckSystem)}.Scan", ex);
            }
        }

        private void Scan()
        {
            EntityManager em = EntityManager;
            m_ScanCount++;

            int articFronts = 0;
            int preconditionFronts = 0;   // ParkedCar + LayoutElement: the depot-upgrade DuplicateVehiclesJob CTD precondition
            int frontsMissingTrailer = 0; // live, un-parked artic front with no live trailer (transient/expected for garaged)
            int frontsMultipleTrailers = 0; // >1 live trailer in a front's layout (a real bug)
            Entity samplePrecondition = Entity.Null;
            Entity sampleMultiple = Entity.Null;

            NativeArray<Entity> fronts = m_FrontQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < fronts.Length; i++)
                {
                    Entity front = fronts[i];
                    if (!IsArticulatedBusFront(em, front))
                    {
                        continue;
                    }

                    articFronts++;
                    bool parked = em.HasComponent<ParkedCar>(front);
                    bool hasLayout = em.HasBuffer<LayoutElement>(front);

                    if (parked && hasLayout)
                    {
                        preconditionFronts++;
                        if (samplePrecondition == Entity.Null)
                        {
                            samplePrecondition = front;
                        }
                    }

                    int liveTrailers = CountLiveTrailers(em, front);
                    if (liveTrailers == 0 && !parked)
                    {
                        frontsMissingTrailer++;
                    }
                    else if (liveTrailers > 1)
                    {
                        frontsMultipleTrailers++;
                        if (sampleMultiple == Entity.Null)
                        {
                            sampleMultiple = front;
                        }
                    }
                }
            }
            finally
            {
                fronts.Dispose();
            }

            int orphanTrailers = 0;
            Entity sampleOrphan = Entity.Null;
            NativeArray<Entity> trailers = m_TrailerQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < trailers.Length; i++)
                {
                    Entity trailer = trailers[i];
                    if (IsOrphaned(em, trailer) && IsArticulatedBusTrailer(em, trailer))
                    {
                        orphanTrailers++;
                        if (sampleOrphan == Entity.Null)
                        {
                            sampleOrphan = trailer;
                        }
                    }
                }
            }
            finally
            {
                trailers.Dispose();
            }

            /* Only the three states that should be impossible in a healthy save make up the danger signature; the
               benign churn (artic count, garaged fronts missing a trailer) is excluded so a clean city stays silent. */
            string dangerSignature = $"{preconditionFronts}|{orphanTrailers}|{frontsMultipleTrailers}";
            if (dangerSignature != m_LastDangerSignature)
            {
                m_LastDangerSignature = dangerSignature;

                if (preconditionFronts == 0 && orphanTrailers == 0 && frontsMultipleTrailers == 0)
                {
                    SessionLog.Event($"self-check OK ({articFronts} articulated fronts; all invariants hold)");
                }
                else
                {
                    ArticulatedBusSessionStats.SelfCheckViolation();
                    SessionLog.Warn(
                        "self-check VIOLATION — " +
                        $"depotUpgradeCtdPrecondition(ParkedCar+LayoutElement)={preconditionFronts} (e.g. {samplePrecondition}), " +
                        $"standingOrphanTrailers={orphanTrailers} (e.g. {sampleOrphan}), " +
                        $"frontsWithMultipleTrailers={frontsMultipleTrailers} (e.g. {sampleMultiple}); " +
                        $"articFronts={articFronts}, frontsMissingTrailer={frontsMissingTrailer}");
                }
            }

            if (m_ScanCount % HeartbeatEveryScans == 0)
            {
                SessionLog.Event($"heartbeat: articFronts={articFronts}, frontsMissingTrailer={frontsMissingTrailer}; {ArticulatedBusSessionStats.Summary()}");
            }
        }

        /* Live trailers (other than the front) referenced in the front's layout buffer. Reads LayoutElement only —
           no hot component touched. */
        private static int CountLiveTrailers(EntityManager em, Entity front)
        {
            if (!em.HasBuffer<LayoutElement>(front))
            {
                return 0;
            }

            DynamicBuffer<LayoutElement> layout = em.GetBuffer<LayoutElement>(front);
            int count = 0;
            for (int i = 0; i < layout.Length; i++)
            {
                Entity vehicle = layout[i].m_Vehicle;
                if (vehicle != Entity.Null && vehicle != front && em.Exists(vehicle) && !em.HasComponent<Deleted>(vehicle))
                {
                    count++;
                }
            }

            return count;
        }

        /* True when the trailer's controlling front is null, already gone, or being deleted. */
        private static bool IsOrphaned(EntityManager em, Entity trailer)
        {
            Entity front = em.GetComponentData<Controller>(trailer).m_Controller;
            return front == Entity.Null || !em.Exists(front) || em.HasComponent<Deleted>(front);
        }

        /* True if the front's prefab declares a fixed trailer (i.e. it is one of our articulated buses). */
        private static bool IsArticulatedBusFront(EntityManager em, Entity front)
        {
            Entity frontPrefab = em.GetComponentData<PrefabRef>(front).m_Prefab;
            return em.HasComponent<CarTractorData>(frontPrefab) &&
                   em.GetComponentData<CarTractorData>(frontPrefab).m_FixedTrailer != Entity.Null;
        }

        /* True only for our trailers: a Fixed trailer whose repaired fixed tractor is a public-transport bus prefab
           (mirrors ArticulatedBusOrphanTrailerCleanupSystem's guard so we never count vanilla/other trailers). */
        private static bool IsArticulatedBusTrailer(EntityManager em, Entity trailer)
        {
            Entity trailerPrefab = em.GetComponentData<PrefabRef>(trailer).m_Prefab;
            if (!em.HasComponent<CarTrailerData>(trailerPrefab))
            {
                return false;
            }

            CarTrailerData trailerData = em.GetComponentData<CarTrailerData>(trailerPrefab);
            return trailerData.m_TrailerType == CarTrailerType.Fixed &&
                   trailerData.m_FixedTractor != Entity.Null &&
                   em.HasComponent<PublicTransportVehicleData>(trailerData.m_FixedTractor);
        }
    }
}
