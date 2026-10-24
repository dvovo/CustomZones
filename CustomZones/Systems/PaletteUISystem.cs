// <copyright file="PaletteUISystem.cs">
// Custom Zones - Licensed under the MIT license.
// Binding architecture follows Platter's P_UISystem and FindIt's
// FindItUISystem (ValueBindingHelper + TriggerBinding + virtual scroll).
// </copyright>

namespace CustomZones.Systems
{
    using Game.Prefabs;
    using Game.Tools;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Unity.Entities;
    using CustomZones.Domain;
    using CustomZones.Domain.UIBinding;
    using CustomZones.Integration;
    using CustomZones.Storage;
    using CustomZones.Utilities;

    /// <summary>
    /// Feeds the zone editor (side panel) with the custom zone library and the
    /// filterable building-family browser, and receives its actions. Painting
    /// itself is done with the vanilla zoning tool: every custom zone has its own
    /// button in the "Custom Zones" toolbar tab (PaletteZoneSystem).
    /// </summary>
    public partial class PaletteUISystem : ExtendedUISystemBase
    {
        private const int MinGridColumns = 2;
        private const int MaxGridColumns = 8;
        private const int MinGridRows = 1;
        private const int MaxGridRows = 12;

        private BuildingIndexSystem m_IndexSystem;
        private PaletteZoneSystem m_ZoneSystem;
        private IconGeneratorSystem m_IconGenerator;
        private ToolSystem m_ToolSystem;

        private readonly FamilyFilters m_Filters = new();
        private string m_SelectedPaletteId = string.Empty;

        private int m_LastIndexVersion = -1;
        private int m_LastStorageVersion = -1;
        private bool m_BrowserDirty = true;

        // Virtual scrolling (FindIt-style): only the visible rows of the filtered
        // list ever cross the UI binding.
        private readonly List<BuildingFamily> m_FilteredCache = new();
        private double m_ScrollRow;
        private int m_GridColumns = 3;
        private int m_GridRows = 4;
        private bool m_WindowDirty = true;

        private ValueBindingHelper<bool> m_PanelVisible;
        private ValueBindingHelper<bool> m_ZoneToolActive;
        private ValueBindingHelper<string> m_ActiveZonePalette;
        private ValueBindingHelper<PaletteUIEntry[]> m_Palettes;
        private ValueBindingHelper<string> m_SelectedPalette;
        private ValueBindingHelper<FamilyUIEntry[]> m_Families;
        private ValueBindingHelper<int> m_FamilyCount;
        private ValueBindingHelper<CoverageUIEntry> m_Coverage;
        private ValueBindingHelper<string[]> m_ColorChoices;
        private ValueBindingHelper<string[]> m_Packs;
        private ValueBindingHelper<string[]> m_PackIcons;
        private ValueBindingHelper<int> m_FilterLotWidth;
        private ValueBindingHelper<int> m_FilterLevel;
        private ValueBindingHelper<string> m_FilterPack;
        private ValueBindingHelper<string> m_FilterSearch;
        private ValueBindingHelper<bool> m_FilterSelectedOnly;
        private ValueBindingHelper<bool> m_FilterGrowable;
        private bool m_GrowableOnly;
        private ValueBindingHelper<double> m_ScrollIndex;
        private ValueBindingHelper<double> m_MaxScrollIndex;
        private ValueBindingHelper<int> m_GridColumnsBinding;
        private ValueBindingHelper<int> m_GridRowsBinding;
        private ValueBindingHelper<bool> m_BigInstalled;
        private ValueBindingHelper<bool> m_SelectedCanLevelUp;

        private LevelUpControlSystem m_LevelUpSystem;
        private Game.UI.InGame.SelectedInfoUISystem m_SelectedInfoSystem;

        private bool m_ZoneToolWasActive;

        // City demand per building (cards and zone summary), re-read while the panel is open.
        private const int DemandRefreshFrames = 60;
        private BuildingDemand m_Demand;
        private int m_DemandFrames;
        private PaletteZoneSpawnSystem m_SpawnSystem;
        private int m_LastGrowthStage = int.MinValue;
        private int m_LastGrowthLots = -1;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_IndexSystem = World.GetOrCreateSystemManaged<BuildingIndexSystem>();
            m_IconGenerator = World.GetOrCreateSystemManaged<IconGeneratorSystem>();
            m_ZoneSystem = World.GetOrCreateSystemManaged<PaletteZoneSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_Demand = new BuildingDemand(World);
            m_SpawnSystem = World.GetOrCreateSystemManaged<PaletteZoneSpawnSystem>();
            m_LevelUpSystem = World.GetOrCreateSystemManaged<LevelUpControlSystem>();
            m_SelectedInfoSystem = World.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>();

            m_PanelVisible = CreateBinding("PanelVisible", "SetPanelVisible", false, visible =>
            {
                if (visible)
                {
                    // Nothing is computed while the panel is closed: re-sync on open.
                    m_Demand.Refresh();
                    m_DemandFrames = 0;
                    MarkBrowserDirty();
                    PushPalettes();
                    PushCoverage();
                }
            });

            m_ZoneToolActive = CreateBinding("ZoneToolActive", false);
            m_ActiveZonePalette = CreateBinding("ActiveZonePalette", string.Empty);
            m_Palettes = CreateBinding("Palettes", Array.Empty<PaletteUIEntry>());
            m_Families = CreateBinding("Families", Array.Empty<FamilyUIEntry>());
            m_FamilyCount = CreateBinding("FamilyCount", 0);
            m_Coverage = CreateBinding("Coverage", new CoverageUIEntry());
            m_ColorChoices = CreateBinding("ColorChoices", ZoneColors.Choices);
            m_Packs = CreateBinding("Packs", Array.Empty<string>());
            m_PackIcons = CreateBinding("PackIcons", Array.Empty<string>());
            m_BigInstalled = CreateBinding("BigInstalled", false);

            // "Level up" button in the header of the selected building's info panel.
            m_SelectedCanLevelUp = CreateBinding("SelectedCanLevelUp", false);

            m_SelectedPalette = CreateBinding("SelectedPalette", "SetSelectedPalette", string.Empty, id =>
            {
                m_SelectedPaletteId = id ?? string.Empty;
                m_ScrollRow = 0;
                MarkBrowserDirty();
                PushCoverage();
            });

            m_ScrollIndex = CreateBinding("ScrollIndex", "SetScrollIndex", 0D, row =>
            {
                m_ScrollRow = row;
                m_WindowDirty = true;
            });
            m_MaxScrollIndex = CreateBinding("MaxScrollIndex", 0D);

            m_GridColumnsBinding = CreateBinding("GridColumns", "SetGridColumns", m_GridColumns, columns =>
            {
                m_GridColumns = Math.Max(MinGridColumns, Math.Min(columns, MaxGridColumns));
                m_GridColumnsBinding.Value = m_GridColumns;
                m_WindowDirty = true;
            });

            m_GridRowsBinding = CreateBinding("GridRows", "SetGridRows", m_GridRows, rows =>
            {
                m_GridRows = Math.Max(MinGridRows, Math.Min(rows, MaxGridRows));
                m_GridRowsBinding.Value = m_GridRows;
                m_WindowDirty = true;
            });

            // Filters (two-way). Zone type and density are forced from the
            // selected zone in RebuildFilteredCache, never chosen in the UI.
            m_FilterLotWidth = CreateBinding("FilterLotWidth", "SetFilterLotWidth", 0,
                v => { m_Filters.LotWidth = v; MarkBrowserDirty(); });
            m_FilterLevel = CreateBinding("FilterLevel", "SetFilterLevel", 0,
                v => { m_Filters.Level = Math.Max(0, Math.Min(5, v)); MarkBrowserDirty(); });
            m_FilterPack = CreateBinding("FilterPack", "SetFilterPack", string.Empty,
                v => { m_Filters.Pack = v ?? string.Empty; MarkBrowserDirty(); });
            m_FilterSearch = CreateBinding("FilterSearch", "SetFilterSearch", string.Empty,
                v => { m_Filters.Search = v ?? string.Empty; MarkBrowserDirty(); });
            m_FilterSelectedOnly = CreateBinding("FilterSelectedOnly", "SetFilterSelectedOnly", false,
                v => { m_Filters.SelectedOnly = v; MarkBrowserDirty(); });

            // "Can grow now": only buildings the city currently has demand for.
            m_FilterGrowable = CreateBinding("FilterGrowable", "SetFilterGrowable", false,
                v => { m_GrowableOnly = v; MarkBrowserDirty(); });

            CreateTrigger<string, int, int>("CreatePalette", (name, category, density) =>
            {
                var palette = PaletteStorage.Instance.Create(name, (ZoneCategory)category, (DensityType)density);
                Select(palette.Id);
            });

            CreateTrigger<string, string>("RenamePalette",
                (id, name) => PaletteStorage.Instance.Rename(id, name));

            CreateTrigger<string, string>("SetPaletteColor",
                (id, color) => PaletteStorage.Instance.SetColor(id, color));

            CreateTrigger<string>("DuplicatePalette", id =>
            {
                var copy = PaletteStorage.Instance.Duplicate(id);
                if (copy != null)
                {
                    Select(copy.Id);
                }
            });

            CreateTrigger<string>("DeletePalette", id =>
            {
                PaletteStorage.Instance.Delete(id);
                if (m_SelectedPaletteId == id)
                {
                    Select(PaletteStorage.Instance.Palettes.FirstOrDefault()?.Id ?? string.Empty);
                }
            });

            // One building = one family at one level.
            CreateTrigger<string, int>("ToggleFamilyLevel", (key, level) =>
            {
                if (!string.IsNullOrEmpty(m_SelectedPaletteId))
                {
                    PaletteStorage.Instance.ToggleSelection(m_SelectedPaletteId, key, level);
                }
            });

            // "Add / remove all shown": every card on the filtered list, at the level it shows.
            CreateTrigger<bool>("SetAllVisible", allowed =>
            {
                var palette = PaletteStorage.Instance.GetById(m_SelectedPaletteId);
                if (palette == null)
                {
                    return;
                }

                PaletteStorage.Instance.SetSelections(
                    m_SelectedPaletteId,
                    m_FilteredCache.Select(f => BuildingFamilies.SelectionKey(f.Key, ShownLevel(f, palette))).ToList(),
                    allowed);
            });

            CreateTrigger<string, bool>("SetPaletteLevelUp",
                (id, value) => PaletteStorage.Instance.SetLevelUp(id, value));

            CreateTrigger<string, bool>("SetPaletteKeepDesign",
                (id, value) => PaletteStorage.Instance.SetKeepDesign(id, value));

            // "Place this building": hands the building at the given level to the
            // game's own placement tool, to see how it looks in the city.
            CreateTrigger<string, int>("PlaceFamily", (key, level) =>
            {
                if (!m_IndexSystem.FamiliesByKey.TryGetValue(key, out var family))
                {
                    return;
                }

                var building = family.MemberAt(level) ?? family.Representative;
                if (building == null || building.PrefabEntity == Entity.Null)
                {
                    return;
                }

                var prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
                if (prefabSystem.TryGetPrefab<PrefabBase>(building.PrefabEntity, out var prefab) &&
                    m_ToolSystem.ActivatePrefabTool(prefab))
                {
                    m_PanelVisible.Value = false;
                }
            });

            CreateTrigger("LevelUpSelected", () => LevelUpControlSystem.LevelUpSelectedRequested = true);

            CreateTrigger("ResetFilters", () =>
            {
                m_Filters.Level = 0;
                m_Filters.LotWidth = 0;
                m_Filters.Pack = string.Empty;
                m_Filters.Search = string.Empty;
                m_Filters.SelectedOnly = false;
                m_GrowableOnly = false;
                PushFilterValues();
                MarkBrowserDirty();
            });
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            HandleToolSelection();

            var canLevelUp = m_LevelUpSystem.CanLevelUp(m_SelectedInfoSystem.selectedEntity);
            if (m_SelectedCanLevelUp.Value != canLevelUp)
            {
                m_SelectedCanLevelUp.Value = canLevelUp;
            }

            if (m_IndexSystem.Version != m_LastIndexVersion)
            {
                m_LastIndexVersion = m_IndexSystem.Version;
                m_Packs.Value = m_IndexSystem.Packs.ToArray();
                m_PackIcons.Value = m_IndexSystem.PackIcons.ToArray();
                MarkBrowserDirty();
                PushPalettes();
                PushCoverage();
            }

            if (PaletteStorage.Instance.Version != m_LastStorageVersion)
            {
                m_LastStorageVersion = PaletteStorage.Instance.Version;

                // Keep a valid selection (first zone when the selected one is gone).
                if (PaletteStorage.Instance.GetById(m_SelectedPaletteId) == null)
                {
                    Select(PaletteStorage.Instance.Palettes.FirstOrDefault()?.Id ?? string.Empty);
                }

                MarkBrowserDirty();
                PushPalettes();
                PushCoverage();

                // Zone creation happens in PaletteZoneSystem (PrefabUpdate, before
                // the vanilla ZoneSystem), never here.
            }

            if (!m_PanelVisible.Value)
            {
                return;
            }

            if (m_BigInstalled.Value != BigIcons.IsInstalled)
            {
                m_BigInstalled.Value = BigIcons.IsInstalled;
            }

            // Demand changes a few times a day: refresh the badges when it does.
            if (++m_DemandFrames >= DemandRefreshFrames)
            {
                m_DemandFrames = 0;
                if (m_Demand.Refresh())
                {
                    m_WindowDirty = true;
                    if (m_GrowableOnly)
                    {
                        MarkBrowserDirty();
                    }

                    PushCoverage();
                }
                else
                {
                    // The growth diagnosis changes with every spawn pass.
                    var (stage, lots) = ReadGrowth(PaletteStorage.Instance.GetById(m_SelectedPaletteId));
                    if (stage != m_LastGrowthStage || lots != m_LastGrowthLots)
                    {
                        PushCoverage();
                    }
                }
            }

            if (m_BrowserDirty)
            {
                m_BrowserDirty = false;
                RebuildFilteredCache();
            }

            if (m_WindowDirty)
            {
                m_WindowDirty = false;
                PushWindow();
            }
        }

        private void MarkBrowserDirty() => m_BrowserDirty = true;

        private void Select(string id)
        {
            m_SelectedPaletteId = id ?? string.Empty;
            m_SelectedPalette.Value = m_SelectedPaletteId;
            m_ScrollRow = 0;
            MarkBrowserDirty();
            PushCoverage();
        }

        /// <summary>
        /// Publishes whether the zoning tool is active and which custom zone (if
        /// any) is selected in the toolbar, and closes the panel when the player
        /// leaves the zoning tool.
        /// </summary>
        private void HandleToolSelection()
        {
            var zoneToolActive = m_ToolSystem.activeTool is ZoneToolSystem;

            var activePalette = string.Empty;
            if (zoneToolActive &&
                m_ToolSystem.activePrefab is ZonePrefab zone &&
                m_ZoneSystem.TryGetPaletteIdForZone(zone, out var paletteId))
            {
                activePalette = paletteId;
            }

            if (!zoneToolActive && m_ZoneToolWasActive && m_PanelVisible.Value)
            {
                m_PanelVisible.Value = false;
            }

            if (m_ZoneToolActive.Value != zoneToolActive)
            {
                m_ZoneToolActive.Value = zoneToolActive;
            }

            if (m_ActiveZonePalette.Value != activePalette)
            {
                m_ActiveZonePalette.Value = activePalette;
            }

            m_ZoneToolWasActive = zoneToolActive;
        }

        private void PushFilterValues()
        {
            m_FilterLotWidth.Value = m_Filters.LotWidth;
            m_FilterLevel.Value = m_Filters.Level;
            m_FilterPack.Value = m_Filters.Pack;
            m_FilterSearch.Value = m_Filters.Search;
            m_FilterSelectedOnly.Value = m_Filters.SelectedOnly;
            m_FilterGrowable.Value = m_GrowableOnly;
        }

        private void PushPalettes()
        {
            m_Palettes.Value = PaletteStorage.Instance.Palettes
                .Select(p => new PaletteUIEntry
                {
                    Id = p.Id,
                    Name = p.Name,
                    Category = (int)p.Category,
                    Density = (int)p.Density,
                    Color = p.Color,
                    Icon = m_ZoneSystem.GetZoneIcon(p.Id),
                    FamilyCount = p.Families.Count,
                    BuildingCount = SelectedBuildings(p).Count(),
                    LevelUp = p.LevelUp,
                    KeepDesign = p.KeepDesign,
                })
                .ToArray();
        }

        /// <summary>The prefabs a zone may give birth to (its family-at-level selections that exist).</summary>
        private IEnumerable<BuildingEntry> SelectedBuildings(PaletteDefinition palette)
        {
            foreach (var selection in palette.Families)
            {
                BuildingFamilies.ParseSelection(selection, out var key, out var level);
                if (m_IndexSystem.FamiliesByKey.TryGetValue(key, out var family))
                {
                    var member = family.MemberAt(level);
                    if (member != null)
                    {
                        yield return member;
                    }
                }
            }
        }

        /// <summary>What the selected zone can grow: lot widths covered and highest level.</summary>
        private void PushCoverage()
        {
            var coverage = new CoverageUIEntry();
            var palette = PaletteStorage.Instance.GetById(m_SelectedPaletteId);

            if (palette != null)
            {
                var widths = new SortedSet<int>();
                var blockedGoods = new SortedSet<string>();
                foreach (var building in SelectedBuildings(palette))
                {
                    coverage.GrowableBuildings++;
                    widths.Add(Math.Min(building.LotSize.x, 6));
                    coverage.MaxLevel = Math.Max(coverage.MaxLevel, building.Level);

                    if (m_Demand.DemandFor(building, palette) >= 1)
                    {
                        coverage.DemandReady++;
                    }
                    else
                    {
                        var goods = BuildingDemand.NameOf(m_Demand.GoodsOf(building));
                        if (goods.Length > 0)
                        {
                            blockedGoods.Add(goods);
                        }
                    }
                }

                coverage.CoveredWidths = widths.ToArray();
                coverage.BlockedGoods = blockedGoods.ToArray();
                (coverage.GrowthStage, coverage.GrowthLots) = ReadGrowth(palette);
                m_LastGrowthStage = coverage.GrowthStage;
                m_LastGrowthLots = coverage.GrowthLots;
            }

            m_Coverage.Value = coverage;
        }

        /// <summary>What the spawner found for the zone in its last pass (stage, free lots); -2 when unknown.</summary>
        private (int stage, int lots) ReadGrowth(PaletteDefinition palette)
        {
            if (palette == null)
            {
                return (-2, 0);
            }

            foreach (var (index, zone) in m_ZoneSystem.GetPaletteZoneIndices())
            {
                if (zone.Id == palette.Id)
                {
                    return m_SpawnSystem.TryGetGrowth(index, out var growth)
                        ? ((int)growth.Stage, growth.Lots)
                        : (-2, 0);
                }
            }

            return (-2, 0);
        }

        /// <summary>
        /// Level a card shows: the level filter when set, else the lowest level
        /// chosen for the zone, else the lowest level the building has.
        /// </summary>
        private int ShownLevel(BuildingFamily family, PaletteDefinition palette)
        {
            if (m_Filters.Level > 0 && family.HasLevel(m_Filters.Level))
            {
                return m_Filters.Level;
            }

            for (var level = 1; level <= 5; level++)
            {
                if (BuildingFamilies.IsSelected(palette, family.Key, level) && family.HasLevel(level))
                {
                    return level;
                }
            }

            return family.MinLevel;
        }

        /// <summary>Re-applies the filters over all families (managed cache; only the visible rows are sent).</summary>
        private void RebuildFilteredCache()
        {
            var palette = PaletteStorage.Instance.GetById(m_SelectedPaletteId);

            // A zone is locked to its type (and density, for residential): the
            // browser only shows families that can grow in it.
            m_Filters.Category = palette?.Category ?? ZoneCategory.Any;
            m_Filters.Density = palette?.Density ?? DensityType.Any;

            m_FilteredCache.Clear();
            if (palette != null)
            {
                var families = m_Filters.Apply(m_IndexSystem.Families, palette);
                if (m_GrowableOnly)
                {
                    families = families.Where(f => m_Demand.DemandFor(f.MemberAt(ShownLevel(f, palette)) ?? f.Representative, palette) >= 1);
                }

                m_FilteredCache.AddRange(families);
            }

            m_FamilyCount.Value = m_FilteredCache.Count;
            m_WindowDirty = true;
        }

        /// <summary>Sends the visible rows (+1 overscan row) of the filtered families.</summary>
        private void PushWindow()
        {
            var palette = PaletteStorage.Instance.GetById(m_SelectedPaletteId);

            var totalRows = Math.Ceiling(m_FilteredCache.Count / (double)m_GridColumns);
            var maxScroll = Math.Max(0, totalRows - m_GridRows);

            m_ScrollRow = Math.Max(0, Math.Min(m_ScrollRow, maxScroll));
            m_ScrollIndex.Value = m_ScrollRow;
            m_MaxScrollIndex.Value = maxScroll;

            var start = (int)(Math.Floor(m_ScrollRow) * m_GridColumns);
            var end = Math.Min(m_FilteredCache.Count, start + (m_GridColumns * (m_GridRows + 1)));
            var window = new List<FamilyUIEntry>(Math.Max(0, end - start));

            for (var i = start; i < end; i++)
            {
                var family = m_FilteredCache[i];
                var shownLevel = palette != null ? ShownLevel(family, palette) : family.MinLevel;
                var shown = family.MemberAt(shownLevel) ?? family.Representative;

                var selectedLevels = new List<int>();
                for (var level = 1; level <= 5; level++)
                {
                    if (BuildingFamilies.IsSelected(palette, family.Key, level))
                    {
                        selectedLevels.Add(level);
                    }
                }

                window.Add(new FamilyUIEntry
                {
                    Key = family.Key,
                    Name = family.DisplayName,
                    Thumbnail = ResolveThumbnail(shown),
                    FallbackThumbnail = shown.FallbackThumbnail,
                    LotSizes = family.LotSizes,
                    MinLevel = family.MinLevel,
                    MaxLevel = family.MaxLevel,
                    MemberCount = family.Members.Count,
                    Selected = selectedLevels.Contains(shownLevel),
                    Theme = family.ThemeName,
                    ThemeIcon = family.ThemeIcon,
                    IsMod = family.IsMod,
                    Packs = family.Packs,
                    Picture = ResolvePicture(shown),
                    ShownLevel = shownLevel,
                    SelectedLevels = selectedLevels.ToArray(),
                    LevelThumbnails = PerLevel(family, ResolveThumbnail),
                    LevelPictures = PerLevel(family, ResolvePicture),
                    Goods = BuildingDemand.NameOf(m_Demand.GoodsOf(shown)),
                    HasDemand = m_Demand.DemandFor(shown, palette) >= 1,
                    GoodsRole = m_Demand.RoleOf(shown),
                });
            }

            m_Families.Value = window.ToArray();
        }

        /// <summary>Large picture for the hover preview: BIG's full-size icon, else the card picture.</summary>
        private string ResolvePicture(BuildingEntry building)
        {
            if (BigIcons.IsInstalled && BigIcons.TryGetUri(building.PrefabName, out var uri))
            {
                return uri;
            }

            return ResolveThumbnail(building);
        }

        /// <summary>One picture per level 1..5 of the family ("" where the level does not exist).</summary>
        private static string[] PerLevel(BuildingFamily family, Func<BuildingEntry, string> picture)
        {
            var pictures = new string[5];
            for (var i = 0; i < pictures.Length; i++)
            {
                var member = family.MemberAt(i + 1);
                var url = member == null ? string.Empty : picture(member);
                pictures[i] = string.IsNullOrEmpty(url) && member != null
                    ? member.FallbackThumbnail ?? string.Empty
                    : url ?? string.Empty;
            }

            return pictures;
        }

        /// <summary>
        /// Picture of a card: BIG's thumbnail when installed (requested when
        /// missing), otherwise the indexed thumbnail (game icon / internal render).
        /// </summary>
        private string ResolveThumbnail(BuildingEntry building)
        {
            if (BigIcons.IsInstalled)
            {
                if (BigIcons.TryGetThumbnailUri(building.PrefabName, out var uri))
                {
                    return uri;
                }

                BigIcons.RequestGeneration(building.PrefabName);
                return building.Thumbnail ?? string.Empty;
            }

            if (string.IsNullOrEmpty(building.Thumbnail))
            {
                m_IconGenerator.Prioritize(building.PrefabName);
            }

            return building.Thumbnail ?? string.Empty;
        }
    }
}
