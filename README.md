# Net Relay documentation

This is the single-document collection of the project-root Markdown documents.
The original files remain the authoritative, separately linked documents and
have deliberately not been deleted. Release-validation sections below are
historical snapshots dated 2026-09-06; they preserve the evidence and status
recorded at that time rather than silently rewriting it.

## Contents

1. [README](#readme)
2. [Architecture](#architecture)
3. [Contributing](#contributing)
4. [Security policy](#security-policy)
5. [Third-party notices](#third-party-notices)
6. [Final Chromium-only public release](#final-chromium-only-public-release)
7. [Public Setup certification](#net-relay-100--public-setup-certification)
8. [Responsive DPI any-display candidate](#net-relay--responsive--dpi--any-display-candidate)

---

## README

# Net Relay

Net Relay 1.0.0 is the completed source baseline for sharing an Android phone's Internet connection with Windows over USB or Wi-Fi Direct. Both transports use the same bounded AEL2 data plane.

The supported product scope is the Android phone application and Windows client. Bluetooth, Android TV, and TV Box targets are not part of this repository.

### Features

- USB transport through Android Debug Bridge forwarding.
- Wi-Fi Direct discovery, active WLAN scanning, saved-profile connection, and P2P endpoint discovery.
- Local SOCKS5 gateway with Wintun and tun2socks integration.
- TCP and UDP multiplexing over the framed AEL2 protocol.
- Android upstream selection with VPN-aware, fail-closed routing policy.
- Windows WPF host with a local React/WebView2 interface.
- English, Arabic, French, and Russian UI resources.

### Repository layout

- `android/` — Android application and phone-side socket gateway.
- `windows/` — Windows client, protocol, transport, and React UI source.
- `logic_tests/` — protocol and endpoint logic tests.
- `installer/` — Inno Setup source; generated installers are not committed.
- `scripts/` — dependency preparation and repeatable build scripts.
- `docs/` — build, architecture, security, and release documentation.

Generated binaries, APKs, installers, signing identities, local SDK paths, runtime captures, and archived builds are intentionally excluded from this repository.

### Quick build on Windows

Install .NET 8 SDK, Node.js with npm, JDK 17, and Android SDK 34. Ensure `ANDROID_SDK_ROOT` or `ANDROID_HOME` points to the SDK.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Build-All.ps1
```

The script prepares the official runtime dependencies, builds the React UI, runs Android unit tests, creates a debug APK for development, and builds the Windows Release configuration.

Remove all generated outputs again with:

```powershell
.\scripts\Clean.ps1
```

For manual instructions and release signing notes, see [docs/BUILDING.md](docs/BUILDING.md). For ports and data flow, see [ARCHITECTURE.md](ARCHITECTURE.md).

### Security

Do not commit passwords, keystores, PFX files, VPN configurations, device logs, or signing environment files. The repository includes automated build, dependency review, CodeQL, and release-hygiene checks. See [SECURITY.md](SECURITY.md).

### License

Net Relay source is available under the MIT License. Third-party components retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

---

## Architecture

### Supported transports

#### USB

Windows connects to Android through ADB forwards:

- Control: local `8988` to Android `8988`.
- AEL2 data gateway: local `8992` to Android `8991`.

#### Wi-Fi Direct

Android creates the P2P group and waits for its real P2P interface address. Windows connects to that network and reaches:

- Control: Android P2P address on `8988`.
- AEL2 data gateway: Android P2P address on `8991`.

### Shared data plane

Windows applications -> Wintun -> tun2socks -> SOCKS5 `127.0.0.1:8990` -> AEL2 -> Android socket gateway -> Android-selected network -> Internet.

The Android upstream selector enforces the current VPN policy and does not silently route applicable VPN traffic through a physical network. Windows keeps transport endpoints outside the tunnel default route to prevent routing loops. NCSI integration uses the configured Wintun addressing and routes.

### Protocol

AEL2 is a bounded framed stream carrying multiplexed TCP and UDP flows. It is shared by USB and Wi-Fi Direct; transport-specific code only establishes the control and data connection.

---

## Contributing

Contributions are welcome through focused issues and pull requests.

1. Do not commit generated binaries, device logs, credentials, or signing files.
2. Keep USB and Wi-Fi Direct transport endpoints isolated from each other.
3. Preserve AEL2 frame ordering, bounded queues, and session-generation guards.
4. Run `scripts/Build-All.ps1` before submitting a pull request.
5. Describe runtime validation separately from build/test success.

For networking changes, include the first failed stage, relevant logs with secrets removed, and the exact transport used.

---

## Security policy

### Reporting a vulnerability

Please report security issues privately to the repository owner instead of opening a public issue. Include affected version, reproduction steps, and impact. Remove phone identifiers, public IP addresses, VPN credentials, and private keys from all attachments.

### Sensitive files

The repository must never contain Android keystores, Authenticode PFX files, passwords, tokens, VPN configurations, private certificates, device dumps, or production signing environment variables.

The included build scripts create only generated, ignored directories. Release signing must be performed by the maintainer using credentials stored outside the repository.

### Automated safeguards

Every change is checked for retired branding, generated binaries, private credentials, personal paths, and incomplete implementation markers. GitHub workflows also run the complete build and test suite, CodeQL analysis, and dependency review. Dependabot proposes dependency updates for GitHub Actions, npm, NuGet, and Gradle.

These controls reduce accidental exposure and vulnerable changes, but they do not replace release signing or review. Keep Android keystores and Authenticode certificates outside the repository and publish checksums for release artifacts.

---

## Third-party notices

Net Relay downloads or uses the following runtime components during local builds. Their binaries are not committed to this source repository.

- Android SDK Platform-Tools / ADB — Android Open Source Project; Apache License 2.0 and component-specific notices.
- tun2socks v2.6.0 — xjasonlyu/tun2socks; MIT License.
- Wintun 0.14.1 — WireGuard LLC. Source is GPL-2.0; the official signed binary distribution includes its own more permissive redistribution license.
- Microsoft Edge WebView2 Runtime — Microsoft license terms.
- React, Vite, TypeScript, Microsoft.Web.WebView2, System.Reactive, AndroidX, Kotlin coroutines, Google Play services Nearby, and Material Components — see their package metadata and upstream licenses.

Distributors must retain upstream notices and review the license files included in downloaded archives.

---

## Final Chromium-only public release

> Historical validation snapshot dated 2026-09-06. Its distribution correction
> below records that this local gate was not by itself public-Setup certification.

Date: 2026-09-06
Build source: this public project, not the private reference build.

C SOURCE CHROMIUM CLEANUP: PASS
MainWindow.xaml: ABSENT
MainWindow.xaml.cs: ABSENT
OpenLegacyFallback: 0
Legacy runtime routes: 0
NETRELAY_DISABLE_WEBUI legacy route: 0
ChromiumWindow compiled type: PRESENT
MainWindow compiled type: ABSENT

The startup and failure handling now match the accepted private reference cleanup. The only application UI is Chromium/WebView2. Native startup error dialogs do not expose a replacement application UI. Shared WPF framework DLLs remain necessary to host WebView2; their presence is not an old application shell.

Frontend: PASS
Windows Debug: PASS
Windows Release: PASS
Release errors: 0
Release warnings: 0
AEL2 tests: PASS
WFD/WLAN tests: PASS
Backpressure tests: PASS
Product UI notification harness: PASS

### Real executable validation

Direct NetRelay.exe: PASS. WebView initialized and uiReady logged at 13:11:56–13:11:58. All 6 Chromium pages: PASS, user verified Dashboard, Connection, Statistics, Logs, Settings and About.

Isolated missing-ui test: PASS. A separate copy without ui/dist logged the actual missing-file exception. The user confirmed the native startup error and exit without old UI. The failure-test process exited. Normal Release assets were not removed or altered for this test.

The prior dotnet/DevTools diagnostic invocation was not used as the production launch gate.

### Installer and installed copy

Installed Setup: PASS
Installed UI: CHROMIUM
Legacy UI possible: NO in this compiled release

Previous installed copy was uninstalled successfully. The new Setup was built from the fresh ChromiumOnly-20260906/Release directory with explicit compiler path overrides, then signed using the existing identity. Installer branding and WebView2 installation strategy were preserved.

Clean installation returned exit code 0. All 485 application payload files installed under Program Files matched the fresh Release by SHA-256. The actual installed EXE was launched normally; WebView initialized and uiReady were recorded. The user verified all six pages again in the installed copy.

Installed NetRelay.exe SHA256:
`3C201C0D1868104153A7006F52CA5AD92F125C240160C8D383B3EA3E173F146E`

### Quick connectivity checks

USB quick check: PASS (user-confirmed Internet; HELLO_ACK 13:17:58, data plane ready, NCSI Internet 13:18:23).
WFD quick check: PASS (user-confirmed Internet; HELLO_ACK 13:37:22, data plane ready 13:37:30, NCSI Internet and ActiveHttpProbeSucceeded 13:37:40).
VPN: NOT TESTED in this gate; no claim of fresh VPN certification.

The user attributed early failed attempts to USB debugging/phone USB mode and Wi-Fi not being enabled. The user also reported a later temporary Wi-Fi interruption after taking the phone downstairs. These are recorded as user-reported setup/range events, not evidence of a program crash.

The log also contains destination-specific TCP TTL-expired warnings, including 188.114.96.5:443, alongside successful HTTPS/DNS flows and NCSI Internet. They were not silently discarded or all attributed to setup. This is a quick product gate, not a zero-warning or long-duration network qualification.

Android modified: NO. Networking modified: NO. React design/themes/localization modified: NO. Reference comparisons matched all checked Android source, services, transports, ViewModels and React source files. RAR emergency backup: NOT TOUCHED. Private signing material copied into public source: NO.

### Final APPS

APK: `APPS/NetRelay-Android-1.0.0-release.apk`
SHA-256: `ACD3DD5E5DCE2AC27CF9EF1DE213A366D88E5B99BDA5D3C03A447AF27F85798C`
Package: `com.netrelay.connectivity`, version 1.0.0 (1)
Certificate SHA-256: `9CD99B7BEAE9D1A2CE7562BA67F22E957B4023AC6D9CF8F4C749B05694BD6B7B`

Setup: `APPS/NetRelay-Windows-1.0.0-Setup.exe`
SHA-256: `5888CA0BB5E6C0869E750FC0906A77CECB4232407BCB7F6E75CA035929D13D8A`

Portable: `APPS/NetRelay-Windows-1.0.0-Portable.zip`
SHA-256: `7818CEA6A6812A8FCDF98A4B2A963B761CCC5F5E9676ED54D5569ECB1E471101`

Portable contains the exact tested Release payload plus README and the offline WebView2 installer. All 487 ZIP files were checked against staging. A separate second-machine Portable launch was not performed. WebView2 installation is required if absent; it is bundled as an installer, not a fixed-version browser runtime.

Windows signer thumbprint: `D7B5736B8CF01BFE6E90301F74BE8022013418F5`. Existing self-signed identity retained; not publicly CA-trusted. Windows reports an untrusted root. No new identity or trust-store modification was made.

APPS contains only the current APK, current Setup, current Portable ZIP and SHA256SUMS.txt. The previous Setup and hash list were preserved outside APPS under FINAL_RELEASE/ChromiumOnly-20260906/SupersededAPPS.

FINAL_PUBLIC_CHROMIUM_RELEASE = PASS (historical local UI/payload gate only)

2026-09-06 distribution correction: this historical local gate is NOT public Setup certification. The normal-user PC reported missing WebView2. Public release is BLOCKED until the corrected Setup passes normal-user acceptance. See the next section. APPS has not yet been promoted.

---

## Net Relay 1.0.0 — public Setup certification

> Historical certification snapshot dated 2026-09-06. It records the corrected
> installer design and the evidence available at that point.

Date: 2026-09-06
NETRELAY_PUBLIC_SETUP = BLOCKED

Reason: the corrected, signed Setup passed developer-machine installation and payload verification, but the mandatory normal-user acceptance on the brother's PC without WebView2 had not happened yet. This was not a public-release PASS. No file in APPS was replaced.

### Public user model

A normal Windows user receives ONE Setup file only. Setup must provision required redistributables itself. No manual WebView2/.NET/VC++/ADB installation, PowerShell, PATH changes, or DLL copying is part of the end-user workflow.

Candidate (323,403,392 bytes): `FINAL_SETUP_VALIDATION/NetRelay-Windows-1.0.0-Setup-PublicCandidate.exe`
SHA-256: `C94EE24A456E62EA8C2D7A2E0CFD0120D66329BF6EDE309373F0BC40387AC5FA`

The exact accepted bytes must be copied to APPS after acceptance. Do not rebuild a different binary after the brother's successful test.

### Failure and correction

The user screenshot established that WebView2 was unavailable when the previous installed application started on the other PC. The old installer used a `[Run]` entry without checking the prerequisite installer's exit code or verifying runtime availability afterwards. It could complete and launch Net Relay even without a working WebView2 Runtime. The reason Microsoft's installer did not provision the runtime on that particular PC remained unproven without that run's installation log.

The corrected installer uses `PrepareToInstall` before copying/finalizing the app:

1. Check documented WebView2 registration in HKLM32, HKLM64 and HKCU.
2. Verify availability using a signed, x64, installer-only native probe calling `GetAvailableCoreWebView2BrowserVersionString` from the same shipped loader.
3. If unavailable, extract and run the bundled offline x64 redistributable with `/silent /install` under elevated Setup; wait and log the exit code.
4. Recheck registration AND the loader API, bounded to 30 seconds after exit.
5. If unavailable, stop with a concise Setup error. No successful installation completion or Net Relay launch is allowed from this branch.
6. Restart-required codes 3010/1641 stop the installation with restart guidance; they do not launch Net Relay. No forced reboot is requested.
7. Gate the Finish-page launch on runtime verification again.

No legacy UI fallback was added. Existing UAC behavior was retained.

Sources for implementation:

- https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- https://jrsoftware.org/ishelp/topic_scriptevents.htm

### Inventory before packaging changes

`FINAL_SETUP_VALIDATION/baseline-inventory.json`: 485 original publish files, sizes, hashes, PE architectures, normal imports and delay imports.

`FINAL_SETUP_VALIDATION/release-inventory.json`: 485 freshly published files. All 485 match the accepted baseline hashes. The application payload is unchanged; the changes are in installer provisioning and installer-only support files.

`FINAL_SETUP_VALIDATION/support-inventory.json`: native probe and loader evidence.

### Build and freeze

Frontend: PASS (npm run build, 52 modules). Windows Release: PASS (fresh publish, win-x64, self-contained true). Final successful compiler runs: no errors or warnings observed. Intermediate invocation errors were corrected (npm initially invoked from repository root; Inno preprocessor rejected a line beginning with a Pascal character literal). Installer compiled successfully; existing Windows identity used for signing.

Android/source/networking/React code modified in this phase: NO. USB and Wi-Fi Direct requalified in this phase: NO; previously accepted baseline retained. No VPN qualification, rotation changes, adb monkey, Computer Use, or network disabling. Historical Chromium-only removals in the existing git diff predate this phase.

### Runtime dependency audit

| Component | Evidence / result |
| --- | --- |
| .NET | Self-contained Microsoft.NETCore.App and Microsoft.WindowsDesktop.App 8.0.25 in runtimeconfig includedFrameworks; coreclr/hostfxr/hostpolicy/WPF assemblies present. No external .NET installation required by this payload. Clean-PC runtime proof pending. |
| WebView2 SDK | Core/Wpf/WinForms assemblies, x64 WebView2Loader.dll at application root and runtimes/win-x64/native present. SDK files are distinct from the installed browser runtime. |
| WebView2 offline redistributable | Microsoft-signed 258,438,352-byte standalone payload. SHA256 987A9D8B3107E84F9B53B4A077D28AE4814FC3D964D5A55C559E7334BBF24D61. PE engine version 1.3.263.3 is NOT the browser version. Embedded manifest identifies WebView2 x64 152.0.4191.53, WebView2 app GUID, and --msedgewebview install action. Embedded tag selects Microsoft Edge WebView2 Runtime. No separate bootstrap download is specified by Net Relay Setup. Actual disconnected installation still pending. |
| VC++ | No separate vcruntime140.dll/msvcp140.dll requirement found in normal/delay native imports. .NET's vcruntime140_cor3.dll is bundled. Probe uses static CRT and imports only kernel32/ole32. Separate VC++ redistributable not added without evidence of need. |
| Windows DLLs/UCRT | OS imports and api-ms-win-crt contracts rely on Windows; random System32 DLLs were not copied. Installer currently declares Windows 10 build 17763 minimum and x64-compatible platform. Local test OS is build 26200. Minimum-version and ARM64 behavior were not certified. |
| ADB | Bundled adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll are a matching x86 helper set, valid as out-of-process tools on x64 Windows; not loaded into the x64 app. adb version from installed Program Files passes: 1.0.41 / 37.0.1-15733141. No SDK/PATH lookup needed. |
| Wintun | tools/netmode/wintun.dll present, PE AMD64; dependencies audited; app-relative lookup exists. Network behavior unchanged. |
| tun2socks | tools/netmode/tun2socks.exe present, PE AMD64; installed `-version` passes: 2.6.0, windows/amd64, Go1.23.1. Normal PE import is kernel32; runtime Wintun companion bundled. |
| Android APK | Installed android/NetRelay-Android-1.0.0-release.apk hash matches accepted APK. AdbBridgeService resolves both APK and ADB from AppContext.BaseDirectory. |
| React | Complete ui/dist HTML, JS, CSS and local assets; no Vite server, Node/npm at runtime, or external CSS/JS imports in HTML/CSS. React error-documentation URLs and XML namespace URLs are not runtime asset dependencies. Offline six-page test on normal-user PC remained pending. |

Physical phone USB debugging/authorization and an appropriate device driver remain Windows/phone prerequisites for detecting specific hardware, not SDK installations. This packaging audit does not establish universal OEM-driver compatibility.

### Developer-machine checks

- New Setup executed: PASS, exit 0.
- Log: `FINAL_SETUP_VALIDATION/developer-install.log`.
- 17:05:01: WebView2 registered=1, loader probe exit=0, ALREADY_PRESENT.
- 17:05:08: installation process succeeded.
- Installed Program Files hashes: all 485 files match fresh Release.
- Installed bundled ADB and tun2socks independently start successfully.
- Installed APK hash verified unchanged.
- NetRelay.exe started from Program Files without starting a connection.
- 17:06:56: WebView initialized; 17:06:58: uiReady and loading panel hidden.
- Probe with loader absent: exit 11 (LOADER_ERROR=126).
- Probe with nonexistent browser folder in its isolated process environment: exit 12, HRESULT 0x80070002, version absent. Environment restored afterwards.
- Probe with actual local runtime: exit 0, version 152.0.4191.66.
- No installed WebView2 removal or machine-wide registry tampering performed.
- Release hygiene script: PASS.
- No private keys/password files in the 485-file payload or prerequisite support.

### Normal-user acceptance — not yet run at snapshot time

Test machine: brother's normal end-user Windows PC. Previous Net Relay installation: YES according to the user's incident. WebView2 initially available: screenshot says unavailable; state before the new run pending. Internet disconnected during new Setup: PENDING. Only one new Setup executed: PENDING. Manual WebView2/.NET/VC++ installation or DLL copying: NOT AUTHORIZED / none instructed. Additional dependency files given to user: NONE. New Setup executed: PENDING. WebView2 automatic provisioning: PENDING (not ALREADY_PRESENT proof). Exit code and post-install verification: PENDING. Installed NetRelay.exe / Chromium startup: PENDING. Dashboard / Connection / Statistics / Logs / Settings / About: all PENDING. Offline frontend: PENDING. Legacy UI: absent in accepted unchanged payload; no fallback introduced.

Acceptance instruction: give the brother only the candidate Setup, complete the normal wizard, then launch Net Relay and check the six pages. Do not install libraries separately. Prefer disconnected Internet for this run. If Setup reports an error, retain the Setup log; do not work around the prerequisite manually.

### Security and publication

Windows signer unchanged: `D7B5736B8CF01BFE6E90301F74BE8022013418F5`. Self-signed, NOT publicly CA-trusted; no claim of eliminating SmartScreen/UAC warnings. No certificate regenerated, no trust store changed, no private PFX/password packaged.

Android package: `com.netrelay.connectivity`. APK modified/rebuilt/resigned: NO. APK SHA256: `ACD3DD5E5DCE2AC27CF9EF1DE213A366D88E5B99BDA5D3C03A447AF27F85798C`. Emergency D RAR and private master: untouched.

APPS remained unchanged at the snapshot. Existing Setup hash: `5888CA0BB5E6C0869E750FC0906A77CECB4232407BCB7F6E75CA035929D13D8A`. Portable remained unchanged; it requires its bundled WebView2 installer to be run once if runtime is absent. It was NOT claimed to provide Setup's automatic provisioning.

Published-candidate hash equality: NOT APPLICABLE until normal-user acceptance. After acceptance, copy exact candidate bytes to `APPS/NetRelay-Windows-1.0.0-Setup.exe`, verify SHA256 equals `C94EE24A456E62EA8C2D7A2E0CFD0120D66329BF6EDE309373F0BC40387AC5FA`, and update `APPS/SHA256SUMS.txt` without replacing Android.

FINAL STATUS: NETRELAY_PUBLIC_SETUP = BLOCKED (mandatory normal-user test pending).

---

## Net Relay — responsive / DPI / any-display candidate

> Historical responsive-validation snapshot dated 2026-09-06.

Date: 2026-09-06
NETRELAY_RESPONSIVE_ANY_DISPLAY = BLOCKED

Pending: visual acceptance on both the developer PC and brother's plasma/TV. Automated layout tests are not proof of all physical displays, GPU drivers, color calibration or overscan conditions.

### Evidence before changes

Developer display: 1680x1050 physical pixels, scale 100%, window DPI 96. Installed baseline window: normal (not maximized), 1380x860 pixels at (150,71). Client area: 1364x821 pixels. Native window DPI awareness: SYSTEM (1), not per-monitor. Effective viewport approximately client size at 100%; exact old WebView2 viewport and ZoomFactor were not exposed by the production bridge. No invented measurement. Baseline source did not set ZoomFactor and disabled zoom controls; SDK default is 1.0. Persisted origin zoom was not independently verified or reset.

Brother display: 1080-class plasma/TV according to user; exact width/height, Windows Scale/DPI, window bounds, viewport, devicePixelRatio, zoom and active breakpoint UNKNOWN. User described the initial window as too large and needing manual resize. Photographs showed Chromium, not a legacy UI. They did not establish missing CSS or a specific cause for color differences.

### Confirmed application constraints

- Host started at fixed 1380x860 DIPs, minimum 760x620. On a short/high-scale work area the initial window could be too large even when the physical screen is 1080p.
- Manifest did not declare PerMonitorV2; baseline window API reported system awareness.
- CSS app-shell minimum height 620 and fixed log minimum 390 imposed short-height limits.
- Wide connection columns had hard minimum widths; the 1080 breakpoint abruptly hid sidebar labels. Narrow hero content could force horizontal overflow.
- Baseline automated matrix: 210 page/viewport cases, one measured horizontal overflow at effective 731x411, DPR1.75 (1280x720 physical equivalent), Dashboard.

These are verified constraints, not a claim to have proved the exact combination of TV settings responsible for the brother's photograph.

### Changes (presentation only)

- `windows/app.manifest`: explicit PerMonitorV2 with true/pm fallback.
- `windows/src/UI/ChromiumWindow.xaml`: allow small windows (minimum 320x300).
- `windows/src/UI/ChromiumWindow.xaml.cs`: initial dimensions limited by selected monitor WorkingArea and DPI, preserving 1380x860 when it fits; 16-DIP edge margin. On DPI changes constrain normal window dimensions to current monitor area without enlarging a user-resized window. No Windows display settings are changed.
- Explicit WebView2 ZoomFactor=1.0; no arbitrary global zoom/transform scaling.
- Display-only logging of work area, DPI, window DIP dimensions and JS viewport/DPR.
- `windows/ui/src/styles/responsive.css`: viewport-height shell, independent vertical scrolling, medium-width labeled sidebar, stacking/wrapping at narrow widths, readable wrapping technical values, constrained log/modal heights.
- `windows/ui/src/main.tsx`: load the presentation overrides last.
- `windows/ui/src/components/Sidebar.tsx`: accessible labels/tooltips retained when narrow navigation renders icons only. Navigation behavior unchanged.
- `installer/Test-Responsive.py` and `Read-Display.py`: isolated test/read-only helpers.

No colors, branding tokens or themes changed. No application settings/notification/statistics/translation logic changed. No Android, USB, Wi-Fi Direct, AEL2, SOCKS, Wintun, tun2socks, routes, DNS or VPN changes. Installer prerequisites unchanged. Legacy UI remained absent. APPS and emergency RAR untouched.

### Responsive layout

Above 1200 CSS pixels: accepted wide desktop grid and 236px sidebar retained.
721–1200: 184px labeled sidebar, single-column connection/dashboard layout and two-column metric cards; controls wrap where needed.
At/below 720: compact accessible icon navigation at top and vertically scrollable main content.
At/below 420: one-column actions/metrics.
At/below 720 CSS pixels height: reduce header and decorative spacing; do not shrink the whole application. Normal vertical scrolling is expected when content is taller.

### Test evidence

Fresh frontend production build: PASS, 53 modules, no reported warnings/errors. Fresh self-contained x64 Windows publish: PASS. One warning WFAC010: WinForms analyzer recommends API DPI setup; this is a WPF application with a WinForms tray helper. WPF/Windows manifest DPI declaration is intentional; the warning was not globally suppressed. Native verification pending.

Final isolated Chromium matrix: 245 cases, zero measured horizontal overflows or controls extending beyond the viewport horizontally. Six pages plus both Connection USB and WFD views, seven physical-size classes, five DPR cases: 1280x720, 1366x768, 1600x900, 1680x1050, 1920x1080, 2560x1440, 3840x2160; DPR 1.0, 1.25, 1.5, 1.75, 2.0. Effective CSS viewport = physical size / DPR.

Evidence: `FINAL_SETUP_VALIDATION/ResponsiveFinalMatrix/matrix.json` and selected PNGs. Test browser has an isolated no-op bridge; it never calls real networking commands. Tests do not prove actual Windows DPI transitions or physical TV rendering.

### Native launch issue — do not hide

The original installed instance (PID4060 during test) remained alive after a normal CloseMainWindow request. Candidate launch at 17:33 logged correct local work-area measurement (1680x1002, DPI96, 1380x860 DIPs), but WebView2 initialization failed with HRESULT 0x8007139F while the baseline instance still held the shared profile. Concurrent-profile/environment conflict is a hypothesis, not yet a proven cause. User was asked to Exit Net Relay completely before retrying a single instance. No force-kill of elevated applications or deletion of WebView2 data was performed.

Single-instance retry after the user exited the old instance succeeded at 17:36:52. Before launch, process enumeration returned no NetRelay.exe instance. Candidate PID13956 initialized WebView2, then uiReady at 17:36:54.148, and hid the loading panel. Measured native window: 1380x860 pixels, client 1364x821, DPI96 / Scale100%, normal window, per-monitor awareness (API value 2), versus baseline system awareness (1). Measured WebView2 ZoomFactor=1, JS viewport=1364x821, devicePixelRatio=1.

The disappearance after exiting the baseline supports a concurrent-environment conflict for the earlier failure; it is not a reproduced underlying WebView2 diagnosis. Native single-instance startup: PASS. Both physical visual acceptances remained pending at snapshot time.

### Candidate

`FINAL_SETUP_VALIDATION/NetRelay-Windows-1.0.0-Setup-ResponsiveCandidate.exe`
SHA256: `8BC6D9015B94C0B22521CB73C0D0E93D7E0D385C0958913BC69F54306AF0802C`
Signed with existing identity `D7B5736B8CF01BFE6E90301F74BE8022013418F5`.
Android APK unchanged: `ACD3DD5E5DCE2AC27CF9EF1DE213A366D88E5B99BDA5D3C03A447AF27F85798C`.

### Acceptance status at snapshot time

720p/768p, 1080p, 1440p, 4K simulated layout: PASS for measured horizontal checks.
Original desktop physical appearance: PENDING user confirmation.
Brother/plasma physical appearance: PENDING user confirmation.
Actual mixed-DPI monitor movement: NOT TESTED.
Single-instance candidate WebView2 startup: PASS (17:36:54 uiReady).
Native candidate viewport/DPR/zoom: 1364x821 / 1 / 1.0.
Overall: BLOCKED until both visual acceptances pass. APPS remained unchanged.

### Primary references

- https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process
- https://github.com/microsoft/WPF-Samples/blob/main/PerMonitorDPI/readme.md
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.wpf.webview2.zoomfactor?view=webview2-dotnet-1.0.2849.39

Windows recommends manifest DPI declaration; WebView2 zoom defaults to 1.0. No claim is made that application layout code can normalize all TV color processing, camera exposure, hardware sharpness or cropped physical display pixels.
