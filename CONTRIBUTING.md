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

Join [Crowdin](https://crowdin.com/project/qftplus), choose your language, and open **Windows app** or **Quest app**. Keep wording short, product names unchanged, and placeholders such as `{0}` and `%1$s` intact. Make translation corrections in Crowdin.

Approved translations sync hourly to a public GitHub pull request. After review and merge, they ship in the next release. Missing text falls back to English.

Maintainers: [crowdin.yml](crowdin.yml) configures the [GitHub integration](https://support.crowdin.com/github-integration/) on `main`. Export only approved strings, skip untranslated strings, and keep partial files enabled. Mirror the language mappings in Crowdin; update the export list and validator when adding a language. Delete the translation branch after merging.

Run the [localization checks](.github/workflows/localization.yml) before merging source or translation changes.
