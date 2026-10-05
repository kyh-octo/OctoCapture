# OctoCapture 1.6.1 signing release

This patch signs the existing 1.6.0 application; no capture features changed.

## Rebuild
Set process environment variables OCTO_SIGNTOOL, OCTO_SIGN_DLIB and
OCTO_SIGN_METADATA to existing absolute file paths. Configure Azure CLI
authentication externally and add its bin directory to the process PATH.
Never commit signing metadata, credentials or authentication caches.

Run installer/build-installer.ps1 -Signed -SkipWebsite.
For the existing release workflow, use installer/release.ps1 -Signed -SkipWebsite.
The signing command uses Microsoft Artifact Signing with SHA256 and the
RFC3161 timestamp service http://timestamp.acs.microsoft.com.

Only OctoCapture.exe, its installer and embedded uninstaller are signed.
Third-party binaries are not re-signed. Signed builds fail on signing or
signature/timestamp verification errors. SHA256SUMS.txt is generated beside
the installer. User settings are preserved on uninstall/reinstall.

## Validation boundary
Release build: zero warnings/errors. Application, installer and pre-embedding uninstaller Authenticode
and timestamp validation passed. Isolated per-user installation, installed app launch and uninstall passed. Installed app/uninstaller signatures and timestamps were valid. User settings and a data sentinel remained after uninstall; original settings and startup registration were restored. Full capture/recording interaction was not re-tested in this signing-only patch.

The repository's own LICENSE is MIT. The installer includes LICENSE, THIRD-PARTY-NOTICES.md and original licenses for ScreenRecorderLib and the included .NET runtimes. FFmpeg is an optional external download, not bundled. No license terms were changed.

A valid signature identifies the publisher. It does not guarantee that
Windows SmartScreen will omit reputation warnings.