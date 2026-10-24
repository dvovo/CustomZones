// <copyright file="LevelUpControlSystem.cs">
// Custom Zones - Licensed under the MIT license.
// </copyright>

namespace CustomZones.Systems
{
    using System.Collections.Generic;
    using System.Linq;
    using Colossal.Collections;
    using Colossal.Entities;
    using Colossal.Mathematics;
    using Game;
    using Game.Buildings;
    using Game.Common;
    using Game.Notifications;
    using Game.Objects;
    using Game.Prefabs;
    using Game.Simulation;
    using Game.Tools;
    using Game.UI.InGame;
    using Game.Zones;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using CustomZones.Domain;
    using Transform = Game.Objects.Transform;

    /// <summary>
    /// Applies each custom zone's level-up rules.
    /// </summary>
    /// <remarks>
    /// When a building levels up, the game's BuildingUpkeepSystem picks the next
    /// prefab at random among every building of the same zone, level+1, lot size
    /// and goods (design and name are ignored), and adds an UnderConstruction
    /// component whose m_NewPrefab is that prefab; BuildingConstructionSystem
    /// swaps the prefab later. This system runs every frame just before that
    /// swap and, for buildings standing on a custom zone:
    /// <list type="bullet">
    /// <item>level-up blocked: removes the UnderConstruction (the building keeps its level);</item>
    /// <item>keep design: the next level of the same building (same name), or no level-up;</item>
    /// <item>otherwise: a random next-level building chosen for this zone, or no level-up.</item>
    /// </list>
    /// The game has already spent the building's condition on the level-up, so a
    /// cancelled one is not retried every tick.
    /// <para>
    /// It also runs the "level up" button of the building panel: instead of asking
    /// the game (whose level-up pass only runs ~16 times a day), it picks the next
    /// building right away with the same rules and starts the swap, which the game
    /// finishes on its next construction tick (about a second).
    /// </para>
    /// </remarks>
    public partial class LevelUpControlSystem : GameSystemBase
    {
        /// <summary>Set by the "level up" button of the building panel.</summary>
        public static bool LevelUpSelectedRequested;

        // LeftAccess | RightAccess: the flags the game matches when it levels up.
        private const Game.Prefabs.BuildingFlags LevelUpFlagsMask = (Game.Prefabs.BuildingFlags)12;

        // Buildings levelled up by hand (button), with the frame: their next building
        // is already chosen, so the zone rules are not applied again. Forgotten after
        // a while in case the swap never happens.
        private const int HandledLifetimeFrames = 3600;
        private readonly Dictionary<Entity, int> m_Handled = new();
        private int m_Frame;

        private EntityQuery m_BuildingConfigurationQuery;
        private IconCommandSystem m_IconCommandSystem;
        private ZoneBuiltRequirementSystem m_ZoneBuiltRequirementSystem;

        private EntityQuery m_UnderConstructionQuery;
        private PaletteZoneSystem m_ZoneSystem;
        private BuildingIndexSystem m_IndexSystem;
        private Game.Zones.SearchSystem m_ZoneSearchSystem;
        private Unity.Mathematics.Random m_Random;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_UnderConstructionQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadWrite<UnderConstruction>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            m_ZoneSystem = World.GetOrCreateSystemManaged<PaletteZoneSystem>();
            m_IndexSystem = World.GetOrCreateSystemManaged<BuildingIndexSystem>();
            m_ZoneSearchSystem = World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>();
            m_IconCommandSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
            m_ZoneBuiltRequirementSystem = World.GetOrCreateSystemManaged<ZoneBuiltRequirementSystem>();
            m_BuildingConfigurationQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());

            // Only chunks whose construction state changed (new level-ups) are looked at.
            m_UnderConstructionQuery.SetChangedVersionFilter(ComponentType.ReadWrite<UnderConstruction>());
            m_Random = new Unity.Mathematics.Random(0x6C7A31u);
        }

        protected override void OnUpdate()
        {
            m_Frame++;
            if (m_Handled.Count > 0 && (m_Frame & 63) == 0)
            {
                foreach (var stale in m_Handled.Where(kv => m_Frame - kv.Value > HandledLifetimeFrames).Select(kv => kv.Key).ToList())
                {
                    m_Handled.Remove(stale);
                }
            }

            if (LevelUpSelectedRequested)
            {
                LevelUpSelectedRequested = false;
                LevelUpSelectedBuilding();
            }

            if (m_UnderConstructionQuery.IsEmpty)
            {
                return;
            }

            // Level-ups only: the game marks them with a new prefab different from
            // the current one (new buildings under construction have none).
            var pending = new List<(Entity entity, UnderConstruction construction, Entity current)>();
            using (var entities = m_UnderConstructionQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (var entity in entities)
                {
                    if (m_Handled.ContainsKey(entity))
                    {
                        continue; // levelled up by hand: already decided
                    }

                    var construction = EntityManager.GetComponentData<UnderConstruction>(entity);
                    var current = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (construction.m_NewPrefab != Entity.Null && construction.m_NewPrefab != current)
                    {
                        pending.Add((entity, construction, current));
                    }
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            foreach (var (entity, construction, current) in pending)
            {
                if (!TryGetCustomZone(entity, out var zone))
                {
                    continue; // not on a custom zone: the game decides
                }

                Apply(entity, construction, current, zone);
            }
        }

        /// <summary>The custom zone under a building (false on a vanilla zone).</summary>
        private bool TryGetCustomZone(Entity building, out PaletteDefinition zone)
        {
            var tree = m_ZoneSearchSystem.GetSearchTree(true, out var treeDependency);
            treeDependency.Complete();

            var iterator = new CellIterator
            {
                m_Blocks = GetComponentLookup<Block>(true),
                m_Cells = GetBufferLookup<Cell>(true),
                m_Position = EntityManager.GetComponentData<Transform>(building).m_Position.xz,
            };
            tree.Iterate(ref iterator);

            return m_ZoneSystem.TryGetPaletteByZoneIndex(iterator.m_ZoneIndex, out zone);
        }

        private void Apply(Entity entity, UnderConstruction construction, Entity current, PaletteDefinition zone)
        {
            m_IndexSystem.ByEntity.TryGetValue(current, out var from);
            m_IndexSystem.ByEntity.TryGetValue(construction.m_NewPrefab, out var gameChoice);
            var fromName = from?.PrefabName ?? current.ToString();

            if (!zone.LevelUp)
            {
                EntityManager.RemoveComponent<UnderConstruction>(entity);
                Mod.Log.Info($"Level-up: '{fromName}' in zone '{zone.Name}' kept its level (level-up blocked).");
                return;
            }

            var target = from == null ? Entity.Null : ChooseNext(from, zone);
            if (target == Entity.Null)
            {
                EntityManager.RemoveComponent<UnderConstruction>(entity);
                Mod.Log.Info($"Level-up: '{fromName}' in zone '{zone.Name}' kept its level (no next level allowed; the game wanted '{gameChoice?.PrefabName}').");
                return;
            }

            if (target != construction.m_NewPrefab)
            {
                construction.m_NewPrefab = target;
                EntityManager.SetComponentData(entity, construction);
            }

            m_IndexSystem.ByEntity.TryGetValue(target, out var to);
            Mod.Log.Info($"Level-up: '{fromName}' -> '{to?.PrefabName}' in zone '{zone.Name}' (game wanted '{gameChoice?.PrefabName}', keep design={zone.KeepDesign}).");
        }

        /// <summary>The prefab the building becomes, or Entity.Null to keep its level.</summary>
        private Entity ChooseNext(BuildingEntry from, PaletteDefinition zone)
        {
            var nextLevel = from.Level + 1;
            m_IndexSystem.FamiliesByKey.TryGetValue(from.FamilyKey, out var ownFamily);
            var sameDesign = ownFamily?.MemberAt(nextLevel);

            if (zone.KeepDesign)
            {
                return sameDesign?.PrefabEntity ?? Entity.Null;
            }

            // Any building of this zone at the next level with the same lot size and
            // goods (like the game, so the companies inside stay valid) - the
            // building's own design included.
            var candidates = new List<Entity>();
            if (sameDesign != null)
            {
                candidates.Add(sameDesign.PrefabEntity);
            }

            foreach (var familyKey in zone.Families
                         .Select(s => { BuildingFamilies.ParseSelection(s, out var key, out _); return key; })
                         .Distinct())
            {
                if (familyKey == from.FamilyKey ||
                    !m_IndexSystem.FamiliesByKey.TryGetValue(familyKey, out var family))
                {
                    continue;
                }

                var member = family.MemberAt(nextLevel);
                if (member != null && IsValidNext(from, member))
                {
                    candidates.Add(member.PrefabEntity);
                }
            }

            return candidates.Count == 0 ? Entity.Null : candidates[m_Random.NextInt(candidates.Count)];
        }

        /// <summary>Next building on a vanilla zone, like the game picks it: any building of the same zone at the next level.</summary>
        private Entity ChooseNextVanilla(BuildingEntry from)
        {
            if (!EntityManager.TryGetComponent<SpawnableBuildingData>(from.PrefabEntity, out var fromSpawnable))
            {
                return Entity.Null;
            }

            var nextLevel = from.Level + 1;
            var candidates = new List<Entity>();
            foreach (var entry in m_IndexSystem.ByEntity.Values)
            {
                if (entry.Level == nextLevel &&
                    EntityManager.TryGetComponent<SpawnableBuildingData>(entry.PrefabEntity, out var spawnable) &&
                    spawnable.m_ZonePrefab == fromSpawnable.m_ZonePrefab &&
                    IsValidNext(from, entry))
                {
                    candidates.Add(entry.PrefabEntity);
                }
            }

            return candidates.Count == 0 ? Entity.Null : candidates[m_Random.NextInt(candidates.Count)];
        }

        /// <summary>
        /// Same lot size, road access and goods as the current building (what the
        /// game matches, so the companies and households inside stay valid).
        /// </summary>
        private bool IsValidNext(BuildingEntry from, BuildingEntry to)
        {
            if (!to.LotSize.Equals(from.LotSize) ||
                !EntityManager.TryGetComponent<BuildingPropertyData>(from.PrefabEntity, out var a) ||
                !EntityManager.TryGetComponent<BuildingPropertyData>(to.PrefabEntity, out var b) ||
                a.m_AllowedSold != b.m_AllowedSold ||
                a.m_AllowedManufactured != b.m_AllowedManufactured ||
                a.m_AllowedInput != b.m_AllowedInput ||
                a.m_AllowedStored != b.m_AllowedStored)
            {
                return false;
            }

            return !EntityManager.TryGetComponent<BuildingData>(from.PrefabEntity, out var fromData) ||
                   !EntityManager.TryGetComponent<BuildingData>(to.PrefabEntity, out var toData) ||
                   (fromData.m_Flags & LevelUpFlagsMask) == (toData.m_Flags & LevelUpFlagsMask);
        }

        /// <summary>True when the building can be levelled up by hand (a zoned building below level 5).</summary>
        public bool CanLevelUp(Entity building)
        {
            if (building == Entity.Null ||
                !EntityManager.HasComponent<Building>(building) ||
                EntityManager.HasComponent<UnderConstruction>(building) ||
                !EntityManager.TryGetComponent<PrefabRef>(building, out var prefabRef) ||
                !EntityManager.TryGetComponent<SpawnableBuildingData>(prefabRef.m_Prefab, out var spawnable))
            {
                return false;
            }

            return spawnable.m_Level < 5 && spawnable.m_ZonePrefab != Entity.Null;
        }

        /// <summary>
        /// "Level up" button: levels the selected building up now. On a custom zone
        /// its design rule decides the next building (a blocked level-up is
        /// overridden: the player asked for it); elsewhere, like the game does.
        /// </summary>
        private void LevelUpSelectedBuilding()
        {
            var selected = World.GetExistingSystemManaged<SelectedInfoUISystem>()?.selectedEntity ?? Entity.Null;
            if (!CanLevelUp(selected))
            {
                return;
            }

            var current = EntityManager.GetComponentData<PrefabRef>(selected).m_Prefab;
            if (!m_IndexSystem.ByEntity.TryGetValue(current, out var from))
            {
                return;
            }

            var onCustomZone = TryGetCustomZone(selected, out var zone);
            var target = onCustomZone ? ChooseNext(from, zone) : ChooseNextVanilla(from);
            if (target == Entity.Null)
            {
                Mod.Log.Info($"Level-up by hand: '{from.PrefabName}' has no next level it can become{(onCustomZone ? $" in zone '{zone.Name}'" : string.Empty)}.");
                return;
            }

            // Progress 255 = finished: the game swaps the building on its next
            // construction tick (what its own level-up does).
            EntityManager.AddComponentData(selected, new UnderConstruction { m_NewPrefab = target, m_Progress = 255 });
            m_Handled[selected] = m_Frame;

            NotifyLevelUp(selected, from, target);

            m_IndexSystem.ByEntity.TryGetValue(target, out var to);
            Mod.Log.Info($"Level-up by hand: '{from.PrefabName}' -> '{to?.PrefabName}'{(onCustomZone ? $" in zone '{zone.Name}'" : string.Empty)}.");
        }

        /// <summary>The game's level-up side effects: the icon over the building and the zone's built-level statistics.</summary>
        private void NotifyLevelUp(Entity building, BuildingEntry from, Entity target)
        {
            try
            {
                if (!m_BuildingConfigurationQuery.IsEmptyIgnoreFilter)
                {
                    var config = m_BuildingConfigurationQuery.GetSingleton<BuildingConfigurationData>();
                    var icons = m_IconCommandSystem.CreateCommandBuffer();
                    icons.Add(building, config.m_LevelUpNotification, (IconPriority)10, (IconClusterLayer)2, (IconFlags)0, Entity.Null, false, false, false, 0f);
                }

                if (EntityManager.TryGetComponent<SpawnableBuildingData>(target, out var spawnable) &&
                    EntityManager.TryGetComponent<BuildingData>(target, out var data))
                {
                    var queue = m_ZoneBuiltRequirementSystem.GetZoneBuiltLevelQueue(out var dependency);
                    dependency.Complete();
                    queue.Enqueue(new ZoneBuiltLevelUpdate
                    {
                        m_Zone = spawnable.m_ZonePrefab,
                        m_FromLevel = from.Level,
                        m_ToLevel = spawnable.m_Level,
                        m_Squares = data.m_LotSize.x * data.m_LotSize.y,
                    });
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Level-up by hand: could not notify the game ({e.Message}).");
            }
        }

        /// <summary>Finds the zone type of the cell under a position.</summary>
        private struct CellIterator : INativeQuadTreeIterator<Entity, Bounds2>
        {
            public float2 m_Position;
            public ComponentLookup<Block> m_Blocks;
            public BufferLookup<Cell> m_Cells;
            public ushort m_ZoneIndex;

            public bool Intersect(Bounds2 bounds) => m_ZoneIndex == 0 && MathUtils.Intersect(bounds, m_Position);

            public void Iterate(Bounds2 bounds, Entity blockEntity)
            {
                if (m_ZoneIndex != 0 || !MathUtils.Intersect(bounds, m_Position) ||
                    !m_Blocks.TryGetComponent(blockEntity, out var block) ||
                    !m_Cells.TryGetBuffer(blockEntity, out var cells))
                {
                    return;
                }

                var index = ZoneUtils.GetCellIndex(block, m_Position);
                if (math.any(index < 0) || math.any(index >= block.m_Size))
                {
                    return;
                }

                var cellIndex = (index.y * block.m_Size.x) + index.x;
                if (cellIndex >= 0 && cellIndex < cells.Length)
                {
                    m_ZoneIndex = cells[cellIndex].m_Zone.m_Index;
                }
            }
        }
    }
}
