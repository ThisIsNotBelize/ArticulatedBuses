using System;
using System.Collections.Generic;
using System.IO;
using Colossal.Localization;
using Game.Modding;
using Game.SceneFlow;
using Newtonsoft.Json;

namespace TINB.ArticulatedBuses
{
    /* Loads per-locale settings translations from a folder of JSON files and registers them with the game's
       localization manager. One file per locale, named by its locale id (en-US.json, de-DE.json, ...). Each file is
       a flat { "key": "value" } map using SHORT keys, which are expanded to the vanilla Options.* locale IDs through
       the ModSetting's own helpers, so the result is always correctly-named regardless of mod id.

       Dependency-free (uses only the game's bundled Newtonsoft.Json) and reusable across mods. Short-key conventions:
         "$title"          -> the settings page title
         "$tab.<Tab>"      -> a tab name
         "$group.<Group>"  -> a group name
         "<Option>"        -> an option's label
         "<Option>.desc"   -> an option's description
         "<Option>.warn"   -> an option's confirmation-dialog text (buttons with [SettingsUIConfirmation]) */
    public static class SettingsLocaleLoader
    {
        /* Registers every &lt;locale&gt;.json in langFolder. Returns the number of locale files loaded. Never throws:
           a malformed or unreadable file is logged and skipped so one bad translation can't break mod load. */
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
                    Dictionary<string, string>? raw = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file));
                    if (raw == null || raw.Count == 0)
                    {
                        continue;
                    }

                    Dictionary<string, string> expanded = new Dictionary<string, string>(raw.Count);
                    foreach (KeyValuePair<string, string> entry in raw)
                    {
                        expanded[Expand(setting, entry.Key)] = entry.Value;
                    }

                    localizationManager.AddSource(localeId, new LocaleFileSource(expanded));
                    registered++;
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"Failed to load locale file {file}: {ex.Message}");
                }
            }

            return registered;
        }

        /* Expands a short translation key to the vanilla Options.* locale ID for this setting */
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
    }
}
