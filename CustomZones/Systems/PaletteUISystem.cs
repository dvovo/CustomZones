// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Binding architecture follows Platter's P_UISystem and FindIt's
// FindItUISystem (ValueBindingHelper + TriggerBinding + virtual scroll).

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
        private int m_LastIconVersion = -1;
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
        private ValueBindingHelper<FamilyUIEntry[]> m_PreviewFamily;
        private string m_PreviewKey = string.Empty;
        private ValueBindingHelper<int> m_FamilyCount;
        private ValueBindingHelper<CoverageUIEntry> m_Coverage;
        private ValueBindingHelper<string[]> m_ColorChoices;
        private ValueBindingHelper<string[]> m_IconChoices;
        private ValueBindingHelper<SavedIconUIEntry[]> m_IconLibrary;
        private int m_LastLibraryVersion = -1;
        private readonly Dictionary<string, string> m_LibraryPreviews = new();
        private ValueBindingHelper<string[]> m_Packs;

        // Sharing zones: the exported files, the folder they are in and the last export ("file|n").
        private ValueBindingHelper<string[]> m_ImportFiles;

        // Zone statistics: [painted cells, buildings, homes, households, jobs, workers].
        private ValueBindingHelper<int[]> m_ZoneStats;
        private EntityQuery m_StatsBuildingQuery;
        private ValueBindingHelper<string> m_ExportFolder;
        private ValueBindingHelper<string> m_ExportResult;
        private int m_ExportCount;
        private ValueBindingHelper<string[]> m_PackIcons;
        private ValueBindingHelper<int> m_FilterLotWidth;
        private ValueBindingHelper<int> m_FilterLotDepth;
        private ValueBindingHelper<int> m_FilterHeight;
        private ValueBindingHelper<int> m_FilterLevel;
        private ValueBindingHelper<string> m_FilterPack;
        private ValueBindingHelper<string> m_FilterSearch;
        private ValueBindingHelper<bool> m_FilterSelectedOnly;
        private ValueBindingHelper<bool> m_FilterGrowable;
        private ValueBindingHelper<string> m_FilterType;
        private ValueBindingHelper<int> m_PaintedCells;

        // Type filter of the browser: "" = the zone's own type, "all", or "category:density".
        private string m_TypeFilter = string.Empty;
        private bool m_GrowableOnly;
        private ValueBindingHelper<double> m_ScrollIndex;
        private ValueBindingHelper<double> m_MaxScrollIndex;
        private ValueBindingHelper<int> m_GridColumnsBinding;
        private ValueBindingHelper<int> m_GridRowsBinding;
        private ValueBindingHelper<bool> m_BigInstalled;
        private ValueBindingHelper<bool> m_SelectedCanLevelUp;
        private ValueBindingHelper<int[]> m_SelectedLevels;
        private Entity m_LevelsEntity;
        private Entity m_LevelsPrefab;
        private bool m_LevelsUnderConstruction;
        private bool m_LevelsHistoric;

        private LevelUpControlSystem m_LevelUpSystem;
        private Game.UI.InGame.SelectedInfoUISystem m_SelectedInfoSystem;

        private bool m_ZoneToolWasActive;

        // A zone chosen in the panel, to be picked in the zoning toolbar as well, and
        // the frames left to wait for it (a new zone gets its zone type a frame or two
        // after it is created).
        private string m_PaintPaletteId = string.Empty;
        private int m_PaintWaitFrames;

        // "Place it in the city" from the large preview: the building handed to the
        // game's placement tool and the zone tool that was open. When the player
        // leaves the placement (Escape / right click), the zone tool, the panel and
        // the preview come back instead of the whole build menu closing.
        private PrefabBase m_PlacingPrefab;
        private ZonePrefab m_PlacingReturnZone;
        private ValueBindingHelper<int> m_ReturnFromPlacing;

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
                    if (m_IconChoices.Value.Length == 0)
                    {
                        m_IconChoices.Value = m_ZoneSystem.GetIconChoices();
                        m_ColorChoices.Value = m_ZoneSystem.GetGameZoneColors();
                    }

                    PushPalettes();
                    PushCoverage();
                }
            });

            m_ZoneToolActive = CreateBinding("ZoneToolActive", false);
            m_ActiveZonePalette = CreateBinding("ActiveZonePalette", string.Empty);
            m_Palettes = CreateBinding("Palettes", Array.Empty<PaletteUIEntry>());
            m_Families = CreateBinding("Families", Array.Empty<FamilyUIEntry>());
            m_PreviewFamily = CreateBinding("PreviewFamily", Array.Empty<FamilyUIEntry>());
            m_FamilyCount = CreateBinding("FamilyCount", 0);
            m_Coverage = CreateBinding("Coverage", new CoverageUIEntry());
            m_ColorChoices = CreateBinding("ColorChoices", ZoneColors.Choices);
            m_IconChoices = CreateBinding("IconChoices", Array.Empty<string>());
            m_IconLibrary = CreateBinding("IconLibrary", Array.Empty<SavedIconUIEntry>());
            m_Packs = CreateBinding("Packs", Array.Empty<string>());
            m_PackIcons = CreateBinding("PackIcons", Array.Empty<string>());
            m_BigInstalled = CreateBinding("BigInstalled", false);

            // "Level up" button in the header of the selected building's info panel.
            m_SelectedCanLevelUp = CreateBinding("SelectedCanLevelUp", false);
            m_SelectedLevels = CreateBinding("SelectedLevels", Array.Empty<int>());
            m_ReturnFromPlacing = CreateBinding("ReturnFromPlacing", 0);
            CreateTrigger<int>("SetSelectedLevel", level => LevelUpControlSystem.SetLevelSelectedRequested = level);

            m_SelectedPalette = CreateBinding("SelectedPalette", "SetSelectedPalette", string.Empty, id =>
            {
                m_SelectedPaletteId = id ?? string.Empty;
                m_ScrollRow = 0;
                MarkBrowserDirty();
                PushCoverage();
            });

            // A zone clicked in the panel's list: also picked in the zoning toolbar.
            CreateTrigger<string>("PickZone", SelectToPaint);

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
            m_FilterLotDepth = CreateBinding("FilterLotDepth", "SetFilterLotDepth", 0,
                v => { m_Filters.LotDepth = v; MarkBrowserDirty(); });
            m_FilterHeight = CreateBinding("FilterHeight", "SetFilterHeight", 0,
                v => { m_Filters.HeightBand = v; MarkBrowserDirty(); });
            m_FilterLevel = CreateBinding("FilterLevel", "SetFilterLevel", 0,
                v => { m_Filters.Level = Math.Max(0, Math.Min(5, v)); MarkBrowserDirty(); });
            m_FilterPack = CreateBinding("FilterPack", "SetFilterPack", string.Empty,
                v => { m_Filters.Pack = v ?? string.Empty; MarkBrowserDirty(); });
            m_FilterSearch = CreateBinding("FilterSearch", "SetFilterSearch", string.Empty,
                v => { m_Filters.Search = v ?? string.Empty; MarkBrowserDirty(); });
            m_FilterSelectedOnly = CreateBinding("FilterSelectedOnly", "SetFilterSelectedOnly", false,
                v => { m_Filters.SelectedOnly = v; MarkBrowserDirty(); });

            // Building type shown in the browser: the zone's own type by default; a
            // zone may also take buildings of other types (mixed zones).
            m_FilterType = CreateBinding("FilterType", "SetFilterType", string.Empty,
                v => { m_TypeFilter = v ?? string.Empty; m_ScrollRow = 0; MarkBrowserDirty(); });

            // Painted cells of a zone, counted on request (before deleting it).
            m_PaintedCells = CreateBinding("PaintedCells", -1);
            CreateTrigger<string>("CountPaintedCells", id => m_PaintedCells.Value = m_ZoneSystem.CountPaintedCells(id));

            // "Can grow now": only buildings the city currently has demand for.
            m_FilterGrowable = CreateBinding("FilterGrowable", "SetFilterGrowable", false,
                v => { m_GrowableOnly = v; MarkBrowserDirty(); });

            CreateTrigger<string, int, int>("CreatePalette", (name, category, density) =>
            {
                var palette = PaletteStorage.Instance.Create(name, (ZoneCategory)category, (DensityType)density);
                SelectToPaint(palette.Id);
            });

            CreateTrigger<string, string>("RenamePalette",
                (id, name) => PaletteStorage.Instance.Rename(id, name));

            CreateTrigger<string, string>("SetPaletteColor",
                (id, color) => PaletteStorage.Instance.SetColor(id, color));

            CreateTrigger<string, string>("SetPaletteIcon",
                (id, icon) => PaletteStorage.Instance.SetIcon(id, icon));

            // Icon library: designed icons kept apart from the zones.
            CreateTrigger<string>("SaveIconToLibrary", id =>
            {
                var palette = PaletteStorage.Instance.GetById(id);
                if (palette != null && PaletteZoneSystem.TryParseGeneratedIcon(palette.Icon, out _, out _))
                {
                    IconLibrary.Instance.Add(palette.Name, palette.Icon, palette.Color);
                }
            });
            CreateTrigger<string>("RemoveLibraryIcon", id => IconLibrary.Instance.Remove(id));

            // Icon drawn from the zone's buildings; again = another set of them.
            CreateTrigger<string>("GeneratePaletteIcon", GenerateIcon);

            CreateTrigger<string>("DuplicatePalette", id =>
            {
                var copy = PaletteStorage.Instance.Duplicate(id);
                if (copy != null)
                {
                    Select(copy.Id);
                }
            });

            // Sharing: export one zone (or all, with ""), list the files, import one.
            m_ImportFiles = CreateBinding("ImportFiles", Array.Empty<string>());

            m_ZoneStats = CreateBinding("ZoneStats", Array.Empty<int>());
            m_StatsBuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            CreateTrigger<string>("RefreshZoneStats", id => m_ZoneStats.Value = ComputeZoneStats(id));
            m_ExportFolder = CreateBinding("ExportFolder", PaletteStorage.Instance.ExportFolder);
            m_ExportResult = CreateBinding("ExportResult", string.Empty);
            CreateTrigger<string>("ExportZone", id =>
            {
                var file = PaletteStorage.Instance.Export(string.IsNullOrEmpty(id) ? null : id);
                m_ExportResult.Value = $"{file ?? string.Empty}|{++m_ExportCount}";
                m_ImportFiles.Value = PaletteStorage.Instance.ExportFiles().ToArray();
            });
            CreateTrigger("RefreshImportFiles", () => m_ImportFiles.Value = PaletteStorage.Instance.ExportFiles().ToArray());
            CreateTrigger<string>("ImportZone", file =>
            {
                var zone = PaletteStorage.Instance.Import(file);
                if (zone != null)
                {
                    SelectToPaint(zone.Id);
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

            // A card: the design with every level it has (click again: remove them all).
            // Single levels are taken away with the level numbers.
            CreateTrigger<string, int>("ToggleFamilyCard", (key, level) =>
            {
                var palette = PaletteStorage.Instance.GetById(m_SelectedPaletteId);
                if (palette == null || !m_IndexSystem.FamiliesByKey.TryGetValue(key, out var family))
                {
                    return;
                }

                var any = BuildingFamilies.AnySelected(palette, key);
                PaletteStorage.Instance.SetSelections(m_SelectedPaletteId, DesignKeys(family), !any);
            });

            // The building open in the large preview (empty: closed).
            CreateTrigger<string>("SetPreviewFamily", key =>
            {
                m_PreviewKey = key ?? string.Empty;
                PushPreviewFamily(PaletteStorage.Instance.GetById(m_SelectedPaletteId));
            });

            // Large preview, "All its levels": the whole design on or off.
            CreateTrigger<string, bool>("SetFamilyAllLevels", (key, allowed) =>
            {
                if (!string.IsNullOrEmpty(m_SelectedPaletteId) && m_IndexSystem.FamiliesByKey.TryGetValue(key, out var family))
                {
                    PaletteStorage.Instance.SetSelections(m_SelectedPaletteId, DesignKeys(family).ToList(), allowed);
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

                // Whole designs (every level they have), added or removed.
                var keys = m_FilteredCache.SelectMany(DesignKeys).ToList();
                PaletteStorage.Instance.SetSelections(m_SelectedPaletteId, keys, allowed);
            });

            CreateTrigger<string, bool>("SetPaletteLevelUp",
                (id, value) => PaletteStorage.Instance.SetLevelUp(id, value));

            CreateTrigger<string, bool>("SetPaletteKeepDesign",
                (id, value) => PaletteStorage.Instance.SetKeepDesign(id, value));

            CreateTrigger<string, int>("SetPaletteGrowth",
                (id, value) => PaletteStorage.Instance.SetGrowth(id, value == 1 ? GrowthMode.SkipLevels : GrowthMode.LikeGame));

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
                var returnZone = m_ToolSystem.activeTool is ZoneToolSystem ? m_ToolSystem.activePrefab as ZonePrefab : null;
                if (prefabSystem.TryGetPrefab<PrefabBase>(building.PrefabEntity, out var prefab) &&
                    m_ToolSystem.ActivatePrefabTool(prefab))
                {
                    m_PlacingPrefab = prefab;
                    m_PlacingReturnZone = returnZone;
                    m_PanelVisible.Value = false;
                }
            });

            CreateTrigger("LevelUpSelected", () => LevelUpControlSystem.LevelUpSelectedRequested = true);

            CreateTrigger("ResetFilters", () =>
            {
                m_Filters.Level = 0;
                m_Filters.LotWidth = 0;
                m_Filters.LotDepth = 0;
                m_Filters.HeightBand = 0;
                m_Filters.Pack = string.Empty;
                m_Filters.Search = string.Empty;
                m_Filters.SelectedOnly = false;
                m_GrowableOnly = false;
                m_TypeFilter = string.Empty;
                PushFilterValues();
                MarkBrowserDirty();
            });
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            HandlePlacingReturn();
            PickPaintZone();
            HandleToolSelection();

            var selectedEntity = m_SelectedInfoSystem.selectedEntity;
            var swapping = selectedEntity != Entity.Null &&
                           selectedEntity == m_LevelsEntity &&
                           EntityManager.HasComponent<Game.Objects.UnderConstruction>(selectedEntity);

            // While a building changes level (about a second) its controls stay as
            // they were, instead of vanishing and coming back.
            var showLevels = Mod.Settings?.ShowLevelControls ?? true;
            var canLevelUp = !showLevels ? false : swapping ? m_SelectedCanLevelUp.Value : m_LevelUpSystem.CanLevelUp(selectedEntity);
            if (m_SelectedCanLevelUp.Value != canLevelUp)
            {
                m_SelectedCanLevelUp.Value = canLevelUp;
            }

            // Level selector: worked out again only when the selection or its building changes.
            var selectedPrefab = selectedEntity != Entity.Null && EntityManager.HasComponent<PrefabRef>(selectedEntity)
                ? EntityManager.GetComponentData<PrefabRef>(selectedEntity).m_Prefab
                : Entity.Null;
            var underConstruction = selectedEntity != Entity.Null && EntityManager.HasComponent<Game.Objects.UnderConstruction>(selectedEntity) && !swapping;
            // Marking it historic (or not) in the game's panel also changes the selector.
            var historic = selectedEntity != Entity.Null && m_LevelUpSystem.IsHistoric(selectedEntity);
            if (!showLevels)
            {
                if (m_SelectedLevels.Value.Length > 0)
                {
                    m_SelectedLevels.Value = Array.Empty<int>();
                }

                m_LevelsEntity = Entity.Null;
            }
            else if (!swapping &&
                (selectedEntity != m_LevelsEntity || selectedPrefab != m_LevelsPrefab || underConstruction != m_LevelsUnderConstruction || historic != m_LevelsHistoric))
            {
                m_LevelsHistoric = historic;
                m_LevelsEntity = selectedEntity;
                m_LevelsPrefab = selectedPrefab;
                m_LevelsUnderConstruction = underConstruction;
                m_SelectedLevels.Value = underConstruction ? Array.Empty<int>() : m_LevelUpSystem.LevelStates(selectedEntity);
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

            if (IconLibrary.Instance.Version != m_LastLibraryVersion && m_PanelVisible.Value)
            {
                m_LastLibraryVersion = IconLibrary.Instance.Version;
                PushIconLibrary();
            }

            // A zone's toolbar icon was redrawn (new colour or icon): show it in the list.
            if (m_ZoneSystem.IconVersion != m_LastIconVersion)
            {
                m_LastIconVersion = m_ZoneSystem.IconVersion;
                PushPalettes();
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
            // Another zone: the browser starts again on that zone's own type.
            if ((id ?? string.Empty) != m_SelectedPaletteId && m_TypeFilter.Length > 0)
            {
                m_TypeFilter = string.Empty;
                m_FilterType.Value = m_TypeFilter;
            }

            m_SelectedPaletteId = id ?? string.Empty;
            m_SelectedPalette.Value = m_SelectedPaletteId;
            m_ScrollRow = 0;
            MarkBrowserDirty();
            PushCoverage();
        }

        /// <summary>A zone the player chose in the panel: selected, and picked in the zoning toolbar to paint.</summary>
        private void SelectToPaint(string id)
        {
            Select(id);
            m_PaintPaletteId = m_SelectedPaletteId;
            m_PaintWaitFrames = 120;
        }

        /// <summary>
        /// While the zoning tool is open, picks the zone chosen in the panel in the
        /// game's zoning toolbar, so the player can paint it straight away. (The other
        /// way round, a zone picked in the toolbar is selected in the panel by
        /// <see cref="HandleToolSelection"/>.)
        /// </summary>
        private void PickPaintZone()
        {
            if (m_PaintPaletteId.Length == 0)
            {
                return;
            }

            if (!(m_ToolSystem.activeTool is ZoneToolSystem) || m_PaintWaitFrames-- <= 0)
            {
                m_PaintPaletteId = string.Empty;
                return;
            }

            if (!m_ZoneSystem.TryGetPaletteZone(m_PaintPaletteId, out var zone))
            {
                return; // not created yet
            }

            var paletteId = m_PaintPaletteId;
            if (m_ToolSystem.activePrefab == zone)
            {
                m_PaintPaletteId = string.Empty;
                return;
            }

            // Only once the game has given the zone its zone type (a new zone gets it
            // a frame or two after it is created): before that it cannot be painted.
            var prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            if (!prefabSystem.TryGetEntity(zone, out var zoneEntity) ||
                !EntityManager.HasComponent<ZoneData>(zoneEntity) ||
                EntityManager.GetComponentData<ZoneData>(zoneEntity).m_ZoneType.m_Index == 0)
            {
                return;
            }

            m_PaintPaletteId = string.Empty;
            if (SelectInToolbar(zoneEntity) || m_ToolSystem.ActivatePrefabTool(zone))
            {
                // Already selected in the panel: HandleToolSelection must not select it again.
                m_ActiveZonePalette.Value = paletteId;
                m_ZoneToolWasActive = true;
            }
        }

        private static readonly System.Reflection.MethodInfo s_ToolbarSelectAsset =
            typeof(Game.UI.InGame.ToolbarUISystem).GetMethod(
                "SelectAsset",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null,
                new[] { typeof(Entity), typeof(bool) },
                null);

        /// <summary>
        /// Picks a zone the way a click in the zoning toolbar does (ToolbarUISystem's
        /// private SelectAsset with updateTool): the tool paints with it and the
        /// toolbar highlights it. When the tool is changed directly, the game updates
        /// the tool but keeps the old zone highlighted. False when that method
        /// cannot be called (the caller then activates the tool itself).
        /// </summary>
        private bool SelectInToolbar(Entity zoneEntity)
        {
            if (s_ToolbarSelectAsset == null)
            {
                return false;
            }

            try
            {
                var toolbar = World.GetExistingSystemManaged<Game.UI.InGame.ToolbarUISystem>();
                if (toolbar == null)
                {
                    return false;
                }

                s_ToolbarSelectAsset.Invoke(toolbar, new object[] { zoneEntity, true });
                return m_ToolSystem.activeTool is ZoneToolSystem && m_ToolSystem.activePrefab != null &&
                       World.GetOrCreateSystemManaged<PrefabSystem>().GetEntity(m_ToolSystem.activePrefab) == zoneEntity;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Could not pick the zone in the toolbar ({ex.Message}).");
                return false;
            }
        }

        /// <summary>
        /// What a custom zone holds now: [painted cells, buildings, homes, households,
        /// jobs, workers]. Asked by the zone window while it is open.
        /// </summary>
        private int[] ComputeZoneStats(string paletteId)
        {
            var stats = new int[6];
            stats[0] = m_ZoneSystem.CountPaintedCells(paletteId);
            if (stats[0] == 0)
            {
                return stats;
            }

            using var buildings = m_StatsBuildingQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            foreach (var building in buildings)
            {
                var prefab = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
                if (!EntityManager.HasComponent<SpawnableBuildingData>(prefab) ||
                    !m_LevelUpSystem.TryGetCustomZoneOf(building, out var zone) ||
                    zone.Id != paletteId)
                {
                    continue;
                }

                stats[1]++;
                if (EntityManager.TryGetComponent<BuildingPropertyData>(prefab, out var properties))
                {
                    stats[2] += properties.m_ResidentialProperties;
                }

                if (!EntityManager.HasBuffer<Game.Buildings.Renter>(building))
                {
                    continue;
                }

                var renters = EntityManager.GetBuffer<Game.Buildings.Renter>(building, true);

                for (var i = 0; i < renters.Length; i++)
                {
                    var renter = renters[i].m_Renter;
                    if (EntityManager.HasComponent<Game.Citizens.Household>(renter))
                    {
                        stats[3]++;
                    }
                    else if (EntityManager.TryGetComponent<Game.Companies.WorkProvider>(renter, out var work))
                    {
                        stats[4] += work.m_MaxWorkers;
                        if (EntityManager.HasBuffer<Game.Companies.Employee>(renter))
                        {
                            stats[5] += EntityManager.GetBuffer<Game.Companies.Employee>(renter, true).Length;
                        }
                    }
                }
            }

            return stats;
        }

        /// <summary>
        /// After "Place it in the city": once the player leaves the placement tool
        /// back to the default tool (Escape, right click), reopens the zone tool, the
        /// panel and the large preview. Choosing another tool just ends it.
        /// </summary>
        private void HandlePlacingReturn()
        {
            if (m_PlacingPrefab == null)
            {
                return;
            }

            if (m_ToolSystem.activeTool is ObjectToolSystem && m_ToolSystem.activePrefab == m_PlacingPrefab)
            {
                return; // still placing
            }

            var zone = m_PlacingReturnZone;
            var cancelled = m_ToolSystem.activeTool is DefaultToolSystem;
            m_PlacingPrefab = null;
            m_PlacingReturnZone = null;
            if (!cancelled || zone == null || !m_ToolSystem.ActivatePrefabTool(zone))
            {
                return;
            }

            // The zone tool is back (the toolbar reopens its menu); keep the panel open.
            m_ZoneToolWasActive = true;
            m_PanelVisible.Value = true;
            m_ReturnFromPlacing.Value = m_ReturnFromPlacing.Value + 1;
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

                // A custom zone picked in the game's zone toolbar: the panel shows it too.
                if (activePalette.Length > 0 && activePalette != m_SelectedPaletteId)
                {
                    Select(activePalette);
                }
            }

            m_ZoneToolWasActive = zoneToolActive;
        }

        private void PushFilterValues()
        {
            m_FilterLotWidth.Value = m_Filters.LotWidth;
            m_FilterLotDepth.Value = m_Filters.LotDepth;
            m_FilterHeight.Value = m_Filters.HeightBand;
            m_FilterLevel.Value = m_Filters.Level;
            m_FilterPack.Value = m_Filters.Pack;
            m_FilterSearch.Value = m_Filters.Search;
            m_FilterSelectedOnly.Value = m_Filters.SelectedOnly;
            m_FilterGrowable.Value = m_GrowableOnly;
            m_FilterType.Value = m_TypeFilter;
        }

        /// <summary>The icon library, each icon with a preview drawn in the colour it was saved with.</summary>
        private void PushIconLibrary()
        {
            var entries = new List<SavedIconUIEntry>();
            foreach (var icon in IconLibrary.Instance.Icons)
            {
                if (!m_LibraryPreviews.TryGetValue(icon.Id, out var preview) &&
                    PaletteZoneSystem.TryParseGeneratedIcon(icon.Value, out _, out var models))
                {
                    var colour = BuildingZoneIcon.ParseHex(icon.Color) ?? new UnityEngine.Color(0.55f, 0.6f, 0.65f, 1f);
                    preview = BuildingZoneIcon.Create("Library_" + icon.Id, models, colour) ?? string.Empty;
                    m_LibraryPreviews[icon.Id] = preview;
                }

                entries.Add(new SavedIconUIEntry { Id = icon.Id, Name = icon.Name, Value = icon.Value, Preview = preview ?? string.Empty });
            }

            m_IconLibrary.Value = entries.ToArray();
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
                    IconSource = p.Icon ?? string.Empty,
                    FamilyCount = p.Families.Count,
                    BuildingCount = SelectedBuildings(p).Count(),
                    LevelUp = p.LevelUp,
                    KeepDesign = p.KeepDesign,
                    Growth = (int)p.Growth,
                })
                .ToArray();
        }

        /// <summary>Selection keys of every level a design has.</summary>
        private static IEnumerable<string> DesignKeys(BuildingFamily family) =>
            family.Members.Select(m => m.Level).Distinct().Select(l => BuildingFamilies.SelectionKey(family.Key, l));

        /// <summary>A building of a zone (or itself) not unlocked yet in this city (milestones): it does not grow.</summary>
        private bool IsLocked(BuildingEntry building)
        {
            bool Locked(Entity prefab) => prefab != Entity.Null && EntityManager.HasComponent<Locked>(prefab) && EntityManager.IsComponentEnabled<Locked>(prefab);
            return Locked(building.PrefabEntity) ||
                   (EntityManager.HasComponent<SpawnableBuildingData>(building.PrefabEntity) &&
                    Locked(EntityManager.GetComponentData<SpawnableBuildingData>(building.PrefabEntity).m_ZonePrefab));
        }

        private bool Exists(string familyKey, int level) =>
            m_IndexSystem.FamiliesByKey.TryGetValue(familyKey, out var family) && family.MemberAt(level) != null;

        private BuildingEntry Member(string selection)
        {
            BuildingFamilies.ParseSelection(selection, out var key, out var level);
            return m_IndexSystem.FamiliesByKey[key].MemberAt(level);
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

                // The buildings that may be born (level 1 like the game, or each design's
                // lowest ticked level when the zone skips levels); and where they stop
                // levelling up, per lot size ("2x2|3": lots 2x2 stop at 3).
                var born = BuildingFamilies.BornSelections(palette, Exists)
                    .Select(Member)
                    .ToList();
                coverage.GrowableBuildings = born.Count;
                coverage.LockedBuildings = born.Count(IsLocked);
                coverage.NoBirth = born.Count == 0 && palette.Families.Count > 0;
                if (palette.LevelUp)
                {
                    coverage.Stops = born
                        .GroupBy(b => $"{b.LotSize.x}x{b.LotSize.y}")
                        .Select(g => (lot: g.Key, level: g.Max(b => m_LevelUpSystem.ReachableLevel(b, palette))))
                        .Where(x => x.level < 5)
                        .OrderBy(x => x.level)
                        .ThenBy(x => x.lot)
                        .Select(x => $"{x.lot}|{x.level}")
                        .ToArray();
                }
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
            if (m_TypeFilter == "all")
            {
                m_Filters.Category = ZoneCategory.Any;
                m_Filters.Density = DensityType.Any;
            }
            else if (m_TypeFilter.Length > 0)
            {
                var parts = m_TypeFilter.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var category) && int.TryParse(parts[1], out var density))
                {
                    m_Filters.Category = (ZoneCategory)category;
                    m_Filters.Density = (DensityType)density;
                }
            }

            // "In this zone": every building the zone has chosen, whatever its type
            // (a zone may mix types); the other filters still apply.
            if (m_Filters.SelectedOnly)
            {
                m_Filters.Category = ZoneCategory.Any;
                m_Filters.Density = DensityType.Any;
            }

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
                window.Add(BuildEntry(m_FilteredCache[i], palette));
            }

            m_Families.Value = window.ToArray();
            PushPreviewFamily(palette);
        }

        /// <summary>The building open in the large preview, kept up to date even when its card is off screen.</summary>
        private void PushPreviewFamily(PaletteDefinition palette)
        {
            m_PreviewFamily.Value = m_PreviewKey.Length > 0 && m_IndexSystem.FamiliesByKey.TryGetValue(m_PreviewKey, out var family)
                ? new[] { BuildEntry(family, palette) }
                : Array.Empty<FamilyUIEntry>();
        }

        private FamilyUIEntry BuildEntry(BuildingFamily family, PaletteDefinition palette)
        {
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

            return new FamilyUIEntry
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
                LevelHeights = Enumerable.Range(1, 5)
                    .Select(l => (int)Math.Round(ShownMemberAt(family, l)?.Height ?? 0f))
                    .ToArray(),
                LevelKinds = Enumerable.Range(1, 5)
                    .Select(l => family.HasLevel(l) ? 2 : ShownMemberAt(family, l) != null ? 1 : 0)
                    .ToArray(),
                Goods = BuildingDemand.NameOf(m_Demand.GoodsOf(shown)),
                HasDemand = m_Demand.DemandFor(shown, palette) >= 1,
                GoodsRole = m_Demand.RoleOf(shown),
            };
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
        /// <summary>
        /// The building that shows a level of a design: its own, or else the
        /// nearest level below (the game makes one model for two levels, so a
        /// design without a level 2 looks like its level 1). Null below its first level.
        /// </summary>
        private static BuildingEntry ShownMemberAt(BuildingFamily family, int level)
        {
            for (var l = level; l >= 1; l--)
            {
                var member = family.MemberAt(l);
                if (member != null)
                {
                    return member;
                }
            }

            return null;
        }

        private static string[] PerLevel(BuildingFamily family, Func<BuildingEntry, string> picture)
        {
            var pictures = new string[5];
            for (var i = 0; i < pictures.Length; i++)
            {
                var member = ShownMemberAt(family, i + 1);
                var url = member == null ? string.Empty : picture(member);
                pictures[i] = string.IsNullOrEmpty(url) && member != null
                    ? member.FallbackThumbnail ?? string.Empty
                    : url ?? string.Empty;
            }

            return pictures;
        }

        /// <summary>
        /// Draws the zone's icon from one of its buildings (its shape and roof colour).
        /// Each call takes the next building, so the player can browse until one fits.
        /// </summary>
        private void GenerateIcon(string paletteId)
        {
            var palette = PaletteStorage.Instance.GetById(paletteId);
            if (palette == null)
            {
                return;
            }

            var candidates = SelectedBuildings(palette)
                .GroupBy(b => b.PrefabName)
                .Select(g => (building: g.First(), model: IconModel(g.First())))
                .OrderByDescending(c => c.model.Width * c.model.Depth * c.model.Height)
                .ThenBy(c => c.building.PrefabName, StringComparer.Ordinal)
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            PaletteZoneSystem.TryParseGeneratedIcon(palette.Icon, out var seed, out _);
            seed++;

            // One building per icon, like the game's zone icons: each call shows the
            // next one, from the most imposing down.
            var count = 1;
            var picked = new List<int>();
            for (var j = 0; j < count; j++)
            {
                var index = (seed + (j * candidates.Count / count)) % candidates.Count;
                if (!picked.Contains(index))
                {
                    picked.Add(index);
                }
            }

            var models = picked
                .Select(i => candidates[i])
                .OrderByDescending(c => c.model.Height)
                .Select(c => WithRoofColour(c.building, c.model))
                .ToList();
            PaletteStorage.Instance.SetIcon(paletteId, PaletteZoneSystem.GeneratedIconValue(seed, models));
        }

        /// <summary>A building's size and shape for the zone icon.</summary>
        private CustomZones.Utilities.BuildingZoneIcon.Model IconModel(BuildingEntry building)
        {
            // Footprint and height of the building itself (its lot when unknown).
            var width = building.LotSize.x * 8f;
            var depth = building.LotSize.y * 8f;
            var height = 10f;
            var geometry = EntityManager.HasComponent<Game.Prefabs.ObjectGeometryData>(building.PrefabEntity)
                ? EntityManager.GetComponentData<Game.Prefabs.ObjectGeometryData>(building.PrefabEntity)
                : default;
            if (geometry.m_Size.y > 0.5f)
            {
                width = Math.Min(width, Math.Max(geometry.m_Size.x, 2f));
                depth = Math.Min(depth, Math.Max(geometry.m_Size.z, 2f));
                height = geometry.m_Size.y;
            }

            // The kind of drawing, from the zone of the building and the game's own
            // building families (the prefab names say what they are).
            var name = building.PrefabName ?? string.Empty;
            var preset = CustomZones.Utilities.BuildingZoneIcon.PresetFor(building.Category, building.Density, height, name);
            if (preset == CustomZones.Utilities.BuildingZoneIcon.Preset.House)
            {
                // The measured height includes the roof: the icon wants the eaves.
                height *= 0.7f;
            }

            var model = CustomZones.Utilities.BuildingZoneIcon.FromPreset(preset, width, depth, height);
            if (preset == CustomZones.Utilities.BuildingZoneIcon.Preset.Townhouses)
            {
                model.Units = Math.Max(2, Math.Min(4, building.LotSize.x + 1));
                model.Width = Math.Max(model.Width, model.Units * 5f);
            }

            if (name.IndexOf("Waterfront", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                model.Ground = CustomZones.Utilities.BuildingZoneIcon.Ground.Water;
            }

            if (name.IndexOf("Hotel", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Motel", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                model.Details |= CustomZones.Utilities.BuildingZoneIcon.Details.Sign;
            }

            return model;
        }

        /// <summary>The model with the roof colour of the building's own picture, when there is one.</summary>
        private CustomZones.Utilities.BuildingZoneIcon.Model WithRoofColour(BuildingEntry building, CustomZones.Utilities.BuildingZoneIcon.Model model)
        {
            string picture = null;
            if (BigIcons.TryGetPath(building.PrefabName, out var full) && !string.IsNullOrEmpty(full))
            {
                // The 128 px thumbnail is plenty for one average colour.
                var thumbnail = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full) ?? string.Empty, "thumbnails", System.IO.Path.GetFileName(full));
                picture = System.IO.File.Exists(thumbnail) ? thumbnail : full;
            }
            else if (m_IconGenerator.TryGetGeneratedPath(building.PrefabName, out var generated))
            {
                picture = generated;
            }

            var roof = CustomZones.Utilities.BuildingZoneIcon.SampleRoof(picture);
            if (roof.HasValue)
            {
                model.RoofColor = roof.Value;
            }

            return model;
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
