# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

An Android TeamSpeak voice client. Two projects in `MobileTS.sln`:

- **MobileTS** — `net9.0-android` app (`OutputType=Exe`, min API 26). Uses the **native Android UI stack** (`Activity`, `Service`, `RecyclerView`, XML layouts in `Resources/layout/`) — *not* .NET MAUI / XAML. Bundles `libopus.so` per-ABI (`lib/<abi>/`).
- **TSLib** — `net9.0` class library, a vendored copy of the TeamSpeak 3/5 client library from [Splamy/TS3AudioBot](https://github.com/Splamy/TS3AudioBot). Pinned to `LangVersion=8.0`. Treat as an upstream dependency: prefer changing MobileTS over editing TSLib.

Code comments and UI strings are mostly in Russian.

## Build / run

No test project exists. Build via the .NET Android SDK:

```powershell
dotnet build MobileTS.sln                              # build both projects
dotnet build MobileTS/MobileTS.csproj -t:Run           # build, deploy & launch on attached device/emulator
```

In Visual Studio: open `MobileTS.sln`, set `MobileTS` as startup, deploy to an Android device/emulator (the project requires a real Android target — it won't run on desktop).

### Package ids, versioning & signing (Debug vs Release side-by-side)

[MobileTS.csproj](MobileTS/MobileTS.csproj) deliberately gives the two configurations **different package ids and signing keys** so they install next to each other on one device:

| | `ApplicationId` | Launcher label | Signing key |
|---|---|---|---|
| **Release** | `ru.errorovich.MobileTS` | `MobileTS` | `MobileTS/mobilets-release.keystore` (alias `mobilets`), creds in `signing.props` |
| **Debug** | `ru.errorovich.MobileTS.debug` | `MobileTS debug` | Android auto debug key |

- Version is `ApplicationDisplayVersion` (`versionName`, currently **0.1.1**) + integer `ApplicationVersion` (`versionCode`); bump both for releases.
- The `.debug` suffix and label come from config-conditional `PropertyGroup`s + an `${appLabel}` `AndroidManifestPlaceholders` value (Debug overrides it). Different ids mean the two builds never collide on (re)install — no signature-mismatch uninstall dance between them.
- **FileProvider authority is per-package** (`${applicationId}.fileprovider` in the manifest; computed at runtime as `context.PackageName + ".fileprovider"` in [LogShare.cs](MobileTS/Logging/LogShare.cs)). Hardcoding one authority would make the second install fail with `INSTALL_FAILED_CONFLICTING_PROVIDER` — provider authorities must be globally unique across installed apps.
- **Release signing secrets stay out of git.** The keystore (`*.keystore`) and `signing.props` (path + passwords) are gitignored; the csproj just does `<Import Project="signing.props" Condition="Exists(...)" />`. Copy [signing.props.sample](MobileTS/signing.props.sample) → `MobileTS/signing.props` and fill in your values, or pass the same `AndroidSigning*` props on the command line in CI (`-p:` overrides the import; restore the keystore from a CI secret). **Without** `signing.props`/`-p:`, Release falls back to the auto debug key — buildable by anyone who clones, but not a stable update identity. Keep the keystore file safe: losing it means a new id/key (this is the standard OSS posture — source is public, the signing key is not).

### Release APK (for sideloading)

Produce a signed, installable APK:

```powershell
dotnet publish MobileTS/MobileTS.csproj -c Release -f net9.0-android -p:AndroidPackageFormat=apk
```

- Output: `MobileTS/bin/Release/net9.0-android/publish/ru.errorovich.MobileTS-Signed.apk` (`-Signed.apk` is the one to distribute; the unsigned `.apk` next to it is not). Install via `adb install <apk>` or by opening it on-device.
- To build a signed APK without a device and install it manually (robust against flaky ADB): `dotnet build MobileTS/MobileTS.csproj -c Release -f net9.0-android -t:SignAndroidPackage` → `bin/Release/net9.0-android/ru.errorovich.MobileTS-Signed.apk` → `adb install -r <apk>`.
- **Debug APK gotcha:** Debug keeps assemblies *outside* the APK (fast deployment), so a bare `adb install` of a Debug `-Signed.apk` native-crashes on startup. Either deploy via `-t:Install` (MSBuild pushes the assemblies) or build the APK self-contained with `-p:EmbedAssembliesIntoApk=true` before `adb install`.
- **For Play** (not the sideload key above): publish `-p:AndroidPackageFormat=aab` and override the signing with your own `AndroidSigningKeyStore`/`AndroidSigningKeyAlias`/`AndroidSigningStorePass`/`AndroidSigningKeyPass`.
- **ABIs:** only `arm64-v8a` + `x86_64` (matches the bundled `libopus.so` per-ABI) — 32-bit-only devices (`armeabi-v7a`) are not supported.
- Release uses `TrimMode=full`; smoke-test on a real device after publishing (full trimming can break reflection paths that Debug tolerates — e.g. `System.Text.Json` reflection is stripped, so serialized types need a source-generated `JsonSerializerContext`, see [AppJsonContext.cs](MobileTS/AppJsonContext.cs)). Verify the package with the SDK build-tools: `apksigner verify <apk>`.
- Reinstalling over an install of the **same** id with a **different** key fails on signature mismatch — uninstall first (this wipes `SharedPreferences`: saved servers + identity). Debug and Release have different ids, so they don't trigger this against each other.

#### Per-ABI & universal APKs (MSBuild targets in [MobileTS/Release.targets](MobileTS/Release.targets))

```powershell
dotnet build MobileTS/MobileTS.csproj -t:ReleaseAll        # all three -> dist/
dotnet build MobileTS/MobileTS.csproj -t:ReleaseArm64      # arm64-v8a only (phones)
dotnet build MobileTS/MobileTS.csproj -t:ReleaseX64        # x86_64 only (emulators)
dotnet build MobileTS/MobileTS.csproj -t:ReleaseUniversal  # both ABIs in one APK
```

Builds into `dist/` (gitignored): `MobileTS-<ver>-arm64-v8a.apk` (~7.5 MB), `…-x86_64.apk` (~7.6 MB), `…-universal.apk` (~12.8 MB, both ABIs). The single-ABI builds roughly halve the universal size by dropping the other ABI's native libs + assembly blob. AOT is off by default; add `-p:RunAOTCompilation=true` (forwarded to the variant builds).

- **ABI selection is driven by `-p:AbiTarget=android-arm64|android-x64`, a project-local property** — *not* `-p:RuntimeIdentifier(s)` directly. Two traps this avoids: (1) a singular `-p:RuntimeIdentifier` does **not** narrow Android ABIs (the ABI set comes from the *plural* `RuntimeIdentifiers`); (2) passing `-p:RuntimeIdentifiers` globally leaks the RID into the `TSLib` (`net9.0`) project reference and breaks it (`MSB3030`). The csproj maps `AbiTarget` → `RuntimeIdentifiers` for the app only (Release-conditioned, so Debug `-t:Run` is untouched); the universal build passes no `AbiTarget` and uses the default `android-arm64;android-x64`.
- **Each variant runs in its own `dotnet build` child process (`Exec`), not via the `<MSBuild>` task**: the Android SDK caches assembly-compression info per MSBuild *session* keyed by project path, so two builds with different `RuntimeIdentifiers` in one session die with `XABLD7009` ("compression assembly info for architecture … not available").
- All variants emit to the same `bin/Release/net9.0-android/`, so `_BuildReleaseVariant` copies each APK into `dist/` right after its build; an inline `_VerifyApkAbis` task (RoslynCodeTaskFactory) re-opens the APK and fails the build unless the `lib/<abi>/` folders match the expected ABI set exactly.
- The SDK's own `AndroidCreatePackagePerAbi=true` would do per-ABI splits in one go, but it's deprecated (warning `XA1037`, slated for removal in .NET 10) — don't switch to it.
- **versionCode is the same** across variants — fine for sideloading (you install one). For a Play multi-APK track each ABI needs a distinct, ordered `versionCode` (pass `-p:ApplicationVersion=<n>`; it is explicitly forwarded to the child builds).

### TSLib code generation (T4 / `.tt`)

Many TSLib files are generated by T4 templates and committed alongside their output (`*.gen.cs`, `Generated/*.cs`). The pairing is the `.tt` → `LastGenOutput` `.cs` in [TSLib.csproj](TSLib/TSLib.csproj). **Never hand-edit a generated `.cs`** (e.g. `Messages.cs`, `Book.cs`, `TsFullClient.gen.cs`, `TsCommand.gen.cs`) — edit the `.tt`/`.ttinclude` source and regenerate via Visual Studio's *Transform All Templates* (the `dotnet` CLI does not run these).

## Architecture

### App → library bridge: the static `Client` class

[Client.cs](MobileTS/Client.cs) is the single seam between the Android app and TSLib. It owns the one `TsFullClient` instance and the dedicated thread it runs on:

- `TsFullClient` is **single-threaded by contract**: it must only be touched from its own `DedicatedTaskScheduler` thread (TSLib's [DedicatedTaskScheduler](TSLib/Scheduler/DedicatedTaskScheduler.cs)). `Client.Connect` spins up a `Thread`, calls `DedicatedTaskScheduler.FromCurrentThread`, and constructs the client there.
- **All calls into the client must go through `Client.Invoke(...)`**, which marshals the lambda onto the scheduler thread and unwraps TSLib's `R<T,CommandError>` result type into `(bool ok, T[] data)`. Do not call `TsFullClient` methods directly from Activity/UI code.
- UI observes connection state via `TsFullClient.OnStatusChangedEvent` (`TsClientStatus`) and ready-state via `Client.SubscribeInstance` / `OnInstanceReady`. Marshal back to the UI thread with `Activity.RunOnUiThread`.

### Audio pipeline

Audio flows through a chain of `IAudioPipe` segments (`.Chain(...)` / `.Into(...)` from TSLib, see [AudioInterfaces.cs](TSLib/Audio/AudioInterfaces.cs)). The chain is wired in `Client.ClientThread`:

- **Capture:** `AudioRecordPipe` → `PreciseTimedPipe` → `EncoderPipe(OpusVoice)` → `client`
- **Playback:** `client` → `DecoderPipe` → `VoiceActivationTrackerPipe` → `AudioTrackPipe`

The MobileTS-specific pipes live in [MobileTS/Audio/](MobileTS/Audio/) and wrap Android APIs:
- `AudioRecordPipe` — Android `AudioRecord` (mic, 48 kHz mono PCM16) as an `IAudioPassiveProducer`.
- `AudioTrackPipe` — one Android `AudioTrack` per `ClientId`, routed by `meta.In.Sender`.
- `VoiceActivationTrackerPipe` — taps the decoded stream to raise `OnClientIsTalkingChanged`, surfaced on `Client.OnClientIsTalkingChanged` and used by `ServerFragment` to color talking clients green.

### Screens & lifecycle

The UI is **single-Activity + Fragments**: one host owns the navigation drawer + ActionBar so the side menu is shared by every screen and never rebuilds/flickers on navigation (a per-Activity drawer can't persist across Activity transitions). Screens are platform `Android.App.Fragment`s swapped in `content_frame`.

- [MainActivity.cs](MobileTS/Activity/MainActivity.cs) (`MainLauncher`) — host. Owns the `androidx.drawerlayout.widget.DrawerLayout` ([activity_main.xml](MobileTS/Resources/layout/activity_main.xml)) and the ActionBar "☰" home button that opens it. Drawer header lists currently connected server(s) (one for now); items are **Сервера / Настройки / Журнал**. Calls `Client.Init` + `Crypto.EnsureKey`. Navigation helpers: `ShowServersList` (root, clears back stack), `Push` (back-stack), `ShowServer` (after connect), `DisconnectCurrent`, `UpdateConnectedTitle`. The connected-server header survives rotation via `OnSaveInstanceState` (the connection itself lives in `ClientService`).
- [ServersFragment.cs](MobileTS/Activity/ServersList/ServersFragment.cs) — CRUD list of saved servers (JSON in `SharedPreferences`). Tapping a server requests `RECORD_AUDIO`, starts `ClientService`, shows a cancelable "Подключение..." `ProgressDialog` (back/cancel stops the service → aborts the connect), and on `TsClientStatus.Connected` calls `MainActivity.ShowServer`; on failure shows the disconnect reason.
- [ClientService.cs](MobileTS/Services/ClientService.cs) — foreground `Service` (`ForegroundServiceType.Microphone`) that owns the live connection so voice keeps running in the background. The `ServerInfo` is passed via intent extra as JSON; it decrypts passwords and calls `Client.Connect`. Stopping the service (`OnDestroy`) calls `Client.Disconnect`.
- [ServerFragment.cs](MobileTS/Activity/Server/ServerFragment.cs) — channel/client tree via `RecyclerView` with two view types (`ChannelItem` / `ClientItem`), built from `Client.GetBookSnapshot()`. Adds the "Отключиться" item to the host ActionBar; "Назад" returns to the list while staying connected, disconnect is explicit.
- [SettingsFragment.cs](MobileTS/Activity/Settings/SettingsFragment.cs) / [LogFragment.cs](MobileTS/Activity/Log/LogFragment.cs) — placeholders.

### Identity & secrets

- TeamSpeak identity (ed25519 private key + key offset) is generated once via `TsCrypt` and stored in `SharedPreferences` (`ts_client`). See `Client.Init`.
- Server/channel passwords are stored **in plaintext** in `SharedPreferences` (`servers_storage`, JSON) and passed as-is across the intent boundary to `ClientService`. This is a deliberate UX trade-off (passwords survive Auto Backup / device migration; `android:allowBackup="true"`) accepted over at-rest encryption — there is intentionally no `Crypto` class. Don't reintroduce encryption without revisiting that decision.
