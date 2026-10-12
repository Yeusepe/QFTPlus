# Contributing

Issues and pull requests are welcome. Please keep changes narrowly scoped and state
which Quest firmware, VRCFaceTracking version, connection type, GPU, and model were
tested.

Do not submit raw inward-camera captures, extracted Meta binaries/models, personal
calibration data, or checkpoints trained on people who did not consent to public
redistribution. Synthetic fixtures and summarized diagnostics are preferred.

Build the managed projects with `dotnet build QFTPlus.slnx -c Release`.
Maintainers with the private build tooling should also run `./Build.ps1 -Check`.
Verify Stop restores the stock eye model, disables custom output, stops the relay,
and clears ADB forwarding. Firmware support needs a hardware test and fingerprint.

The combined VRCFT bridge must remain the single owner of final face state. Gaze and
tongue additions must never overwrite stock jaw, lip, cheek, brow, or blink values.

## Translations

1. Join [QFT+ on Crowdin](https://crowdin.com/project/qftplus) and choose your language.
2. Open **Windows app**, read the string's context, and submit your suggestion.
3. A proofreader reviews and approves it. Approved strings sync to a public GitHub pull request, then ship in an app release after the pull request is reviewed and merged.

Use short, clear wording that fits buttons and settings. Keep product names such as QFT+, Quest Pro, SteamVR, Virtual Desktop, and VRCFaceTracking unchanged. Preserve placeholders such as `{0}`, `{1}`, and `{0:0}` exactly; their order may change. Preserve leading or trailing spaces when the context says the string is a sentence fragment. Search keywords can be translated into useful local search terms.

Make translation corrections in Crowdin so the next sync preserves them. Report unclear English text through a Crowdin string issue or a GitHub issue. English source changes belong in [Strings.resx](src/app/Localization/Strings.resx); retain keys when their meaning is unchanged and include a helpful comment.

### Automatic delivery

The native [Crowdin GitHub integration](https://support.crowdin.com/github-integration/) connects `Yeusepe/QFTPlus`, branch `main`, in **Source and translation files** mode. [crowdin.yml](crowdin.yml) versions the source path, translation paths, language codes, and pull request title. No private scripts or local ZIP imports are required.

- Source changes merged into `main` sync continuously to Crowdin.
- Crowdin's hourly **Sync Schedule** pushes approved translations to its `l10n_main` service branch and opens or updates **Update approved translations**. **Sync Now** runs it immediately.
- GitHub's [localization workflow](.github/workflows/localization.yml) checks resources and builds the Windows app. A maintainer reviews and merges the pull request. This is separate from approving wording in Crowdin.
- Merged `Strings.<culture>.resx` files are compiled into the app by .NET. The app chooses the Windows display language at startup and falls back to English for missing text.

### Integration settings for maintainers

In [Crowdin Integrations](https://crowdin.com/project/qftplus/integrations), use the repository and branch above, `crowdin.yml`, service branch prefix `l10n_`, and an hourly sync. Leave **Push Sources** and **Always import new translations from the repository** off: GitHub owns English text and Crowdin owns translations.

In **Settings > Export**, enable **Skip untranslated strings** and **Export only approved translations**. Leave **Skip untranslated files** off, because it would wait for 100% translated files. These are [Crowdin project export settings](https://support.crowdin.com/project-settings/export/), not credentials or GitHub Actions secrets.

In **Settings > Languages > Language Mapping**, mirror these overrides for `%two_letters_code%` from `crowdin.yml`. Other languages use their default two-letter code. [Crowdin documents these project-level mappings here](https://support.crowdin.com/project-settings/languages/#adding-custom-language-codes).

| Crowdin language | .NET culture |
| --- | --- |
| Chinese Simplified (`zh-CN`) | `zh-Hans` |
| Chinese Traditional (`zh-TW`) | `zh-Hant` |
| Brazilian Portuguese (`pt-BR`) | `pt-BR` |
| Norwegian (`no`) | `nb` |
| Serbian Cyrillic (`sr`) | `sr-Cyrl` |

English remains the source and is excluded from target file exports. When adding a language, check its .NET culture and update the validator's supported cultures if necessary. Do not commit authentication tokens to `crowdin.yml`.

### Checks

From the repository root, with Python 3 and the .NET SDK in `global.json`:

```powershell
python .github/scripts/check-localization.py
python -m unittest discover -s .github/scripts -p "test_*.py"
dotnet build src/app/QFTPlus.csproj -c Release
```

The resource check rejects non-string resources, duplicate or unknown keys, invalid cultures, and changed format placeholders before .NET compiles community files. Missing entries are allowed for partial translations.
