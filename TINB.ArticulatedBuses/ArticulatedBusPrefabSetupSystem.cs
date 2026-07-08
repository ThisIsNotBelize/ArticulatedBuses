using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Sets up articulated bus prefabs
    /// </summary>
    public sealed partial class ArticulatedBusPrefabSetupSystem : GameSystemBase
    {
        /// <summary>
        /// Sets for already handled prefabs
        /// </summary>
        private readonly HashSet<Entity> m_WarnedMismatches = new HashSet<Entity>();
        private readonly HashSet<Entity> m_WarnedNonFixedBuses = new HashSet<Entity>();
        private readonly HashSet<Entity> m_WarnedNonFixedTrailers = new HashSet<Entity>();
        private readonly HashSet<Entity> m_RuntimeLinkedTrailers = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ColorSyncedTrailers = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ActivityLocationSyncedBuses = new HashSet<Entity>();

        /// <summary>
        /// Original prefab z-geometry storage
        /// </summary>
        private struct OriginalGeometryZ
        {
            public float m_MinZ;
            public float m_MaxZ;
            public float m_SizeZ;
        }

        private readonly Dictionary<Entity, OriginalGeometryZ> m_InflatedFronts = new Dictionary<Entity, OriginalGeometryZ>();

        /// <summary>
        /// Original ActivityLocationElement count per front prefab
        /// </summary>
        /// <remarks>
        /// So leaving the game can drop the copied trailer doors
        /// </remarks>
        private readonly Dictionary<Entity, int> m_SyncedActivityFronts = new Dictionary<Entity, int>();

        /// <summary>
        /// Cached bus-prefab query and PrefabSystem
        /// </summary>
        /// <remarks>
        /// PrefabSystem is used for prefab-name lookups
        /// </remarks>
        private EntityQuery m_BusPrefabQuery;
        private PrefabSystem m_PrefabSystem = null!;

        /// <summary>
        /// Get the query for articulated bus prefabs
        /// </summary>
        /// <remarks>
        /// Assumed articulated buses are a car prefab plus public-transport component plus car tractor component
        /// </remarks>
        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // construct query
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

            // run OnUpdate when matching prefabs
            RequireForUpdate(m_BusPrefabQuery);
        }

        /// <summary>
        /// Set up bus prefabs
        /// </summary>
        protected override void OnUpdate()
        {
            if (!Mod.IsInGame())
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            NativeArray<Entity> busPrefabs = m_BusPrefabQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < busPrefabs.Length; i++)
                {
                    SetUpBusPrefab(entityManager, busPrefabs[i]);
                }
            }
            finally
            {
                busPrefabs.Dispose();
            }
        }

        /// <summary>
        /// Link a trailer to its bus front prefab
        /// </summary>
        /// <remarks>
        /// Runtime-only to preserve vanilla-compatibility of prefabs; also a serialized circular front-trailer ref
        /// crashes on load
        /// </remarks>
        private void SetUpBusPrefab(EntityManager entityManager, Entity busPrefab)
        {
            // Get trailer from tractor component
            CarTractorData tractorData = entityManager.GetComponentData<CarTractorData>(busPrefab);
            Entity trailerPrefab = tractorData.m_FixedTrailer;
            if (trailerPrefab == Entity.Null)
            {
                return;
            }

            // Only if fixed trailer set
            if (tractorData.m_TrailerType != CarTrailerType.Fixed)
            {
                if (m_WarnedNonFixedBuses.Add(busPrefab))
                {
                    SessionLog.DiagnosticWarn($"Bus prefab {busPrefab} uses fixed trailer prefab {trailerPrefab}, but tractor trailer type is {tractorData.m_TrailerType} instead of Fixed");
                }
                return;
            }

            // Only if trailer is a proper car trailer prefab
            if (!entityManager.HasComponent<CarTrailerData>(trailerPrefab))
            {
                return;
            }

            // Only if trailer set to fixed
            CarTrailerData trailerData = entityManager.GetComponentData<CarTrailerData>(trailerPrefab);
            if (trailerData.m_TrailerType != CarTrailerType.Fixed)
            {
                if (m_WarnedNonFixedTrailers.Add(trailerPrefab))
                {
                    SessionLog.DiagnosticWarn($"Bus prefab {busPrefab} fixed trailer {trailerPrefab} uses trailer type {trailerData.m_TrailerType} instead of Fixed");
                }
                return;
            }

            // Already reverse-linked to this front on an earlier pass, so skip
            if (trailerData.m_FixedTractor == busPrefab)
            {
                return;
            }

            // Only allow unique front-trailer combinations. (avoid oubling of one trailer to multiple front prefabs as this leads to one front missing out its trailer)
            if (trailerData.m_FixedTractor != Entity.Null)
            {
                // Add to warning set and log
                if (m_WarnedMismatches.Add(trailerPrefab))
                {
                    SessionLog.Warn(
                        $"trailer prefab '{ArticulatedBusPrefabHelper.GetPrefabName(m_PrefabSystem, trailerPrefab)}' is shared by multiple bus fronts " +
                        $"('{ArticulatedBusPrefabHelper.GetPrefabName(m_PrefabSystem, trailerData.m_FixedTractor)}' owns it; '{ArticulatedBusPrefabHelper.GetPrefabName(m_PrefabSystem, busPrefab)}' also points to it). " +
                        "Each front needs its unique trailer prefab, or the extra front(s) will run with no trailer");

                    SessionLog.DiagnosticWarn($"Bus prefab {busPrefab} fixed trailer {trailerPrefab} already points to tractor {trailerData.m_FixedTractor} (shared trailer prefab; {busPrefab} will run trailerless)");
                }

                return;
            }

            // Copy the front's color variations onto the trailer prefab
            CopyColorVariations(entityManager, busPrefab, trailerPrefab);
            // Expand activity locations from trailer to front (otherwise the game will ignore those on trailers)
            SyncTrailerActivityLocations(entityManager, busPrefab, trailerPrefab, tractorData, trailerData);
            // Caclulate (inflate) the front prefabs geometry bounds, so it will not use too small parkings, e.g. at the depot
            InflateFrontParkingLength(entityManager, busPrefab, trailerPrefab, tractorData, trailerData);

            // Set the runtime-only reverse link (trailer prefab -> front)
            trailerData.m_FixedTractor = busPrefab;
            entityManager.SetComponentData(trailerPrefab, trailerData);

            if (m_RuntimeLinkedTrailers.Add(trailerPrefab))
            {
                SessionLog.Diagnostic($"Applied runtime-only articulated bus reverse link from trailer prefab {trailerPrefab} to bus prefab {busPrefab}");
            }
        }

        /// <summary>
        /// Inflate the front prefab's geometry bounds
        /// </summary>
        /// <remarks>
        /// Stretches the front's parking length to span the whole bus, so it garages at a depot instead of taking an
        /// undersized bay
        /// </remarks>
        private void InflateFrontParkingLength(EntityManager entityManager, Entity busPrefab, Entity trailerPrefab, CarTractorData tractorData, CarTrailerData trailerData)
        {
            // Avoid duplication
            if (m_InflatedFronts.ContainsKey(busPrefab) ||
                !entityManager.HasComponent<ObjectGeometryData>(busPrefab) ||
                !entityManager.HasComponent<ObjectGeometryData>(trailerPrefab))
            {
                return;
            }

            // Get geometries
            ObjectGeometryData frontGeometry = entityManager.GetComponentData<ObjectGeometryData>(busPrefab);
            ObjectGeometryData trailerGeometry = entityManager.GetComponentData<ObjectGeometryData>(trailerPrefab);

            // The trailer origin sits at (front attach - trailer attach) relative to the
            // front, so shift the trailer's z-bounds by that offset and union with the front's z-bounds
            float trailerOffsetZ = tractorData.m_AttachPosition.z - trailerData.m_AttachPosition.z;
            if (!ArticulatedBusGeometryHelper.TryComputeInflatedBoundsZ(
                    frontGeometry.m_Bounds.min.z, frontGeometry.m_Bounds.max.z,
                    trailerGeometry.m_Bounds.min.z, trailerGeometry.m_Bounds.max.z,
                    trailerOffsetZ,
                    out float combinedMinZ, out float combinedMaxZ, out float combinedLength))
            {
                return; // front bounds already span the whole bus (or unexpected geometry), so nothing to do
            }

            float currentLength = frontGeometry.m_Bounds.max.z - frontGeometry.m_Bounds.min.z;
            m_InflatedFronts[busPrefab] = new OriginalGeometryZ
            {
                m_MinZ = frontGeometry.m_Bounds.min.z,
                m_MaxZ = frontGeometry.m_Bounds.max.z,
                m_SizeZ = frontGeometry.m_Size.z
            };

            frontGeometry.m_Bounds.min.z = combinedMinZ;
            frontGeometry.m_Bounds.max.z = combinedMaxZ;
            frontGeometry.m_Size.z = combinedLength;
            entityManager.SetComponentData(busPrefab, frontGeometry);

            SessionLog.Diagnostic($"Inflated front {busPrefab} parking length {currentLength:F2}m -> {combinedLength:F2}m (full bus, force depot garaging)");
        }

        /// <summary>
        /// Revert the prefab mutations that must not leak into the editor or main menu
        /// </summary>
        /// <remarks>
        /// Reverts the z-geometry and the copied trailer activity locations, since prefabs live in the shared World
        /// across modes
        /// </remarks>
        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);

            if (mode.IsGame() || (m_InflatedFronts.Count == 0 && m_SyncedActivityFronts.Count == 0))
            {
                return;
            }

            EntityManager entityManager = EntityManager;

            // Revert inflated parking length
            int restored = 0;
            foreach (KeyValuePair<Entity, OriginalGeometryZ> entry in m_InflatedFronts)
            {
                Entity busPrefab = entry.Key;
                if (!entityManager.Exists(busPrefab) || !entityManager.HasComponent<ObjectGeometryData>(busPrefab))
                {
                    continue;
                }

                ObjectGeometryData geometry = entityManager.GetComponentData<ObjectGeometryData>(busPrefab);
                geometry.m_Bounds.min.z = entry.Value.m_MinZ;
                geometry.m_Bounds.max.z = entry.Value.m_MaxZ;
                geometry.m_Size.z = entry.Value.m_SizeZ;
                entityManager.SetComponentData(busPrefab, geometry);
                restored++;
            }

            m_InflatedFronts.Clear();

            // Drop the copied trailer activity locations back to the front's own count
            foreach (KeyValuePair<Entity, int> entry in m_SyncedActivityFronts)
            {
                Entity busPrefab = entry.Key;
                if (!entityManager.Exists(busPrefab) || !entityManager.HasBuffer<ActivityLocationElement>(busPrefab))
                {
                    continue;
                }

                DynamicBuffer<ActivityLocationElement> frontLocations = entityManager.GetBuffer<ActivityLocationElement>(busPrefab);
                if (frontLocations.Length > entry.Value)
                {
                    frontLocations.ResizeUninitialized(entry.Value);
                }
            }

            m_SyncedActivityFronts.Clear();

            // Clear the once-per-prefab guard so the activity sync re-runs on the next game load
            m_ActivityLocationSyncedBuses.Clear();

            if (restored > 0)
            {
                SessionLog.Diagnostic($"Restored original parking length on {restored} bus prefab(s) (leaving game mode)");
            }
        }

        /// <summary>
        /// Copy front bus prefab color variations to the trailer
        /// </summary>
        private void CopyColorVariations(EntityManager entityManager, Entity busPrefab, Entity trailerPrefab)
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

            if (syncedMeshes > 0)
            {
                SessionLog.Diagnostic($"Synced {syncedMeshes} trailer render-prefab color variation set(s) on {trailerPrefab} to bus prefab {busPrefab}");
            }
        }

        /// <summary>
        /// Copy trailer activity locations onto the front bus prefab
        /// </summary>
        /// <remarks>
        /// Only boarding/disembarking locations. The game reads a passenger's door position only from the boarded
        /// vehicle's prefab (the actual transport prefab / front)
        /// </remarks>
        private void SyncTrailerActivityLocations(EntityManager entityManager, Entity busPrefab, Entity trailerPrefab, CarTractorData tractorData, CarTrailerData trailerData)
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

            // Trailer origin in the front's local space when straight
            float3 trailerOriginInFront = tractorData.m_AttachPosition - trailerData.m_AttachPosition;
            uint boardingMask = new ActivityMask(ActivityType.Enter).m_Mask | new ActivityMask(ActivityType.Exit).m_Mask;

            // Ensure the front buffer exists BEFORE taking buffer handles (AddBuffer is a structural change)
            if (!entityManager.HasBuffer<ActivityLocationElement>(busPrefab))
            {
                entityManager.AddBuffer<ActivityLocationElement>(busPrefab);
            }

            DynamicBuffer<ActivityLocationElement> trailerLocations = entityManager.GetBuffer<ActivityLocationElement>(trailerPrefab);
            DynamicBuffer<ActivityLocationElement> frontLocations = entityManager.GetBuffer<ActivityLocationElement>(busPrefab);

            // remember the front's own count so OnGamePreload can drop the copied doors when leaving the game
            int originalCount = frontLocations.Length;

            int copied = 0;
            for (int i = 0; i < trailerLocations.Length; i++)
            {
                ActivityLocationElement location = trailerLocations[i];
                if ((location.m_ActivityMask.m_Mask & boardingMask) == 0)
                {
                    continue; // only boarding/disembarking locations; skip others
                }

                location.m_Position += trailerOriginInFront;
                frontLocations.Add(location);
                copied++;
            }

            if (copied > 0)
            {
                m_SyncedActivityFronts[busPrefab] = originalCount;
            }

            if (copied > 0)
            {
                SessionLog.Diagnostic($"Copied {copied} trailer boarding/alighting location(s) from {trailerPrefab} onto bus prefab {busPrefab}");
            }
        }

        /// <summary>
        /// Check whether any variation is Brand-sourced with external channels
        /// </summary>
        /// <remarks>
        /// Such a variation is eligible for transport line colors
        /// </remarks>
        /// <returns>True if at least one variation is Brand-sourced with external channels</returns>
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
