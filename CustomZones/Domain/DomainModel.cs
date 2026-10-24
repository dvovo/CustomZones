// <copyright file="DomainModel.cs">
// CustomZones - Licensed under the MIT license.
// Filtering model inspired by FindIt-CSII (Domain/Filters.cs, Domain/Enums).
// </copyright>

namespace CustomZones.Domain
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Unity.Entities;
    using Unity.Mathematics;

    /// <summary>
    /// High-level zone category of a growable building
    /// (derived from ZoneData.m_AreaType + ZonePrefab.m_Office).
    /// </summary>
    public enum ZoneCategory
    {
        Any = 0,
        Residential = 1,
        Mixed = 2,
        Commercial = 3,
        Industrial = 4,
        Office = 5,
    }

    /// <summary>
    /// Density classification, mirroring FindIt's ZoneTypeFilter.
    /// </summary>
    public enum DensityType
    {
        Any = 0,
        Low = 1,
        Row = 2,
        Medium = 3,
        High = 4,
        Signature = 5,
    }

    /// <summary>
    /// One indexed spawnable building prefab.
    /// Built once per game session by BuildingIndexSystem.
    /// </summary>
    public class BuildingEntry
    {
        /// <summary>Stable identifier: the prefab name.</summary>
        public string PrefabName;

        /// <summary>Family this prefab belongs to (see BuildingFamilies.KeyOf).</summary>
        public string FamilyKey = string.Empty;

        /// <summary>Localized display name resolved for the UI.</summary>
        public string DisplayName;

        /// <summary>Prefab entity (session-local, never persisted).</summary>
        public Entity PrefabEntity;

        public ZoneCategory Category;
        public DensityType Density;

        /// <summary>Building level (1-5).</summary>
        public int Level;

        /// <summary>Lot size in cells (width, depth) from BuildingData.m_LotSize.</summary>
        public int2 LotSize;

        /// <summary>
        /// Names of the asset packs (region packs UK/JP/... via AssetPackItem)
        /// this building belongs to. Empty = base game (no pack). Used by the
        /// "Pack" browser filter and shown as descriptor badges on the card.
        /// </summary>
        public string[] Packs = Array.Empty<string>();

        /// <summary>Theme name (European / North American / ...) if any, for the card badge tooltip.</summary>
        public string ThemeName = string.Empty;

        /// <summary>Theme flag icon URL (ThemePrefab UIObject) shown as a card badge.</summary>
        public string ThemeIcon = string.Empty;

        /// <summary>
        /// True if this building comes from a subscribed mod (Paradox Mods) —
        /// <see cref="Game.Prefabs.PrefabBase.isSubscribedMod"/>. Drives the "Mod"
        /// filter and the PDX badge on the card (mirrors FindIt).
        /// </summary>
        public bool IsMod;

        /// <summary>True if this building is a base-game asset (PrefabBase.isBuiltin).</summary>
        public bool IsBuiltin;

        /// <summary>Zone icon shown beneath the render + used if it fails to load.</summary>
        public string FallbackThumbnail;

        /// <summary>
        /// Thumbnail URL for the UI: the prefab's icon (which AssetIconLibrary
        /// rewrites for covered buildings), falling back to the zone icon via
        /// <see cref="FallbackThumbnail"/> when it fails to render.
        /// </summary>
        public string Thumbnail;
    }

    /// <summary>
    /// A saved zoning palette ("plantilla de zonificación"):
    /// a named whitelist of building prefab names.
    /// Serialized to JSON in ModsData and listed in the mod submenu.
    /// </summary>
    public class PaletteDefinition
    {
        /// <summary>Stable unique id (GUID string). Districts reference its hash.</summary>
        public string Id = Guid.NewGuid().ToString("N");

        public string Name = "New Palette";

        /// <summary>
        /// Zone area type this palette belongs to. A palette is exactly one
        /// area type so its zone draws the correct demand and gets a valid
        /// ZoneType index (ZoneSystem skips AreaType.None).
        /// </summary>
        public ZoneCategory Category = ZoneCategory.Residential;

        /// <summary>
        /// Density bucket. Only meaningful for Residential (demand is split
        /// Low/Medium/High). For Commercial/Industrial/Office the demand is a
        /// single pool, so density is left as Any and the browser shows every
        /// density of the category.
        /// </summary>
        public DensityType Density = DensityType.Low;

        /// <summary>
        /// The buildings that may be born in this zone: one entry per family and
        /// level ("EU_ResidentialLow01|3x2@3", see BuildingFamilies.SelectionKey).
        /// </summary>
        public HashSet<string> Families = new();

        /// <summary>Buildings in this zone may level up (false = they keep their level).</summary>
        public bool LevelUp = true;

        /// <summary>When levelling up, only become the next level of the same building (same design).</summary>
        public bool KeepDesign = true;

        /// <summary>Zone colour as "#rrggbb" (empty = pick a default).</summary>
        public string Color = string.Empty;

        /// <summary>Legacy (v0.1): individual prefab names. Migrated to <see cref="Families"/> on load.</summary>
        public HashSet<string> AllowedBuildings = new();
    }

    /// <summary>
    /// Current filter state of the building browser inside the palette editor.
    /// Matching logic mirrors FindIt's Filters.cs.
    /// </summary>
    public class BrowserFilters
    {
        public ZoneCategory Category = ZoneCategory.Any;
        public DensityType Density = DensityType.Any;

        /// <summary>0 = any. Otherwise exact lot width in cells (>=10 means "10+").</summary>
        public int LotWidth = 0;

        /// <summary>0 = any. Otherwise exact lot depth in cells (>=10 means "10+").</summary>
        public int LotDepth = 0;

        /// <summary>Building level filter, 0 = any.</summary>
        public int Level = 0;

        /// <summary>
        /// Asset-pack filter. Empty = any pack; <see cref="PackNone"/> = base
        /// game only (no pack); otherwise an exact pack name.
        /// </summary>
        public string Pack = string.Empty;

        /// <summary>Sentinel <see cref="Pack"/> value meaning "base game asset".</summary>
        public const string PackNone = "__CZ_NONE__";

        /// <summary>Sentinel <see cref="Pack"/> value meaning "from a subscribed mod (Paradox Mods)".</summary>
        public const string PackMod = "__CZ_MOD__";

        /// <summary>Free text search over display + prefab name.</summary>
        public string Search = string.Empty;

        /// <summary>Show only buildings already selected in the palette.</summary>
        public bool SelectedOnly = false;

        public IEnumerable<BuildingEntry> Apply(
            IEnumerable<BuildingEntry> source,
            PaletteDefinition currentPalette)
        {
            var query = source;

            if (Category != ZoneCategory.Any)
            {
                query = query.Where(b => b.Category == Category);
            }

            if (Density != DensityType.Any)
            {
                query = query.Where(b => b.Density == Density);
            }

            if (LotWidth > 0)
            {
                query = LotWidth >= 10
                    ? query.Where(b => b.LotSize.x >= 10)
                    : query.Where(b => b.LotSize.x == LotWidth);
            }

            if (LotDepth > 0)
            {
                query = LotDepth >= 10
                    ? query.Where(b => b.LotSize.y >= 10)
                    : query.Where(b => b.LotSize.y == LotDepth);
            }

            if (Level > 0)
            {
                query = query.Where(b => b.Level == Level);
            }

            if (!string.IsNullOrEmpty(Pack))
            {
                if (Pack == PackNone)
                {
                    query = query.Where(b => b.IsBuiltin);
                }
                else if (Pack == PackMod)
                {
                    query = query.Where(b => b.IsMod);
                }
                else
                {
                    query = query.Where(b => b.Packs.Contains(Pack));
                }
            }

            if (!string.IsNullOrWhiteSpace(Search))
            {
                var term = Search.Trim();
                query = query.Where(b =>
                    b.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    b.PrefabName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (SelectedOnly && currentPalette != null)
            {
                query = query.Where(b => currentPalette.AllowedBuildings.Contains(b.PrefabName));
            }

            return query;
        }
    }
}
