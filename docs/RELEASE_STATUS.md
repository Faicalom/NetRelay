# Release Status

Net Relay 1.0.0 is complete for its supported Android phone and Windows scope. Supported transports are USB and Wi-Fi Direct. Bluetooth, Android TV, and TV Box targets are outside the product scope.

The private development workspace validated Net Relay 1.0.0 with USB and Wi-Fi Direct, both with normal Android Internet and phone VPN egress. Those runtime artifacts, device captures, installers, signing identities, and historical debug reports are intentionally not part of this public source tree.

Build success is not a substitute for runtime validation. Every release should repeat:

- USB normal and VPN runtime gates.
- Wi-Fi Direct normal and VPN runtime gates.
- DNS, HTTPS, browser, and NCSI checks.
- AEL2 reader/writer, queue, heartbeat, and session-reset checks.
- VPN disconnect fail-closed and reconnect behavior.
- Installer and clean-machine dependency validation.
