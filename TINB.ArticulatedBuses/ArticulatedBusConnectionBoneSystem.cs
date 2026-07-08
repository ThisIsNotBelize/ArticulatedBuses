using Colossal.Collections;
using Game;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using ObjectTransform = Game.Objects.Transform;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Calculate proper bone transforms for smooth bending
    /// </summary>
    /// <remarks>
    /// Game vehicle skinning is rigid (one bone per vertex). The solution is to approximate a curve by a rigid chain of
    /// VehicleConnection bones, rotated each frame. Sections are layout members, i.e. the front bus or a trailer
    /// The bend is applied only to near-camera buses, so rig VehicleConnection bones on the high-detail LOD
    /// only. Reduced LODs (LOD1/LOD2) show at distances where the bend is not applied, so keep them rigid (no bone
    /// chains) for performance
    /// Per-frame pipeline:
    /// 1. OnUpdate picks the near-camera, multi-section, skeleton-bearing vehicles out of the render culling list
    /// 2. TransformLayoutConnectionBones validates the bus and walks its layout, handing each section its layout neighbours
    /// 3. TransformSectionConnectionBones ensures the section's runtime skeleton exists, then solves each connection
    ///    submesh (bone-count math in EnsureVehicleRuntimeSkeletonInitialized and its helpers)
    /// 4. SolveConnectionChain rotates the connection-bone chain toward the neighbour and pulls the cap (outmost) bone to the
    ///    shared join point
    /// </remarks>
    public sealed partial class ArticulatedBusConnectionBoneSystem : GameSystemBase
    {
        private PreCullingSystem m_PreCullingSystem = null!;
        private ProceduralSkeletonSystem m_ProceduralSkeletonSystem = null!;

        /// <summary>
        /// BoneType.PlaybackLayer0..PlaybackLayer7 are 8 contiguous animation-layer bone types
        /// </summary>
        private const BoneType FirstPlaybackLayerBone = BoneType.PlaybackLayer0;
        private const int PlaybackLayerCount = 8;

        /// <summary>
        /// Bytes per bone in the skeleton matrix heap
        /// </summary>
        /// <remarks>
        /// One 4x4 float matrix
        /// </remarks>
        private const uint SkeletonMatrixByteSize = 64u;

        /// <summary>
        /// Caches the vanilla systems' culling data and skeleton heap
        /// </summary>
        protected override void OnCreate()
        {
            base.OnCreate();
            m_PreCullingSystem = World.GetOrCreateSystemManaged<PreCullingSystem>();
            m_ProceduralSkeletonSystem = World.GetOrCreateSystemManaged<ProceduralSkeletonSystem>();
        }

        /// <summary>
        /// Transform the connection bones of every articulated bus this frame
        /// </summary>
        /// <remarks>
        /// Pipeline step 1. Snapshots the render culling list and forwards each near-camera, multi-section,
        /// skeleton-bearing vehicle to TransformLayoutConnectionBones
        /// </remarks>
        protected override void OnUpdate()
        {
            if (!Mod.IsInGame())
            {
                return;
            }

            // Snapshot the render culling list (read-only) and wait for its build job before reading it
            JobHandle cullingDeps;
            NativeList<PreCullingData> cullingData = m_PreCullingSystem.GetCullingData(readOnly: true, out cullingDeps);
            cullingDeps.Complete();

            EntityManager entityManager = EntityManager;

            // Only near-camera vehicles drawn as a multi-section layout with a skeleton get their connection bones transformed
            for (int i = 0; i < cullingData.Length; i++)
            {
                PreCullingData entry = cullingData[i];
                if ((entry.m_Flags & (PreCullingFlags.NearCamera | PreCullingFlags.VehicleLayout | PreCullingFlags.Skeleton)) !=
                    (PreCullingFlags.NearCamera | PreCullingFlags.VehicleLayout | PreCullingFlags.Skeleton))
                {
                    continue;
                }

                TransformLayoutConnectionBones(entityManager, entry.m_Entity);
            }
        }

        /// <summary>
        /// Validate that root is an articulated bus, then transform the connection bones of every section in its layout
        /// </summary>
        /// <remarks>
        /// Pipeline step 2. Each section (front or trailer) is passed its layout neighbours (the section ahead or
        /// behind). Bails out unless root leads a real multi-section layout whose fixed trailer prefab is present
        /// </remarks>
        private void TransformLayoutConnectionBones(EntityManager entityManager, Entity root)
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

            // Require a real multi-section layout / trailer by this front (index 0 == root)
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

            // Transform each section's connection bones, handing it its layout neighbours (the section ahead and behind)
            for (int i = 0; i < layout.Length; i++)
            {
                Entity previous = i > 0 ? layout[i - 1].m_Vehicle : Entity.Null;
                Entity current = layout[i].m_Vehicle;
                Entity next = i < layout.Length - 1 ? layout[i + 1].m_Vehicle : Entity.Null;

                TransformSectionConnectionBones(entityManager, previous, current, next);
            }
        }

        /// <summary>
        /// Check if any layout member other than the lead uses the given prefab
        /// </summary>
        /// <remarks>
        /// Guards step 2, confirming the front's fixed trailer prefab is actually present in the layout before bending
        /// </remarks>
        /// <returns>True if a non-lead member uses the prefab</returns>
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

        /// <summary>
        /// Transform one section's connection bones toward its layout neighbours
        /// </summary>
        /// <remarks>
        /// Pipeline step 3. previous/next are the front/trailer sections on each side. Ensures the runtime skeleton
        /// exists, reads the neighbour poses and cap rest positions, then solves each connection submesh
        /// </remarks>
        private void TransformSectionConnectionBones(EntityManager entityManager, Entity previous, Entity current, Entity next)
        {
            if (current == Entity.Null ||
                !entityManager.HasComponent<PrefabRef>(current) ||
                !entityManager.HasComponent<InterpolatedTransform>(current))
            {
                return;
            }

            PrefabRef currentPrefabRef = entityManager.GetComponentData<PrefabRef>(current);
            Entity currentPrefab = currentPrefabRef.m_Prefab;

            // Only if geometry component and submesh buffer exist
            if (!entityManager.HasComponent<ObjectGeometryData>(currentPrefab) ||
                !entityManager.HasBuffer<SubMesh>(currentPrefab))
            {
                return;
            }

            // Only if runtime Skeleton + Bone buffers are initialised
            EnsureVehicleRuntimeSkeletonInitialized(entityManager, current, currentPrefab);

            if (!entityManager.HasBuffer<Skeleton>(current) ||
                !entityManager.HasBuffer<Bone>(current))
            {
                return;
            }

            // Get layout neighbours' geometries and transforms
            ObjectGeometryData previousGeometry = default(ObjectGeometryData);
            ObjectGeometryData nextGeometry = default(ObjectGeometryData);

            ObjectTransform previousTransform = default(ObjectTransform);
            ObjectTransform currentTransform = entityManager.GetComponentData<InterpolatedTransform>(current).ToTransform();
            ObjectTransform nextTransform = default(ObjectTransform);

            TryGetLayoutNeighbor(entityManager, previous, out previousGeometry, out previousTransform);
            TryGetLayoutNeighbor(entityManager, next, out nextGeometry, out nextTransform);

            // Rest positions of each neighbour's cap bone (the outermost connection bone), in its object space
            float3 prevNeighborCapLocal = default(float3);
            float3 nextNeighborCapLocal = default(float3);
            bool hasPrevNeighborCap = previous != Entity.Null &&
                TryGetNeighborCapRestPositionLocal(entityManager, previous, previousTransform, currentTransform, out prevNeighborCapLocal);
            bool hasNextNeighborCap = next != Entity.Null &&
                TryGetNeighborCapRestPositionLocal(entityManager, next, nextTransform, currentTransform, out nextNeighborCapLocal);

            // Get skeleton, bones and submeshes
            DynamicBuffer<Skeleton> skeletons = entityManager.GetBuffer<Skeleton>(current);
            DynamicBuffer<Bone> bones = entityManager.GetBuffer<Bone>(current);
            DynamicBuffer<SubMesh> subMeshes = entityManager.GetBuffer<SubMesh>(currentPrefab);

            int skeletonCount = math.min(skeletons.Length, subMeshes.Length);
            for (int skeletonIndex = 0; skeletonIndex < skeletonCount; skeletonIndex++)
            {
                // Skip submeshes whose skeleton has no procedural bones
                ref Skeleton skeleton = ref skeletons.ElementAt(skeletonIndex);
                if (skeleton.m_BufferAllocation.Empty || skeleton.m_BoneOffset < 0)
                {
                    continue;
                }

                // Only if bone exists in submesh
                Entity subMeshEntity = subMeshes[skeletonIndex].m_SubMesh;
                if (!entityManager.HasBuffer<ProceduralBone>(subMeshEntity))
                {
                    continue;
                }

                DynamicBuffer<ProceduralBone> proceduralBones = entityManager.GetBuffer<ProceduralBone>(subMeshEntity);

                // Count this submesh's VehicleConnection bones (per LOD). Each gets local fraction 0.5/N and the
                // parent hierarchy accumulates them to the half-angle, so a reduced LOD (e.g. N=1) self-adapts
                int connectionBoneCount = CountVehicleConnectionBones(proceduralBones);
                if (connectionBoneCount == 0)
                {
                    continue;
                }

                // Bend this submesh's connection-bone chain toward the neighbour
                SolveConnectionChain(
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
                    nextNeighborCapLocal);
            }
        }

        /// <summary>
        /// Ensure runtime Skeleton and Bone buffers exist and match the prefab's procedural bones
        /// </summary>
        /// <remarks>
        /// Called by step 3 before solving. Mirrors vanilla ProceduralSkeletonSystem to write bone transforms. Needed
        /// because a trailer spawned from an archetype may not have been initialized yet, or the buffers exist but are
        /// the wrong size after a layout/mesh change
        /// </remarks>
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

            // Tally expected bone/layer counts across all submeshes and check whether the current buffers already match
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

            // Borrow vanilla's skeleton-matrix heap so the bones live in the same GPU buffer it uploads
            JobHandle heapDependencies;
            NativeReference<ProceduralSkeletonSystem.AllocationInfo> allocationInfo;
            NativeQueue<ProceduralSkeletonSystem.AllocationRemove> allocationRemoves;
            int currentTime;
            NativeHeapAllocator heapAllocator = m_ProceduralSkeletonSystem.GetHeapAllocator(out allocationInfo, out allocationRemoves, out currentTime, out heapDependencies);
            heapDependencies.Complete();

            DeallocateSkeletonBuffers(skeletons, allocationRemoves, currentTime);

            skeletons.ResizeUninitialized(subMeshes.Length);
            bones.ResizeUninitialized(totalBoneCount);

            // Reset Momentum (per-bone motion history) to track the new bone count
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

            // Resize the animation playback-layer buffer to match
            DynamicBuffer<PlaybackLayer> playbackLayers = default(DynamicBuffer<PlaybackLayer>);
            if (entityManager.HasBuffer<PlaybackLayer>(vehicle))
            {
                playbackLayers = entityManager.GetBuffer<PlaybackLayer>(vehicle);
                playbackLayers.ResizeUninitialized(playbackLayerCount);
            }

            // Build each submesh's skeleton by reserving a heap block and copying its prefab rest-pose bones into the runtime buffer
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

                // Reserve a heap block sized to this submesh's bone count, growing the heap if it is full
                DynamicBuffer<ProceduralBone> proceduralBones = entityManager.GetBuffer<ProceduralBone>(subMeshEntity);
                NativeHeapBlock bufferAllocation = heapAllocator.Allocate((uint)proceduralBones.Length);
                if (bufferAllocation.Empty)
                {
                    heapAllocator.Resize(heapAllocator.Size + 1048576u / SkeletonMatrixByteSize);
                    bufferAllocation = heapAllocator.Allocate((uint)proceduralBones.Length);
                }

                // Register the allocation in vanilla's bookkeeping
                ref ProceduralSkeletonSystem.AllocationInfo info = ref allocationInfo.ValueAsRef();
                info.m_AllocationCount++;

                // Point this submesh's skeleton at its heap block plus its bone/layer offsets
                Skeleton skeleton = new Skeleton
                {
                    m_BufferAllocation = bufferAllocation,
                    m_BoneOffset = boneOffset,
                    m_LayerOffset = layerOffset,
                    m_CurrentUpdated = true,
                    m_HistoryUpdated = true
                };

                // Copy each rest-pose bone into the runtime buffer and register its playback layer once
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

                    // Register this bone's playback layer (PlaybackLayer0..7 map to layers 0..7) the first time it appears
                    if (playbackLayers.IsCreated)
                    {
                        int layerIndex = proceduralBone.m_Type - FirstPlaybackLayerBone;
                        if (layerIndex >= 0 && layerIndex < PlaybackLayerCount)
                        {
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

            // Flag to game, that the skeleton heap was written so it syncs before the GPU upload
            m_ProceduralSkeletonSystem.AddHeapWriter(default(JobHandle));
        }

        /// <summary>
        /// Queue the existing skeleton heap allocations for release
        /// </summary>
        /// <remarks>
        /// Called by EnsureVehicleRuntimeSkeletonInitialized before it reallocates the heap blocks
        /// </remarks>
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

        /// <summary>
        /// Count the distinct playback layers used by a submesh's bones
        /// </summary>
        /// <remarks>
        /// PlaybackLayer0..7. Used while sizing the runtime skeleton's playback-layer buffer
        /// </remarks>
        /// <returns>The number of distinct playback layers in use</returns>
        private static int CountPlaybackLayers(DynamicBuffer<ProceduralBone> proceduralBones)
        {
            int usedMask = 0;
            int count = 0;
            for (int i = 0; i < proceduralBones.Length; i++)
            {
                int layerIndex = proceduralBones[i].m_Type - FirstPlaybackLayerBone;
                if (layerIndex < 0 || layerIndex >= PlaybackLayerCount)
                {
                    continue;
                }

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

        /// <summary>
        /// Read a layout neighbour's geometry and interpolated transform
        /// </summary>
        /// <remarks>
        /// Used by step 3 to gather each neighbour's pose before solving
        /// </remarks>
        /// <returns>False when the neighbour is null or lacks geometry/transform data</returns>
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

        /// <summary>
        /// Count the VehicleConnection bones in a submesh
        /// </summary>
        /// <remarks>
        /// The count sets each bone's share of the bend (see ArticulatedBusGeometryHelper.ConnectionBoneFraction)
        /// </remarks>
        /// <returns>The number of VehicleConnection bones</returns>
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

        /// <summary>
        /// Find the cap bone of a submesh
        /// </summary>
        /// <remarks>
        /// The cap bone is the outermost connection bone, the one with the largest |z|, at the section's outer edge
        /// closest to the layout neighbour
        /// </remarks>
        /// <returns>The cap bone index, or -1 if the submesh has no connection bones</returns>
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

        /// <summary>
        /// Compute a layout neighbour's cap-bone rest position in its object space
        /// </summary>
        /// <remarks>
        /// Both layout sections (front / trailer) read each other's cap-bone rest position and average the two, so they
        /// place the join between them at the same point
        /// </remarks>
        /// <returns>False if the neighbour has no readable connection bones</returns>
        private bool TryGetNeighborCapRestPositionLocal(
            EntityManager entityManager,
            Entity neighbor,
            ObjectTransform neighborTransform,
            ObjectTransform currentTransform,
            out float3 capRestLocal)
        {
            // Layout neighbour must exist and reference a prefab
            capRestLocal = default(float3);
            if (neighbor == Entity.Null || !entityManager.HasComponent<PrefabRef>(neighbor))
            {
                return false;
            }

            // Neighbour prefab must carry submeshes to read its bones from
            Entity neighborPrefab = entityManager.GetComponentData<PrefabRef>(neighbor).m_Prefab;
            if (!entityManager.HasBuffer<SubMesh>(neighborPrefab))
            {
                return false;
            }

            // Scan the neighbour's connection submesh and compute where its cap bone rests at the current angle/bend
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

                // get the rotation angle
                quaternion relative = math.mul(math.inverse(neighborTransform.m_Rotation), currentTransform.m_Rotation);
                quaternion neighborChainRotation = math.slerp(quaternion.identity, relative, ArticulatedBusGeometryHelper.ConnectionBoneFraction(n));
                capRestLocal = ComputeBoneObjectMatrix(neighborBones, neighborCapIndex, neighborChainRotation).c3.xyz;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Bend one submesh's connection-bone chain toward the neighbour
        /// </summary>
        /// <remarks>
        /// Pipeline step 4. Every connection bone gets the same local rotation slerp(identity, neighborRotation, 0.5/N)
        /// Only the cap bone (outermost bone) is additionally moved to match the world-space midpoint of the two layouts
        /// / front and trailer rest positions (symmetric, so both sections agree and the connection stays tight)
        /// </remarks>
        private void SolveConnectionChain(
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
            float3 nextNeighborCapLocal)
        {
            // Get cap bone (outermost connection bone)
            int capIndex = FindCapIndex(proceduralBones);
            if (capIndex < 0)
            {
                return;
            }

            ProceduralBone cap = proceduralBones[capIndex];
            quaternion inverseCurrentRotation = math.inverse(currentTransform.m_Rotation);
            quaternion neighborRotation;
            bool capUsesNextNeighbor; // which neighbour the cap bone meets (sets the join midpoint below)

            // Define which side the cap bone faces and the neighbour's relative rotation (see base game
            // AnimateVehicleConnectionBone). Reversed handling is omitted as bus sections are never layout-reversed
            if (cap.m_ObjectPosition.z < 0f)
            {
                capUsesNextNeighbor = true;
                if (nextGeometryData.m_Bounds.max.z == nextGeometryData.m_Bounds.min.z)
                {
                    ResetConnectionBones(proceduralBones, bones, ref skeleton);
                    return;
                }

                neighborRotation = math.mul(inverseCurrentRotation, nextTransform.m_Rotation);
            }
            else
            {
                capUsesNextNeighbor = false;
                if (previousGeometryData.m_Bounds.max.z == previousGeometryData.m_Bounds.min.z)
                {
                    ResetConnectionBones(proceduralBones, bones, ref skeleton);
                    return;
                }

                neighborRotation = math.mul(inverseCurrentRotation, previousTransform.m_Rotation);
            }

            float chainFraction = ArticulatedBusGeometryHelper.ConnectionBoneFraction(connectionBoneCount);
            quaternion chainRotation = math.slerp(quaternion.identity, neighborRotation, chainFraction);

            // Reposition the cap bone only if this side has a neighbour section to meet
            bool capHasNeighbor = capUsesNextNeighbor ? hasNextNeighborCap : hasPrevNeighborCap;
            float3 capTargetObject = default(float3);
            bool capRepositioned = false;

            if (capHasNeighbor)
            {
                // The neighbour section on the side the cap bone faces
                ObjectTransform neighborTransform = capUsesNextNeighbor ? nextTransform : previousTransform;
                float3 neighborCapLocal = capUsesNextNeighbor ? nextNeighborCapLocal : prevNeighborCapLocal;

                // This section's cap bone rest position (object space) at the current bend
                float3 myCapRestLocal = ComputeBoneObjectMatrix(proceduralBones, capIndex, chainRotation).c3.xyz;
                // ...taken to world space
                float3 myCapWorld = currentTransform.m_Position + math.rotate(currentTransform.m_Rotation, myCapRestLocal);
                // The neighbour's cap bone in world space
                float3 neighborCapWorld = neighborTransform.m_Position + math.rotate(neighborTransform.m_Rotation, neighborCapLocal);
                // Join point = midpoint of the two cap bones; both sections compute the same point
                float3 meetingWorld = ArticulatedBusGeometryHelper.CapMidpoint(myCapWorld, neighborCapWorld);

                capTargetObject = math.rotate(inverseCurrentRotation, meetingWorld - currentTransform.m_Position);
                capRepositioned = true;
            }

            // Write every connection bone: all share the chain rotation, only the cap bone is repositioned
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

                // Rest position by default; the cap bone is pulled to the join point (in its parent's local space)
                ProceduralBone pb = proceduralBones[i];
                float3 localPosition = pb.m_Position;
                if (i == capIndex && capRepositioned)
                {
                    // Convert the world meeting point into this cap bone's local space via its parent's accumulated matrix
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
        }

        /// <summary>
        /// Builds a bone's full object-space transform by walking up the bone hierarchy
        /// </summary>
        /// <remarks>
        /// Composes its own position/rotation/scale (a TRS matrix) with its parent's. Connection bones use the current
        /// chain (bend) rotation, all others use their authored rotation, so the result matches how the game builds its
        /// skin matrices
        /// </remarks>
        /// <returns>The bone's object-space transform matrix</returns>
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

        /// <summary>
        /// Reset a submesh's connection bones to their authored rest pose
        /// </summary>
        /// <remarks>
        /// Called by SolveConnectionChain when the neighbour on the cap-bone side has degenerate geometry, so the
        /// section stays rigid rather than bending toward nothing
        /// </remarks>
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
