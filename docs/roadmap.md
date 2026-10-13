# Roadmap

## Milestone 1 — Foundation

All modules are built and covered by automated tests on Linux. Real-hardware testing on Windows is next.

- [x] Requirements, architecture and VU1 protocol documentation
- [x] Solution scaffold: .NET 10, central package management, xUnit v3
- [x] Core contracts: sensor hub, provider framework, value mapping, smoothing, units, backlight engine, face model, profiles, configuration store
- [x] Bundled assets: OFL fonts and the Material Design Icons subset
- [x] Hardware layer
  - VU1 protocol, serial transport, hub discovery
  - `DialController` with interleaved scheduling
  - Simulator (`--simulate N`)
- [x] Face renderer
  - SkiaSharp layers, dithering
  - Theme families: vintage, minimal, instrument, HUD
- [x] Engine: pages, rotation, profiles, night mode, face refresh policy, history
- [x] Service host
  - REST + WebSocket API, security middleware
  - Windows service integration, SQLite history
- [x] Providers
  - Clock, demo, system/Linux, ping, process, HTTP-JSON, script, push
  - Agent (with mDNS discovery), Glances, Prometheus, LHM-remote, Unraid, UPnP WAN / Nest Wifi, weather, crypto, MQTT, Home Assistant
- [x] Windows providers: LibreHardwareMonitor, HWiNFO shared memory
- [x] Web UI (React, IT/EN)
  - Dashboard with live dial previews
  - Data, face (themes + layer editor) and light editors
  - Sources, sensors, history, profiles, settings, calibration wizard
- [x] Tray app
  - WebView2 window, single instance, start with Windows
  - Session collector: audio, media, foreground, idle/lock, accent, RTSS FPS
  - Script runner, notifications
- [x] Agent: Linux/Windows, Docker image + Unraid template, Steam Deck installer, systemd unit
- [x] Installer (Inno Setup), GitHub Actions CI and tag-triggered releases

## Milestone 1.1 — Verification on real hardware

These parts compile and pass tests against fakes and fixtures but have not run on the real thing yet:

- [ ] VU1 hub and dials over USB serial on Windows
- [ ] Windows service install/uninstall via the installer; tray app (WebView2, audio, media, lock, accent, RTSS)
- [ ] LibreHardwareMonitor 0.9.6 with and without PawnIO; HWiNFO shared memory under LocalSystem
- [ ] Agent Docker image on Unraid, `install.sh` on a Steam Deck, mDNS discovery over the LAN
- [ ] UPnP on the ISP router and Google Nest Wifi; Unraid GraphQL field names; Home Assistant WebSocket; MQTT broker
- [ ] First tagged release through `.github/workflows/release.yml`

## Milestone 2 — Polish

- First-run wizard: detect dials, identify, order, pick a starter profile
- More theme variants
- DPAPI-protected secrets
- PresentMon FPS
- OpenRGB sync
- UWP foreground-app resolution (child window of `ApplicationFrameHost.exe`)
- Code signing for the installer and binaries
