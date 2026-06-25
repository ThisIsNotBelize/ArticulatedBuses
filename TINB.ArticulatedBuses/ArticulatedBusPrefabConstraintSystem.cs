using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TINB.ArticulatedBuses
{
    /* Runs in PrefabUpdate (after the vanilla VehicleInitializeSystem), once per bus prefab: fills in the
       trailer's reverse CarTrailerData.m_FixedTractor link, copies the front's colour/livery onto the trailer,
       and copies the trailer's boarding/alighting doors onto the front (so passengers use them) */
    public sealed partial class ArticulatedBusPrefabConstraintSystem : GameSystemBase
    {
        /* "Already handled" sets, so each repair/diagnostic fires once per prefab */
        private readonly HashSet<Entity> m_WarnedMismatches = new HashSet<Entity>();
        private readonly HashSet<Entity> m_WarnedNonFixedBuses = new HashSet<Entity>();
        private readonly HashSet<Entity> m_WarnedNonFixedTrailers = new HashSet<Entity>();
        private readonly HashSet<Entity> m_RuntimeLinkedTrailers = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ColorSyncedTrailers = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ActivityLocationSyncedBuses = new HashSet<Entity>();
        private readonly HashSet<Entity> m_InflatedFronts = new HashSet<Entity>();

        private EntityQuery m_BusPrefabQuery;

        /* Query: bus front prefabs (car + public-transport + tractor data) */
        protected override void OnCreate()
        {
            base.OnCreate();

            m_BusPrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabData>(),
                    ComponentType.ReadOnly<CarData>(),
                    ComponentType.ReadOnly<PublicTransportVehicleData>(),
                    ComponentType.ReadOnly<CarTractorData>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<CarTrailerData>(),
                    ComponentType.ReadOnly<Deleted>()
                }
            });

            RequireForUpdate(m_BusPrefabQuery);
        }

        /* Applies the constraints to each matching bus prefab 
         */
        protected override void OnUpdate()
        {
            if (!Mod.ShouldRunRuntimeSystems())
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            NativeArray<Entity> busPrefabs = m_BusPrefabQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < busPrefabs.Length; i++)
                {
                    ConstrainFixedTrailer(entityManager, busPrefabs[i], Mod.IsDiagnosticLoggingEnabled());
                }
            }
            finally
            {
                busPrefabs.Dispose();
            }
        }

        /* Syncs the trailer's colours to the front and fills the reverse m_FixedTractor link (runtime-only; the
           saved asset leaves it empty, since a serialized circular front<->trailer ref crashes on load) */
        private void ConstrainFixedTrailer(EntityManager entityManager, Entity busPrefab, bool diagnosticLogging)
        {
            CarTractorData tractorData = entityManager.GetComponentData<CarTractorData>(busPrefab);
            Entity trailerPrefab = tractorData.m_FixedTrailer;
            if (trailerPrefab == Entity.Null)
            {
                return;
            }

            if (tractorData.m_TrailerType != CarTrailerType.Fixed)
            {
                if (diagnosticLogging && m_WarnedNonFixedBuses.Add(busPrefab))
                {
                    Mod.Log.WarnFormat("Bus prefab {0} uses fixed trailer prefab {1}, but tractor trailer type is {2} instead of Fixed", busPrefab, trailerPrefab, tractorData.m_TrailerType);
                }
                return;
            }

            if (!entityManager.HasComponent<CarTrailerData>(trailerPrefab))
            {
                return;
            }

            CarTrailerData trailerData = entityManager.GetComponentData<CarTrailerData>(trailerPrefab);
            if (trailerData.m_TrailerType != CarTrailerType.Fixed)
            {
                if (diagnosticLogging && m_WarnedNonFixedTrailers.Add(trailerPrefab))
                {
                    Mod.Log.WarnFormat("Bus prefab {0} fixed trailer {1} uses trailer type {2} instead of Fixed", busPrefab, trailerPrefab, trailerData.m_TrailerType);
                }
                return;
            }

            SyncTrailerColors(entityManager, busPrefab, trailerPrefab, diagnosticLogging);
            SyncTrailerActivityLocations(entityManager, busPrefab, trailerPrefab, tractorData, trailerData, diagnosticLogging);
            InflateFrontParkingLength(entityManager, busPrefab, trailerPrefab, tractorData, trailerData, diagnosticLogging);

            if (trailerData.m_FixedTractor == busPrefab)
            {
                return;
            }

            if (trailerData.m_FixedTractor != Entity.Null)
            {
                if (diagnosticLogging && m_WarnedMismatches.Add(trailerPrefab))
                {
                    Mod.Log.WarnFormat("Bus prefab {0} fixed trailer {1} already points to tractor {2}", busPrefab, trailerPrefab, trailerData.m_FixedTractor);
                }

                return;
            }

            trailerData.m_FixedTractor = busPrefab;
            entityManager.SetComponentData(trailerPrefab, trailerData);

            if (diagnosticLogging && m_RuntimeLinkedTrailers.Add(trailerPrefab))
            {
                Mod.Log.InfoFormat("Applied runtime-only articulated bus reverse link from trailer prefab {0} to bus prefab {1}", trailerPrefab, busPrefab);
            }
        }

        /* Extends the front prefab's logical length (ObjectGeometryData.m_Bounds along z) to cover the WHOLE
           assembled bus (front + trailer) — computed from both prefabs' geometry bounds and their attach offsets,
           so it is correct for any creator's asset dimensions. With the front reported this long, the return
           pathfind rejects every single-bus depot bay (bay m_MaxCarLength < full bus length) and the bus always
           GARAGES instead of surface-parking, so the trailer never overhangs the driveway and is never bay-parked.
           Only m_Bounds is read by GetParkingSize; extending it changes the culling/collision/parking footprint
           only, NOT the visible mesh (so the bus still looks and selects normally). */
        private void InflateFrontParkingLength(EntityManager entityManager, Entity busPrefab, Entity trailerPrefab, CarTractorData tractorData, CarTrailerData trailerData, bool diagnosticLogging)
        {
            if (!m_InflatedFronts.Add(busPrefab) ||
                !entityManager.HasComponent<ObjectGeometryData>(busPrefab) ||
                !entityManager.HasComponent<ObjectGeometryData>(trailerPrefab))
            {
                return;
            }

            ObjectGeometryData frontGeometry = entityManager.GetComponentData<ObjectGeometryData>(busPrefab);
            ObjectGeometryData trailerGeometry = entityManager.GetComponentData<ObjectGeometryData>(trailerPrefab);

            /* When assembled straight, the trailer origin sits at (front attach - trailer attach) relative to the
               front, so shift the trailer's z-bounds by that offset and union with the front's z-bounds */
            float trailerOffsetZ = tractorData.m_AttachPosition.z - trailerData.m_AttachPosition.z;
            float combinedMinZ = math.min(frontGeometry.m_Bounds.min.z, trailerOffsetZ + trailerGeometry.m_Bounds.min.z);
            float combinedMaxZ = math.max(frontGeometry.m_Bounds.max.z, trailerOffsetZ + trailerGeometry.m_Bounds.max.z);

            float currentLength = frontGeometry.m_Bounds.max.z - frontGeometry.m_Bounds.min.z;
            float combinedLength = combinedMaxZ - combinedMinZ;
            if (combinedLength <= currentLength)
            {
                return; // front bounds already span the whole bus (or unexpected geometry) -> nothing to do
            }

            frontGeometry.m_Bounds.min.z = combinedMinZ;
            frontGeometry.m_Bounds.max.z = combinedMaxZ;
            frontGeometry.m_Size.z = combinedLength;
            entityManager.SetComponentData(busPrefab, frontGeometry);

            if (diagnosticLogging)
            {
                Mod.Log.InfoFormat("Inflated front {0} parking length {1:F2}m -> {2:F2}m (full bus, force depot garaging)", busPrefab, currentLength, combinedLength);
            }
        }

        /* Copies the front's ColorVariation set onto the trailer's render prefabs so both share one livery; runs
           once per trailer prefab, and leaves non-Brand trailers (or trailers without ColorProperties) alone */
        private void SyncTrailerColors(EntityManager entityManager, Entity busPrefab, Entity trailerPrefab, bool diagnosticLogging)
        {
            if (!m_ColorSyncedTrailers.Add(trailerPrefab))
            {
                return;
            }

            if (!entityManager.HasBuffer<SubMesh>(busPrefab) || !entityManager.HasBuffer<SubMesh>(trailerPrefab))
            {
                return;
            }

            // Source = the front's first Brand-sourced ColorVariation set (its primary livery)
            NativeArray<ColorVariation> source = default(NativeArray<ColorVariation>);
            DynamicBuffer<SubMesh> busSubMeshes = entityManager.GetBuffer<SubMesh>(busPrefab);
            for (int i = 0; i < busSubMeshes.Length; i++)
            {
                Entity renderPrefab = busSubMeshes[i].m_SubMesh;
                if (entityManager.HasBuffer<ColorVariation>(renderPrefab))
                {
                    DynamicBuffer<ColorVariation> variations = entityManager.GetBuffer<ColorVariation>(renderPrefab);
                    if (variations.Length > 0 && IsBrandSourced(variations))
                    {
                        source = variations.ToNativeArray(Allocator.Temp);
                        break;
                    }
                }
            }

            if (!source.IsCreated)
            {
                return;
            }

            int syncedMeshes = 0;
            DynamicBuffer<SubMesh> trailerSubMeshes = entityManager.GetBuffer<SubMesh>(trailerPrefab);
            for (int i = 0; i < trailerSubMeshes.Length; i++)
            {
                Entity renderPrefab = trailerSubMeshes[i].m_SubMesh;
                if (!entityManager.HasBuffer<ColorVariation>(renderPrefab))
                {
                    continue;
                }

                DynamicBuffer<ColorVariation> variations = entityManager.GetBuffer<ColorVariation>(renderPrefab);
                if (variations.Length == 0 || !IsBrandSourced(variations))
                {
                    continue; // no ColorProperties / not Brand -> leave independent
                }

                variations.Clear();
                variations.AddRange(source);
                syncedMeshes++;
            }

            source.Dispose();

            if (diagnosticLogging && syncedMeshes > 0)
            {
                Mod.Log.InfoFormat("Synced {0} trailer render-prefab color variation set(s) on {1} to bus prefab {2}", syncedMeshes, trailerPrefab, busPrefab);
            }
        }

        /* Copies the trailer's boarding/alighting (Enter/Exit) activity locations onto the FRONT prefab, offset
           to the trailer's straight-behind position. The game reads a passenger's door position only from the
           boarded vehicle's prefab (the front), so without this the trailer's own doors are never used. The
           copied doors sit in the front's rest space, so they line up exactly when the bus is straight (the
           usual case at an in-lane stop) and only drift on a curved stop. Runs once per bus prefab */
        private void SyncTrailerActivityLocations(EntityManager entityManager, Entity busPrefab, Entity trailerPrefab, CarTractorData tractorData, CarTrailerData trailerData, bool diagnosticLogging)
        {
            if (!m_ActivityLocationSyncedBuses.Add(busPrefab))
            {
                return;
            }

            if (!entityManager.HasBuffer<ActivityLocationElement>(trailerPrefab) ||
                entityManager.GetBuffer<ActivityLocationElement>(trailerPrefab).Length == 0)
            {
                return;
            }

            /* Trailer origin in the front's local space when straight: the two sections share a rotation, so the
               offset is simply the front attach point minus the trailer attach point */
            float3 trailerOriginInFront = tractorData.m_AttachPosition - trailerData.m_AttachPosition;
            uint boardingMask = new ActivityMask(ActivityType.Enter).m_Mask | new ActivityMask(ActivityType.Exit).m_Mask;

            // ensure the front buffer exists BEFORE taking buffer handles (AddBuffer is a structural change)
            if (!entityManager.HasBuffer<ActivityLocationElement>(busPrefab))
            {
                entityManager.AddBuffer<ActivityLocationElement>(busPrefab);
            }

            DynamicBuffer<ActivityLocationElement> trailerLocations = entityManager.GetBuffer<ActivityLocationElement>(trailerPrefab);
            DynamicBuffer<ActivityLocationElement> frontLocations = entityManager.GetBuffer<ActivityLocationElement>(busPrefab);

            int copied = 0;
            for (int i = 0; i < trailerLocations.Length; i++)
            {
                ActivityLocationElement location = trailerLocations[i];
                if ((location.m_ActivityMask.m_Mask & boardingMask) == 0)
                {
                    continue; // only boarding/alighting doors; skip driver/other slots
                }

                location.m_Position += trailerOriginInFront;
                frontLocations.Add(location);
                copied++;
            }

            if (diagnosticLogging && copied > 0)
            {
                Mod.Log.InfoFormat("Copied {0} trailer boarding/alighting location(s) from {1} onto bus prefab {2}", copied, trailerPrefab, busPrefab);
            }
        }

        /* True if any variation is Brand-sourced with external channels (i.e. eligible for livery syncing)
         */
        private static bool IsBrandSourced(DynamicBuffer<ColorVariation> variations)
        {
            for (int i = 0; i < variations.Length; i++)
            {
                if (variations[i].m_ColorSourceType == ColorSourceType.Brand && variations[i].hasExternalChannels)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
