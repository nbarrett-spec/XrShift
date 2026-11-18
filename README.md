# XrShift

Floating screens for XR glasses on Windows. Plug your glasses in and your laptop screen and any window you choose, like Teams, a browser or a game, float around you where you put them.

Built and tested with **XREAL One Pro** glasses (with the XREAL Eye camera). XrShift is an independent project. It is not made or endorsed by XREAL.

> **Status: early.** XrShift works and is used daily by its first tester, but it has been tested on one laptop with one pair of glasses. Expect rough edges, and please report what you find.

## Made with AI

XrShift was written by **Claude** (Anthropic's AI model), working as the developer. A person tested every version with real glasses and decided what to build. The design, the code, the measurements and this README all came out of that collaboration. Fixes came from that person's logs and reports.

## What it does

- **Two modes, matching the glasses' own button**
  - **Follow:** XrShift tracks your head and keeps your screens all around you: left, right, above, at any size, flat or curved.
  - **Stationary:** the glasses hold their picture still in the room using their camera. XrShift arranges your screens inside it, so they stay put even when you lean or walk. Each mode keeps its own screens and profiles, and XrShift notices on its own which mode the glasses are in.
- **Pin any window.** Float a single window as a screen of its own. Pinned windows keep updating on their own, even Teams and browsers, which normally stop drawing when other windows cover them.
- **Look to point.** The mouse can jump to the screen you look at, or ride the middle of your view.
- **Hand gestures (stationary mode).** Your palm moves the pointer and a pinch clicks, using the glasses' own camera. This is an optional extra that is set up from inside the app.
- **Profiles, presets and zoom.** Keep arrangements for work, games or films, and lay them out in one click.
- **Plain screen.** One key hands the glasses back to Windows as an ordinary screen, for a game at full speed.
- **VR mode (experimental).** Play SteamVR games seated: the glasses become a headset that follows your head's turns (not leaning or walking). Switch the glasses to their 3D mode for depth. Games that work with a gamepad, like No Man's Sky, are the place to start. Needs SteamVR; the driver is in `src/XrShift.SteamVR` and builds with Microsoft's C++ build tools.
- **Glasses view.** A live picture of what the glasses show, inside the settings window. Handy for showing someone else.

## Getting started

1. Plug the glasses into a USB-C port that carries video. In Windows, press **Win+P** and choose **Extend**.
2. Start XrShift. A short guide explains the rest.
3. To quit, close the XrShift window with **X**. Nothing keeps running afterwards.

Requirements: Windows 11 and an XREAL One series pair of glasses. Building it yourself needs the .NET 10 SDK:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

This builds XrShift and puts it in your user folder with Desktop and Start menu shortcuts. `scripts\install.ps1 -Uninstall` removes it again.

## Keys

All of them work from any program.

| Key | Does |
|---|---|
| Ctrl+Alt+S | Open XrShift's window |
| Ctrl+Alt+0 | Recentre: the way you face now becomes straight ahead |
| Ctrl+Alt+L | Look to point: off, jump, follow |
| Ctrl+Alt+M | Bring the mouse to where you are looking |
| Ctrl+Alt+P | Pick up the screen you look at; again to set it down |
| Ctrl+Alt+PgUp / PgDn | Everything bigger or smaller |
| Ctrl+Alt+B | Hide all screens for a moment |
| Ctrl+Alt+K | Hand gestures on or off |
| Ctrl+Alt+G | Plain screen: the glasses become an ordinary Windows screen |
| Ctrl+Alt+V | VR mode on or off |
| Ctrl+Alt+1 to 4 | Switch profile |
| Ctrl+Alt+Q | Quit |

## Privacy

- XrShift sends nothing over the internet. It talks to the glasses over their USB cable only. It reads their motion sensor, and in stationary mode their camera picture. It never sends commands to the glasses.
- The camera picture is used for hand tracking on your PC and is never saved.
- Setting up hand gestures downloads Google's open-source MediaPipe (from the Python package index) and its hand model (from Google). It's about 390 MB, kept in one folder, and removable from the app's setup script.
- Settings and a log of the last run stay on your PC, in `%LOCALAPPDATA%\XrShift`.

## Known limits

- **Follow mode tracks rotation only.** Leaning or walking moves the screens with you, and straight ahead slowly drifts over time; Ctrl+Alt+0 recentres.
- **Stationary mode has one picture.** The glasses hold a single 1920x1080 picture still, so every screen in stationary mode shares it. Two screens side by side are each about half as sharp as one.
- **Extra Windows desktops.** Virtual monitor drivers slowed Windows down badly in our tests and one crashed repeatedly, so XrShift no longer needs one. Pinned windows cost almost nothing.
- **Hand tracking is a first version.** It needs good light; the camera sees in grey.

## How it works

| Folder | What it is |
|---|---|
| `src/XrShift.Tracking` | Reads the glasses' motion sensor and works out where the head points, with prediction |
| `src/XrShift.Scene` | The maths of screens in space: layouts, profiles, presets |
| `src/XrShift.App` | The app: Direct3D 11 renderer, window and screen capture, the settings window, hand tracking |
| `src/XrShift.SteamVR` | The SteamVR driver for VR mode (C++, uses Valve's OpenVR driver header) |
| `src/XrShift.Probe` | `xrprobe`, a command-line tool for checking and calibrating the tracking |
| `tests` | Unit tests for the tracking and scene libraries |

What we learned about how the glasses talk to a PC builds on open community work, in particular [kevindurb/xreal-linux](https://github.com/kevindurb/xreal-linux) and [nikryden/Screen-XREAL-1S](https://github.com/nikryden/Screen-XREAL-1S).

Thanks to [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct3D for .NET) and [MediaPipe](https://github.com/google-ai-edge/mediapipe) (hand tracking).

## License

MIT. See [LICENSE](LICENSE).
