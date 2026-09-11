# Changelog

## 0.15.0 — Passage

Ferry is now a single-process native Windows app with an in-process Kestrel server and a matching Android browser client.

### Highlights

- Replaced the Node server and launcher with one WPF host.
- Added the Passage object-current interface across Windows and mobile web.
- Added text and file transfer, Pin, Draft, Read state, Upload queue, image previews, notifications, themes, cleanup, and pairing.
- Added a stable per-user Windows install, Start menu shortcut, autostart, and tray behavior.
- Preserved the original HTTP, WebSocket, and SQLite wire contract with conformance coverage.

### Release status

This is a personal beta for trusted local networks. It is not hardened for public Wi-Fi or internet exposure, and the Windows executable is unsigned.
