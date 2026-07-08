using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using ObjectTransform = Game.Objects.Transform;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Create compatibility with the "Asset Icon Creator" mod
    /// </summary>
    /// <remarks>
    /// While AIC is capturing, temporarily spawn the trailer then remove it again
    /// </remarks>
    public sealed partial class ArticulatedBusIconCreatorCompatSystem : GameSystemBase
    {
        private const string IconCreatorToolTypeName = "AssetIconCreator.AssetSetupToolSystem";

        /// <summary>
        /// Frames to wait after the capture closes before teardown
        /// </summary>
        /// <remarks>
        /// So AIC's graphics restore has settled
        /// </remarks>
        private const int TeardownDelayFrames = 120;

        /// <summary>
        /// How often to re-scan for the AIC assembly until found
        /// </summary>
        /// <remarks>
        /// Measured in editor frames. It can load after our first editor frame, so a one-shot scan would miss it
        /// </remarks>
        private const int TypeScanIntervalFrames = 30;

        private EntityQuery m_BusQuery;

        /// <summary>
        /// Reflection handles into the optional AIC mod
        /// </summary>
        /// <remarks>
        /// If it is absent m_AicToolType stays null and we no-op
        /// </remarks>
        private int m_TypeScanCooldown;
        private Type? m_AicToolType;
        private PropertyInfo? m_ScreenshotUtilityProp;
        private ComponentSystemBase? m_AicTool;
        private PropertyInfo? m_SettingUpProp;

        /// <summary>
        /// Front bus to trailer we spawned
        /// </summary>
        /// <remarks>
        /// So teardown removes exactly what we created
        /// </remarks>
        private readonly Dictionary<Entity, Entity> m_SpawnedTrailers = new Dictionary<Entity, Entity>();

        /// <summary>
        /// Fronts we've already logged a spawn failure for this capture
        /// </summary>
        /// <remarks>
        /// So the breadcrumb fires once, not per frame
        /// </remarks>
        private readonly HashSet<Entity> m_SpawnFailureLogged = new HashSet<Entity>();
        private bool m_WasCapturing;
        private int m_TeardownCountdown = -1; // < 0 = idle, counts down to 0 then tears down

        /// <summary>
        /// Query AIC's photo-instance bus fronts
        /// </summary>
        /// <remarks>
        /// Same as the in-game spawner but without the one-shot Created tag
        /// </remarks>
        protected override void OnCreate()
        {
            base.OnCreate();

            m_BusQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<VehiclePublicTransport>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<ObjectTransform>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<CarTrailer>(),
                    ComponentType.ReadOnly<LayoutElement>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });
        }

        /// <summary>
        /// Spawn the trailer while AIC captures, then remove it afterward
        /// </summary>
        /// <remarks>
        /// Runs in the editor only. Removes it a set number of frames after the capture closes
        /// </remarks>
        protected override void OnUpdate()
        {
            // editor only, in real gameplay the in-game spawner handles trailers
            if (!Mod.IsInEditor())
            {
                m_WasCapturing = false;
                m_TeardownCountdown = -1;
                return;
            }

            bool capturing = IsIconCreatorCapturing();

            if (capturing)
            {
                if (!m_WasCapturing)
                {
                    // capture just started; record the matched-front count to the dev log only (diagnostic detail)
                    SessionLog.Diagnostic($"Asset Icon Creator capture started: {m_BusQuery.CalculateEntityCount()} articulated bus front(s) matched the spawn query");
                    m_SpawnFailureLogged.Clear();
                }

                m_TeardownCountdown = -1; // capture in progress -> cancel any pending teardown
                SpawnTrailersForCapture();
            }
            else if (m_WasCapturing && m_SpawnedTrailers.Count > 0)
            {
                m_TeardownCountdown = TeardownDelayFrames; // capture just ended -> arm the deferred teardown
            }
            else if (m_WasCapturing)
            {
                // capture ended having spawned nothing; dev-log only (expected when capturing a non-articulated bus)
                SessionLog.Diagnostic("Asset Icon Creator capture ended with no trailer spawned (no matching articulated bus front seen during capture)");
            }
            else if (m_TeardownCountdown > 0 && --m_TeardownCountdown == 0)
            {
                RemoveCaptureTrailers();
                m_TeardownCountdown = -1;
            }

            m_WasCapturing = capturing;
        }

        /// <summary>
        /// Check whether AIC is currently capturing, read from its ScreenshotUtility.SettingUp by reflection
        /// </summary>
        /// <remarks>
        /// Resolve the AIC handles lazily and return false, never throw, when AIC is absent or its API moved
        /// </remarks>
        /// <returns>True while an AIC capture is setting up; false when AIC is absent or unreadable</returns>
        private bool IsIconCreatorCapturing()
        {
            if (m_AicToolType == null || m_ScreenshotUtilityProp == null)
            {
                // AIC may load after our first editor frame, so re-scan periodically instead of giving up once
                if (m_TypeScanCooldown > 0)
                {
                    m_TypeScanCooldown--;
                    return false;
                }
                m_TypeScanCooldown = TypeScanIntervalFrames;

                m_AicToolType = FindLoadedType(IconCreatorToolTypeName);
                m_ScreenshotUtilityProp = m_AicToolType?.GetProperty("ScreenshotUtility", BindingFlags.Public | BindingFlags.Instance);
                if (m_AicToolType == null || m_ScreenshotUtilityProp == null)
                {
                    return false; // AIC not installed (yet)
                }
            }

            if (m_AicTool == null)
            {
                // the tool system is created during editor tool init, keep retrying until it exists
                m_AicTool = World.GetExistingSystemManaged(m_AicToolType);
                if (m_AicTool == null)
                {
                    return false;
                }

                SessionLog.Diagnostic("Asset Icon Creator detected; articulated-bus icon capture compatibility is active");
            }

            try
            {
                object? screenshotUtility = m_ScreenshotUtilityProp.GetValue(m_AicTool);
                if (screenshotUtility == null)
                {
                    return false;
                }

                if (m_SettingUpProp == null)
                {
                    m_SettingUpProp = screenshotUtility.GetType().GetProperty("SettingUp", BindingFlags.Public | BindingFlags.Instance);
                    if (m_SettingUpProp == null)
                    {
                        return false;
                    }
                }

                return m_SettingUpProp.GetValue(screenshotUtility) is bool settingUp && settingUp;
            }
            catch
            {
                return false; // never let a reflection mismatch take down the editor
            }
        }

        /// <summary>
        /// Find a type by full name across all loaded assemblies
        /// </summary>
        /// <returns>The matching type, or null if no loaded assembly defines it</returns>
        private static Type? FindLoadedType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type? type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    // some dynamic assemblies throw on GetType, ignore and keep scanning
                }
            }

            return null;
        }

        /// <summary>
        /// Spawn a trailer once for each eligible bus front
        /// </summary>
        /// <remarks>
        /// Records each front-to-trailer pair for teardown
        /// </remarks>
        private void SpawnTrailersForCapture()
        {
            EntityManager entityManager = EntityManager;
            NativeArray<Entity> buses = m_BusQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < buses.Length; i++)
                {
                    Entity bus = buses[i];

                    // skip fronts we already trailered this capture
                    if (m_SpawnedTrailers.ContainsKey(bus))
                    {
                        continue;
                    }

                    // spawn and record the front-to-trailer pair
                    Entity trailer = ArticulatedBusTrailerSpawnSystem.TrySpawnTrailer(entityManager, bus);
                    if (trailer != Entity.Null)
                    {
                        m_SpawnedTrailers[bus] = trailer;
                        SessionLog.Event($"Asset Icon Creator: captured articulated bus front {bus} with its trailer");
                        SessionLog.Diagnostic($"Asset Icon Creator capture: spawned trailer {trailer} for bus {bus}");
                    }
                    else if (m_SpawnFailureLogged.Add(bus))
                    {
                        // matched the query but produced no trailer; dev-log the exact blocker (expected for a
                        // non-articulated bus, so keep it out of the user log)
                        SessionLog.DiagnosticWarn($"Asset Icon Creator: no trailer spawned for bus front {bus} ({DescribeSpawnBlocker(entityManager, bus)})");
                    }
                }
            }
            finally
            {
                buses.Dispose();
            }
        }

        /// <summary>
        /// Remove the spawned trailers and keep each front bus in place
        /// </summary>
        private void RemoveCaptureTrailers()
        {
            if (m_SpawnedTrailers.Count == 0)
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            int removed = 0;

            // Shrink the front's LayoutElement buffer back to [front] and delete the trailer, keeping the front
            // This is a buffer-content edit, not a RemoveComponent. A layout length of 1 makes vanilla
            // ObjectInterpolateSystem.UpdateInterpolatedCarTrailers skip it, exactly as for any single bus
            foreach (KeyValuePair<Entity, Entity> pair in m_SpawnedTrailers)
            {
                Entity bus = pair.Key;
                Entity trailer = pair.Value;

                // shrink the front's layout back to just the front
                if (bus != Entity.Null && entityManager.Exists(bus) && !entityManager.HasComponent<Deleted>(bus) &&
                    entityManager.HasBuffer<LayoutElement>(bus))
                {
                    DynamicBuffer<LayoutElement> layout = entityManager.GetBuffer<LayoutElement>(bus);
                    layout.Clear();
                    layout.Add(new LayoutElement(bus));
                    entityManager.AddComponent<BatchesUpdated>(bus);
                }

                // delete the trailer we spawned
                if (trailer != Entity.Null && entityManager.Exists(trailer) && !entityManager.HasComponent<Deleted>(trailer))
                {
                    entityManager.AddComponent<Deleted>(trailer);
                    removed++;
                }
            }

            SessionLog.Diagnostic($"Asset Icon Creator capture finished: removing {removed} trailer(s)");

            m_SpawnedTrailers.Clear();
        }

        /// <summary>
        /// Describe the reason a trailer spawn was blocked, for the session log
        /// </summary>
        /// <remarks>
        /// Mirror the spawner's precondition order but read only structural data, so it stays safe on the main thread
        /// </remarks>
        /// <returns>A short human-readable reason the spawn was blocked</returns>
        private static string DescribeSpawnBlocker(EntityManager entityManager, Entity bus)
        {
            if (!entityManager.HasComponent<PrefabRef>(bus))
            {
                return "front instance has no PrefabRef";
            }

            Entity busPrefab = entityManager.GetComponentData<PrefabRef>(bus).m_Prefab;
            if (!entityManager.HasComponent<CarTractorData>(busPrefab))
            {
                return "front prefab has no CarTractorData (not initialised as a tractor in the editor)";
            }

            CarTractorData tractorData = entityManager.GetComponentData<CarTractorData>(busPrefab);
            if (tractorData.m_FixedTrailer == Entity.Null)
            {
                return "front prefab CarTractorData.m_FixedTrailer is empty (no rear section linked)";
            }

            if (tractorData.m_TrailerType != CarTrailerType.Fixed)
            {
                return $"front prefab tractor trailer type is {tractorData.m_TrailerType}, not Fixed";
            }

            Entity trailerPrefab = tractorData.m_FixedTrailer;
            if (!entityManager.HasComponent<CarTrailerData>(trailerPrefab))
            {
                return "trailer prefab has no CarTrailerData (rear section not initialised in the editor)";
            }

            if (!entityManager.HasComponent<ObjectData>(trailerPrefab))
            {
                return "trailer prefab has no ObjectData / archetype (rear section not initialised in the editor)";
            }

            CarTrailerData trailerData = entityManager.GetComponentData<CarTrailerData>(trailerPrefab);
            if (trailerData.m_TrailerType != CarTrailerType.Fixed)
            {
                return $"trailer prefab trailer type is {trailerData.m_TrailerType}, not Fixed";
            }

            if (trailerData.m_FixedTractor != Entity.Null && trailerData.m_FixedTractor != busPrefab)
            {
                return $"trailer prefab is already linked to a different tractor ({trailerData.m_FixedTractor})";
            }

            return "preconditions look satisfied, spawn failed for another reason (enable diagnostic logging for detail)";
        }
    }
}
