// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// The zone spawner of the city. It replaces the game's ZoneSpawnSystem
// (disabled in Mod.OnLoad) and runs at the same pace, so the city grows at the
// game's own rate:
//   - vanilla zones: the game's own EvaluateSpawnAreas job, unchanged;
//   - custom zones: our EvaluateSpawnAreas, which picks from the zone's
//     catalog (PaletteAllowedBuilding) with the game's demand and score rules,
//     evaluating every building by its OWN zone (type, density, office,
//     process estimates), so one custom zone may mix densities and types.
// Both feed the same three candidate queues, and the game's SpawnBuildingJob
// then spawns the single best candidate of each type, exactly like the game.
//
// The idea of spawning from a list of allowed buildings comes from ZoningByLaw
// by FutureProg (reimplemented, not copied).

namespace CustomZones.Systems
{
    using System.Collections.Generic;
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

    /// <summary>
    /// The city's zone spawner, for the game's zones and the custom zones (see the
    /// file header).
    /// </summary>
    public partial class PaletteZoneSpawnSystem : GameSystemBase
    {
        private EntityQuery m_VacantLotsQuery;
        private EntityQuery m_ProcessQuery;
        private EntityQuery m_BuildingConfigQuery;
        private EntityQuery m_BuildingQuery;
        private EntityArchetype m_DefinitionArchetype;

        private global::Game.Prefabs.ZoneSystem m_ZoneSystem;
        private ZoneSpawnSystem m_ZoneSpawnSystem;
        private ResidentialDemandSystem m_ResidentialDemandSystem;
        private CommercialDemandSystem m_CommercialDemandSystem;
        private IndustrialDemandSystem m_IndustrialDemandSystem;
        private GroundPollutionSystem m_PollutionSystem;
        private AirPollutionSystem m_AirPollutionSystem;
        private NoisePollutionSystem m_NoisePollutionSystem;
        private TerrainSystem m_TerrainSystem;
        private global::Game.Zones.SearchSystem m_SearchSystem;
        private ResourceSystem m_ResourceSystem;
        private CityConfigurationSystem m_CityConfigurationSystem;
        private EndFrameBarrier m_EndFrameBarrier;

        // Growth diagnosis: one entry per custom-zone lot evaluated, (zone index << 4) | stage.
        private NativeQueue<int> m_LotResults;
        private JobHandle m_LotResultsWriter;
        private readonly Dictionary<ushort, ZoneGrowth> m_Growth = new();
        private bool m_SpawnResidential;
        private bool m_SpawnCommercial;
        private bool m_SpawnIndustrial;
        private bool m_SpawnStorage;

        /// <summary>How far the lots of a custom zone got in the last spawn pass (best stage reached).</summary>
        public struct ZoneGrowth
        {
            /// <summary>Free lots of the zone that were evaluated.</summary>
            public int Lots;

            /// <summary>Best stage any lot reached (a <see cref="GrowthStage"/>).</summary>
            public GrowthStage Stage;
        }

        /// <summary>What stopped the zone's buildings, in the order the spawner checks.</summary>
        public enum GrowthStage
        {
            /// <summary>No free lot was evaluated.</summary>
            NoLots = -1,

            /// <summary>No chosen building fits a free lot (size, height).</summary>
            NothingFits = 0,

            /// <summary>Some fit, but the city has no demand for them.</summary>
            NoDemand = 1,

            /// <summary>Wanted, but the location scores too low for them.</summary>
            LowScore = 2,

            /// <summary>A building was picked for a lot: it is growing.</summary>
            Growing = 3,

            /// <summary>The demand bar of this zone type is empty.</summary>
            TypeHasNoDemand = 4,
        }

        /// <summary>Growth diagnosis of a custom zone (by zone type index); false before the first pass.</summary>
        public bool TryGetGrowth(ushort zoneIndex, out ZoneGrowth growth)
        {
            if (m_Growth.TryGetValue(zoneIndex, out growth))
            {
                return true;
            }

            growth = new ZoneGrowth { Lots = 0, Stage = GrowthStage.NoLots };
            return m_HasRun;
        }

        /// <summary>Whether the game lets this zone type grow at all now (its demand bar is not empty).</summary>
        public bool TypeCanGrow(AreaType areaType) => areaType switch
        {
            AreaType.Residential => m_SpawnResidential,
            AreaType.Commercial => m_SpawnCommercial,
            AreaType.Industrial => m_SpawnIndustrial || m_SpawnStorage,
            _ => true,
        };

        private bool m_HasRun;
        private readonly Dictionary<ushort, GrowthStage> m_LoggedStages = new();

        // Same pace as the game's ZoneSpawnSystem (every 16 frames, offset 13).
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        public override int GetUpdateOffset(SystemUpdatePhase phase) => 13;

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
            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<BuildingData>(),
                ComponentType.ReadOnly<SpawnableBuildingData>(),
                ComponentType.ReadOnly<BuildingSpawnGroupData>(),
                ComponentType.ReadOnly<PrefabData>());
            m_BuildingConfigQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());

            m_ZoneSystem = World.GetOrCreateSystemManaged<global::Game.Prefabs.ZoneSystem>();
            m_ZoneSpawnSystem = World.GetOrCreateSystemManaged<ZoneSpawnSystem>();
            m_ResidentialDemandSystem = World.GetOrCreateSystemManaged<ResidentialDemandSystem>();
            m_CommercialDemandSystem = World.GetOrCreateSystemManaged<CommercialDemandSystem>();
            m_IndustrialDemandSystem = World.GetOrCreateSystemManaged<IndustrialDemandSystem>();
            m_PollutionSystem = World.GetOrCreateSystemManaged<GroundPollutionSystem>();
            m_AirPollutionSystem = World.GetOrCreateSystemManaged<AirPollutionSystem>();
            m_NoisePollutionSystem = World.GetOrCreateSystemManaged<NoisePollutionSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_SearchSystem = World.GetOrCreateSystemManaged<global::Game.Zones.SearchSystem>();
            m_ResourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();
            m_CityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            RequireForUpdate(m_VacantLotsQuery);
            m_LotResults = new NativeQueue<int>(Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            m_LotResultsWriter.Complete();
            if (m_LotResults.IsCreated)
            {
                m_LotResults.Dispose();
            }

            base.OnDestroy();
        }

        /// <summary>Folds the previous pass's per-lot results into the per-zone diagnosis.</summary>
        private void CollectLotResults()
        {
            m_LotResultsWriter.Complete();
            m_Growth.Clear();
            while (m_LotResults.TryDequeue(out var result))
            {
                var zoneIndex = (ushort)(result >> 4);
                var stage = (GrowthStage)(result & 0xF);
                m_Growth.TryGetValue(zoneIndex, out var growth);
                if (growth.Lots == 0)
                {
                    growth.Stage = stage;
                }
                else if (stage != GrowthStage.TypeHasNoDemand && (growth.Stage == GrowthStage.TypeHasNoDemand || stage > growth.Stage))
                {
                    growth.Stage = stage;
                }

                growth.Lots++;
                m_Growth[zoneIndex] = growth;
            }

            m_HasRun = true;

            // Log when a zone's diagnosis changes (support / debugging).
            foreach (var kv in m_Growth)
            {
                if (!m_LoggedStages.TryGetValue(kv.Key, out var logged) || logged != kv.Value.Stage)
                {
                    m_LoggedStages[kv.Key] = kv.Value.Stage;
                    Mod.Log.Info($"Growth: custom zone type {kv.Key}: {kv.Value.Lots} free lots, {kv.Value.Stage}.");
                }
            }
        }

        // Cost on the main thread (the evaluation itself runs in Burst jobs, like the
        // game's): logged now and then so a slow city can be diagnosed from the log.
        private readonly System.Diagnostics.Stopwatch m_Timer = new();
        private int m_TimedPasses;
        private double m_TimedMs;
        private double m_MaxMs;

        protected override void OnUpdate()
        {
            try
            {
                m_Timer.Restart();
                UpdateSpawn();
                m_Timer.Stop();
                var ms = m_Timer.Elapsed.TotalMilliseconds;
                m_TimedMs += ms;
                m_MaxMs = System.Math.Max(m_MaxMs, ms);
                if (++m_TimedPasses >= 4096)
                {
                    Mod.Log.Info($"Spawn: {m_TimedPasses} passes, main thread {m_TimedMs / m_TimedPasses:0.###} ms on average, {m_MaxMs:0.##} ms at most.");
                    m_TimedPasses = 0;
                    m_TimedMs = 0;
                    m_MaxMs = 0;
                }
            }
            catch (System.Exception ex)
            {
                // A game update changed something this system relies on: hand growth
                // back to the game's own spawner rather than leave the city frozen.
                Mod.Log.Error(ex, "Spawn: failed; the game's own zone spawner takes over (custom zones stop growing until the mod is updated).");
                m_ZoneSpawnSystem.Enabled = true;
                Enabled = false;
            }
        }

        private void UpdateSpawn()
        {
            // The game's overall gates: no type grows while its demand bar is empty.
            var fastSpawn = m_ZoneSpawnSystem.debugFastSpawn;
            var residentialDemand = m_ResidentialDemandSystem.buildingDemand;
            var spawnResidential = fastSpawn || (residentialDemand.x + residentialDemand.y + residentialDemand.z) / 3 > 0;
            var spawnCommercial = fastSpawn || m_CommercialDemandSystem.buildingDemand > 0;
            var spawnIndustrial = fastSpawn || (m_IndustrialDemandSystem.industrialBuildingDemand + m_IndustrialDemandSystem.officeBuildingDemand) / 2 > 0;
            var spawnStorage = fastSpawn || m_IndustrialDemandSystem.storageBuildingDemand > 0;
            m_SpawnResidential = spawnResidential;
            m_SpawnCommercial = spawnCommercial;
            m_SpawnIndustrial = spawnIndustrial;
            m_SpawnStorage = spawnStorage;

            CollectLotResults();

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
                lockedLookup = GetComponentLookup<Locked>(true),
                resourcePrefabs = m_ResourceSystem.GetPrefabs(),

                groundPollutionMap = m_PollutionSystem.GetMap(true, out var pollutionJob),
                airPollutionMap = m_AirPollutionSystem.GetMap(true, out var airPollutionJob),
                noisePollutionMap = m_NoisePollutionSystem.GetMap(true, out var noisePollutionJob),
                spawnResidential = spawnResidential,
                spawnCommercial = spawnCommercial,
                spawnIndustrial = spawnIndustrial,
                spawnStorage = spawnStorage,
                zonePreferenceData = SystemAPI.GetSingleton<ZonePreferenceData>(),
                industrialProcesses = m_ProcessQuery.ToComponentDataListAsync<IndustrialProcessData>(World.UpdateAllocator.ToAllocator, out var processJob),

                residentialDemands = m_ResidentialDemandSystem.buildingDemand,
                commercialDemands = m_CommercialDemandSystem.GetBuildingDemands(out var commercialDemandJob),
                industrialDemands = m_IndustrialDemandSystem.GetBuildingDemands(out var industrialDemandJob),
                storageDemands = m_IndustrialDemandSystem.GetStorageBuildingDemands(out var storageDemandJob),

                lotResults = m_LotResults.AsParallelWriter(),
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

            // The game's own evaluation for the vanilla zones, into the same queues.
            var vanilla = new ZoneSpawnSystem.EvaluateSpawnAreas
            {
                m_BuildingChunks = m_BuildingQuery.ToArchetypeChunkListAsync(World.UpdateAllocator.ToAllocator, out var buildingChunksJob),
                m_ZonePrefabs = m_ZoneSystem.GetPrefabs(),
                m_Preferences = SystemAPI.GetSingleton<ZonePreferenceData>(),
                m_SpawnResidential = spawnResidential ? 1 : 0,
                m_SpawnCommercial = spawnCommercial ? 1 : 0,
                m_SpawnIndustrial = spawnIndustrial ? 1 : 0,
                m_SpawnStorage = spawnStorage ? 1 : 0,
                m_MinDemand = fastSpawn ? 0 : 1,
                m_ResidentialDemands = residentialDemand,
                m_CommercialBuildingDemands = evaluate.commercialDemands,
                m_IndustrialDemands = evaluate.industrialDemands,
                m_StorageDemands = evaluate.storageDemands,
                m_RandomSeed = RandomSeed.Next(),
                m_EntityType = GetEntityTypeHandle(),
                m_BlockType = GetComponentTypeHandle<Block>(true),
                m_OwnerType = GetComponentTypeHandle<Owner>(true),
                m_CurvePositionType = GetComponentTypeHandle<CurvePosition>(true),
                m_VacantLotType = GetBufferTypeHandle<VacantLot>(true),
                m_BuildingDataType = GetComponentTypeHandle<BuildingData>(true),
                m_SpawnableBuildingType = GetComponentTypeHandle<SpawnableBuildingData>(true),
                m_BuildingPropertyType = GetComponentTypeHandle<BuildingPropertyData>(true),
                m_ObjectGeometryType = GetComponentTypeHandle<ObjectGeometryData>(true),
                m_BuildingSpawnGroupType = GetSharedComponentTypeHandle<BuildingSpawnGroupData>(),
                m_WarehouseType = GetComponentTypeHandle<WarehouseData>(true),
                m_ZoneData = GetComponentLookup<ZoneData>(true),
                m_ZonePropertiesDatas = GetComponentLookup<ZonePropertiesData>(true),
                m_Availabilities = GetBufferLookup<ResourceAvailability>(true),
                m_LandValues = GetComponentLookup<LandValue>(true),
                m_BlockData = GetComponentLookup<Block>(true),
                m_Processes = evaluate.industrialProcesses,
                m_ProcessEstimates = GetBufferLookup<ProcessEstimate>(true),
                m_ResourceDatas = GetComponentLookup<ResourceData>(true),
                m_ResourcePrefabs = evaluate.resourcePrefabs,
                m_GroundPollutionMap = evaluate.groundPollutionMap,
                m_AirPollutionMap = evaluate.airPollutionMap,
                m_NoisePollutionMap = evaluate.noisePollutionMap,
                m_Residential = residentialQ.AsParallelWriter(),
                m_Commercial = commercialQ.AsParallelWriter(),
                m_Industrial = industrialQ.AsParallelWriter(),
            };

            var inputs = JobHandle.CombineDependencies(
                JobHandle.CombineDependencies(Dependency, pollutionJob, processJob),
                JobHandle.CombineDependencies(airPollutionJob, noisePollutionJob, buildingChunksJob),
                JobHandle.CombineDependencies(commercialDemandJob, industrialDemandJob, storageDemandJob));

            var vanillaHandle = vanilla.ScheduleParallel(m_VacantLotsQuery, inputs);
            var evaluateHandle = evaluate.ScheduleParallel(m_VacantLotsQuery, vanillaHandle);

            var spawnHandle = spawnBuildingJob.Schedule(3, 1, JobHandle.CombineDependencies(evaluateHandle, searchTreeJob));

            m_ResourceSystem.AddPrefabsReader(evaluateHandle);
            m_PollutionSystem.AddReader(evaluateHandle);
            m_AirPollutionSystem.AddReader(evaluateHandle);
            m_NoisePollutionSystem.AddReader(evaluateHandle);
            m_CommercialDemandSystem.AddReader(evaluateHandle);
            m_IndustrialDemandSystem.AddReader(evaluateHandle);

            residentialQ.Dispose(spawnHandle);
            commercialQ.Dispose(spawnHandle);
            industrialQ.Dispose(spawnHandle);

            m_ZoneSystem.AddPrefabsReader(evaluateHandle);
            m_TerrainSystem.AddCPUHeightReader(spawnHandle);
            m_EndFrameBarrier.AddJobHandleForProducer(spawnHandle);
            m_SearchSystem.AddSearchTreeReader(spawnHandle);

            m_LotResultsWriter = evaluateHandle;
            Dependency = spawnHandle;
        }

        /// <summary>
        /// The game's spawn evaluation for the lots of custom zones: each chosen
        /// building that fits a free lot is scored by the rules of its own zone and
        /// offered to the queue of its type.
        /// </summary>
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
            [ReadOnly] public ComponentLookup<Locked> lockedLookup;
            [ReadOnly] public ResourcePrefabs resourcePrefabs;

            [ReadOnly] public NativeArray<GroundPollution> groundPollutionMap;
            [ReadOnly] public NativeArray<AirPollution> airPollutionMap;
            [ReadOnly] public NativeArray<NoisePollution> noisePollutionMap;
            public bool spawnResidential;
            public bool spawnCommercial;
            public bool spawnIndustrial;
            public bool spawnStorage;
            public ZonePreferenceData zonePreferenceData;
            [ReadOnly] public NativeList<IndustrialProcessData> industrialProcesses;

            public int3 residentialDemands;
            [ReadOnly] public NativeArray<int> commercialDemands;
            [ReadOnly] public NativeArray<int> industrialDemands;
            [ReadOnly] public NativeArray<int> storageDemands;

            public NativeQueue<int>.ParallelWriter lotResults;
            public NativeQueue<ZoneSpawnSystem.SpawnLocation>.ParallelWriter residentialQ;
            public NativeQueue<ZoneSpawnSystem.SpawnLocation>.ParallelWriter commercialQ;
            public NativeQueue<ZoneSpawnSystem.SpawnLocation>.ParallelWriter industrialQ;

            public RandomSeed randomSeed;
            public int minDemand;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var random = randomSeed.GetRandom(unfilteredChunkIndex);

                // Best lot per type, like the game (one candidate of each type per chunk).
                var bestResidential = default(ZoneSpawnSystem.SpawnLocation);
                var bestCommercial = default(ZoneSpawnSystem.SpawnLocation);
                var bestIndustrial = default(ZoneSpawnSystem.SpawnLocation);

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
                            !zoneDataLookup.TryGetComponent(zonePrefab, out var zoneData))
                        {
                            continue;
                        }

                        // The game cuts every free run wider than 8 cells into two lots
                        // that each reach the middle, so a building wider than half the run
                        // never fits and the halves get built from the middle. A custom
                        // zone's catalog may only have big buildings: evaluate the whole
                        // run instead (the building is then placed at one of its ends).
                        if (!MergeRun(vacantLots, j, ref vacantLot))
                        {
                            continue; // already evaluated as part of an earlier lot's run
                        }

                        var curvePosScalar = CalculateCurvePos(curvePos, vacantLot, block);

                        var zoneIndex = zoneData.m_ZoneType.m_Index;

                        // A custom zone may mix types: each type of building in its
                        // catalog competes in that type's queue, under that type's gate.
                        var types = CatalogTypes(allowed);
                        var stage = GrowthStage.NoLots;
                        var anyOpen = false;
                        if ((types & 1) != 0 && spawnResidential)
                        {
                            anyOpen = true;
                            stage = Max(stage, TryAddLot(allowed, AreaType.Residential, ref bestResidential, ref random, owner.m_Owner, curvePosScalar, entity,
                                vacantLot.m_Area, vacantLot.m_Flags, (int)vacantLot.m_Height, zoneData, true, false));
                        }

                        if ((types & 2) != 0 && spawnCommercial)
                        {
                            anyOpen = true;
                            stage = Max(stage, TryAddLot(allowed, AreaType.Commercial, ref bestCommercial, ref random, owner.m_Owner, curvePosScalar, entity,
                                vacantLot.m_Area, vacantLot.m_Flags, (int)vacantLot.m_Height, zoneData, true, false));
                        }

                        if ((types & 4) != 0 && (spawnIndustrial || spawnStorage))
                        {
                            anyOpen = true;
                            stage = Max(stage, TryAddLot(allowed, AreaType.Industrial, ref bestIndustrial, ref random, owner.m_Owner, curvePosScalar, entity,
                                vacantLot.m_Area, vacantLot.m_Flags, (int)vacantLot.m_Height, zoneData, spawnIndustrial, spawnStorage));
                        }

                        lotResults.Enqueue((zoneIndex << 4) | (int)(anyOpen ? Max(stage, GrowthStage.NothingFits) : GrowthStage.TypeHasNoDemand));
                    }
                }

                if (bestResidential.m_Priority != 0f)
                {
                    residentialQ.Enqueue(bestResidential);
                }

                if (bestCommercial.m_Priority != 0f)
                {
                    commercialQ.Enqueue(bestCommercial);
                }

                if (bestIndustrial.m_Priority != 0f)
                {
                    industrialQ.Enqueue(bestIndustrial);
                }
            }

            /// <summary>
            /// Joins the lot with the other half of its free run (same zone, same
            /// depth, touching). Returns false when the other half comes first in
            /// the buffer (the run was evaluated with it).
            /// </summary>
            private static bool MergeRun(DynamicBuffer<VacantLot> lots, int index, ref VacantLot lot)
            {
                for (var k = 0; k < lots.Length; k++)
                {
                    if (k == index)
                    {
                        continue;
                    }

                    var other = lots[k];
                    if (!other.m_Type.Equals(lot.m_Type) ||
                        other.m_Area.z != lot.m_Area.z || other.m_Area.w != lot.m_Area.w ||
                        other.m_Area.x > lot.m_Area.y || other.m_Area.y < lot.m_Area.x)
                    {
                        continue;
                    }

                    if (k < index)
                    {
                        return false;
                    }

                    lot.m_Area.x = math.min(lot.m_Area.x, other.m_Area.x);
                    lot.m_Area.y = math.max(lot.m_Area.y, other.m_Area.y);
                    lot.m_Flags |= other.m_Flags;
                    lot.m_Height = (short)math.min((int)lot.m_Height, (int)other.m_Height);
                }

                return true;
            }

            private bool IsLocked(Entity prefab) => lockedLookup.HasComponent(prefab) && lockedLookup.IsComponentEnabled(prefab);

            private static GrowthStage Max(GrowthStage a, GrowthStage b) => (int)a >= (int)b ? a : b;

            /// <summary>Area types present in a catalog (bit 1 residential, 2 commercial, 4 industrial/office).</summary>
            private int CatalogTypes(DynamicBuffer<PaletteAllowedBuilding> allowed)
            {
                var types = 0;
                for (var i = 0; i < allowed.Length; i++)
                {
                    if (spawnableLookup.TryGetComponent(allowed[i].m_Building, out var spawnable) &&
                        zoneDataLookup.TryGetComponent(spawnable.m_ZonePrefab, out var buildingZone))
                    {
                        types |= buildingZone.m_AreaType switch
                        {
                            AreaType.Residential => 1,
                            AreaType.Commercial => 2,
                            AreaType.Industrial => 4,
                            _ => 0,
                        };
                    }
                }

                return types;
            }

            private float CalculateCurvePos(CurvePosition curvePosition, VacantLot lot, Block block)
            {
                var s = math.saturate((lot.m_Area.x + lot.m_Area.y) * 0.5f / block.m_Size.x);
                return math.lerp(curvePosition.m_CurvePosition.x, curvePosition.m_CurvePosition.y, s);
            }

            private GrowthStage TryAddLot(DynamicBuffer<PaletteAllowedBuilding> allowed, AreaType type, ref ZoneSpawnSystem.SpawnLocation bestLocation,
                ref Random random, Entity road, float curvePos, Entity entity, int4 area, LotFlags flags, int height,
                ZoneData zoneData, bool normal, bool storage)
            {
                if (!resourceAvailabilityLookup.HasBuffer(road))
                {
                    return GrowthStage.NothingFits;
                }

                // Corner flags are masked per building below, by the building's own
                // zone (the game masks them by the lot's zone, which is the same
                // zone for every building it considers).
                var spawnLocation = default(ZoneSpawnSystem.SpawnLocation);
                spawnLocation.m_Entity = entity;
                spawnLocation.m_LotArea = area;
                spawnLocation.m_ZoneType = zoneData.m_ZoneType;
                spawnLocation.m_AreaType = type;
                spawnLocation.m_LotFlags = flags;

                var availabilities = resourceAvailabilityLookup[road];

                if (!blockLookup.HasComponent(spawnLocation.m_Entity))
                {
                    return GrowthStage.NothingFits;
                }

                var position = ZoneUtils.GetPosition(blockLookup[spawnLocation.m_Entity], spawnLocation.m_LotArea.xz, spawnLocation.m_LotArea.yw);
                // Ground, noise and air pollution, in the order the game's score expects.
                var pollution = new float3(
                    GroundPollutionSystem.GetPollution(position, groundPollutionMap).m_Pollution,
                    NoisePollutionSystem.GetPollution(position, noisePollutionMap).m_Pollution,
                    AirPollutionSystem.GetPollution(position, airPollutionMap).m_Pollution);
                var landValue = landValueLookup[road].m_LandValue;
                var maxHeight = height - position.y;

                var selected = SelectBuilding(allowed, type, ref spawnLocation, ref random, availabilities, zoneData,
                    curvePos, pollution, landValue, maxHeight, normal, storage, out var stage);

                if (selected && spawnLocation.m_Priority > bestLocation.m_Priority)
                {
                    bestLocation = spawnLocation;
                }

                return stage;
            }

            private bool SelectBuilding(DynamicBuffer<PaletteAllowedBuilding> allowed, AreaType type, ref ZoneSpawnSystem.SpawnLocation location,
                ref Random random, DynamicBuffer<ResourceAvailability> availabilities, ZoneData zoneData,
                float curvePos, float3 pollution, float landValue, float maxHeight,
                bool normal, bool storage, out GrowthStage stage)
            {
                stage = GrowthStage.NothingFits;
                var maxLotSize = location.m_LotArea.yw - location.m_LotArea.xz;
                var chosenBuildingData = default(BuildingData);

                for (var i = 0; i < allowed.Length; i++)
                {
                    var building = allowed[i].m_Building;

                    // Any level: a zone may choose a building at level 3 for its design,
                    // and it is then born at that level.
                    if (!spawnableLookup.TryGetComponent(building, out var spawnableData) ||
                        !buildingLookup.TryGetComponent(building, out var subjBuildingData) ||
                        !propertyLookup.TryGetComponent(building, out var buildingPropertyData) ||
                        !objGeomLookup.TryGetComponent(building, out var objGeom) ||
                        !zoneDataLookup.TryGetComponent(spawnableData.m_ZonePrefab, out var buildingZone) ||
                        buildingZone.m_AreaType != type)
                    {
                        continue;
                    }

                    // Progression: a building of a zone the city has not unlocked yet
                    // (milestones) does not grow, as it could not in the game.
                    if (IsLocked(spawnableData.m_ZonePrefab) || IsLocked(building))
                    {
                        continue;
                    }

                    var lotFlags = location.m_LotFlags;
                    if ((buildingZone.m_ZoneFlags & ZoneFlags.SupportLeftCorner) == 0)
                    {
                        lotFlags &= ~LotFlags.CornerLeft;
                    }

                    if ((buildingZone.m_ZoneFlags & ZoneFlags.SupportRightCorner) == 0)
                    {
                        lotFlags &= ~LotFlags.CornerRight;
                    }

                    var lotAccess = new bool2((lotFlags & LotFlags.CornerLeft) != 0, (lotFlags & LotFlags.CornerRight) != 0);
                    var supportsNarrow = (buildingZone.m_ZoneFlags & ZoneFlags.SupportNarrow) == 0;

                    // Everything about demand and score comes from the building's own
                    // zone (its density bar, office or industry, process estimates),
                    // so buildings of several densities or types can share a zone.
                    zonePropertiesDataLookup.TryGetComponent(spawnableData.m_ZonePrefab, out var buildingZoneProperties);
                    if (!processEstimatesLookup.TryGetBuffer(spawnableData.m_ZonePrefab, out var estimates))
                    {
                        continue;
                    }

                    // As the game decides it: an industrial zone without process estimates is office.
                    var office = buildingZone.m_AreaType == AreaType.Industrial && estimates.Length == 0;

                    var lotSize = subjBuildingData.m_LotSize;
                    var buildingAccess = new bool2(
                        (subjBuildingData.m_Flags & Game.Prefabs.BuildingFlags.LeftAccess) > 0,
                        (subjBuildingData.m_Flags & Game.Prefabs.BuildingFlags.RightAccess) > 0);
                    var bldgHeight = objGeom.m_Size.y;

                    if (!math.all(lotSize <= maxLotSize) || bldgHeight > maxHeight)
                    {
                        continue;
                    }

                    if (stage < GrowthStage.NoDemand)
                    {
                        stage = GrowthStage.NoDemand;
                    }

                    var isStorage = warehouseLookup.HasComponent(building);

                    // Industry: warehouses only while storage is in demand, the rest only while industry is.
                    if (isStorage ? !storage : !normal)
                    {
                        continue;
                    }
                    var evalAreaType = buildingZone.m_AreaType;
                    var zoneDensity = PropertyUtils.GetZoneDensity(buildingZone, buildingZoneProperties);
                    var num = EvaluateDemandAndAvailability(evalAreaType, buildingPropertyData, zoneDensity, isStorage);

                    if (num < minDemand)
                    {
                        continue;
                    }

                    if (stage < GrowthStage.LowScore)
                    {
                        stage = GrowthStage.LowScore;
                    }

                    var int2 = math.select(maxLotSize - lotSize, 0, lotSize == maxLotSize - 1);
                    var num2 = lotSize.x * lotSize.y * random.NextFloat(1f, 1.05f);
                    num2 += int2.x * lotSize.y * random.NextFloat(0.95f, 1f);
                    num2 += maxLotSize.x * int2.y * random.NextFloat(0.55f, 0.6f);
                    num2 /= maxLotSize.x * maxLotSize.y;
                    num2 *= num + 1;
                    // Corner access as the game weighs it: a lot corner the building
                    // does not face only lowers its score (0.01), it does not rule it out.
                    var accessMatchInfluence = 0.5f;
                    var accessMisMatchInfluence = 0.01f;
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

                    if (num2 > 0)
                    {
                        stage = GrowthStage.Growing;
                    }

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
