# VR mode: the glasses as a SteamVR headset

VR mode turns the glasses into a seated, rotation-only SteamVR headset. SteamVR games draw to the glasses, and the
picture turns with your head using XrShift's own head tracking. Position is not tracked: leaning and walking do
nothing. A gamepad (or keyboard and mouse) does the rest.

Status: written and unit-tested on the C# side; the C++ driver has never been compiled or run on this machine (no
C++ compiler, no SteamVR). The release build on GitHub compiles it. The test plan at the end is the first real run.

## The parts

| Part | Where | Job |
|---|---|---|
| The driver | `src/XrShift.SteamVR/driver.cpp`, built to `steamvr/xrshift/bin/win64/driver_xrshift.dll` beside `XrShift.exe` | Loaded by SteamVR's `vrserver.exe`. Adds one headset ("XrShift glasses"), tells SteamVR where to draw, hands it the head pose. |
| The driver folder | `src/XrShift.SteamVR/xrshift/` (`driver.vrdrivermanifest`, `resources/settings/default.vrsettings`) | What SteamVR needs to find the driver. Copied next to the app by `XrShift.App.csproj`. |
| The wire | `src/XrShift.Scene/SteamVr.cs` (`VrWire`), mirrored by `struct WireBlock` in `driver.cpp` | One block of shared memory between the two. |
| The bridge | `src/XrShift.App/VrBridge.cs` | Writes the block; finds SteamVR; registers the driver; starts SteamVR. |
| VR mode | `src/XrShift.App/LiveApp.cs` (`SetVrMode`) | Ctrl+Alt+V, the Games page button and the home page's VR card. |
| The Games page | `src/XrShift.App/SettingsWindow.cs` (`BuildGames`), `SettingsPages.cs` (`ShowVrNeeds`) | Says what VR mode needs and whether each need is met. |
| The pose | `src/XrShift.Tracking/HeadTracker.cs` (`GetVrPose`) | The head's orientation and turn rate in SteamVR's axes. |

## How it works, end to end

1. **Start** (Ctrl+Alt+V, or Start VR). XrShift checks, in order: not in direct mode; glasses in Follow (in
   Stationary the glasses hold one picture still, so a game could not follow the head); a full-screen session on the
   glasses; head tracking connected; the driver present in this copy; SteamVR installed (any Steam library);
   SteamVR has run once (it then has `%LOCALAPPDATA%\openvr\openvrpaths.vrpath`); the driver registered (if not, XrShift
   registers it, with a backup); SteamVR not already running without the glasses. The first need not met is shown in
   a message box and logged, and nothing changes.
2. XrShift then goes to **plain screen** (game mode): the floating-screens scene stops drawing and its window hides,
   the extra virtual monitors are removed, the pointer is not guided or confined, and the glasses' links (camera,
   requests to the glasses) pause. The settings window hides so nothing of XrShift's holds the keyboard or the
   gamepad.
3. XrShift writes the glasses' display into the shared block with the command **Run**, starts publishing the head on
   every motion-sensor reading (about 1000 per second), then starts SteamVR (`steam://rungameid/250820`).
4. SteamVR loads the driver. The driver's `Init` reads the block. No block (XrShift not running), another version,
   or the command not Run: the driver adds nothing and SteamVR is free to use any other headset. Otherwise it adds
   one HMD, `XRSHIFT-GLASSES`.
5. SteamVR, in its **extended** display mode, opens its compositor window over the glasses' place on the Windows
   desktop and draws each eye into its half (3D) or into the whole screen (flat).
6. A driver thread hands SteamVR a pose every 2 ms; SteamVR predicts from it to the moment each frame is seen.
7. While VR is on, XrShift watches the focus: when one of SteamVR's own windows has held it for 1.5 s, it is given
   back to the game, because Steam passes the gamepad only to the program in front.
8. **Stop** (Ctrl+Alt+V again, Ctrl+Alt+G, the button, quitting XrShift, or the glasses changing size): XrShift writes
   the command **QuitSteamVr**; the driver's next `RunFrame` asks SteamVR to close (`VREvent_DriverRequestedQuit`),
   which frees the glasses' screen. XrShift leaves plain screen: the scene window comes back, is recentred, and the
   virtual monitors are put back as they were.

## The wire

Named shared memory `Local\XrShift.VrPose`, 128 bytes, little-endian, natural alignment. XrShift creates it
(`MemoryMappedFile.CreateOrOpen`) and writes it under a **sequence lock**: the sequence number is odd while a write
is in progress; the driver copies the block and keeps the copy only if the number was even and unchanged across the
copy. The driver writes only `driverTicks`, its own 8 bytes outside the lock.

| Offset | Type | Field | Meaning |
|---:|---|---|---|
| 0 | uint32 | sequence | odd while XrShift writes |
| 4 | uint32 | magic | `0x56535258`, the bytes "XRSV" |
| 8 | uint32 | version | 1; the driver refuses any other |
| 12 | uint32 | size | 128 |
| 16 | int64 | sampleTicks | `QueryPerformanceCounter` (= .NET `Stopwatch`) when the head was read |
| 24 | double[4] | rotation | w, x, y, z: head to world, in SteamVR's axes (below) |
| 56 | double[3] | angularVelocity | rad/s about the world's axes |
| 80 | int32 ×4 | windowX, windowY, windowWidth, windowHeight | the glasses on the Windows desktop, in pixels |
| 96 | int32 | sideBySide | 1: the glasses' 3D mode, left half left eye |
| 100 | float | refreshHz | the glasses' refresh rate |
| 104 | float | fovDeg | horizontal view angle of one eye's whole picture |
| 108 | float | ipdMeters | 0.063 in 3D, 0 flat |
| 112 | float | vsyncToPhotons | seconds; the glasses' own delay setting |
| 116 | int32 | command | 0 Run, 1 QuitSteamVr |
| 120 | int64 | driverTicks | written by the driver each time it reads: XrShift's sign that SteamVR has the glasses |

The layout is defined once in `VrWire`. `driver.cpp` pins every offset and the size with `static_assert`, and
`tests/XrShift.Scene.Tests/SteamVrTests.cs` checks that those asserts, the magic, the version, the commands and the
name match `VrWire`, and pins the bytes `VrWire.Write` produces. Change one side and a test fails; change the layout
and raise the version.

This replaced the first version's two channels: a 48-byte block with only the orientation, and a text file
`%LOCALAPPDATA%\XrShift\vr.txt` for the display, which the driver read from a fixed path (wrong whenever
`XRSHIFT_CONFIG` moves XrShift's settings) with `fscanf`.

## The pose

**Axes.** SteamVR's world is right-handed, +X right, +Y up, -Z forward; a rotation turns head axes into world axes.
`HeadPose.Orientation` is already exactly that (XrShift's render world is the same), so no conversion is needed:
the quaternion goes over as it is, written w first as OpenVR's `HmdQuaternion_t`. A head turned 30 degrees left is
w = cos 15°, y = sin 15° (a test pins it).

**No reading hold, no look-ahead of our own.** `HeadTracker.GetDisplayPose` is made for floating screens: it looks
ahead by the measured delay and holds the picture still while the head is nearly still, so text does not shake. In
VR the hold would feel like the world sticking to the head, and our look-ahead would be doubled by SteamVR's. VR uses
`HeadTracker.GetVrPose` instead: the orientation as it is now, plus the turn rate converted to the world. The driver
gives SteamVR that rate (`vecAngularVelocity`) and the reading's age (`poseTimeOffset` = minus the age), and SteamVR
predicts to the moment its frame reaches the eye, which only it knows. The rate is the smoothed one `GetDisplayPose`
predicts with, capped so it never runs faster than the fresh reading (no overshoot when the head stops), so prediction
does not amplify sensor noise.

**Neck model.** Rotation only, but eyes do not turn on the spot: the driver's reference point is a fixed neck pivot
at (0, 1.125, 0.0805) m, and the head (between the eyes) sits 0.075 m above and 0.0805 m in front of it
(`vecDriverFromHeadTranslation`, in the head's frame). Looking straight ahead the eyes are at (0, 1.2, 0): a seated
adult's eye height above the floor. SteamVR swings that offset round the pivot, also when predicting.
`shouldApplyHeadModel` is off since the driver applies its own.

**Staleness.** A reading older than 0.25 s, a broken quaternion, or the command not Run: the pose goes out as not
valid (`Running_OutOfRange`) with the last good direction, and SteamVR fades out rather than freezing on a stale
picture. `willDriftInYaw` is set: a 3DoF IMU drifts slowly in heading (Ctrl+Alt+0 recentres).

## The display

The driver uses SteamVR's **extended mode** (`IVRDisplayComponent`, `IsDisplayOnDesktop` true): SteamVR draws into
a window placed exactly over the glasses' monitor, `GetWindowBounds` = the glasses' rectangle on the desktop.

**Chosen mode: the glasses' 3D mode, 3840 x 1080 at 60 Hz.** In 3D mode Windows sees one 3840 x 1080 display and the
glasses show its left half to the left eye and its right half to the right eye, each a full 1920 x 1080. That is real
stereo depth at the glasses' full resolution per eye, which is what VR is for; 60 Hz is the price. Flat mode
(1920 x 1080 at 90 Hz) still works as a fallback: both eyes see the same picture, so the driver puts both eye
viewports on the whole screen and sets the eye separation to 0 (a separation would only shift the one picture
sideways); smoother, but no depth. The Games page says which one the glasses are in.

| | 3D (side by side) | Flat |
|---|---|---|
| Window | glasses' rectangle, 3840 x 1080 | glasses' rectangle, 1920 x 1080 |
| Left eye viewport | x 0, 1920 x 1080 | x 0, 1920 x 1080 |
| Right eye viewport | x 1920, 1920 x 1080 | x 0, 1920 x 1080 |
| Render target per eye | 1920 x 1080 (no lens distortion to make up for; SteamVR's resolution setting still scales it) | same |
| IPD | 0.063 m | 0 |
| Refresh | as Windows reports (60) | as Windows reports (90) |

**Projection.** Each eye looks straight ahead through the middle of its picture: a symmetric frustum with
tan(half angle) = tan(view angle / 2) horizontally and that times 1080/1920 vertically, OpenVR's convention of top
negative. The view angle is XrShift's own calibrated "View angle" setting (50.7° on the One Pro by default, about
29.8° vertically). The glasses' optics need no correction from the PC, so distortion is the identity.

**Latency.** `SecondsFromVsyncToPhotons` is XrShift's "Glasses delay" setting, clamped to 0..50 ms.

## Registering the driver

SteamVR finds extra drivers through `external_drivers` in `%LOCALAPPDATA%\openvr\openvrpaths.vrpath` (the file
`vrpathreg.exe adddriver` edits). XrShift edits it itself, through the pure, tested `SteamVrSetup.Register` /
`Unregister` (JSON text in, JSON text out): every other driver and setting stays; any other copy of XrShift's driver
(a folder ending `\steamvr\xrshift`) is replaced, since two drivers both called `xrshift` would fight; a file that is
not JSON or whose `external_drivers` is not a list of strings is refused and left alone; a copy is saved first as
`openvrpaths.vrpath.bak-before-xrshift` (later ones dated); the new file is written beside it and swapped in. If the
file does not exist SteamVR has never run, and XrShift asks you to run it once rather than invent the file. Start VR
registers automatically; the Games page has Register and Unregister buttons.

The driver only adds the glasses while XrShift is in VR mode, so leaving it registered does not get in the way of
another headset.

## The Games page

The VR card lists, each with ✓ or ✗ and what to do: the driver in this copy; SteamVR installed (Steam's own
folder and every library in `steamapps\libraryfolders.vdf`); the driver registered; the glasses in Follow; the
glasses connected and in 3D (– and a note when flat: it works, without depth). Picture:
`XrShift.exe --settings-shot games.png --page games` with `XRSHIFT_CONFIG` set to a scratch folder.

## Known limits and things to watch on the first run

- The driver has not been compiled here. See the report that came with this change for what a compiler might still
  object to.
- `VREvent_DriverRequestedQuit` through `VendorSpecificEvent` is how other drivers (ALVR) close SteamVR; if SteamVR
  ignores it, close SteamVR's window by hand after stopping VR.
- Windows display scaling: the window rectangle is in physical pixels (XrShift is per-monitor DPI aware). If SteamVR's
  window lands offset or the wrong size on a glasses monitor set to a scale other than 100%, set that monitor to 100%.
- SteamVR may ask for Room Setup on first run: choose "Seated only".
- If another headset's driver is installed and active, SteamVR may pick it first. `"forcedDriver": "xrshift"` in the
  `steamvr` section of `Steam\config\steamvr.vrsettings` makes it use the glasses.
- The angular velocity is given in world axes. If, on the first run, turning the head makes the picture overshoot
  sideways or swing the wrong way during quick turns (but settle correctly), SteamVR wants it in head axes instead:
  that is a one-line change in `HeadTracker.GetVrPose`.

## Test plan (for a person, once SteamVR is installed and the driver DLL exists)

Before: a release of XrShift that has `steamvr\xrshift\bin\win64\driver_xrshift.dll` beside `XrShift.exe`; SteamVR
installed from Steam, started once and closed; the glasses plugged in, XrShift running with them, the glasses in
Follow; a gamepad connected.

1. **Games page.** Open XrShift's settings, Games. Expect ✓ for the driver, SteamVR (with its folder) and Follow, and for
   the glasses ✓ in 3D or – when flat. The registration line says SteamVR does not know the driver yet. Click Register driver,
   answer Yes. Expect it to turn ✓, and `%LOCALAPPDATA%\openvr\openvrpaths.vrpath` to list the driver folder, with
   `openvrpaths.vrpath.bak-before-xrshift` beside it.
2. **3D mode.** In the glasses' own menu, switch them to 3D. Expect XrShift to start its scene again at
   3840 x 1080 and the Games page's glasses line to say 3D mode, 3840 x 1080 at 60 Hz.
3. **Start VR.** Click Start VR (or Ctrl+Alt+V). Expect the floating screens to go, the settings window to hide, and
   SteamVR to start. SteamVR's status window should show a headset (green) and no "headset not detected". In the
   glasses: SteamVR's grey home or void, in stereo (each eye its own picture; things at different depths). XrShift's
   log says "VR mode on: glasses at ..., 3D side by side, 60 Hz".
4. **Looking around.** Turn your head left: the world turns right in the glasses, at the same speed as your head,
   without sticking or lagging, and stops when you stop. Look up and down, tilt your head. Turning 90 degrees and back
   should bring you back to the same view. Ctrl+Alt+0 makes where you look straight ahead.
5. **Scale and shape.** Things should look their real size and not stretched: a doorway looks like a doorway, a
   circle like a circle. If everything looks too big or squashed, the view angle is off; if both eyes do not fuse
   into one picture, the IPD or the halves are swapped.
6. **A game.** Start a gamepad VR game from Steam (No Man's Sky). It should appear in the glasses and take the gamepad.
7. **Stop.** Ctrl+Alt+V. Expect SteamVR to close by itself within a second or two, the floating screens to come back,
   and your extra monitors to return. XrShift's log says "VR mode off; SteamVR asked to close."
8. **Flat.** Switch the glasses back to flat (1920 x 1080 at 90 Hz) and repeat 3 and 4: one picture, no depth, but it
   should still turn with your head and look the right size.
9. **Without VR mode.** Start SteamVR yourself from Steam while XrShift is not in VR mode: SteamVR should not show the
   glasses as a headset (vrserver.txt says "XrShift is not in VR mode").

If something fails, send:

- `C:\Program Files (x86)\Steam\logs\vrserver.txt` (the driver's own lines start with `XrShift:`) and
  `C:\Program Files (x86)\Steam\logs\vrmonitor.txt` (in your Steam folder's `logs` if Steam is elsewhere);
- XrShift's `%LOCALAPPDATA%\XrShift\last-run.txt`;
- `%LOCALAPPDATA%\openvr\openvrpaths.vrpath`;
- which step, what you saw in the glasses, and the glasses' mode (3D or flat).
