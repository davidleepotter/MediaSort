# Code Signing

MediaSort Release builds are automatically Authenticode-signed with the
self-signed certificate at [`signing/MediaSort-CodeSign.pfx`](signing/MediaSort-CodeSign.pfx).

> ⚠️ **This is a development-only certificate.** It is committed to the repo
> for convenience so any contributor can produce a signed Release build. It
> is **not** trusted by Windows by default — SmartScreen will still warn
> users who download the EXE unless they import the public certificate (see
> [Trusting the cert on a dev box](#trusting-the-cert-on-a-dev-box) below).
> Replace this with a real CA-issued cert before any public release.

## How it works

`MediaSort/MediaSort.csproj` contains a `SignMediaSortExe` MSBuild target
that runs `AfterTargets="Build"` and signs `MediaSort.exe` with `signtool.exe`
from the Windows SDK. The target is gated on:

- `Configuration == Release`
- `OS == Windows_NT` (no-op on Linux/macOS)
- `SkipCodeSign != true`
- The PFX file exists at the expected path

If `signtool.exe` cannot be found (no Windows SDK installed) the build emits
a warning and produces an unsigned EXE — it does **not** fail.

CI (`.github/workflows/build.yml`) already builds `--configuration Release`
on `windows-latest`, so every push to `main` produces a signed binary with
no extra configuration.

## Cert details

| Field | Value |
| --- | --- |
| Subject | `CN=David Potter, O=David Potter, C=US` |
| Thumbprint | `5CAFAF6D0D9A2A8E7795857BFD19A20E99459A40` |
| Validity | 2026-04-30 → 2031-04-30 (5 years) |
| Algorithm | RSA, SHA-256 |
| Timestamp | DigiCert RFC 3161 (`http://timestamp.digicert.com`) |
| PFX password | `test` |

## Trusting the cert on a dev box

Run **PowerShell as your user** (admin not required for `CurrentUser` stores)
from the repo root:

```powershell
# Import the private key into your personal store (lets you sign with it).
$pwd = ConvertTo-SecureString -String "test" -AsPlainText -Force
Import-PfxCertificate `
    -FilePath "signing\MediaSort-CodeSign.pfx" `
    -Password $pwd `
    -CertStoreLocation Cert:\CurrentUser\My

# Trust the public cert as a root CA so Windows accepts the chain.
Import-Certificate `
    -FilePath "signing\MediaSort-CodeSign.cer" `
    -CertStoreLocation Cert:\CurrentUser\Root

# Mark it as a trusted publisher so SmartScreen / UAC stop warning.
Import-Certificate `
    -FilePath "signing\MediaSort-CodeSign.cer" `
    -CertStoreLocation Cert:\CurrentUser\TrustedPublisher
```

Windows will prompt to confirm the root-store import the first time —
that's expected.

## Verifying a signed build

After `dotnet build -c Release`:

```powershell
# Located in the Windows SDK; on PATH inside a Developer PowerShell.
signtool verify /pa /v "MediaSort\bin\Release\net9.0-windows\MediaSort.exe"
```

You can also right-click the EXE → **Properties** → **Digital Signatures**
tab and confirm `David Potter` is listed as the signer with a valid
timestamp.

## Build overrides

| Flag | Effect |
| --- | --- |
| `-p:SkipCodeSign=true` | Skip signing entirely (Release builds remain unsigned). |
| `-p:SignPfxPassword=…` | Use a different PFX password (for production certs). |
| `-p:SignPfxPath=…` | Point at a different PFX file (absolute or repo-relative). |
| `-p:SignTimestampUrl=…` | Use a different RFC 3161 timestamp authority. |
| `-p:SignDescription=…` | Override the `/d` description string baked into the signature. |

Examples:

```powershell
# Local Release build with signing disabled.
dotnet build MediaSort.sln -c Release -p:SkipCodeSign=true

# Release build with a non-test cert.
dotnet build MediaSort.sln -c Release `
    -p:SignPfxPath=C:\certs\real-ev-cert.pfx `
    -p:SignPfxPassword=$env:CERT_PW
```

## Replacing the dev cert with a real one

1. Obtain a code-signing cert from a public CA (DigiCert, Sectigo, SSL.com,
   etc.) — ideally an EV cert on a hardware token to avoid SmartScreen
   reputation warnings.
2. Either:
   - Export it as PFX and pass `-p:SignPfxPath=… -p:SignPfxPassword=…` from
     CI (using a GitHub Actions secret), **or**
   - Switch the `Exec` command in `MediaSort.csproj` to use
     `/sha1 <thumbprint>` against an HSM/Azure Key Vault signer.
3. Delete `signing/MediaSort-CodeSign.pfx` from the repo and rotate any
   exposed credentials.
