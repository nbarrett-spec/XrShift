# Releasing

The version lives in one place: `<Version>` in `Directory.Build.props`. The app shows it in the settings window title.

## Cut a release

1. Make sure `main` is what you want to ship and the `build` workflow is green.
2. Tag and push:

   ```
   git tag v0.3.1
   git push origin v0.3.1
   ```

   The tag is the version: `v0.3.1` builds as `0.3.1` (the `<Version>` in `Directory.Build.props` is only the default for local builds, so bump it afterwards if you want local builds to match).

## What the workflow does

`.github/workflows/release.yml` runs on any `v*` tag, on `windows-latest` with .NET 10:

1. builds and tests the solution with the tag's version,
2. builds the SteamVR driver (C++) with `src/XrShift.SteamVR/build.ps1 -Verify` (the runner has the Visual C++ tools; a PC does not need them). The `build` workflow does the same on every push and keeps the driver folder as the `steamvr-driver` artifact,
3. publishes `src/XrShift.App` self-contained for `win-x64` (nothing else to install on the PC); the project copies the driver folder into the app,
4. makes `XrShift-kit-<version>.zip` containing the `XrShift` folder, `setup-other-pc.ps1` and `register-vr-driver.ps1`, and fails if the driver is missing from it,
5. creates a GitHub release for the tag with the zip attached and notes generated from the commits.

The kit holds no `network.json` and nothing from a settings folder.

The driver is built with `/O2 /MT`: the C++ runtime is linked in, so no Visual C++ redistributable is needed. In the kit it sits at:

```
XrShift/steamvr/xrshift/driver.vrdrivermanifest
XrShift/steamvr/xrshift/resources/settings/default.vrsettings
XrShift/steamvr/xrshift/bin/win64/driver_xrshift.dll
```

Check a release: unzip it and confirm `driver_xrshift.dll` is there; run `register-vr-driver.ps1 -Status` (it says whether SteamVR is installed, the driver is present and registered). With the Visual Studio tools installed, `dumpbin /exports driver_xrshift.dll` must list `HmdDriverFactory`. `register-vr-driver.ps1 -Register` / `-Unregister` add or remove the driver in SteamVR's `openvrpaths.vrpath`; `setup-other-pc.ps1 -RegisterVr` does it as part of the install (without the switch it only prints the status and a hint).

## Install on the other PCs

1. Download `XrShift-kit-<version>.zip` from the release and unzip the whole thing.
2. Copy `network.json` (the shared secret, from the PC with the glasses) into the unzipped folder, beside `setup-other-pc.ps1`.
3. Quit XrShift on that PC, then run `powershell -ExecutionPolicy Bypass -File setup-other-pc.ps1`.

The script prints the version it installs and refuses to put an older version over a newer one; add `-Force` to do that on purpose.
