![SillyFace](https://github.com/user-attachments/assets/11596c0a-00c8-4848-9243-031f420c1e69)
![Multimodal](https://github.com/user-attachments/assets/584e7bec-1ee3-40d1-97e4-6c778780f77e)
# QFT+
Tongue, extra expressions, pupil dilation and independent eye tracking for Quest Pro, with per-expression adjustments and avatar testing.

**[Download QFT+](https://github.com/Yeusepe/QFTPlus/releases)** · Release candidate

Requires Windows 10/11 x64, SteamVR, VRCFaceTracking, Virtual Desktop or Steam Link, and a **rooted Quest Pro on v2.7 or lower**. [Check root compatibility first.](https://github.com/Lumince/singularity)

## Setup

1. Install QFT+. Enable Developer Mode, connect USB and allow debugging. In Magisk → Superuser, allow Shell.
2. Open **Setup → Set up**. QFT+ prepares the headset, Wi-Fi and VRCFaceTracking module.
3. Connect through Steam Link or Virtual Desktop, then press **Start**. Enable headset eye/face tracking; in Virtual Desktop, also enable **Forward face/eye tracking to PC**. QFT+ configures Steam Link sharing automatically.
4. Open **Calibration** to fit tracking to your face. Open it in the Steam overlay so you can see the window.

After a headset restart, reconnect USB once to restore Wi-Fi. Use only a trusted local network: Wi-Fi setup exposes headset ADB access. Use QFT+'s module in place of the standalone SteamLink module; both use port 9015.

Hybrid hands/controllers is experimental and requires Virtual Desktop. Independent eyes has been tested on firmware `51483620027600340`.

## Troubleshooting

- If installation gets stuck, including after allowing Shell in Magisk, try cancelling it and clicking **Start** again.
- If VRCFaceTracking (VRCFT) gets stuck loading, restart VRCFT.

## Privacy

No account or analytics. Camera frames, calibration recordings and personal models stay on your PC. Share them only if you intend to share your face data.

## Build

With the .NET SDK from `global.json`: `dotnet build src/app/QFTPlus.csproj -c Release`. The VRCFT bridge also requires VRCFaceTracking's SDK; headset components require the Android NDK; the SteamVR driver requires CMake and the Visual Studio C++ tools.

## Credits and license

Based on [Qpro Enhanced FT](https://github.com/n0tmast3r/Qpro-Enhanced-FT) by n0tmast3r, with Steam Link support based on [VRCFT-SteamLink](https://github.com/danwillm/VRCFT-SteamLink) and [LinkFT](https://github.com/ykeara/LinkFT). LLMs helped with models, hand tracking and memory checks.

MIT; see [third-party notices](assets/THIRD_PARTY_NOTICES.txt). No warranty. Not affiliated with Meta, Virtual Desktop, VRCFaceTracking or VRChat Inc. VRChat is a trademark of VRChat Inc.
