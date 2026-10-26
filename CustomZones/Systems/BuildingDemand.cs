// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace CustomZones.Systems
{
    using System.Collections.Generic;
    using Colossal.Entities;
    using Game.Buildings;
    using Game.Economy;
    using Game.Prefabs;
    using Game.Simulation;
    using Game.Zones;
    using Unity.Entities;
    using Unity.Mathematics;
    using CustomZones.Domain;

    /// <summary>
    /// Whether the city currently wants more of a building, read from the game's
    /// demand systems the same way the zone spawner decides (see
    /// PaletteZoneSpawnSystem.EvaluateDemandAndAvailability): residential by
    /// density, commercial by the goods sold, industry and offices by the goods
    /// made, warehouses by the goods stored. A building only grows while that
    /// demand is at least 1.
    /// </summary>
    internal sealed class BuildingDemand
    {
        private readonly EntityManager m_EntityManager;
        private readonly ResidentialDemandSystem m_Residential;
        private readonly CommercialDemandSystem m_Commercial;
        private readonly IndustrialDemandSystem m_Industrial;

        private readonly Dictionary<Entity, (Resource goods, Kind kind, ZoneDensity density)> m_Goods = new();

        private int3 m_ResidentialDemand;
        private int[] m_CommercialDemands = System.Array.Empty<int>();
        private int[] m_IndustrialDemands = System.Array.Empty<int>();
        private int[] m_StorageDemands = System.Array.Empty<int>();

        public BuildingDemand(World world)
        {
            m_EntityManager = world.EntityManager;
            m_Residential = world.GetOrCreateSystemManaged<ResidentialDemandSystem>();
            m_Commercial = world.GetOrCreateSystemManaged<CommercialDemandSystem>();
            m_Industrial = world.GetOrCreateSystemManaged<IndustrialDemandSystem>();
        }

        private enum Kind
        {
            Residential,
            Commercial,
            Industrial,
            Storage,
            Office,
            Other,
        }

        /// <summary>Copies the current demand values. Returns true when any of them changed.</summary>
        public bool Refresh()
        {
            var changed = false;

            var residential = m_Residential.buildingDemand;
            if (!residential.Equals(m_ResidentialDemand))
            {
                m_ResidentialDemand = residential;
                changed = true;
            }

            var commercial = m_Commercial.GetBuildingDemands(out var commercialDependency);
            commercialDependency.Complete();
            changed |= CopyIfChanged(commercial, ref m_CommercialDemands);

            var industrial = m_Industrial.GetBuildingDemands(out var industrialDependency);
            industrialDependency.Complete();
            changed |= CopyIfChanged(industrial, ref m_IndustrialDemands);

            var storage = m_Industrial.GetStorageBuildingDemands(out var storageDependency);
            storageDependency.Complete();
            changed |= CopyIfChanged(storage, ref m_StorageDemands);

            return changed;
        }

        /// <summary>What the building does with its goods: "sells", "makes", "stores", "office" ("" for housing).</summary>
        public string RoleOf(BuildingEntry building)
        {
            switch (Describe(building.PrefabEntity).kind)
            {
                case Kind.Commercial: return "sells";
                case Kind.Industrial: return "makes";
                case Kind.Storage: return "stores";
                case Kind.Office: return "office";
                default: return string.Empty;
            }
        }

        /// <summary>The goods a building sells / makes / stores (NoResource for housing).</summary>
        public Resource GoodsOf(BuildingEntry building) => Describe(building.PrefabEntity).goods;

        /// <summary>
        /// The residential demand bar a density uses (0 low, 1 medium, 2 high), the
        /// same classification the game applies to the building's own zone
        /// (PropertyUtils.GetZoneDensity): row housing and medium are both medium.
        /// </summary>
        public static int ResidentialBar(ZoneDensity density) => density == ZoneDensity.Low ? 0 : density == ZoneDensity.Medium ? 1 : 2;

        /// <summary>
        /// The city's demand for one more building like this one in the given zone
        /// (a building grows only when it is at least 1).
        /// </summary>
        public int DemandFor(BuildingEntry building, PaletteDefinition zone)
        {
            var (goods, kind, density) = Describe(building.PrefabEntity);
            switch (kind)
            {
                case Kind.Residential:
                    // The bar of the building's own density (the game counts homes
                    // and vacancies per density from the building's zone).
                    return ResidentialBar(density) switch
                    {
                        0 => m_ResidentialDemand.x,
                        1 => m_ResidentialDemand.y,
                        _ => m_ResidentialDemand.z,
                    };
                case Kind.Commercial:
                    return Sum(m_CommercialDemands, goods);
                case Kind.Industrial:
                case Kind.Office:
                    return Sum(m_IndustrialDemands, goods);
                case Kind.Storage:
                    return Sum(m_StorageDemands, goods);
                default:
                    return 1;
            }
        }

        /// <summary>Resource name as the game's icons and texts use it ("Lodging" -> Media/Game/Resources/Lodging.svg).</summary>
        public static string NameOf(Resource goods)
        {
            if (goods == Resource.NoResource)
            {
                return string.Empty;
            }

            var iterator = ResourceIterator.GetIterator();
            while (iterator.Next())
            {
                if ((goods & iterator.resource) != Resource.NoResource)
                {
                    return iterator.resource.ToString();
                }
            }

            return string.Empty;
        }

        private (Resource goods, Kind kind, ZoneDensity density) Describe(Entity prefab)
        {
            if (m_Goods.TryGetValue(prefab, out var known))
            {
                return known;
            }

            var result = (Resource.NoResource, Kind.Other, ZoneDensity.Low);
            if (m_EntityManager.TryGetComponent<SpawnableBuildingData>(prefab, out var spawnable) &&
                m_EntityManager.TryGetComponent<ZoneData>(spawnable.m_ZonePrefab, out var zone) &&
                m_EntityManager.TryGetComponent<BuildingPropertyData>(prefab, out var properties))
            {
                m_EntityManager.TryGetComponent<ZonePropertiesData>(spawnable.m_ZonePrefab, out var zoneProperties);
                var density = PropertyUtils.GetZoneDensity(zone, zoneProperties);
                switch (zone.m_AreaType)
                {
                    case AreaType.Residential:
                        result = (Resource.NoResource, Kind.Residential, density);
                        break;
                    case AreaType.Commercial:
                        result = (properties.m_AllowedSold, Kind.Commercial, density);
                        break;
                    case AreaType.Industrial:
                        result = m_EntityManager.HasComponent<WarehouseData>(prefab)
                            ? (properties.m_AllowedStored, Kind.Storage, density)
                            : m_EntityManager.HasComponent<OfficeBuilding>(prefab)
                                ? (properties.m_AllowedManufactured, Kind.Office, density)
                                : (properties.m_AllowedManufactured, Kind.Industrial, density);
                        break;
                }
            }

            m_Goods[prefab] = result;
            return result;
        }

        private static int Sum(int[] demands, Resource goods)
        {
            var sum = 0;
            var iterator = ResourceIterator.GetIterator();
            while (iterator.Next())
            {
                if ((goods & iterator.resource) != Resource.NoResource)
                {
                    var index = EconomyUtils.GetResourceIndex(iterator.resource);
                    if (index >= 0 && index < demands.Length)
                    {
                        sum += demands[index];
                    }
                }
            }

            return sum;
        }

        private static bool CopyIfChanged(Unity.Collections.NativeArray<int> source, ref int[] target)
        {
            if (!source.IsCreated)
            {
                return false;
            }

            var changed = target.Length != source.Length;
            if (changed)
            {
                target = new int[source.Length];
            }

            for (var i = 0; i < source.Length; i++)
            {
                if (target[i] != source[i])
                {
                    target[i] = source[i];
                    changed = true;
                }
            }

            return changed;
        }
    }
}
