// <copyright file="BuildingDemand.cs">
// Custom Zones - Licensed under the MIT license.
// </copyright>

namespace CustomZones.Systems
{
    using System.Collections.Generic;
    using Colossal.Entities;
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

        private readonly Dictionary<Entity, (Resource goods, Kind kind)> m_Goods = new();

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
        /// The city's demand for one more building like this one in the given zone
        /// (a building grows only when it is at least 1).
        /// </summary>
        public int DemandFor(BuildingEntry building, PaletteDefinition zone)
        {
            var (goods, kind) = Describe(building.PrefabEntity);
            switch (kind)
            {
                case Kind.Residential:
                    var density = zone?.Density ?? building.Density;
                    return density == DensityType.Low ? m_ResidentialDemand.x
                        : density == DensityType.High ? m_ResidentialDemand.z
                        : m_ResidentialDemand.y;
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

        private (Resource goods, Kind kind) Describe(Entity prefab)
        {
            if (m_Goods.TryGetValue(prefab, out var known))
            {
                return known;
            }

            var result = (Resource.NoResource, Kind.Other);
            if (m_EntityManager.TryGetComponent<SpawnableBuildingData>(prefab, out var spawnable) &&
                m_EntityManager.TryGetComponent<ZoneData>(spawnable.m_ZonePrefab, out var zone) &&
                m_EntityManager.TryGetComponent<BuildingPropertyData>(prefab, out var properties))
            {
                switch (zone.m_AreaType)
                {
                    case AreaType.Residential:
                        result = (Resource.NoResource, Kind.Residential);
                        break;
                    case AreaType.Commercial:
                        result = (properties.m_AllowedSold, Kind.Commercial);
                        break;
                    case AreaType.Industrial:
                        result = m_EntityManager.HasComponent<WarehouseData>(prefab)
                            ? (properties.m_AllowedStored, Kind.Storage)
                            : m_EntityManager.HasComponent<OfficeBuilding>(prefab)
                                ? (properties.m_AllowedManufactured, Kind.Office)
                                : (properties.m_AllowedManufactured, Kind.Industrial);
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
