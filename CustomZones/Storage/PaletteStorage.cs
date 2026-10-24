// <copyright file="PaletteStorage.cs">
// Custom Zones - Licensed under the MIT license.
// Storage location pattern follows FindIt's custom data folder usage
// (Colossal.PSI.Environment.EnvPath.kUserDataPath + /ModsData/).
// </copyright>

namespace CustomZones.Storage
{
    using Colossal.Json;
    using Colossal.PSI.Environment;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using CustomZones.Domain;

    /// <summary>
    /// Loads/saves the custom zone library to
    /// {UserData}/ModsData/CustomZones/palettes.json. The library is global (the
    /// zones must exist before a savegame loads).
    /// </summary>
    public class PaletteStorage
    {
        private static PaletteStorage _instance;

        public static PaletteStorage Instance => _instance ??= new PaletteStorage();

        private readonly string _folder = Path.Combine(EnvPath.kUserDataPath, "ModsData", Mod.Id);

        private string FilePath => Path.Combine(_folder, "palettes.json");

        private readonly List<PaletteDefinition> _palettes = new();

        public IReadOnlyList<PaletteDefinition> Palettes => _palettes;

        /// <summary>Bumped on every mutation so the UI system can re-bind.</summary>
        public int Version { get; private set; }

        /// <summary>Bumped only when a zone colour changes (the zone system repaints it).</summary>
        public int ColorVersion { get; private set; }

        private PaletteStorage()
        {
            Load();
        }

        public PaletteDefinition GetById(string id) =>
            _palettes.FirstOrDefault(p => p.Id == id);

        public PaletteDefinition Create(string name, ZoneCategory category, DensityType density)
        {
            // Density only matters for Residential (split demand). Force Any
            // for the others so their browser shows every density.
            if (category != ZoneCategory.Residential)
            {
                density = DensityType.Any;
            }

            var palette = new PaletteDefinition
            {
                Name = string.IsNullOrWhiteSpace(name) ? DefaultName(category, density) : name.Trim(),
                Category = category,
                Density = density,
                Color = ZoneColors.NextFree(_palettes.Select(p => p.Color)),
            };

            _palettes.Add(palette);
            Touch();
            Save();
            return palette;
        }

        public void Rename(string id, string newName)
        {
            var palette = GetById(id);
            if (palette == null || string.IsNullOrWhiteSpace(newName))
            {
                return;
            }

            palette.Name = newName.Trim();
            Touch();
            Save();
        }

        public void SetColor(string id, string color)
        {
            var palette = GetById(id);
            if (palette == null || string.IsNullOrWhiteSpace(color) ||
                string.Equals(palette.Color, color, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            palette.Color = color.Trim().ToLowerInvariant();
            ColorVersion++;
            Touch();
            Save();
        }

        public void Delete(string id)
        {
            var palette = GetById(id);
            if (palette == null)
            {
                return;
            }

            _palettes.Remove(palette);
            Touch();
            Save();
        }

        public PaletteDefinition Duplicate(string id)
        {
            var source = GetById(id);
            if (source == null)
            {
                return null;
            }

            var copy = new PaletteDefinition
            {
                Name = source.Name + " (copy)",
                Category = source.Category,
                Density = source.Density,
                Families = new HashSet<string>(source.Families),
                LevelUp = source.LevelUp,
                KeepDesign = source.KeepDesign,
                Color = ZoneColors.NextFree(_palettes.Select(p => p.Color)),
            };

            _palettes.Add(copy);
            Touch();
            Save();
            return copy;
        }

        /// <summary>Adds or removes one building (family at a level) from a zone.</summary>
        public void ToggleSelection(string paletteId, string familyKey, int level)
        {
            var palette = GetById(paletteId);
            if (palette == null || string.IsNullOrEmpty(familyKey) || level < 1 || level > 5)
            {
                return;
            }

            var key = BuildingFamilies.SelectionKey(familyKey, level);
            if (!palette.Families.Remove(key))
            {
                palette.Families.Add(key);
            }

            Touch();
            Save();
        }

        public void SetLevelUp(string paletteId, bool levelUp)
        {
            var palette = GetById(paletteId);
            if (palette == null || palette.LevelUp == levelUp)
            {
                return;
            }

            palette.LevelUp = levelUp;
            Touch();
            Save();
        }

        public void SetKeepDesign(string paletteId, bool keepDesign)
        {
            var palette = GetById(paletteId);
            if (palette == null || palette.KeepDesign == keepDesign)
            {
                return;
            }

            palette.KeepDesign = keepDesign;
            Touch();
            Save();
        }

        /// <summary>Adds or removes many selections (see BuildingFamilies.SelectionKey) at once.</summary>
        public void SetSelections(string paletteId, IEnumerable<string> selectionKeys, bool allowed)
        {
            var palette = GetById(paletteId);
            if (palette == null)
            {
                return;
            }

            foreach (var key in selectionKeys)
            {
                if (allowed)
                {
                    palette.Families.Add(key);
                }
                else
                {
                    palette.Families.Remove(key);
                }
            }

            Touch();
            Save();
        }

        /// <summary>
        /// Replaces keys saved before lot sizes were split (a whole style) with the
        /// families of every lot size of that style. Returns true when anything changed.
        /// </summary>
        public bool ExpandStyleKeys(IReadOnlyDictionary<string, List<string>> familyKeysByStyle)
        {
            var changed = false;

            foreach (var palette in _palettes)
            {
                var styleKeys = palette.Families.Where(BuildingFamilies.IsStyleOnlyKey).ToList();
                foreach (var style in styleKeys)
                {
                    if (!familyKeysByStyle.TryGetValue(style, out var keys))
                    {
                        continue; // style not installed right now: keep it for later
                    }

                    palette.Families.Remove(style);
                    foreach (var key in keys)
                    {
                        palette.Families.Add(BuildingFamilies.SelectionKey(key, 1));
                    }

                    changed = true;
                }
            }

            if (changed)
            {
                Mod.Log.Info("Custom zones: building styles split into one entry per lot size.");
                Touch();
                Save();
            }

            return changed;
        }

        private void Touch() => Version++;

        private string DefaultName(ZoneCategory category, DensityType density)
        {
            var type = category switch
            {
                ZoneCategory.Residential when density != DensityType.Any => $"{density} residential",
                ZoneCategory.Commercial => "Commercial",
                ZoneCategory.Industrial => "Industrial",
                ZoneCategory.Office => "Office",
                _ => "Residential",
            };

            var number = 1;
            string name;
            do
            {
                name = $"{type} zone {number++}";
            }
            while (_palettes.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)));

            return name;
        }

        public void Load()
        {
            _palettes.Clear();
            var migrated = false;

            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var data = JSON.MakeInto<List<PaletteDefinition>>(JSON.Load(json));
                    if (data != null)
                    {
                        _palettes.AddRange(data);
                    }
                }

                foreach (var palette in _palettes)
                {
                    palette.Families ??= new HashSet<string>();
                    palette.AllowedBuildings ??= new HashSet<string>();

                    // v0.1 stored individual prefabs: keep their families instead.
                    if (palette.AllowedBuildings.Count > 0)
                    {
                        foreach (var prefabName in palette.AllowedBuildings)
                        {
                            // Style only: expanded to its lot sizes once the index exists.
                            palette.Families.Add(BuildingFamilies.StyleOf(prefabName));
                        }

                        palette.AllowedBuildings.Clear();
                        migrated = true;
                    }

                    // Entries saved before levels could be chosen mean level 1.
                    var withoutLevel = palette.Families
                        .Where(k => k.IndexOf('|') >= 0 && k.IndexOf('@') < 0)
                        .ToList();
                    foreach (var key in withoutLevel)
                    {
                        palette.Families.Remove(key);
                        palette.Families.Add(BuildingFamilies.SelectionKey(key, 1));
                        migrated = true;
                    }

                    if (string.IsNullOrEmpty(palette.Color))
                    {
                        palette.Color = ZoneColors.NextFree(_palettes.Select(p => p.Color));
                        migrated = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "Failed to load palettes.json");
            }

            if (migrated)
            {
                Mod.Log.Info("Custom zone library migrated to building families and zone colours.");
                Save();
            }

            Touch();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(_folder);
                File.WriteAllText(FilePath, JSON.Dump(_palettes));
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "Failed to save palettes.json");
            }
        }
    }
}
