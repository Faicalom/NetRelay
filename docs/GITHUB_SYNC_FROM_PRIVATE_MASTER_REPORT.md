# GitHub Sync from Private Master

Date: 2026-09-02

- Direction: canonical private master to GitHub-clean source.
- Queue-counter race correction synchronized: YES.
- Active WLAN scan fix retained: YES.
- WebView2 LocalAppData handling retained: YES.
- Elevation/UAC handling retained: YES.
- Private signing files, passwords, APPS, development caches, and build outputs copied: NO.

## Verification

- Android unit tests and Debug assembly: PASS.
- Windows Release build: PASS, 0 errors, 0 warnings.
- WLAN active scan logic tests: PASS.
- Wi-Fi Direct endpoint state tests: PASS.
- AEL2 frame integrity tests: PASS.
- AEL2 backpressure harness: PASS.
- Product UI notification harness: PASS.
- Release hygiene: PASS.
- GitHub secrets: 0.

The GitHub-clean source remains a source-only publication tree. Private development and signing material remains exclusively in the canonical private master.
