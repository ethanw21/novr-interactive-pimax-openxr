# PC VR setup and performance

NOVR Interactive runs with the Windows game on Windows or through Proton on
Linux. Both are compatibility targets. A successful test on one platform does
not establish that the other works; report the host OS when reporting an issue.
Steam Frame and Quest-style controllers use the same core virtual HOTAS and
floating HUD widgets. See [controller bindings](CONTROLLER_BINDINGS.md).

## Starting a session

1. Install both NOVR plugin and patcher folders using the [README](../README.md).
2. Start SteamVR and connect the headset. Confirm tracking before starting the game.
3. Launch Nuclear Option normally, using its Direct3D 11 renderer. The current
   package needs no experimental rendering plugin or forced Direct3D 12 launch.
4. Open **View** on the controller, or **F9** on the keyboard, for NOVR settings.

On Linux/Proton, BepInEx also needs the `winhttp` native/builtin override described
in the README. The game and mod still run as Windows binaries inside Proton;
NOVR does not supply a native Linux game build.

For Steam Frame, **Streaming Status → host PC → Advanced → Stream VR without
game** can establish the VR session first. A flat desktop stream is not a VR
session. If the PC is only ready for 2D streaming, resolve that connection before
adjusting NOVR's HUD. The [historical setup notes](history/STEAM_FRAME.md) describe
older builds and retired controls, not the current installation procedure.

## Installation troubleshooting

**The BepInEx script says the game is a Windows executable:** use the Windows
x64 BepInEx **5** ZIP, even on Arch, KDE or another Linux desktop. Its files go
beside `NuclearOption.exe`; there is no installer executable to open with Proton.
Launch the game through Steam. Remove any `run_bepinex.sh` command from Steam's
launch options and keep the `winhttp` override from the README. Leave unrelated
launch options in place.

**The game starts but NOVR does not:** check these locations inside the game's
installation folder:

```text
NuclearOption.exe
winhttp.dll
doorstop_config.ini
BepInEx/core/BepInEx.dll
BepInEx/plugins/NOVR/NOVR.dll
BepInEx/patchers/NOVR/NOVR.Patcher.dll
```

Copy each archive's contents, without an extra enclosing folder named after the
ZIP. BepInEx goes in the game folder; NOVR goes inside `BepInEx`. Preserve your
existing `BepInEx/config` folder and other mods when correcting an installation.

After launching, `BepInEx/LogOutput.log` should contain `Loading [NOVR Interactive`.
If no fresh log appears, check the BepInEx files and Proton launch option first.
If BepInEx loads but NOVR does not, check both NOVR folders above and the log for
errors. BepInEx's official [installation guide](https://docs.bepinex.dev/articles/user_guide/installation/index.html)
and [Proton/Wine guide](https://docs.bepinex.dev/articles/advanced/proton_wine.html)
explain the loader setup.

**The headset shows only grey on Pimax OpenXR:** grey is the compositor's own
backdrop, shown when it has no usable game image. Two causes are handled, and
the game's `Player.log` (Windows:
`%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption`) tells them apart.

*The runtime refuses the eye textures.* The one log received from a Pimax
headset so far repeats this every frame, and no image ever reaches the headset:

```text
[XR] … xrCreateSwapchain: XR_ERROR_FEATURE_UNSUPPORTED
[XR] … Failed to create swapchain, exiting session.
```

The plugin's request is an ordinary one, and why Pimax's runtime refuses it is
not known yet. On a runtime whose name contains "Pimax", NOVR therefore watches
that request. A refused request is logged in full, retried without its optional
parts (extension data, creation flags, the sampling usage that only the desktop
mirror needs) and, if still refused, examined once with throwaway requests so
the log names the sizes and formats the runtime does accept:

```text
[NOVR] Eye texture requests: watching on Pimax OpenXR.
[NOVR] Eye texture request refused, XR_ERROR_FEATURE_UNSUPPORTED: 4312x5104 format 29, usage 0x21, …
[NOVR] Eye texture retry, …: XR_SUCCESS.
[NOVR] Eye texture probe, 1024x1024 format 29: …
```

NOVR never changes a size, a format or a layer count behind the plugin's back.
After five seconds in VR without eye textures it also writes the OpenXR plugin's
own report, which lists the runtime, installed API layers, extensions, formats
and view sizes. Share `Player.log` with those lines. `Eye Texture Retry` under
`[Compatibility]` in `BepInEx/config/deltawing.novr.cfg` accepts `Automatic`,
`Always` (every runtime) and `Off`; it applies the next time VR starts.

*The image arrives but is treated as transparent.* The bundled OpenXR plugin
submits the game image as an alpha-blended layer, but the game never authors
that alpha channel. SteamVR ignores it. A compositor that honors it can show its
backdrop instead of the game. NOVR requests an opaque layer on every runtime
and, when the runtime's name contains "Pimax", also sets the alpha of the eye
textures to one after each frame. The desktop view, headset resolution and game
cameras are unchanged. This only matters once eye textures exist:

```text
[NOVR] Headset image: runtime=…; mode=Automatic; opaque layer=requested; alpha rewrite=on.
[NOVR] Headset image alpha rewrite: 2 eye image(s) in 2 pass(es), …
```

`Headset Image Opacity` under `[Compatibility]` accepts `Automatic`, `Always`
(rewrite alpha on any runtime; try it if another runtime shows a grey or
washed-out view) and `Off` (the plugin's alpha-blended default). It applies
immediately.

Both paths have source checks only. Neither has been confirmed on Pimax
hardware.

## Tuning performance

The desktop mirror and headset render resolution are separate. The mirror copies
an existing eye image; it does not render another complete scene. A smaller
window can reduce copying and desktop presentation costs, but lowering it does
not reduce the headset's eye textures. Check SteamVR's **per-eye resolution**, not
just the game's resolution menu or a percentage without its resulting dimensions.

Use the game's graphics settings to change its windowed/fullscreen preference.
Nuclear Option reapplies that saved preference during startup, so Unity's
`-screen-fullscreen` launch option alone can be overridden after loading.

Start with a stable headset refresh rate and change one setting at a time:

| Refresh rate | Time available per frame |
| --- | --- |
| 90 Hz | 11.11 ms |
| 120 Hz | 8.33 ms |
| 144 Hz | 6.94 ms |

Use SteamVR frame timing to distinguish slow game rendering from streaming or
tracking problems. Compare the same mission, view, per-eye resolution and graphics
settings after loading has finished. Desktop FPS alone does not measure headset
smoothness. If GPU frame time exceeds the budget, reducing per-application eye
resolution is a direct tradeoff between detail and rendering cost. Keep
antialiasing enabled when evaluating clarity; it is not disabled for eye tracking.

Wireless interruption, encoding delay and tracking loss can cause shaking or
stutter even when game rendering fits the budget. Record these separately rather
than attributing every missed frame to the mod.

NOVR offers optional [PC foveated rendering](PC_FOVEATION.md) on supported native
Windows/NVIDIA/Direct3D 11 systems. Fixed and eye-tracked modes are experimental
and default to Off; Linux/Proton keeps normal rendering. Steam Frame's foveated
**streaming** remains a separate runtime feature.

## Useful bug reports

Include the mod/game versions, aircraft, OS, GPU/driver, headset/controllers,
streaming application, SteamVR version, graphics backend, actual per-eye
resolution and refresh rate. For Linux, include the Proton version. Note whether
the issue also happens in a stock aircraft with a fresh layout profile.

Keep logs free of personal information before sharing. For source checks and their
limits, see [verification](VERIFICATION.md). Automated checks and desktop captures
cannot establish headset comfort, controller reach or haptic quality.
