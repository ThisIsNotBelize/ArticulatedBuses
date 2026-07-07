using Colossal.Collections;
using System.Collections.Generic;
using System.Text;
using Game;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Jobs;
using ObjectTransform = Game.Objects.Transform;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace TINB.ArticulatedBuses
{
    /* Drives the bellows of an articulated bus. Game vehicle skinning is rigid (one bone per vertex), so the
       accordion is approximated by a rigid chain of VehicleConnection bones, rotated each frame to match the
       inter-section angle, with the gap-most bone (the "cap") pinned so the seam stays tight. Runs in the
       Rendering phase, after ObjectInterpolateSystem and before ProceduralSkeletonSystem */
    public sealed partial class ArticulatedBusConnectionBoneSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem = null!;
        private PreCullingSystem m_PreCullingSystem = null!;
        private ProceduralSkeletonSystem m_ProceduralSkeletonSystem = null!;

        /* Diagnostics state (Debug only). Once per entity: was the bus recognised and how many connection bones
           per submesh (0 = bones not typed VehicleConnection). Rate-limited: the peak articulation angle */
        private readonly HashSet<Entity> m_LoggedRigSummary = new HashSet<Entity>();
        private readonly Dictionary<Entity, float> m_MaxLoggedSolveAngleDegrees = new Dictionary<Entity, float>();
        private readonly List<Entity> m_PruneScratch = new List<Entity>();

        /* Only re-log the solve line once the peak articulation grows by this many degrees, so a turning bus
           logs a handful of times instead of once per ~degree */
        private const float SolveLogAngleStepDegrees = 20f;

        /* Prune diagnostics entries of deleted buses this often (the sets only grow with diagnostics on) */
        private const int PruneIntervalFrames = 4096;
        private int m_PruneCountdown = PruneIntervalFrames;

        /* Caches the vanilla systems we read: prefab names, culling data, skeleton heap */
        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_PreCullingSystem = World.GetOrCreateSystemManaged<PreCullingSystem>();
            m_ProceduralSkeletonSystem = World.GetOrCreateSystemManaged<ProceduralSkeletonSystem>();
        }

        /* Drives the bellows for every near-camera articulated layout that has a skeleton */
        protected override void OnUpdate()
        {
            if (!Mod.ShouldRunRuntimeSystems())
            {
                return;
            }

            JobHandle cullingDeps;
            NativeList<PreCullingData> cullingData = m_PreCullingSystem.GetCullingData(readOnly: true, out cullingDeps);
            cullingDeps.Complete();

            bool diagnosticLogging = Mod.IsDiagnosticLoggingEnabled();
            EntityManager entityManager = EntityManager;
            for (int i = 0; i < cullingData.Length; i++)
            {
                PreCullingData entry = cullingData[i];
                if ((entry.m_Flags & (PreCullingFlags.NearCamera | PreCullingFlags.VehicleLayout | PreCullingFlags.Skeleton)) !=
                    (PreCullingFlags.NearCamera | PreCullingFlags.VehicleLayout | PreCullingFlags.Skeleton))
                {
                    continue;
                }

                UpdateLayoutConnectionBones(entityManager, entry.m_Entity, diagnosticLogging);
            }

            if (--m_PruneCountdown <= 0)
            {
                m_PruneCountdown = PruneIntervalFrames;
                PruneDeadEntries(entityManager);
            }
        }

        /* Drops diagnostics state for buses that no longer exist */
        private void PruneDeadEntries(EntityManager entityManager)
        {
            if (m_LoggedRigSummary.Count == 0 && m_MaxLoggedSolveAngleDegrees.Count == 0)
            {
                return;
            }

            m_PruneScratch.Clear();
            foreach (Entity entity in m_LoggedRigSummary)
            {
                if (!entityManager.Exists(entity))
                {
                    m_PruneScratch.Add(entity);
                }
            }

            foreach (Entity entity in m_MaxLoggedSolveAngleDegrees.Keys)
            {
                if (!entityManager.Exists(entity))
                {
                    m_PruneScratch.Add(entity);
                }
            }

            for (int i = 0; i < m_PruneScratch.Count; i++)
            {
                m_LoggedRigSummary.Remove(m_PruneScratch[i]);
                m_MaxLoggedSolveAngleDegrees.Remove(m_PruneScratch[i]);
            }

            m_PruneScratch.Clear();
        }

        /* Validates that root is one of our articulated buses, then drives the connection bones of every section
           in its layout, passing each its layout neighbours */
        private void UpdateLayoutConnectionBones(EntityManager entityManager, Entity root, bool diagnosticLogging)
        {
            if (root == Entity.Null ||
                !entityManager.HasBuffer<LayoutElement>(root) ||
                !entityManager.HasComponent<Car>(root) ||
                !entityManager.HasComponent<VehiclePublicTransport>(root) ||
                !entityManager.HasComponent<PrefabRef>(root) ||
                !entityManager.HasComponent<InterpolatedTransform>(root))
            {
                return;
            }

            DynamicBuffer<LayoutElement> layout = entityManager.GetBuffer<LayoutElement>(root);
            if (layout.Length < 2 || layout[0].m_Vehicle != root)
            {
                return;
            }

            PrefabRef rootPrefabRef = entityManager.GetComponentData<PrefabRef>(root);
            Entity rootPrefab = rootPrefabRef.m_Prefab;
            if (!entityManager.HasComponent<CarTractorData>(rootPrefab))
            {
                return;
            }

            CarTractorData tractorData = entityManager.GetComponentData<CarTractorData>(rootPrefab);
            Entity fixedTrailerPrefab = tractorData.m_FixedTrailer;
            if (fixedTrailerPrefab == Entity.Null || !LayoutContainsPrefab(entityManager, layout, fixedTrailerPrefab))
            {
                return;
            }

            for (int i = 0; i < layout.Length; i++)
            {
                Entity previous = i > 0 ? layout[i - 1].m_Vehicle : Entity.Null;
                Entity current = layout[i].m_Vehicle;
                Entity next = i < layout.Length - 1 ? layout[i + 1].m_Vehicle : Entity.Null;

                UpdateVehicleConnectionBones(entityManager, previous, current, next, diagnosticLogging);
            }
        }

        /* True if any non-lead layout vehicle uses the given (trailer) prefab */
        private static bool LayoutContainsPrefab(EntityManager entityManager, DynamicBuffer<LayoutElement> layout, Entity prefab)
        {
            for (int i = 1; i < layout.Length; i++)
            {
                Entity vehicle = layout[i].m_Vehicle;
                if (vehicle != Entity.Null &&
                    entityManager.HasComponent<PrefabRef>(vehicle) &&
                    entityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab == prefab)
                {
                    return true;
                }
            }

            return false;
        }

        /* Drives one section's connection bones using its previous/next neighbours; ensures the runtime skeleton
           exists, then solves each connection submesh */
        private void UpdateVehicleConnectionBones(EntityManager entityManager, Entity previous, Entity current, Entity next, bool diagnosticLogging)
        {
            if (current == Entity.Null ||
                !entityManager.HasComponent<PrefabRef>(current) ||
                !entityManager.HasComponent<InterpolatedTransform>(current))
            {
                return;
            }

            PrefabRef currentPrefabRef = entityManager.GetComponentData<PrefabRef>(current);
            Entity currentPrefab = currentPrefabRef.m_Prefab;

            if (!entityManager.HasComponent<ObjectGeometryData>(currentPrefab) ||
                !entityManager.HasBuffer<SubMesh>(currentPrefab))
            {
                return;
            }

            EnsureVehicleRuntimeSkeletonInitialized(entityManager, current, currentPrefab);

            if (!entityManager.HasBuffer<Skeleton>(current) ||
                !entityManager.HasBuffer<Bone>(current))
            {
                return;
            }

            /* Resolve the prefab name (a managed lookup) once per vehicle, only with diagnostics on; prefabName
               != null also doubles as the diagnostics-enabled flag below */
            string? prefabName = diagnosticLogging ? TryGetPrefabName(currentPrefab) : null;

            ObjectGeometryData previousGeometry = default(ObjectGeometryData);
            ObjectGeometryData nextGeometry = default(ObjectGeometryData);

            ObjectTransform previousTransform = default(ObjectTransform);
            ObjectTransform currentTransform = entityManager.GetComponentData<InterpolatedTransform>(current).ToTransform();
            ObjectTransform nextTransform = default(ObjectTransform);

            TryGetLayoutNeighbor(entityManager, previous, out previousGeometry, out previousTransform);
            TryGetLayoutNeighbor(entityManager, next, out nextGeometry, out nextTransform);

            // natural cap positions of the two neighbours (object space) for the tight-seam solve
            float3 prevNeighborCapLocal = default(float3);
            float3 nextNeighborCapLocal = default(float3);
            bool hasPrevNeighborCap = previous != Entity.Null &&
                TryGetNeighborCapNaturalLocal(entityManager, previous, previousTransform, currentTransform, out prevNeighborCapLocal);
            bool hasNextNeighborCap = next != Entity.Null &&
                TryGetNeighborCapNaturalLocal(entityManager, next, nextTransform, currentTransform, out nextNeighborCapLocal);

            DynamicBuffer<Skeleton> skeletons = entityManager.GetBuffer<Skeleton>(current);
            DynamicBuffer<Bone> bones = entityManager.GetBuffer<Bone>(current);
            DynamicBuffer<SubMesh> subMeshes = entityManager.GetBuffer<SubMesh>(currentPrefab);

            /* Once-per-entity rig summary (a submesh with bones but N=0 connection bones = bones not typed
               VehicleConnection) */
            bool logRigSummary = prefabName != null && m_LoggedRigSummary.Add(current);
            StringBuilder? perSubmesh = logRigSummary ? new StringBuilder() : null;

            int skeletonCount = math.min(skeletons.Length, subMeshes.Length);
            int animatedConnectionBoneCount = 0;
            for (int skeletonIndex = 0; skeletonIndex < skeletonCount; skeletonIndex++)
            {
                ref Skeleton skeleton = ref skeletons.ElementAt(skeletonIndex);
                if (skeleton.m_BufferAllocation.Empty || skeleton.m_BoneOffset < 0)
                {
                    continue;
                }

                Entity subMeshEntity = subMeshes[skeletonIndex].m_SubMesh;
                if (!entityManager.HasBuffer<ProceduralBone>(subMeshEntity))
                {
                    continue;
                }

                DynamicBuffer<ProceduralBone> proceduralBones = entityManager.GetBuffer<ProceduralBone>(subMeshEntity);

                /* Count this submesh's VehicleConnection bones (per LOD). Each gets local fraction 0.5/N and the
                   parent hierarchy accumulates them to the half-angle, so a reduced LOD (e.g. N=1) self-adapts */
                int connectionBoneCount = CountVehicleConnectionBones(proceduralBones);
                perSubmesh?.Append(perSubmesh.Length > 0 ? $",{connectionBoneCount}" : connectionBoneCount.ToString());
                if (connectionBoneCount == 0)
                {
                    continue;
                }

                float lastLoggedMeaningfulAngle = 0f;
                if (prefabName != null)
                {
                    m_MaxLoggedSolveAngleDegrees.TryGetValue(current, out lastLoggedMeaningfulAngle);
                }

                float loggedMeaningfulAngle = SolveConnectionChain(
                    proceduralBones,
                    bones,
                    ref skeleton,
                    previousGeometry,
                    nextGeometry,
                    previousTransform,
                    currentTransform,
                    nextTransform,
                    connectionBoneCount,
                    hasPrevNeighborCap,
                    prevNeighborCapLocal,
                    hasNextNeighborCap,
                    nextNeighborCapLocal,
                    lastLoggedMeaningfulAngle,
                    prefabName,
                    current);

                if (prefabName != null && loggedMeaningfulAngle > lastLoggedMeaningfulAngle)
                {
                    m_MaxLoggedSolveAngleDegrees[current] = loggedMeaningfulAngle;
                }

                animatedConnectionBoneCount += connectionBoneCount;
            }

            if (logRigSummary)
            {
                Mod.Log.Info(
                    $"{nameof(ArticulatedBusConnectionBoneSystem)} diagnostics: articulated bus prefab={prefabName}, entity={current}, " +
                    $"runtimeSkeletons={skeletons.Length}, runtimeBones={bones.Length}, connectionBonesPerSubmesh=[{perSubmesh}], animatedBones={animatedConnectionBoneCount}");
            }
        }

        /* Ensures the vehicle has runtime Skeleton + Bone buffers matching its prefab's procedural bones
           (mirroring vanilla ProceduralSkeletonSystem), so we can write bone transforms; no-op if already
           correctly sized */
        private void EnsureVehicleRuntimeSkeletonInitialized(EntityManager entityManager, Entity vehicle, Entity prefab)
        {
            if (!entityManager.HasBuffer<Skeleton>(vehicle))
            {
                entityManager.AddBuffer<Skeleton>(vehicle);
            }

            if (!entityManager.HasBuffer<Bone>(vehicle))
            {
                entityManager.AddBuffer<Bone>(vehicle);
            }

            DynamicBuffer<Skeleton> skeletons = entityManager.GetBuffer<Skeleton>(vehicle);
            DynamicBuffer<Bone> bones = entityManager.GetBuffer<Bone>(vehicle);
            DynamicBuffer<SubMesh> subMeshes = entityManager.GetBuffer<SubMesh>(prefab);

            int totalBoneCount = 0;
            int playbackLayerCount = 0;
            bool needsInitialization = skeletons.Length != subMeshes.Length;

            for (int i = 0; i < subMeshes.Length; i++)
            {
                Entity subMeshEntity = subMeshes[i].m_SubMesh;
                if (!entityManager.HasBuffer<ProceduralBone>(subMeshEntity))
                {
                    needsInitialization |= i >= skeletons.Length || skeletons[i].m_BoneOffset != -1;
                    continue;
                }

                DynamicBuffer<ProceduralBone> proceduralBones = entityManager.GetBuffer<ProceduralBone>(subMeshEntity);
                totalBoneCount += proceduralBones.Length;
                playbackLayerCount += CountPlaybackLayers(proceduralBones);

                if (!needsInitialization)
                {
                    if (i >= skeletons.Length)
                    {
                        needsInitialization = true;
                    }
                    else
                    {
                        Skeleton skeleton = skeletons[i];
                        int expectedBoneOffset = totalBoneCount - proceduralBones.Length;
                        needsInitialization |= skeleton.m_BufferAllocation.Empty || skeleton.m_BoneOffset != expectedBoneOffset;
                    }
                }
            }

            if (!needsInitialization &&
                bones.Length == totalBoneCount &&
                (!entityManager.HasBuffer<PlaybackLayer>(vehicle) || entityManager.GetBuffer<PlaybackLayer>(vehicle).Length == playbackLayerCount))
            {
                return;
            }

            JobHandle heapDependencies;
            NativeReference<ProceduralSkeletonSystem.AllocationInfo> allocationInfo;
            NativeQueue<ProceduralSkeletonSystem.AllocationRemove> allocationRemoves;
            int currentTime;
            NativeHeapAllocator heapAllocator = m_ProceduralSkeletonSystem.GetHeapAllocator(out allocationInfo, out allocationRemoves, out currentTime, out heapDependencies);
            heapDependencies.Complete();

            DeallocateSkeletonBuffers(skeletons, allocationRemoves, currentTime);

            skeletons.ResizeUninitialized(subMeshes.Length);
            bones.ResizeUninitialized(totalBoneCount);

            DynamicBuffer<Momentum> momentums = default(DynamicBuffer<Momentum>);
            if (entityManager.HasBuffer<Momentum>(vehicle))
            {
                momentums = entityManager.GetBuffer<Momentum>(vehicle);
                momentums.ResizeUninitialized(totalBoneCount);
                for (int i = 0; i < momentums.Length; i++)
                {
                    momentums[i] = default(Momentum);
                }
            }

            DynamicBuffer<PlaybackLayer> playbackLayers = default(DynamicBuffer<PlaybackLayer>);
            if (entityManager.HasBuffer<PlaybackLayer>(vehicle))
            {
                playbackLayers = entityManager.GetBuffer<PlaybackLayer>(vehicle);
                playbackLayers.ResizeUninitialized(playbackLayerCount);
            }

            int boneOffset = 0;
            int layerOffset = 0;
            for (int subMeshIndex = 0; subMeshIndex < subMeshes.Length; subMeshIndex++)
            {
                Entity subMeshEntity = subMeshes[subMeshIndex].m_SubMesh;
                if (!entityManager.HasBuffer<ProceduralBone>(subMeshEntity))
                {
                    skeletons[subMeshIndex] = new Skeleton
                    {
                        m_BoneOffset = -1
                    };
                    continue;
                }

                DynamicBuffer<ProceduralBone> proceduralBones = entityManager.GetBuffer<ProceduralBone>(subMeshEntity);
                NativeHeapBlock bufferAllocation = heapAllocator.Allocate((uint)proceduralBones.Length);
                if (bufferAllocation.Empty)
                {
                    heapAllocator.Resize(heapAllocator.Size + 1048576u / SkeletonMatrixByteSize);
                    bufferAllocation = heapAllocator.Allocate((uint)proceduralBones.Length);
                }

                ref ProceduralSkeletonSystem.AllocationInfo info = ref allocationInfo.ValueAsRef();
                info.m_AllocationCount++;

                Skeleton skeleton = new Skeleton
                {
                    m_BufferAllocation = bufferAllocation,
                    m_BoneOffset = boneOffset,
                    m_LayerOffset = layerOffset,
                    m_CurrentUpdated = true,
                    m_HistoryUpdated = true
                };

                int usedPlaybackMask = 0;
                for (int boneIndex = 0; boneIndex < proceduralBones.Length; boneIndex++)
                {
                    ProceduralBone proceduralBone = proceduralBones[boneIndex];
                    skeleton.m_RequireHistory |= proceduralBone.m_ConnectionID != 0;

                    bones[boneOffset++] = new Bone
                    {
                        m_Position = proceduralBone.m_Position,
                        m_Rotation = proceduralBone.m_Rotation,
                        m_Scale = proceduralBone.m_Scale
                    };

                    if (playbackLayers.IsCreated)
                    {
                        BoneType type = proceduralBone.m_Type;
                        if ((uint)(type - 35) <= 7u)
                        {
                            int layerIndex = (int)(type - 35);
                            int layerMask = 1 << layerIndex;
                            if ((usedPlaybackMask & layerMask) == 0)
                            {
                                usedPlaybackMask |= layerMask;
                                playbackLayers[layerOffset++] = new PlaybackLayer
                                {
                                    m_ClipIndex = -1,
                                    m_LayerIndex = (byte)layerIndex
                                };
                            }
                        }
                    }
                }

                skeletons[subMeshIndex] = skeleton;
            }

            m_ProceduralSkeletonSystem.AddHeapWriter(default(JobHandle));
        }

        /* Queues the existing skeleton heap allocations for release before reallocating */
        private static void DeallocateSkeletonBuffers(DynamicBuffer<Skeleton> skeletons, NativeQueue<ProceduralSkeletonSystem.AllocationRemove> allocationRemoves, int currentTime)
        {
            for (int i = 0; i < skeletons.Length; i++)
            {
                Skeleton skeleton = skeletons[i];
                if (!skeleton.m_BufferAllocation.Empty)
                {
                    allocationRemoves.Enqueue(new ProceduralSkeletonSystem.AllocationRemove
                    {
                        m_Allocation = skeleton.m_BufferAllocation,
                        m_RemoveTime = currentTime
                    });
                }
            }
        }

        /* Counts the distinct playback layers used by a submesh's bones (types 35..42 -> layers 0..7) */
        private static int CountPlaybackLayers(DynamicBuffer<ProceduralBone> proceduralBones)
        {
            int usedMask = 0;
            int count = 0;
            for (int i = 0; i < proceduralBones.Length; i++)
            {
                BoneType type = proceduralBones[i].m_Type;
                if ((uint)(type - 35) > 7u)
                {
                    continue;
                }

                int layerIndex = (int)(type - 35);
                int layerMask = 1 << layerIndex;
                if ((usedMask & layerMask) != 0)
                {
                    continue;
                }

                usedMask |= layerMask;
                count++;
            }

            return count;
        }

        private const uint SkeletonMatrixByteSize = 64u;

        /* Prefab display name for diagnostics (falls back to the entity id) */
        private string TryGetPrefabName(Entity prefabEntity)
        {
            return m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out PrefabBase prefab)
                ? prefab.name
                : prefabEntity.ToString();
        }

        /* Reads a neighbour's geometry + interpolated transform; false if it is null or lacks the data */
        private static bool TryGetLayoutNeighbor(
            EntityManager entityManager,
            Entity vehicle,
            out ObjectGeometryData geometryData,
            out ObjectTransform transform)
        {
            geometryData = default(ObjectGeometryData);
            transform = default(ObjectTransform);

            if (vehicle == Entity.Null ||
                !entityManager.HasComponent<PrefabRef>(vehicle) ||
                !entityManager.HasComponent<InterpolatedTransform>(vehicle))
            {
                return false;
            }

            Entity prefab = entityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
            if (!entityManager.HasComponent<ObjectGeometryData>(prefab))
            {
                return false;
            }

            geometryData = entityManager.GetComponentData<ObjectGeometryData>(prefab);
            transform = entityManager.GetComponentData<InterpolatedTransform>(vehicle).ToTransform();
            return true;
        }

        /* Counts the VehicleConnection bones in a submesh (the N the bend is split over) */
        private static int CountVehicleConnectionBones(DynamicBuffer<ProceduralBone> proceduralBones)
        {
            int count = 0;
            for (int i = 0; i < proceduralBones.Length; i++)
            {
                if (proceduralBones[i].m_Type == BoneType.VehicleConnection)
                {
                    count++;
                }
            }

            return count;
        }

        /* The cap = the gap-most connection bone (largest |z|, i.e. closest to the neighbour) */
        private static int FindCapIndex(DynamicBuffer<ProceduralBone> proceduralBones)
        {
            int capIndex = -1;
            float capAbsZ = -1f;
            for (int i = 0; i < proceduralBones.Length; i++)
            {
                if (proceduralBones[i].m_Type != BoneType.VehicleConnection)
                {
                    continue;
                }

                float absZ = math.abs(proceduralBones[i].m_ObjectPosition.z);
                if (absZ > capAbsZ)
                {
                    capAbsZ = absZ;
                    capIndex = i;
                }
            }

            return capIndex;
        }

        /* A neighbour section's natural (pure-rotation) cap position in its own object space. Builds the
           neighbour's chainRotation from the relative transform and accumulates its rest chain, so both
           vehicles place the seam at the midpoint of the two naturals identically */
        private bool TryGetNeighborCapNaturalLocal(
            EntityManager entityManager,
            Entity neighbor,
            ObjectTransform neighborTransform,
            ObjectTransform currentTransform,
            out float3 capNaturalLocal)
        {
            capNaturalLocal = default(float3);
            if (neighbor == Entity.Null || !entityManager.HasComponent<PrefabRef>(neighbor))
            {
                return false;
            }

            Entity neighborPrefab = entityManager.GetComponentData<PrefabRef>(neighbor).m_Prefab;
            if (!entityManager.HasBuffer<SubMesh>(neighborPrefab))
            {
                return false;
            }

            DynamicBuffer<SubMesh> neighborSubMeshes = entityManager.GetBuffer<SubMesh>(neighborPrefab);
            for (int i = 0; i < neighborSubMeshes.Length; i++)
            {
                Entity subMeshEntity = neighborSubMeshes[i].m_SubMesh;
                if (!entityManager.HasBuffer<ProceduralBone>(subMeshEntity))
                {
                    continue;
                }

                DynamicBuffer<ProceduralBone> neighborBones = entityManager.GetBuffer<ProceduralBone>(subMeshEntity);
                int n = CountVehicleConnectionBones(neighborBones);
                int neighborCapIndex = FindCapIndex(neighborBones);
                if (n == 0 || neighborCapIndex < 0)
                {
                    continue;
                }

                quaternion relative = math.mul(math.inverse(neighborTransform.m_Rotation), currentTransform.m_Rotation);
                quaternion neighborChainRotation = math.slerp(quaternion.identity, relative, ArticulatedBusGeometry.ConnectionBoneFraction(n));
                capNaturalLocal = ComputeBoneObjectMatrix(neighborBones, neighborCapIndex, neighborChainRotation).c3.xyz;
                return true;
            }

            return false;
        }

        /* Drives a submesh's connection-bone chain. Every connection bone gets the same local rotation
           slerp(identity, neighborRotation, 0.5/N); the hierarchy accumulates them into a fan reaching the
           half-angle. Only the cap is additionally moved -- to the world-space midpoint of this section's and
           the neighbour's natural caps (symmetric, so both sections agree => tight seam) -- while keeping the
           chain rotation. N=1 collapses to the vanilla single-bone solve. Returns the peak angle for diagnostics */
        private float SolveConnectionChain(
            DynamicBuffer<ProceduralBone> proceduralBones,
            DynamicBuffer<Bone> bones,
            ref Skeleton skeleton,
            ObjectGeometryData previousGeometryData,
            ObjectGeometryData nextGeometryData,
            ObjectTransform previousTransform,
            ObjectTransform currentTransform,
            ObjectTransform nextTransform,
            int connectionBoneCount,
            bool hasPrevNeighborCap,
            float3 prevNeighborCapLocal,
            bool hasNextNeighborCap,
            float3 nextNeighborCapLocal,
            float lastLoggedMeaningfulAngle,
            string? prefabName,
            Entity vehicleEntity)
        {
            /* The cap = the gap-most connection bone; the one that must meet the neighbour's cap */
            int capIndex = FindCapIndex(proceduralBones);
            if (capIndex < 0)
            {
                return lastLoggedMeaningfulAngle;
            }

            ProceduralBone cap = proceduralBones[capIndex];
            quaternion inverseCurrentRotation = math.inverse(currentTransform.m_Rotation);
            quaternion neighborRotation;
            bool capUsesNextNeighbor; // which neighbour the cap meets (drives the tight-seam midpoint below)

            /* Vanilla AnimateVehicleConnectionBone branch: which side the cap faces and the neighbour's relative
               rotation. The degenerate guard resets when the neighbour has no depth (e.g. not yet spawned).
               Reversed handling is omitted: bus sections are never layout-reversed */
            if (cap.m_ObjectPosition.z < 0f)
            {
                capUsesNextNeighbor = true;
                if (nextGeometryData.m_Bounds.max.z == nextGeometryData.m_Bounds.min.z)
                {
                    ResetConnectionBones(proceduralBones, bones, ref skeleton);
                    return lastLoggedMeaningfulAngle;
                }

                neighborRotation = math.mul(inverseCurrentRotation, nextTransform.m_Rotation);
            }
            else
            {
                capUsesNextNeighbor = false;
                if (previousGeometryData.m_Bounds.max.z == previousGeometryData.m_Bounds.min.z)
                {
                    ResetConnectionBones(proceduralBones, bones, ref skeleton);
                    return lastLoggedMeaningfulAngle;
                }

                neighborRotation = math.mul(inverseCurrentRotation, previousTransform.m_Rotation);
            }

            float chainFraction = ArticulatedBusGeometry.ConnectionBoneFraction(connectionBoneCount);
            quaternion chainRotation = math.slerp(quaternion.identity, neighborRotation, chainFraction);

            /* Midpoint of natural caps. Inner bones run the untouched pure-rotation fan. The cap
               keeps the chain rotation (its accumulated angle = the seam bisector, identical on both caps) and
               is the only bone repositioned: to the world-space midpoint of this section's and the neighbour's
               natural caps. That midpoint is symmetric, so both sections compute the same point => tight seam at
               any bend. Falls back to the natural position if the neighbour cap can't be read */
            bool capHasNeighbor = capUsesNextNeighbor ? hasNextNeighborCap : hasPrevNeighborCap;
            float3 capTargetObject = default(float3);
            bool capRepositioned = false;
            if (capHasNeighbor)
            {
                ObjectTransform neighborTransform = capUsesNextNeighbor ? nextTransform : previousTransform;
                float3 neighborCapLocal = capUsesNextNeighbor ? nextNeighborCapLocal : prevNeighborCapLocal;

                float3 myCapNaturalLocal = ComputeBoneObjectMatrix(proceduralBones, capIndex, chainRotation).c3.xyz;
                float3 myCapWorld = currentTransform.m_Position + math.rotate(currentTransform.m_Rotation, myCapNaturalLocal);
                float3 neighborCapWorld = neighborTransform.m_Position + math.rotate(neighborTransform.m_Rotation, neighborCapLocal);
                float3 meetingWorld = ArticulatedBusGeometry.CapMidpoint(myCapWorld, neighborCapWorld);

                capTargetObject = math.rotate(inverseCurrentRotation, meetingWorld - currentTransform.m_Position);
                capRepositioned = true;
            }

            for (int i = 0; i < proceduralBones.Length; i++)
            {
                if (proceduralBones[i].m_Type != BoneType.VehicleConnection)
                {
                    continue;
                }

                int runtimeBoneIndex = skeleton.m_BoneOffset + i;
                if (runtimeBoneIndex < 0 || runtimeBoneIndex >= bones.Length)
                {
                    continue;
                }

                ProceduralBone pb = proceduralBones[i];
                float3 localPosition = pb.m_Position;
                if (i == capIndex && capRepositioned)
                {
                    // convert the world meeting point into this cap's local space via its parent's accumulated matrix
                    int parentIndex = pb.m_ParentIndex;
                    float4x4 parentObjectMatrix = (parentIndex < 0 || parentIndex >= proceduralBones.Length)
                        ? float4x4.identity
                        : ComputeBoneObjectMatrix(proceduralBones, parentIndex, chainRotation);
                    localPosition = math.transform(math.inverse(parentObjectMatrix), capTargetObject);
                }

                ref Bone bone = ref bones.ElementAt(runtimeBoneIndex);
                skeleton.m_CurrentUpdated |= !bone.m_Position.Equals(localPosition) | !bone.m_Rotation.Equals(chainRotation);
                bone.m_Position = localPosition;
                bone.m_Rotation = chainRotation;
            }

            if (prefabName != null)
            {
                float neighborAngleDegrees = math.degrees(2f * math.acos(math.clamp(math.abs(neighborRotation.value.w), 0f, 1f)));
                if (neighborAngleDegrees > 1f && neighborAngleDegrees >= lastLoggedMeaningfulAngle + SolveLogAngleStepDegrees)
                {
                    Mod.Log.Info(
                        $"{nameof(ArticulatedBusConnectionBoneSystem)} diagnostics: solve prefab={prefabName}, entity={vehicleEntity}, " +
                        $"connectionBones={connectionBoneCount}, neighborAngleDeg={neighborAngleDegrees:F2}, perBoneFraction={chainFraction:F4}, " +
                        $"capIndex={capIndex}, capRepositioned={capRepositioned}");
                    lastLoggedMeaningfulAngle = neighborAngleDegrees;
                }
            }

            return lastLoggedMeaningfulAngle;
        }

        /* Accumulates a bone's object-space TRS as GetSkinMatrices will: connection bones use the chain
           rotation, other ancestors use their authored rotation */
        private static float4x4 ComputeBoneObjectMatrix(DynamicBuffer<ProceduralBone> proceduralBones, int index, quaternion chainRotation)
        {
            ProceduralBone bone = proceduralBones[index];
            quaternion rotation = bone.m_Type == BoneType.VehicleConnection ? chainRotation : bone.m_Rotation;
            float4x4 local = float4x4.TRS(bone.m_Position, rotation, bone.m_Scale);
            if (bone.m_ParentIndex < 0 || bone.m_ParentIndex >= proceduralBones.Length)
            {
                return local;
            }

            return math.mul(ComputeBoneObjectMatrix(proceduralBones, bone.m_ParentIndex, chainRotation), local);
        }

        /* Resets a submesh's connection bones to their authored rest pose (when the neighbour has no depth yet) */
        private static void ResetConnectionBones(DynamicBuffer<ProceduralBone> proceduralBones, DynamicBuffer<Bone> bones, ref Skeleton skeleton)
        {
            for (int i = 0; i < proceduralBones.Length; i++)
            {
                ProceduralBone proceduralBone = proceduralBones[i];
                if (proceduralBone.m_Type != BoneType.VehicleConnection)
                {
                    continue;
                }

                int runtimeBoneIndex = skeleton.m_BoneOffset + i;
                if (runtimeBoneIndex < 0 || runtimeBoneIndex >= bones.Length)
                {
                    continue;
                }

                ref Bone bone = ref bones.ElementAt(runtimeBoneIndex);
                skeleton.m_CurrentUpdated |= !bone.m_Position.Equals(proceduralBone.m_Position) |
                                             !bone.m_Rotation.Equals(proceduralBone.m_Rotation);
                bone.m_Position = proceduralBone.m_Position;
                bone.m_Rotation = proceduralBone.m_Rotation;
            }
        }
    }
}
