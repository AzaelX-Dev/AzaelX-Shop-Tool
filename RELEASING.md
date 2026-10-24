# AzaelX-Tools release process

The app's Velopack updater uses the public release feed hosted in:

`https://github.com/AzaelX-Dev/AzaelX-Shop-Tool`

The workflow in `.github/workflows/release.yml` runs in this repository on a pushed tag matching `v*`. It validates that a `vMAJOR.MINOR.PATCH` tag exactly matches `<Version>` in `src/LuaToolsGui.csproj`, restores and tests the solution on Windows, publishes the app, packages it with Velopack, then publishes the installer/feed assets to this repository using the workflow's `GITHUB_TOKEN` permissions.

## Before tagging a version

1. Build and test from a clean checkout on Windows:

   ```powershell
   dotnet restore .\LuaToolsGui.sln
   dotnet build .\LuaToolsGui.sln -c Release -m:1
   dotnet test .\LuaToolsGui.sln -c Release -m:1
   ```

2. Confirm the working tree and current tags. Never overwrite a tag or reuse an existing version number.
3. Update `<Version>` in `src/LuaToolsGui.csproj`, commit and push the source, then create and push the matching `vMAJOR.MINOR.PATCH` tag **only after** Build and Tests pass.
4. Confirm the `Build and publish Velopack release` workflow succeeded. It publishes the setup installer, `.nupkg` package(s), and update-feed metadata. Uploading only the setup EXE is not enough for in-app updates.

## Initial release candidate

The project file is set to `1.0.0` for a fresh repository. Run the Windows build and tests from a clean checkout before pushing the `v1.0.0` tag. Confirm the release workflow succeeds and that the Velopack installer, package(s), and update-feed metadata are present. Do not reuse an existing tag or version.
