# DeviceSync TLS Pinning Plan

This stage does not enable TLS and does not implement a custom TLS replacement.

The next stage should:

1. create a Windows self-signed TLS certificate;
2. bind that certificate to the Windows identity key;
3. save the certificate or public-key fingerprint on Android after pairing;
4. use TLS certificate pinning on Android;
5. let Windows verify the Android client through a client certificate or signed identity proof;
6. migrate the current authenticated handshake onto TLS;
7. remove plaintext transport after migration.

The current `PinnedDeviceIdentity` model reserves `FutureTlsCertificateFingerprint` for that migration.
