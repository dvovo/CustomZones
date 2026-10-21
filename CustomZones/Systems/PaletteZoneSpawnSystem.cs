// <copyright file="PaletteZoneSpawnSystem.cs">
// CustomZones - Licensed under the MIT license.
//
// The ByLaw-model spawn system (Phase 2). A trimmed reimplementation of the
// vanilla ZoneSpawnSystem.EvaluateSpawnAreas that runs UpdateBefore the
// vanilla ZoneSpawnSystem: for every vacant lot whose zone carries our
// PaletteZoneMarker, it selects a building ONLY from that zone's
// PaletteAllowedBuilding whitelist (evaluated against each building's own
// area-type demand, exactly like the vanilla spawner), then hands the chosen
// spawn location to the vanilla ZoneSpawnSystem.SpawnBuildingJob.
//
// Unlike ZoningByLaw (which scans a random building chunk and filters), we
// iterate the whitelist entities DIRECTLY, so a palette with only a handful of
// buildings still spawns reliably instead of almost never hitting a match.
//
// Because a whitelisted building keeps its ORIGINAL zone (we do not clone or
// re-zone it), the vanilla ZoneCheckSystem would see the cell's palette zone
// type != the building's own zone type and immediately condemn it — so, like
// ByLaw, the mod disables ZoneCheckSystem (see Mod.OnLoad).
//
// Technique reimplemented from FutureProg/ZoningByLawMod (no license file),
// reusing the game's own ZoneSpawnSystem.SpawnBuildingJob + demand systems.
// </copyright>

namespace CustomZones.Systems
{
    using Game;
    using Game.Buildings;
    using Game.City;
    using Game.Common;
    using Game.Economy;
    using Game.Net;
    using Game.Prefabs;
    using Game.Simulation;
    using Game.Tools;
    using Game.Zones;
    using Unity.Burst;
    using Unity.Burst.Intrinsics;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Mathematics;
    using CustomZones.Components;
    using BuildingData = Game.Prefabs.BuildingData;

    public partial class PaletteZoneSpawnSystem : GameSystemBase
    {
        private EntityQuery m_VacantLotsQuery;
        private EntityQuery m_ProcessQuery;
        private EntityQuery m_BuildingConfigQuery;
        private EntityArchetype m_DefinitionArchetype;

        private global::Game.Prefabs.ZoneSystem m_ZoneSystem;
        private ZoneSpawnSystem m_ZoneSpawnSystem;
        private ResidentialDemandSystem m_ResidentialDemandSystem;
        private CommercialDemandSystem m_CommercialDemandSystem;
        private IndustrialDemandSystem m_IndustrialDemandSystem;
        private GroundPollutionSystem m_PollutionSystem;
        private TerrainSystem m_TerrainSystem;
        private global::Game.Zones.SearchSystem m_SearchSystem;
        private ResourceSystem m_ResourceSystem;
        private CityConfigurationSystem m_CityConfigurationSystem;
        private EndFrameBarrier m_EndFrameBarrier;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 8;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_VacantLotsQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Block>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<CurvePosition>(),
                    ComponentType.ReadOnly<VacantLot>(),
                },
                None = new[]
                {
                    ComponentType.ReadWrite<Temp>(),
                    ComponentType.ReadWrite<Deleted>(),
                },
            });

            m_DefinitionArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<CreationDefinition>(),
                ComponentType.ReadWrite<ObjectDefinition>(),
                ComponentType.ReadWrite<Updated>(),
                ComponentType.ReadWrite<Deleted>());

            m_ProcessQuery = GetEntityQuery(ComponentType.ReadOnly<IndustrialProcessData>());
            m_BuildingConfigQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());

            m_ZoneSystem = World.GetOrCreateSystemManaged<global::Game.Prefabs.ZoneSystem>();
            m_ZoneSpawnSystem = World.GetOrCreateSystemManaged<ZoneSpawnSystem>();
            m_ResidentialDemandSystem = World.GetOrCreateSystemManaged<ResidentialDemandSystem>();
            m_CommercialDemandSystem = World.GetOrCreateSystemManaged<CommercialDemandSystem>();
            m_IndustrialDemandSystem = World.GetOrCreateSystemManaged<IndustrialDemandSystem>();
            m_PollutionSystem = World.GetOrCreateSystemManaged<GroundPollutionSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_SearchSystem = World.GetOrCreateSystemManaged<global::Game.Zones.SearchSystem>();
            m_ResourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();
            m_CityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            RequireForUpdate(m_VacantLotsQuery);
        }

        protected override void OnUpdate()
        {
            var residentialQ = new NativeQueue<ZoneSpawnSystem.SpawnLocation>(Allocator.TempJob);
            var commercialQ = new NativeQueue<ZoneSpawnSystem.SpawnLocation>(Allocator.TempJob);
            var industrialQ = new NativeQueue<ZoneSpawnSystem.SpawnLocation>(Allocator.TempJob);

            var evaluate = new EvaluateSpawnAreas
            {
                entityHandle = GetEntityTypeHandle(),
                ownerHandle = GetComponentTypeHandle<Owner>(true),
                curvePositionHandle = GetComponentTypeHandle<CurvePosition>(true),
                blockHandle = GetComponentTypeHandle<Block>(true),
                vacantLotHandle = GetBufferTypeHandle<VacantLot>(true),

                zonePrefabs = m_ZoneSystem.GetPrefabs(),
                paletteMarkerLookup = GetComponentLookup<PaletteZoneMarker>(true),
                allowedBuildingLookup = GetBufferLookup<PaletteAllowedBuilding>(true),
                zoneDataLookup = GetComponentLookup<ZoneData>(true),
                zonePropertiesDataLookup = GetComponentLookup<ZonePropertiesData>(true),
                processEstimatesLookup = GetBufferLookup<ProcessEstimate>(true),
                spawnableLookup = GetComponentLookup<SpawnableBuildingData>(true),
                buildingLookup = GetComponentLookup<BuildingData>(true),
                propertyLookup = GetComponentLookup<BuildingPropertyData>(true),
                objGeomLookup = GetComponentLookup<ObjectGeometryData>(true),
                warehouseLookup = GetComponentLookup<WarehouseData>(true),
                resourceAvailabilityLookup = GetBufferLookup<ResourceAvailability>(true),
                blockLookup = GetComponentLookup<Block>(true),
                landValueLookup = GetComponentLookup<LandValue>(true),
                resourceDataLookup = GetComponentLookup<ResourceData>(true),
                resourcePrefabs = m_ResourceSystem.GetPrefabs(),

                groundPollutionMap = m_PollutionSystem.GetMap(true, out var pollutionJob),
                zonePreferenceData = SystemAPI.GetSingleton<ZonePreferenceData>(),
                industrialProcesses = m_ProcessQuery.ToComponentDataListAsync<IndustrialProcessData>(World.UpdateAllocator.ToAllocator, out var processJob),

                residentialDemands = m_ResidentialDemandSystem.buildingDemand,
                commercialDemands = m_CommercialDemandSystem.GetBuildingDemands(out var commercialDemandJob),
                industrialDemands = m_IndustrialDemandSystem.GetBuildingDemands(out var industrialDemandJob),
                storageDemands = m_IndustrialDemandSystem.GetStorageBuildingDemands(out var storageDemandJob),

                residentialQ = residentialQ.AsParallelWriter(),
                commercialQ = commercialQ.AsParallelWriter(),
                industrialQ = industrialQ.AsParallelWriter(),

                randomSeed = RandomSeed.Next(),
                minDemand = m_ZoneSpawnSystem.debugFastSpawn ? 0 : 1,
            };

            var spawnBuildingJob = new ZoneSpawnSystem.SpawnBuildingJob
            {
                m_BlockData = GetComponentLookup<Block>(true),
                m_BuildingConfigurationData = m_BuildingConfigQuery.GetSingleton<BuildingConfigurationData>(),
                m_Cells = GetBufferLookup<Cell>(true),
                m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter(),
                m_Commercial = commercialQ,
                m_DefinitionArchetype = m_DefinitionArchetype,
                m_Industrial = industrialQ,
                m_LefthandTraffic = m_CityConfigurationSystem.leftHandTraffic,
                m_PrefabAreaGeometryData = GetComponentLookup<AreaGeometryData>(true),
                m_PrefabBuildingData = GetComponentLookup<BuildingData>(true),
                m_PrefabNetGeometryData = GetComponentLookup<NetGeometryData>(true),
                m_PrefabObjectGeometryData = GetComponentLookup<ObjectGeometryData>(true),
                m_PrefabPlaceableObjectData = GetComponentLookup<PlaceableObjectData>(true),
                m_PrefabPlaceholderElements = GetBufferLookup<PlaceholderObjectElement>(true),
                m_PrefabRefData = GetComponentLookup<PrefabRef>(true),
                m_PrefabSpawnableObjectData = GetComponentLookup<SpawnableObjectData>(true),
                m_PrefabSubAreaNodes = GetBufferLookup<Game.Prefabs.SubAreaNode>(true),
                m_PrefabSubAreas = GetBufferLookup<Game.Prefabs.SubArea>(true),
                m_PrefabSubNets = GetBufferLookup<Game.Prefabs.SubNet>(true),
                m_RandomSeed = RandomSeed.Next(),
                m_Residential = residentialQ,
                m_TerrainHeightData = m_TerrainSystem.GetHeightData(false),
                m_TransformData = GetComponentLookup<Game.Objects.Transform>(true),
                m_ValidAreaData = GetComponentLookup<ValidArea>(true),
                m_ZoneSearchTree = m_SearchSystem.GetSearchTree(true, out var searchTreeJob),
            };

            var evaluateHandle = evaluate.ScheduleParallel(
                m_VacantLotsQuery,
                JobHandle.CombineDependencies(
                    JobHandle.CombineDependencies(Dependency, pollutionJob, processJob),
                    JobHandle.CombineDependencies(commercialDemandJob, industrialDemandJob, storageDemandJob)));

            var spawnHandle = spawnBuildingJob.Schedule(3, 1, JobHandle.CombineDependencies(evaluateHandle, searchTreeJob));

            m_ResourceSystem.AddPrefabsReader(evaluateHandle);
            m_PollutionSystem.AddReader(evaluateHandle);
            m_CommercialDemandSystem.AddReader(evaluateHandle);
            m_IndustrialDemandSystem.AddReader(evaluateHandle);

            residentialQ.Dispose(spawnHandle);
            commercialQ.Dispose(spawnHandle);
            industrialQ.Dispose(spawnHandle);

            m_ZoneSystem.AddPrefabsReader(evaluateHandle);
            m_TerrainSystem.AddCPUHeightReader(spawnHandle);
            m_EndFrameBarrier.AddJobHandleForProducer(spawnHandle);
            m_SearchSystem.AddSearchTreeReader(spawnHandle);

            Dependency = spawnHandle;
        }

        [BurstCompile]
        public struct EvaluateSpawnAreas : IJobChunk
        {
            public EntityTypeHandle entityHandle;
            [ReadOnly] public ComponentTypeHandle<Owner> ownerHandle;
            [ReadOnly] public ComponentTypeHandle<CurvePosition> curvePositionHandle;
            [ReadOnly] public ComponentTypeHandle<Block> blockHandle;
            [ReadOnly] public BufferTypeHandle<VacantLot> vacantLotHandle;

            [ReadOnly] public ZonePrefabs zonePrefabs;
            [ReadOnly] public ComponentLookup<PaletteZoneMarker> paletteMarkerLookup;
            [ReadOnly] public BufferLookup<PaletteAllowedBuilding> allowedBuildingLookup;
            [ReadOnly] public ComponentLookup<ZoneData> zoneDataLookup;
            [ReadOnly] public ComponentLookup<ZonePropertiesData> zonePropertiesDataLookup;
            [ReadOnly] public BufferLookup<ProcessEstimate> processEstimatesLookup;
            [ReadOnly] public ComponentLookup<SpawnableBuildingData> spawnableLookup;
            [ReadOnly] public ComponentLookup<BuildingData> buildingLookup;
            [ReadOnly] public ComponentLookup<BuildingPropertyData> propertyLookup;
            [ReadOnly] public ComponentLookup<ObjectGeometryData> objGeomLookup;
            [ReadOnly] public ComponentLookup<WarehouseData> warehouseLookup;
            [ReadOnly] public BufferLookup<ResourceAvailability> resourceAvailabilityLookup;
            [ReadOnly] public ComponentLookup<Block> blockLookup;
            [ReadOnly] public ComponentLookup<LandValue> landValueLookup;
            [ReadOnly] public ComponentLookup<ResourceData> resourceDataLookup;
            [ReadOnly] public ResourcePrefabs resourcePrefabs;

            [ReadOnly] public NativeArray<GroundPollution> groundPollutionMap;
            public ZonePreferenceData zonePreferenceData;
            [ReadOnly] public NativeList<IndustrialProcessData> industrialProcesses;

            public int3 residentialDemands;
            [ReadOnly] public NativeArray<int> commercialDemands;
            [ReadOnly] public NativeArray<int> industrialDemands;
            [ReadOnly] public NativeArray<int> storageDemands;

            public NativeQueue<ZoneSpawnSystem.SpawnLocation>.ParallelWriter residentialQ;
            public NativeQueue<ZoneSpawnSystem.SpawnLocation>.ParallelWriter commercialQ;
            public NativeQueue<ZoneSpawnSystem.SpawnLocation>.ParallelWriter industrialQ;

            public RandomSeed randomSeed;
            public int minDemand;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var random = randomSeed.GetRandom(unfilteredChunkIndex);
                var best = default(ZoneSpawnSystem.SpawnLocation);

                var entities = chunk.GetNativeArray(entityHandle);
                var vacantLotsAccessor = chunk.GetBufferAccessor(ref vacantLotHandle);
                if (vacantLotsAccessor.Length == 0)
                {
                    return;
                }

                var owners = chunk.GetNativeArray(ref ownerHandle);
                var curvePositions = chunk.GetNativeArray(ref curvePositionHandle);
                var blocks = chunk.GetNativeArray(ref blockHandle);

                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    var vacantLots = vacantLotsAccessor[i];
                    var owner = owners[i];
                    var curvePos = curvePositions[i];
                    var block = blocks[i];

                    for (var j = 0; j < vacantLots.Length; j++)
                    {
                        var vacantLot = vacantLots[j];
                        var zonePrefab = zonePrefabs[vacantLot.m_Type];

                        // Only palette zones with a non-empty whitelist.
                        if (!paletteMarkerLookup.HasComponent(zonePrefab) ||
                            !allowedBuildingLookup.TryGetBuffer(zonePrefab, out var allowed) ||
                            allowed.Length == 0 ||
                            !zonePropertiesDataLookup.TryGetComponent(zonePrefab, out var zoneProperties) ||
                            !processEstimatesLookup.TryGetBuffer(zonePrefab, out var estimates) ||
                            !zoneDataLookup.TryGetComponent(zonePrefab, out var zoneData))
                        {
                            continue;
                        }

                        var curvePosScalar = CalculateCurvePos(curvePos, vacantLot, block);

                        TryAddLot(allowed, ref best, ref random, owner.m_Owner, curvePosScalar, entity,
                            vacantLot.m_Area, vacantLot.m_Flags, (int)vacantLot.m_Height, zoneData, zoneProperties, estimates);
                    }
                }

                if (best.m_Priority != 0f)
                {
                    switch (best.m_AreaType)
                    {
                        case AreaType.Commercial:
                            commercialQ.Enqueue(best);
                            break;
                        case AreaType.Industrial:
                            industrialQ.Enqueue(best);
                            break;
                        case AreaType.Residential:
                            residentialQ.Enqueue(best);
                            break;
                    }
                }
            }

            private float CalculateCurvePos(CurvePosition curvePosition, VacantLot lot, Block block)
            {
                var s = math.saturate((lot.m_Area.x + lot.m_Area.y) * 0.5f / block.m_Size.x);
                return math.lerp(curvePosition.m_CurvePosition.x, curvePosition.m_CurvePosition.y, s);
            }

            private void TryAddLot(DynamicBuffer<PaletteAllowedBuilding> allowed, ref ZoneSpawnSystem.SpawnLocation bestLocation,
                ref Random random, Entity road, float curvePos, Entity entity, int4 area, LotFlags flags, int height,
                ZoneData zoneData, ZonePropertiesData zoneProperties, DynamicBuffer<ProcessEstimate> estimates)
            {
                if (!resourceAvailabilityLookup.HasBuffer(road))
                {
                    return;
                }

                if ((zoneData.m_ZoneFlags & ZoneFlags.SupportLeftCorner) == 0)
                {
                    flags &= ~LotFlags.CornerLeft;
                }

                if ((zoneData.m_ZoneFlags & ZoneFlags.SupportRightCorner) == 0)
                {
                    flags &= ~LotFlags.CornerRight;
                }

                var spawnLocation = default(ZoneSpawnSystem.SpawnLocation);
                spawnLocation.m_Entity = entity;
                spawnLocation.m_LotArea = area;
                spawnLocation.m_ZoneType = zoneData.m_ZoneType;
                spawnLocation.m_AreaType = zoneData.m_AreaType;
                spawnLocation.m_LotFlags = flags;

                var office = zoneData.m_AreaType == AreaType.Industrial && estimates.Length == 0;
                var availabilities = resourceAvailabilityLookup[road];

                if (!blockLookup.HasComponent(spawnLocation.m_Entity))
                {
                    return;
                }

                var position = ZoneUtils.GetPosition(blockLookup[spawnLocation.m_Entity], spawnLocation.m_LotArea.xz, spawnLocation.m_LotArea.yw);
                var pollution = GroundPollutionSystem.GetPollution(position, groundPollutionMap).m_Pollution;
                var landValue = landValueLookup[road].m_LandValue;
                var maxHeight = height - position.y;

                if (SelectBuilding(allowed, ref spawnLocation, ref random, availabilities, zoneData, zoneProperties,
                        curvePos, pollution, landValue, maxHeight, estimates, office)
                    && spawnLocation.m_Priority > bestLocation.m_Priority)
                {
                    bestLocation = spawnLocation;
                }
            }

            private bool SelectBuilding(DynamicBuffer<PaletteAllowedBuilding> allowed, ref ZoneSpawnSystem.SpawnLocation location,
                ref Random random, DynamicBuffer<ResourceAvailability> availabilities, ZoneData zoneData, ZonePropertiesData zoneProperties,
                float curvePos, float pollution, float landValue, float maxHeight, DynamicBuffer<ProcessEstimate> estimates, bool office)
            {
                var maxLotSize = location.m_LotArea.yw - location.m_LotArea.xz;
                var chosenBuildingData = default(BuildingData);
                var lotAccess = new bool2((location.m_LotFlags & LotFlags.CornerLeft) > 0, (location.m_LotFlags & LotFlags.CornerRight) > 0);
                var supportsNarrow = (zoneData.m_ZoneFlags & ZoneFlags.SupportNarrow) == 0;

                for (var i = 0; i < allowed.Length; i++)
                {
                    var building = allowed[i].m_Building;

                    if (!spawnableLookup.TryGetComponent(building, out var spawnableData) || spawnableData.m_Level != 1 ||
                        !buildingLookup.TryGetComponent(building, out var subjBuildingData) ||
                        !propertyLookup.TryGetComponent(building, out var buildingPropertyData) ||
                        !objGeomLookup.TryGetComponent(building, out var objGeom))
                    {
                        continue;
                    }

                    var lotSize = subjBuildingData.m_LotSize;
                    var buildingAccess = new bool2(
                        (subjBuildingData.m_Flags & Game.Prefabs.BuildingFlags.LeftAccess) > 0,
                        (subjBuildingData.m_Flags & Game.Prefabs.BuildingFlags.RightAccess) > 0);
                    var bldgHeight = objGeom.m_Size.y;

                    if (!math.all(lotSize <= maxLotSize) || bldgHeight > maxHeight)
                    {
                        continue;
                    }

                    var isStorage = warehouseLookup.HasComponent(building);
                    var evalAreaType = zoneDataLookup[spawnableData.m_ZonePrefab].m_AreaType;
                    var zoneDensity = PropertyUtils.GetZoneDensity(zoneData, zoneProperties);
                    var num = EvaluateDemandAndAvailability(evalAreaType, buildingPropertyData, zoneDensity, isStorage);

                    if (num < minDemand)
                    {
                        continue;
                    }

                    var int2 = math.select(maxLotSize - lotSize, 0, lotSize == maxLotSize - 1);
                    var num2 = lotSize.x * lotSize.y * random.NextFloat(1f, 1.05f);
                    num2 += int2.x * lotSize.y * random.NextFloat(0.95f, 1f);
                    num2 += maxLotSize.x * int2.y * random.NextFloat(0.55f, 0.6f);
                    num2 /= maxLotSize.x * maxLotSize.y;
                    num2 *= num + 1;
                    var accessMatchInfluence = 0.5f;
                    var accessMisMatchInfluence = 0f;
                    num2 *= math.csum(math.select(accessMisMatchInfluence, accessMatchInfluence, lotAccess == buildingAccess));

                    var num3 = landValue;
                    float num4;
                    if (evalAreaType == AreaType.Residential)
                    {
                        num4 = buildingPropertyData.m_ResidentialProperties == 1 ? 2f : buildingPropertyData.CountProperties();
                        lotSize.x = math.select(lotSize.x, maxLotSize.x, lotSize.x == maxLotSize.x - 1 && supportsNarrow);
                        num3 *= lotSize.x * maxLotSize.y;
                    }
                    else
                    {
                        num4 = buildingPropertyData.m_SpaceMultiplier;
                    }

                    var score = ZoneEvaluationUtils.GetScore(evalAreaType, office, availabilities, curvePos, ref zonePreferenceData,
                        isStorage, isStorage ? storageDemands : industrialDemands, buildingPropertyData, pollution, num3 / num4,
                        estimates, industrialProcesses, resourcePrefabs, ref resourceDataLookup);
                    score = math.select(score, math.max(0f, score) + 1f, minDemand == 0);
                    num2 *= score;

                    if (num2 > location.m_Priority && num2 > 0)
                    {
                        location.m_Building = building;
                        location.m_AreaType = evalAreaType;
                        chosenBuildingData = subjBuildingData;
                        location.m_Priority = num2;
                    }
                }

                if (location.m_Building != Entity.Null)
                {
                    var rightAccessOnly = (chosenBuildingData.m_Flags & Game.Prefabs.BuildingFlags.LeftAccess) == 0 &&
                                          ((chosenBuildingData.m_Flags & Game.Prefabs.BuildingFlags.RightAccess) != 0 || random.NextBool());
                    if (rightAccessOnly)
                    {
                        location.m_LotArea.x = location.m_LotArea.y - chosenBuildingData.m_LotSize.x;
                        location.m_LotArea.w = location.m_LotArea.z + chosenBuildingData.m_LotSize.y;
                    }
                    else
                    {
                        location.m_LotArea.yw = location.m_LotArea.xz + chosenBuildingData.m_LotSize;
                    }

                    return true;
                }

                return false;
            }

            private int EvaluateDemandAndAvailability(AreaType areaType, BuildingPropertyData propertyData, ZoneDensity zoneDensity, bool storage)
            {
                switch (areaType)
                {
                    case AreaType.Residential:
                        if (zoneDensity == ZoneDensity.Low)
                        {
                            return residentialDemands.x;
                        }

                        if (zoneDensity == ZoneDensity.Medium)
                        {
                            return residentialDemands.y;
                        }

                        return residentialDemands.z;

                    case AreaType.Commercial:
                    {
                        var sum = 0;
                        var iterator = ResourceIterator.GetIterator();
                        while (iterator.Next())
                        {
                            if ((propertyData.m_AllowedSold & iterator.resource) != Resource.NoResource)
                            {
                                sum += commercialDemands[EconomyUtils.GetResourceIndex(iterator.resource)];
                            }
                        }

                        return sum;
                    }

                    case AreaType.Industrial:
                    {
                        var sum = 0;
                        var iterator = ResourceIterator.GetIterator();
                        while (iterator.Next())
                        {
                            if (storage)
                            {
                                if ((propertyData.m_AllowedStored & iterator.resource) != Resource.NoResource)
                                {
                                    sum += storageDemands[EconomyUtils.GetResourceIndex(iterator.resource)];
                                }
                            }
                            else if ((propertyData.m_AllowedManufactured & iterator.resource) != Resource.NoResource)
                            {
                                sum += industrialDemands[EconomyUtils.GetResourceIndex(iterator.resource)];
                            }
                        }

                        return sum;
                    }

                    default:
                        return 0;
                }
            }
        }
    }
}
