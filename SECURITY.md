# Security

Ferry is designed for one person's devices on a trusted local network. It is not an internet-facing service.

## Safe use

- Use Ferry only on a home or otherwise trusted LAN.
- Do not forward its port, expose it through a tunnel, or run it on public Wi-Fi.
- Treat the pairing QR and link as secrets: either grants access to the thread and transferred files.
- Rotate the pairing token from Settings if a pairing link may have been shared.
- Keep Windows and the .NET runtime current.

Ferry uses plain HTTP because the phone connects directly to a local IP address. The pairing token prevents casual unauthenticated access, but it does not provide transport encryption.

## Reporting

Please use GitHub's private vulnerability reporting for the repository. Do not include pairing links, tokens, transferred files, or personal thread content in a report.
