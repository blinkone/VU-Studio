# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

VU Studio is an open-source (GPL-3.0) replacement for the official VU-Server. It drives Streacom VU1 dials: an analog needle, an RGBW backlight and a 200×144 one-bit e-paper face. It talks **directly** to the VU1 hub over USB serial and pulls data from the local PC, LAN devices and online/home-automation sources.

The UI ships in Italian and English. Read `docs/requirements.md` for product decisions, `docs/architecture.md` for the design and `docs/vu1-protocol.md` for the hub's serial protocol.

## Commands

```bash
dotnet build VuStudio.slnx                       # whole solution, including Windows-only projects
dotnet test --project tests/VuStudio.Tests       # all tests (Microsoft.Testing.Platform, enabled in global.json)
dotnet test --project tests/VuStudio.Tests -- --filter-class "VuStudio.Tests.Core.ValueMappingTests"
dotnet test --project tests/VuStudio.Tests -- --filter-method "*Hold_keeps_peak"
```

* **xUnit v3 on MTP.** `dotnet test` must use `--project`. Filters go after `--`. The legacy VSTest mode is not supported with the .NET 10 SDK.
* **Windows-only projects build on Linux.** `VuStudio.Tray` (WinForms/WebView2, `net10.0-windows10.0.19041.0`) builds there because `EnableWindowsTargeting=true` is set in `Directory.Build.props`. It can only *run* on Windows.
* **Cloud containers lack the .NET SDK.** Install it with `apt-get install -y dotnet-sdk-10.0` (Ubuntu feed). `builds.dotnet.microsoft.com` is blocked by the egress proxy; nuget.org and npm work.
* **Package versions are central.** They live in `Directory.Packages.props`. Project files reference packages without versions.

## Architecture (big picture)

### Processes

| Process | Project | Role |
|---|---|---|
| Service | `src/VuStudio.Server` | ASP.NET Core host, Windows Service (LocalSystem) or console. The **only owner of the serial port**. Runs providers, engine, history, REST/WebSocket API, and serves the React UI (`web/`). |
| Tray | `src/VuStudio.Tray` | Shows the UI in WebView2. Acts as the **user-session agent** and pushes `session/*` sensors (audio, media, foreground app, idle/lock, accent color) to the service over `/api/session`. Runs user scripts so they never execute as SYSTEM. |
| Agent | `src/VuStudio.Agent` | Small HTTP service for other machines: Unraid Docker, Steam Deck, Raspberry Pi, other PCs. The service polls `GET /api/v1/sensors`. |

### Data flow and its contracts

The core contracts live in `src/VuStudio.Core`:

1. **Providers** implement `ISensorProvider.RunAsync(sink, status, ct)`.
   * They are created by an `IProviderFactory` whose `ProviderDescriptor` declares a settings schema (`SettingField[]`); the UI renders forms from it.
   * `PollingSensorProvider` is the base class for interval sampling.
   * `ProviderManager` runs each instance, restarts it with backoff, and reconciles instances when the config changes.
2. **`SensorHub`** stores metadata plus the latest value and a version per sensor.
   * Sensor ids are `{providerInstanceId}/{localPath}`.
   * Providers pass **local** ids to their `ISensorSink`; the hub adds the prefix.
   * Consumers **poll** the hub with `GetChangedSince(version)` or `GetNumber`. There are no per-update events, by design.
   * Values are always in **canonical units** (`Sensors/Units`). `Mapping/UnitConverter` handles display units (°F, Mbit/s, GB…).
3. **Engine** (20 Hz) works per slot: active `Profile` → `DialAssignment` → current `DialPage`.
   * **Needle:** `ValueBinding` → `ValueMapping` (in display units) → `Smoother`.
   * **Light:** `BacklightConfig` → `BacklightEngine.Compute` (gradient/thresholds/sync, alarms, effects).
   * **Face:** `FaceConfig` → `IFaceRenderer` → `MonoBitmap`.
   * Results go to `IDialOutput`.
   * The scale labels and the needle share the same `ValueMapping`, so labels always line up with the needle.
4. **`IDialOutput`** (implemented by `VuStudio.Hardware`'s `DialController`) is a *desired-state* sink. Setters only record targets, and a single serial worker sends diffs (latest value wins). Needle and backlight updates are interleaved between e-paper image chunks.
5. **Faces** are `FaceDocument`s made of polymorphic `FaceLayer`s: scale, text, icon, image, shape, bar, sparkline.
   * Themes generate documents from `FaceConfig`. The editor materialises them into `FaceConfig.Custom`.
   * Text uses `TextTemplate` placeholders (`{value}`, `{unit}`, `{time:HH:mm}`, `{sensor:<id>}`…) resolved by `FaceRenderContext`.
   * Rendering (`VuStudio.Rendering`) uses SkiaSharp with fonts and icons embedded from `src/VuStudio.Rendering/Assets`.

### Conventions that span files

* **JSON** always goes through `Config/Json.Options`: camelCase, string enums, nulls omitted. Polymorphic types use a `"type"` discriminator.
  * `RgbwColor` serializes as `"#RRGGBB"` or `"#RRGGBB+WW"` (WW = white LED).
  * Backlight device levels (`LedLevels`) are 0–100 per channel.
* **Profiles address dials by slot** (1-based position). `HardwareSettings.Dials` maps slots to dial UIDs, so swapping hardware does not break profiles.
* **Needle geometry** (pivot 99.5,225.5 px; −40°…+40°) is `Faces/NeedleGeometry`. It was measured from the stock VU1 faces and is configurable.
* **`ConfigStore`** hands out an immutable snapshot (`Current`). Edits go through `Update(mutate)` or `Replace(config, baseRevision)` and are saved atomically with a `.bak` copy.
* **Windows-only code** lives in `VuStudio.Sources.Windows` (`net10.0`, guarded by `OperatingSystem.IsWindows()` at registration) or in the Tray project. The service stays a single portable TFM and also runs on Linux.
* **Localization.** Provider and theme strings shown in the UI use `LocalizedText(En, It)`. UI strings live in the web app's locale files.
