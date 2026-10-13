# VU Studio — Architecture

## 1. Processes

```
                    ┌──────────────────────────── Windows PC ────────────────────────────┐
                    │                                                                     │
 Browser (LAN) ──►  │  VU Studio Service (VuStudio.Server, Windows Service, LocalSystem)  │
                    │   ├─ Web API + WebSocket + static UI  (http://127.0.0.1:5341)       │
 Tray app  ──WS──►  │   ├─ Engine (profiles, pages, mapping, backlight, faces, night)     │
 (VuStudio.Tray,    │   ├─ Providers (LibreHardwareMonitor, HWiNFO, RTSS, network,        │
  user session)     │   │             ping, HTTP, MQTT, Home Assistant, agents, …)        │
   ├─ WebView2 UI   │   ├─ History (memory ring + SQLite)                                 │
   ├─ audio/media/  │   └─ DialController ──USB serial──► VU1 hub ──I²C──► 4 dials       │
   │  foreground/   │                                                                     │
   │  idle/accent   └─────────────────────────────────────────────────────────────────────┘
   └─ scripts (user context)
                                   ▲ HTTP (pull)
 LAN: VU Studio Agent (Unraid Docker, Steam Deck, other PCs, Raspberry Pi) · Glances ·
      Prometheus · LHM web server · Home Assistant · MQTT broker · UPnP router
```

* **Service** (`VuStudio.Server`) — an ASP.NET Core generic host.
  * Runs as a Windows Service (LocalSystem, so LibreHardwareMonitor and ETW have the privileges they need), or as a console process for development and on Linux.
  * It is the **only owner of the serial port**.
* **Tray app** (`VuStudio.Tray`, WinForms + WebView2) — starts at user logon.
  * Shows the UI in a native window and offers quick actions: profile switch, pause, open in browser.
  * Acts as the **session agent**: it collects data that only exists in the interactive session (audio meters, media session, foreground app, idle/lock, accent color), runs user scripts and shows notifications.
  * Talks to the service over a local WebSocket (`/api/session`).
* **Web UI** (`web/`, React + TypeScript + Vite) — built into static files and served by the service. The same UI runs in WebView2 and in any browser.
* **Agent** (`VuStudio.Agent`) — a small cross-platform HTTP service for other machines. It exposes their sensors at `GET /api/v1/sensors`, and the service polls it. Distributed as a single-file binary and a Docker image.

## 2. Solution layout

| Project | TFM | Responsibility | Depends on |
|---|---|---|---|
| `VuStudio.Core` | net10.0 | Domain model, configuration, sensor hub, provider framework, value mapping, backlight engine, face *model*, profiles, engine loop, history abstractions | — |
| `VuStudio.Hardware` | net10.0 | VU1 protocol, transports (serial, simulator), hub discovery, `DialController` (`IDialOutput`) | Core |
| `VuStudio.Rendering` | net10.0 | SkiaSharp face renderer (`IFaceRenderer`), themes, fonts, icons, dithering | Core |
| `VuStudio.Sources` | net10.0 | Cross-platform providers (clock, demo, system/Linux, network, ping, process, HTTP-JSON, MQTT, Home Assistant, weather, crypto, push, agent, Glances, Prometheus, LHM-remote, UPnP) | Core |
| `VuStudio.Sources.Windows` | net10.0 (`[SupportedOSPlatform("windows")]`) | LibreHardwareMonitor, HWiNFO shared memory, RTSS | Core |
| `VuStudio.Server` | net10.0 (Web SDK) | Host, DI wiring, REST/WebSocket API, auth, static UI, SQLite history, Windows service integration | all of the above |
| `VuStudio.Tray` | net10.0-windows10.0.19041.0 | Tray, WebView2 window, session collectors | Core (DTOs) |
| `VuStudio.Agent` | net10.0 | Remote agent | Core, Sources, Sources.Windows |
| `tests/VuStudio.Tests` | net10.0 | xUnit tests | all libraries |

Windows-only code is guarded with `OperatingSystem.IsWindows()` at registration time. The service therefore stays a single portable TFM.

## 3. Data flow

```
Provider ──Define/Publish──► SensorHub ──(latest values, versions)──► Engine tick (20 Hz)
                                 │                                       │
                                 ├──► HistoryRecorder (1 s ring, 1 min SQLite)
                                 └──► LiveHub (WebSocket, batched)       ▼
                                                    per dial: page → needle % → IDialOutput
                                                              light → RGBW   → IDialOutput
                                                              face → MonoBitmap (when changed) → IDialOutput
                                                                                   │
                                                                     DialController scheduler
                                                                     (latest-wins, interleaved
                                                                      image chunks) → serial
```

### 3.1 Sensors

* A sensor has a globally unique id `"{providerId}/{localPath}"`, for example
  `lhm/cpu/0/temperature/1`, `session/audio/peak`, `agent-unraid/disk/sdb/temp`.
* Provider instance ids match `[a-z0-9-]+`.
* `SensorInfo` holds:
  * the name, plus the device/host (`null` = this PC),
  * the category (`Cpu`, `Gpu`, `Memory`, `Storage`, `Network`, `Cooling`, `Power`, `Battery`, `Motherboard`, `Audio`, `Media`, `Game`, `Process`, `Time`, `Weather`, `Finance`, `Home`, `System`, `Custom`),
  * the kind (`load`, `temperature`, `clock`, `power`, `fan`, `throughput`, `data`, `fps`, `level`, …),
  * the canonical unit, the value kind (`Number` | `Text` | `Color`), the suggested min/max and an icon.
* Canonical units are `%`, `°C`, `MHz`, `W`, `V`, `A`, `RPM`, `B`, `B/s`, `ms`, `s`, `fps`, `dB`, `Wh`, `L/h` and `""`. Display units (°F, Mbit/s, GB, GHz…) are conversions applied at presentation.
* `SensorHub` is thread-safe. It keeps metadata, latest value, timestamp and a monotonically increasing version per sensor. Consumers **poll** it (engine tick, WebSocket batching), so update storms never fan out as events.

### 3.2 Providers

* `IProviderFactory` publishes a `ProviderDescriptor`:
  * type, name, description,
  * whether it is a singleton, whether it is enabled by default, which platforms it supports,
  * its **settings schema** (`SettingField[]`). The UI renders the forms generically from that schema.
* The factory creates an `ISensorProvider` for each configured instance (`ProviderInstanceConfig`: id, type, name, enabled, JSON settings).
* `ProviderManager` starts every enabled instance on a long-running task. It restarts failed instances with backoff and reconciles running instances whenever the configuration changes.
* Providers publish through an `ISensorSink` scoped to their own id prefix.

### 3.3 Engine

The engine ticks every 50 ms. For each **slot** (a physical dial position, mapped to a dial UID in hardware settings) it:

1. Picks the active **profile**. Manual override wins. Otherwise the highest-priority enabled profile whose conditions hold is used, and the default profile always matches.
2. Picks the current **page** of the slot's assignment, rotating every N seconds when it has several pages.
3. **Needle**: `ValueBinding`, then the sensor value, then `ValueMapping` (linear/log/sqrt/piecewise, range, invert), then smoothing (attack/release, peak hold), giving a percent 0–100. When the source is offline, the needle follows the configured offline behaviour.
4. **Light**: the `BacklightConfig` mode (fixed/gradient/thresholds/sync), then alarms (with hysteresis and hold time), then the effect (breathe/pulse/blink), then brightness, then the night-mode attenuation, giving RGBW levels 0–100.
5. **Face**: the face config and its context go to `IFaceRenderer`, which returns a `MonoBitmap`. A new bitmap is sent only when its hash changed, and at most once per `MinRefreshSeconds`; a page or profile switch sends it immediately.
6. Writes the targets to `IDialOutput`. A `DialStateSnapshot` per slot feeds the UI preview.

### 3.4 Hardware

`DialController` implements `IDialOutput` and owns the `VuHubClient`.

* **Connection.** It discovers the hub port, opens it, enumerates dials and reconnects with backoff.
* **Scheduling.** A single worker loop sends *desired-state diffs*, latest value wins, so the queue never grows. Priority order:
  1. needles,
  2. backlights,
  3. easing,
  4. face uploads.
* **Face uploads** are cut into 1000-byte frames. Pending needle and backlight diffs are interleaved between frames, so needles stay live during the multi-second e-paper refresh.
* **Simulator.** A `SimulatedHub` speaks the same protocol in memory (`--simulate 4`). It is used for development, tests and demos.

## 4. Configuration and storage

| Item | Windows | Linux |
|---|---|---|
| Config `config.json` | `%ProgramData%\VU Studio\` | `$XDG_CONFIG_HOME/vustudio/` or `/etc/vustudio/` (service) |
| Assets (uploaded images) | `%ProgramData%\VU Studio\assets\` | `…/vustudio/assets/` |
| History `history.db` | `%ProgramData%\VU Studio\` | `$XDG_STATE_HOME/vustudio/` |
| Logs | `%ProgramData%\VU Studio\logs\` | `$XDG_STATE_HOME/vustudio/logs/` |

* `config.json` is human-readable camelCase JSON with a `version` field and forward migrations.
* Writes are atomic: write a temp file, then rename. The previous file is kept as `.bak`.
* The UI edits the whole document with optimistic concurrency (`revision`).

Root shape:

```jsonc
{
  "version": 1,
  "revision": 42,
  "general":  { "language": "auto", "temperatureUnit": "C" },
  "hardware": { "port": "auto", "simulate": 0, "dials": [ { "uid": "3E00…", "slot": 1, "name": "CPU", "needleEasing": {…}, "backlightEasing": {…} } ], "geometry": { "pivotX": 99.5, "pivotY": 225.5, "startAngle": -40, "endAngle": 40 } },
  "web":      { "port": 5341, "allowLan": false, "passwordHash": null, "apiToken": "…" },
  "night":    { … },
  "history":  { "retentionDays": 30 },
  "providers": [ { "id": "lhm", "type": "lhm", "enabled": true, "settings": {} } ],
  "profiles":  [ { "id": "default", "name": "Default", "priority": 0, "activation": "always",
                   "dials": [ { "slot": 1, "rotationSeconds": 10,
                                "pages": [ { "id": "p1", "value": {…}, "light": {…}, "face": {…} } ] } ] } ]
}
```

## 5. Faces

* A **face document** is a list of layers on a 200 × 144 1-bit canvas. The layer types are:
  * `scale`: ticks, labels and zones drawn around the needle pivot, following the binding's mapping,
  * `text`: templated, e.g. `{value} {unit}`, `{title}`, `{time:HH:mm}`, `{sensor:<id>}`, `{sensor:<id>|0.0}`,
  * `icon`: Material Design Icons subset,
  * `image`: an uploaded asset with fit, threshold or dithering,
  * `shape`: rect, round rect, ellipse, line or arc,
  * `bar`: a linear gauge,
  * `sparkline`: recent history of a sensor.
* **Themes** are generators: given `ThemeOptions` and the binding context, they produce a face document. There are four families (`vintage`, `minimal`, `instrument`, `hud`), each with variants.
* The editor starts from a theme and **materialises** it into a custom document, which the user then edits freely.
* Rendering uses SkiaSharp. Vector layers draw with antialiasing either off (crisp) or on with a threshold. Image layers are dithered. The final threshold at 50 % produces the `MonoBitmap`.
* The preview PNG the UI shows is that exact bitmap, so the preview is identical to the device.
* Fonts (OFL) and icons (Apache-2.0) are bundled in `src/VuStudio.Rendering/Assets`.

## 6. Security model

* The service binds to loopback by default. LAN access (`allowLan`) is opt-in and requires a password: PBKDF2 hash, session cookie `HttpOnly; SameSite=Strict`.
* **DNS-rebinding protection.** The `Host` header must be loopback, an IP literal, the machine's host names or a configured allow-list entry.
* **Local trust.** A request is *local* (no login) only when it comes from a loopback address **and** its `Host` is `localhost`, `*.localhost`, `127.0.0.1` or `[::1]`. Machine names and `web.allowedHosts` entries are accepted but need the LAN login, so a rebound name never gets local trust.
* Every `web.*` setting (LAN access, port, allowed hosts, password, API token) can only be changed by local requests.
* A masked secret sent back by the UI is reused only if the source keeps its id, type and non-secret settings (table rows are matched by their non-secret columns); otherwise the user must type it again.
* **SSRF to itself.** Every outgoing request of the service's sources carries an `X-VuStudio-Outbound` marker (per-process random value), and the service refuses requests that carry it. An HTTP JSON source whose settings were last changed from the LAN (`changedRemotely`, set by the service) also refuses loopback and link-local destinations, checked on the addresses actually connected to.
* **CSRF protection.**
  * Every state-changing request needs the custom header `X-VuStudio: 1`, which forces a CORS preflight that the server never grants. A matching `Origin` header is also accepted.
  * WebSocket upgrades must carry a same-origin `Origin` or come from a non-browser local client.
* The push API (`/api/push`) requires the `Authorization: Bearer <apiToken>` header (never a query parameter).
* On-demand history recording is a `POST` (`/api/history/track`) limited to existing sensors and capped at 2000.
* Scripts are executed by the **tray app in the user's session**, never by the LocalSystem service.
* Script definitions can only be changed from loopback requests.
* Secrets in the config (Home Assistant tokens, MQTT passwords) are stored in clear text in `%ProgramData%\VU Studio`. That folder is protected twice:
  * the installer creates it with `admins-full system-full` permissions (its `[Dirs]` entry), and
  * the service, when it starts as a Windows service, sets its DACL to SYSTEM and Administrators full control only, protected from inheritance (nothing from `%ProgramData%`, where Users may create and write), with Administrators as owner. Files inside inherit that DACL.
  * When started as a service, the data folder and its `logs` and `assets` subfolders are refused if they are reparse points (symbolic links, junctions): the link is removed, the folder recreated and an error logged.
  * Standard users therefore cannot read `config.json`. The tray falls back to the default port 5341 (or `VUSTUDIO_PORT` / `--port`) when it cannot read `web.port`.
  * DPAPI is planned.
* `nvidia-smi` is started from absolute well-known paths on Windows (`System32`, `Program Files\NVIDIA Corporation\NVSMI`), never found through `PATH`.

## 7. Threading

* Providers run on their own tasks. `SensorHub` uses lock-free maps.
* The engine runs on one dedicated loop (`PeriodicTimer`, 50 ms). Face rendering runs on that loop, which is cheap at this size, and is rate-limited.
* `DialController` runs one serial worker task. All serial I/O happens there.
* ASP.NET request handlers only read snapshots, or post commands to the engine and controller through thread-safe methods.

## 8. Extensibility

* **New provider:** implement `IProviderFactory` + `ISensorProvider` and register it in DI. The settings UI comes for free from the schema.
* **New theme:** implement `IThemeGenerator` (family, variants, options) in `VuStudio.Rendering/Themes`.
* **External programs:** use the push API or MQTT.
