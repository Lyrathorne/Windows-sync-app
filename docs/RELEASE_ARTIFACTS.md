# Local release artifacts — DeviceSync 1.0.0

Generated on 2026-07-16. These artifacts have not been published.

| Platform | Artifact | Signing status | SHA-256 |
|---|---|---|---|
| Windows x64 | `windows/artifacts/publish/win-x64/DeviceSync.App.exe` | Unsigned local self-contained publish | `3C93F2C8F0BDCC4927955560B0D688C49F8D1C3D9BD02D04D283DD0967464802` |
| Windows x64 | `windows/artifacts/DeviceSync-1.0.0-win-x64-portable.zip` | Contains unsigned executable | `C44F4C39EACFF2A0C4706E01E1CCC67403B2EA9431FF4A4E9B93EBEC0CB3741E` |
| Android | `app/build/outputs/apk/release/app-release-unsigned.apk` | Unsigned | `E3CCB9644E15A163118B55EAA9B9B585265C7E375E2F22E41F53D476CDC6C8DA` |
| Android | `app/build/outputs/bundle/release/app-release.aab` | Unsigned (`jarsigner` verified no signature) | `8C01B9D661649892593834AE5F659A92CEC4C02AC623BC4C4D62B2E0AA706E07` |

Windows file version is `1.0.0.0`; product version is `1.0.0` plus the deterministic source
revision suffix.

The Inno Setup source exists at `windows/installer/DeviceSync.iss`, but `ISCC.exe` is not installed
on this workstation, so no installer was produced. External Android/Windows signing was
intentionally not attempted.
