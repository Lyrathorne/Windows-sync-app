# DeviceSync Manual Pairing Test

Status: physical Android + Windows end-to-end run is pending.

Physical E2E: pending

## Automated Loopback Coverage

The Windows integration suite includes a loopback security flow that exercises the production TCP server, protocol serializers, pairing session manager, auth handler, heartbeat responder, and file trust storage without CameraX, mDNS, TLS, multicast, or a physical phone.

Covered automatically:

1. QR payload parse, `pairing.request`, HMAC validation, `pairing.challenge`, matching verification code, both user confirmations, Android ECDSA verification by Windows, `pairing.accepted`, Windows ECDSA verification by Android, `pairing.complete_ack`, and Active trust.
2. Authenticated reconnect on a fresh TCP connection through `connection.hello`, `auth.challenge`, Windows signature verification, `auth.response`, Android signature verification, `auth.accepted`, and heartbeat ping/pong.
3. Windows-side revoke while a session is active, session shutdown, reconnect rejection with `TRUST_REVOKED`, and no heartbeat after rejection.
4. Security rejections for changed identity key, consumed pairing session reuse, wrong pairing HMAC, pending trust auth, invalid auth signature, repeated `auth.response`, and heartbeat before `auth.accepted`.

## Setup

1. Start the Windows DeviceSync app.
2. Confirm the TCP server is listening on the configured port.
3. Confirm mDNS advertises `_devicesync._tcp.local` and `pairingAvailable=false`.
4. On Windows, click `Add phone`.
5. Confirm the QR code is visible and mDNS now advertises `pairingAvailable=true`.
6. Start the Android DeviceSync app on the same local network.
7. Open `Add computer`.
8. Confirm discovery can see the Windows computer.

## Pairing

1. Tap `Scan QR code` on Android.
2. Grant camera permission if prompted.
3. Scan the QR code shown on Windows.
4. Confirm Android leaves scanning and shows pairing progress.
5. Confirm Windows receives `pairing.request`.
6. Confirm Windows sends `pairing.challenge`.
7. Confirm both screens show the same six digit code, formatted as `000 000`.
8. Tap `Matches` on Android.
9. Click the matching confirmation on Windows.
10. Confirm both Android and Windows show an active trust record.

## Reconnect

1. Close both apps.
2. Start Windows DeviceSync.
3. Start Android DeviceSync.
4. Confirm reconnect without QR starts only for active trusted records.
5. Confirm heartbeat begins only after `auth.accepted`.

## Security Cases

1. Try connecting from an unknown Android identity and confirm Windows returns `PAIRING_REQUIRED`.
2. Try the same Android device ID with a different key and confirm `IDENTITY_KEY_CHANGED`.
3. Confirm no UI offers a "connect anyway" bypass.

## Revoke

1. Pair successfully.
2. Break the TCP connection.
3. Confirm reconnect runs `connection.hello -> auth.challenge -> auth.response -> auth.accepted`.
4. Confirm heartbeat ping/pong resumes after `auth.accepted`.

## Windows Revoke

1. Remove the phone in the Windows trusted phones list.
2. Confirm the active session closes.
3. Confirm the next Android reconnect receives `TRUST_REVOKED`.
4. Confirm Android stops reconnecting and requires a new QR pairing.

## Android Revoke

1. Open the Android computer details screen.
2. Tap `Remove trust`.
3. Confirm the active session closes.
4. Confirm reconnect does not start automatically.
5. Confirm a new QR pairing is required.

## Changed Key

Use only a debug/test identity provider.

1. Keep the same Device ID.
2. Replace the test identity key.
3. Confirm Windows returns `IDENTITY_KEY_CHANGED`.
4. Confirm no authenticated session is created.
5. Do not change production identity keys for this test.

## Local Revoke Rule

Revoke is local. Removing trust on one side does not silently delete the other side's trust record. A new QR pairing is required to trust the device again.
