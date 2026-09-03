using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using ObjectTransform = Game.Objects.Transform;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Calculates connection-bone transforms for articulated buses after vanilla vehicle interpolation.
    /// </summary>
    /// <remarks>
    /// Bone solving runs as a Burst IJobChunk. The job reads vanilla interpolation, layout and prefab data and writes
    /// only the runtime Skeleton/Bone buffers owned by the section in its current chunk. Entities whose vanilla
    /// runtime skeleton has not been initialized yet are not in the query and are picked up on a later frame.
    /// </remarks>
    public sealed partial class ArticulatedBusConnectionBoneSystem : GameSystemBase
    {
        private PreCullingSystem m_PreCullingSystem = null!;
        private EntityQuery m_BusSectionQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PreCullingSystem = World.GetOrCreateSystemManaged<PreCullingSystem>();

            // Process both the front and its trailer sections. Requiring Skeleton/Bone makes the job operate only on
            // runtime skeletons already initialized by vanilla; newly created sections simply join on a later frame.
            m_BusSectionQuery = GetEntityQuery(
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<Car>(),
                        ComponentType.ReadOnly<VehiclePublicTransport>(),
                        ComponentType.ReadOnly<LayoutElement>(),
                        ComponentType.ReadOnly<PrefabRef>(),
                        ComponentType.ReadOnly<InterpolatedTransform>(),
                        ComponentType.ReadOnly<CullingInfo>(),
                        ComponentType.ReadWrite<Skeleton>(),
                        ComponentType.ReadWrite<Bone>()
                    },
                    None = new[]
                    {
                        ComponentType.ReadOnly<Deleted>(),
                        ComponentType.ReadOnly<Temp>(),
                        ComponentType.ReadOnly<CarTrailer>()
                    }
                },
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<CarTrailer>(),
                        ComponentType.ReadOnly<Controller>(),
                        ComponentType.ReadOnly<PrefabRef>(),
                        ComponentType.ReadOnly<InterpolatedTransform>(),
                        ComponentType.ReadWrite<Skeleton>(),
                        ComponentType.ReadWrite<Bone>()
                    },
                    None = new[]
                    {
                        ComponentType.ReadOnly<Deleted>(),
                        ComponentType.ReadOnly<Temp>()
                    }
                });

            RequireForUpdate(m_BusSectionQuery);
        }

        protected override void OnUpdate()
        {
            if (!Mod.IsInGame())
            {
                return;
            }

            NativeList<PreCullingData> cullingData =
                m_PreCullingSystem.GetCullingData(readOnly: true, out JobHandle cullingDependencies);

            ConnectionBoneJob job = new ConnectionBoneJob
            {
                EntityType = GetEntityTypeHandle(),
                CullingData = cullingData,
                CullingInfos = GetComponentLookup<CullingInfo>(isReadOnly: true),
                Cars = GetComponentLookup<Car>(isReadOnly: true),
                PublicTransports = GetComponentLookup<VehiclePublicTransport>(isReadOnly: true),
                Controllers = GetComponentLookup<Controller>(isReadOnly: true),
                PrefabRefs = GetComponentLookup<PrefabRef>(isReadOnly: true),
                TractorData = GetComponentLookup<CarTractorData>(isReadOnly: true),
                GeometryData = GetComponentLookup<ObjectGeometryData>(isReadOnly: true),
                InterpolatedTransforms = GetComponentLookup<InterpolatedTransform>(isReadOnly: true),
                Layouts = GetBufferLookup<LayoutElement>(isReadOnly: true),
                SubMeshes = GetBufferLookup<SubMesh>(isReadOnly: true),
                ProceduralBones = GetBufferLookup<ProceduralBone>(isReadOnly: true),
                SkeletonType = GetBufferTypeHandle<Skeleton>(isReadOnly: false),
                BoneBufferType = GetBufferTypeHandle<Bone>(isReadOnly: false)
            };

            JobHandle inputDependencies = JobHandle.CombineDependencies(Dependency, cullingDependencies);
            Dependency = job.ScheduleParallel(m_BusSectionQuery, inputDependencies);
            m_PreCullingSystem.AddCullingDataReader(Dependency);
        }

        [BurstCompile]
        private struct ConnectionBoneJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle EntityType;
            [ReadOnly] public NativeList<PreCullingData> CullingData;
            [ReadOnly] public ComponentLookup<CullingInfo> CullingInfos;
            [ReadOnly] public ComponentLookup<Car> Cars;
            [ReadOnly] public ComponentLookup<VehiclePublicTransport> PublicTransports;
            [ReadOnly] public ComponentLookup<Controller> Controllers;
            [ReadOnly] public ComponentLookup<PrefabRef> PrefabRefs;
            [ReadOnly] public ComponentLookup<CarTractorData> TractorData;
            [ReadOnly] public ComponentLookup<ObjectGeometryData> GeometryData;
            [ReadOnly] public ComponentLookup<InterpolatedTransform> InterpolatedTransforms;
            [ReadOnly] public BufferLookup<LayoutElement> Layouts;
            [ReadOnly] public BufferLookup<SubMesh> SubMeshes;
            [ReadOnly] public BufferLookup<ProceduralBone> ProceduralBones;
            public BufferTypeHandle<Skeleton> SkeletonType;
            public BufferTypeHandle<Bone> BoneBufferType;

            [BurstCompile]
            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(EntityType);
                BufferAccessor<Skeleton> skeletonAccessor = chunk.GetBufferAccessor(ref SkeletonType);
                BufferAccessor<Bone> boneAccessor = chunk.GetBufferAccessor(ref BoneBufferType);

                for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
                {
                    Entity section = entities[entityIndex];
                    if (!TryGetFrontAndLayout(section, out Entity front, out DynamicBuffer<LayoutElement> layout) ||
                        !IsVisibleArticulatedBus(front, layout))
                    {
                        continue;
                    }

                    int sectionIndex = FindSectionIndex(layout, section);
                    if (sectionIndex < 0)
                    {
                        continue;
                    }

                    AnimateSection(
                        layout,
                        sectionIndex,
                        section,
                        skeletonAccessor[entityIndex],
                        boneAccessor[entityIndex]);
                }
            }

            private bool TryGetFrontAndLayout(
                Entity section,
                out Entity front,
                out DynamicBuffer<LayoutElement> layout)
            {
                front = section;
                if (Layouts.TryGetBuffer(section, out layout) &&
                    layout.Length > 0 &&
                    layout[0].m_Vehicle == section)
                {
                    return true;
                }

                if (!Controllers.TryGetComponent(section, out Controller controller) ||
                    controller.m_Controller == Entity.Null ||
                    !Layouts.TryGetBuffer(controller.m_Controller, out layout))
                {
                    return false;
                }

                front = controller.m_Controller;
                return true;
            }

            private bool IsVisibleArticulatedBus(Entity front, DynamicBuffer<LayoutElement> layout)
            {
                if (!Cars.HasComponent(front) ||
                    !PublicTransports.HasComponent(front) ||
                    layout.Length < 2 ||
                    layout[0].m_Vehicle != front ||
                    !PrefabRefs.TryGetComponent(front, out PrefabRef frontPrefabRef) ||
                    !TractorData.TryGetComponent(frontPrefabRef.m_Prefab, out CarTractorData tractorData) ||
                    tractorData.m_FixedTrailer == Entity.Null ||
                    !LayoutContainsPrefab(layout, tractorData.m_FixedTrailer))
                {
                    return false;
                }

                if (!CullingInfos.TryGetComponent(front, out CullingInfo cullingInfo) ||
                    cullingInfo.m_CullingIndex < 0 ||
                    cullingInfo.m_CullingIndex >= CullingData.Length)
                {
                    return false;
                }

                PreCullingData entry = CullingData[cullingInfo.m_CullingIndex];
                const PreCullingFlags required =
                    PreCullingFlags.NearCamera | PreCullingFlags.VehicleLayout | PreCullingFlags.Skeleton;
                return entry.m_Entity == front && (entry.m_Flags & required) == required;
            }

            private bool LayoutContainsPrefab(DynamicBuffer<LayoutElement> layout, Entity prefab)
            {
                for (int i = 1; i < layout.Length; i++)
                {
                    Entity vehicle = layout[i].m_Vehicle;
                    if (vehicle != Entity.Null &&
                        PrefabRefs.TryGetComponent(vehicle, out PrefabRef prefabRef) &&
                        prefabRef.m_Prefab == prefab)
                    {
                        return true;
                    }
                }

                return false;
            }

            private static int FindSectionIndex(DynamicBuffer<LayoutElement> layout, Entity section)
            {
                for (int i = 0; i < layout.Length; i++)
                {
                    if (layout[i].m_Vehicle == section)
                    {
                        return i;
                    }
                }

                return -1;
            }

            private void AnimateSection(
                DynamicBuffer<LayoutElement> layout,
                int sectionIndex,
                Entity section,
                DynamicBuffer<Skeleton> skeletons,
                DynamicBuffer<Bone> bones)
            {
                if (!TryGetSectionState(
                        section,
                        out ObjectTransform currentTransform,
                        out Entity currentPrefab,
                        out ObjectGeometryData _))
                {
                    return;
                }

                Entity previousPrefab = Entity.Null;
                Entity nextPrefab = Entity.Null;
                ObjectGeometryData previousGeometry = default(ObjectGeometryData);
                ObjectGeometryData nextGeometry = default(ObjectGeometryData);
                ObjectTransform previousTransform = default(ObjectTransform);
                ObjectTransform nextTransform = default(ObjectTransform);

                bool hasPrevious = sectionIndex > 0 &&
                    TryGetSectionState(
                        layout[sectionIndex - 1].m_Vehicle,
                        out previousTransform,
                        out previousPrefab,
                        out previousGeometry);
                bool hasNext = sectionIndex + 1 < layout.Length &&
                    TryGetSectionState(
                        layout[sectionIndex + 1].m_Vehicle,
                        out nextTransform,
                        out nextPrefab,
                        out nextGeometry);

                if (!SubMeshes.TryGetBuffer(currentPrefab, out DynamicBuffer<SubMesh> subMeshes))
                {
                    return;
                }

                float3 previousCapLocal = default(float3);
                float3 nextCapLocal = default(float3);
                bool hasPreviousCap = hasPrevious &&
                    TryGetNeighborCapRestPositionLocal(
                        previousPrefab,
                        previousTransform,
                        currentTransform,
                        out previousCapLocal);
                bool hasNextCap = hasNext &&
                    TryGetNeighborCapRestPositionLocal(
                        nextPrefab,
                        nextTransform,
                        currentTransform,
                        out nextCapLocal);

                int skeletonCount = math.min(skeletons.Length, subMeshes.Length);
                for (int skeletonIndex = 0; skeletonIndex < skeletonCount; skeletonIndex++)
                {
                    ref Skeleton skeleton = ref skeletons.ElementAt(skeletonIndex);
                    if (skeleton.m_BufferAllocation.Empty || skeleton.m_BoneOffset < 0 ||
                        !ProceduralBones.TryGetBuffer(
                            subMeshes[skeletonIndex].m_SubMesh,
                            out DynamicBuffer<ProceduralBone> proceduralBones))
                    {
                        continue;
                    }

                    int connectionBoneCount = CountVehicleConnectionBones(proceduralBones);
                    if (connectionBoneCount == 0)
                    {
                        continue;
                    }

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
                        hasPreviousCap,
                        previousCapLocal,
                        hasNextCap,
                        nextCapLocal);
                }
            }

            private bool TryGetSectionState(
                Entity vehicle,
                out ObjectTransform transform,
                out Entity prefab,
                out ObjectGeometryData geometry)
            {
                transform = default(ObjectTransform);
                prefab = Entity.Null;
                geometry = default(ObjectGeometryData);

                if (vehicle == Entity.Null ||
                    !PrefabRefs.TryGetComponent(vehicle, out PrefabRef prefabRef) ||
                    !InterpolatedTransforms.TryGetComponent(vehicle, out InterpolatedTransform interpolated) ||
                    !GeometryData.TryGetComponent(prefabRef.m_Prefab, out geometry))
                {
                    return false;
                }

                prefab = prefabRef.m_Prefab;
                transform = interpolated.ToTransform();
                return true;
            }

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

            private bool TryGetNeighborCapRestPositionLocal(
                Entity neighborPrefab,
                ObjectTransform neighborTransform,
                ObjectTransform currentTransform,
                out float3 capRestLocal)
            {
                capRestLocal = default(float3);
                if (neighborPrefab == Entity.Null ||
                    !SubMeshes.TryGetBuffer(neighborPrefab, out DynamicBuffer<SubMesh> neighborSubMeshes))
                {
                    return false;
                }

                for (int i = 0; i < neighborSubMeshes.Length; i++)
                {
                    if (!ProceduralBones.TryGetBuffer(
                            neighborSubMeshes[i].m_SubMesh,
                            out DynamicBuffer<ProceduralBone> neighborBones))
                    {
                        continue;
                    }

                    int connectionBoneCount = CountVehicleConnectionBones(neighborBones);
                    int capIndex = FindCapIndex(neighborBones);
                    if (connectionBoneCount == 0 || capIndex < 0)
                    {
                        continue;
                    }

                    quaternion relative =
                        math.mul(math.inverse(neighborTransform.m_Rotation), currentTransform.m_Rotation);
                    quaternion chainRotation = math.slerp(
                        quaternion.identity,
                        relative,
                        ArticulatedBusGeometryHelper.ConnectionBoneFraction(connectionBoneCount));
                    capRestLocal = ComputeBoneObjectMatrix(neighborBones, capIndex, chainRotation).c3.xyz;
                    return true;
                }

                return false;
            }

            private static void SolveConnectionChain(
                DynamicBuffer<ProceduralBone> proceduralBones,
                DynamicBuffer<Bone> bones,
                ref Skeleton skeleton,
                ObjectGeometryData previousGeometry,
                ObjectGeometryData nextGeometry,
                ObjectTransform previousTransform,
                ObjectTransform currentTransform,
                ObjectTransform nextTransform,
                int connectionBoneCount,
                bool hasPreviousCap,
                float3 previousCapLocal,
                bool hasNextCap,
                float3 nextCapLocal)
            {
                int capIndex = FindCapIndex(proceduralBones);
                if (capIndex < 0)
                {
                    return;
                }

                ProceduralBone cap = proceduralBones[capIndex];
                quaternion inverseCurrentRotation = math.inverse(currentTransform.m_Rotation);
                quaternion neighborRotation;
                bool capUsesNextNeighbor;

                if (cap.m_ObjectPosition.z < 0f)
                {
                    capUsesNextNeighbor = true;
                    if (nextGeometry.m_Bounds.max.z == nextGeometry.m_Bounds.min.z)
                    {
                        ResetConnectionBones(proceduralBones, bones, ref skeleton);
                        return;
                    }

                    neighborRotation = math.mul(inverseCurrentRotation, nextTransform.m_Rotation);
                }
                else
                {
                    capUsesNextNeighbor = false;
                    if (previousGeometry.m_Bounds.max.z == previousGeometry.m_Bounds.min.z)
                    {
                        ResetConnectionBones(proceduralBones, bones, ref skeleton);
                        return;
                    }

                    neighborRotation = math.mul(inverseCurrentRotation, previousTransform.m_Rotation);
                }

                quaternion chainRotation = math.slerp(
                    quaternion.identity,
                    neighborRotation,
                    ArticulatedBusGeometryHelper.ConnectionBoneFraction(connectionBoneCount));

                bool capHasNeighbor = capUsesNextNeighbor ? hasNextCap : hasPreviousCap;
                float3 capTargetObject = default(float3);
                bool capRepositioned = false;

                if (capHasNeighbor)
                {
                    ObjectTransform neighborTransform = capUsesNextNeighbor ? nextTransform : previousTransform;
                    float3 neighborCapLocal = capUsesNextNeighbor ? nextCapLocal : previousCapLocal;

                    float3 myCapRestLocal =
                        ComputeBoneObjectMatrix(proceduralBones, capIndex, chainRotation).c3.xyz;
                    float3 myCapWorld = currentTransform.m_Position +
                                        math.rotate(currentTransform.m_Rotation, myCapRestLocal);
                    float3 neighborCapWorld = neighborTransform.m_Position +
                                              math.rotate(neighborTransform.m_Rotation, neighborCapLocal);
                    float3 meetingWorld = ArticulatedBusGeometryHelper.CapMidpoint(myCapWorld, neighborCapWorld);

                    capTargetObject = math.rotate(
                        inverseCurrentRotation,
                        meetingWorld - currentTransform.m_Position);
                    capRepositioned = true;
                }

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

                    float3 localPosition = proceduralBone.m_Position;
                    if (i == capIndex && capRepositioned)
                    {
                        int parentIndex = proceduralBone.m_ParentIndex;
                        float4x4 parentObjectMatrix =
                            parentIndex < 0 || parentIndex >= proceduralBones.Length
                                ? float4x4.identity
                                : ComputeBoneObjectMatrix(proceduralBones, parentIndex, chainRotation);
                        localPosition = math.transform(math.inverse(parentObjectMatrix), capTargetObject);
                    }

                    ref Bone bone = ref bones.ElementAt(runtimeBoneIndex);
                    skeleton.m_CurrentUpdated |=
                        !bone.m_Position.Equals(localPosition) | !bone.m_Rotation.Equals(chainRotation);
                    bone.m_Position = localPosition;
                    bone.m_Rotation = chainRotation;
                }
            }

            private static float4x4 ComputeBoneObjectMatrix(
                DynamicBuffer<ProceduralBone> proceduralBones,
                int boneIndex,
                quaternion connectionRotation)
            {
                // Iterative hierarchy walk: Burst jobs cannot use the original recursive helper.
                FixedList512Bytes<int> hierarchy = default(FixedList512Bytes<int>);
                int index = boneIndex;
                int remaining = proceduralBones.Length;
                while (index >= 0 &&
                       index < proceduralBones.Length &&
                       remaining-- > 0 &&
                       hierarchy.Length < hierarchy.Capacity)
                {
                    hierarchy.Add(in index);
                    index = proceduralBones[index].m_ParentIndex;
                }

                float4x4 result = float4x4.identity;
                for (int i = hierarchy.Length - 1; i >= 0; i--)
                {
                    ProceduralBone bone = proceduralBones[hierarchy[i]];
                    quaternion rotation = bone.m_Type == BoneType.VehicleConnection
                        ? connectionRotation
                        : bone.m_Rotation;
                    result = math.mul(result, float4x4.TRS(bone.m_Position, rotation, bone.m_Scale));
                }

                return result;
            }

            private static void ResetConnectionBones(
                DynamicBuffer<ProceduralBone> proceduralBones,
                DynamicBuffer<Bone> bones,
                ref Skeleton skeleton)
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
                    skeleton.m_CurrentUpdated |=
                        !bone.m_Position.Equals(proceduralBone.m_Position) |
                        !bone.m_Rotation.Equals(proceduralBone.m_Rotation);
                    bone.m_Position = proceduralBone.m_Position;
                    bone.m_Rotation = proceduralBone.m_Rotation;
                }
            }
        }
    }
}
