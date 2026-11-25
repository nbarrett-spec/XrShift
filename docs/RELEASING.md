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
2. publishes `src/XrShift.App` self-contained for `win-x64` (nothing else to install on the PC),
3. makes `XrShift-kit-<version>.zip` containing the `XrShift` folder and `setup-other-pc.ps1`,
4. creates a GitHub release for the tag with the zip attached and notes generated from the commits.

The kit holds no `network.json` and nothing from a settings folder.

## Install on the other PCs

1. Download `XrShift-kit-<version>.zip` from the release and unzip the whole thing.
2. Copy `network.json` (the shared secret, from the PC with the glasses) into the unzipped folder, beside `setup-other-pc.ps1`.
3. Quit XrShift on that PC, then run `powershell -ExecutionPolicy Bypass -File setup-other-pc.ps1`.

The script prints the version it installs and refuses to put an older version over a newer one; add `-Force` to do that on purpose.
