![SillyFace](https://github.com/user-attachments/assets/11596c0a-00c8-4848-9243-031f420c1e69)
![Multimodal](https://github.com/user-attachments/assets/584e7bec-1ee3-40d1-97e4-6c778780f77e)

# QFT+

QFT+ adds tongue tracking, more face expressions, pupil size tracking and separate eye tracking to Quest Pro.
It includes PC and headset apps, calibration and avatar testing.

**[Download QFT+](https://github.com/Yeusepe/QFTPlus/releases)** · Release candidate

## Setup

PC mode needs Windows 10/11 x64, SteamVR and VRCFaceTracking (VRCFT).
Use Virtual Desktop or Steam Link with a **[supported rooted Quest Pro](https://github.com/Lumince/singularity)**.

1. Install QFT+. Enable Developer Mode on the headset. Connect USB. Allow USB debugging. In Magisk → Superuser, allow Shell.
2. Open **Setup → Set up** in QFT+.
3. Enable eye and face tracking on the headset. For Virtual Desktop, enable **Forward face/eye tracking to PC**. QFT+ sets up Steam Link sharing.
4. Connect through Virtual Desktop or Steam Link. Select **Start**.
5. Open **Calibration**. Use the Steam overlay to see the app in VR.

- After each headset restart, connect USB once to restore Wi-Fi access.
- Use a trusted network. Wi-Fi setup allows ADB access to the headset.
- Use QFT+'s VRCFT module. Disable the standalone SteamLink module to avoid a port 9015 conflict.
- Hybrid hand/controller tracking needs Virtual Desktop. It is experimental.
- Separate eye tracking was tested on firmware `51483620027600340`.

**Help:** If setup stops responding, cancel it. Start setup again. If VRCFT stops loading, restart it.

## Privacy

Camera processing and calibration run on your PC or headset. Recordings and models can contain face data.
Tracking output goes to your selected apps or OSC address.

## Sources

These credits cover shipped features. Each row links a source to its use in QFT+.

| Source | Use in QFT+ |
| --- | --- |
| [Qpro Enhanced FT, n0tmast3r](https://github.com/n0tmast3r/Qpro-Enhanced-FT/tree/d50e7ef42b51e2d47d00953864dc5bcbaf3b3708) | Base for [tracking](src/tracking), [headset code](src/headset) and the [bridge](src/bridges/vrcft). |
| [Singularity, Lumince](https://github.com/Lumince/singularity); [Magisk, topjohnwu](https://github.com/topjohnwu/Magisk) | Root tools used by [setup](src/app/SetupService.cs), [ADB](src/app/Adb.cs) and [tracking](src/tracking/headset.py). Singularity credits [fuguquest, Henry1887](https://github.com/Henry1887/fuguquest). |
| [Frida injection example, Ole André Vadla Ravnås](https://github.com/oleavr/android-inject-custom/tree/ee64bd404cdbb62ae0e8fa382726f9a0c722a27b) | Reference for [injection](src/headset/capture_attach.c) and [camera](src/headset/camera_capture.c)/[stream](src/headset/streamer.c) code. Frida also runs the [hybrid hooks](src/tracking/hybrid). |
| [scrcpy, Genymobile](https://github.com/Genymobile/scrcpy/blob/19871982cefb9de4c981c2314dfda2ac81564b48/server/src/main/java/com/genymobile/scrcpy/CleanUp.java) | Design reference for [cleanup after disconnect](src/app/HeadsetCleanup.cs). |
| [VirtualDesktop.VRCFaceTracking, Guy Godin](https://github.com/guygodin/VirtualDesktop.VRCFaceTracking/blob/1c0499252040ead367dade9b56da89a1d8ae4332/TrackingModule.cs) | Adapted [face mapping](src/headset/app/java/com/qftplus/headset/FaceExpressions.java) and [bridge code](src/bridges/vrcft/TrackingModule.cs). |
| [VRCFaceTracking, benaclejames and contributors](https://github.com/benaclejames/VRCFaceTracking/tree/6432e6a8d85fa7ec5115fc725c6abcb6dbdd4f35) | Adapted [Java formulas](src/headset/app/java/com/qftplus/headset/FaceExpressions.java), [OSC encoding](src/headset/app/java/com/qftplus/headset/OscOutput.java) and [parameters](assets/tracking-parameters.json). |
| [VRCFT-SteamLink, danwillm](https://github.com/danwillm/VRCFT-SteamLink); [LinkFT, ykeara](https://github.com/ykeara/LinkFT) | References for [Steam Link support](src/bridges/vrcft/SteamLinkState.cs) and [setup](src/app/SteamVr.cs). |
| [OpenCV](https://github.com/opencv/opencv/blob/4.13.0/modules/imgproc/src/contours.cpp) | Adapted [pupil outline detection](src/headset/pupil_contours.hpp). |
| [OpenVR, Valve](https://github.com/ValveSoftware/openvr) | [Driver header](src/steamvr/openvr_driver.h) and [API](src/steamvr/driver.cpp). [Input profiles](src/steamvr/qftplus/resources/input) adapt Valve's SteamVR Touch files. |
| [Meta Avatars SDK](https://developers.meta.com/vr/downloads/package/meta-avatars-sdk/) | Adapted [shader](src/headset/calibration_scene.cpp) and [SDK mapping](src/headset/calibration_avatar.hpp). Includes avatar runtime and preset avatars. |
| [1€ filter, Casiez, Roussel and Vogel](https://gery.casiez.net/1euro/) | Method for [Python](src/tracking/eye_signal_filter.py) and [Java](src/headset/app/java/com/qftplus/headset/Convergence.java) eye smoothing. |
| [Ava-256, Meta](https://github.com/facebookresearch/ava-256) | Training data for the mouth-camera encoder. |
| [GNM, Google](https://github.com/google/GNM) | Used to create the synthetic training dataset. |
| [Universal Facial Encoding, Bai et al.](https://arxiv.org/abs/2407.13038) | Design reference for [calibration with saved expressions](src/tracking/universal_face.py). |
| [Material Symbols, Google](https://github.com/google/material-design-icons) | Icons in [Studio](src/app/Symbols.cs) and the [headset app](src/headset/app/java/com/qftplus/headset/Symbols.kt). |
| [Quest Pro model, TurboSquid](https://www.turbosquid.com/es/3d-models/3d-meta-quest-pro-headset-and-controllers-2020830) | Purchased model used for [headset images](src/app/headset-render). Original mesh and textures are excluded. |

Sources for the Java code: [expression formulas](https://github.com/benaclejames/VRCFaceTracking/blob/6432e6a8d85fa7ec5115fc725c6abcb6dbdd4f35/VRCFaceTracking.Core/Params/Expressions/UnifiedSimpleExpressions.cs), [expression parameters](https://github.com/benaclejames/VRCFaceTracking/blob/6432e6a8d85fa7ec5115fc725c6abcb6dbdd4f35/VRCFaceTracking.Core/Params/Expressions/UnifiedExpressionsParameters.cs) and [binary encoding](https://github.com/benaclejames/VRCFaceTracking/blob/6432e6a8d85fa7ec5115fc725c6abcb6dbdd4f35/VRCFaceTracking.Core/OSC/DataTypes/BinaryBaseParameter.cs).

The headset app uses installed Meta [UI](src/headset/app/java/com/qftplus/headset/Ocui.kt), [setup scenes](src/headset/app/java/com/qftplus/headset/SetupSpace.java), [tracking](src/headset/native_tracking.c) and [controller services](src/headset/thumbrest.c).
The hybrid hooks use your installed Virtual Desktop software.

## License

QFT+ uses [MIT](LICENSE). Other code and assets keep their own licenses. See [third-party notices](assets/THIRD_PARTY_NOTICES.txt).
The face models use **[CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/), for non-commercial use**.

The eye patcher reads the stock model from your headset. QFT+ does not include Meta firmware or stock tracking models.

## Build

Install the .NET SDK listed in [global.json](global.json). Build the Windows app:

```powershell
dotnet build src/app/QFTPlus.csproj -c Release
```

This does not build a full release. [Build.ps1](Build.ps1) needs tools outside the public repository. Use the downloads to install QFT+.

## A personal note

I build QFT+ for my own use, with help from LLMs. It has bugs and experimental features.
Use it with care and at your own risk. I provide no warranty and accept no responsibility for damage.

QFT+ is not affiliated with Meta, Virtual Desktop, VRCFaceTracking or VRChat Inc. VRChat is a trademark of VRChat Inc.
