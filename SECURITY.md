# Security and privacy

Please report security or privacy concerns through a private GitHub security
advisory rather than a public issue.

QFT+ processes inward-facing eye and mouth cameras and requires root on
the headset. Recordings and generated profiles can contain biometric data. They are
ignored by the supplied `.gitignore`, but users are responsible for checking commits
and archives before publishing.

Camera and preview services bind to localhost. Camera access uses an authenticated
ADB-forwarded connection over USB or Wi-Fi. Wireless ADB remains reachable on the
local network until disabled or the headset reboots; use a trusted network.
