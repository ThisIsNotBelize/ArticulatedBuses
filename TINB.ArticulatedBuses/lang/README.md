# Translations (`lang/`)

One JSON file per locale, named by its locale id: `en-US.json`, `de-DE.json`, `fr-FR.json`,
`es-ES.json`, `it-IT.json`.

Only locales the game natively supports are shipped (vanilla set: en-US, de-DE, es-ES, fr-FR, it-IT,
ja-JP, ko-KR, pl-PL, pt-BR, ru-RU, zh-HANS, zh-HANT). Dutch/Swedish/Finnish were dropped: nl/sv are not
vanilla locales (files would be inert without a locale-adding mod) and no trusted fi translation exists.
Unsupported locales simply fall back to English.

Each file is a flat `{ "key": "value" }` map. **Only translate the values — keep the keys identical.**
The keys are short, logical names; `SettingsLocaleLoader` expands them to the game's `Options.*` locale IDs
at load time, so they are always correct regardless of the mod id.

Key conventions:

| Key            | Meaning                       |
|----------------|-------------------------------|
| `$title`       | Settings page title           |
| `$tab.<Name>`  | A settings tab name           |
| `$group.<Name>`| A settings group name         |
| `<Option>`     | An option's label             |
| `<Option>.desc`| An option's description text   |
| `<Option>.warn`| Confirmation-dialog text (buttons with `[SettingsUIConfirmation]`) |

`en-US.json` is the authoritative source. To add a language, copy it, rename to the target locale id, and
translate the values. Files are loaded automatically — no code changes needed.

These files are deployed next to the mod DLL (`lang/`) and read by `SettingsLocaleLoader.LoadFromFolder`,
which hydrates the game's localization manager via `IDictionarySource`. No external mod dependency.
