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
//     painter and one hidden zone per existing palette (ByLaw recipe:
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

        /// <summary>Prefab name of the single toolbar painter.</summary>
        public const string PainterName = ClonePrefix + "PalettePainter";

        private const string CategoryName = "CZ_PaletteCategory";
        private const string CategoryIcon = "coui://uil/Standard/ColorPalette.svg";

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

        /// <summary>
        /// The single visible toolbar entry. Selecting it paints the currently
        /// selected palette's (hidden) zone; PaletteUISystem keeps the painter's
        /// ZoneType pointed at it via <see cref="ApplyPaletteToPainter"/>.
        /// </summary>
        public ZonePrefab PainterZone { get; private set; }

        /// <summary>paletteId -> created (hidden) zone prefab.</summary>
        private readonly Dictionary<string, ZonePrefab> m_PaletteZones = new();

        /// <summary>Locale entries (display names) registered so far.</summary>
        private readonly Dictionary<string, string> m_LocaleEntries = new();

        private int m_StatusTick;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_IndexSystem = World.GetOrCreateSystemManaged<BuildingIndexSystem>();
        }

        // ------------------------------------------------------------------
        // Install (called from Mod.OnLoad — ByLaw timing)
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates the category, painter and one zone per existing palette while
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
                EnsurePainter();

                foreach (var palette in PaletteStorage.Instance.Palettes)
                {
                    EnsurePaletteZone(palette);
                }

                PushLocaleEntries();
                m_SyncedStorageVersion = PaletteStorage.Instance.Version;
                m_Installed = true;
                Mod.Log.Info($"InstallAtLoad complete: category + painter + {m_PaletteZones.Count} palette zone(s) created in OnLoad (ByLaw timing).");
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

            LogStatus();
        }

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
                m_LocaleEntries[$"Assets.NAME[{CategoryName}]"] = "Palettes";
                Mod.Log.Info("Created palette zone category in the zoning toolbar.");
            }
            else
            {
                Mod.Log.Error($"AddPrefab failed for palette category '{CategoryName}'.");
            }
        }

        // ------------------------------------------------------------------
        // Single toolbar painter (visible zone; its ZoneType is redirected)
        // ------------------------------------------------------------------
        private void EnsurePainter()
        {
            if (PainterZone != null || m_Category == null)
            {
                return;
            }

            var painter = BuildZonePrefab(
                PainterName,
                ZoneCategory.Residential,
                DensityType.Any,
                visible: true,
                uiPriority: 0);

            if (painter == null)
            {
                Mod.Log.Error("Could not create the palette painter zone.");
                return;
            }

            PainterZone = painter;
            m_LocaleEntries[$"Assets.NAME[{painter.name}]"] = "Palette Painter";
            Mod.Log.Info("Created palette painter zone in the toolbar category.");
        }

        // ------------------------------------------------------------------
        // One (hidden) paintable zone per palette
        // ------------------------------------------------------------------
        private void EnsurePaletteZone(PaletteDefinition palette)
        {
            if (!m_PaletteZones.TryGetValue(palette.Id, out var zone))
            {
                zone = BuildZonePrefab(
                    $"{ClonePrefix}Zone_{palette.Id}",
                    palette.Category,
                    palette.Density,
                    visible: false,
                    uiPriority: 0);

                if (zone == null)
                {
                    return;
                }

                m_PaletteZones[palette.Id] = zone;
            }

            // Display name follows renames.
            m_LocaleEntries[$"Assets.NAME[{zone.name}]"] = palette.Name;
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
                        EnsurePaletteZone(palette);
                        changed = true;
                    }
                    else
                    {
                        // keep the display name in sync with renames
                        m_LocaleEntries[$"Assets.NAME[{ClonePrefix}Zone_{palette.Id}]"] = palette.Name;
                    }
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
            int uiPriority)
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

            zone.m_Color = PaletteColor;
            zone.m_Edge = PaletteEdgeColor;

            // Strip the theme (so the zone is not restricted to EU/NA) and the
            // milestone unlock (so it is always available, no Locked component,
            // no unlock event needed). Removing from OUR component list does not
            // touch the shared base-zone instances.
            zone.Remove<ThemeObject>();
            zone.Remove<Unlockable>();

            // Fresh UIObject: hidden palette zones (group=null, inactive) index
            // in the background; the visible painter sits under our category.
            zone.Remove<UIObject>();
            var uiObject = ScriptableObject.CreateInstance<UIObject>();
            uiObject.name = zoneName;
            uiObject.active = visible;
            uiObject.m_Group = visible ? m_Category : null;
            uiObject.m_Priority = uiPriority;
            uiObject.m_Icon = CategoryIcon;
            uiObject.m_IsDebugObject = false;
            zone.AddComponentFrom(uiObject);

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

                foreach (var buildingName in palette.AllowedBuildings)
                {
                    if (m_IndexSystem.ByName.TryGetValue(buildingName, out var entry) &&
                        entry.PrefabEntity != Entity.Null)
                    {
                        buffer.Add(new Components.PaletteAllowedBuilding { m_Building = entry.PrefabEntity });
                        total++;
                    }
                    else
                    {
                        missing++;
                    }
                }
            }

            m_BufferStorageVersion = PaletteStorage.Instance.Version;
            m_BufferIndexVersion = m_IndexSystem.Version;
            Mod.Log.Info($"PopulateAllowedBuffers: {total} allowed building(s) wired into palette zones ({missing} unresolved, {orphaned} orphaned zone(s) cleared).");
        }

        // ------------------------------------------------------------------
        // Painter redirection (called every frame by PaletteUISystem)
        // ------------------------------------------------------------------
        public bool TryGetPaletteZone(string paletteId, out ZonePrefab zone)
            => m_PaletteZones.TryGetValue(paletteId ?? string.Empty, out zone) && zone != null;

        private string m_LastPainterLog = string.Empty;

        private void LogPainterOnce(string message)
        {
            if (m_LastPainterLog != message)
            {
                m_LastPainterLog = message;
                Mod.Log.Info(message);
            }
        }

        /// <summary>
        /// Points the (always-active) painter's ZoneData.m_ZoneType at the
        /// selected palette's zone, so painting the painter paints that palette
        /// without swapping the toolbar selection. When there is no valid,
        /// indexed palette the painter is pointed at Unzoned (index 0): the
        /// vanilla ZoneToolSystem.GetAllowApplyZone then refuses to apply on
        /// empty cells (they already read zone 0), so the painter can never
        /// create inert "dead" zones without a real palette behind it — a native
        /// paint guard, no Harmony patch required. (On already-zoned cells the
        /// Unzoned painter clears the zoning, i.e. acts as a zone eraser.)
        /// Returns true only when a real palette's zone was applied.
        /// </summary>
        public bool ApplyPaletteToPainter(string paletteId)
        {
            if (PainterZone == null)
            {
                LogPainterOnce("Painter: no painter prefab yet.");
                return false;
            }

            if (!m_PrefabSystem.TryGetEntity(PainterZone, out var painterEntity) ||
                !EntityManager.TryGetComponent<ZoneData>(painterEntity, out var painterData))
            {
                LogPainterOnce("Painter: painter entity not materialized yet.");
                return false;
            }

            // Resolve the selected palette's indexed zone type, or fall back to
            // Unzoned (default) when there is none.
            var target = default(Game.Zones.ZoneType);
            var valid = false;

            if (!string.IsNullOrEmpty(paletteId) &&
                TryGetPaletteZone(paletteId, out var paletteZone) &&
                m_PrefabSystem.TryGetEntity(paletteZone, out var paletteEntity) &&
                EntityManager.TryGetComponent<ZoneData>(paletteEntity, out var paletteData) &&
                paletteData.m_ZoneType.m_Index != 0)
            {
                target = paletteData.m_ZoneType;
                valid = true;
            }

            LogPainterOnce(valid
                ? $"Painter sync: painterIdx={painterData.m_ZoneType.m_Index} -> paletteIdx={target.m_Index} (palette '{paletteId}')."
                : $"Painter: no paintable palette (id='{paletteId}') -> painter set to Unzoned (paint disabled on empty land).");

            if (painterData.m_ZoneType.m_Index != target.m_Index)
            {
                painterData.m_ZoneType = target;
                EntityManager.SetComponentData(painterEntity, painterData);
            }

            return valid;
        }

        // ------------------------------------------------------------------
        // Diagnostics + locale
        // ------------------------------------------------------------------
        private void LogStatus()
        {
            m_StatusTick++;
            if (m_StatusTick % 300 != 0) // ~ every 5s at 60fps
            {
                return;
            }

            foreach (var kv in m_PaletteZones)
            {
                var zone = kv.Value;
                if (zone == null || !m_PrefabSystem.TryGetEntity(zone, out var entity))
                {
                    Mod.Log.Info($"STATUS {zone?.name}: no entity yet.");
                    continue;
                }

                var ourIdx = EntityManager.TryGetComponent<ZoneData>(entity, out var zd)
                    ? zd.m_ZoneType.m_Index : -1;
                var area = zd.m_AreaType;
                var hasMarker = EntityManager.HasComponent<Components.PaletteZoneMarker>(entity);
                var allowedCount = EntityManager.TryGetBuffer<Components.PaletteAllowedBuilding>(entity, true, out var buf)
                    ? buf.Length : -1;
                var hasEstimates = EntityManager.HasBuffer<Game.Zones.ProcessEstimate>(entity);
                var hasProps = EntityManager.HasComponent<Game.Prefabs.ZonePropertiesData>(entity);

                Mod.Log.Info($"STATUS {zone.name}: ourZoneTypeIdx={ourIdx}, area={area}, hasMarker={hasMarker}, allowedBuildings={allowedCount}, hasEstimates={hasEstimates}, hasProps={hasProps}.");
            }
        }

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
