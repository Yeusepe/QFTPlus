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
