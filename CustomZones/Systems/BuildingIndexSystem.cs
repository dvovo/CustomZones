// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// The index of growable buildings. The way buildings are found and classified
// follows FindIt-CSII (ZonedBuildingPrefabCategoryProcessor and
// PrefabIndexingSystem, MIT License):
//   - density comes from the zone's ZonePropertiesData, not from its name, so
//     region packs whose zones are named differently are classified correctly;
//   - the index is rebuilt whenever the number of growable buildings grows, so
//     packs that finish loading after the first pass are picked up.

namespace CustomZones.Systems
{
    using Game;
    using Game.Prefabs;
    using Game.SceneFlow;
    using Game.UI;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Unity.Collections;
    using Unity.Entities;
    using CustomZones.Domain;
    using CustomZones.Integration;
    using CustomZones.Utilities;

    /// <summary>
    /// Builds an index of every spawnable (growable) building prefab.
    /// For each one it resolves: zone category (R/Mixed/C/I/O), density,
    /// level, lot size, theme, asset packs and a thumbnail URL.
    /// The index is consumed by PaletteUISystem (browser + filters) and
    /// PaletteZoneSystem (name -> entity for the allowed-building buffer).
    /// </summary>
    public partial class BuildingIndexSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private ImageSystem m_ImageSystem;
        private IconGeneratorSystem m_IconGenerator;
        private EntityQuery m_SpawnableQuery;
        private EntityQuery m_ZoneQuery;
        private int m_LastIndexedCount = -1;

        // Asset Icon Library rewrites building icons some time after load: one late
        // re-index (~1 min @60fps) picks those up. Building Icon Generator pictures
        // are resolved live by the editor, so they need no re-index.
        private static readonly int[] RefreshFrames = { 3600 };
        private int m_FramesSinceIndex;
        private int m_RefreshesDone;

        /// <summary>All indexed buildings, sorted by category then name.</summary>
        public List<BuildingEntry> Buildings { get; } = new();

        /// <summary>Fast lookup: prefab entity -> entry.</summary>
        public Dictionary<Entity, BuildingEntry> ByEntity { get; } = new();

        /// <summary>Fast lookup: prefab name -> entry.</summary>
        public Dictionary<string, BuildingEntry> ByName { get; } = new();

        /// <summary>Buildings grouped by family (every level and lot size of one style).</summary>
        public List<BuildingFamily> Families { get; private set; } = new();

        /// <summary>Fast lookup: family key -> family.</summary>
        public Dictionary<string, BuildingFamily> FamiliesByKey { get; } = new();

        /// <summary>Style -> its families (one per lot size). Used to upgrade old style-only keys.</summary>
        public Dictionary<string, List<string>> FamilyKeysByStyle { get; } = new();

        /// <summary>Distinct asset-pack names found, for the "Pack" filter.</summary>
        public List<string> Packs { get; } = new();

        /// <summary>Icon URL per entry in <see cref="Packs"/> (same order).</summary>
        public List<string> PackIcons { get; } = new();

        /// <summary>pack name -> icon URL (from the pack prefab's UIObject).</summary>
        private readonly Dictionary<string, string> m_PackIconByName = new();

        /// <summary>Incremented every time the index is rebuilt.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// Representative icon per ZoneCategory (index = enum value),
        /// resolved at runtime from real zone prefabs so they never 404
        /// (vanilla icon file names vary across game versions).
        /// </summary>
        public List<string> CategoryIcons { get; } = new();

        /// <summary>zone prefab entity -> density (FindIt's ZonePropertiesData rule).</summary>
        private readonly Dictionary<Entity, DensityType> m_ZoneDensity = new();

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_ImageSystem = World.GetOrCreateSystemManaged<ImageSystem>();
            m_IconGenerator = World.GetOrCreateSystemManaged<IconGeneratorSystem>();

            // Same query shape FindIt uses for zoned buildings.
            m_SpawnableQuery = SystemAPI.QueryBuilder()
                .WithAll<PrefabData, SpawnableBuildingData, BuildingData>()
                .Build();

            m_ZoneQuery = SystemAPI.QueryBuilder()
                .WithAll<PrefabData, ZoneData, ZonePropertiesData>()
                .Build();

            RequireForUpdate(m_SpawnableQuery);
        }

        protected override void OnUpdate()
        {
            // Re-index when the set of spawnable buildings grows (region packs
            // and other mods can register their prefabs after the first pass).
            var count = m_SpawnableQuery.CalculateEntityCount();
            if (m_LastIndexedCount != count)
            {
                Reindex();
                m_LastIndexedCount = count;
                m_FramesSinceIndex = 0;
                m_RefreshesDone = 0;
                return;
            }

            // New generated pictures do not rebuild the index (9000+ buildings):
            // IconGeneratorSystem updates the entry in place (ApplyGeneratedIcon)
            // and bumps the version now and then.

            // A few scheduled rebuilds pick up icons Asset Icon Library sets late.
            if (m_RefreshesDone < RefreshFrames.Length)
            {
                m_FramesSinceIndex++;
                if (m_FramesSinceIndex >= RefreshFrames[m_RefreshesDone])
                {
                    m_RefreshesDone++;
                    Reindex();
                }
            }
        }

        /// <summary>
        /// Patches a single indexed building with its freshly generated icon.
        /// O(1) — deliberately NOT a re-index (see OnUpdate).
        /// </summary>
        public void ApplyGeneratedIcon(string prefabName, string url)
        {
            if (ByName.TryGetValue(prefabName, out var entry))
            {
                entry.Thumbnail = url;
            }
        }

        /// <summary>
        /// Bumps the version so the UI picks up icons patched in place. Called
        /// sparingly by IconGeneratorSystem (batched), never per icon.
        /// </summary>
        public void NotifyIconsChanged() => Version++;

        /// <summary>
        /// Forces a full rebuild on the next update, so thumbnails are resolved
        /// again (used after the loading-screen icon preload).
        /// </summary>
        public void ForceReindex() => m_LastIndexedCount = -1;

        private void Reindex()
        {
            try
            {
                BuildIndex();
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "Failed to build building index");
            }
        }

        private void BuildIndex()
        {
            Buildings.Clear();
            ByEntity.Clear();
            ByName.Clear();
            Packs.Clear();
            PackIcons.Clear();
            m_PackIconByName.Clear();

            CategoryIcons.Clear();
            for (var i = 0; i < 6; i++)
            {
                CategoryIcons.Add(string.Empty);
            }

            // "Any" filter icon (a game glyph).
            CategoryIcons[0] = "Media/Glyphs/StarFilled.svg";

            BuildZoneDensityCache();

            var entities = m_SpawnableQuery.ToEntityArray(Allocator.Temp);

            foreach (var entity in entities)
            {
                if (!TryIndex(entity, out var entry))
                {
                    continue;
                }

                Buildings.Add(entry);
                ByEntity[entity] = entry;
                ByName[entry.PrefabName] = entry;
            }

            entities.Dispose();

            Buildings.Sort((a, b) =>
            {
                var c = a.Category.CompareTo(b.Category);
                if (c != 0) return c;
                c = a.Density.CompareTo(b.Density);
                if (c != 0) return c;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            Families = BuildingFamilies.Build(Buildings);
            FamiliesByKey.Clear();
            FamilyKeysByStyle.Clear();
            foreach (var family in Families)
            {
                FamiliesByKey[family.Key] = family;

                var style = BuildingFamilies.StyleOfKey(family.Key);
                if (!FamilyKeysByStyle.TryGetValue(style, out var keys))
                {
                    keys = new List<string>();
                    FamilyKeysByStyle[style] = keys;
                }

                keys.Add(family.Key);
            }

            // Pack list (name + icon), sorted, for the "Pack" filter row.
            foreach (var kv in m_PackIconByName.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                Packs.Add(kv.Key);
                PackIcons.Add(kv.Value);
            }

            Version++;

            // Diagnostics: category/density breakdown so a missing region pack
            // shows up as e.g. "Residential/Any" instead of silently vanishing.
            var byCat = string.Join(", ", Buildings
                .GroupBy(b => $"{b.Category}/{b.Density}")
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key}={g.Count()}"));
            Mod.Log.Info($"Indexed {Buildings.Count} spawnable buildings in {Families.Count} families ({Packs.Count} packs). Breakdown: {byCat}");
        }

        /// <summary>
        /// FindIt's PrefabIndexingSystem.IndexZones rule: derive a zone's density
        /// from its ZonePropertiesData (residential properties / space multiplier
        /// / scale flag), NOT from its name. Non-residential zones -> Any.
        /// </summary>
        private void BuildZoneDensityCache()
        {
            m_ZoneDensity.Clear();

            var zones = m_ZoneQuery.ToEntityArray(Allocator.Temp);
            var zoneProps = m_ZoneQuery.ToComponentDataArray<ZonePropertiesData>(Allocator.Temp);

            // Per zone: does any spawnable building of that zone use a lot wider
            // than 2 cells? (distinguishes Row housing from Medium)
            var wideZones = new HashSet<Entity>();
            var buildingEntities = m_SpawnableQuery.ToEntityArray(Allocator.Temp);
            foreach (var be in buildingEntities)
            {
                if (EntityManager.TryGetComponent<SpawnableBuildingData>(be, out var sb) &&
                    EntityManager.TryGetComponent<BuildingData>(be, out var bd) &&
                    bd.m_LotSize.x > 2)
                {
                    wideZones.Add(sb.m_ZonePrefab);
                }
            }

            buildingEntities.Dispose();

            for (var i = 0; i < zones.Length; i++)
            {
                var zone = zones[i];
                var info = zoneProps[i];

                if (info.m_ResidentialProperties <= 0f)
                {
                    m_ZoneDensity[zone] = DensityType.Any; // commercial/industrial/office
                    continue;
                }

                if (!info.m_ScaleResidentials)
                {
                    m_ZoneDensity[zone] = DensityType.Low;
                }
                else if (info.m_ResidentialProperties / info.m_SpaceMultiplier < 1f)
                {
                    m_ZoneDensity[zone] = wideZones.Contains(zone) ? DensityType.Medium : DensityType.Row;
                }
                else
                {
                    m_ZoneDensity[zone] = DensityType.High;
                }
            }

            zones.Dispose();
            zoneProps.Dispose();
        }

        private bool TryIndex(Entity entity, out BuildingEntry entry)
        {
            entry = null;

            if (!m_PrefabSystem.TryGetPrefab<BuildingPrefab>(entity, out var buildingPrefab))
            {
                return false;
            }

            // Never index our own runtime prefabs.
            if (buildingPrefab.name.StartsWith(PaletteZoneSystem.ClonePrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var spawnable = EntityManager.GetComponentData<SpawnableBuildingData>(entity);
            var zonePrefabEntity = spawnable.m_ZonePrefab;

            if (zonePrefabEntity == Entity.Null ||
                !EntityManager.HasComponent<ZoneData>(zonePrefabEntity))
            {
                return false;
            }

            var zoneData = EntityManager.GetComponentData<ZoneData>(zonePrefabEntity);
            m_PrefabSystem.TryGetPrefab<ZonePrefab>(zonePrefabEntity, out var zonePrefab);

            // --- Category (same switch as FindIt) ---
            ZoneCategory category;
            switch (zoneData.m_AreaType)
            {
                case Game.Zones.AreaType.Residential:
                    category = HasMixedAmbience(zonePrefabEntity)
                        ? ZoneCategory.Mixed
                        : ZoneCategory.Residential;
                    break;
                case Game.Zones.AreaType.Commercial:
                    category = ZoneCategory.Commercial;
                    break;
                case Game.Zones.AreaType.Industrial:
                    category = zonePrefab != null && zonePrefab.m_Office
                        ? ZoneCategory.Office
                        : ZoneCategory.Industrial;
                    break;
                default:
                    return false; // not a player-zonable building
            }

            // First zone of each category provides the filter icon.
            if (string.IsNullOrEmpty(CategoryIcons[(int)category]))
            {
                CategoryIcons[(int)category] = m_ImageSystem.GetIconOrGroupIcon(zonePrefabEntity);
            }

            var buildingData = EntityManager.GetComponentData<BuildingData>(entity);
            var zoneIcon = m_ImageSystem.GetIconOrGroupIcon(zonePrefabEntity);
            var categoryIcon = CategoryIcons[(int)category];
            ResolveTheme(buildingPrefab, zonePrefab, out var themeName, out var themeIcon);

            entry = new BuildingEntry
            {
                PrefabName = buildingPrefab.name,
                FamilyKey = BuildingFamilies.KeyOf(buildingPrefab.name, buildingData.m_LotSize.x, buildingData.m_LotSize.y),
                DisplayName = LocaleHelper.GetAssetName(buildingPrefab),
                PrefabEntity = entity,
                Category = category,
                Density = ResolveDensity(entity, zonePrefabEntity),
                Level = spawnable.m_Level,
                LotSize = buildingData.m_LotSize,
                Height = EntityManager.HasComponent<ObjectGeometryData>(entity)
                    ? EntityManager.GetComponentData<ObjectGeometryData>(entity).m_Size.y
                    : 0f,
                Packs = CollectPacks(buildingPrefab, zonePrefab),
                ThemeName = themeName,
                ThemeIcon = themeIcon,
                IsMod = buildingPrefab.isSubscribedMod,
                IsBuiltin = buildingPrefab.isBuiltin,

                // The card picture: see ResolveThumbnail.
                Thumbnail = ResolveThumbnail(buildingPrefab),

                // Drawn behind the picture and shown if it fails, so a card is
                // never blank (as FindIt does); also the zone badge of the card.
                FallbackThumbnail = string.IsNullOrEmpty(zoneIcon) ? categoryIcon : zoneIcon,
            };

            return true;
        }

        private bool HasMixedAmbience(Entity zonePrefabEntity)
        {
            return EntityManager.TryGetComponent<GroupAmbienceData>(zonePrefabEntity, out var ambience)
                   && ambience.m_AmbienceType == Game.Simulation.GroupAmbienceType.ResidentialMixed;
        }

        /// <summary>
        /// Density resolution. Signature buildings first (SignatureBuildingData),
        /// then the zone's ZonePropertiesData-derived density (FindIt's rule,
        /// robust to region-pack zones that ignore the vanilla naming).
        /// </summary>
        private DensityType ResolveDensity(Entity buildingEntity, Entity zonePrefabEntity)
        {
            if (EntityManager.HasComponent<SignatureBuildingData>(buildingEntity))
            {
                return DensityType.Signature;
            }

            return m_ZoneDensity.TryGetValue(zonePrefabEntity, out var density)
                ? density
                : DensityType.Any;
        }

        /// <summary>
        /// Resolves the asset packs a building belongs to via AssetPackItem
        /// (checked on both the building and its zone prefab), registering each
        /// pack's icon in <see cref="m_PackIconByName"/> for the filter row.
        /// Returns an empty array for base-game buildings.
        /// </summary>
        private string[] CollectPacks(BuildingPrefab buildingPrefab, ZonePrefab zonePrefab)
        {
            var names = new List<string>();
            CollectPacksFrom(buildingPrefab, names);
            CollectPacksFrom(zonePrefab, names);
            return names.Count == 0 ? Array.Empty<string>() : names.ToArray();
        }

        /// <summary>
        /// The picture of a building, in order:
        ///  1. The prefab's own icon when it is a real file (coui://ail/... from
        ///     Asset Icon Library, assetdb://...).
        ///  2. Otherwise ImageSystem only gives a thumbnail://ThumbnailCamera/... URL,
        ///     which shows nothing in a mod panel (the game's thumbnail cache only
        ///     looks up pre-baked atlases). Then: Building Icon Generator's picture
        ///     when that mod is installed; otherwise the mod's own generated picture
        ///     (IconGeneratorSystem), which is requested when it does not exist yet.
        ///  3. Empty while there is none: the UI draws a placeholder.
        /// </summary>
        private string ResolveThumbnail(BuildingPrefab buildingPrefab)
        {
            var thumbnail = ImageSystem.GetThumbnail(buildingPrefab);

            // A real, renderable icon (coui://ail/..., assetdb://...): keep it.
            if (!string.IsNullOrEmpty(thumbnail) &&
                !thumbnail.StartsWith("thumbnail://", StringComparison.OrdinalIgnoreCase))
            {
                return thumbnail;
            }

            // Building Icon Generator, when installed, has a real picture of every
            // building: its 128 px thumbnail. Missing ones are requested by the
            // panel for the cards on screen.
            if (BigIcons.IsInstalled)
            {
                if (BigIcons.TryGetThumbnailUri(buildingPrefab.name, out var bigThumbnail))
                {
                    return bigThumbnail;
                }

                return string.Empty;
            }

            var generated = m_IconGenerator.GetGeneratedIcon(buildingPrefab.name);
            if (!string.IsNullOrEmpty(generated))
            {
                return generated;
            }

            m_IconGenerator.RequestIcon(buildingPrefab);
            return string.Empty;
        }

        /// <summary>
        /// Resolves a building's theme (European / North American / region theme)
        /// via ThemeObject (checked on the building then its zone prefab). The
        /// icon is the ThemePrefab's own UIObject flag, shown as a card badge.
        /// </summary>
        private void ResolveTheme(BuildingPrefab buildingPrefab, ZonePrefab zonePrefab, out string themeName, out string themeIcon)
        {
            var theme = buildingPrefab?.GetComponent<ThemeObject>()?.m_Theme
                        ?? zonePrefab?.GetComponent<ThemeObject>()?.m_Theme;

            if (theme == null)
            {
                themeName = string.Empty;
                themeIcon = string.Empty;
                return;
            }

            themeName = theme.name;
            themeIcon = ResolvePrefabIcon(theme);
        }

        /// <summary>
        /// Icon URL of a prefab: its managed UIObject.m_Icon, falling back to the
        /// game's ImageSystem (so themes/packs without an explicit UIObject still
        /// resolve their flag). Empty when nothing is found.
        /// </summary>
        private string ResolvePrefabIcon(PrefabBase prefab)
        {
            var icon = prefab.GetComponent<UIObject>()?.m_Icon;
            if (string.IsNullOrEmpty(icon) && m_PrefabSystem.TryGetEntity(prefab, out var entity))
            {
                icon = m_ImageSystem.GetIconOrGroupIcon(entity);
            }

            return string.IsNullOrEmpty(icon) ? string.Empty : icon;
        }

        private void CollectPacksFrom(PrefabBase prefab, List<string> names)
        {
            var item = prefab?.GetComponent<AssetPackItem>();
            if (item?.m_Packs == null)
            {
                return;
            }

            foreach (var pack in item.m_Packs)
            {
                if (pack == null || names.Contains(pack.name))
                {
                    continue;
                }

                names.Add(pack.name);

                if (!m_PackIconByName.ContainsKey(pack.name))
                {
                    var icon = ResolvePrefabIcon(pack);
                    m_PackIconByName[pack.name] = string.IsNullOrEmpty(icon)
                        ? "coui://uil/Standard/StarAll.svg"
                        : icon;
                }
            }
        }
    }
}
