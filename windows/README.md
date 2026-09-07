# NetRelay Windows

The Windows client supports USB and Wi-Fi Direct. Both transports feed the same AEL2 client and local data plane.

Key endpoints:

- `127.0.0.1:8990`: local SOCKS5 endpoint used by tun2socks.
- USB data forward: `127.0.0.1:8992` to Android `8991`.
- Wi-Fi Direct data endpoint: Android P2P address on `8991`.

Build:

```powershell
dotnet build .\NetRelay.csproj -c Release
```
