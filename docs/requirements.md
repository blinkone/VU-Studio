# VU Studio — Requirements

This document captures what VU Studio must do. The product decisions come from the kickoff
Q&A with the project owner (2026-10-12). Hardware facts come from studying the open-source
[VU-Server](https://github.com/SasaKaranovic/VU-Server) and the stock VU1 dial faces.

## 1. Context

The owner has **4 Streacom VU1 dials** on one hub, connected to a **Windows 10/11** PC.
The official stack is VU-Server, a REST server that only moves needles, sets colors and
uploads images. Anything else needs another program pushing data to it. That makes it hard to
configure and limits what the dials can show.

VU Studio replaces it with one application that:

* talks to the hub directly,
* gathers data from the PC, from other devices on the LAN, and from online and software sources,
* lets the user design how each dial looks: e-ink face, needle behaviour and RGBW backlight.

## 2. Product decisions

| Topic | Decision |
|---|---|
| Platform | Windows 10/11 first. The core stays cross-platform (Linux is used for the network agent and for development). |
| Application shape | Background **service** + **desktop app** (tray icon + window) + optional **web panel** reachable from the LAN. |
| Hardware access | **Direct** connection to the VU1 hub over USB serial. VU-Server is not needed and must not run at the same time, because only one program can own the port. |
| Technology | Our choice: **.NET 10 (C#)** for service, agent and tray; **React + TypeScript** for the UI. |
| Hardware data | As much as possible: CPU, GPU, RAM, disks, network, cooling. This also covers **other devices on the LAN**. |
| Sensor back-ends | **LibreHardwareMonitor** built in. **HWiNFO** (shared memory) is used as well whenever it is running. |
| Software data | Audio (a real VU meter on the output signal, volume), games and apps (FPS, per-process usage), internet and home automation (weather, markets, Home Assistant, MQTT, any JSON API), time and media (clock, timers, now playing). |
| Custom sources | **Scripts/commands** and a **local API** (HTTP/WebSocket, MQTT) to push values. |
| LAN devices | Other Windows PCs, an **Unraid** server, a **Steam Deck**, servers/NAS/Raspberry Pi, router and connection (ISP router + **Google Nest Wifi** mesh), home automation/IoT. |
| Face design | **Themes + visual editor**, plus image upload. |
| E-ink content | **Dynamic**: values, text, clock, sparklines, refreshed at a sensible rate. |
| Backlight | Color driven by the value (gradient/thresholds), fixed color, **effects and alarms**, **sync** with other lights (Windows accent color, album art, OpenRGB). |
| Theme families | Vintage analog, Modern minimal, Instrumentation (car/aircraft), Sci-fi / gaming HUD. |
| Advanced features | Automatic **profiles**, data **rotation**, **history & statistics**, **night & standby** behaviour. |
| Audience | **Open source** on GitHub, UI in **Italian and English**. |
| Name / license | **VU Studio**, **GPL-3.0**. |

## 3. VU1 hardware facts

| Item | Value |
|---|---|
| Hub | USB serial bridge FTDI FT231X, **VID 0x0403 / PID 0x6015**, 115200 8N1 |
| Topology | Dials daisy-chained on an I²C bus behind the hub; each dial has a 12-byte **UID** (24 hex chars) and a bus index |
| Needle | 0–100 % (integer), raw DAC set, calibration of the 100 % and 50 % points, on-device easing (`step` %, `period` ms; default 2 %/50 ms) |
| Backlight | **RGBW**, each channel 0–100, on-device easing (default 5 %/100 ms) |
| Display | E-paper **200 × 144 px, 1 bit** (188 dpi). An upload is 3600 bytes sent in 1000-byte chunks and takes a few seconds |
| Needle geometry | The pivot sits below the display, at **(99.5, 225.5)** in display pixels. 0 % = **−40°**, 50 % = 0°, 100 % = **+40°** from vertical, linear in between. Stock tick marks span radius ≈ 137–152 px |

The serial protocol is documented in [vu1-protocol.md](vu1-protocol.md).

## 4. Functional requirements

### 4.1 Dials and hardware

* **FR-HW-1** Find the hub automatically (USB VID/PID), with a manual port override. Reconnect by itself after unplug, sleep or errors.
* **FR-HW-2** Enumerate dials. Identify each one by UID, read firmware/hardware info, and remember a friendly name and order.
* **FR-HW-3** Identify a dial physically (needle wiggle + backlight flash).
* **FR-HW-4** Guided calibration of the 0/50/100 % points. Needle and backlight easing per dial.
* **FR-HW-5** Keep needles responsive while e-ink images upload: needle and backlight updates are interleaved between image chunks.
* **FR-HW-6** A built-in **simulator** (virtual hub with N dials) so everything works without hardware.

### 4.2 Data sources

* **FR-DS-1** One sensor catalog. Every value has an id, name, device/host, category, unit and suggested range. The catalog can be browsed and searched, with live values.
* **FR-DS-2** Local PC sensors through LibreHardwareMonitor: CPU, GPU, memory, storage, network, fans/pumps, power, batteries, controllers.
* **FR-DS-3** HWiNFO shared memory, picked up automatically while HWiNFO runs.
* **FR-DS-4** Network throughput, ping/latency/packet loss, UPnP WAN counters.
* **FR-DS-5** Audio: output peak and VU levels (L/R), master volume, microphone level and mute.
* **FR-DS-6** Media: title, artist, playback state, progress, album art color.
* **FR-DS-7** Games and apps: FPS and frame time (RTSS shared memory, later PresentMon), per-process CPU and RAM, foreground application.
* **FR-DS-8** Time: clock, date, countdown and pomodoro timers.
* **FR-DS-9** Online: weather (Open-Meteo, no key), crypto prices, generic HTTP/JSON with JSONPath extraction.
* **FR-DS-10** Home automation: Home Assistant entities, MQTT topics (with JSON extraction).
* **FR-DS-11** Remote devices: the **VU Studio Agent** (Windows/Linux/Steam Deck/Unraid Docker/Raspberry Pi), Glances, Prometheus/node_exporter, LibreHardwareMonitor's remote web server.
* **FR-DS-12** Scripts/commands run on an interval, with stdout parsed as a number, `key=value` pairs or JSON. They run in the user's session, not as SYSTEM.
* **FR-DS-13** Local push API: external programs (Stream Deck, scripts, Home Assistant) can create sensors and push values. They can also temporarily override a dial.

### 4.3 Needle and value mapping

* **FR-MAP-1** Range: fixed min/max or the sensor's suggested range. Linear, logarithmic, square-root or custom piecewise scales. Optional inversion.
* **FR-MAP-2** Smoothing with separate attack and release times, so classic VU/PPM ballistics can be reproduced. Optional peak hold.
* **FR-MAP-3** Behaviour when a source goes offline: park at zero, hold the last value, or show an offline face.

### 4.4 Faces (e-ink)

* **FR-FACE-1** Themes in four families, generated from the dial's data: title, unit, icon, scale labels that follow the mapping, warning zones.
* **FR-FACE-2** Visual editor with layers: scale, text, icon, image, shapes, bar, sparkline. Drag to place, edit properties, live 1-bit preview identical to the device.
* **FR-FACE-3** Dynamic text bindings such as `{value}`, `{unit}`, `{min}`, `{max}`, `{time:HH:mm}` or any sensor via `{sensor:<id>}`, with formatting.
* **FR-FACE-4** Image upload with automatic resize, threshold and dithering (Floyd–Steinberg, Atkinson, ordered).
* **FR-FACE-5** Refresh policy: re-render only when the rendered bitmap changes, with a minimum interval between updates (default 60 s) to protect the e-paper.

### 4.5 Backlight

* **FR-BL-1** Modes: off, fixed RGBW, gradient over the scale, thresholds in sensor units.
* **FR-BL-2** Sync with the Windows accent color, the album-art color or any color-valued sensor.
* **FR-BL-3** Effects: breathe, pulse, blink, plus global brightness.
* **FR-BL-4** Alarms: value above/below a threshold for N seconds triggers a color/effect override and, optionally, a desktop notification.

### 4.6 Pages, profiles and modes

* **FR-PRO-1** Each dial can have several **pages** (data + face + light), rotated on a timer.
* **FR-PRO-2** **Profiles** (e.g. Gaming, Work, Night) with priorities. A profile activates when a process runs, an app is in the foreground, a schedule matches or a sensor condition holds, or manually (tray menu, hotkey, API).
* **FR-PRO-3** **Night/standby**: on screen lock, idle, sleep or quiet hours, dim or turn off the lights and park the needles. Restore afterwards.

### 4.7 History and statistics

* **FR-HIS-1** Keep recent samples (1 s resolution, ~1 h) in memory and per-minute min/avg/max in SQLite (default retention 30 days).
* **FR-HIS-2** Charts and min/avg/max statistics in the UI. Sparklines on the e-ink faces.

### 4.8 User interface

* **FR-UI-1** Dashboard with a live, realistic preview of each dial: face, needle angle, backlight tint.
* **FR-UI-2** Editors for data binding, face, light, pages and profiles, plus a source catalog and settings.
* **FR-UI-3** Italian and English, with automatic detection and a manual switch.
* **FR-UI-4** The same UI runs inside the desktop app (WebView2) and in any browser.

## 5. Non-functional requirements

* **NFR-1 Reliability.** Runs for months without attention: auto-reconnect, bounded queues, no unbounded memory growth.
* **NFR-2 Responsiveness.** A sensor change reaches the needle in under 250 ms under normal load.
* **NFR-3 Footprint.** The service uses under 2 % CPU and under 150 MB RAM with 4 dials and the default sources.
* **NFR-4 Security.**
  * The web panel binds to localhost by default. LAN access is opt-in and password-protected.
  * The API is protected against CSRF and DNS rebinding.
  * Push clients authenticate with an API token.
  * Scripts never run as SYSTEM.
* **NFR-5 Privacy.** No telemetry. Online sources are contacted only when the user configures them.
* **NFR-6 Portability.** Core, rendering, hardware and agent run on Windows and Linux (x64/arm64).
* **NFR-7 Open source hygiene.** GPL-3.0, CI on every push, contributor docs, reproducible builds.

## 6. Out of scope (for now)

* macOS-specific sensors.
* Firmware updates of hub/dials. VU-Server exposes bootloader commands, but flashing is risky and stays out of scope.
* A VU-Server–compatible REST API (not requested).
