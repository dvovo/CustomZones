// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace CustomZones.Systems
{
    using System.Collections.Generic;
    using System.Linq;
    using Colossal.Collections;
    using Colossal.Serialization.Entities;
    using Colossal.Entities;
    using Colossal.Mathematics;
    using Game;
    using Game.Buildings;
    using Game.Common;
    using Game.Companies;
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
    /// A building on a zone that skips levels, saving up for a level more than one
    /// above its own. Saved with the city.
    /// </summary>
    /// <remarks>
    /// The game levels a building up when its condition reaches the levelling cost
    /// of its level (40 x 4^level per home for housing, more for companies), and the
    /// condition grows by an amount x 2^level each upkeep tick. A level k therefore
    /// takes a time proportional to 4^k / 2^k = 2^k. A building of level c that skips
    /// to level t lets the game's level-up come round 1 + Σ 2^(k-c) times (k = c+1 ..
    /// t-1), so the skipped levels take the time they would take in the game. Each
    /// round goes through the game's whole level-up (materials bought and
    /// delivered, condition back to 0); the extra ones are then cancelled at the
    /// swap, as when the game finds no building to level into.
    /// </remarks>
    public struct LevelSkipProgress : IComponentData, IQueryTypeParameter, ISerializable
    {
        public int m_FromLevel;
        public int m_TargetLevel;

        /// <summary>Level-up rounds still to let pass before the building changes.</summary>
        public int m_Remaining;

        public void Serialize<TWriter>(TWriter writer)
            where TWriter : IWriter
        {
            writer.Write(1);
            writer.Write(m_FromLevel);
            writer.Write(m_TargetLevel);
            writer.Write(m_Remaining);
        }

        public void Deserialize<TReader>(TReader reader)
            where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out m_FromLevel);
            reader.Read(out m_TargetLevel);
            reader.Read(out m_Remaining);
        }

        /// <summary>Extra level-up rounds a building of level <paramref name="from"/> lets pass to reach <paramref name="target"/>.</summary>
        public static int ExtraRounds(int from, int target)
        {
            var rounds = 0;
            for (var k = from + 1; k < target; k++)
            {
                rounds += 1 << (k - from);
            }

            return rounds;
        }
    }

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
    /// <item>level up off: removes the UnderConstruction (the building keeps its level);</item>
    /// <item>like the game: a building ticked for this zone at the next level that fits,
    /// its own design first with "prefer same design", or no level-up when there is none;</item>
    /// <item>skip levels: the next ticked level of the building's own design, after as many
    /// level-up rounds as the skipped levels take in the game (see <see cref="LevelSkipProgress"/>).</item>
    /// </list>
    /// The game has already spent the building's condition on the level-up, so a
    /// cancelled one is not retried every tick.
    /// <para>
    /// Since the game asks for construction materials before a level-up (trucks
    /// deliver them, then the swap starts), a building whose custom zone will not
    /// let it level up has that request cancelled as soon as it appears: no
    /// materials are bought for nothing and the "levelling up" icon goes away.
    /// Buildings marked historic never level up, as in the game.
    /// </para>
    /// <para>
    /// Any other building chosen for the next level must fit like the game's own
    /// choice: same lot, road access and goods, at least as many homes, and no
    /// taller than the zone cells allow there (bridges, power lines...).
    /// </para>
    /// <para>
    /// When the game's own level-up finds no building one level up for the lot
    /// (possible when a design skips levels), the zone's choice is applied once the
    /// materials have arrived (<see cref="CheckMissedLevelUps"/>).
    /// </para>
    /// <para>
    /// It also runs the "level up" and "choose level" buttons of the building
    /// panel: instead of asking the game (whose level-up pass only runs about 16
    /// times a day), it picks the building right away with the same rules and
    /// starts the swap, which the game finishes on its next construction tick
    /// (about a second).
    /// </para>
    /// </remarks>
    public partial class LevelUpControlSystem : GameSystemBase
    {
        /// <summary>Set by the "level up" button of the building panel.</summary>
        public static bool LevelUpSelectedRequested;

        /// <summary>Set by the level selector of the building panel (1-5; 0 = none).</summary>
        public static int SetLevelSelectedRequested;

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

        // Buildings lowered by hand, until the game has swapped their prefab: their
        // shops then get the staff limit of the smaller building (see FitCompanies).
        private readonly Dictionary<Entity, (Entity prefab, int frame)> m_Downgraded = new();

        // Custom-zone buildings gathering materials for a level-up the zone allows,
        // with their prefab then. The game's own level-up (LevelupJob) only looks
        // one level up among all the buildings of the zone; when it finds none on
        // this lot (possible when a design skips levels), the mod levels the
        // building up itself once its materials are in (see CheckMissedLevelUps).
        private readonly Dictionary<Entity, Entity> m_AwaitingSwap = new();
        private ZoneBuiltRequirementSystem m_ZoneBuiltRequirementSystem;

        private EntityQuery m_UnderConstructionQuery;
        private EntityQuery m_MaterialsQuery;
        private readonly HashSet<Entity> m_MaterialsChecked = new();
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

            // Buildings waiting for level-up materials (the game adds the buffer then).
            m_MaterialsQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<ResourceNeeding>(),
                    ComponentType.ReadOnly<BuildingCondition>(),
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

            if (SetLevelSelectedRequested > 0)
            {
                var level = SetLevelSelectedRequested;
                SetLevelSelectedRequested = 0;
                SetSelectedBuildingLevel(level);
            }

            if ((m_Frame & 15) == 0)
            {
                CancelBlockedMaterialRequests();
                CheckMissedLevelUps();
                FitCompanies();
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

        /// <summary>The custom zone under a building (false on a game zone or none).</summary>
        public bool TryGetCustomZoneOf(Entity building, out PaletteDefinition zone) => TryGetCustomZone(building, out zone);

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
            m_AwaitingSwap.Remove(entity);
            m_IndexSystem.ByEntity.TryGetValue(current, out var from);
            m_IndexSystem.ByEntity.TryGetValue(construction.m_NewPrefab, out var gameChoice);
            var fromName = from?.PrefabName ?? current.ToString();

            if (!zone.LevelUp)
            {
                CancelGameLevelUp(entity, current, gameChoice);
                Mod.Log.Info($"Level-up: '{fromName}' in zone '{zone.Name}' kept its level (level-up blocked).");
                return;
            }

            var target = from == null ? Entity.Null : ChooseNext(entity, from, zone);
            if (target == Entity.Null)
            {
                CancelGameLevelUp(entity, current, gameChoice);
                Mod.Log.Info($"Level-up: '{fromName}' in zone '{zone.Name}' kept its level (no next level allowed; the game wanted '{gameChoice?.PrefabName}').");
                return;
            }

            // A skip that has not yet taken the time of the levels it jumps over (the
            // materials stage normally holds it back): this round is spent instead.
            if (SavingUpForSkip(entity, from, target, zone))
            {
                CancelGameLevelUp(entity, current, gameChoice);
                CancelLevelUpProgress(entity);
                return;
            }

            // The game counted the building one level up (zone statistics used for
            // unlocks); a skip ends higher than that.
            m_IndexSystem.ByEntity.TryGetValue(target, out var chosen);
            if (gameChoice != null && chosen != null && chosen.Level != gameChoice.Level)
            {
                QueueBuiltLevel(current, gameChoice.Level, chosen.Level);
            }

            if (target != construction.m_NewPrefab)
            {
                construction.m_NewPrefab = target;
                EntityManager.SetComponentData(entity, construction);
            }

            if (EntityManager.HasComponent<LevelSkipProgress>(entity))
            {
                EntityManager.RemoveComponent<LevelSkipProgress>(entity);
            }

            m_IndexSystem.ByEntity.TryGetValue(target, out var to);
            Mod.Log.Info($"Level-up: '{fromName}' -> '{to?.PrefabName}' in zone '{zone.Name}' (game wanted '{gameChoice?.PrefabName}', growth={zone.Growth}, keep design={zone.KeepDesign}).");
        }

        /// <summary>
        /// Buildings whose materials arrived but that the game did not level up
        /// (its LevelupJob found no building one level up for this lot, so the
        /// building only went back to condition 0): the zone's own choice is
        /// applied now, with the game's side effects, as Apply would have.
        /// </summary>
        private void CheckMissedLevelUps()
        {
            if (m_AwaitingSwap.Count == 0)
            {
                return;
            }

            foreach (var (building, prefab) in m_AwaitingSwap.ToList())
            {
                if (!EntityManager.Exists(building) || EntityManager.HasComponent<Deleted>(building))
                {
                    m_AwaitingSwap.Remove(building);
                    continue;
                }

                if (EntityManager.HasBuffer<ResourceNeeding>(building))
                {
                    continue; // still gathering materials
                }

                m_AwaitingSwap.Remove(building);
                if (!EntityManager.TryGetComponent<PrefabRef>(building, out var prefabRef) ||
                    prefabRef.m_Prefab != prefab ||
                    EntityManager.HasComponent<UnderConstruction>(building) ||
                    EntityManager.HasComponent<Abandoned>(building) ||
                    EntityManager.HasComponent<Destroyed>(building) ||
                    m_Handled.ContainsKey(building) ||
                    !m_IndexSystem.ByEntity.TryGetValue(prefab, out var from) ||
                    !TryGetCustomZone(building, out var zone) ||
                    !zone.LevelUp)
                {
                    continue; // levelled up, changed or gone meanwhile
                }

                var target = ChooseNext(building, from, zone);
                if (target == Entity.Null || SavingUpForSkip(building, from, target, zone))
                {
                    continue; // one round of a skip spent (the game already reset the condition)
                }

                EntityManager.AddComponentData(building, new UnderConstruction { m_NewPrefab = target, m_Progress = 255 });
                m_Handled[building] = m_Frame;
                NotifyLevelUp(building, from, target);
                if (EntityManager.HasComponent<LevelSkipProgress>(building))
                {
                    EntityManager.RemoveComponent<LevelSkipProgress>(building);
                }

                m_IndexSystem.ByEntity.TryGetValue(target, out var to);
                Mod.Log.Info($"Level-up: '{from.PrefabName}' -> '{to?.PrefabName}' in zone '{zone.Name}' (the game had no building one level up for this lot).");
            }
        }

        /// <summary>
        /// After a building was lowered by hand: its companies get the staff limit
        /// of the smaller building, the way the game sets it when a company moves
        /// into a building (at most what fits, at least two thirds of it). Industry
        /// and offices do this themselves every day; shops only shrink when their
        /// customers are served, so they are done here. Extra workers are then laid
        /// off by the game, as when a building levels up and its jobs change.
        /// </summary>
        private void FitCompanies()
        {
            if (m_Downgraded.Count == 0)
            {
                return;
            }

            foreach (var (building, (prefab, frame)) in m_Downgraded.ToList())
            {
                if (!EntityManager.Exists(building) || m_Frame - frame > HandledLifetimeFrames)
                {
                    m_Downgraded.Remove(building);
                    continue;
                }

                if (!EntityManager.TryGetComponent<PrefabRef>(building, out var prefabRef) || prefabRef.m_Prefab != prefab)
                {
                    continue; // not swapped yet
                }

                m_Downgraded.Remove(building);
                if (!EntityManager.TryGetBuffer<Renter>(building, true, out var renters) ||
                    !EntityManager.TryGetComponent<BuildingData>(prefab, out var buildingData) ||
                    !EntityManager.TryGetComponent<BuildingPropertyData>(prefab, out var properties) ||
                    !EntityManager.TryGetComponent<SpawnableBuildingData>(prefab, out var spawnable))
                {
                    continue;
                }

                var companies = new List<Entity>();
                for (var i = 0; i < renters.Length; i++)
                {
                    companies.Add(renters[i].m_Renter);
                }

                foreach (var company in companies)
                {
                    if (!EntityManager.TryGetComponent<WorkProvider>(company, out var provider) ||
                        !EntityManager.TryGetComponent<PrefabRef>(company, out var companyPrefab) ||
                        !EntityManager.TryGetComponent<ServiceCompanyData>(companyPrefab.m_Prefab, out var service))
                    {
                        continue;
                    }

                    var fitting = CompanyUtils.GetCommercialMaxFittingWorkers(buildingData, properties, spawnable.m_Level, service);
                    var limit = math.max(math.min(provider.m_MaxWorkers, fitting), 2 * fitting / 3);
                    if (limit != provider.m_MaxWorkers)
                    {
                        Mod.Log.Info($"Set level by hand: shop staff limit {provider.m_MaxWorkers} -> {limit} for the smaller building.");
                        provider.m_MaxWorkers = limit;
                        EntityManager.SetComponentData(company, provider);
                    }
                }
            }
        }

        /// <summary>
        /// Undoes a level-up the game had already started: the building keeps its
        /// prefab, and the game's side effects are taken back (the zone statistics
        /// count it at its own level again; the level-up icon goes).
        /// </summary>
        private void CancelGameLevelUp(Entity building, Entity current, BuildingEntry gameChoice)
        {
            EntityManager.RemoveComponent<UnderConstruction>(building);
            if (gameChoice == null || !EntityManager.TryGetComponent<SpawnableBuildingData>(current, out var spawnable))
            {
                return;
            }

            QueueBuiltLevel(current, gameChoice.Level, spawnable.m_Level);
            if (!m_BuildingConfigurationQuery.IsEmptyIgnoreFilter)
            {
                var config = m_BuildingConfigurationQuery.GetSingleton<BuildingConfigurationData>();
                m_IconCommandSystem.CreateCommandBuffer().Remove(building, config.m_LevelUpNotification);
            }
        }

        /// <summary>
        /// Moves one building between levels in the game's zone statistics (what
        /// signature buildings and other unlocks count), like its own level-up does.
        /// </summary>
        private void QueueBuiltLevel(Entity prefab, int fromLevel, int toLevel)
        {
            if (fromLevel == toLevel ||
                !EntityManager.TryGetComponent<SpawnableBuildingData>(prefab, out var spawnable) ||
                !EntityManager.TryGetComponent<BuildingData>(prefab, out var data))
            {
                return;
            }

            try
            {
                var queue = m_ZoneBuiltRequirementSystem.GetZoneBuiltLevelQueue(out var dependency);
                dependency.Complete();
                queue.Enqueue(new ZoneBuiltLevelUpdate
                {
                    m_Zone = spawnable.m_ZonePrefab,
                    m_FromLevel = fromLevel,
                    m_ToLevel = toLevel,
                    m_Squares = data.m_LotSize.x * data.m_LotSize.y,
                });
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Level-up: could not update the zone statistics ({e.Message}).");
            }
        }

        /// <summary>
        /// The prefab the building becomes at the next level, or Entity.Null to keep
        /// its level. Like the game, one level at a time and only a building that
        /// fits (lot, road access, goods, homes, height); unlike the game, only the
        /// buildings ticked for this zone at that level. With "prefer same design" its own
        /// design wins when it is ticked and fits; otherwise any of them, at random.
        /// </summary>
        private Entity ChooseNext(Entity building, BuildingEntry from, PaletteDefinition zone)
        {
            var nextLevel = from.Level + 1;
            if (nextLevel > 5)
            {
                return Entity.Null;
            }

            var maxHeight = GetMaxHeight(building, from.PrefabEntity);
            if (zone.Growth == GrowthMode.SkipLevels)
            {
                return NextOwnLevel(from, zone, maxHeight)?.PrefabEntity ?? Entity.Null;
            }

            var own = Entity.Null;
            var candidates = new List<Entity>();
            foreach (var selection in zone.Families)
            {
                BuildingFamilies.ParseSelection(selection, out var familyKey, out var level);
                if (level != nextLevel ||
                    !m_IndexSystem.FamiliesByKey.TryGetValue(familyKey, out var family) ||
                    family.MemberAt(nextLevel) is not { } member ||
                    !IsValidNext(from, member, maxHeight))
                {
                    continue;
                }

                if (familyKey == from.FamilyKey)
                {
                    own = member.PrefabEntity;
                }

                candidates.Add(member.PrefabEntity);
            }

            if (zone.KeepDesign && own != Entity.Null)
            {
                return own;
            }

            return candidates.Count == 0 ? Entity.Null : candidates[m_Random.NextInt(candidates.Count)];
        }

        /// <summary>
        /// Zones that skip levels: the next ticked level of the building's own design
        /// that fits (the lowest one above its level), or null.
        /// </summary>
        private BuildingEntry NextOwnLevel(BuildingEntry from, PaletteDefinition zone, float maxHeight)
        {
            if (!m_IndexSystem.FamiliesByKey.TryGetValue(from.FamilyKey, out var family))
            {
                return null;
            }

            for (var level = from.Level + 1; level <= 5; level++)
            {
                if (BuildingFamilies.IsSelected(zone, from.FamilyKey, level) &&
                    family.MemberAt(level) is { } member &&
                    IsValidNext(from, member, maxHeight))
                {
                    return member;
                }
            }

            return null;
        }

        /// <summary>
        /// The highest level buildings born from <paramref name="start"/> can reach
        /// in a zone (following the ticked buildings level by level; height ignored).
        /// </summary>
        public int ReachableLevel(BuildingEntry start, PaletteDefinition zone)
        {
            if (zone.Growth == GrowthMode.SkipLevels)
            {
                var building = start;
                while (zone.LevelUp && NextOwnLevel(building, zone, float.PositiveInfinity) is { } next)
                {
                    building = next;
                }

                return building.Level;
            }

            var current = new List<BuildingEntry> { start };
            var level = start.Level;
            while (level < 5 && zone.LevelUp)
            {
                var next = new List<BuildingEntry>();
                foreach (var selection in zone.Families)
                {
                    BuildingFamilies.ParseSelection(selection, out var familyKey, out var selectedLevel);
                    if (selectedLevel == level + 1 &&
                        m_IndexSystem.FamiliesByKey.TryGetValue(familyKey, out var family) &&
                        family.MemberAt(level + 1) is { } member &&
                        current.Any(from => IsValidNext(from, member, float.PositiveInfinity)))
                    {
                        next.Add(member);
                    }
                }

                if (next.Count == 0)
                {
                    break;
                }

                current = next;
                level++;
            }

            return level;
        }

        /// <summary>Next building on a vanilla zone, like the game picks it: any building of the same zone at the next level.</summary>
        private Entity ChooseNextVanilla(Entity building, BuildingEntry from)
        {
            var maxHeight = GetMaxHeight(building, from.PrefabEntity);
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
                    IsValidNext(from, entry, maxHeight))
                {
                    candidates.Add(entry.PrefabEntity);
                }
            }

            return candidates.Count == 0 ? Entity.Null : candidates[m_Random.NextInt(candidates.Count)];
        }

        /// <summary>
        /// What the game matches when it levels a building up, so the companies and
        /// households inside stay valid: same lot size, road access and goods, at
        /// least as many homes, and no taller than the zone allows on that lot.
        /// </summary>
        /// <remarks>
        /// Going down a level (level selector) the new building may have fewer
        /// homes than the old one, as long as the households living there now all
        /// fit (<paramref name="householdsInside"/>); going up it never has fewer.
        /// </remarks>
        private bool IsValidNext(BuildingEntry from, BuildingEntry to, float maxHeight, int householdsInside = -1)
        {
            if (!EntityManager.TryGetComponent<BuildingPropertyData>(from.PrefabEntity, out var a) ||
                !EntityManager.TryGetComponent<BuildingPropertyData>(to.PrefabEntity, out var b))
            {
                return false;
            }

            // The game only levels a building into one of its own zone (its spawn
            // group); a custom zone may mix designs of several zones, but each
            // building stays in its own, so its demand and statistics stay right.
            if (!EntityManager.TryGetComponent<SpawnableBuildingData>(from.PrefabEntity, out var fromSpawnable) ||
                !EntityManager.TryGetComponent<SpawnableBuildingData>(to.PrefabEntity, out var toSpawnable) ||
                fromSpawnable.m_ZonePrefab != toSpawnable.m_ZonePrefab)
            {
                return false;
            }

            var homesNeeded = to.Level > from.Level || householdsInside < 0 ? a.m_ResidentialProperties : householdsInside;
            if (!to.LotSize.Equals(from.LotSize) ||
                b.m_ResidentialProperties < homesNeeded ||
                (EntityManager.TryGetComponent<ObjectGeometryData>(to.PrefabEntity, out var geometry) && geometry.m_Size.y > maxHeight) ||
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

            return spawnable.m_Level < 5 && spawnable.m_ZonePrefab != Entity.Null && !IsHistoric(building);
        }

        /// <summary>Households living in a building now.</summary>
        private int HouseholdsIn(Entity building)
        {
            if (!EntityManager.TryGetBuffer<Renter>(building, true, out var renters))
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < renters.Length; i++)
            {
                if (EntityManager.HasComponent<Game.Citizens.Household>(renters[i].m_Renter))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>True for a building the player marked historic (the game never levels it up or down).</summary>
        public bool IsHistoric(Entity building) =>
            EntityManager.TryGetComponent<Building>(building, out var data) &&
            (data.m_Flags & Game.Buildings.BuildingFlags.Historical) != 0;

        /// <summary>
        /// Highest building that fits on this building's lot, from the zone cells
        /// under it (bridges, power lines and other objects above lower them), as
        /// the game measures it for a level-up. Infinity when unknown.
        /// </summary>
        private float GetMaxHeight(Entity building, Entity prefab)
        {
            if (!EntityManager.TryGetComponent<Transform>(building, out var transform) ||
                !EntityManager.TryGetComponent<BuildingData>(prefab, out var buildingData))
            {
                return float.PositiveInfinity;
            }

            var right = math.rotate(transform.m_Rotation, new float3(8f, 0f, 0f)).xz;
            var forward = math.rotate(transform.m_Rotation, new float3(0f, 0f, 8f)).xz;
            var halfX = right * ((buildingData.m_LotSize.x * 0.5f) - 0.5f);
            var halfY = forward * ((buildingData.m_LotSize.y * 0.5f) - 0.5f);
            var extent = math.abs(halfY) + math.abs(halfX);

            var tree = m_ZoneSearchSystem.GetSearchTree(true, out var treeDependency);
            treeDependency.Complete();
            var iterator = new HeightIterator
            {
                m_Bounds = new Bounds2(transform.m_Position.xz - extent, transform.m_Position.xz + extent),
                m_LotSize = buildingData.m_LotSize,
                m_StartPosition = transform.m_Position.xz + halfY + halfX,
                m_Right = right,
                m_Forward = forward,
                m_MaxHeight = int.MaxValue,
                m_Blocks = GetComponentLookup<Block>(true),
                m_ValidAreas = GetComponentLookup<ValidArea>(true),
                m_Cells = GetBufferLookup<Cell>(true),
            };
            tree.Iterate(ref iterator);
            return iterator.m_MaxHeight == int.MaxValue ? float.PositiveInfinity : iterator.m_MaxHeight - transform.m_Position.y;
        }

        /// <summary>
        /// Buildings that have just asked for level-up materials while standing on
        /// a custom zone that will not let them level up (level-up blocked, or no
        /// next building allowed): the request is cancelled before any truck is
        /// sent, like the game does for historic buildings, and the building starts
        /// saving up again.
        /// </summary>
        private void CancelBlockedMaterialRequests()
        {
            if (m_MaterialsQuery.IsEmpty)
            {
                m_MaterialsChecked.Clear();
                return;
            }

            using var entities = m_MaterialsQuery.ToEntityArray(Allocator.Temp);
            if (m_MaterialsChecked.Count > 0)
            {
                var present = new HashSet<Entity>(entities.ToArray());
                m_MaterialsChecked.RemoveWhere(e => !present.Contains(e));
            }

            foreach (var entity in entities)
            {
                if (!m_MaterialsChecked.Add(entity) || m_Handled.ContainsKey(entity))
                {
                    continue;
                }

                var prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                if (!EntityManager.TryGetComponent<SpawnableBuildingData>(prefab, out var spawnable) ||
                    spawnable.m_ZonePrefab == Entity.Null ||
                    !m_IndexSystem.ByEntity.TryGetValue(prefab, out var from) ||
                    !TryGetCustomZone(entity, out var zone))
                {
                    continue;
                }

                // It can level up (now, or as a round of a skip): the game goes on
                // exactly as usual, materials included. The rounds of a skip are
                // counted when the game swaps the building (Apply), after the
                // materials, so each round costs what a game level-up costs.
                if (zone.LevelUp && ChooseNext(entity, from, zone) != Entity.Null)
                {
                    m_AwaitingSwap[entity] = prefab;
                    continue;
                }

                CancelLevelUpProgress(entity);
                m_MaterialsChecked.Remove(entity);
                m_AwaitingSwap.Remove(entity);
                Mod.Log.Info($"Level-up: '{from.PrefabName}' in zone '{zone.Name}' will not level up ({(zone.LevelUp ? "no next level allowed" : "level-up blocked")}); its request for materials was cancelled.");
            }
        }

        /// <summary>
        /// A building about to skip levels: true while it still has to let level-up
        /// rounds pass (this one is then cancelled), false when it changes now.
        /// </summary>
        private bool SavingUpForSkip(Entity building, BuildingEntry from, Entity target, PaletteDefinition zone)
        {
            var targetLevel = m_IndexSystem.ByEntity.TryGetValue(target, out var to) ? to.Level : from.Level + 1;
            if (targetLevel <= from.Level + 1)
            {
                if (EntityManager.HasComponent<LevelSkipProgress>(building))
                {
                    EntityManager.RemoveComponent<LevelSkipProgress>(building);
                }

                return false;
            }

            var progress = EntityManager.HasComponent<LevelSkipProgress>(building)
                ? EntityManager.GetComponentData<LevelSkipProgress>(building)
                : default;
            if (progress.m_FromLevel != from.Level || progress.m_TargetLevel != targetLevel)
            {
                progress = new LevelSkipProgress
                {
                    m_FromLevel = from.Level,
                    m_TargetLevel = targetLevel,
                    m_Remaining = LevelSkipProgress.ExtraRounds(from.Level, targetLevel),
                };
            }

            if (progress.m_Remaining <= 0)
            {
                return false; // saved up: it changes with this round
            }

            progress.m_Remaining--;
            if (EntityManager.HasComponent<LevelSkipProgress>(building))
            {
                EntityManager.SetComponentData(building, progress);
            }
            else
            {
                EntityManager.AddComponentData(building, progress);
            }

            Mod.Log.Info($"Level-up: '{from.PrefabName}' in zone '{zone.Name}' skips to level {targetLevel}; {progress.m_Remaining + 1} more round(s) before it changes.");
            return true;
        }

        /// <summary>
        /// Clears a building's level-up progress: condition back to 0 (what the
        /// game does once it levels up) and any pending request for materials with
        /// its "levelling up" icon (the game drops deliveries whose request is gone).
        /// </summary>
        private void CancelLevelUpProgress(Entity building)
        {
            if (EntityManager.HasComponent<BuildingCondition>(building))
            {
                EntityManager.SetComponentData(building, new BuildingCondition { m_Condition = 0 });
            }

            if (!EntityManager.HasBuffer<ResourceNeeding>(building))
            {
                return;
            }

            EntityManager.RemoveComponent<ResourceNeeding>(building);
            if (!m_BuildingConfigurationQuery.IsEmptyIgnoreFilter)
            {
                var config = m_BuildingConfigurationQuery.GetSingleton<BuildingConfigurationData>();
                m_IconCommandSystem.CreateCommandBuffer().Remove(building, config.m_LevelingBuildingNotificationPrefab);
            }
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
            var target = onCustomZone ? ChooseNext(selected, from, zone) : ChooseNextVanilla(selected, from);
            if (target == Entity.Null)
            {
                Mod.Log.Info($"Level-up by hand: '{from.PrefabName}' has no next level it can become{(onCustomZone ? $" in zone '{zone.Name}'" : string.Empty)}.");
                return;
            }

            // Progress 255 = finished: the game swaps the building on its next
            // construction tick (what its own level-up does). Any progress or
            // materials it was gathering for this level-up are used up.
            CancelLevelUpProgress(selected);
            m_AwaitingSwap.Remove(selected);
            EntityManager.AddComponentData(selected, new UnderConstruction { m_NewPrefab = target, m_Progress = 255 });
            m_Handled[selected] = m_Frame;

            NotifyLevelUp(selected, from, target);

            m_IndexSystem.ByEntity.TryGetValue(target, out var to);
            Mod.Log.Info($"Level-up by hand: '{from.PrefabName}' -> '{to?.PrefabName}'{(onCustomZone ? $" in zone '{zone.Name}'" : string.Empty)}.");
        }

        /// <summary>
        /// Buildings the given building can become at a level: its own design first
        /// (when it has that level), then the other buildings of its zone at that
        /// level with the same lot, road access and goods (custom zone: its chosen
        /// buildings; game zone: every building of that zone).
        /// </summary>
        private List<Entity> CandidatesAt(Entity building, BuildingEntry from, PaletteDefinition zone, int level, float maxHeight, out Entity sameDesign)
        {
            var households = HouseholdsIn(building);
            var candidates = new List<Entity>();
            sameDesign = Entity.Null;
            if (m_IndexSystem.FamiliesByKey.TryGetValue(from.FamilyKey, out var ownFamily) &&
                ownFamily.MemberAt(level) is { } own &&
                IsValidNext(from, own, maxHeight, households))
            {
                sameDesign = own.PrefabEntity;
                candidates.Add(own.PrefabEntity);
            }

            if (zone != null)
            {
                foreach (var familyKey in zone.Families
                             .Select(s => { BuildingFamilies.ParseSelection(s, out var key, out _); return key; })
                             .Distinct())
                {
                    if (familyKey != from.FamilyKey &&
                        m_IndexSystem.FamiliesByKey.TryGetValue(familyKey, out var family) &&
                        family.MemberAt(level) is { } member &&
                        IsValidNext(from, member, maxHeight, households))
                    {
                        candidates.Add(member.PrefabEntity);
                    }
                }
            }
            else if (EntityManager.TryGetComponent<SpawnableBuildingData>(from.PrefabEntity, out var fromSpawnable))
            {
                foreach (var entry in m_IndexSystem.ByEntity.Values)
                {
                    if (entry.Level == level &&
                        entry.FamilyKey != from.FamilyKey &&
                        EntityManager.TryGetComponent<SpawnableBuildingData>(entry.PrefabEntity, out var spawnable) &&
                        spawnable.m_ZonePrefab == fromSpawnable.m_ZonePrefab &&
                        IsValidNext(from, entry, maxHeight, households))
                    {
                        candidates.Add(entry.PrefabEntity);
                    }
                }
            }

            return candidates;
        }

        /// <summary>
        /// Level selector of a building: [level - 1] = 2 for its current level, 1 when
        /// it can be set to that level, 0 when no building fits. Empty when the
        /// building is not a zoned building.
        /// </summary>
        public int[] LevelStates(Entity building)
        {
            if (building == Entity.Null ||
                !EntityManager.HasComponent<Building>(building) ||
                !EntityManager.TryGetComponent<PrefabRef>(building, out var prefabRef) ||
                !EntityManager.TryGetComponent<SpawnableBuildingData>(prefabRef.m_Prefab, out var spawnable) ||
                spawnable.m_ZonePrefab == Entity.Null ||
                IsHistoric(building) ||
                !m_IndexSystem.ByEntity.TryGetValue(prefabRef.m_Prefab, out var from))
            {
                return System.Array.Empty<int>();
            }

            var zone = TryGetCustomZone(building, out var custom) ? custom : null;
            var maxHeight = GetMaxHeight(building, prefabRef.m_Prefab);
            var states = new int[5];
            for (var level = 1; level <= 5; level++)
            {
                states[level - 1] = level == from.Level ? 2
                    : CandidatesAt(building, from, zone, level, maxHeight, out _).Count > 0 ? 1
                    : 0;
            }

            return states;
        }

        /// <summary>
        /// Level selector: turns the selected building into one of the given level
        /// now, up or down. Its own design when it has that level, otherwise
        /// another building of its zone at that level (a custom zone's growth
        /// settings are overridden: the player picked the level).
        /// </summary>
        private void SetSelectedBuildingLevel(int level)
        {
            var selected = World.GetExistingSystemManaged<SelectedInfoUISystem>()?.selectedEntity ?? Entity.Null;
            if (selected == Entity.Null ||
                EntityManager.HasComponent<UnderConstruction>(selected) ||
                IsHistoric(selected) ||
                !EntityManager.TryGetComponent<PrefabRef>(selected, out var prefabRef) ||
                !m_IndexSystem.ByEntity.TryGetValue(prefabRef.m_Prefab, out var from) ||
                from.Level == level)
            {
                return;
            }

            var onCustomZone = TryGetCustomZone(selected, out var zone);
            var candidates = CandidatesAt(selected, from, onCustomZone ? zone : null, level, GetMaxHeight(selected, prefabRef.m_Prefab), out var sameDesign);
            if (candidates.Count == 0)
            {
                Mod.Log.Info($"Set level by hand: '{from.PrefabName}' has no building it can become at level {level}.");
                return;
            }

            var target = sameDesign != Entity.Null ? sameDesign : candidates[m_Random.NextInt(candidates.Count)];
            CancelLevelUpProgress(selected);
            m_AwaitingSwap.Remove(selected);
            EntityManager.AddComponentData(selected, new UnderConstruction { m_NewPrefab = target, m_Progress = 255 });
            m_Handled[selected] = m_Frame;

            if (level > from.Level)
            {
                NotifyLevelUp(selected, from, target);
            }
            else
            {
                QueueBuiltLevel(from.PrefabEntity, from.Level, level);
                m_Downgraded[selected] = (target, m_Frame);
            }

            m_IndexSystem.ByEntity.TryGetValue(target, out var to);
            Mod.Log.Info($"Set level by hand: '{from.PrefabName}' (L{from.Level}) -> '{to?.PrefabName}' (L{level}){(onCustomZone ? $" in zone '{zone.Name}'" : string.Empty)}.");
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

                if (EntityManager.TryGetComponent<SpawnableBuildingData>(target, out var spawnable))
                {
                    QueueBuiltLevel(from.PrefabEntity, from.Level, spawnable.m_Level);
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Level-up by hand: could not notify the game ({e.Message}).");
            }
        }

        /// <summary>Lowest cell height limit under a lot (as the game's level-up measures it).</summary>
        private struct HeightIterator : INativeQuadTreeIterator<Entity, Bounds2>
        {
            public Bounds2 m_Bounds;
            public int2 m_LotSize;
            public float2 m_StartPosition;
            public float2 m_Right;
            public float2 m_Forward;
            public int m_MaxHeight;
            public ComponentLookup<Block> m_Blocks;
            public ComponentLookup<ValidArea> m_ValidAreas;
            public BufferLookup<Cell> m_Cells;

            public bool Intersect(Bounds2 bounds) => MathUtils.Intersect(bounds, m_Bounds);

            public void Iterate(Bounds2 bounds, Entity blockEntity)
            {
                if (!MathUtils.Intersect(bounds, m_Bounds) ||
                    !m_ValidAreas.TryGetComponent(blockEntity, out var validArea) ||
                    validArea.m_Area.y <= validArea.m_Area.x ||
                    !m_Blocks.TryGetComponent(blockEntity, out var block) ||
                    !m_Cells.TryGetBuffer(blockEntity, out var cells))
                {
                    return;
                }

                var rowStart = m_StartPosition;
                for (var y = 0; y < m_LotSize.y; y++)
                {
                    var position = rowStart;
                    for (var x = 0; x < m_LotSize.x; x++)
                    {
                        var cellIndex = ZoneUtils.GetCellIndex(block, position);
                        if (math.all((cellIndex >= validArea.m_Area.xz) & (cellIndex < validArea.m_Area.yw)))
                        {
                            var cell = cells[(cellIndex.y * block.m_Size.x) + cellIndex.x];
                            if ((cell.m_State & CellFlags.Visible) != 0)
                            {
                                m_MaxHeight = math.min(m_MaxHeight, (int)cell.m_Height);
                            }
                        }

                        position -= m_Right;
                    }

                    rowStart -= m_Forward;
                }
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
