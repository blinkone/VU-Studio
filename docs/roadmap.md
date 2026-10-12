# Roadmap

## Milestone 1 — Foundation

### Done

- [x] Requirements, architecture and VU1 protocol documentation
- [x] Solution scaffold: .NET 10, central package management, xUnit v3
- [x] Core contracts: sensor hub, provider framework, value mapping, smoothing, units, backlight engine, face model, profiles, configuration store
- [x] Bundled assets: OFL fonts and the Material Design Icons subset

### To do

- [ ] Hardware layer
  - VU1 protocol, serial transport, hub discovery
  - `DialController` with interleaved scheduling
  - Simulator
- [ ] Face renderer
  - SkiaSharp layers, dithering
  - Theme families: vintage, minimal, instrument, HUD
- [ ] Engine: pages, rotation, profiles, night mode, face refresh policy, history
- [ ] Service host
  - REST + WebSocket API, security middleware
  - Windows service integration, SQLite history
- [ ] Providers
  - Clock, demo, system/Linux, network, ping, process
  - HTTP-JSON, MQTT, Home Assistant, weather, crypto, push, agent, Glances, Prometheus, LHM-remote, UPnP
- [ ] Windows providers: LibreHardwareMonitor, HWiNFO shared memory, RTSS FPS
- [ ] Web UI (React, IT/EN)
  - Dashboard with live dial previews
  - Data, face and light editors
  - Sources, profiles, history, settings
- [ ] Tray app
  - WebView2 window
  - Session collector: audio, media, foreground, idle/lock, accent
  - Script runner, notifications
- [ ] Agent: Linux/Windows, Docker image + Unraid template, Steam Deck installer
- [ ] Installer (Inno Setup) and GitHub Actions CI/CD

## Milestone 2 — Polish

- First-run wizard: detect dials, identify, order, pick a starter profile
- Calibration wizard
- More theme variants
- DPAPI-protected secrets
- PresentMon FPS
- OpenRGB sync
- mDNS discovery of agents
