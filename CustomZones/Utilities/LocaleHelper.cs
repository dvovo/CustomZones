// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Embedded-resource locale loading pattern follows FindIt's LocaleHelper.

namespace CustomZones.Utilities
{
    using Colossal;
    using Colossal.Json;
    using Game.Prefabs;
    using Game.SceneFlow;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;

    /// <summary>Loads the mod's translations and resolves building names.</summary>
    public static class LocaleHelper
    {
        private static Dictionary<string, Dictionary<string, string>> _locales;

        /// <summary>
        /// Loads every embedded "CustomZones.Locale.*.json" resource and
        /// registers it as a localization source for the matching locale id.
        /// </summary>
        public static void LoadEmbeddedLocales()
        {
            _locales = new Dictionary<string, Dictionary<string, string>>();

            var assembly = Assembly.GetExecutingAssembly();

            foreach (var resourceName in assembly.GetManifestResourceNames()
                         .Where(n => n.Contains(".Locale.") && n.EndsWith(".json")))
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                using var reader = new StreamReader(stream);

                var localeId = Path.GetFileNameWithoutExtension(
                    resourceName.Substring(resourceName.IndexOf(".Locale.") + ".Locale.".Length));

                var dictionary = JSON.MakeInto<Dictionary<string, string>>(JSON.Load(reader.ReadToEnd()));
                _locales[localeId] = dictionary;
            }

            // Every translation is completed with English for any text it lacks.
            _locales.TryGetValue("en-US", out var enUs);
            foreach (var pair in _locales)
            {
                var dictionary = pair.Value;
                if (enUs != null && pair.Key != "en-US")
                {
                    foreach (var entry in enUs)
                    {
                        if (!dictionary.ContainsKey(entry.Key))
                        {
                            dictionary[entry.Key] = entry.Value;
                        }
                    }
                }

                GameManager.instance.localizationManager.AddSource(pair.Key, new DictionarySource(dictionary));
            }

            // Fall back to en-US for locales without a translation.
            if (enUs != null)
            {
                foreach (var localeId in GameManager.instance.localizationManager.GetSupportedLocales())
                {
                    if (!_locales.ContainsKey(localeId))
                    {
                        GameManager.instance.localizationManager.AddSource(localeId, new DictionarySource(enUs));
                    }
                }
            }
        }

        public static string Translate(string key, string fallback = null)
        {
            return GameManager.instance.localizationManager.activeDictionary
                .TryGetValue(key, out var value)
                ? value
                : fallback ?? key;
        }

        /// <summary>Localized asset name for a prefab, falling back to its raw name.</summary>
        public static string GetAssetName(PrefabBase prefab)
        {
            return Translate($"Assets.NAME[{prefab.name}]", prefab.name);
        }

        private class DictionarySource : IDictionarySource
        {
            private readonly Dictionary<string, string> _dictionary;

            public DictionarySource(Dictionary<string, string> dictionary)
            {
                _dictionary = dictionary;
            }

            public IEnumerable<KeyValuePair<string, string>> ReadEntries(
                IList<IDictionaryEntryError> errors,
                Dictionary<string, int> indexCounts)
            {
                return _dictionary;
            }

            public void Unload()
            {
            }
        }
    }
}
