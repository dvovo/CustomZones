// <copyright file="IconLibrary.cs">
// Custom Zones - Licensed under the MIT license.
// </copyright>

namespace CustomZones.Storage
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Colossal.Json;
    using Colossal.PSI.Environment;

    /// <summary>A zone icon designed in the icon designer and kept for later.</summary>
    public class SavedIcon
    {
        public string Id = Guid.NewGuid().ToString("N");

        public string Name = string.Empty;

        /// <summary>The designer's value ("gen:...|body=...").</summary>
        public string Value = string.Empty;

        /// <summary>Colour it was saved with ("#rrggbb"), for its preview.</summary>
        public string Color = string.Empty;
    }

    /// <summary>
    /// Designed zone icons kept apart from the zones, so an icon survives the
    /// zone it was made for: {UserData}/ModsData/CustomZones/icon-library.json.
    /// </summary>
    public class IconLibrary
    {
        private static IconLibrary s_Instance;

        private readonly string m_Folder = Path.Combine(EnvPath.kUserDataPath, "ModsData", Mod.Id);
        private readonly List<SavedIcon> m_Icons = new();

        private IconLibrary()
        {
            Load();
        }

        public static IconLibrary Instance => s_Instance ??= new IconLibrary();

        public IReadOnlyList<SavedIcon> Icons => m_Icons;

        /// <summary>Bumped on every change, so the UI can re-bind.</summary>
        public int Version { get; private set; }

        private string FilePath => Path.Combine(m_Folder, "icon-library.json");

        /// <summary>Keeps an icon (newest first). The same design is not kept twice.</summary>
        public SavedIcon Add(string name, string value, string color)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            var existing = m_Icons.FirstOrDefault(i => i.Value == value);
            if (existing != null)
            {
                return existing;
            }

            var icon = new SavedIcon
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"Icon {m_Icons.Count + 1}" : name.Trim(),
                Value = value,
                Color = color ?? string.Empty,
            };
            m_Icons.Insert(0, icon);
            Changed();
            return icon;
        }

        public void Remove(string id)
        {
            if (m_Icons.RemoveAll(i => i.Id == id) > 0)
            {
                Changed();
            }
        }

        /// <summary>Removes every saved icon.</summary>
        public void Clear()
        {
            if (m_Icons.Count > 0)
            {
                m_Icons.Clear();
                Changed();
            }
        }

        private void Changed()
        {
            Version++;
            Save();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var data = JSON.MakeInto<List<SavedIcon>>(JSON.Load(File.ReadAllText(FilePath)));
                    if (data != null)
                    {
                        m_Icons.AddRange(data.Where(i => i != null && !string.IsNullOrEmpty(i.Value)));
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "Failed to load icon-library.json");
            }

            Version++;
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(m_Folder);
                File.WriteAllText(FilePath, JSON.Dump(m_Icons));
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "Failed to save icon-library.json");
            }
        }
    }
}
