// <copyright file="PaletteStorage.cs">
// CustomZones - Licensed under the MIT license.
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
    /// Loads/saves the palette library to
    /// {UserData}/ModsData/CustomZones/palettes.json.
    /// Palettes are global (shared across savegames); districts only store
    /// a stable hash referencing a palette id.
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
                Name = string.IsNullOrWhiteSpace(name) ? $"Palette {_palettes.Count + 1}" : name.Trim(),
                Category = category,
                Density = density,
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
                AllowedBuildings = new HashSet<string>(source.AllowedBuildings),
            };

            _palettes.Add(copy);
            Touch();
            Save();
            return copy;
        }

        public void ToggleBuilding(string paletteId, string prefabName)
        {
            var palette = GetById(paletteId);
            if (palette == null)
            {
                return;
            }

            if (!palette.AllowedBuildings.Remove(prefabName))
            {
                palette.AllowedBuildings.Add(prefabName);
            }

            Touch();
            Save();
        }

        public void SetBuildings(string paletteId, IEnumerable<string> prefabNames, bool allowed)
        {
            var palette = GetById(paletteId);
            if (palette == null)
            {
                return;
            }

            foreach (var name in prefabNames)
            {
                if (allowed)
                {
                    palette.AllowedBuildings.Add(name);
                }
                else
                {
                    palette.AllowedBuildings.Remove(name);
                }
            }

            Touch();
            Save();
        }

        private void Touch() => Version++;

        public void Load()
        {
            _palettes.Clear();

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
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "Failed to load palettes.json");
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
