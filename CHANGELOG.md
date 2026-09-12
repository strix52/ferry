# Changelog

## 0.16.0 — Crossing

Reliability and reach. Everything here removes a gesture or a failure mode in a flow that
already existed; nothing adds a concept to learn.

### Phone client

- The tab reconnects when it comes back to the foreground instead of waiting for the socket to
  notice it died. Unlock the phone, switch to Ferry, and the thread is current. Both reconnect
  loops now back off with jitter rather than retrying at a fixed interval forever.
- Filter the thread by sender, filename, or text.
- Pick a whole folder to send. `webkitdirectory` works on Chrome for Android 132 and up.

### Windows client

- New media gallery. The header's image button opens every picture in the thread as a grid,
  newest first, paged 120 at a time. Clicking a tile opens the existing viewer, so arrow keys
  still page through the run. It reads the full thread rather than the filtered view.
- Paste a screenshot or a copied file straight into the composer. Clipboard images are stamped
  with a timestamp so a thread of screenshots stays tellable apart after download.
- Twenty photos from the phone arrive as twenty messages and used to fire twenty toasts. Inbound
  notifications now coalesce into one summary after arrivals stop, and replace each other in
  Action Center rather than stacking.
- The pinned strip no longer shifts its heading from centre to left when expanded.
- Filter the thread by sender, filename, or text.

### Server

- `GET /api/download/:id` honours `Range`, so a large video seeks and resumes instead of
  restarting. Additive amendment to the wire contract; the conformance suite gained six checks
  and lost none.

### Release status

Unchanged from 0.15.0: a personal beta for trusted local networks, unsigned, plain HTTP, not
hardened for public Wi-Fi or internet exposure.

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
