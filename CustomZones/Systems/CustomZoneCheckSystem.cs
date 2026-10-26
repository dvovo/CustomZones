// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace CustomZones.Systems
{
    using System.Collections.Generic;
    using Colossal.Collections;
    using Colossal.Entities;
    using Colossal.Mathematics;
    using Game;
    using Game.Buildings;
    using Game.Common;
    using Game.Notifications;
    using Game.Objects;
    using Game.Prefabs;
    using Game.Tools;
    using Game.Zones;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Mathematics;
    using CustomZones.Domain;
    using CustomZones.Storage;
    using Transform = Game.Objects.Transform;

    /// <summary>
    /// The game's zone check (which condemns a growable building when the zone
    /// under it changes), aware of custom zones.
    /// </summary>
    /// <remarks>
    /// The vanilla ZoneCheckSystem compares every cell under a building with the
    /// building's own zone type. Buildings grown on a custom zone keep their own
    /// (vanilla) zone, so it would condemn all of them; it is disabled in
    /// Mod.OnLoad and this system takes its place. It is the same check, step by
    /// step (the game's own jobs find the buildings to check), with one rule
    /// added: a custom-zone cell also counts as the zone of the buildings in that
    /// custom zone's catalog (any of their levels, any type). Vanilla zones keep exactly the game's behaviour.
    /// The jobs mirror the game's own (they are private in ZoneCheckSystem).
    /// <para>
    /// Editing a custom zone's catalog re-checks every building standing on a
    /// custom zone (debounced): buildings removed from the catalog are condemned,
    /// buildings added back are pardoned, like repainting the zone would do.
    /// </para>
    /// <para>
    /// Buildings placed by hand (<see cref="HandPlaced"/>: the game's placement
    /// tool, Find It...) are left alone while the zones under them stay the ones
    /// they were placed on. When those zones change, the rules above apply to
    /// them again, like to any other building.
    /// </para>
    /// </remarks>
    public partial class CustomZoneCheckSystem : GameSystemBase
    {
        private Game.Zones.UpdateCollectSystem m_ZoneUpdateCollectSystem;
        private Game.Zones.SearchSystem m_ZoneSearchSystem;
        private Game.Objects.SearchSystem m_ObjectSearchSystem;
        private ModificationEndBarrier m_ModificationEndBarrier;
        private ToolSystem m_ToolSystem;
        private IconCommandSystem m_IconCommandSystem;
        private PaletteZoneSystem m_PaletteZoneSystem;
        private BuildingIndexSystem m_IndexSystem;
        private EntityQuery m_BuildingSettingsQuery;

        // Frames to wait after the last catalog edit before re-checking the city.
        private const int FullCheckDelayFrames = 30;

        // (custom zone index << 16 | vanilla zone index) pairs that count as the same zone.
        private NativeParallelHashSet<int> m_Equivalent;

        // (custom zone index << 32 | building prefab index): buildings in a custom zone's catalog.
        private NativeParallelHashSet<long> m_Allowed;

        // Zone type indices of the custom zones.
        private NativeParallelHashSet<int> m_CustomZones;

        private JobHandle m_TableReaders;
        private int m_TablesIndexVersion = -1;
        private int m_TablesStorageVersion = -1;
        private int m_TablesZoneCount = -1;
        private int m_Frame;
        private int m_FullCheckIn = -1;
        private EntityQuery m_AllBuildingsQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ZoneUpdateCollectSystem = World.GetOrCreateSystemManaged<Game.Zones.UpdateCollectSystem>();
            m_ZoneSearchSystem = World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>();
            m_ObjectSearchSystem = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
            m_ModificationEndBarrier = World.GetOrCreateSystemManaged<ModificationEndBarrier>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_IconCommandSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
            m_PaletteZoneSystem = World.GetOrCreateSystemManaged<PaletteZoneSystem>();
            m_IndexSystem = World.GetOrCreateSystemManaged<BuildingIndexSystem>();
            m_BuildingSettingsQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
            m_Equivalent = new NativeParallelHashSet<int>(64, Allocator.Persistent);
            m_Allowed = new NativeParallelHashSet<long>(256, Allocator.Persistent);
            m_CustomZones = new NativeParallelHashSet<int>(16, Allocator.Persistent);
            m_AllBuildingsQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Transform>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnDestroy()
        {
            m_TableReaders.Complete();
            if (m_Equivalent.IsCreated)
            {
                m_Equivalent.Dispose();
            }

            if (m_Allowed.IsCreated)
            {
                m_Allowed.Dispose();
            }

            if (m_CustomZones.IsCreated)
            {
                m_CustomZones.Dispose();
            }

            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            m_Frame++;
            if (m_BuildingSettingsQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            // Catalog / zone edits: rebuild the tables and plan a city re-check.
            if (RefreshTables())
            {
                m_FullCheckIn = FullCheckDelayFrames;
            }

            var zonesUpdated = m_ZoneUpdateCollectSystem.isUpdated;
            var fullCheck = m_FullCheckIn >= 0 && --m_FullCheckIn < 0;
            if (!zonesUpdated && !fullCheck)
            {
                return;
            }

            var dependency = Dependency;
            if (zonesUpdated)
            {
                dependency = CheckUpdatedAreas(dependency);
            }

            if (fullCheck && !m_CustomZones.IsEmpty)
            {
                dependency = CheckBuildingsOnCustomZones(dependency);
            }

            Dependency = dependency;
        }

        /// <summary>The game's check: buildings in the areas whose zones just changed.</summary>
        private JobHandle CheckUpdatedAreas(JobHandle dependency)
        {
            var queue = new NativeQueue<Entity>(Allocator.TempJob);
            var buildings = new NativeList<Entity>(Allocator.TempJob);
            var updatedBounds = m_ZoneUpdateCollectSystem.GetUpdatedBounds(true, out var boundsDependency);

            // 1. Growable buildings in the updated areas (as the game finds them).
            var findJob = new FindSpawnableBuildingsJob
            {
                m_Bounds = updatedBounds.AsDeferredJobArray(),
                m_SearchTree = m_ObjectSearchSystem.GetStaticSearchTree(true, out var objectTreeDependency),
                m_BuildingData = GetComponentLookup<Building>(true),
                m_PrefabRefData = GetComponentLookup<PrefabRef>(true),
                m_PrefabSpawnableBuildingData = GetComponentLookup<SpawnableBuildingData>(true),
                m_PrefabSignatureBuildingData = GetComponentLookup<SignatureBuildingData>(true),
                m_ResultQueue = queue.AsParallelWriter(),
            };

            var collectJob = new CollectEntitiesJob
            {
                m_Queue = queue,
                m_List = buildings,
            };

            // 2. The check, with custom zones counted as the zones they behave like.
            var checkJob = CreateCheckJob(buildings.AsDeferredJobArray(), false, out var zoneTreeDependency);

            var findHandle = findJob.Schedule(updatedBounds, 1, JobHandle.CombineDependencies(boundsDependency, objectTreeDependency, dependency));
            var collectHandle = collectJob.Schedule(findHandle);
            var checkHandle = checkJob.Schedule(buildings, 1, JobHandle.CombineDependencies(collectHandle, zoneTreeDependency));

            queue.Dispose(collectHandle);
            buildings.Dispose(checkHandle);

            m_ZoneUpdateCollectSystem.AddBoundsReader(findHandle);
            m_ObjectSearchSystem.AddStaticSearchTreeReader(findHandle);
            FinishCheck(checkHandle);
            return checkHandle;
        }

        /// <summary>After a catalog edit: every growable building, changed only where it stands on a custom zone.</summary>
        private JobHandle CheckBuildingsOnCustomZones(JobHandle dependency)
        {
            var buildings = m_AllBuildingsQuery.ToEntityListAsync(Allocator.TempJob, out var listDependency);
            var checkJob = CreateCheckJob(buildings.AsDeferredJobArray(), true, out var zoneTreeDependency);
            var checkHandle = checkJob.Schedule(buildings, 16, JobHandle.CombineDependencies(dependency, listDependency, zoneTreeDependency));
            buildings.Dispose(checkHandle);
            FinishCheck(checkHandle);
            return checkHandle;
        }

        private CheckBuildingZonesJob CreateCheckJob(NativeArray<Entity> buildings, bool onlyCustomZones, out JobHandle zoneTreeDependency)
        {
            return new CheckBuildingZonesJob
            {
                m_CondemnedData = GetComponentLookup<Condemned>(true),
                m_HandPlacedData = GetComponentLookup<HandPlaced>(true),
                m_BlockData = GetComponentLookup<Block>(true),
                m_ValidAreaData = GetComponentLookup<ValidArea>(true),
                m_DestroyedData = GetComponentLookup<Destroyed>(true),
                m_AbandonedData = GetComponentLookup<Abandoned>(true),
                m_TransformData = GetComponentLookup<Transform>(true),
                m_AttachedData = GetComponentLookup<Attached>(true),
                m_PrefabRefData = GetComponentLookup<PrefabRef>(true),
                m_PrefabData = GetComponentLookup<PrefabData>(true),
                m_PrefabBuildingData = GetComponentLookup<BuildingData>(true),
                m_PrefabSpawnableBuildingData = GetComponentLookup<SpawnableBuildingData>(true),
                m_PrefabSignatureBuildingData = GetComponentLookup<SignatureBuildingData>(true),
                m_PrefabPlaceholderBuildingData = GetComponentLookup<PlaceholderBuildingData>(true),
                m_PrefabZoneData = GetComponentLookup<ZoneData>(true),
                m_Cells = GetBufferLookup<Cell>(true),
                m_BuildingConfigurationData = m_BuildingSettingsQuery.GetSingleton<BuildingConfigurationData>(),
                m_Buildings = buildings,
                m_SearchTree = m_ZoneSearchSystem.GetSearchTree(true, out zoneTreeDependency),
                m_EditorMode = m_ToolSystem.actionMode.IsEditor(),
                m_OnlyCustomZones = onlyCustomZones,
                m_KeepHandPlaced = Mod.Settings?.KeepHandPlacedBuildings ?? true,
                m_Equivalent = m_Equivalent,
                m_Allowed = m_Allowed,
                m_CustomZones = m_CustomZones,
                m_IconCommandBuffer = m_IconCommandSystem.CreateCommandBuffer(),
                m_CommandBuffer = m_ModificationEndBarrier.CreateCommandBuffer().AsParallelWriter(),
            };
        }

        private void FinishCheck(JobHandle checkHandle)
        {
            m_ZoneSearchSystem.AddSearchTreeReader(checkHandle);
            m_IconCommandSystem.AddCommandBufferWriter(checkHandle);
            m_ModificationEndBarrier.AddJobHandleForProducer(checkHandle);
            m_TableReaders = JobHandle.CombineDependencies(m_TableReaders, checkHandle);
        }

        /// <summary>
        /// Rebuilds the custom zone tables (zone equivalence, catalogs) when the
        /// zones, their settings or the building index changed. Returns true then.
        /// </summary>
        private bool RefreshTables()
        {
            var storageChanged = m_TablesStorageVersion != PaletteStorage.Instance.Version ||
                                 m_TablesIndexVersion != m_IndexSystem.Version;
            if (!storageChanged && (m_Frame & 63) != 0)
            {
                return false;
            }

            var zones = m_PaletteZoneSystem.GetPaletteZoneIndices();
            if (!storageChanged && m_TablesZoneCount == zones.Count)
            {
                return false;
            }

            m_TablesIndexVersion = m_IndexSystem.Version;
            m_TablesStorageVersion = PaletteStorage.Instance.Version;
            m_TablesZoneCount = zones.Count;

            m_TableReaders.Complete();
            m_TableReaders = default;
            m_Equivalent.Clear();
            m_Allowed.Clear();
            m_CustomZones.Clear();

            if (zones.Count == 0)
            {
                return true;
            }

            // Kind (category + density) of every vanilla zone that grows buildings.
            var vanillaKinds = new Dictionary<ushort, (ZoneCategory category, DensityType density)>();
            foreach (var entry in m_IndexSystem.ByEntity.Values)
            {
                if (EntityManager.TryGetComponent<SpawnableBuildingData>(entry.PrefabEntity, out var spawnable) &&
                    EntityManager.TryGetComponent<ZoneData>(spawnable.m_ZonePrefab, out var zoneData) &&
                    zoneData.m_ZoneType.m_Index != 0 &&
                    !vanillaKinds.ContainsKey(zoneData.m_ZoneType.m_Index))
                {
                    vanillaKinds[zoneData.m_ZoneType.m_Index] = (entry.Category, entry.Density);
                }
            }

            foreach (var (customIndex, palette) in zones)
            {
                m_CustomZones.Add(customIndex);

                foreach (var kv in vanillaKinds)
                {
                    if (kv.Value.category == palette.Category &&
                        (palette.Density == DensityType.Any || kv.Value.density == palette.Density))
                    {
                        m_Equivalent.Add((customIndex << 16) | kv.Key);
                    }
                }

                // Every level of every building in the catalog: a building chosen at
                // level 1 may level up (same design) or become another catalog
                // building at the next level.
                foreach (var selection in palette.Families)
                {
                    BuildingFamilies.ParseSelection(selection, out var key, out _);
                    if (m_IndexSystem.FamiliesByKey.TryGetValue(key, out var family))
                    {
                        foreach (var member in family.Members)
                        {
                            m_Allowed.Add(((long)customIndex << 32) | (uint)member.PrefabEntity.Index);
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>The game's FindSpawnableBuildingsJob: growable (non-signature) buildings touching the updated areas.</summary>
        [BurstCompile]
        private struct FindSpawnableBuildingsJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<Bounds2> m_Bounds;
            [ReadOnly] public NativeQuadTree<Entity, QuadTreeBoundsXZ> m_SearchTree;
            [ReadOnly] public ComponentLookup<Building> m_BuildingData;
            [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRefData;
            [ReadOnly] public ComponentLookup<SpawnableBuildingData> m_PrefabSpawnableBuildingData;
            [ReadOnly] public ComponentLookup<SignatureBuildingData> m_PrefabSignatureBuildingData;
            public NativeQueue<Entity>.ParallelWriter m_ResultQueue;

            public void Execute(int index)
            {
                var iterator = new Iterator
                {
                    m_Bounds = m_Bounds[index],
                    m_BuildingData = m_BuildingData,
                    m_PrefabRefData = m_PrefabRefData,
                    m_PrefabSpawnableBuildingData = m_PrefabSpawnableBuildingData,
                    m_PrefabSignatureBuildingData = m_PrefabSignatureBuildingData,
                    m_ResultQueue = m_ResultQueue,
                };
                m_SearchTree.Iterate(ref iterator);
            }

            private struct Iterator : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
            {
                public Bounds2 m_Bounds;
                public ComponentLookup<Building> m_BuildingData;
                public ComponentLookup<PrefabRef> m_PrefabRefData;
                public ComponentLookup<SpawnableBuildingData> m_PrefabSpawnableBuildingData;
                public ComponentLookup<SignatureBuildingData> m_PrefabSignatureBuildingData;
                public NativeQueue<Entity>.ParallelWriter m_ResultQueue;

                public bool Intersect(QuadTreeBoundsXZ bounds) => MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds);

                public void Iterate(QuadTreeBoundsXZ bounds, Entity entity)
                {
                    if (!MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds) || !m_BuildingData.HasComponent(entity))
                    {
                        return;
                    }

                    var prefab = m_PrefabRefData[entity].m_Prefab;
                    if (m_PrefabSpawnableBuildingData.HasComponent(prefab) && !m_PrefabSignatureBuildingData.HasComponent(prefab))
                    {
                        m_ResultQueue.Enqueue(entity);
                    }
                }
            }
        }

        /// <summary>The game's CollectEntitiesJob: the found buildings, sorted, each once.</summary>
        [BurstCompile]
        private struct CollectEntitiesJob : IJob
        {
            public NativeQueue<Entity> m_Queue;
            public NativeList<Entity> m_List;

            public void Execute()
            {
                var count = m_Queue.Count;
                if (count == 0)
                {
                    return;
                }

                m_List.ResizeUninitialized(count);
                for (var i = 0; i < count; i++)
                {
                    m_List[i] = m_Queue.Dequeue();
                }

                m_List.Sort();

                var unique = 0;
                var last = Entity.Null;
                for (var i = 0; i < m_List.Length; i++)
                {
                    var entity = m_List[i];
                    if (entity != last)
                    {
                        m_List[unique++] = entity;
                        last = entity;
                    }
                }

                m_List.RemoveRangeSwapBack(unique, m_List.Length - unique);
            }
        }

        /// <summary>The game's CheckBuildingZonesJob, with custom zone equivalence.</summary>
        [BurstCompile]
        private struct CheckBuildingZonesJob : IJobParallelForDefer
        {
            [ReadOnly] public ComponentLookup<Condemned> m_CondemnedData;
            [ReadOnly] public ComponentLookup<HandPlaced> m_HandPlacedData;
            [ReadOnly] public ComponentLookup<Block> m_BlockData;
            [ReadOnly] public ComponentLookup<ValidArea> m_ValidAreaData;
            [ReadOnly] public ComponentLookup<Destroyed> m_DestroyedData;
            [ReadOnly] public ComponentLookup<Abandoned> m_AbandonedData;
            [ReadOnly] public ComponentLookup<Transform> m_TransformData;
            [ReadOnly] public ComponentLookup<Attached> m_AttachedData;
            [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRefData;
            [ReadOnly] public ComponentLookup<PrefabData> m_PrefabData;
            [ReadOnly] public ComponentLookup<BuildingData> m_PrefabBuildingData;
            [ReadOnly] public ComponentLookup<SpawnableBuildingData> m_PrefabSpawnableBuildingData;
            [ReadOnly] public ComponentLookup<SignatureBuildingData> m_PrefabSignatureBuildingData;
            [ReadOnly] public ComponentLookup<PlaceholderBuildingData> m_PrefabPlaceholderBuildingData;
            [ReadOnly] public ComponentLookup<ZoneData> m_PrefabZoneData;
            [ReadOnly] public BufferLookup<Cell> m_Cells;
            [ReadOnly] public BuildingConfigurationData m_BuildingConfigurationData;
            [ReadOnly] public NativeArray<Entity> m_Buildings;
            [ReadOnly] public NativeQuadTree<Entity, Bounds2> m_SearchTree;
            [ReadOnly] public NativeParallelHashSet<int> m_Equivalent;
            [ReadOnly] public NativeParallelHashSet<long> m_Allowed;
            [ReadOnly] public NativeParallelHashSet<int> m_CustomZones;
            [ReadOnly] public bool m_EditorMode;
            [ReadOnly] public bool m_OnlyCustomZones;
            [ReadOnly] public bool m_KeepHandPlaced;
            public IconCommandBuffer m_IconCommandBuffer;
            public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

            public void Execute(int index)
            {
                var building = m_Buildings[index];
                var prefabRef = m_PrefabRefData[building];
                if (!m_PrefabSpawnableBuildingData.TryGetComponent(prefabRef.m_Prefab, out var spawnable) ||
                    !m_PrefabBuildingData.TryGetComponent(prefabRef.m_Prefab, out var buildingData) ||
                    m_PrefabSignatureBuildingData.HasComponent(prefabRef.m_Prefab))
                {
                    return; // only growable buildings are zone-checked
                }

                if (m_KeepHandPlaced && m_HandPlacedData.TryGetComponent(building, out var handPlaced))
                {
                    var zoneHash = LotZones.Hash(m_TransformData[building], buildingData.m_LotSize, m_SearchTree, m_BlockData, m_ValidAreaData, m_Cells);
                    if (zoneHash == handPlaced.m_ZoneHash)
                    {
                        return; // placed by hand and its zones are unchanged: kept as it is
                    }

                    // The zone under it changed: from now on the usual rules (and
                    // these zones are the new reference).
                    m_CommandBuffer.SetComponent(index, building, new HandPlaced { m_ZoneHash = zoneHash });
                }

                var touchesCustomZone = false;
                var valid = m_EditorMode || ValidateAttachedParent(building, spawnable) ||
                            ValidateZoneBlocks(building, prefabRef.m_Prefab, buildingData, spawnable, out touchesCustomZone);

                // Catalog re-check: buildings away from custom zones are the game's business.
                if (m_OnlyCustomZones && !touchesCustomZone)
                {
                    return;
                }

                if (valid)
                {
                    if (m_CondemnedData.HasComponent(building))
                    {
                        m_CommandBuffer.RemoveComponent<Condemned>(index, building);
                        m_IconCommandBuffer.Remove(building, m_BuildingConfigurationData.m_CondemnedNotification, default, (IconFlags)0);
                    }

                    return;
                }

                if (m_CondemnedData.HasComponent(building))
                {
                    return;
                }

                m_CommandBuffer.AddComponent(index, building, default(Condemned));
                if (!m_DestroyedData.HasComponent(building) && !m_AbandonedData.HasComponent(building))
                {
                    m_IconCommandBuffer.Add(building, m_BuildingConfigurationData.m_CondemnedNotification, (IconPriority)250, (IconClusterLayer)0, (IconFlags)0, Entity.Null, false, false, false, 0f);
                }
            }

            private bool ValidateAttachedParent(Entity building, SpawnableBuildingData spawnable)
            {
                return m_AttachedData.TryGetComponent(building, out var attached) &&
                       m_PrefabRefData.TryGetComponent(attached.m_Parent, out var parentPrefab) &&
                       m_PrefabPlaceholderBuildingData.TryGetComponent(parentPrefab.m_Prefab, out var placeholder) &&
                       placeholder.m_ZonePrefab == spawnable.m_ZonePrefab;
            }

            private bool ValidateZoneBlocks(Entity building, Entity prefab, BuildingData buildingData, SpawnableBuildingData spawnable, out bool touchesCustomZone)
            {
                touchesCustomZone = false;
                var transform = m_TransformData[building];

                if (m_PrefabZoneData.TryGetComponent(spawnable.m_ZonePrefab, out var zoneData) &&
                    !zoneData.m_ZoneType.Equals(ZoneType.None) &&
                    !m_PrefabData.IsComponentEnabled(spawnable.m_ZonePrefab))
                {
                    return false;
                }

                var right = math.rotate(transform.m_Rotation, new float3(8f, 0f, 0f)).xz;
                var forward = math.rotate(transform.m_Rotation, new float3(0f, 0f, 8f)).xz;
                var halfRight = right * ((buildingData.m_LotSize.x * 0.5f) - 0.5f);
                var halfForward = forward * ((buildingData.m_LotSize.y * 0.5f) - 0.5f);
                var extents = math.abs(halfForward) + math.abs(halfRight);

                var validated = new NativeArray<bool>(buildingData.m_LotSize.x * buildingData.m_LotSize.y, Allocator.Temp);
                var iterator = new Iterator
                {
                    m_Bounds = new Bounds2(transform.m_Position.xz - extents, transform.m_Position.xz + extents),
                    m_LotSize = buildingData.m_LotSize,
                    m_StartPosition = transform.m_Position.xz + halfForward + halfRight,
                    m_Right = right,
                    m_Forward = forward,
                    m_ZoneType = zoneData.m_ZoneType,
                    m_Validated = validated,
                    m_BlockData = m_BlockData,
                    m_ValidAreaData = m_ValidAreaData,
                    m_Cells = m_Cells,
                    m_Equivalent = m_Equivalent,
                    m_Allowed = m_Allowed,
                    m_CustomZones = m_CustomZones,
                    m_Prefab = prefab,
                };
                m_SearchTree.Iterate(ref iterator);
                touchesCustomZone = iterator.m_TouchesCustomZone;

                var result = (iterator.m_Directions & CellFlags.Roadside) != 0;
                for (var i = 0; i < validated.Length; i++)
                {
                    result &= validated[i];
                }

                validated.Dispose();
                return result;
            }

            private struct Iterator : INativeQuadTreeIterator<Entity, Bounds2>
            {
                public Bounds2 m_Bounds;
                public int2 m_LotSize;
                public float2 m_StartPosition;
                public float2 m_Right;
                public float2 m_Forward;
                public ZoneType m_ZoneType;
                public CellFlags m_Directions;
                public NativeArray<bool> m_Validated;
                public ComponentLookup<Block> m_BlockData;
                public ComponentLookup<ValidArea> m_ValidAreaData;
                public BufferLookup<Cell> m_Cells;
                public NativeParallelHashSet<int> m_Equivalent;
                public NativeParallelHashSet<long> m_Allowed;
                public NativeParallelHashSet<int> m_CustomZones;
                public Entity m_Prefab;
                public bool m_TouchesCustomZone;

                public bool Intersect(Bounds2 bounds) => MathUtils.Intersect(bounds, m_Bounds);

                public void Iterate(Bounds2 bounds, Entity blockEntity)
                {
                    if (!MathUtils.Intersect(bounds, m_Bounds))
                    {
                        return;
                    }

                    var validArea = m_ValidAreaData[blockEntity];
                    if (validArea.m_Area.y <= validArea.m_Area.x)
                    {
                        return;
                    }

                    var otherBlock = new Block { m_Direction = m_Forward };
                    var block = m_BlockData[blockEntity];
                    var cells = m_Cells[blockEntity];

                    var rowStart = m_StartPosition;
                    int2 lotCell;
                    for (lotCell.y = 0; lotCell.y < m_LotSize.y; lotCell.y++)
                    {
                        var position = rowStart;
                        for (lotCell.x = 0; lotCell.x < m_LotSize.x; lotCell.x++)
                        {
                            var cellIndex = ZoneUtils.GetCellIndex(block, position);
                            if (math.all((cellIndex >= validArea.m_Area.xz) & (cellIndex < validArea.m_Area.yw)))
                            {
                                var cell = cells[(cellIndex.y * block.m_Size.x) + cellIndex.x];
                                if ((cell.m_State & CellFlags.Visible) != 0 && SameZone(cell.m_Zone))
                                {
                                    m_Validated[(lotCell.y * m_LotSize.x) + lotCell.x] = true;

                                    if ((cell.m_State & (CellFlags.Roadside | CellFlags.RoadLeft | CellFlags.RoadRight | CellFlags.RoadBack)) != 0)
                                    {
                                        var roadDirection = ZoneUtils.GetRoadDirection(otherBlock, block, cell.m_State);
                                        var edges = math.select(
                                            new int4(0),
                                            new int4((int)CellFlags.RoadLeft, (int)CellFlags.Roadside, (int)CellFlags.RoadRight, (int)CellFlags.RoadBack),
                                            new bool4(lotCell == 0, lotCell == m_LotSize - 1));
                                        m_Directions |= roadDirection & (CellFlags)(ushort)math.csum(edges);
                                    }
                                }
                            }

                            position -= m_Right;
                        }

                        rowStart -= m_Forward;
                    }
                }

                // The building's own zone, or a custom zone of its kind that has it in its catalog.
                private bool SameZone(ZoneType cellZone)
                {
                    if (cellZone.Equals(m_ZoneType))
                    {
                        return true;
                    }

                    if (cellZone.m_Index == 0 || !m_CustomZones.Contains(cellZone.m_Index))
                    {
                        return false;
                    }

                    // A custom zone accepts exactly the buildings of its catalog (any
                    // level), whatever their own type: zones may mix types.
                    m_TouchesCustomZone = true;
                    return m_Allowed.Contains(((long)cellZone.m_Index << 32) | (uint)m_Prefab.Index);
                }
            }
        }
    }
}
