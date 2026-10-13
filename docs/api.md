# VU Studio service API

**Base URL:** `http://127.0.0.1:5341` by default. When `web.allowLan` is set, the API is also reachable from the LAN.

**JSON conventions:**
* All bodies are JSON with camelCase property names.
* Enums are camelCase strings, e.g. `"gradient"`, `"lightsOff"`.
* Colors are strings in the form `"#RRGGBB"` or `"#RRGGBB+WW"`, where `WW` is the white LED level in hex (0–255).
* Polymorphic objects carry a `"type"` discriminator: face layers, profile conditions.

The data model is defined in `src/VuStudio.Core`:
* configuration: `Config/AppConfig.cs`, `Profiles/Profile.cs`,
* lighting: `Lighting/Backlight.cs`,
* faces: `Faces/FaceDocument.cs`,
* engine snapshots: `Engine/EngineModels.cs`.

## Security

| Caller | Requirement |
|---|---|
| Browser / web UI | Every `POST`/`PUT`/`DELETE` must send the header `X-VuStudio: 1`. Requests whose `Origin` differs from the server origin are rejected. |
| LAN clients | Must log in (`POST /api/auth/login`), which sets an `HttpOnly` cookie. Loopback clients are trusted. |
| Push clients | `Authorization: Bearer <web.apiToken>`. |

Every request must also carry a valid `Host` header: localhost, an IP literal, the machine name or an entry in `web.allowedHosts`.

## Information and state

| Method | Path | Result |
|---|---|---|
| GET | `/api/info` | `{ name, version, os, machine, dataDirectory, simulate, local }` |
| GET | `/api/state` | `StateDto` (below) |

```jsonc
// StateDto
{
  "revision": 12,                                         // config revision
  "engine": {
    "activeProfileId": "default", "activeProfileName": "Default", "manualProfileId": null,
    "night": "none",                                      // none | dim | lightsOff | sleep
    "paused": false,
    "time": "2026-10-12T16:00:00Z",
    "dials": [{
      "slot": 1, "uid": "3E00…", "name": "CPU", "online": true,
      "profileId": "default", "pageIndex": 0, "pageCount": 2, "pageId": "p1", "pageName": null,
      "sensorId": "lhm/cpu/0/load/0", "value": 42.5, "valueText": null, "unit": "%",
      "title": "CPU Total", "needle": 42.5,               // 0..100 (smoothed)
      "light": { "r": 0, "g": 80, "b": 100, "w": 0 },     // device levels 0..100
      "previewColor": "#00CCFF",                          // approximate RGB of the backlight
      "faceVersion": 7,                                   // bump → reload face.png
      "assigned": true, "alarmActive": false
    }]
  },
  "hub": { "state": "connected", "port": "COM5", "message": null, "simulated": false },
  "hardware": [{ "uid": "3E00…", "busIndex": 0, "online": true, "firmwareVersion": "…", "hardwareVersion": "…" }],
  "providers": [{ "id": "lhm", "type": "lhm", "name": null, "enabled": true, "health": "ok", "message": null, "since": "…", "restarts": 0 }]
}
```

## Live updates — WebSocket `/api/live`

Server → client messages:

| `type` | Payload | When |
|---|---|---|
| `hello` | `{ revision, catalogVersion, version }` | on connect |
| `state` | `StateDto` | when the engine snapshot changes, at most 10 per second |
| `values` | `{ data: [ { id, v, t, s, u } ] }` with `v` = number or null, `t` = text or null, `s` = stale, `u` = updated (ISO) | every 500 ms with the sensors changed since the last batch, for subscribed sensors only |
| `catalog` | `{ catalogVersion }` | sensors added or removed: re-fetch `/api/sensors` |
| `config` | `{ revision }` | configuration saved: re-fetch `/api/config` |
| `notification` | `{ title, message, icon, slot }` | alarms |

Client → server: `{ "type": "subscribe", "sensors": "*" }` or `{ "type": "subscribe", "sensors": ["id1", "id2"] }`.

## Configuration

| Method | Path | Notes |
|---|---|---|
| GET | `/api/config` | `AppConfig`. Values of `secret` settings are replaced by `"__secret__"`. `web.passwordHash` is replaced by `"__set__"` or `null`. |
| PUT | `/api/config` | Body `AppConfig`; header `If-Match: <revision>` is optional. Send `"__secret__"` to keep a stored secret. **409** on a revision conflict, **400** `{ errors: [string] }` on validation errors, **403** when a loopback-only provider (scripts) is changed from the LAN. Returns the saved config (masked). |

## Sensors and providers

| Method | Path | Result |
|---|---|---|
| GET | `/api/sensors` | `[{ id, name, device, category, kind, unit, valueKind, min, max, icon, host, providerId, value, text, updated, stale }]` |
| GET | `/api/providers` | `{ types: [ProviderDescriptor & { supported }], status: [ProviderStatusInfo] }` |
| GET | `/api/units/{unit}` | display units compatible with a canonical unit |

## Faces, themes and assets

| Method | Path | Result |
|---|---|---|
| GET | `/api/catalog/themes` | `[ThemeInfo]` (id, family, name {en,it}, description, options) |
| GET | `/api/catalog/fonts` | `[FontInfo]` |
| GET | `/api/catalog/icons` | `[IconInfo]` (id, category, path = SVG path data, 24×24) |
| GET | `/api/dials/{slot}/face.png?scale=3&tint=true` | the face currently on the dial (tinted with its backlight color when `tint`) |
| POST | `/api/faces/preview` | Body `{ slot, page: DialPage, scale?: 3, tint?: "#RRGGBB" }` → `image/png`. Pixel-exact preview of a page that is not saved yet. |
| POST | `/api/faces/document` | Body `{ slot, page }` → `FaceDocument` generated from the page's theme (used by "Customize" in the editor) |
| GET | `/api/assets` | `[{ id, name, size, created }]` |
| POST | `/api/assets` | multipart/form-data field `file` (PNG/JPEG/GIF/BMP/WebP, ≤ 5 MB) → `{ id }` |
| GET | `/api/assets/{id}` | the image bytes |
| DELETE | `/api/assets/{id}` | — |

## Engine and hardware control

| Method | Path | Body |
|---|---|---|
| POST | `/api/engine/profile` | `{ profileId: string \| null }` (null = automatic) |
| POST | `/api/engine/pause` | `{ paused: bool }` |
| POST | `/api/engine/slots/{slot}/advance` | `{ delta: 1 \| -1 }` |
| POST | `/api/engine/refresh` | — |
| POST | `/api/hardware/rescan` | `{ provision: bool }` |
| POST | `/api/hardware/dials/{uid}/identify` | — |
| POST | `/api/hardware/dials/{uid}/raw` | `{ raw: int }` (calibration helper) |
| POST | `/api/hardware/dials/{uid}/calibrate` | `{ point: "full" \| "half", raw: int }` |

## History

| Method | Path | Result |
|---|---|---|
| GET | `/api/history?sensor=<id>&from=<iso>&to=<iso>` | `[{ minute, min, avg, max, count }]` |
| GET | `/api/history/recent?sensor=<id>&seconds=900` | `[{ time, value }]` at 1 s resolution (≤ 1 h). Calling it starts recording the sensor. |

## Authentication

| Method | Path | Notes |
|---|---|---|
| GET | `/api/auth` | `{ local, authenticated, lanEnabled, passwordSet }` |
| POST | `/api/auth/login` | `{ password }` → sets the cookie, or 401 |
| POST | `/api/auth/logout` | — |
| POST | `/api/auth/password` | `{ password }`, loopback only |

## Push API (external programs)

`POST /api/push` with `Authorization: Bearer <token>`:

```json
{ "sensors": [ { "id": "obs/viewers", "value": 42, "name": "Viewers", "unit": "", "min": 0, "max": 500, "icon": "eye" },
               { "id": "obs/scene", "text": "Gaming" } ] }
```

Ids must match `[a-z0-9][a-z0-9/_.-]*`. They appear as `push/<id>`.

## Session channel — WebSocket `/api/session` (tray app only)

Only loopback clients without an `Origin` header (non-browser) are accepted.

Client → server:

| `type` | Fields |
|---|---|
| `hello` | `user`, `machine`, `version` |
| `define` | `provider` (`"session"` or a user-session provider id), `sensors: [SensorInfo with local ids]` |
| `values` | `provider`, `values: { "<localId>": number \| string \| null }` |
| `status` | `provider` (a user-session provider id), `health` (camelCase `ProviderHealth`), `message`; shown in the provider status list |
| `remove` | `provider`, `sensors: [localIds]`: drops those sensors from the catalog |

`session/locked` is owned by the service: the tray only reports its value, and `define`/`remove` for it are ignored. Sensors of a user-session provider are dropped when it is disabled or deleted.

Server → client:

| `type` | Fields |
|---|---|
| `notify` | `title`, `message`, `icon` |
| `providers` | `items: [ProviderInstanceConfig]` — the user-session providers (scripts) the tray must run |

## Agent API (VU Studio Agent on other machines)

Default port **5342**. When an agent token is configured, every endpoint requires `Authorization: Bearer <token>`. The agent advertises itself over mDNS as `_vustudio._tcp` with TXT `role=agent`, `v=1` and `name=<display name>`.

| Method | Path | Result |
|---|---|---|
| GET | `/api/v1/info` | `{ name, version, os, hostname, uptimeSeconds }` |
| GET | `/api/v1/sensors` | `{ name, version, time, catalogVersion, sensors: [SensorInfo with local ids], values: { "<id>": number \| string \| null } }` |

* Ids are the agent's own `{providerId}/{path}`, e.g. `system/cpu/load` or `unraid/disk/disk1/temp`.
* The service's `agent` provider polls this endpoint every 2 s by default. It re-publishes each sensor as `<agentProviderInstanceId>/<id>` with `Host = name`.
