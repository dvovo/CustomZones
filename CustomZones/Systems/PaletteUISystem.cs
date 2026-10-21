// <copyright file="PaletteUISystem.cs">
// CustomZones - Licensed under the MIT license.
// Binding architecture follows Platter's P_UISystem and FindIt's
// FindItUISystem (ValueBindingHelper + TriggerBinding + virtual scroll).
// </copyright>

namespace CustomZones.Systems
{
    using Game.Prefabs;
    using Game.Tools;
    using System;
    using Unity.Entities;
    using System.Collections.Generic;
    using System.Linq;
    using CustomZones.Domain;
    using CustomZones.Domain.UIBinding;
    using CustomZones.Storage;
    using CustomZones.Utilities;

    /// <summary>
    /// Exposes the palette library and the filterable building browser to
    /// the React UI, and receives UI triggers. Painting happens with the
    /// vanilla zoning tool (palette zones live in their own toolbar
    /// category, created by PaletteZoneSystem), so there is no assignment
    /// UI anymore.
    /// </summary>
    public partial class PaletteUISystem : ExtendedUISystemBase
    {
        private BuildingIndexSystem m_IndexSystem;
        private PaletteZoneSystem m_ZoneSystem;
        private ToolSystem m_ToolSystem;

        private readonly BrowserFilters m_Filters = new();
        private string m_SelectedPaletteId = string.Empty;

        private int m_LastIndexVersion = -1;
        private int m_LastStorageVersion = -1;
        private bool m_BrowserDirty = true;

        // --- Virtual scrolling (FindIt-style): only the visible window of
        // the filtered list ever crosses the UI binding. ---
        public const int GridRows = 5;
        private const int MinGridColumns = 3;
        private const int MaxGridColumns = 12;
        private readonly List<BuildingEntry> m_FilteredCache = new();
        private double m_ScrollRow;
        private int m_GridColumns = 4;
        private bool m_WindowDirty = true;

        // --- Bindings to JS ---
        private ValueBindingHelper<bool> m_PanelVisible;
        private ValueBindingHelper<bool> m_PainterToolActiveBinding;
        private ValueBindingHelper<PaletteUIEntry[]> m_Palettes;
        private ValueBindingHelper<BuildingUIEntry[]> m_Buildings;
        private ValueBindingHelper<string[]> m_Packs;
        private ValueBindingHelper<string[]> m_PackIcons;
        private ValueBindingHelper<string> m_SelectedPalette;
        private ValueBindingHelper<int> m_FilterLotWidth;
        private ValueBindingHelper<int> m_FilterLotDepth;
        private ValueBindingHelper<int> m_FilterLevel;
        private ValueBindingHelper<string> m_FilterPack;
        private ValueBindingHelper<string> m_FilterSearch;
        private ValueBindingHelper<bool> m_FilterSelectedOnly;
        private ValueBindingHelper<double> m_ScrollIndex;
        private ValueBindingHelper<double> m_MaxScrollIndex;
        private ValueBindingHelper<int> m_BuildingCount;

        // Icon generation progress (0-100, and -1 when there is nothing to do).
        private ValueBindingHelper<int> m_IconProgress;
        private ValueBindingHelper<int> m_IconPending;
        private IconGeneratorSystem m_IconGenerator;
        private ValueBindingHelper<int> m_GridColumnsBinding;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_IndexSystem = World.GetOrCreateSystemManaged<BuildingIndexSystem>();
            m_IconGenerator = World.GetOrCreateSystemManaged<IconGeneratorSystem>();
            m_ZoneSystem = World.GetOrCreateSystemManaged<PaletteZoneSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();

            // ----- Value bindings -----
            m_PanelVisible = CreateBinding("PanelVisible", "SetPanelVisible", false, visible =>
            {
                if (visible)
                {
                    // Lazy refresh: nothing is computed while the panel is
                    // closed, so re-sync everything on open.
                    MarkBrowserDirty();
                    PushPalettes();
                }
            });

            m_PainterToolActiveBinding = CreateBinding("PainterToolActive", false);
            m_Palettes = CreateBinding("Palettes", Array.Empty<PaletteUIEntry>());
            m_Buildings = CreateBinding("Buildings", Array.Empty<BuildingUIEntry>());
            m_Packs = CreateBinding("Packs", Array.Empty<string>());
            m_PackIcons = CreateBinding("PackIcons", Array.Empty<string>());

            m_SelectedPalette = CreateBinding("SelectedPalette", "SetSelectedPalette",
                string.Empty, id =>
                {
                    m_SelectedPaletteId = id;
                    MarkBrowserDirty();
                    Mod.Log.Info($"UI selected palette '{id}'.");

                    // The painter is re-synced to m_SelectedPaletteId every
                    // frame in HandlePainterSelection (OnUpdate, sim thread),
                    // so we do NOT touch the entity from this UI callback.
                });

            m_ScrollIndex = CreateBinding("ScrollIndex", "SetScrollIndex", 0D, row =>
            {
                m_ScrollRow = row;
                m_WindowDirty = true;
            });
            m_MaxScrollIndex = CreateBinding("MaxScrollIndex", 0D);
            m_BuildingCount = CreateBinding("BuildingCount", 0);
            m_IconProgress = CreateBinding("IconProgress", -1);
            m_IconPending = CreateBinding("IconPending", 0);
            m_GridColumnsBinding = CreateBinding("GridColumns", "SetGridColumns", m_GridColumns, columns =>
            {
                m_GridColumns = Math.Max(MinGridColumns, Math.Min(columns, MaxGridColumns));
                m_GridColumnsBinding.Value = m_GridColumns;
                m_WindowDirty = true;
            });

            // ----- Filter bindings (two-way). Category + density are NOT
            // exposed: a palette is locked to its own zone type, so those are
            // forced in RebuildFilteredCache, not chosen in the UI. -----
            m_FilterLotWidth = CreateBinding("FilterLotWidth", "SetFilterLotWidth", 0,
                v => { m_Filters.LotWidth = v; MarkBrowserDirty(); });
            m_FilterLotDepth = CreateBinding("FilterLotDepth", "SetFilterLotDepth", 0,
                v => { m_Filters.LotDepth = v; MarkBrowserDirty(); });
            m_FilterLevel = CreateBinding("FilterLevel", "SetFilterLevel", 0,
                v => { m_Filters.Level = v; MarkBrowserDirty(); });
            m_FilterPack = CreateBinding("FilterPack", "SetFilterPack", string.Empty,
                v => { m_Filters.Pack = v; MarkBrowserDirty(); });
            m_FilterSearch = CreateBinding("FilterSearch", "SetFilterSearch", string.Empty,
                v => { m_Filters.Search = v; MarkBrowserDirty(); });
            m_FilterSelectedOnly = CreateBinding("FilterSelectedOnly", "SetFilterSelectedOnly", false,
                v => { m_Filters.SelectedOnly = v; MarkBrowserDirty(); });

            // ----- Triggers -----
            CreateTrigger<string, int, int>("CreatePalette", (name, category, density) =>
            {
                var palette = PaletteStorage.Instance.Create(
                    name, (ZoneCategory)category, (DensityType)density);
                m_SelectedPaletteId = palette.Id;
                m_SelectedPalette.Value = palette.Id;
            });

            CreateTrigger<string, string>("RenamePalette",
                (id, name) => PaletteStorage.Instance.Rename(id, name));

            CreateTrigger<string>("DuplicatePalette", id =>
            {
                var copy = PaletteStorage.Instance.Duplicate(id);
                if (copy != null)
                {
                    m_SelectedPaletteId = copy.Id;
                    m_SelectedPalette.Value = copy.Id;
                }
            });

            CreateTrigger<string>("DeletePalette", id =>
            {
                PaletteStorage.Instance.Delete(id);

                if (m_SelectedPaletteId == id)
                {
                    m_SelectedPaletteId = string.Empty;
                    m_SelectedPalette.Value = string.Empty;
                }
            });

            // "Place this building" (FindIt-style): hands the prefab to the
            // game's own placement tool so the user can drop it on the map and
            // see how it actually looks. Closes our panel so the map is visible.
            CreateTrigger<string>("PlaceBuilding", prefabName =>
            {
                if (!m_IndexSystem.ByName.TryGetValue(prefabName, out var entry) ||
                    entry.PrefabEntity == Entity.Null)
                {
                    Mod.Log.Warn($"PlaceBuilding: '{prefabName}' is not indexed.");
                    return;
                }

                var prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

                if (prefabSystem.TryGetPrefab<PrefabBase>(entry.PrefabEntity, out var prefab) &&
                    m_ToolSystem.ActivatePrefabTool(prefab))
                {
                    m_PanelVisible.Value = false;
                    Mod.Log.Info($"PlaceBuilding: placement tool activated for '{prefabName}'.");
                }
                else
                {
                    Mod.Log.Warn($"PlaceBuilding: could not activate the placement tool for '{prefabName}'.");
                }
            });

            CreateTrigger<string>("ToggleBuilding", prefabName =>
            {
                if (!string.IsNullOrEmpty(m_SelectedPaletteId))
                {
                    PaletteStorage.Instance.ToggleBuilding(m_SelectedPaletteId, prefabName);
                }
            });

            CreateTrigger<bool>("SetAllVisible", allowed =>
            {
                var palette = PaletteStorage.Instance.GetById(m_SelectedPaletteId);
                if (palette == null)
                {
                    return;
                }

                var visible = m_Filters.Apply(m_IndexSystem.Buildings, palette)
                    .Select(b => b.PrefabName);
                PaletteStorage.Instance.SetBuildings(m_SelectedPaletteId, visible, allowed);
            });

            // Dev probe: measures how fast the "photograph the real game" icon
            // pipeline could run (see CaptureProbeSystem). Results go to the log.
            CreateTrigger("MeasureCapture", () =>
                World.GetOrCreateSystemManaged<CaptureProbeSystem>().Request());

            CreateTrigger("ResetFilters", () =>
            {
                m_Filters.Category = ZoneCategory.Any;
                m_Filters.Density = DensityType.Any;
                m_Filters.LotWidth = 0;
                m_Filters.LotDepth = 0;
                m_Filters.Level = 0;
                m_Filters.Pack = string.Empty;
                m_Filters.Search = string.Empty;
                m_Filters.SelectedOnly = false;
                PushFilterValues();
                MarkBrowserDirty();
            });
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            HandlePainterSelection();
            PushIconProgress();

            if (m_IndexSystem.Version != m_LastIndexVersion)
            {
                m_LastIndexVersion = m_IndexSystem.Version;
                m_Packs.Value = m_IndexSystem.Packs.ToArray();
                m_PackIcons.Value = m_IndexSystem.PackIcons.ToArray();
                MarkBrowserDirty();
            }

            if (PaletteStorage.Instance.Version != m_LastStorageVersion)
            {
                m_LastStorageVersion = PaletteStorage.Instance.Version;
                MarkBrowserDirty();
                PushPalettes();

                // NOTE: zone creation is intentionally NOT triggered here.
                // It must happen in PaletteZoneSystem.OnUpdate, which runs in
                // PrefabUpdate BEFORE the vanilla ZoneSystem so a new zone gets
                // indexed the same frame. Calling it here (UIUpdate, after
                // ZoneSystem already ran) would create the zone too late and it
                // would never get a ZoneType index. PaletteZoneSystem picks up
                // the storage-version change on its next update.
            }

            // Everything below is UI-only work: skip it entirely while the
            // panel is closed (lazy refresh happens on open).
            if (!m_PanelVisible.Value)
            {
                return;
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

        /// <summary>
        /// Publishes icon-generation progress so the panel can show a bar while
        /// the renders are still being produced. -1 means "nothing to do", which
        /// the UI uses to hide the bar entirely.
        /// </summary>
        private void PushIconProgress()
        {
            var pending = m_IconGenerator.Pending;
            var total = m_IconGenerator.Total;

            var progress = pending == 0 || total == 0
                ? -1
                : Math.Max(0, Math.Min(100, (int)((total - pending) * 100L / total)));

            if (m_IconProgress.Value != progress)
            {
                m_IconProgress.Value = progress;
            }

            if (m_IconPending.Value != pending)
            {
                m_IconPending.Value = pending;
            }
        }

        private bool m_PainterWasActive;

        /// <summary>
        /// Tracks the painter tool selection. The painter always stays the
        /// active prefab (so the other zoning tabs remain visible). We only:
        /// (a) keep the painter pointed at the selected palette's zone,
        /// (b) publish active state to the UI (left-panel button visibility),
        /// (c) close the panel when the painter is deselected.
        /// </summary>
        private void HandlePainterSelection()
        {
            var painter = m_ZoneSystem.PainterZone;
            var isActive = painter != null && m_ToolSystem.activePrefab == painter;

            if (isActive)
            {
                // Keep the painter's ZoneType pointed at the selected palette.
                m_ZoneSystem.ApplyPaletteToPainter(m_SelectedPaletteId);
            }
            else if (m_PainterWasActive && m_PanelVisible.Value)
            {
                // Left the painter tool -> close the palette panel.
                m_PanelVisible.Value = false;
            }

            m_PainterToolActiveBinding.Value = isActive;
            m_PainterWasActive = isActive;
        }

        private void PushFilterValues()
        {
            m_FilterLotWidth.Value = m_Filters.LotWidth;
            m_FilterLotDepth.Value = m_Filters.LotDepth;
            m_FilterLevel.Value = m_Filters.Level;
            m_FilterPack.Value = m_Filters.Pack;
            m_FilterSearch.Value = m_Filters.Search;
            m_FilterSelectedOnly.Value = m_Filters.SelectedOnly;
        }

        private void PushPalettes()
        {
            m_Palettes.Value = PaletteStorage.Instance.Palettes
                .Select(p => new PaletteUIEntry
                {
                    Id = p.Id,
                    Name = p.Name,
                    BuildingCount = p.AllowedBuildings.Count,
                    Category = (int)p.Category,
                    Density = (int)p.Density,
                })
                .ToArray();
        }

        /// <summary>
        /// Re-applies the filters over the full index. The result stays in a
        /// managed cache; only the visible window is serialized to the UI.
        /// </summary>
        private void RebuildFilteredCache()
        {
            var palette = PaletteStorage.Instance.GetById(m_SelectedPaletteId);

            // A palette is locked to its area type (and density, for
            // residential). Force those filters so the browser only ever shows
            // buildings that belong to this palette's zone — the user can still
            // refine by lot/level/theme/search within that set.
            if (palette != null)
            {
                m_Filters.Category = palette.Category;
                m_Filters.Density = palette.Density;
            }

            m_FilteredCache.Clear();
            m_FilteredCache.AddRange(m_Filters.Apply(m_IndexSystem.Buildings, palette));

            m_BuildingCount.Value = m_FilteredCache.Count;
            m_WindowDirty = true;
        }

        /// <summary>
        /// Serializes ONLY the visible rows (plus 2 overscan rows) to the UI.
        /// With a 4x5 default grid this is at most 28 entries per push, instead of
        /// the entire filtered list (thousands of entries).
        /// </summary>
        private void PushWindow()
        {
            var palette = PaletteStorage.Instance.GetById(m_SelectedPaletteId);

            var totalRows = Math.Ceiling(m_FilteredCache.Count / (double)m_GridColumns);
            var maxScroll = Math.Max(0, totalRows - GridRows);

            m_ScrollRow = Math.Max(0, Math.Min(m_ScrollRow, maxScroll));
            m_ScrollIndex.Value = m_ScrollRow;
            m_MaxScrollIndex.Value = maxScroll;

            var startIndex = (int)(Math.Floor(m_ScrollRow) * m_GridColumns);
            var maxIndex = Math.Min(m_FilteredCache.Count, startIndex + (m_GridColumns * (GridRows + 2)));
            var window = new List<BuildingUIEntry>(Math.Max(0, maxIndex - startIndex));

            var iconGenerator = World.GetExistingSystemManaged<IconGeneratorSystem>();

            for (var i = startIndex; i < maxIndex; i++)
            {
                var b = m_FilteredCache[i];

                // On screen but still without an image: jump it to the front of
                // the generation queue so the user never stares at a placeholder
                // while thousands of unrelated prefabs render first.
                if (string.IsNullOrEmpty(b.Thumbnail))
                {
                    iconGenerator?.Prioritize(b.PrefabName);
                }

                window.Add(new BuildingUIEntry
                {
                    Name = b.PrefabName,
                    DisplayName = b.DisplayName,
                    Thumbnail = b.Thumbnail,
                    FallbackThumbnail = b.FallbackThumbnail,
                    Category = (int)b.Category,
                    Density = (int)b.Density,
                    Level = b.Level,
                    LotWidth = b.LotSize.x,
                    LotDepth = b.LotSize.y,
                    Theme = b.ThemeName,
                    ThemeIcon = b.ThemeIcon,
                    IsMod = b.IsMod,
                    Packs = b.Packs,
                    Selected = palette != null && palette.AllowedBuildings.Contains(b.PrefabName),
                });
            }

            m_Buildings.Value = window.ToArray();
        }
    }
}
