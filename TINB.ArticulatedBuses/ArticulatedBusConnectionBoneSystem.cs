using Game;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Vehicles;
using Unity.Burst;
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
    /// The solve runs as a Burst job that takes the render culling list as an input dependency instead of blocking
    /// on it, and writes only the runtime Bone buffers. The runtime skeletons themselves are owned by vanilla:
    /// InitializeBonesSystem allocates them when a section's culling entry turns near-camera and clears them when it
    /// leaves, so a section whose skeleton is still empty is skipped and bends from the next frame on
    /// Per-frame pipeline:
    /// 1. OnUpdate schedules ConnectionBoneJob against the render culling list
    /// 2. ConnectionBoneJob.Execute picks the near-camera, multi-section, skeleton-bearing vehicles out of the list
    /// 3. TransformLayoutConnectionBones validates the bus and walks its layout, handing each section its layout neighbours
    /// 4. TransformSectionConnectionBones reads the neighbour poses and cap rest positions, then solves each connection submesh
    /// 5. SolveConnectionChain rotates the connection-bone chain toward the neighbour and pulls the cap (outmost) bone to the
    ///    shared join point
    /// </remarks>
    public sealed partial class ArticulatedBusConnectionBoneSystem : GameSystemBase
    {
        private PreCullingSystem m_PreCullingSystem = null!;

        /// <summary>
        /// Caches the vanilla culling system
        /// </summary>
        protected override void OnCreate()
        {
            base.OnCreate();
            m_PreCullingSystem = World.GetOrCreateSystemManaged<PreCullingSystem>();
        }

        /// <summary>
        /// Schedule the connection-bone solve for every articulated bus this frame
        /// </summary>
        /// <remarks>
        /// Pipeline step 1. The culling list's build job becomes an input dependency of the solve, and the solve is
        /// registered as a reader of the list, mirroring vanilla InitializeBonesSystem. Nothing is completed on the
        /// main thread
        /// </remarks>
        protected override void OnUpdate()
        {
            if (!Mod.IsInGame())
            {
                return;
            }

            NativeList<PreCullingData> cullingData = m_PreCullingSystem.GetCullingData(readOnly: true, out JobHandle cullingDeps);

            ConnectionBoneJob job = new ConnectionBoneJob
            {
                m_CullingData = cullingData,
                m_CarData = GetComponentLookup<Car>(isReadOnly: true),
                m_PublicTransportData = GetComponentLookup<VehiclePublicTransport>(isReadOnly: true),
                m_PrefabRefData = GetComponentLookup<PrefabRef>(isReadOnly: true),
                m_CarTractorData = GetComponentLookup<CarTractorData>(isReadOnly: true),
                m_ObjectGeometryData = GetComponentLookup<ObjectGeometryData>(isReadOnly: true),
                m_InterpolatedTransformData = GetComponentLookup<InterpolatedTransform>(isReadOnly: true),
                m_LayoutElements = GetBufferLookup<LayoutElement>(isReadOnly: true),
                m_SubMeshes = GetBufferLookup<SubMesh>(isReadOnly: true),
                m_ProceduralBones = GetBufferLookup<ProceduralBone>(isReadOnly: true),
                m_Skeletons = GetBufferLookup<Skeleton>(isReadOnly: false),
                m_Bones = GetBufferLookup<Bone>(isReadOnly: false)
            };

            JobHandle jobHandle = job.Schedule(JobHandle.CombineDependencies(Dependency, cullingDeps));
            m_PreCullingSystem.AddCullingDataReader(jobHandle);
            Dependency = jobHandle;
        }

        /// <summary>
        /// Solve the connection-bone chains of every near-camera articulated bus
        /// </summary>
        /// <remarks>
        /// A single-threaded Burst job over the render culling list, like vanilla InitializeBonesSystem. Only the
        /// handful of near-camera buses ever reach the solve, so the list walk is the cheap part. Jobs cannot make
        /// structural changes, so the layout and prefab buffers stay valid for the whole walk
        /// </remarks>
        [BurstCompile]
        private struct ConnectionBoneJob : IJob
        {
            [ReadOnly] public NativeList<PreCullingData> m_CullingData;
            [ReadOnly] public ComponentLookup<Car> m_CarData;
            [ReadOnly] public ComponentLookup<VehiclePublicTransport> m_PublicTransportData;
            [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRefData;
            [ReadOnly] public ComponentLookup<CarTractorData> m_CarTractorData;
            [ReadOnly] public ComponentLookup<ObjectGeometryData> m_ObjectGeometryData;
            [ReadOnly] public ComponentLookup<InterpolatedTransform> m_InterpolatedTransformData;
            [ReadOnly] public BufferLookup<LayoutElement> m_LayoutElements;
            [ReadOnly] public BufferLookup<SubMesh> m_SubMeshes;
            [ReadOnly] public BufferLookup<ProceduralBone> m_ProceduralBones;
            public BufferLookup<Skeleton> m_Skeletons;
            public BufferLookup<Bone> m_Bones;

            /// <summary>
            /// Walk the culling list and solve every near-camera, multi-section, skeleton-bearing vehicle
            /// </summary>
            /// <remarks>
            /// Pipeline step 2
            /// </remarks>
            public void Execute()
            {
                const PreCullingFlags required = PreCullingFlags.NearCamera | PreCullingFlags.VehicleLayout | PreCullingFlags.Skeleton;

                for (int i = 0; i < m_CullingData.Length; i++)
                {
                    PreCullingData entry = m_CullingData[i];
                    if ((entry.m_Flags & required) != required)
                    {
                        continue;
                    }

                    TransformLayoutConnectionBones(entry.m_Entity);
                }
            }

            /// <summary>
            /// Validate that root is an articulated bus, then transform the connection bones of every section in its layout
            /// </summary>
            /// <remarks>
            /// Pipeline step 3. Each section (front or trailer) is passed its layout neighbours (the section ahead or
            /// behind). Bails out unless root leads a real multi-section layout whose fixed trailer prefab is present
            /// </remarks>
            private void TransformLayoutConnectionBones(Entity root)
            {
                if (root == Entity.Null ||
                    !m_CarData.HasComponent(root) ||
                    !m_PublicTransportData.HasComponent(root) ||
                    !m_InterpolatedTransformData.HasComponent(root) ||
                    !m_PrefabRefData.TryGetComponent(root, out PrefabRef rootPrefabRef) ||
                    !m_LayoutElements.TryGetBuffer(root, out DynamicBuffer<LayoutElement> layout))
                {
                    return;
                }

                // Require a real multi-section layout / trailer by this front (index 0 == root)
                if (layout.Length < 2 || layout[0].m_Vehicle != root)
                {
                    return;
                }

                if (!m_CarTractorData.TryGetComponent(rootPrefabRef.m_Prefab, out CarTractorData tractorData) ||
                    tractorData.m_FixedTrailer == Entity.Null ||
                    !LayoutContainsPrefab(layout, tractorData.m_FixedTrailer))
                {
                    return;
                }

                // Transform each section's connection bones, handing it its layout neighbours (the section ahead and behind)
                for (int i = 0; i < layout.Length; i++)
                {
                    Entity previous = i > 0 ? layout[i - 1].m_Vehicle : Entity.Null;
                    Entity current = layout[i].m_Vehicle;
                    Entity next = i < layout.Length - 1 ? layout[i + 1].m_Vehicle : Entity.Null;

                    TransformSectionConnectionBones(previous, current, next);
                }
            }

            /// <summary>
            /// Check if any layout member other than the lead uses the given prefab
            /// </summary>
            /// <remarks>
            /// Guards step 3, confirming the front's fixed trailer prefab is actually present in the layout before bending
            /// </remarks>
            /// <returns>True if a non-lead member uses the prefab</returns>
            private bool LayoutContainsPrefab(DynamicBuffer<LayoutElement> layout, Entity prefab)
            {
                for (int i = 1; i < layout.Length; i++)
                {
                    Entity vehicle = layout[i].m_Vehicle;
                    if (vehicle != Entity.Null &&
                        m_PrefabRefData.TryGetComponent(vehicle, out PrefabRef prefabRef) &&
                        prefabRef.m_Prefab == prefab)
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
            /// Pipeline step 4. previous/next are the front/trailer sections on each side. Skips a section whose runtime
            /// skeleton vanilla has not initialised yet (empty buffers), reads the neighbour poses and cap rest positions,
            /// then solves each connection submesh
            /// </remarks>
            private void TransformSectionConnectionBones(Entity previous, Entity current, Entity next)
            {
                if (current == Entity.Null ||
                    !m_PrefabRefData.TryGetComponent(current, out PrefabRef currentPrefabRef) ||
                    !m_InterpolatedTransformData.TryGetComponent(current, out InterpolatedTransform currentInterpolated))
                {
                    return;
                }

                Entity currentPrefab = currentPrefabRef.m_Prefab;

                // Only if geometry, submeshes and the vanilla-owned runtime skeleton buffers exist
                if (!m_ObjectGeometryData.HasComponent(currentPrefab) ||
                    !m_SubMeshes.TryGetBuffer(currentPrefab, out DynamicBuffer<SubMesh> subMeshes) ||
                    !m_Skeletons.TryGetBuffer(current, out DynamicBuffer<Skeleton> skeletons) ||
                    !m_Bones.TryGetBuffer(current, out DynamicBuffer<Bone> bones))
                {
                    return;
                }

                // Get layout neighbours' geometries and transforms
                ObjectTransform currentTransform = currentInterpolated.ToTransform();
                TryGetLayoutNeighbor(previous, out ObjectGeometryData previousGeometry, out ObjectTransform previousTransform);
                TryGetLayoutNeighbor(next, out ObjectGeometryData nextGeometry, out ObjectTransform nextTransform);

                // Rest positions of each neighbour's cap bone (the outermost connection bone), in its object space
                float3 prevNeighborCapLocal = default(float3);
                float3 nextNeighborCapLocal = default(float3);
                bool hasPrevNeighborCap = previous != Entity.Null &&
                    TryGetNeighborCapRestPositionLocal(previous, previousTransform, currentTransform, out prevNeighborCapLocal);
                bool hasNextNeighborCap = next != Entity.Null &&
                    TryGetNeighborCapRestPositionLocal(next, nextTransform, currentTransform, out nextNeighborCapLocal);

                // An empty skeleton (not yet initialised by vanilla, or cleared after leaving near-camera) yields zero iterations
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
                    if (!m_ProceduralBones.TryGetBuffer(subMeshes[skeletonIndex].m_SubMesh, out DynamicBuffer<ProceduralBone> proceduralBones))
                    {
                        continue;
                    }

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
            /// Read a layout neighbour's geometry and interpolated transform
            /// </summary>
            /// <remarks>
            /// Used by step 4 to gather each neighbour's pose before solving. A missing neighbour leaves the geometry
            /// degenerate (zero-length bounds), which SolveConnectionChain reads as "no neighbour on that side"
            /// </remarks>
            /// <returns>False when the neighbour is null or lacks geometry/transform data</returns>
            private bool TryGetLayoutNeighbor(Entity vehicle, out ObjectGeometryData geometryData, out ObjectTransform transform)
            {
                geometryData = default(ObjectGeometryData);
                transform = default(ObjectTransform);

                if (vehicle == Entity.Null ||
                    !m_PrefabRefData.TryGetComponent(vehicle, out PrefabRef prefabRef) ||
                    !m_InterpolatedTransformData.TryGetComponent(vehicle, out InterpolatedTransform interpolated) ||
                    !m_ObjectGeometryData.TryGetComponent(prefabRef.m_Prefab, out geometryData))
                {
                    return false;
                }

                transform = interpolated.ToTransform();
                return true;
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
                Entity neighbor,
                ObjectTransform neighborTransform,
                ObjectTransform currentTransform,
                out float3 capRestLocal)
            {
                // Layout neighbour must exist, reference a prefab, and that prefab must carry submeshes to read its bones from
                capRestLocal = default(float3);
                if (neighbor == Entity.Null ||
                    !m_PrefabRefData.TryGetComponent(neighbor, out PrefabRef neighborPrefabRef) ||
                    !m_SubMeshes.TryGetBuffer(neighborPrefabRef.m_Prefab, out DynamicBuffer<SubMesh> neighborSubMeshes))
                {
                    return false;
                }

                // Scan the neighbour's connection submesh and compute where its cap bone rests at the current angle/bend
                for (int i = 0; i < neighborSubMeshes.Length; i++)
                {
                    if (!m_ProceduralBones.TryGetBuffer(neighborSubMeshes[i].m_SubMesh, out DynamicBuffer<ProceduralBone> neighborBones))
                    {
                        continue;
                    }

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
        /// Bend one submesh's connection-bone chain toward the neighbour
        /// </summary>
        /// <remarks>
        /// Pipeline step 5. Every connection bone gets the same local rotation slerp(identity, neighborRotation, 0.5/N)
        /// Only the cap bone (outermost bone) is additionally moved to match the world-space midpoint of the two layouts
        /// / front and trailer rest positions (symmetric, so both sections agree and the connection stays tight)
        /// </remarks>
        private static void SolveConnectionChain(
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
        /// Walks the parent chain iteratively, like the game's own bone hierarchy walks (ObjectInterpolateSystem
        /// LocalToWorld / LocalToObject). The step cap bounds a malformed third-party rig whose parent indices form a
        /// cycle: a well-formed hierarchy is never deeper than it has bones
        /// </remarks>
        /// <returns>The bone's object-space transform matrix</returns>
        private static float4x4 ComputeBoneObjectMatrix(DynamicBuffer<ProceduralBone> proceduralBones, int index, quaternion chainRotation)
        {
            float4x4 objectMatrix = float4x4.identity;

            for (int step = 0; step < proceduralBones.Length; step++)
            {
                if (index < 0 || index >= proceduralBones.Length)
                {
                    break;
                }

                ProceduralBone bone = proceduralBones[index];
                quaternion rotation = bone.m_Type == BoneType.VehicleConnection ? chainRotation : bone.m_Rotation;
                float4x4 local = float4x4.TRS(bone.m_Position, rotation, bone.m_Scale);

                // Parent-first composition, so the accumulated matrix stays parent * ... * self
                objectMatrix = math.mul(local, objectMatrix);
                index = bone.m_ParentIndex;
            }

            return objectMatrix;
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
