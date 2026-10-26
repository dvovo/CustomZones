// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace CustomZones.Domain
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;

    /// <summary>
    /// A building "family": every level of one building style at one lot size, e.g.
    /// EU_ResidentialLow01_L1_3x2 ... EU_ResidentialLow01_L5_3x2 are the family
    /// "EU_ResidentialLow01|3x2". Each lot size of a style is a different design,
    /// so it is its own family (and card); its levels stay together.
    /// </summary>
    public class BuildingFamily
    {
        /// <summary>Stable key (see <see cref="BuildingFamilies.KeyOf"/>).</summary>
        public string Key;

        /// <summary>Display name without the level / lot-size suffix.</summary>
        public string DisplayName;

        public ZoneCategory Category;
        public DensityType Density;

        /// <summary>Every indexed prefab of the family.</summary>
        public readonly List<BuildingEntry> Members = new();

        /// <summary>The member whose picture represents the family (level 1, mid-sized lot).</summary>
        public BuildingEntry Representative;

        public int MinLevel;
        public int MaxLevel;

        /// <summary>Distinct lot widths of the level-1 members, ascending.</summary>
        public int[] LotWidths = Array.Empty<int>();

        /// <summary>Distinct lot depths of the level-1 members, ascending.</summary>
        public int[] LotDepths = Array.Empty<int>();

        /// <summary>Lowest and highest building of the family, in metres (0 = unknown).</summary>
        public float MinHeight;
        public float MaxHeight;

        /// <summary>Distinct level-1 lot sizes ("2x3"), ordered by width then depth.</summary>
        public string[] LotSizes = Array.Empty<string>();

        /// <summary>True when at least one member is level 1 (only those can grow from empty lots).</summary>
        public bool CanGrow;

        public string[] Packs = Array.Empty<string>();
        public string ThemeName = string.Empty;
        public string ThemeIcon = string.Empty;
        public bool IsMod;
        public bool IsBuiltin;

        /// <summary>Level-1 members.</summary>
        public IEnumerable<BuildingEntry> GrowableMembers => Members.Where(m => m.Level == 1);

        /// <summary>The member of the given level (null when the family has none).</summary>
        public BuildingEntry MemberAt(int level) => Members.FirstOrDefault(m => m.Level == level);

        public bool HasLevel(int level) => Members.Any(m => m.Level == level);
    }

    /// <summary>Naming rules that turn prefabs into families.</summary>
    public static class BuildingFamilies
    {
        // <family>_L<level>[_<W>x<D>]<rest>, e.g. "NA_CommercialLow01_L2_2x3".
        private static readonly Regex NamePattern =
            new(@"^(?<family>.+?)_L(?<level>[1-5])(?:_(?<lot>\d+x\d+))?(?<rest>.*)$", RegexOptions.Compiled);

        // " - L1 2x3" at the end of a display name.
        private static readonly Regex DisplaySuffix =
            new(@"\s*-\s*L\d+(\s+\d+x\d+)?\s*$", RegexOptions.Compiled);

        /// <summary>Family key of a building: its style plus its lot size ("EU_ResidentialLow01|3x2").</summary>
        public static string KeyOf(string prefabName, int lotWidth, int lotDepth) =>
            $"{StyleOf(prefabName)}|{lotWidth}x{lotDepth}";

        /// <summary>Style part of a family key (keys saved before lot sizes were split have no "|").</summary>
        public static string StyleOfKey(string familyKey)
        {
            var bar = familyKey?.IndexOf('|') ?? -1;
            return bar < 0 ? familyKey ?? string.Empty : familyKey.Substring(0, bar);
        }

        /// <summary>True for keys saved before lot sizes were split (a whole style).</summary>
        public static bool IsStyleOnlyKey(string familyKey) => familyKey != null && familyKey.IndexOf('|') < 0;

        /// <summary>
        /// What a zone stores: one family at one level ("EU_ResidentialLow01|3x2@3"),
        /// meaning "this building may be born at that level in this zone".
        /// </summary>
        public static string SelectionKey(string familyKey, int level) => $"{familyKey}@{level}";

        /// <summary>Splits a stored selection; entries saved without a level mean level 1.</summary>
        public static void ParseSelection(string selection, out string familyKey, out int level)
        {
            var at = selection?.LastIndexOf('@') ?? -1;
            if (at > 0 && int.TryParse(selection.Substring(at + 1), out level))
            {
                familyKey = selection.Substring(0, at);
                return;
            }

            familyKey = selection ?? string.Empty;
            level = 1;
        }

        public static bool IsSelected(PaletteDefinition zone, string familyKey, int level) =>
            zone != null && zone.Families.Contains(SelectionKey(familyKey, level));

        /// <summary>
        /// The selections that may be born: level 1 like in the game, or each
        /// design's lowest ticked level when the zone skips levels.
        /// </summary>
        public static IEnumerable<string> BornSelections(PaletteDefinition zone, Func<string, int, bool> exists)
        {
            if (zone.Growth == GrowthMode.LikeGame)
            {
                foreach (var selection in zone.Families)
                {
                    ParseSelection(selection, out var key, out var level);
                    if (level == 1 && exists(key, level))
                    {
                        yield return selection;
                    }
                }

                yield break;
            }

            var lowest = new Dictionary<string, int>();
            foreach (var selection in zone.Families)
            {
                ParseSelection(selection, out var key, out var level);
                if (!exists(key, level))
                {
                    continue;
                }

                if (!lowest.TryGetValue(key, out var known) || level < known)
                {
                    lowest[key] = level;
                }
            }

            foreach (var kv in lowest)
            {
                yield return SelectionKey(kv.Key, kv.Value);
            }
        }

        public static bool AnySelected(PaletteDefinition zone, string familyKey)
        {
            for (var level = 1; level <= 5; level++)
            {
                if (IsSelected(zone, familyKey, level))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Style of a prefab (all levels and lot sizes); the prefab name itself when it does not follow the pattern.</summary>
        public static string StyleOf(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                return string.Empty;
            }

            var match = NamePattern.Match(prefabName);
            return match.Success
                ? match.Groups["family"].Value + match.Groups["rest"].Value
                : prefabName;
        }

        /// <summary>Display name of a family, from one member's display name.</summary>
        public static string DisplayNameOf(string memberDisplayName) =>
            DisplaySuffix.Replace(memberDisplayName ?? string.Empty, string.Empty).Trim();

        /// <summary>Groups indexed buildings into families (sorted by category, density, name).</summary>
        public static List<BuildingFamily> Build(IEnumerable<BuildingEntry> buildings)
        {
            var byKey = new Dictionary<string, BuildingFamily>(StringComparer.Ordinal);

            foreach (var building in buildings)
            {
                var key = building.FamilyKey;
                if (!byKey.TryGetValue(key, out var family))
                {
                    family = new BuildingFamily
                    {
                        Key = key,
                        Category = building.Category,
                        Density = building.Density,
                    };
                    byKey[key] = family;
                }

                family.Members.Add(building);
            }

            foreach (var family in byKey.Values)
            {
                Finish(family);
            }

            return byKey.Values
                .OrderBy(f => f.Category)
                .ThenBy(f => f.Density)
                .ThenBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Representative.LotSize.x)
                .ThenBy(f => f.Representative.LotSize.y)
                .ToList();
        }

        private static void Finish(BuildingFamily family)
        {
            var members = family.Members;
            var growable = members.Where(m => m.Level == 1).ToList();
            var pool = growable.Count > 0 ? growable : members;

            // Picture: the lot closest to 3x3 looks most like the style.
            family.Representative = pool
                .OrderBy(m => Math.Abs((m.LotSize.x * m.LotSize.y) - 9))
                .ThenBy(m => m.LotSize.x)
                .First();

            family.DisplayName = BuildingFamilies.DisplayNameOf(family.Representative.DisplayName);
            if (string.IsNullOrEmpty(family.DisplayName))
            {
                family.DisplayName = family.Key;
            }

            family.MinLevel = members.Min(m => m.Level);
            family.MaxLevel = members.Max(m => m.Level);
            family.CanGrow = growable.Count > 0;

            family.LotWidths = pool.Select(m => m.LotSize.x).Distinct().OrderBy(w => w).ToArray();
            family.LotDepths = pool.Select(m => m.LotSize.y).Distinct().OrderBy(d => d).ToArray();
            family.MinHeight = members.Min(m => m.Height);
            family.MaxHeight = members.Max(m => m.Height);
            family.LotSizes = pool
                .Select(m => m.LotSize)
                .Distinct()
                .OrderBy(s => s.x)
                .ThenBy(s => s.y)
                .Select(s => $"{s.x}x{s.y}")
                .ToArray();

            var first = family.Representative;
            family.Packs = members.SelectMany(m => m.Packs).Distinct().ToArray();
            family.ThemeName = first.ThemeName;
            family.ThemeIcon = first.ThemeIcon;
            family.IsMod = members.Any(m => m.IsMod);
            family.IsBuiltin = members.All(m => m.IsBuiltin);
        }
    }

    /// <summary>
    /// Default colours for new custom zones. They stay away from the hues the game
    /// uses for its own zones (green residential, blue commercial, yellow
    /// industrial, purple office) so a custom zone is never mistaken for one.
    /// </summary>
    public static class ZoneColors
    {
        public static readonly string[] Choices =
        {
            "#e8505b", // coral
            "#f26ca7", // pink
            "#14b8a6", // teal
            "#ff7a1a", // orange
            "#d946ef", // magenta
            "#9f4a1c", // brick
            "#be185d", // raspberry
            "#06b6d4", // turquoise
            "#ffffff", // white
            "#6b7280", // grey
        };

        /// <summary>First choice not used by another zone yet (cycles when all are taken).</summary>
        public static string NextFree(IEnumerable<string> used)
        {
            var taken = new HashSet<string>(used.Where(c => !string.IsNullOrEmpty(c)), StringComparer.OrdinalIgnoreCase);
            foreach (var color in Choices)
            {
                if (!taken.Contains(color))
                {
                    return color;
                }
            }

            return Choices[taken.Count % Choices.Length];
        }
    }

    /// <summary>Filter state of the family browser in the zone editor.</summary>
    public class FamilyFilters
    {
        /// <summary>Forced from the selected zone (not chosen in the UI).</summary>
        public ZoneCategory Category = ZoneCategory.Any;

        /// <summary>Forced from the selected zone (residential only).</summary>
        public DensityType Density = DensityType.Any;

        /// <summary>0 = any; otherwise families with a level-1 lot of this width (6 = 6 or more).</summary>
        public int LotWidth;

        /// <summary>0 = any; otherwise families with a level-1 lot of this depth (6 = 6 or more).</summary>
        public int LotDepth;

        /// <summary>0 = any; otherwise a height band (see <see cref="HeightBands"/>).</summary>
        public int HeightBand;

        /// <summary>Height bands of the filter, in metres: [band - 1] = (from, to).</summary>
        public static readonly (float from, float to)[] HeightBands =
        {
            (0f, 10f), (10f, 20f), (20f, 40f), (40f, 80f), (80f, float.MaxValue),
        };

        /// <summary>0 = any; otherwise only families that have this level (shown at that level).</summary>
        public int Level;

        /// <summary>Empty = any; <see cref="BrowserFilters.PackNone"/>, <see cref="BrowserFilters.PackMod"/> or a pack name.</summary>
        public string Pack = string.Empty;

        public string Search = string.Empty;

        public bool SelectedOnly;

        public IEnumerable<BuildingFamily> Apply(IEnumerable<BuildingFamily> source, PaletteDefinition zone)
        {
            // Signature buildings are placed by the player, never grown by demand.
            var query = source.Where(f => f.Density != DensityType.Signature);

            if (Level > 0)
            {
                query = query.Where(f => f.HasLevel(Level));
            }

            if (Category != ZoneCategory.Any)
            {
                query = query.Where(f => f.Category == Category);
            }

            if (Density != DensityType.Any)
            {
                query = query.Where(f => f.Density == Density);
            }

            if (LotWidth > 0)
            {
                query = LotWidth >= 6
                    ? query.Where(f => f.LotWidths.Any(w => w >= 6))
                    : query.Where(f => f.LotWidths.Contains(LotWidth));
            }

            if (LotDepth > 0)
            {
                query = LotDepth >= 6
                    ? query.Where(f => f.LotDepths.Any(d => d >= 6))
                    : query.Where(f => f.LotDepths.Contains(LotDepth));
            }

            if (HeightBand > 0 && HeightBand <= HeightBands.Length)
            {
                var (from, to) = HeightBands[HeightBand - 1];

                // Any of its levels within the band (heights grow with the level).
                query = query.Where(f => f.MaxHeight >= from && f.MinHeight < to);
            }

            if (!string.IsNullOrEmpty(Pack))
            {
                if (Pack == BrowserFilters.PackNone)
                {
                    query = query.Where(f => f.IsBuiltin);
                }
                else if (Pack == BrowserFilters.PackMod)
                {
                    query = query.Where(f => f.IsMod);
                }
                else
                {
                    query = query.Where(f => f.Packs.Contains(Pack));
                }
            }

            if (!string.IsNullOrWhiteSpace(Search))
            {
                var term = Search.Trim();
                query = query.Where(f =>
                    f.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    f.Key.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (SelectedOnly && zone != null)
            {
                query = Level > 0
                    ? query.Where(f => BuildingFamilies.IsSelected(zone, f.Key, Level))
                    : query.Where(f => BuildingFamilies.AnySelected(zone, f.Key));
            }

            return query;
        }
    }
}
