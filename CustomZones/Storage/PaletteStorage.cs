// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Storage location pattern follows FindIt's custom data folder usage
// (Colossal.PSI.Environment.EnvPath.kUserDataPath + /ModsData/).

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

            // Newest first: the zone just made is the one the player works on next.
            _palettes.Insert(0, palette);
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

        /// <summary>Sets the game zone icon the zone's icon is drawn from (empty = its base zone's).</summary>
        public void SetIcon(string id, string icon)
        {
            var palette = GetById(id);
            icon = icon?.Trim() ?? string.Empty;
            if (palette == null || string.Equals(palette.Icon ?? string.Empty, icon, StringComparison.Ordinal))
            {
                return;
            }

            palette.Icon = icon;
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
                Growth = source.Growth,
                Icon = source.Icon,
                Color = ZoneColors.NextFree(_palettes.Select(p => p.Color)),
            };

            _palettes.Insert(0, copy);
            Touch();
            Save();
            return copy;
        }

        /// <summary>Folder of exported zones, to share them: {UserData}/ModsData/CustomZones/Exports.</summary>
        public string ExportFolder => Path.Combine(_folder, "Exports");

        /// <summary>
        /// Writes a zone (or every zone, with a null id) to a file in
        /// <see cref="ExportFolder"/> and returns the file name, or null on failure.
        /// </summary>
        public string Export(string id)
        {
            var zones = id == null ? _palettes.ToList() : _palettes.Where(p => p.Id == id).ToList();
            if (zones.Count == 0)
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(ExportFolder);
                var stem = SafeFileName(id == null ? "All zones" : zones[0].Name);
                var fileName = stem + ".json";
                for (var n = 2; File.Exists(Path.Combine(ExportFolder, fileName)); n++)
                {
                    fileName = $"{stem} ({n}).json";
                }

                File.WriteAllText(Path.Combine(ExportFolder, fileName), JSON.Dump(zones.Select(ExportCopy).ToList()));
                Mod.Log.Info($"Exported {zones.Count} zone(s) to '{fileName}'.");
                return fileName;
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "Could not export zones.");
                return null;
            }
        }

        /// <summary>Exported zone files, newest first (file names).</summary>
        public List<string> ExportFiles()
        {
            try
            {
                return Directory.Exists(ExportFolder)
                    ? new DirectoryInfo(ExportFolder).GetFiles("*.json")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .Select(f => f.Name)
                        .ToList()
                    : new List<string>();
            }
            catch (Exception ex)
            {
                Mod.Log.Warn(ex, "Could not list the exported zones.");
                return new List<string>();
            }
        }

        /// <summary>
        /// Adds the zones of an exported file as new zones (first in the list) and
        /// returns the first one, or null when the file cannot be read. Buildings of
        /// assets that are not installed stay in the zone and simply never grow.
        /// </summary>
        public PaletteDefinition Import(string fileName)
        {
            try
            {
                var path = Path.Combine(ExportFolder, Path.GetFileName(fileName ?? string.Empty));
                if (!File.Exists(path))
                {
                    return null;
                }

                var data = JSON.MakeInto<List<PaletteDefinition>>(JSON.Load(File.ReadAllText(path)));
                if (data == null || data.Count == 0)
                {
                    return null;
                }

                PaletteDefinition first = null;
                for (var i = data.Count - 1; i >= 0; i--)
                {
                    var source = data[i];
                    if (source == null)
                    {
                        continue;
                    }

                    var name = string.IsNullOrWhiteSpace(source.Name) ? DefaultName(source.Category, source.Density) : source.Name.Trim();
                    if (_palettes.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        name += " (imported)";
                    }

                    var zone = ExportCopy(source);
                    zone.Name = name;
                    if (string.IsNullOrEmpty(zone.Color) ||
                        _palettes.Any(p => string.Equals(p.Color, zone.Color, StringComparison.OrdinalIgnoreCase)))
                    {
                        zone.Color = ZoneColors.NextFree(_palettes.Select(p => p.Color));
                    }

                    _palettes.Insert(0, zone);
                    first = zone;
                }

                Mod.Log.Info($"Imported {data.Count} zone(s) from '{Path.GetFileName(path)}'.");
                ColorVersion++;
                Touch();
                Save();
                return first;
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, $"Could not import '{fileName}'.");
                return null;
            }
        }

        /// <summary>A zone's settings under a new id (what is shared).</summary>
        private static PaletteDefinition ExportCopy(PaletteDefinition source) => new()
        {
            Name = source.Name,
            Category = source.Category,
            Density = source.Category == ZoneCategory.Residential ? source.Density : DensityType.Any,
            Families = new HashSet<string>(source.Families ?? new HashSet<string>()),
            LevelUp = source.LevelUp,
            KeepDesign = source.KeepDesign,
            Growth = source.Growth,
            Icon = source.Icon ?? string.Empty,
            Color = source.Color ?? string.Empty,
        };

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((name ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return clean.Length == 0 ? "Zone" : clean;
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

        public void SetGrowth(string paletteId, GrowthMode growth)
        {
            var palette = GetById(paletteId);
            if (palette == null || palette.Growth == growth)
            {
                return;
            }

            palette.Growth = growth;
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
                    palette.Icon ??= string.Empty;

                    // The old format stored individual prefabs: keep their families instead.
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
