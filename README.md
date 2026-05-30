# Ferry

Ferry is a self-hosted, LAN-only chat thread for sending notes and files between your phone and laptop.

It runs on your laptop, opens in the phone browser, and keeps a persistent local history. There is no cloud service, no account, and no external sync provider.

## Features

- Send text and files between devices on the same local network.
- Keep persistent history in SQLite, with uploaded files stored on the laptop.
- Preview image uploads as thumbnails and open them in a lightbox.
- Scan a QR code from the phone instead of typing the laptop IP address.
- Switch between light, dark, and system themes.
- Set the device name shown beside messages.
- Preview and clean up old uploaded files from Settings.
- Open or reveal laptop-side files directly from supported desktop browsers on Windows.

## Quick Start

```powershell
npm install
npm start
```

Open the printed LAN URL on your phone, or open `http://localhost:8787` on the laptop. Use the Connect button to show a QR code for the current LAN address.

## How It Works

The laptop is the server and each device is a browser client. Ferry uses HTTP for the app and file transfers, WebSocket for live updates, SQLite for message metadata, and `data/files/` for uploaded file blobs.

The runtime is intentionally small: Node.js, `ws`, built-in `node:sqlite`, vanilla HTML/CSS/JavaScript, and a vendored offline QR generator.

## Security Scope

Ferry is designed for a trusted home LAN.

There is currently no authentication. Anyone on the same network who can reach the Ferry URL can read messages, post messages, upload/download files, and trigger host-side open/reveal actions. Do not expose Ferry to untrusted networks, public Wi-Fi, or the internet, and do not port-forward it.

Shared-token authentication is the top roadmap item before wider use.

## Storage

Message metadata is stored in `data/flow.db`. Uploaded files are stored in `data/files/`. The `data/` directory is ignored by git.

Settings includes a cleanup preview for deleting uploaded files older than a selected age. Message history stays visible, but deleted file blobs become unavailable.

## Development

```powershell
npm start
```

The default port is `8787`. Override it with `PORT` if needed:

```powershell
$env:PORT=8790
npm start
```

## Publishing Note

If this project has ever contained personal helper scripts, private notes, real device IDs, or local paths in git history, publish Ferry from a fresh orphan branch or a new repository containing only the app files:

- `server.js`
- `package.json`
- `package-lock.json`
- `public/`
- `.gitignore`
- `README.md`
- `LICENSE`

Do not publish private helper scripts, internal notes, local data, or old branch history.

## Credits

Ferry vendors `public/vendor/qrcode.js`, based on `qrcode-generator` by Kazuhiko Arase, under the MIT License.

## License

MIT. See `LICENSE`.
