# Automatic incoming files

Status: implemented for Android → Windows  
Version: P10, 2026-07-15

Automatic receiving is an optional policy layer over File Transfer V1/V2. It does
not weaken transport authentication or any filesystem guarantee.

## Safe defaults

- The global automatic-receive switch is off after installation or migration.
- Every paired device starts in `AlwaysAsk` mode with a 25 MiB automatic limit.
- Automatic rules may be restricted to an explicitly approved network.
- A network fingerprint combines the Windows network-adapter identifier and observed
  gateways. SSID alone is not trusted.
- A missing, revoked or replaced trusted identity is rejected even if an old policy
  remains stored.

## Per-device modes

| Mode | Behaviour |
|---|---|
| `AlwaysAsk` | Show the incoming-file decision window for every offer. |
| `AutoUpToLimit` | Automatically accept files up to the configured limit; ask above it. |
| `AutoKnownTypes` | Automatically accept only a known safe MIME/extension pair under the limit. |
| `Never` | Reject all incoming file offers from that device. |

If `approved networks only` is enabled and the current network is not approved,
an otherwise automatic offer falls back to an explicit prompt.

## Content safety

Executables, installers, scripts, shortcuts, registry files, disk images,
macro-enabled Office documents and executable MIME types are blocked before a
`.part` file is created. Known automatic types compare both extension and MIME type.
Unknown non-executable types can only be automatic in `AutoUpToLimit`; the user is
responsible for choosing that broader mode.

All accepted transfers retain:

- 100 MiB protocol maximum and free-space validation;
- safe basename, reserved-name and traversal protection;
- unique non-overwriting destination names;
- streaming `.part` writes and SHA-256 verification;
- close/flush before atomic commit;
- cleanup on checksum failure, cancellation, disconnect or trust revocation.

The incoming-transfer window is displayed after an automatic acceptance so the user
can monitor and cancel it. Completion produces a Windows tray notification. DeviceSync
never automatically opens a received file.

## Trust changes during transfer

Trust is checked at offer time, for every chunk, and before final commit. Revoking the
sender or changing its mode to `Never` fails the transfer with `TRUST_REVOKED`, closes
the stream and deletes the partial file.
