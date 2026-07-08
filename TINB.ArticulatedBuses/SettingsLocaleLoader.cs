using System;
using System.Collections.Generic;
using System.IO;
using Colossal;
using Colossal.Localization;
using Game.Modding;
using Game.SceneFlow;
using Newtonsoft.Json;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Load the mod-settings translations and register them with the game
    /// </summary>
    /// <remarks>
    /// Only official game locales are supported
    /// </remarks>
    public static class SettingsLocaleLoader
    {
        /// <summary>
        /// Load the locale JSON files from a folder
        /// </summary>
        /// <remarks>
        /// Malformed files are logged
        /// </remarks>
        /// <returns>The number of locale sources registered</returns>
        public static int LoadFromFolder(ModSetting setting, string langFolder)
        {
            if (setting == null || string.IsNullOrEmpty(langFolder) || !Directory.Exists(langFolder))
            {
                return 0;
            }

            LocalizationManager localizationManager = GameManager.instance.localizationManager;
            int registered = 0;

            foreach (string file in Directory.GetFiles(langFolder, "*.json"))
            {
                try
                {
                    string localeId = Path.GetFileNameWithoutExtension(file);
                    // Deserialize JSON
                    Dictionary<string, string>? raw = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file));
                    if (raw == null || raw.Count == 0)
                    {
                        continue;
                    }

                    // Create localised/translated settings dict
                    Dictionary<string, string> expanded = new Dictionary<string, string>(raw.Count);
                    foreach (KeyValuePair<string, string> entry in raw)
                    {
                        expanded[Expand(setting, entry.Key)] = entry.Value;
                    }

                    // Register
                    localizationManager.AddSource(localeId, new LocaleFileSource(expanded));
                    registered++;
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"Failed to load translation file {file}: {ex.Message}");
                }
            }

            return registered;
        }

        /// <summary>
        /// Expand a short translation key to the vanilla Options locale ID for this setting
        /// </summary>
        /// <returns>The full locale ID the game expects</returns>
        private static string Expand(ModSetting setting, string key)
        {
            if (key == "$title")
            {
                return setting.GetSettingsLocaleID();
            }

            if (key.StartsWith("$tab.", StringComparison.Ordinal))
            {
                return setting.GetOptionTabLocaleID(key.Substring("$tab.".Length));
            }

            if (key.StartsWith("$group.", StringComparison.Ordinal))
            {
                return setting.GetOptionGroupLocaleID(key.Substring("$group.".Length));
            }

            if (key.EndsWith(".desc", StringComparison.Ordinal))
            {
                return setting.GetOptionDescLocaleID(key.Substring(0, key.Length - ".desc".Length));
            }

            if (key.EndsWith(".warn", StringComparison.Ordinal))
            {
                return setting.GetOptionWarningLocaleID(key.Substring(0, key.Length - ".warn".Length));
            }

            return setting.GetOptionLabelLocaleID(key);
        }

        /// <summary>
        /// Generic IDictionarySource backed by a fixed key-value map
        /// </summary>
        /// <remarks>
        /// Handed to the game's localization manager as one locale's translations
        /// </remarks>
        private sealed class LocaleFileSource : IDictionarySource
        {
            private readonly IReadOnlyDictionary<string, string> m_Entries;

            /// <summary>
            /// Wrap a fixed key-value map as a locale source
            /// </summary>
            public LocaleFileSource(IReadOnlyDictionary<string, string> entries)
            {
                m_Entries = entries;
            }

            /// <summary>
            /// Return the source's translation entries
            /// </summary>
            /// <returns>The wrapped key-value map</returns>
            public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
            {
                return m_Entries;
            }

            /// <summary>
            /// Release the source. Nothing to do for an in-memory map
            /// </summary>
            public void Unload()
            {
            }
        }
    }
}
