# Building Net Relay

## Prerequisites

- Windows 10 or later, x64.
- .NET 8 SDK.
- Node.js and npm.
- JDK 17.
- Android SDK Platform 34 and Build Tools, with `ANDROID_SDK_ROOT` or `ANDROID_HOME` configured.
- Inno Setup 6 only when building the Windows installer.

## One-command development build

From the repository root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Build-All.ps1
```

The script performs these stages:

1. Copies ADB from the installed Android SDK.
2. Downloads official tun2socks 2.6.0 and Wintun 0.14.1 archives and verifies the selected binaries.
3. Runs `npm ci` and builds `windows/ui/dist`.
4. Runs Android unit tests and builds the debug APK.
5. Copies the development APK under the filename expected by the Windows client.
6. Builds the Windows Release configuration and the protocol/logic tests.

All generated outputs are ignored by Git.

## Manual build

```powershell
.\scripts\Prepare-Dependencies.ps1

Push-Location windows\ui
npm ci
npm run build
Pop-Location

Push-Location android
.\gradlew.bat testDebugUnitTest assembleDebug --no-daemon
Pop-Location

New-Item -ItemType Directory windows\android -Force
Copy-Item android\app\build\outputs\apk\debug\app-debug.apk `
  windows\android\NetRelay-Android-1.0.0-release.apk

dotnet build windows\NetRelay.csproj -c Release
```

The copied debug APK is for development only. Maintainers create official release APKs with a private signing identity stored outside the repository.

## Release signing

Android signing is configured only through the `NETRELAY_KEYSTORE_PATH`, `NETRELAY_KEYSTORE_PASSWORD`, `NETRELAY_KEY_ALIAS`, and `NETRELAY_KEY_PASSWORD` environment variables. Do not place their values in files tracked by Git.

The Windows installer source is `installer/NetRelay.iss`. Place the official WebView2 offline installer at `installer/prerequisites/MicrosoftEdgeWebView2RuntimeInstallerX64.exe`, publish the Windows application to `FINAL_RELEASE/Windows/App`, and compile with Inno Setup. Authenticode signing is a maintainer-only post-build step.
