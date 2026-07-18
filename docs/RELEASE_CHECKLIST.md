# DeviceSync 1.0.0 release checklist

## Required automated gates

- [ ] Windows `dotnet test windows/DeviceSync.sln -c Release`
- [ ] Android `testReleaseUnitTest`, `lintRelease`, `assembleRelease`, `bundleRelease`
- [ ] Protocol vectors pass on both platforms.
- [ ] No debug endpoints or unrestricted content logging in release.
- [ ] SBOM/dependency inventory and open-source notices reviewed.
- [ ] SHA-256 recorded for every distributed artifact.

## Signing and secrets

- Android signing values are supplied only through `DEVICESYNC_ANDROID_KEYSTORE`,
  `DEVICESYNC_ANDROID_STORE_PASSWORD`, `DEVICESYNC_ANDROID_KEY_ALIAS` and
  `DEVICESYNC_ANDROID_KEY_PASSWORD`. The key is never stored in the repository.
- Windows Authenticode signing is not performed automatically. It requires explicit approval and a
  release-owner certificate outside the repository.
- Do not publish debug APKs or unsigned installers as stable releases.

## Upgrade / rollback

- App settings, trusted identities and transfer history are stored outside the Windows installation
  directory and are preserved during upgrade/uninstall.
- Windows autostart is opt-in in both application settings and installer tasks.
- Verify firewall rule creation/removal on a clean machine before release.
- Keep the previous signed installer and checksums available for rollback.
- Android Room/DataStore migrations must be tested from the last public version; never use
  destructive migration for trusted identities.

## Manual gates

- [ ] Every applicable `REAL_DEVICE_TEST_MATRIX.md` row is PASS or has an accepted documented risk.
- [ ] Windows clean install, upgrade, uninstall and reinstall.
- [ ] Android clean install and upgrade using the same release key.
- [ ] Crash/restart produces a privacy-safe diagnostic bundle.
- [ ] Changelog and protocol compatibility table are published with the artifacts.
