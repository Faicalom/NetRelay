# Open-Source Preparation Report

Date: 2026-09-01

## Scope

The public-source copy was created from the validated private Net Relay workspace. The original workspace was not modified.

## Removed from the public copy

- Windows and Android build outputs.
- APK, EXE, DLL, installer, portable, and archived release artifacts.
- Runtime diagnostics, Logcat captures, PID files, and device dumps.
- Local SDK paths and IDE caches.
- Node modules, Gradle caches, frontend `dist`, and .NET `bin/obj` trees.
- Private signing identities, passwords, certificates, and release backups.
- Historical one-off debugging reports and superseded build snapshots.
- Redistributable third-party binaries; reproducible preparation replaces them.
- The discontinued TV Box target and unused Windows discovery/heartbeat stubs.
- Retired product names, package identifiers, log prefixes, and signing-variable prefixes.

## Added for contributors

- MIT license.
- Build, cleanup, and dependency-preparation scripts.
- GitHub Actions build workflow.
- Security policy, contributing guide, third-party notices, and focused documentation.
- Ignore and line-ending policies.
- Release-hygiene checks, CodeQL scanning, dependency review, and Dependabot configuration.

## Verification performed before final cleanup

- React/TypeScript production UI build: PASS; npm audit reported 0 vulnerabilities.
- Android unit tests and debug APK build: PASS.
- Windows Release build: PASS, 0 errors, 0 warnings.
- WLAN active-scan logic tests: PASS.
- Wi-Fi Direct endpoint isolation tests: PASS.
- AEL2 frame integrity stress tests: PASS.
- AEL2 backpressure harness: PASS.
- Product UI notification harness: PASS.
- Non-ASCII repository path build relay: PASS.
- Android package identity `com.netrelay.connectivity`: PASS.
- Retired product-name scan: PASS, zero findings.
- Release hygiene and credential scan: PASS.

All generated test outputs were removed again after verification.
