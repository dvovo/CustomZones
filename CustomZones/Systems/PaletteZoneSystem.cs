// <copyright file="PaletteZoneSystem.cs">
// CustomZones - Licensed under the MIT license.
//
// THE core of the mod (v3 architecture, faithful to ZoningByLaw).
//
// Every palette becomes a REAL paintable zone in the game's zoning toolbar,
// under a new "Palettes" category tab. The vanilla ZoneSpawnSystem only
// grows building prefabs whose zone matches the cell's zone type, so once a
// palette zone has its own ZoneType index only that palette's buildings can
// grow there.
//
// KEY INSIGHT (from decompiling ZoneSystem + PrefabSystem, 2026-07-31):
//   ZoneSystem.InitializeZonePrefabs assigns a ZoneType index to any entity
//   with ZoneData + PrefabData + Created whose MANAGED prefab has
//   m_AreaType != None. PrefabSystem.AddPrefab creates that entity
//   synchronously (with Created) and PrefabSystem.OnUpdate itself drives the
//   PrefabUpdate phase where ZoneSystem runs. So the zone gets indexed on the
//   first ZoneSystem pass that sees it Created.
//
//   ZoningByLaw creates its zones in Mod.OnLoad — inside the initial prefab
//   window of the persistent world, where that first pass runs over fresh
//   Created prefabs and the index then persists forever. Creating them later
//   (OnGamePreload / mid-game as the old code did) missed that clean window
//   and the zone stayed area=None, index=0 forever.
//
//   Bonus: creating in OnLoad lets the vanilla UIObject.LateInitialize wire
//   the category/zone into the toolbar automatically (as ByLaw and Platter
//   rely on), which removes all the manual WireToolbar / unlock-event /
//   deserialize-repair machinery the old code needed.
//
// This file therefore:
//   - InstallAtLoad(): called from Mod.OnLoad, creates the category, the
//     one visible toolbar zone per existing palette (ByLaw recipe:
//     ScriptableObject.CreateInstance + copied base-zone components + explicit
//     m_AreaType + stripped ThemeObject/Unlockable + fresh UIObject).
//   - OnUpdate(): mid-game palette add/remove (ByLaw CreateNewByLaw style) and
//     the (Phase 1) per-building clone creation once a zone is indexed.
//
// PHASE 2 (planned) replaces the building clones with a ByLaw-style
// PaletteZoneSpawnSystem that filters ALL buildings by the palette whitelist
// at spawn time and disables the vanilla ZoneCheckSystem.
// </copyright>

namespace CustomZones.Systems
{
    using Colossal.Entities;
    using Game;
    using Game.Prefabs;
    using Game.SceneFlow;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Unity.Entities;
    using UnityEngine;
    using CustomZones.Domain;
    using CustomZones.Storage;

    public partial class PaletteZoneSystem : GameSystemBase
    {
        /// <summary>Name prefix for every prefab this mod creates.</summary>
        public const string ClonePrefix = "CZ_";

        private const string CategoryName = "CZ_PaletteCategory";
        private const string CategoryIcon = CustomZones.Utilities.ZoneIcons.CategoryIcon;

        /// <summary>Light-pink fill all palette zones use.</summary>
        private static readonly Color PaletteColor = new(1f, 0.72f, 0.85f, 1f);

        /// <summary>Slightly deeper pink edge.</summary>
        private static readonly Color PaletteEdgeColor = new(0.95f, 0.5f, 0.72f, 1f);

        private PrefabSystem m_PrefabSystem;
        private BuildingIndexSystem m_IndexSystem;
        private UIAssetCategoryPrefab m_Category;
        private bool m_Installed;
        private int m_SyncedStorageVersion = -1;
        private List<PrefabBase> m_AllPrefabs;

        /// <summary>paletteId -> created (hidden) zone prefab.</summary>
        private readonly Dictionary<string, ZonePrefab> m_PaletteZones = new();

        /// <summary>Locale entries (display names) registered so far.</summary>
        private readonly Dictionary<string, string> m_LocaleEntries = new();

        private EntityArchetype m_UnlockEventArchetype;
        private Entity m_RefreshEventEntity = Entity.Null;

        /// <summary>Frames left before the pending toolbar refresh is sent (-1 = none pending).</summary>
        private int m_ToolbarRefreshCountdown = -1;

        /// <summary>Zones created mid-game whose toolbar button still has to be checked.</summary>
        private readonly List<ZonePrefab> m_PendingToolbarZones = new();

        /// <summary>Palette ids whose zone button was removed from the toolbar (palette deleted).</summary>
        private readonly HashSet<string> m_HiddenZones = new();

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_IndexSystem = World.GetOrCreateSystemManaged<BuildingIndexSystem>();

            // Same archetype the game's own unlock systems use. The toolbar
            // (ToolbarUISystem) rebuilds all its asset bindings on the frame it
            // sees an Unlock event for a prefab with UIObjectData.
            m_UnlockEventArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<global::Game.Common.Event>(),
                ComponentType.ReadWrite<Unlock>());
        }

        // ------------------------------------------------------------------
        // Install (called from Mod.OnLoad — ByLaw timing)
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates the category and one zone per existing palette while
        /// ZoneSystem's first indexing pass still runs over freshly-Created
        /// prefab entities. Must be called from <c>Mod.OnLoad</c>; safe to call
        /// once (idempotent).
        /// </summary>
        public void InstallAtLoad()
        {
            if (m_Installed)
            {
                return;
            }

            try
            {
                EnsureCategory();

                foreach (var palette in PaletteStorage.Instance.Palettes)
                {
                    EnsurePaletteZone(palette);
                }

                PushLocaleEntries();
                m_SyncedStorageVersion = PaletteStorage.Instance.Version;
                m_Installed = true;
                Mod.Log.Info($"InstallAtLoad complete: category + {m_PaletteZones.Count} palette zone(s) created in OnLoad (ByLaw timing).");
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "PaletteZoneSystem.InstallAtLoad failed");
            }
        }

        protected override void OnUpdate()
        {
            // Backstop for mid-game palette create/delete. The primary real-time
            // path is PaletteUISystem calling SyncPalettes() the instant storage
            // changes (UIUpdate phase, exactly like ByLaw's CreateNewByLaw). Both
            // are idempotent. Cheap no-op when nothing changed.
            if (m_SyncedStorageVersion != PaletteStorage.Instance.Version)
            {
                SyncPalettes();
            }

            // ByLaw spawn path: keep each palette zone entity's PaletteAllowedBuilding
            // buffer in sync with its whitelist. PaletteZoneSpawnSystem reads this
            // buffer to grow ONLY the whitelisted buildings on that zone's cells.
            PopulateAllowedBuffers();

            ApplyColorChanges();

            UpdateToolbarRefresh();

        }

        // ------------------------------------------------------------------
        // Toolbar refresh for zones added/removed mid-game
        // ------------------------------------------------------------------

        /// <summary>
        /// Schedules a toolbar rebuild. It is sent a couple of frames later so the
        /// new zone's UIObject has been initialized (and added to our category)
        /// by the game first.
        /// </summary>
        private void RequestToolbarRefresh()
        {
            m_ToolbarRefreshCountdown = 2;
        }

        private void UpdateToolbarRefresh()
        {
            // The event entity only has to live for one frame (the toolbar reads it
            // in UIUpdate, after this phase); remove it on the next pass.
            if (m_RefreshEventEntity != Entity.Null)
            {
                if (EntityManager.Exists(m_RefreshEventEntity))
                {
                    EntityManager.DestroyEntity(m_RefreshEventEntity);
                }

                m_RefreshEventEntity = Entity.Null;
            }

            if (m_ToolbarRefreshCountdown < 0)
            {
                return;
            }

            if (m_ToolbarRefreshCountdown > 0)
            {
                m_ToolbarRefreshCountdown--;
                return;
            }

            m_ToolbarRefreshCountdown = -1;

            if (m_Category == null || !m_PrefabSystem.TryGetEntity(m_Category, out var categoryEntity))
            {
                Mod.Log.Warn("Toolbar refresh skipped: category entity not found.");
                return;
            }

            // Make sure every new zone is listed in the category. The game normally
            // does this itself (UIObject.LateInitialize); the log tells us whether
            // the fallback was needed.
            foreach (var zone in m_PendingToolbarZones)
            {
                if (!m_PrefabSystem.TryGetEntity(zone, out var zoneEntity))
                {
                    Mod.Log.Warn($"Toolbar refresh: zone '{zone.name}' has no entity yet.");
                    continue;
                }

                var listed = IsInCategory(categoryEntity, zoneEntity);
                if (!listed)
                {
                    m_Category.AddElement(EntityManager, zoneEntity);
                }

                Mod.Log.Info($"Toolbar refresh: zone '{zone.name}' listed by the game={listed}{(listed ? string.Empty : " (added by the mod)")}.");
            }

            m_PendingToolbarZones.Clear();

            // Command buffers cannot be created during PrefabUpdate, so the event is
            // created directly (this phase runs on the main thread).
            m_RefreshEventEntity = EntityManager.CreateEntity(m_UnlockEventArchetype);
            EntityManager.SetComponentData(m_RefreshEventEntity, new Unlock(categoryEntity));
            Mod.Log.Info("Toolbar refresh: unlock event sent for the Custom Zones category.");
        }

        private bool IsInCategory(Entity categoryEntity, Entity zoneEntity)
        {
            if (!EntityManager.HasBuffer<UIGroupElement>(categoryEntity))
            {
                return false;
            }

            var elements = EntityManager.GetBuffer<UIGroupElement>(categoryEntity, true);
            for (var i = 0; i < elements.Length; i++)
            {
                if (elements[i].m_Prefab == zoneEntity)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Removes a deleted palette's zone button from the toolbar category.</summary>
        private void HideZoneButton(string paletteId, ZonePrefab zone)
        {
            if (m_Category == null ||
                !m_PrefabSystem.TryGetEntity(m_Category, out var categoryEntity) ||
                !m_PrefabSystem.TryGetEntity(zone, out var zoneEntity) ||
                !EntityManager.HasBuffer<UIGroupElement>(categoryEntity))
            {
                return;
            }

            var elements = EntityManager.GetBuffer<UIGroupElement>(categoryEntity);
            for (var i = elements.Length - 1; i >= 0; i--)
            {
                if (elements[i].m_Prefab == zoneEntity)
                {
                    elements.RemoveAt(i);
                }
            }

            m_HiddenZones.Add(paletteId);
            Mod.Log.Info($"Toolbar: removed the button of deleted palette zone '{zone.name}'.");
        }

        /// <summary>Fill + edge colours of a zone from its "#rrggbb" colour (edge = a darker shade).</summary>
        private static void GetPaletteColors(PaletteDefinition palette, out Color fill, out Color edge)
        {
            if (!ColorUtility.TryParseHtmlString(palette.Color, out fill))
            {
                fill = PaletteColor;
            }

            fill.a = 1f;
            Color.RGBToHSV(fill, out var h, out var s, out var v);
            edge = Color.HSVToRGB(h, Mathf.Clamp01(s * 1.15f), v * 0.8f);
        }

        // ------------------------------------------------------------------
        // Live colour changes
        // ------------------------------------------------------------------
        private int m_AppliedColorVersion;

        /// <summary>palette id -> base zone it was built from (needed to rebuild its icon).</summary>
        private readonly Dictionary<string, ZonePrefab> m_BaseZones = new();

        /// <summary>palette id -> colour currently applied to its zone.</summary>
        private readonly Dictionary<string, string> m_AppliedColors = new();

        private System.Reflection.FieldInfo m_ZoneSystemUpdateColors;

        /// <summary>
        /// Repaints the zones whose colour changed in the editor: map overlay
        /// (ZoneSystem rebuilds its colour arrays when m_UpdateColors is set) and
        /// toolbar icon.
        /// </summary>
        private void ApplyColorChanges()
        {
            if (m_AppliedColorVersion == PaletteStorage.Instance.ColorVersion)
            {
                return;
            }

            m_AppliedColorVersion = PaletteStorage.Instance.ColorVersion;
            var changed = false;

            foreach (var kv in m_PaletteZones)
            {
                var palette = PaletteStorage.Instance.GetById(kv.Key);
                if (palette == null ||
                    (m_AppliedColors.TryGetValue(kv.Key, out var applied) &&
                     string.Equals(applied, palette.Color, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var zone = kv.Value;
                GetPaletteColors(palette, out var fill, out var edge);
                zone.m_Color = fill;
                zone.m_Edge = edge;

                var uiObject = zone.GetComponent<UIObject>();
                if (uiObject != null && m_BaseZones.TryGetValue(kv.Key, out var baseZone))
                {
                    var baseIcon = baseZone.GetComponent<UIObject>()?.m_Icon;
                    var icon = string.IsNullOrEmpty(baseIcon)
                        ? null
                        : CustomZones.Utilities.ZoneIcons.Create(zone.name, baseIcon, baseZone.m_Color, fill);
                    if (icon != null)
                    {
                        uiObject.m_Icon = icon;
                    }
                }

                m_AppliedColors[kv.Key] = palette.Color;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            var zoneSystem = World.GetExistingSystemManaged<global::Game.Prefabs.ZoneSystem>();
            m_ZoneSystemUpdateColors ??= typeof(global::Game.Prefabs.ZoneSystem).GetField(
                "m_UpdateColors", BindingFlags.NonPublic | BindingFlags.Instance);

            if (zoneSystem != null && m_ZoneSystemUpdateColors != null)
            {
                m_ZoneSystemUpdateColors.SetValue(zoneSystem, true);
            }
            else
            {
                Mod.Log.Warn("Zone colours changed but ZoneSystem.m_UpdateColors was not found: they apply after a reload.");
            }

            RequestToolbarRefresh();
            Mod.Log.Info("Applied changed zone colour(s).");
        }

        /// <summary>Toolbar icon of a custom zone (for the editor's zone list).</summary>
        public string GetZoneIcon(string paletteId) =>
            m_PaletteZones.TryGetValue(paletteId ?? string.Empty, out var zone)
                ? zone.GetComponent<UIObject>()?.m_Icon ?? string.Empty
                : string.Empty;

        // ------------------------------------------------------------------
        // Category tab in the zoning toolbar (ByLaw CreateAssetCategory)
        // ------------------------------------------------------------------
        private void EnsureCategory()
        {
            if (m_Category != null)
            {
                return;
            }

            var baseCategory = FindBaseCategory();
            if (baseCategory == null)
            {
                Mod.Log.Error("Could not find a vanilla 'Zones*' UI category; palette zones will not show in the toolbar.");
                return;
            }

            var baseComponents = new ComponentBase[baseCategory.components.Count];
            baseCategory.components.CopyTo(baseComponents);

            var category = ScriptableObject.CreateInstance<UIAssetCategoryPrefab>();
            category.name = CategoryName;
            category.m_Menu = baseCategory.m_Menu;
            category.active = true;
            category.isDirty = true;
            category.components.AddRange(baseComponents);

            var baseUiObj = baseCategory.GetComponent<UIObject>();
            category.Remove<UIObject>();

            var uiObject = ScriptableObject.CreateInstance<UIObject>();
            uiObject.name = CategoryName;
            uiObject.active = true;
            uiObject.m_Priority = 100; // after the vanilla zoning categories
            uiObject.m_Icon = CategoryIcon;
            uiObject.m_Group = baseUiObj != null ? baseUiObj.m_Group : null;
            uiObject.m_IsDebugObject = false;
            category.AddComponentFrom(uiObject);

            if (m_PrefabSystem.AddPrefab(category))
            {
                m_Category = category;
                m_LocaleEntries[$"Assets.NAME[{CategoryName}]"] = "Custom Zones";
                Mod.Log.Info("Created palette zone category in the zoning toolbar.");
            }
            else
            {
                Mod.Log.Error($"AddPrefab failed for palette category '{CategoryName}'.");
            }
        }

        // ------------------------------------------------------------------
        // One visible, paintable zone per palette
        // ------------------------------------------------------------------
        private ZonePrefab EnsurePaletteZone(PaletteDefinition palette)
        {
            if (!m_PaletteZones.TryGetValue(palette.Id, out var zone))
            {
                // Each palette is its own visible toolbar button with its own colour.
                GetPaletteColors(palette, out var fill, out var edge);
                zone = BuildZonePrefab(
                    $"{ClonePrefix}Zone_{palette.Id}",
                    palette.Category,
                    palette.Density,
                    visible: true,
                    uiPriority: 10 + m_PaletteZones.Count,
                    fill,
                    edge);

                if (zone == null)
                {
                    return null;
                }

                m_PaletteZones[palette.Id] = zone;
                m_AppliedColors[palette.Id] = palette.Color;

                var baseZone = FindBaseZone(palette.Category, palette.Density);
                if (baseZone != null)
                {
                    m_BaseZones[palette.Id] = baseZone;
                }
            }

            // Display name follows renames.
            m_LocaleEntries[$"Assets.NAME[{zone.name}]"] = palette.Name;
            return zone;
        }

        /// <summary>
        /// Real-time entry point (called by PaletteUISystem the moment a palette
        /// is created/edited): creates any newly added palettes' zones mid-game
        /// via AddPrefab — the same path ByLaw's CreateNewByLaw uses, which
        /// ZoneSystem indexes within a frame or two (no reload needed). Removal
        /// is handled by leaving the (now-unreferenced) zone in place — a prefab
        /// cannot be unregistered at runtime; painting simply stops targeting it.
        /// Idempotent.
        /// </summary>
        public void SyncPalettes()
        {
            if (!m_Installed)
            {
                // OnLoad hasn't run yet (e.g. called very early): install first.
                InstallAtLoad();
                return;
            }

            try
            {
                var changed = false;

                foreach (var palette in PaletteStorage.Instance.Palettes)
                {
                    if (!m_PaletteZones.ContainsKey(palette.Id))
                    {
                        var zone = EnsurePaletteZone(palette);
                        if (zone != null)
                        {
                            m_PendingToolbarZones.Add(zone);
                        }

                        changed = true;
                    }
                    else
                    {
                        // keep the display name in sync with renames
                        m_LocaleEntries[$"Assets.NAME[{ClonePrefix}Zone_{palette.Id}]"] = palette.Name;
                    }
                }

                // Deleted palettes: take their button out of the toolbar.
                var toolbarChanged = m_PendingToolbarZones.Count > 0;
                foreach (var kv in m_PaletteZones)
                {
                    if (!m_HiddenZones.Contains(kv.Key) && PaletteStorage.Instance.GetById(kv.Key) == null)
                    {
                        HideZoneButton(kv.Key, kv.Value);
                        toolbarChanged = true;
                    }
                }

                if (toolbarChanged)
                {
                    RequestToolbarRefresh();
                }

                if (changed)
                {
                    // Mirror ByLaw: reload the active locale so new prefab names show.
                    GameManager.instance.localizationManager.ReloadActiveLocale();
                }

                PushLocaleEntries();
                m_SyncedStorageVersion = PaletteStorage.Instance.Version;
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "PaletteZoneSystem.SyncNewPalettes failed");
            }
        }

        // ------------------------------------------------------------------
        // The ByLaw zone recipe (ScriptableObject.CreateInstance + copied base
        // components + explicit area type + stripped Theme/Unlockable + fresh UI)
        // ------------------------------------------------------------------
        private ZonePrefab BuildZonePrefab(
            string zoneName,
            ZoneCategory category,
            DensityType density,
            bool visible,
            int uiPriority,
            Color? fillColor = null,
            Color? edgeColor = null)
        {
            var baseZone = FindBaseZone(category, density);
            if (baseZone == null)
            {
                Mod.Log.Error($"No base zone found for '{zoneName}' ({category}/{density}).");
                return null;
            }

            var baseComponents = new ComponentBase[baseZone.components.Count];
            baseZone.components.CopyTo(baseComponents);

            var zone = ScriptableObject.CreateInstance<CustomZones.Prefab.PaletteZonePrefab>();
            zone.name = zoneName;
            zone.components.AddRange(baseComponents);
            zone.active = true;
            zone.isDirty = true;

            // CRITICAL: the managed prefab's m_AreaType is what ZoneSystem reads
            // to decide whether to assign a ZoneType index. Office is Industrial
            // + m_Office.
            switch (category)
            {
                case ZoneCategory.Commercial:
                    zone.m_AreaType = Game.Zones.AreaType.Commercial;
                    zone.m_Office = false;
                    break;
                case ZoneCategory.Industrial:
                    zone.m_AreaType = Game.Zones.AreaType.Industrial;
                    zone.m_Office = false;
                    break;
                case ZoneCategory.Office:
                    zone.m_AreaType = Game.Zones.AreaType.Industrial;
                    zone.m_Office = true;
                    break;
                default: // Residential / Mixed
                    zone.m_AreaType = Game.Zones.AreaType.Residential;
                    zone.m_Office = false;
                    break;
            }

            zone.m_Color = fillColor ?? PaletteColor;
            zone.m_Edge = edgeColor ?? PaletteEdgeColor;

            // Strip the theme (so the zone is not restricted to EU/NA) and the
            // milestone unlock (so it is always available, no Locked component,
            // no unlock event needed). Removing from OUR component list does not
            // touch the shared base-zone instances.
            zone.Remove<ThemeObject>();
            zone.Remove<Unlockable>();

            // Toolbar icon: the base zone's own game icon recoloured to this zone's
            // colour (falls back to the base icon, then to the tab icon).
            var baseIcon = baseZone.GetComponent<UIObject>()?.m_Icon;
            var icon = string.IsNullOrEmpty(baseIcon)
                ? CategoryIcon
                : CustomZones.Utilities.ZoneIcons.Create(zoneName, baseIcon, baseZone.m_Color, zone.m_Color) ?? baseIcon;

            // Fresh UIObject under our toolbar tab.
            zone.Remove<UIObject>();
            var uiObject = ScriptableObject.CreateInstance<UIObject>();
            uiObject.name = zoneName;
            uiObject.active = visible;
            uiObject.m_Group = visible ? m_Category : null;
            uiObject.m_Priority = uiPriority;
            uiObject.m_Icon = icon;
            uiObject.m_IsDebugObject = false;
            zone.AddComponentFrom(uiObject);

            m_LocaleEntries[$"Assets.DESCRIPTION[{zoneName}]"] =
                $"Custom zone based on {baseZone.name}. Only the buildings chosen for it in Custom Zones grow here.";

            if (!m_PrefabSystem.AddPrefab(zone))
            {
                Mod.Log.Error($"AddPrefab failed for zone '{zoneName}'.");
                return null;
            }

            Mod.Log.Info($"Created zone '{zoneName}' (areaType={zone.m_AreaType}, office={zone.m_Office}, base='{baseZone.name}', visible={visible}).");
            return zone;
        }

        // ------------------------------------------------------------------
        // Base-prefab resolution via reflection into PrefabSystem.m_Prefabs
        // (available at OnLoad, unlike the BuildingIndexSystem which only runs
        //  in PrefabUpdate). Same source ByLaw reads.
        // ------------------------------------------------------------------
        private List<PrefabBase> AllPrefabs()
        {
            if (m_AllPrefabs != null)
            {
                return m_AllPrefabs;
            }

            var field = typeof(PrefabSystem).GetField("m_Prefabs", BindingFlags.NonPublic | BindingFlags.Instance);
            m_AllPrefabs = field?.GetValue(m_PrefabSystem) as List<PrefabBase>;
            return m_AllPrefabs;
        }

        private UIAssetCategoryPrefab FindBaseCategory()
        {
            var prefabs = AllPrefabs();
            if (prefabs == null)
            {
                return null;
            }

            // Prefer ZonesOffice (last vanilla zoning category) but accept any
            // "Zones*" category — they all share the same Zones menu + group.
            return prefabs.OfType<UIAssetCategoryPrefab>()
                       .FirstOrDefault(c => c.name == "ZonesOffice")
                   ?? prefabs.OfType<UIAssetCategoryPrefab>()
                       .FirstOrDefault(c => c.name != null && c.name.StartsWith("Zones", StringComparison.Ordinal));
        }

        /// <summary>
        /// Finds a vanilla ZonePrefab whose area type (+ office + density for
        /// residential) matches, to copy well-formed components from. Falls back
        /// to any zone of the same area type.
        /// </summary>
        private ZonePrefab FindBaseZone(ZoneCategory category, DensityType density)
        {
            var prefabs = AllPrefabs();
            if (prefabs == null)
            {
                return null;
            }

            var wantOffice = category == ZoneCategory.Office;
            Game.Zones.AreaType wantArea = category switch
            {
                ZoneCategory.Commercial => Game.Zones.AreaType.Commercial,
                ZoneCategory.Industrial => Game.Zones.AreaType.Industrial,
                ZoneCategory.Office => Game.Zones.AreaType.Industrial,
                _ => Game.Zones.AreaType.Residential,
            };

            var candidates = prefabs.OfType<ZonePrefab>()
                .Where(z => z.m_AreaType == wantArea && z.m_Office == wantOffice)
                .Where(z => z.name != null && !z.name.StartsWith(ClonePrefix, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            // Residential density preference (drives the demand bucket via the
            // copied ZonePropertiesData). Others: density is a single pool.
            if (wantArea == Game.Zones.AreaType.Residential && density != DensityType.Any)
            {
                var keyword = density switch
                {
                    DensityType.Low => "Low",
                    DensityType.Row => "Row",
                    DensityType.Medium => "Medium",
                    DensityType.High => "High",
                    _ => null,
                };

                if (keyword != null)
                {
                    var match = candidates.FirstOrDefault(z =>
                        z.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (match != null)
                    {
                        return match;
                    }
                }
            }

            return candidates[0];
        }

        // ------------------------------------------------------------------
        // Whitelist -> PaletteAllowedBuilding buffer sync (consumed by the
        // ByLaw-style PaletteZoneSpawnSystem)
        // ------------------------------------------------------------------
        private int m_BufferStorageVersion = -1;
        private int m_BufferIndexVersion = -1;
        private int m_ExpandedIndexVersion = -1;

        /// <summary>
        /// Fills every palette zone entity's PaletteAllowedBuilding buffer with
        /// the prefab entities of its whitelisted buildings. Re-runs when the
        /// whitelist changes OR when the building index (which resolves names to
        /// entities) is (re)built. Cheap no-op otherwise.
        /// </summary>
        private void PopulateAllowedBuffers()
        {
            // Building name -> entity resolution needs the index populated.
            if (m_IndexSystem.Version == 0)
            {
                return;
            }

            // Zones saved before lot sizes were split hold whole styles: expand them
            // to one family per lot size now that the index knows them.
            if (m_ExpandedIndexVersion != m_IndexSystem.Version)
            {
                m_ExpandedIndexVersion = m_IndexSystem.Version;
                PaletteStorage.Instance.ExpandStyleKeys(m_IndexSystem.FamilyKeysByStyle);
            }

            if (m_BufferStorageVersion == PaletteStorage.Instance.Version &&
                m_BufferIndexVersion == m_IndexSystem.Version)
            {
                return;
            }

            var total = 0;
            var missing = 0;
            var orphaned = 0;

            // Iterate the zones we created (not the storage list): a zone whose
            // palette was deleted mid-game must have its whitelist CLEARED so it
            // stops growing buildings on cells still painted with it. A prefab
            // cannot be unregistered at runtime, but an empty buffer makes the
            // orphaned zone inert.
            foreach (var kv in m_PaletteZones)
            {
                if (!m_PrefabSystem.TryGetEntity(kv.Value, out var zoneEntity) ||
                    !EntityManager.HasBuffer<Components.PaletteAllowedBuilding>(zoneEntity))
                {
                    continue;
                }

                var buffer = EntityManager.GetBuffer<Components.PaletteAllowedBuilding>(zoneEntity);
                buffer.Clear();

                var palette = PaletteStorage.Instance.GetById(kv.Key);
                if (palette == null)
                {
                    orphaned++;
                    continue; // deleted palette -> leave the buffer empty
                }

                // Every chosen building (family at a level): those are the prefabs
                // that may be born on this zone's empty lots, at that level.
                foreach (var selection in palette.Families)
                {
                    BuildingFamilies.ParseSelection(selection, out var familyKey, out var level);
                    var entry = m_IndexSystem.FamiliesByKey.TryGetValue(familyKey, out var family)
                        ? family.MemberAt(level)
                        : null;

                    if (entry == null || entry.PrefabEntity == Entity.Null)
                    {
                        missing++;
                        continue;
                    }

                    buffer.Add(new Components.PaletteAllowedBuilding { m_Building = entry.PrefabEntity });
                    total++;
                }
            }

            m_BufferStorageVersion = PaletteStorage.Instance.Version;
            m_BufferIndexVersion = m_IndexSystem.Version;
            Mod.Log.Info($"PopulateAllowedBuffers: {total} building(s) wired into custom zones ({missing} unknown, {orphaned} deleted zone(s) cleared).");
        }

        // ------------------------------------------------------------------
        // Palette <-> zone lookups (used by PaletteUISystem)
        // ------------------------------------------------------------------
        public bool TryGetPaletteZone(string paletteId, out ZonePrefab zone)
            => m_PaletteZones.TryGetValue(paletteId ?? string.Empty, out zone) && zone != null;

        /// <summary>
        /// Finds the custom zone painted with the given zone type index (cells store
        /// that index). False for the game's own zones.
        /// </summary>
        public bool TryGetPaletteByZoneIndex(ushort zoneIndex, out PaletteDefinition palette)
        {
            palette = null;
            if (zoneIndex == 0)
            {
                return false;
            }

            foreach (var (index, zone) in GetPaletteZoneIndices())
            {
                if (index == zoneIndex)
                {
                    palette = zone;
                    return true;
                }
            }

            return false;
        }

        // Cache of GetPaletteZoneIndices: kept while the zones and their settings do not change.
        private List<(ushort index, PaletteDefinition palette)> m_ZoneIndexCache;
        private int m_ZoneIndexCacheVersion = -1;
        private int m_ZoneIndexCacheCount = -1;

        /// <summary>Zone type index of every custom zone that has one, with its definition (cached; do not modify).</summary>
        public List<(ushort index, PaletteDefinition palette)> GetPaletteZoneIndices()
        {
            if (m_ZoneIndexCache != null &&
                m_ZoneIndexCacheVersion == PaletteStorage.Instance.Version &&
                m_ZoneIndexCacheCount == m_PaletteZones.Count)
            {
                return m_ZoneIndexCache;
            }

            var result = new List<(ushort, PaletteDefinition)>(m_PaletteZones.Count);
            foreach (var kv in m_PaletteZones)
            {
                var palette = PaletteStorage.Instance.GetById(kv.Key);
                if (palette != null &&
                    m_PrefabSystem.TryGetEntity(kv.Value, out var entity) &&
                    EntityManager.TryGetComponent<ZoneData>(entity, out var zoneData) &&
                    zoneData.m_ZoneType.m_Index != 0)
                {
                    result.Add((zoneData.m_ZoneType.m_Index, palette));
                }
            }

            // Zones get their type index a few frames after creation: only cache complete lists.
            if (result.Count == m_PaletteZones.Count)
            {
                m_ZoneIndexCache = result;
                m_ZoneIndexCacheVersion = PaletteStorage.Instance.Version;
                m_ZoneIndexCacheCount = m_PaletteZones.Count;
            }

            return result;
        }

        /// <summary>Finds the palette a toolbar zone belongs to (empty when it is not one of ours).</summary>
        public bool TryGetPaletteIdForZone(PrefabBase prefab, out string paletteId)
        {
            foreach (var kv in m_PaletteZones)
            {
                if (kv.Value == prefab)
                {
                    paletteId = kv.Key;
                    return true;
                }
            }

            paletteId = string.Empty;
            return false;
        }

        // ------------------------------------------------------------------
        // Locale
        // ------------------------------------------------------------------
        private void PushLocaleEntries()
        {
            try
            {
                var lm = GameManager.instance.localizationManager;
                lm.AddSource(lm.activeLocaleId,
                    new Colossal.Localization.MemorySource(new Dictionary<string, string>(m_LocaleEntries)));
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Could not register locale entries: {ex.Message}");
            }
        }
    }
}
