# Ghuboon Packaging Investigation

Status: investigation only. Implementation is intentionally deferred per
[ADR-023](../ADR.md) until at least the first beta. This document captures the
distribution paths that fit Ghuboon's stack
([ADR-001](../ADR.md): C# / .NET 10, [ADR-002](../ADR.md): Avalonia,
[ADR-015](../ADR.md): macOS first) and identifies the lowest-friction path
forward.

The MVP can still be run from `dotnet run` against the `Ghuboon.App` project.
Nothing in this document needs to land before MVP feature work is complete.

## Prerequisites

- .NET SDK `10.0.x` (current LTS-track per [ADR-001](../ADR.md)). Verify with:

  ```bash
  dotnet --version
  # expect 10.0.x
  ```

- macOS 13+ on Apple Silicon for the macOS path.
- Windows 10 22H2 / Windows 11 for the Windows path (deferred — see ADR-015).
- Xcode Command Line Tools (`xcode-select --install`) for `codesign`,
  `xcrun notarytool`, `hdiutil`, `lipo`, `plutil`.
- Homebrew for `create-dmg` and other helpers.

## Foundation: `dotnet publish`

Both platforms build on top of `dotnet publish` from `src/Ghuboon.App`.

```bash
# macOS Apple Silicon
dotnet publish src/Ghuboon.App \
  -c Release \
  -r osx-arm64 \
  --self-contained true \
  -o publish/osx-arm64

# macOS Intel
dotnet publish src/Ghuboon.App \
  -c Release \
  -r osx-x64 \
  --self-contained true \
  -o publish/osx-x64

# Windows x64 (deferred)
dotnet publish src/Ghuboon.App \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -o publish/win-x64
```

`--self-contained true` ships the .NET runtime so users do not need to
install .NET 10. It is the recommended default for Avalonia desktop apps
because the runtime story across macOS and Windows is otherwise inconsistent.

### Trim and AOT

- `PublishTrimmed=true` is **not** recommended for first releases. Avalonia
  has reflection paths (XAML loader, styles, data templates) that do not trim
  cleanly; users have reported missing types at runtime. Re-evaluate once
  Avalonia 12+ ships verified trim annotations.
- `PublishAot=true` is similarly off the table for now. Some of our
  dependencies (Dapper expression trees, SQLitePCLRaw bundle initialization)
  are AOT-hostile. Revisit post-MVP.
- Ship untrimmed, self-contained publishes. Expect ~80–110 MB per platform
  before compression.

### SQLite + SQLCipher native libraries

Per [ADR-011](../ADR.md), the database is encrypted using a SQLCipher-compatible
build. The `.NET` packaging story for this is the most fragile piece of the
publish output:

- `Microsoft.Data.Sqlite` by itself does not ship an encryption-capable
  native library. Use either:
  - `SQLitePCLRaw.bundle_e_sqlcipher` — uses an unofficial SQLCipher build
    bundled by the SQLitePCLRaw maintainers; or
  - Custom-built SQLCipher native libraries dropped into the publish
    `runtimes/<RID>/native/` directory.
- Verify after publish that the appropriate native binary is present:

  ```bash
  # macOS arm64
  ls publish/osx-arm64/runtimes/osx-arm64/native/ 2>/dev/null \
    || ls publish/osx-arm64/ | grep -E 'libe_sqlcipher|libsqlcipher'

  # Windows x64
  dir publish\win-x64\runtimes\win-x64\native
  ```

- TODO:
  - [ ] Confirm which SQLCipher provider Ghuboon uses (decided in Phase 5).
  - [ ] Add a publish-time smoke test that opens the encrypted DB to catch
        missing native binaries before users do.

## macOS

### App bundle layout

A macOS `.app` is just a directory with a specific structure:

```text
Ghuboon.app/
  Contents/
    Info.plist
    MacOS/
      Ghuboon            # the published executable
      ... runtime files, dylibs
    Resources/
      AppIcon.icns
      ... other bundled resources
    PkgInfo              # optional, "APPL????"
```

Two practical ways to produce this:

#### Option A: `dotnet-bundle` (recommended for first cut)

[`dotnet-bundle`](https://github.com/egramtel/dotnet-bundle) is a global tool
that wraps `dotnet publish` and assembles a `.app`.

```bash
dotnet tool install --global dotnet-bundle

dotnet restore -r osx-arm64
dotnet msbuild src/Ghuboon.App -t:BundleApp \
  -p:RuntimeIdentifier=osx-arm64 \
  -p:Configuration=Release \
  -p:UseAppHost=true \
  -p:CFBundleName=Ghuboon \
  -p:CFBundleDisplayName=Ghuboon \
  -p:CFBundleIdentifier=com.ochanuco.ghuboon \
  -p:CFBundleVersion=0.1.0 \
  -p:CFBundleShortVersionString=0.1.0 \
  -p:NSHighResolutionCapable=true
```

Pros: declarative, integrates with MSBuild, surface area is small.
Cons: maintenance has been slow; we still need a manual `Info.plist` for
LSUIElement/LSBackgroundOnly tuning, icon embedding, and entitlements.

#### Option B: Manual bundle script

A short shell script in `scripts/package-macos.sh` (out of scope for this doc)
can:

1. Run `dotnet publish -r osx-arm64 -c Release --self-contained -o build/staging`.
2. Create the directory tree.
3. Copy the publish output into `Contents/MacOS/`.
4. Drop a hand-written `Info.plist` into `Contents/`.
5. Drop `AppIcon.icns` into `Contents/Resources/`.
6. `chmod +x Contents/MacOS/Ghuboon`.
7. (Optionally) sign and notarize.

This is the most flexible and is recommended once the app reaches beta.

### Minimal `Info.plist`

Ghuboon is a menu-bar resident app per [ADR-015](../ADR.md), but it also has
a real main window. Two flags are relevant:

- `LSUIElement=true` — hides the Dock icon and the app menu bar entirely.
  This is wrong for Ghuboon: we still want a focusable main window with a
  proper menu bar when the user opens it.
- `LSBackgroundOnly=true` — makes the app a non-UI background process. Even
  more wrong: the app cannot present any UI at all.

The right balance for "menu bar + main window" is to omit both flags and rely
on the menu bar item we register via Avalonia's `TrayIcon` (or a native
`NSStatusItem` bridge). The Dock icon will still show while the window is
visible; that is acceptable for MVP and matches apps like GitHub Desktop and
VS Code.

Re-evaluate `LSUIElement=true` if we add an explicit "hide Dock icon"
preference; that flag can be toggled at runtime via `TransformProcessType`,
but doing so during MVP is over-scope.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>Ghuboon</string>
  <key>CFBundleDisplayName</key>
  <string>Ghuboon</string>
  <key>CFBundleIdentifier</key>
  <string>com.ochanuco.ghuboon</string>
  <key>CFBundleExecutable</key>
  <string>Ghuboon</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>0.1.0</string>
  <key>CFBundleVersion</key>
  <string>0.1.0</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
  <key>NSSupportsAutomaticTermination</key>
  <false/>
  <key>NSSupportsSuddenTermination</key>
  <false/>
  <key>CFBundleURLTypes</key>
  <array/>
  <key>NSUserNotificationsUsageDescription</key>
  <string>Ghuboon shows notifications for high-priority GitHub events.</string>
</dict>
</plist>
```

Notes:

- No `LSUIElement` and no `LSBackgroundOnly` — see discussion above.
- `LSMinimumSystemVersion` `13.0` aligns with what .NET 10 / Avalonia
  actually support; bump if testing reveals lower-version regressions.
- `NSUserNotificationsUsageDescription` is optional but useful preparation
  for [ADR-021](../ADR.md) (OS notifications for high-priority reasons).

### Universal binary (arm64 + x64)

For a single bundle that runs on both Apple Silicon and Intel:

```bash
# 1. Publish for both RIDs
dotnet publish src/Ghuboon.App -c Release -r osx-arm64 \
  --self-contained -o publish/osx-arm64
dotnet publish src/Ghuboon.App -c Release -r osx-x64 \
  --self-contained -o publish/osx-x64

# 2. Stage one bundle copied from arm64
mkdir -p Ghuboon.app/Contents/MacOS Ghuboon.app/Contents/Resources
cp -R publish/osx-arm64/* Ghuboon.app/Contents/MacOS/

# 3. Replace the main executable with a fat binary
lipo -create \
  publish/osx-arm64/Ghuboon \
  publish/osx-x64/Ghuboon \
  -output Ghuboon.app/Contents/MacOS/Ghuboon

# 4. Replace each native dylib with a fat version (dlopen-ed libs included)
for arm in publish/osx-arm64/*.dylib; do
  name=$(basename "$arm")
  intel="publish/osx-x64/$name"
  if [ -f "$intel" ]; then
    lipo -create "$arm" "$intel" \
      -output "Ghuboon.app/Contents/MacOS/$name"
  fi
done

# 5. Verify
file Ghuboon.app/Contents/MacOS/Ghuboon
# expect: Mach-O universal binary with 2 architectures
```

For MVP we can ship arm64-only (the user's primary target) and add a
universal build only if Intel testers materialize. TODO:

- [ ] Decide arm64-only vs universal for first beta.
- [ ] Audit native deps (`libe_sqlcipher.dylib`, `libHarfBuzzSharp.dylib`,
      `libSkiaSharp.dylib`, `libAvaloniaNative.dylib`) so the loop above
      covers everything.

### `.dmg` distribution

#### `create-dmg` (Homebrew, recommended)

[`create-dmg`](https://github.com/create-dmg/create-dmg) wraps `hdiutil` and
AppleScript window layout into one command:

```bash
brew install create-dmg

create-dmg \
  --volname "Ghuboon" \
  --window-pos 200 120 \
  --window-size 600 400 \
  --icon-size 100 \
  --icon "Ghuboon.app" 175 190 \
  --hide-extension "Ghuboon.app" \
  --app-drop-link 425 190 \
  "Ghuboon-0.1.0.dmg" \
  "Ghuboon.app"
```

Pros: deterministic, scriptable, no GUI required. Works in CI.
Cons: AppleScript window layout fails silently if the build environment lacks
display access; build on a real macOS runner, not a headless container.

#### Plain `hdiutil`

For total control:

```bash
hdiutil create -volname "Ghuboon" \
  -srcfolder Ghuboon.app \
  -ov \
  -format UDZO \
  Ghuboon-0.1.0.dmg
```

This skips the styled background/Applications shortcut but is enough for
internal alpha distribution.

### Code signing

Required for any user who is not a developer. Without it, macOS Gatekeeper
blocks the app and the user has to right-click → Open or remove the
quarantine attribute.

#### Apple Developer prerequisites

- Apple Developer Program membership ($99/yr).
- Developer ID Application certificate (for distribution outside the App
  Store) generated via Xcode or [developer.apple.com](https://developer.apple.com).
- App-specific password or App Store Connect API key for `notarytool`.

#### Signing flow

```bash
# 1. Sign every Mach-O inside the bundle (deep), with hardened runtime
codesign --force --deep --options runtime --timestamp \
  --entitlements scripts/entitlements.plist \
  --sign "Developer ID Application: Your Name (TEAMID)" \
  Ghuboon.app

# 2. Verify
codesign --verify --deep --strict --verbose=2 Ghuboon.app
spctl --assess --type execute --verbose Ghuboon.app
```

#### Hardened runtime entitlements

Hardened runtime is required for notarization. Start minimal and only add
what runtime actually needs:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <!-- .NET JIT and Avalonia/Skia need executable pages -->
  <key>com.apple.security.cs.allow-jit</key>
  <true/>
  <key>com.apple.security.cs.allow-unsigned-executable-memory</key>
  <true/>
  <!-- Outbound HTTPS to api.github.com -->
  <key>com.apple.security.network.client</key>
  <true/>
  <!-- Avoid disabling library validation unless we hit a load failure -->
</dict>
</plist>
```

Trim this list once we confirm what each entitlement actually unlocks. .NET
on macOS is well-known to need `allow-jit`; `allow-unsigned-executable-memory`
is sometimes also needed for Avalonia. Validate empirically.

### Notarization

Required so that Gatekeeper will permit the app on first launch without
warnings.

```bash
# 1. Zip the bundle (notarytool wants a flat archive)
ditto -c -k --sequesterRsrc --keepParent Ghuboon.app Ghuboon.zip

# 2. Submit and wait
xcrun notarytool submit Ghuboon.zip \
  --apple-id "you@example.com" \
  --team-id "TEAMID" \
  --password "@keychain:AC_PASSWORD" \
  --wait

# 3. Staple the notarization ticket onto the .app and the .dmg
xcrun stapler staple Ghuboon.app
xcrun stapler staple Ghuboon-0.1.0.dmg

# 4. Verify
xcrun stapler validate Ghuboon.app
spctl --assess --type execute --verbose Ghuboon.app
```

Notes:

- Use an [App Store Connect API key](https://developer.apple.com/documentation/appstoreconnectapi)
  in CI (`--key`, `--key-id`, `--issuer`) instead of an app-specific password.
- Notarization can take anywhere from 30 seconds to 30 minutes; build CI with
  generous timeouts.

### macOS auto-update

- [Sparkle](https://sparkle-project.org/) is the macOS standard, but it is
  Swift/Objective-C and embeds itself as an `NSObject`. There is no first-class
  .NET binding. Marshaling it through P/Invoke is possible but high-effort.
- [Squirrel.Mac](https://github.com/Squirrel/Squirrel.Mac) is unmaintained and
  similarly ObjC-bound.
- [Velopack](https://github.com/velopack/velopack) supports macOS as of late
  2024 and is the only practical .NET-friendly option. Production maturity on
  macOS is still less than on Windows; revisit before committing.
- Recommendation: **defer auto-update until v1.0**. For pre-v1, ship a
  notification-on-startup that points users to the GitHub Releases page.

## Windows

Windows support is post-MVP per [ADR-015](../ADR.md). This section captures
options so the eventual Phase 17+ work can move quickly.

### Installer options

| Tool       | License   | Output  | Strengths                                       | Weaknesses                                  |
|------------|-----------|---------|-------------------------------------------------|---------------------------------------------|
| Inno Setup | Free      | `.exe`  | Simple Pascal-like script; great UX defaults    | Not store-friendly; older UI feel           |
| MSIX       | Free      | `.msix` | Modern, sandboxed, store distribution           | More restrictive runtime; needs MakeAppx    |
| WiX 5      | Free/OSS  | `.msi`  | Production-grade, group-policy installable      | XML-heavy; learning curve                   |

#### Inno Setup (recommended first cut)

Lowest friction. Authoring a small `.iss` script is enough:

```ini
[Setup]
AppId={{REPLACE-WITH-GUID}}
AppName=Ghuboon
AppVersion=0.1.0
DefaultDirName={autopf}\Ghuboon
DefaultGroupName=Ghuboon
OutputBaseFilename=Ghuboon-Setup-0.1.0
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs

[Icons]
Name: "{group}\Ghuboon"; Filename: "{app}\Ghuboon.exe"
Name: "{commondesktop}\Ghuboon"; Filename: "{app}\Ghuboon.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Additional icons:"
```

Build with `iscc Ghuboon.iss`.

#### MSIX

Better long-term story (sandboxing, clean uninstall, Microsoft Store). Requires:

- An `AppxManifest.xml`.
- `MakeAppx pack` to assemble.
- `signtool sign` with a code-signing certificate (self-signed for sideload
  testing; CA-issued for distribution).

Worth revisiting once we want Microsoft Store presence.

#### WiX 5

Use only if we hit Inno Setup limitations (per-user install, complex MSI
upgrade rules, group policy deployment).

### Windows code signing

- Authenticode certificate from a CA: DigiCert, Sectigo, SSL.com, etc.
- Standard OV cert: ~$200/yr; SmartScreen reputation builds slowly (~30 days
  of installs).
- EV cert: ~$300–$500/yr; instant SmartScreen reputation; requires a hardware
  token (USB) per the 2023 CA/B baseline change. Cloud HSM signing
  (Azure Key Vault, DigiCert KeyLocker) is now the practical path for CI.
- Sign with `signtool`:

  ```powershell
  signtool sign /tr http://timestamp.digicert.com /td sha256 ^
    /fd sha256 /a Ghuboon.exe
  signtool sign /tr http://timestamp.digicert.com /td sha256 ^
    /fd sha256 /a Ghuboon-Setup-0.1.0.exe
  ```

- Recommendation: skip code signing entirely for the first internal alpha;
  sign once a cert is provisioned for beta.

### Windows auto-update

- [Velopack](https://github.com/velopack/velopack) (formerly Squirrel.Windows
  successor) is the most ergonomic .NET option. Supports delta updates,
  background download, and a one-line API:

  ```csharp
  var mgr = new UpdateManager("https://example.com/ghuboon-releases");
  var updateInfo = await mgr.CheckForUpdatesAsync();
  ```

- Built-in Windows auto-update via MSIX is also possible if we go the MSIX
  route (App Installer + `<UpdateSettings>`).
- Recommendation: **defer until v1.0**, same as macOS.

## Cross-platform tooling notes

- `dotnet publish --self-contained true` is the single foundation for both
  platforms.
- Trim/AOT: leave `PublishTrimmed=false`, `PublishAot=false`. See foundation
  section.
- Native deps (SQLCipher, Skia, HarfBuzz, AvaloniaNative): always validate
  the publish output contains the expected RID-specific binaries before
  packaging.
- Single-file publishing (`PublishSingleFile=true`) is **not** recommended.
  Avalonia + SQLitePCLRaw single-file extraction has historically been
  fragile; troubleshooting beats the marginal disk savings.
- Version source-of-truth: a `Directory.Build.props` setting
  `<AssemblyInformationalVersion>` driven from a single tag or env var
  (`GHUBOON_VERSION`) is the cleanest path. Open question below.

## Recommended path forward

Ranked, smallest viable step first:

1. **macOS dev build**: `dotnet publish -r osx-arm64 -c Release --self-contained`
   plus a manual `.app` bundle script (`scripts/package-macos.sh`). Viable
   today, no Apple Developer account required.
2. **`.dmg` recipe**: `create-dmg` against the unsigned `.app`. Ship to early
   testers with documented "right-click → Open" gatekeeper bypass.
3. **Apple Developer + Developer ID** when first public beta is ready. Add
   `codesign --options runtime --timestamp --deep` and `notarytool submit`
   to the pipeline, and `stapler staple` both `.app` and `.dmg`.
4. **Windows path**: ignore until Phase 17+. When picked up, start with Inno
   Setup against `dotnet publish -r win-x64`.
5. **Auto-update**: punt entirely until v1.0. Until then, surface a
   "new release available" hint that links to GitHub Releases.

## Open questions / next steps

- [ ] Bundle ID convention. Proposal: `com.ochanuco.ghuboon`. Confirm before
      first signed build (changing it later breaks Keychain entries per
      [ADR-007](../ADR.md) / [ADR-011](../ADR.md)).
- [ ] Version source-of-truth. Proposal: `Directory.Build.props` with
      `<AssemblyInformationalVersion>` derived from a Git tag or
      `GHUBOON_VERSION` env var, fed into `CFBundleShortVersionString` and
      Inno Setup's `AppVersion`.
- [ ] Release hosting. Proposal: GitHub Releases (free, integrates with the
      public repo per [ADR-024](../ADR.md), provides stable URLs for any
      future auto-update manifest).
- [ ] macOS Catalyst / Mac App Store: Avalonia does not support Catalyst
      directly. Ignore for MVP; revisit only if MAS distribution becomes a
      product requirement.
- [ ] Native binary inventory: enumerate every dylib/so/dll under
      `runtimes/<RID>/native/` so packaging scripts catch them all.
- [ ] CI runner choice for macOS notarization (GitHub-hosted `macos-14` is
      adequate; verify `notarytool` and `create-dmg` availability).
- [ ] Decide arm64-only vs universal for first beta.

## Out of scope for this document

- Auto-update implementation (deferred until v1.0).
- Final installer art / branding (icons, DMG background, EULA text).
- Crash reporting (Sentry, App Center). [ADR-012](../ADR.md) covers
  structured logging only; remote crash telemetry is a separate decision.
- Linux distribution. Not on the roadmap.
- Microsoft Store / Mac App Store distribution.

## References

- Apple: [Notarizing macOS software before distribution](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)
- Apple: [Hardened Runtime entitlements](https://developer.apple.com/documentation/security/hardened_runtime)
- Apple: [Information Property List Key Reference](https://developer.apple.com/documentation/bundleresources/information_property_list)
- `create-dmg`: <https://github.com/create-dmg/create-dmg>
- `dotnet-bundle`: <https://github.com/egramtel/dotnet-bundle>
- Inno Setup: <https://jrsoftware.org/isinfo.php>
- WiX Toolset 5: <https://wixtoolset.org/>
- MSIX overview: <https://learn.microsoft.com/en-us/windows/msix/>
- Velopack: <https://github.com/velopack/velopack>
- .NET runtime identifiers: <https://learn.microsoft.com/en-us/dotnet/core/rid-catalog>
