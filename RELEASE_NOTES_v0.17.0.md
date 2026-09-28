# Ferry v0.17.0

Ferry 0.17.0 adds a local command line tool and makes tray startup the default on Windows. It also changes where data lives on PCs with a ready fixed `D:` drive.

## Changes

- `ferryctl.exe` ships in the Windows ZIP. From the extracted folder, run `.\ferryctl.exe status`; the same command supports `send-text`, `send-file`, `recent`, `read`, and `pull`. The source installer also adds `ferryctl` to your path. Sends require another Ferry device to be connected and return `phone_not_connected` if none is present. Results are one JSON line, with errors on stderr. Interrupted pulls leave no partial download.
- A ready fixed `D:` drive becomes the default home for Ferry's database and received files at `D:\Ferry\data`. On first launch, Ferry copies an existing LocalAppData store, verifies the copied files, then switches to the new location. Portable `data` folders and `FERRY_DATA_DIR` overrides keep their current precedence. Save As starts in `D:\Ferry\Downloads` on those PCs.
- Windows file rows have **Copy path** for the local stored file.
- Ferry starts in the tray, including when Windows launches the EXE without arguments. Use the tray's **Show Ferry** item or `Ferry.exe --show-window` to open the main window. **Show QR** opens the pairing code in a small window that closes after 30 seconds.
- Local automation can read connected devices through authenticated `GET /api/presence`. The existing phone protocol and SQLite schema are unchanged.

## Install or upgrade

1. Download `ferry-windows-x64-v0.17.0.zip` and its `.sha256` file from this release.
2. Verify the ZIP checksum, then extract the complete archive. Keep `public` beside `Ferry.exe`.
3. Run `Ferry.exe`. It starts in the tray; choose **Show Ferry** or **Show QR** there. Run `Ferry.exe --show-window` if you want the main window immediately.
4. If you use the source installer, rerun `npm run install:windows` to update its Start menu shortcut, autostart entry, and `ferryctl` command.

The archive contains `Ferry.exe`, `ferryctl.exe`, and the phone client. It is self-contained for Windows x64 and does not need a separate .NET runtime. Ferry remains an unsigned beta for trusted local networks; do not expose its HTTP port to the internet or public Wi-Fi.
