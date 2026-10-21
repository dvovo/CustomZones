// <copyright file="LocaleHelper.cs">
// CustomZones - Licensed under the MIT license.
// Embedded-resource locale loading pattern follows FindIt's LocaleHelper.
// </copyright>

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

                GameManager.instance.localizationManager.AddSource(localeId, new DictionarySource(dictionary));
            }

            // Fall back to en-US for locales without a translation.
            if (_locales.TryGetValue("en-US", out var enUs))
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
