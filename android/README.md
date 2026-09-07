# NetRelay Android

The Android app supports USB and Wi-Fi Direct. `ConnectionService` owns the active transport, control listener, AEL2 socket gateway and foreground notification.

Key ports:

- `8988`: control listener.
- `8991`: AEL2 TCP/UDP gateway.

Build and test:

```powershell
.\gradlew.bat testDebugUnitTest assembleDebug
```

Debug APK: `app/build/outputs/apk/debug/app-debug.apk`.
