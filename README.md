# Unogram — Telegram client for Windows 10 Mobile

A native Telegram client for Windows 10 Mobile, built on [TDLib](https://github.com/tdlib/td).
Not a browser wrapper — a local UWP application.

**Target platform:** Windows 10 Mobile 1703 (build 15063) and 1709 (build 15254), ARM only.

**Upstream repository:** <https://github.com/nallion/tdlib_wp10>

---

## Requirements

| Component | Version | Notes |
|---|---|---|
| Visual Studio | 2017, **15.9** | "Universal Windows Platform development" workload |
| Windows 10 SDK | **10.0.15063.0** | Install via the VS Installer |
| Device | W10M 1703 / 1709 | Developer mode enabled |

For building TDLib from source you also need `git`, `cmake`, `perl`, `7z`, and `wget` on `PATH`.

### Why 15063 and not 16299

Windows 10 Mobile 1709 is OS build **15254**, from the `feature2` branch. It forked from
1703 (build 15063) and never received the desktop Fall Creators Update API surface.
10.0.15063.0 is therefore the newest SDK whose APIs actually exist on the device.

Do not raise `TargetPlatformMinVersion` above `10.0.15063.0` — the manifest `MinVersion`
must stay at or below `10.0.15254.0` or the package will not install on a phone at all.

---

## Step 1 — Build TDLib for ARM UWP

**This comes first.** The app links against `tdjson.dll` compiled for ARM UWP; nothing
else will build or run without it.

```
build_tdlib.bat
```

The script clones TDLib and vcpkg, builds OpenSSL 1.1.1w for ARM UWP, generates the
cross-compilation scaffolding with an x64 native pass, then builds `tdjson.dll` for ARM.
Expect it to take a while — the OpenSSL and TDLib compiles are the bulk of it.

Before running, open the file and check the paths at the top:

```bat
set PROJECT_DIR=C:\projects\td
set VCPKG_DIR=C:\tools\vcpkg
set OPENSSL_DIR=C:\openssl-arm-uwp
set VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\VC\Auxiliary\Build\vcvarsall.bat
```

`VS_PATH` assumes the **Community** edition — change it to `Professional` or `Enterprise`
if that's what you have, or the OpenSSL step fails at `vcvarsall.bat`.

The script pins vcpkg to commit `281d107`. Don't update it casually; newer vcpkg dropped
working `arm-uwp` triplet support.

On success:

```
C:\projects\td\build\Release\tdjson.dll
```

Copy it, plus the native dependencies, into `TelegramWP10\`:

```
tdjson.dll
zlib1.dll        (from vcpkg, arm-uwp triplet)
libwebp.dll      (WebP sticker decoding)
libsharpyuv.dll  (libwebp dependency)
```

Prebuilt copies of all four are committed to the repository. If they work for you, you can
skip this step entirely — rebuild only when you want a newer TDLib. Note that
`build_tdlib.bat` installs only zlib via vcpkg; `libwebp` and `libsharpyuv` need
`vcpkg install libwebp:arm-uwp` separately.

> `build.sh` is **not** a build script despite the name — it's a git add/commit/push
> helper that generates a random commit message. It has nothing to do with TDLib.

---

## Step 2 — Create a signing certificate if you do not want to use existing .pfx certificate

The repository ships a working test certificate, `TelegramWP10_TemporaryKey.pfx`, already
wired into the csproj. **If you're happy to use it, skip to step 3** — nothing to do.

It was created through the **Packaging** tab of `Package.appxmanifest`, so it carries no
password and MSBuild can read the key straight out of the file — a fresh clone builds and
signs with no setup at all.

It's a self-signed test certificate for sideloading only. Anyone with the repository can
sign packages as `CN=TelegramWP10`, so treat any package signed with it as untrusted
unless you built it yourself.

If a build ever fails with a missing-key error, put it in your certificate store:

```powershell
Import-PfxCertificate -FilePath "<project folder>\TelegramWP10\TelegramWP10_TemporaryKey.pfx" `
  -CertStoreLocation Cert:\CurrentUser\My
```

MSBuild looks for `PackageCertificateThumbprint` in `Cert:\CurrentUser\My` first and falls
back to the key file, so either the store entry or the `.pfx` on disk is enough.

### Using your own certificate instead

The certificate subject must exactly match `Identity/@Publisher` in
`Package.appxmanifest` (`CN=TelegramWP10`), or signing fails with a publisher mismatch.

Easiest route: open `Package.appxmanifest` → **Packaging** tab → **Choose Certificate** →
**Create test certificate**, leaving the password blank. Visual Studio writes the `.pfx`,
sets `PackageCertificateKeyFile` and `PackageCertificateThumbprint` in the csproj, and
installs the key into `Cert:\CurrentUser\My`.

Manually, if you prefer:

```powershell
$cert = New-SelfSignedCertificate `
  -Type Custom `
  -Subject "CN=TelegramWP10" `
  -KeyUsage DigitalSignature `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -NotAfter (Get-Date).AddYears(10) `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}Subject Type:End Entity")

$dir = "<project folder>\TelegramWP10"
$pw  = ConvertTo-SecureString -String "12345" -Force -AsPlainText
Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath "$dir\TelegramWP10_TemporaryKey.pfx" -Password $pw
Export-Certificate    -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath "$dir\TelegramWP10_TemporaryKey.cer" -Type CERT
```

If you intend to commit your certificate the way this repository does, use the Visual
Studio route and leave the password blank — a password-protected `.pfx` forces everyone
who clones to import it manually before their first build.

Then put the new thumbprint into `PackageCertificateThumbprint` in the csproj. Whichever
certificate you use, the matching `.cer` is what has to go into the phone's Trusted Root
store — see step 4.

---

## Step 3 — Build

Open `TelegramWP10.sln` in Visual Studio 2017. Only `Debug|ARM` and `Release|ARM` exist;
the project fails deliberately on any other platform.

`GenerateAppxPackageOnBuild` is on, so every build produces a package — no separate
"Create App Packages" run needed:

```
TelegramWP10\AppPackages\TelegramWP10_<version>_ARM_Test\TelegramWP10_<version>_ARM.appx
```

Verify the signature:

```
signtool verify /pa /v "...\TelegramWP10_<version>_ARM.appx"
```

It should report `CN=TelegramWP10`.

### Versioning

`AppxAutoIncrementPackageRevision` is enabled, so the fourth field of `Identity/@Version`
in `Package.appxmanifest` increments on each build: `1.0.0.0` → `1.0.0.1` → `1.0.0.2`.
The manifest is rewritten in place, so it will show as modified in `git status` after
every build.

To set a different version, edit the first three fields in `Package.appxmanifest` by hand
and let the revision counter restart beneath them. For an exact version with no increment:

```
msbuild TelegramWP10.sln /p:Configuration=Release /p:Platform=ARM /p:AppxAutoIncrementPackageRevision=False
```

Versions must increase — sideload upgrades fail if the new package is older than what's
installed.

---

## Step 4 — Deploy to the phone

Enable **Settings → Update & security → For developers → Developer mode** on the device.

**From Visual Studio** — plug in the phone, select it as the deployment target, press F5.
This registers loose files rather than installing a package, so certificate trust is not
involved. Fastest iteration loop, and the recommended path during development.

**Installing the `.appx` directly** — the certificate must first be in the phone's
**Trusted Root** store. A self-signed certificate is its own root, so anything less
produces `0x800B0109`. Tapping a `.cer` attachment often lands it in the personal store
instead; a provisioning package is more reliable:

1. Windows Configuration Designer → new project → Advanced provisioning
2. Runtime settings → Certificates → **RootCertificates** → point at `TelegramWP10_TemporaryKey.cer`
3. Export as a `.ppkg`, copy to the phone, tap, accept, reboot

With Interop Tools installed, its certificate manager can write to Trusted Root directly.

Then:

```
WinAppDeployCmd devices
WinAppDeployCmd install -file "...\TelegramWP10_<version>_ARM.appx" -dependency "...\Dependencies\ARM\Microsoft.NET.Native.Framework.2.0.appx" -g <device-guid>
```

`WinAppDeployCmd.exe` lives in `C:\Program Files (x86)\Windows Kits\10\bin\10.0.15063.0\x86\`.
Release builds use the .NET Native toolchain and need the framework packages from
`AppPackages\...\Dependencies\ARM\` — they are not fetched automatically on a sideload.

---

## Project configuration reference

| Property | Value | Why |
|---|---|---|
| `TargetPlatformVersion` | `10.0.15063.0` | Newest API surface present on W10M 1709 |
| `TargetPlatformMinVersion` | `10.0.15063.0` | Excludes 1511 (10586) and 1607 (14393) |
| `TargetDeviceFamily` | `Windows.Mobile` | Phones only — no desktop, HoloLens or Xbox |
| `AppxBundle` | `Never` | Plain `.appx`, not a bundle |
| `AppxBundlePlatforms` | `ARM` | ARM only |
| `UapAppxPackageBuildMode` | `SideloadOnly` | Not going through the Store |
| `Microsoft.NETCore.UniversalWindowsPlatform` | `6.0.15` | 6.1.x / 6.2.x require min version 16299 |

The `EnforceArmOnlyMobile` target fails the build if the platform isn't ARM or if
`TargetPlatformMinVersion` has drifted — usually the sign that a Visual Studio retarget
prompt was accepted by accident.

`Windows.Mobile` means the package will not install on a Windows 10 PC for quick testing.
Switch to `Windows.Universal` if you want that; the version bounds do the 1703/1709
filtering either way.

---

## Troubleshooting

**"This version of Visual Studio is unable to open the following projects"**

`ProjectTypeGuids` in the csproj must be
`{A5A43C5B-DE2A-4C0C-9213-0A381AF9435A};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}`,
and the project GUID in the `.sln` must match `<ProjectGuid>` in the csproj.
Delete `.vs\` before reopening — Visual Studio caches failed loads.

**"Couldn't process file ... due to its being in the Internet or Restricted zone"**

Mark of the Web, from extracting a downloaded ZIP:

```powershell
Get-ChildItem -Path "<project folder>" -Recurse -File | Unblock-File
```

Then delete `bin`, `obj`, `.vs`. Cloning with git instead of downloading a ZIP avoids this.

**App isn't trusted / `0x800B0109`**

Either the package isn't signed (check `signtool verify`), or the certificate isn't in the
phone's Trusted Root store. See step 4.

**Signing fails with a publisher mismatch**

The certificate subject and `Identity/@Publisher` in `Package.appxmanifest` must match
character for character. Check with:

```powershell
Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq "<thumbprint>" | Select-Object Subject
```

**NuGet restore fails with NU1202**

`Microsoft.NETCore.UniversalWindowsPlatform` 6.1.x and 6.2.x require
`TargetPlatformMinVersion` 10.0.16299 or higher, which W10M can't run. Stay on 6.0.x.

---

## Credits

Unogram is developed by [nallion](https://github.com/nallion). The upstream repository —
source, releases and issue tracker — is at
<https://github.com/nallion/tdlib_wp10>. Report bugs there rather than here.

Built on [TDLib](https://github.com/tdlib/td) by the Telegram team, licensed under
Boost Software License 1.0.
