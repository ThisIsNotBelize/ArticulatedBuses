using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using ObjectTransform = Game.Objects.Transform;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace TINB.ArticulatedBuses
{
    /* Compatibility with "Asset Icon Creator" mod. While AIC is
       capturing this mod temporarily spawns the trailer (so the icon shows the whole bus), then remove just the
       trailer once the capture settles, leaving the front in place. */
    public sealed partial class ArticulatedBusIconCreatorCompatSystem : GameSystemBase
    {
        private const string IconCreatorToolTypeName = "AssetIconCreator.AssetSetupToolSystem";

        /* Frames to wait after the capture closes before teardown, so AIC's graphics restore has settled */
        private const int TeardownDelayFrames = 120;

        /* How often (in editor frames) to re-scan for the AIC assembly until found (it can load after our first
           editor frame, so a one-shot scan would miss it) */
        private const int TypeScanIntervalFrames = 30;

        private EntityQuery m_BusQuery;

        /* Reflection handles into the optional AIC mod; if it is absent m_AicToolType stays null and we no-op */
        private int m_TypeScanCooldown;
        private Type? m_AicToolType;
        private PropertyInfo? m_ScreenshotUtilityProp;
        private ComponentSystemBase? m_AicTool;
        private PropertyInfo? m_SettingUpProp;

        /* Front bus -> trailer we spawned, so teardown removes exactly what we created */
        private readonly Dictionary<Entity, Entity> m_SpawnedTrailers = new Dictionary<Entity, Entity>();
        private bool m_WasCapturing;
        private int m_TeardownCountdown = -1; // < 0 = idle; counts down to 0 then tears down

        /* Query: AIC's photo-instance bus front (same as the in-game spawner, minus the one-shot Created tag) */
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

        /* Editor only: spawns the trailer while AIC captures, removes it a set number of frames afterwards */
        protected override void OnUpdate()
        {
            // editor only; in real gameplay the in-game spawner handles trailers
            if (GameManager.instance == null || !GameManager.instance.gameMode.IsEditor())
            {
                m_WasCapturing = false;
                m_TeardownCountdown = -1;
                return;
            }

            bool capturing = IsIconCreatorCapturing();

            if (capturing)
            {
                m_TeardownCountdown = -1; // capture in progress -> cancel any pending teardown
                SpawnTrailersForCapture();
            }
            else if (m_WasCapturing && m_SpawnedTrailers.Count > 0)
            {
                m_TeardownCountdown = TeardownDelayFrames; // capture just ended -> arm the deferred teardown
            }
            else if (m_TeardownCountdown > 0 && --m_TeardownCountdown == 0)
            {
                RemoveCaptureTrailers();
                m_TeardownCountdown = -1;
            }

            m_WasCapturing = capturing;
        }

        /* True while AIC is setting up / taking its shot (its ScreenshotUtility.SettingUp, read by reflection);
           resolves the AIC handles lazily and returns false (never throws) when AIC is absent or its API moved */
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
                // the tool system is created during editor tool init; keep retrying until it exists
                m_AicTool = World.GetExistingSystemManaged(m_AicToolType);
                if (m_AicTool == null)
                {
                    return false;
                }

                if (Mod.IsDiagnosticLoggingEnabled())
                {
                    Mod.Log.Info("Asset Icon Creator detected; articulated-bus icon capture compatibility is active");
                }
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

        /* Finds a type by full name across all loaded assemblies, or null if none defines it */
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
                    // some dynamic assemblies throw on GetType; ignore and keep scanning
                }
            }

            return null;
        }

        /* Spawns (once) a trailer for each eligible bus front, recording each pair for teardown */
        private void SpawnTrailersForCapture()
        {
            EntityManager entityManager = EntityManager;
            bool diagnosticLogging = Mod.IsDiagnosticLoggingEnabled();
            NativeArray<Entity> buses = m_BusQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < buses.Length; i++)
                {
                    Entity bus = buses[i];
                    if (m_SpawnedTrailers.ContainsKey(bus))
                    {
                        continue;
                    }

                    Entity trailer = ArticulatedBusTrailerSpawnSystem.TrySpawnTrailer(entityManager, bus, diagnosticLogging);
                    if (trailer != Entity.Null)
                    {
                        m_SpawnedTrailers[bus] = trailer;
                        if (diagnosticLogging)
                        {
                            Mod.Log.InfoFormat("Asset Icon Creator capture: spawned trailer {0} for bus {1}", trailer, bus);
                        }
                    }
                }
            }
            finally
            {
                buses.Dispose();
            }
        }

        /* Removes the spawned trailers while keeping each front bus in place */
        private void RemoveCaptureTrailers()
        {
            if (m_SpawnedTrailers.Count == 0)
            {
                return;
            }

            EntityManager entityManager = EntityManager;
            bool diagnosticLogging = Mod.IsDiagnosticLoggingEnabled();
            int removed = 0;

            /* Detach the trailer but keep the front. The front is a Car Prefab, so its layout runs through
               ObjectInterpolateSystem.UpdateInterpolatedCarTrailers, which returns early when LayoutElement
               length <= 1. So we shrink the front's LayoutElement to [front] (a buffer-content edit, not a
               RemoveComponent) and delete the trailer
             */
            foreach (KeyValuePair<Entity, Entity> pair in m_SpawnedTrailers)
            {
                Entity bus = pair.Key;
                Entity trailer = pair.Value;

                if (bus != Entity.Null && entityManager.Exists(bus) && !entityManager.HasComponent<Deleted>(bus) &&
                    entityManager.HasBuffer<LayoutElement>(bus))
                {
                    DynamicBuffer<LayoutElement> layout = entityManager.GetBuffer<LayoutElement>(bus);
                    layout.Clear();
                    layout.Add(new LayoutElement(bus));
                    entityManager.AddComponent<BatchesUpdated>(bus);
                }

                if (trailer != Entity.Null && entityManager.Exists(trailer) && !entityManager.HasComponent<Deleted>(trailer))
                {
                    entityManager.AddComponent<Deleted>(trailer);
                    removed++;
                }
            }

            if (diagnosticLogging)
            {
                Mod.Log.InfoFormat("Asset Icon Creator capture finished: removing {0} trailer(s)", removed);
            }

            m_SpawnedTrailers.Clear();
        }
    }
}
