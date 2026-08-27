# Security Policy

Muhun MCSV Manager 1.0 uses a production fail-closed security model. A build is distributable only after the formal signed-release verifier succeeds; source-tree or unsigned development builds are not production artifacts.

## Security boundaries

- The Windows Service is the only authority allowed to mutate managed Server state.
- Public traffic must terminate at an approved HTTPS tunnel and reach a loopback-only listener.
- Every REST, event-stream, IPC, update and Provider operation must be authorized by the Service.
- Secrets must not be placed in command lines, logs, events, crash reports, API responses or Provider manifests.
- Update and Provider packages require a cryptographic hash and a trusted publisher signature before activation.
- Release manifests use detached RSA-PSS-SHA256 signatures with an RSA key of at least 3072 bits. Windows binaries and management scripts additionally require SHA-256 Authenticode from the same pinned publisher certificate and a trusted timestamp.
- Self-signed certificates provide project-key continuity only. They are not public CA trust and do not bypass Microsoft SmartScreen; the installer rejects them until the local Windows trust policy explicitly reports the Authenticode signature as valid.
- Signing PFX, encrypted provider private-key files, and their DPAPI-protected password files must remain outside the repository with an ACL limited to the release identity and `SYSTEM`.

## Release signing

See [正式產品簽章與安全發布](docs/正式產品-簽章與安全發布.md) for the fail-closed release, verification, installation and rollback process. The scripts do not add certificates to a Windows trust store and do not execute an installer as part of release generation.

## Reporting

Use this repository's **Security → Advisories → Report a vulnerability** flow to submit a private report. Do not place account credentials, remote URLs containing secrets, Webhook tokens, private keys or unredacted logs in a public issue.

Include the affected product version, package hash, impact, and minimal reproduction steps. Redact player names, IP addresses, local paths and all credentials before attaching logs or screenshots.
