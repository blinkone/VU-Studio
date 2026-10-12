# VU Studio: network, home-automation and online data sources

Research notes for engineers who will code the remote-device, router, Home Assistant, MQTT and online sources of VU Studio (requirements FR-DS-4, FR-DS-9, FR-DS-10, FR-DS-11 in [`docs/requirements.md`](../requirements.md)). Written 2026-10-12. Nothing here is application code; the C# fragments are short, and the ones marked **tested** were compiled and run on .NET 10.0.112 in the research sandbox. Windows-only providers (HWiNFO, LibreHardwareMonitorLib, RTSS, GSMTC, WASAPI, service specifics) are researched in the sibling note [`windows-sources.md`](windows-sources.md) and are not repeated here.

**How the research was done.** Primary sources first: upstream repositories were cloned and read at the versions listed below (docs, API schemas, server code), kernel documentation and driver sources, and official release notes. Third-party material (forum posts, search-engine summaries of pages that could not be fetched) is labelled as such. Every URL is cited next to the claim it supports.

**Legend.**
* **UNVERIFIED** = could not be confirmed from a primary source and/or not run on real hardware in this research. Section 12.3 lists them as a test plan.
* "verified in source" = read in the upstream code or docs at the version named.
* Unit rules used throughout: sysfs hwmon temperatures are millidegrees C, voltages mV, currents mA, power µW; `power_supply` voltages µV, currents µA, charge µAh, energy µWh, battery `temp` in tenths of °C; `/proc` memory in kibibytes; Unraid `.ini` sizes in KiB; network/disk counters in bytes (sectors are always 512 B); UPnP link rates in bit/s.

**Versions consulted.**

| Subject | Version / revision |
|---|---|
| Glances | 4.5.8 (tag, 2026-10-11, latest on PyPI), 3.4.0.5 (last v3), `develop` 5.0.0b1 (serves `/api/5`, pre-release) |
| Prometheus docs / node_exporter | `prometheus/docs` 2026-09-30, node_exporter 1.12.1 (2026-10-10) |
| Netdata | master, 2026-10-12 |
| Unraid | OS 7.2.0 to 7.4.0-rc.1 release notes, `unraid/api` v4.37.5 (2026-09-16), `unraid/webgui` master (2026-10-06) |
| Home Assistant | developer docs and `core` `dev` branch, October 2026 |
| MQTTnet / mDNS | MQTTnet 5.2.0.1603 (2026-07-01); Makaretu.Dns.Multicast.New 0.38.0; Zeroconf 3.7.16; Tmds.MDns 0.10.1 |
| UPnP | miniupnp master (2026-10-09), async_upnp_client 0.49.0 |
| Open-Meteo | server repo 1.6.0 (2026-10-12) |
| Linux / hardware | torvalds/linux master (Oct 2026), MangoHud master (2026-10-09), LibreHardwareMonitor master (2026-10-12), Valve `steamdeck-hwmon` driver |

**Contents.** 1 Glances · 2 Prometheus / node_exporter · 3 Netdata · 4 Unraid · 5 Steam Deck · 6 Generic Linux `/proc` `/sys` · 7 Router and internet connection · 8 Home Assistant · 9 MQTT · 10 Online data · 11 mDNS discovery · 12 Recommendations.

**Headline findings.**
* Glances v4 exposes ready-made per-second rates (`*_rate_per_sec`); v3 only deltas plus `time_since_update`. Sensors refresh every 10 s and filesystems every 60 s by default.
* Unraid has had a built-in GraphQL API since 7.2.0 (2025-10-29); parity-check progress is `mdResyncPos / mdResyncSize` in `var.ini` (KiB blocks). A container needs only read-only mounts of `/var/local/emhttp` and `/mnt` plus host networking.
* Steam Deck: all sensors are plain sysfs; fan RPM comes from the `steamdeck_hwmon` chip (older kernels: `jupiter`), whose only temperature is the *battery*. The agent belongs in `~/.local/share` as a user systemd unit; `/home` survives SteamOS updates, `/usr` does not.
* UPnP counters are 32-bit by spec but 64-bit on many miniupnpd builds and **saturated at 4294967295 on Nest Wifi** (user report): handle wrap, reset and "unsupported". With a Nest behind an ISP router, multicast discovery only finds the Nest; query the ISP router by unicast, using the `wan.gatewayIpAddress` of the Nest's local status JSON.
* Home Assistant's efficient `subscribe_entities` WebSocket command is undocumented (compressed `a`/`c`/`r` diffs); MQTTnet 5 renamed `MqttFactory` to `MqttClientFactory` and dropped the managed client.
* **Stooq is no longer keyless** (since March/April 2026), contrary to the common advice; CoinGecko works keyless but only from a small shared rate-limit pool.
* Open-Meteo (no key) is free for non-commercial and open-source use, requires a visible CC BY 4.0 attribution link, and rate-limits per IP at 10,000/day, 5,000/hour, 600/minute with calls weighted by variable count (read from the server source).

## 1. Glances REST API (v4, with v3 differences)

Glances is a Python system monitor (psutil based) whose web mode (`glances -w`) serves a JSON REST API and a web UI on the same port. It is the lowest-effort way to read a Linux box (Unraid, Raspberry Pi, NAS) without writing an agent, but it needs Python/Docker on the target.

### 1.1 Facts

| Item | Value | Source |
|---|---|---|
| Latest stable | **4.5.8** (tag `v4.5.8`, 2026-10-11; PyPI `glances` 4.5.8, `requires_python >= 3.10`) | PyPI JSON, GitHub tag |
| Next major | `develop` is **5.0.0b1** and serves `/api/5` (pre-release; do not target). Last v3: **3.4.0.5** (`/api/3`) | `glances/__init__.py` on `develop` |
| Start command | `glances -w` (API + WebUI), `glances -w --disable-webui` (API only) | `docs/api/restful.rst` @v4.5.8 |
| Default port / bind | **61208**, binds `0.0.0.0` (the doc's "localhost" examples are just curl) | docs, `--port`, `--bind` |
| Base URL | `http://<host>:61208/api/4` (with `[outputs] url_prefix=/glances/` -> `/glances/api/4`) | docs |
| Self docs | Swagger UI `http://<host>:61208/docs`, OpenAPI file `docs/api/openapi.json` in the repo | docs |
| License | LGPL-3.0-only (we only speak HTTP, so no linkage constraint on our GPL-3.0 code) | file headers |
| Health check | `GET /api/4/status` -> `{"version": "4.5.8"}` (HTTP 200) | `glances_restful_api.py::_api_status` |

Sources: <https://glances.readthedocs.io/en/latest/api/restful.html> (same text as `docs/api/restful.rst` in <https://github.com/nicolargo/glances>), <https://github.com/nicolargo/glances/blob/v4.5.8/NEWS.rst>, <https://pypi.org/project/glances/>.

### 1.2 Endpoints and fields (v4.5.x)

All are `GET`, JSON. `GET /api/4/pluginslist` lists the 36 plugins. `GET /api/4/<plugin>/<field>` returns `{"<field>": value}` (for list plugins: `{"<field>": [v1, v2, ...]}`). `GET /api/4/<plugin>/<keyfield>/value/<value>` returns `{"<value>": [ {item} ]}` (mount point `/` is written `/api/4/fs/mnt_point/value//`). `GET /api/4/<plugin>/history[/N]` and `/<plugin>/<field>/history[/N]` return `{"field": [["ISO-8601 time", value], ...]}` (default history = 1200 points).

| Endpoint | Shape | Fields (unit) |
|---|---|---|
| `/cpu` | object | `total` (% = 100-idle), `user`, `system`, `idle`, `nice`, `iowait`, `irq`, `steal`, `guest` (all %), `cpucore` (int), `ctx_switches`/`interrupts`/`soft_interrupts` (per-interval delta) with `*_rate_per_sec` and `*_gauge`, `time_since_update` (s) |
| `/percpu` | list, key `cpu_number` | per core: `total`, `user`, `system`, `idle`, `iowait`, `irq`, `softirq`, `steal`, `guest`, `guest_nice`, `nice` (%) |
| `/quicklook` | object | `cpu` %, `mem` %, `swap` %, `load` % (15-min load average as % of cores), `percpu` (list), `cpu_name`, `cpu_hz`, `cpu_hz_current` (Hz), `cpu_log_core`, `cpu_phys_core`, `gpu_proc`, `gpu_mem`: **cheapest "overview" call** |
| `/load` | object | `min1`, `min5`, `min15` (run-queue average, float), `cpucore` |
| `/mem` | object | `total`, `available`, `used`, `free`, `active`, `inactive`, `buffers`, `cached`, `shared` (**bytes**), `percent` (= (total-available)/total*100), plus `percent_min/max/mean` since start |
| `/memswap` | object | `total`, `used`, `free` (bytes), `percent`, `sin`, `sout` (cumulative bytes), `time_since_update` |
| `/fs` | list, key `mnt_point` | `device_name`, `fs_type`, `mnt_point`, `options`, `size`, `used`, `free` (**bytes**), `percent` (used %) |
| `/diskio` | list, key `disk_name` | `read_bytes`, `write_bytes`, `read_count`, `write_count`, `read_time`, `write_time` (ms), `read_latency`, `write_latency` (ms/op) with `*_rate_per_sec` and `*_gauge` variants (see 1.3) |
| `/network` | list, key `interface_name` | `bytes_recv`, `bytes_sent`, `bytes_all` with `_rate_per_sec` / `_gauge`, `speed` (**bit/s**, 0 if unknown), `is_up`, `alias`, `time_since_update` |
| `/sensors` | list, key `label` | `label`, `value`, `unit` (`C` temperature, `R` fan RPM, `%` battery), `type` (`temperature_core`, `temperature_hdd`, `fan_speed`, `battery`), `warning`, `critical` |
| `/gpu` | list, key `gpu_id` | `gpu_id` (`nvidia0`, `amd0`, `intel0`...), `name`, `proc` (% load), `mem` (% VRAM), `temperature` (C), `fan_speed` (RPM; **NVIDIA only**, the AMD reader in `cards/amd.py` always returns `null` for it) |
| `/containers` | list, key `name` | `name`, `id`, `image`, `status`, `engine` (docker/podman/lxd), `cpu_percent`, `memory_usage` (bytes), `memory_limit`, `io_rx`/`io_wx` (**B/s**), `network_rx`/`network_tx` (**bit/s**), `ports`, `uptime`, `created` |
| `/uptime` | **string** | `"1 day, 7:49:45"` (Python `str(timedelta)`; forms: `H:MM:SS`, `1 day, H:MM:SS`, `N days, H:MM:SS`). There is no numeric uptime field: parse the string (days part optional) |
| `/system` | object | `hostname`, `os_name`, `os_version`, `linux_distro`, `platform`, `hr_name` |
| `/ip` | object | `address`, `mask`, `mask_cidr`, `gateway`, `public_address` |
| `/core` | object | `phys`, `log` (core counts) |
| `/all` | big object | `{ "cpu": {...}, "mem": {...}, "network": [...], "processlist": [...], ... }` every enabled plugin (includes the process list: large and slow) |
| `/all/limits`, `/all/views` | object | thresholds (`cpu_total_careful/warning/critical` = 65/75/85 default) and UI decoration hints |

Trimmed samples (from the v4.5.8 docs; the fan and battery sensor entries are illustrative of the documented types):

```json
GET /api/4/cpu        {"total": 6.0, "user": 3.1, "system": 3.1, "idle": 93.4, "iowait": 0.4, "steal": 0.0, "cpucore": 16}
GET /api/4/mem        {"total": 15880060928, "available": 7234739384, "used": 8645321544, "percent": 54.4}
GET /api/4/network    [{"interface_name": "wlp0s20f3", "bytes_recv": 0, "bytes_recv_rate_per_sec": 0,
                        "bytes_recv_gauge": 1325174313, "bytes_sent_rate_per_sec": 0, "bytes_sent_gauge": 54420876,
                        "speed": 0, "time_since_update": 0.379, "key": "interface_name"}]
GET /api/4/sensors    [{"label": "Package id 0", "value": 40, "unit": "C", "type": "temperature_core", "warning": 0, "critical": null, "key": "label"},
                       {"label": "CPU Fan", "value": 2100, "unit": "R", "type": "fan_speed", "key": "label"},
                       {"label": "BAT BAT0", "value": 87, "unit": "%", "type": "battery", "key": "label"}]
GET /api/4/fs         [{"mnt_point": "/", "device_name": "/dev/mapper/ubuntu--vg-ubuntu--lv", "fs_type": "ext4",
                        "size": 1003736440832, "used": 502802178048, "free": 449871757312, "percent": 52.8}]
GET /api/4/uptime     "1 day, 7:49:45"
```

Sensor labels come from psutil: the hwmon `tempN_label` if present, else `<chip name> <index>` (`dell_smm 0`), duplicates get a numeric suffix. Battery label is `BAT <name>`. Use `label` + `type` as the sensor id; do not assume stable ordering.

### 1.3 Rates versus counters (the v3 -> v4 change)

| | v3 (3.4.x, `/api/3`) | v4 (4.0 -> 4.5.8, `/api/4`) |
|---|---|---|
| Network | `rx`, `tx`, `cx` = **bytes moved since the previous Glances update**; `cumulative_rx/tx/cx` = kernel counters; `time_since_update` = seconds between the two updates. Rate = `rx / time_since_update` | `bytes_recv`, `bytes_sent`, `bytes_all` = **delta** since previous update; `*_rate_per_sec` = delta / `time_since_update` (**integer floor division, `//`**); `*_gauge` = cumulative kernel counter. **Use `bytes_recv_rate_per_sec` directly (B/s).** |
| Disk I/O | `read_bytes`, `write_bytes`, `read_count`, `write_count` = deltas + `time_since_update` | same scheme: `read_bytes`/`read_bytes_rate_per_sec`/`read_bytes_gauge`, plus `read_latency`/`write_latency` |
| Docker plugin | `/api/3/containers` -> `{"containers": [ {Id, Image, Status, cpu_percent, memory_usage, io_r, io_w, network_rx, ...} ], "version": {}}` | `/api/4/containers` -> bare **list** with snake_case fields (`cpu_percent`, `memory_usage`, `io_rx`, `network_rx`) |
| Alerts | list of lists | list of dicts (`/api/4/alert`) |
| Server | Bottle | FastAPI + Uvicorn (adds `/docs`, JWT) |
| Rate fields present since | n/a | **v4.0.0** (`_manage_rate` decorator, `*_gauge`, `*_rate_per_sec`) |

Mechanics (code: `glances/plugins/plugin/model.py::_manage_rate` @v4.5.8): on each plugin update, `field_gauge = counter`; `field = counter - previous_gauge`; `field_rate_per_sec = field // time_since_last_update`. **The first sample after Glances starts (and the first sample of a newly appeared NIC/disk) reports 0**, so ignore a 0 right after connecting. The delta window is Glances' own refresh window, not your polling window. The REST handler refreshes stats at most once per `--cached-time` (default **1 s**); polling faster than that returns identical numbers. Poll at 1-2 s and use `*_rate_per_sec` (v4) or `rx/time_since_update` (v3).

Per-plugin refresh gotchas (config defaults, `conf/glances.conf` @v4.5.8): **`sensors` refresh = 10 s**, **`fs` refresh = 60 s**, global `-t/--time` = 2 s. A needle bound to temperature or disk usage will therefore update slowly no matter how fast we poll.

Version detection: `GET /api/4/status` works on v4; probe `/api/3/status` for v3. Never assume `/api/4` exists on an Unraid CA container that pins an old tag.

### 1.4 Authentication and security

* **Default: no authentication** and the server binds all interfaces (docs call this out; 4.5.x prints a startup warning). Enable with `glances -w --password` (default username `glances`; `-u <user>` / `--username` to choose, hash stored in `~/.config/glances/<user>.pwd`; in Docker copy the file in as a secret, see `docs/docker.rst`).
* Clients authenticate with **HTTP Basic using the plain password** (`Authorization: Basic base64(user:password)`, the server hashes it) or with a **JWT**: `POST /api/4/token` body `{"username":"u","password":"p"}` -> `{"access_token":"...","token_type":"bearer","expires_in":3600}`, then `Authorization: Bearer <token>` (needs `python-jose`; token endpoint is unauthenticated; expiry `[outputs] jwt_expire_minutes`, default 60). Basic over plain HTTP exposes the password on the LAN, so tell users to use a reverse proxy or accept the risk.
* Hardening keys (`[outputs]` in `glances.conf`): `webui_allowed_hosts` (Host-header allow-list for DNS-rebinding protection; **opt-in in 4.5.8**: the 4.5.2 NEWS says default-deny but the 4.5.8 code/doc only enforce it when the key is set, so if a user sets it, our `Host: <ip>:61208` header must be in the list or the call returns HTTP 400), `cors_origins`, `cors_credentials`. A .NET client sends no `Origin`, so CORS does not affect us.
* Rate limiting exists only in the `develop` (v5) docs: `[outputs] auth_fail_per_minute` (10 by default) answers `429 Too Many Requests` with `Retry-After` for a client address that keeps failing authentication; `rate_limit_per_minute` / `rate_limit_burst` limit all requests and are off by default; `/status` is never limited. A search of the v4.5.8 tag finds no such code, so on 4.5.x expect no limiter, but honour `429` + `Retry-After` anyway.

### 1.5 Running Glances on Unraid (and other Docker hosts)

Image: `nicolargo/glances` (Docker Hub). Tags: `latest` (Alpine, minimal + FastAPI + Docker), `latest-full` (all deps, needed for GPU/SMART/sensors extras), `ubuntu-latest[-full]`, `dev`/`ubuntu-dev` (develop branch, unstable), or a pinned version. The image `CMD` runs `python -m glances ${GLANCES_OPT}`, `EXPOSE 61209 61208`.

Minimal Unraid / `docker run` recipe (from the official Docker doc plus the Unraid CA listing for `nicolargo/glances`, which uses `--pid=host --restart unless-stopped`, `GLANCES_OPT=-w`, port 61208, docker socket and `/etc/os-release` mounts):

```bash
docker run -d --name glances --restart unless-stopped \
  --pid host --network host \
  -e GLANCES_OPT="-w --disable-webui -t 2" \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  -v /etc/os-release:/etc/os-release:ro \
  -v /mnt:/mnt:ro \
  nicolargo/glances:latest-full
```

| Flag / mount | Why |
|---|---|
| `GLANCES_OPT="-w"` | without it the container runs the console UI, which needs a TTY, instead of the web server |
| `--network host` (Unraid "Network Type: Host") | otherwise `/api/4/network` lists only the container veth; with host networking the port is bound directly so no `-p` needed. With bridge networking use `-p 61208:61208`. (The Glances docs add `--privileged` here; read-only access to host `/proc` and `/sys` should not need it, **UNVERIFIED**) |
| `--pid host` | process list/count for the host (CPU, memory, load need nothing: `/proc/stat`, `/proc/meminfo`, `/proc/diskstats` are global in containers) |
| `/var/run/docker.sock:ro` | `containers` plugin (and Podman socket path for Podman) |
| `/etc/os-release:ro` | `system` plugin shows host OS instead of Alpine |
| `/mnt:/mnt:ro` (Unraid) | `fs` plugin only sees mounts inside the container; bind-mount `/mnt` so `/mnt/disk1..N`, `/mnt/cache` appear. Unraid `/mnt/user` is FUSE (`fuse.shfs`), which psutil hides by default; `[fs] allow=shfs` in `glances.conf` (substring match on the fs type, verified in `plugins/fs/__init__.py`) re-adds it (that Unraid shows `shfs` this way is **UNVERIFIED**; `/mnt/diskN` are plain XFS/btrfs/ZFS mounts and always appear) |
| `--gpus`/NVIDIA runtime | only for NVIDIA GPU plugin (`latest-full`); AMD/Intel GPUs are read from `/sys/class/drm` which needs no flag |
| `/sys` (default) | hwmon temperatures and fans work in a default container because `/sys/class/hwmon` is the host's; Unraid disk temperatures are **not** in hwmon, so use the Unraid state files (section 4) or SMART for them |

Auth on Unraid: add `--password` to `GLANCES_OPT` and provide `glances.pwd` through a bind mount, or keep it unauthenticated and restrict by firewall. VU Studio should expose "username/password" fields and send Basic only when set.

Template pointer: the Unraid CA entry for Glances is at <https://ca.unraid.net/apps/glances-0ybubfj0lt3kln> (listing details seen through search snippets, **UNVERIFIED** network type/privileged flag).

## 2. Prometheus: node_exporter text format and the query API

Two different integrations: (a) scrape a `node_exporter` (or any exporter) `/metrics` endpoint directly, no Prometheus server needed; (b) ask an existing Prometheus server with PromQL through `/api/v1/query`. (a) is the right default for "a Linux box that already runs node_exporter"; (b) is for users who already have a Prometheus/Grafana stack and want any metric.

### 2.1 Text exposition format (what a minimal parser must handle)

Spec: <https://prometheus.io/docs/instrumenting/exposition_formats/> (source `docs/instrumenting/exposition_formats.md` in <https://github.com/prometheus/docs>). Reference parser: `expfmt` in <https://github.com/prometheus/common>.

| Rule | Detail |
|---|---|
| Transport | HTTP `GET /metrics`, `Content-Type: text/plain; version=0.0.4; charset=utf-8`, UTF-8, `\n` line endings, optional `Content-Encoding: gzip` (send `Accept-Encoding: gzip`; .NET `AutomaticDecompression`) |
| Lines | empty lines ignored; leading/trailing blanks ignored; tokens separated by any run of spaces/tabs |
| Comments | first non-blank char `#` is a comment **unless** the next token is `HELP` or `TYPE` |
| `# HELP name text` | docstring escapes `\\` and `\n`; ignore for our purposes |
| `# TYPE name counter\|gauge\|histogram\|summary\|untyped` | must precede the first sample of that family; missing = `untyped`. **Use TYPE to know whether to compute a rate (`counter`) or use the value as is (`gauge`)** |
| Sample | `name[{label="value",...}] value [timestamp]` |
| Names | `[a-zA-Z_:][a-zA-Z0-9_:]*`; the current spec also allows quoted UTF-8 metric names (`{"my.metric","label.x"="v"}`; rare, skip) and **quoted label names** (`name{"label"="v"}`, which LibreHardwareMonitor's `/metrics` emits) |
| Label values | double-quoted, escapes **only** `\\`, `\"`, `\n`; may contain `,`, `}`, `#`, spaces. **Parse with a character state machine, not `Split(',')`/regex.** Trailing comma before `}` is legal; `{}` empty is legal |
| Value | Go `ParseFloat`: `1`, `1.5e+09`, `NaN`, `+Inf`, `-Inf`. .NET `double.Parse(InvariantCulture)` accepts `NaN`, `Infinity`, `-Infinity` but **not** `+Inf`/`Inf`: special-case them |
| Timestamp | optional int64 **milliseconds** since epoch (node_exporter does not emit them; do not trust them) |
| Histograms | `x_bucket{le="0.5"}`, `x_sum`, `x_count`; summaries: `x{quantile="0.5"}`; ignore unless needed |
| OpenMetrics | `Accept: application/openmetrics-text` returns the same grammar plus `# EOF`, `# UNIT`, counters named `x_total`, timestamps in **seconds**; node_exporter serves plain text unless asked. Treat `# EOF` as end and ignore `# UNIT` and `# TYPE` lines of kind `info` or `stateset` |
| Duplicates | one series = name + label set; later duplicates are undefined, keep the last |

Tested minimal parser (C# 14 / .NET 10; passes the vectors below; no allocation tuning, no `# TYPE` handling, quoted metric names skipped):

```csharp
static IEnumerable<(string Name, Dictionary<string, string> Labels, double Value)> ParseProm(string text)
{
    foreach (var raw in text.Split('\n'))
    {
        var line = raw.AsSpan().Trim();                                   // also strips \r
        if (line.IsEmpty || line[0] == '#') continue;                     // HELP / TYPE / EOF / UNIT
        int i = 0;
        while (i < line.Length && (char.IsAsciiLetterOrDigit(line[i]) || line[i] is '_' or ':')) i++;
        if (i == 0) continue;                                             // quoted UTF-8 metric name: unsupported
        var name = line[..i].ToString();
        while (i < line.Length && line[i] is ' ' or '\t') i++;            // LibreHardwareMonitor writes "name {labels}"
        var labels = new Dictionary<string, string>();
        if (i < line.Length && line[i] == '{')
        {
            i++;
            while (i < line.Length && line[i] != '}')
            {
                while (i < line.Length && line[i] is ' ' or ',') i++;
                if (i >= line.Length || line[i] == '}') break;
                int ks = i; bool q = line[i] == '"';                      // quoted label names are legal in the current spec
                if (q) { i++; ks = i; while (i < line.Length && line[i] != '"') i++; } else while (i < line.Length && line[i] != '=') i++;
                var key = line[ks..i].Trim().ToString();
                if (q) i++;
                while (i < line.Length && line[i] != '"') i++;            // opening quote of the value
                i++;
                var sb = new StringBuilder();
                while (i < line.Length && line[i] != '"')
                {
                    if (line[i] == '\\' && i + 1 < line.Length) { i++; sb.Append(line[i] == 'n' ? '\n' : line[i]); }   // \\ \" \n
                    else sb.Append(line[i]);
                    i++;
                }
                i++;
                labels[key] = sb.ToString();
            }
            i++;
        }
        var rest = i <= line.Length ? line[i..].Trim() : default;
        int sp = rest.IndexOfAny(' ', '\t');
        var vs = (sp < 0 ? rest : rest[..sp]).ToString();                 // text after the value = optional ms timestamp, ignored
        if (vs.Length == 0) continue;
        yield return (name, labels, vs switch { "+Inf" or "Inf" => double.PositiveInfinity, "-Inf" => double.NegativeInfinity,
            "NaN" => double.NaN, _ => double.Parse(vs, NumberStyles.Float, CultureInfo.InvariantCulture) });
    }
}
```

Test vectors that must pass: `node_cpu_seconds_total{cpu="0",mode="idle"} 1.234e+06`; `msdos_file_access_time_seconds{path="C:\\DIR\\FILE.TXT",error="Cannot find file:\n\"FILE.TXT\""} 1.458255915e9` (value labels `C:\DIR\FILE.TXT` and a two-line error); `something_weird{problem="division by zero"} +Inf -3982045` (timestamp ignored); `x{a="b,}c d",} 5` (separators inside the quoted value, trailing comma); `empty{} 7`; `lhm_cpu_load_percent {"sensorName"="CPU Total", "hostName"="PC-1"} 13.5` (LibreHardwareMonitor style, quoted label names and a blank before `{`).

### 2.2 node_exporter essentials

Current release in the repo: **1.12.1** (2026-10-10). Default `http://<host>:9100/metrics`; restrict collectors per request with repeated `?collect[]=cpu&collect[]=meminfo&collect[]=hwmon` (verified: `node_exporter.go` reads `collect[]`); optional TLS/basic auth via `--web.config.file` (`basic_auth_users` with bcrypt hashes). A full scrape is typically tens to a few hundred KB, so use `collect[]` when polling every second. The exporter reads `/proc` and `/sys` at scrape time (no internal sampling), so counters are always fresh. License Apache-2.0.

| What | Metric (type) and labels | Unit / note |
|---|---|---|
| CPU time | `node_cpu_seconds_total{cpu="0",mode="user\|nice\|system\|idle\|iowait\|irq\|softirq\|steal"}` (counter) | seconds of CPU time, per logical CPU. `guest` time is already inside `user`/`nice` (exported separately as `node_cpu_guest_seconds_total`), so never add it |
| CPU frequency | `node_cpu_scaling_frequency_hertz{cpu}` (gauge), `node_cpu_frequency_max_hertz` | Hz |
| Load | `node_load1`, `node_load5`, `node_load15` (gauge) | run-queue average |
| Memory | `node_memory_MemTotal_bytes`, `node_memory_MemAvailable_bytes`, `node_memory_MemFree_bytes`, `node_memory_Buffers_bytes`, `node_memory_Cached_bytes`, `node_memory_SwapTotal_bytes`, `node_memory_SwapFree_bytes` (gauge) | bytes. Used % = `1 - MemAvailable/MemTotal` |
| Filesystem | `node_filesystem_size_bytes`, `node_filesystem_avail_bytes` (space for non-root, what `df` calls Avail), `node_filesystem_free_bytes`, `node_filesystem_readonly`, labels `{device,fstype,mountpoint}` | filter out `fstype` in `tmpfs`, `overlay`, `squashfs`, `devtmpfs`. Used % = `1 - avail/size` |
| Network | `node_network_receive_bytes_total{device}`, `node_network_transmit_bytes_total{device}` (counter); `node_network_up`, `node_network_speed_bytes` (**bytes/s**, not bits), `node_network_carrier` | `device="lo"` and veth/docker bridges should be excluded in auto mode |
| Disk I/O | `node_disk_read_bytes_total{device}`, `node_disk_written_bytes_total`, `node_disk_io_time_seconds_total` (counter) | bytes, seconds (busy time; `rate()` = utilisation 0..1) |
| hwmon | `node_hwmon_temp_celsius{chip,sensor}`, `node_hwmon_fan_rpm{chip,sensor}`, `node_hwmon_power_average_watt`, `node_hwmon_power_watt`, `node_hwmon_in_volts`, `node_hwmon_curr_amps`, `node_hwmon_temp_max_celsius`, `node_hwmon_temp_crit_celsius` (gauge) | already converted from milli-units to C / V / A / W. **`chip` is a sanitised sysfs device path** (e.g. `pci0000:00_0000:00:18_3`, `platform_coretemp_0`), not the driver name |
| hwmon names | `node_hwmon_chip_names{chip,chip_name}` = 1 and `node_hwmon_sensor_label{chip,sensor,label}` = 1 | join on `chip` (+`sensor`) to learn `k10temp`, `amdgpu`, `coretemp`, `nvme`, and labels such as `Tctl`, `edge`, `Package id 0` |
| Thermal zone | `node_thermal_zone_temp{zone,type}` (C) | Raspberry Pi: `type="cpu-thermal"` |
| Power supply | `node_power_supply_capacity{power_supply}` (%), `node_power_supply_online`, `node_power_supply_energy_watthour`, `node_power_supply_power_watt` | batteries/chargers (laptops, Steam Deck) |
| Time / uptime | `node_boot_time_seconds`, `node_time_seconds` (gauge, Unix s) | uptime = `node_time_seconds - node_boot_time_seconds`; there is no `node_uptime` |
| Info | `node_uname_info{nodename,sysname,release,machine}` (gauge=1), `node_os_info{pretty_name}` | hostname for display |

Sources: collector code in <https://github.com/prometheus/node_exporter/tree/master/collector> (`hwmon_linux.go` builds `node_hwmon_<type>[_<element>]_<unit>` and the `chip` label from the device path; `cpu_linux.go`; `filesystem_common.go`; `netdev_common.go`).

### 2.3 Counters to rates (implementation rules)

```text
rate  = (v_now - v_prev) / (t_now - t_prev)      // t from our own Stopwatch/UtcNow at response time
if (v_now < v_prev) rate = v_now / dt            // counter reset (exporter restart, 32/64-bit wrap): same rule as Prometheus rate()
first sample after (re)connect = no value (do not publish 0)
cpu_busy_% = 100 * (1 - dIdleAll / dTotal)        // dIdleAll = d(idle)+d(iowait); dTotal = d(sum of all 8 modes); sum over cpus for the machine, or per cpu label
net_bytes_per_s = rate(node_network_receive_bytes_total{device})
disk_busy_%     = 100 * rate(node_disk_io_time_seconds_total)  // clamp 0..100
```

Use a minimum window of about 1 s (two samples closer than 0.5 s give noisy values) and never use the exporter's own timestamps; they are absent. For gauges (`node_hwmon_*`, `node_memory_*`, `node_load*`) publish the value unchanged. Skip `NaN`.

### 2.4 Prometheus HTTP query API (`/api/v1/query`)

Spec: <https://prometheus.io/docs/prometheus/latest/querying/api/> (source `docs/querying/api.md` in <https://github.com/prometheus/prometheus>). Default server port **9090**. There is no built-in authentication in a stock server; people add a reverse proxy (Basic/Bearer) or run `--web.config.file` (basic auth + TLS), so support optional `Authorization: Basic`/`Bearer` headers.

```text
GET|POST /api/v1/query?query=<promql>&time=<rfc3339|unix>&timeout=<duration>
POST body (form-urlencoded) is allowed for long queries.
```

```json
{"status":"success","data":{"resultType":"vector","result":[
  {"metric":{"__name__":"up","job":"node","instance":"nas:9100"},"value":[1435781451.781,"1"]}]}}
{"status":"error","errorType":"bad_data","error":"1:4: parse error: ..."}      // HTTP 400 (bad params), 422 (cannot execute), 503 (timeout)
```

* `resultType`: `vector` (list of `{metric,value:[unixSeconds,"stringValue"]}`), `scalar` (`[ts,"v"]`), `matrix` (range queries), `string`. **Sample values are JSON strings**: parse with invariant culture; they may be `"NaN"`, `"+Inf"`, `"-Inf"`.
* `warnings`/`infos` arrays may accompany a success. Series order is not guaranteed unless `sort()` is used. An empty `result: []` means "no data" (stale or wrong selector), not an error.
* Limits: query timeout defaults to 2 min (`--query.timeout`), instant-vector lookback 5 min (`lookback_delta`), so a series that stopped reporting up to 5 min ago still returns its last value (staleness markers end it earlier): for a "device offline" alarm query `up{instance="nas:9100"}` instead.
* Useful queries (1 s `rate` windows are too small for a 15 s scrape; use `[1m]` or at least 4 x scrape interval):

| Need | PromQL |
|---|---|
| CPU % | `100 * (1 - avg(rate(node_cpu_seconds_total{instance="nas:9100",mode="idle"}[1m])))` |
| RAM % | `100 * (1 - node_memory_MemAvailable_bytes{instance="nas:9100"} / node_memory_MemTotal_bytes{instance="nas:9100"})` |
| Disk % `/` | `100 * (1 - node_filesystem_avail_bytes{instance="nas:9100",mountpoint="/"} / node_filesystem_size_bytes{instance="nas:9100",mountpoint="/"})` |
| Net RX B/s | `sum(rate(node_network_receive_bytes_total{instance="nas:9100",device!~"lo\|veth.*\|docker.*\|br-.*"}[1m]))` |
| Hottest sensor | `max(node_hwmon_temp_celsius{instance="nas:9100"})` |
| Uptime s | `time() - node_boot_time_seconds{instance="nas:9100"}` |

UX consequence: let the user paste any PromQL and take the first sample of the first series (scalar-or-single-vector rule, error if more than one series unless a `label` selector is given).

Windows hosts: `windows_exporter` (default port 9182) uses different names (`windows_cpu_time_total`, `windows_memory_*`); not researched here, so treat as **UNVERIFIED** and prefer the VU Studio agent.

## 3. Netdata REST API (brief)

Netdata agent: default port **19999**, open by default on the LAN (`allow dashboard from` ACL in `netdata.conf`; optional bearer protection via `/api/v3/bearer_protection`). OpenAPI file: <https://github.com/netdata/netdata/blob/master/src/web/api/netdata-swagger.yaml>; browsable at <https://learn.netdata.cloud/api>. **The v1 endpoints are flagged `deprecated: true` in the current swagger**, but they still work and are the simplest; `/api/v2/data` and `/api/v3/data` are the group-by capable successors (not researched in depth, **UNVERIFIED**).

| Call | Use |
|---|---|
| `GET /api/v1/allmetrics?format=json` | latest value of **every** chart/dimension in one request (cheapest integration). Other formats: `shell`, `prometheus`, `prometheus_all_hosts`. Optional `filter=system.*` simple-pattern |
| `GET /api/v1/charts` | list of chart ids, units, dimensions |
| `GET /api/v1/data?chart=system.cpu&after=-1&points=1&group=average&format=json` | time-series query; `after` negative = seconds back, `points` = rows, `options=jsonwrap` for metadata. Response has `labels` and `data` rows `[unix_ts, dim1, dim2, ...]`; the wrapper adds `dimension_names`, `latest_values`, `view_update_every`, `min`, `max` |

`allmetrics?format=json` shape (verified in `api_v1_allmetrics.c`):

```json
{ "system.cpu": { "name": "system.cpu", "family": "cpu", "context": "system.cpu", "units": "percentage",
    "last_updated": 1700000000,
    "dimensions": { "user": {"name":"user","value":2.5}, "system": {"name":"system","value":1.0}, "idle": {"name":"idle","value":95.0} } },
  "system.ram": { "units": "MiB", "dimensions": { "free": {"value": 812.1}, "used": {"value": 5120.3}, "cached": {...}, "buffers": {...} } } }
```

Stable chart ids worth mapping (names from the proc plugin; the exact dimension set varies by host, **UNVERIFIED** per version): `system.cpu` (percentage; busy = 100 - idle), `system.load` (`load1/5/15`), `system.ram` (MiB), `system.net` (kilobits/s; the `sent` dimension is **negative**), `system.io` (KiB/s; `out` negative), `system.uptime` (seconds), `sensors.*` and `disk_space./` (GiB used/avail), `net.<iface>`. Because values are "last stored" they update at the collection interval (`update_every`, default 1 s). Netdata is GPL-3.0+ (agent) with a proprietary cloud: the local agent API needs no account.

## 4. Unraid

Three ways in, from least to most work: (A) the official GraphQL API (built in since Unraid 7.2); (B) read the emhttp state files from a container (works on every Unraid version and gives array/parity data the Glances/Prometheus route lacks); (C) a generic metrics container (Glances, section 1). Release timeline from the official release notes ([unraid/docs](https://github.com/unraid/docs/tree/main/docs/unraid-os/release-notes)): **7.2.0 (2025-10-29)** ships the API (dynamix.unraid.net 4.25.3), 7.3.0 (2026-05-12) ships 4.34.0, 7.3.2 (2026-07-08) 4.35.1, 7.4.0-rc.1 (2026-10-08) 4.37.5-2. The API is also installable on older Unraid through the Unraid Connect plugin ([docs](https://docs.unraid.net/API/)).

### 4.1 emhttp state files (`/var/local/emhttp/`)

`emhttpd` writes PHP-style INI files to `/var/local/emhttp/` (same directory as `/usr/local/emhttp/state/`; the WebGUI source uses both spellings: [webgui](https://github.com/unraid/webgui), [unraid/api paths.ts](https://github.com/unraid/api)). Files: `var.ini` (global/array state), `disks.ini` (every slot: parity, data, pools, flash), `shares.ini`, `devs.ini` (unassigned devices), `network.ini`, `nginx.ini`, `users.ini`, `sec.ini`, `sec_nfs.ini`. They live in RAM; treat as read-only.

**Format gotchas.** `parse_ini_file` style, but every value is quoted and section names are quoted: `["disk1"]` then `idx="1"`. Generic INI parsers keep or choke on the quotes in `["disk1"]`. Parse with two regexes: `^\["?(?<s>[^"\]]+)"?\]$` and `^(?<k>[A-Za-z0-9_]+)="(?<v>.*)"$`. Re-read the whole file each poll (it is rewritten by emhttpd; **bind-mount the directory, not single files**). Sizes are **KiB (1024-byte blocks)**: `size`, `fsSize`, `fsUsed`, `fsFree`, `mdResyncPos`, `mdResyncSize` (the GUI multiplies `fsSize*1024`). Booleans are `"yes"/"no"` or `"1"/"0"`. Key case differs between samples (`NAME`, `MAX_ARRAYSZ` upper-case vs `mdState`, `sbSynced` camel-case): use the exact spellings below, case-sensitively.

`var.ini` keys used here (verbatim values of the unraid/api dev fixture, a *stopped* 6.11.2 array, printed several per line to save space: the real file has one `key="value"` per line; a running array shows `fsState="Started"`, `mdState="STARTED"`, `mdColor="green-on"`; the current 7.x WebGUI source reads the same keys):

```ini
version="6.11.2"            NAME="Tower"         SYS_MODEL="Dell R710"    fsState="Stopped"   mdState="STOPPED"   mdColor="green-blink"
mdNumDisks="4"  mdNumDisabled="1"  mdNumInvalid="1"  mdNumMissing="0"  mdNumNew="0"
mdResync="0"  mdResyncPos="0"  mdResyncSize="438960096"  mdResyncDb="0"  mdResyncDt="0"  mdResyncAction="check P"  mdResyncCorr="0"
sbSynced="1586819259"  sbSynced2="1586822456"  sbSyncErrs="0"  sbSyncExit="0"  sbClean="yes"  shareMoverActive="no"  poll_attributes="1800"
```

| Key | Meaning |
|---|---|
| `mdState` | array state: `STARTED`, `STOPPED`, `NEW_ARRAY`, `RECON_DISK`, `DISABLE_DISK`, `SWAP_DSBL`, `INVALID_EXPANSION`, `PARITY_NOT_BIGGEST`, `TOO_MANY_MISSING_DISKS`, `NEW_DISK_TOO_SMALL`, `NO_DATA_DISKS` (enum in unraid/api; may appear as `ERROR:<STATE>`) |
| `fsState` | `Stopped`, `Starting`, `Started`, `Stopping`, `Formatting`, `Copying`, `Clearing` (the `switch` cases of the WebGUI `ArrayOperation.page`; the footer prints `"Array "+fsState` for any other value). `startMode="Maintenance"` marks maintenance mode (array started but not mounted); `fsProgress` is a free-text progress string (shown in blue in the footer) |
| `mdColor` | overall orb: `green-on` started and protected, `yellow-on` started but unprotected (disk emulated/invalid), `green-blink`/`yellow-blink` stopped (GUI `status_indicator()`) |
| `mdNumDisabled`, `mdNumInvalid`, `mdNumMissing` | counts; any non-zero means degraded parity protection |
| `mdResyncPos` | current position of a parity operation in 1 KiB blocks; **> 0 while an operation is running or paused**, 0 when idle |
| `mdResyncSize` | total blocks of the operation (parity disk size) |
| `mdResync` | non-zero while actively running, 0 when paused or idle (GUI uses it to show Pause vs Resume) |
| `mdResyncDt`, `mdResyncDb` | duration (s) and blocks (KiB) of the latest measurement window; `Dt == 0` means paused/idle |
| `mdResyncAction` | `check` followed by a parity name (`check P` in the fixture) = parity check; plain `check` = read-check (no parity); `recon P` / `recon Q` = parity-sync; `recon` followed by anything else = data rebuild (the second token is the disk being rebuilt, presumably a slot, **UNVERIFIED**); `clear` = disk-clear. The GUI helper `resync()` in `ArrayOperation.page` maps `P`/`Q` to "Parity-Sync" and everything else to "Data-Rebuild". **It also names the pending action while the array is idle** (the GUI builds its Check/Sync buttons from it), so only treat an operation as running when `mdResyncPos > 0` |
| `mdResyncCorr` | 1 = writing corrections to parity, 0 = read-only check (GUI labels "errors corrected" vs "detected") |
| `sbSynced`, `sbSynced2` | epoch seconds when the current/last operation started and ended (`sbSynced2` = 0 while running) |
| `sbSyncErrs`, `sbSyncExit` | errors found; exit code (0 OK, **-4 aborted/cancelled** (the GUI prints "aborted"), other non-zero = failed; per `Helpers.php::my_error` and unraid/api `parity-check-status.ts`) |

**Computations** (taken from the GUI's inline JS and the official `parity-check-status.ts`):

```text
running   = mdResyncPos > 0 && mdResyncDt > 0           paused = mdResyncPos > 0 && (mdResync == 0 || mdResyncDt == 0)      idle = mdResyncPos == 0
progress% = mdResyncSize > 0 ? 100 * mdResyncPos / mdResyncSize : 0          // GUI uses pos / (size/100 + 1), a rounding quirk
speed_B/s = mdResyncDt > 0 ? mdResyncDb * 1024 / mdResyncDt : 0              // API rounds to MB/s
eta_s     = speed > 0 ? (mdResyncSize - mdResyncPos) * 1024 / speed : null
elapsed_s = now - sbSynced      (while running; subtract paused time you track yourself)
array_used% = 100 * sum(fsUsed) / sum(fsSize)   over disks.ini sections with type="Data" and a non-empty device   // what the official API reports
```

`disks.ini` per section (`["parity"]`, `["parity2"]`, `["disk1"]`..`["disk28"]`, `["cache"]` and user-named pools, `["flash"]`):

| Key | Meaning |
|---|---|
| `idx`, `name`, `device`, `id`, `transport` | slot number (parity=0, parity2=29, data 1..28, cache 30+, flash 54), name, kernel device (`sdf`, `nvme0n1`; empty = no disk), `MODEL_SERIAL`, `ata`/`nvme`/`usb` |
| `type` | `Parity`, `Data`, `Cache`, `Flash`, `Boot` |
| `status` | `DISK_OK`, `DISK_NP` (empty slot), `DISK_NP_MISSING`, `DISK_INVALID`, `DISK_WRONG`, `DISK_DSBL` (disabled, emulated), `DISK_NP_DSBL`, `DISK_DSBL_NEW`, `DISK_NEW` |
| `color` | `green-on` active, `green-blink` spun down, `yellow-on` emulated, `red-on`/`red-off` disabled, `blue-on` new, `grey-off` no device (GUI help text) |
| `spundown` | `"1"` = in standby, `"0"` = spinning |
| `temp` | degrees C; **`*` when spun down** (emhttpd never reads temperature of standby drives to avoid waking them) and often for SSD/flash |
| `numReads`, `numWrites`, `numErrors` | counters since array start; `numErrors > 0` = unrecoverable I/O errors |
| `size` | device size KiB; `rotational` `"1"` HDD / `"0"` SSD |
| `fsType`, `fsStatus`, `fsColor`, `fsSize`, `fsUsed`, `fsFree` | filesystem (`xfs`, `btrfs`, `zfs`, `luks:xfs`...), `Mounted` or `Unmountable...`, KiB figures (**absent/0 for parity**; for a pool the figures cover the whole pool) |
| `warning`, `critical` | per-disk utilisation thresholds in % (empty = global defaults) |

Rules: never poll SMART of array disks yourself (it spins them up); keep the last numeric `temp` and show "standby" while `temp == "*"` or `spundown == "1"`. `shares.ini` sections have `name`, `useCache`, `size`/`used`/`free` (KiB, computed lazily), `color`. The refresh cadence of `fsFree` and `temp` is decided by emhttpd and SMART polling (`poll_attributes`, default 1800 s in the fixture); **UNVERIFIED how often each key changes**. For fresh usage numbers `statfs` the mount (`/mnt/disk1`, `/mnt/cache`) with `DriveInfo`.

### 4.2 The official Unraid API (GraphQL)

* **Availability.** Built in from 7.2; enable in *Settings -> Management Access -> API* (sandbox toggle under *Developer Options*); CLI `unraid-api status|start|stop|restart|apikey|developer|report` ([docs/API](https://docs.unraid.net/API/), [cli](https://docs.unraid.net/API/cli)). No Unraid Connect sign-in is needed for local use. Source: [unraid/api](https://github.com/unraid/api) (open source). Newer API releases arrive faster than OS releases, so fields differ by version (see the table above); the Unraid Connect plugin gives the newest API on older OS builds.
* **Endpoint.** `POST http://<server>/graphql` (`Content-Type: application/json`, body `{"query": "...", "variables": {}}`), WebSocket subscriptions on the same path with the `graphql-ws` protocol (`graphql.module.ts`). TLS follows *Settings -> Management Access -> Use SSL/TLS* (WebGUI help text): **No** serves plain `http://<ip>/graphql`; **Yes** redirects http to https with an auto-generated *self-signed* certificate (offer a per-server "trust this certificate" option); **Strict** redirects to `https://<lan-ip>.<hash>.myunraid.net` (valid certificate, needs working DNS). Introspection works when the sandbox/dev option is enabled.
* **Auth.** `x-api-key: <key>` header (regex `^[a-zA-Z0-9-_]+$`). Create at *Settings -> Management Access -> API Keys* or `unraid-api apikey --create --name "VU Studio" --roles VIEWER --json` (prints `{"key": "...", "name": "...", "id": "..."}`). Roles: `ADMIN`, `VIEWER` (read-only on all resources: use this), `GUEST`, `CONNECT`. Fine-grained: `--permissions "DOCKER:READ_ANY,ARRAY:READ_ANY"` (resources include `ARRAY`, `DISK`, `DOCKER`, `INFO`, `VMS`, `SHARE`, `VARS`, `NETWORK`). Cookie and OIDC auth also exist (irrelevant for us). The docs warn of **rate limiting** (limits not published, **UNVERIFIED**); poll no faster than 2-5 s, or use subscriptions.
* **Verified sample query** (validated against the v4.37.5 schema with graphql-js; trim fields that your server's API version lacks, since an unknown field fails the whole request; probe with `{ __type(name:"ParityCheck"){ fields { name } } }` when introspection is on):

```graphql
query VuStudioPoll {
  array {
    state                                              # STARTED | STOPPED | ...
    capacity { kilobytes { free used total } disks { free used total } }   # strings, KiB
    parityCheckStatus { status progress speed date duration }              # field exists since API 4.16; progress Int %, speed "MB/s" string
    # newer API only (UNVERIFIED which release): parityCheckStatus { running paused errors correcting }
    parities { name status temp isSpinning numErrors }
    disks    { name device status temp isSpinning numErrors fsSize fsFree fsUsed color }
    caches   { name status temp fsSize fsFree fsUsed }
  }
  metrics {                                            # cpu/memory exist since API 4.18 (before OS 7.2.0)
    cpu { percentTotal }
    memory { total available used percentTotal swapUsed percentSwapTotal }      # bytes (BigInt)
    temperature { summary { average } sensors { name type current { value unit } } }   # API >= 4.30 (2026-03)
    network { name rxSec txSec bytesReceived bytesSent operstate }                      # API >= 4.35 (2026-05)
  }
  docker { containers { names state status autoStart } }           # state: RUNNING | PAUSED | EXITED
  vms { domains { name state } }
  info { os { hostname uptime kernel } cpu { brand cores threads } }
  online
}
```

```json
{"data":{"array":{"state":"STARTED","capacity":{"kilobytes":{"free":"31900000000","used":"21000000000","total":"52900000000"}},
 "parityCheckStatus":{"status":"RUNNING","progress":42,"speed":"151"},
 "disks":[{"name":"disk1","status":"DISK_OK","temp":30,"isSpinning":true,"fsSize":"17576897220","fsFree":"13557363020"}]}}}
```

`ParityCheckStatus` values: `NEVER_RUN`, `RUNNING`, `PAUSED`, `COMPLETED`, `CANCELLED`, `FAILED`. Subscriptions available: `arraySubscription`, `systemMetricsCpu`, `systemMetricsMemory`, `systemMetricsNetwork`, `systemMetricsTemperature`, `dockerContainerStats`, `parityHistorySubscription`. Note `temp` is `null` for standby disks (the ini holds `*`), and many size fields are `BigInt` serialised as **strings** (or JSON numbers beyond 2^53 for `Float` fields): parse with `decimal`/`BigInteger`.
* **License/terms.** API source is open (see the repo `LICENSE.txt`); using it on your own server has no extra terms. The schema and rate limits can change between OS releases.

### 4.3 Reading host metrics from a Docker container on Unraid

What a container sees by default: `/proc/stat`, `/proc/meminfo`, `/proc/loadavg`, `/proc/uptime`, `/proc/diskstats` and `/sys/class/hwmon|thermal|power_supply|drm|block` are the **host's** (not virtualised without LXCFS). Namespaced exceptions: `/proc/net/dev` and `/sys/class/net` (container netns) and the process list (PID namespace). Docker masks some paths (e.g. `/proc/acpi`, `/sys/firmware`, and in recent Docker versions the RAPL `powercap` tree; **UNVERIFIED** on Unraid's Docker).

| Need | Setting (Unraid template field) |
|---|---|
| Host NIC counters, ICMP/UPnP to the LAN, mDNS | **Network Type: Host** (`--net=host`) |
| Host processes / per-process data | Extra Parameters `--pid=host` |
| Unraid array, parity, disk temps | Path `/var/local/emhttp` -> `/host/emhttp`, Read Only, **recursive-slave** propagation (`ro,rslave`); directory, not files |
| Array/pool free space, fresh | Path `/mnt` -> `/host/mnt`, `ro,rslave` (so `/mnt/diskN`, `/mnt/cache`, `/mnt/user` appear; `rslave` also picks up mounts made after start) |
| Docker container list/stats | Path `/var/run/docker.sock` -> same, `ro`. **Security: access to the socket equals root on the host** and `:ro` does not block API writes; make it opt-in and say so in the UI |
| Hardware sensors | nothing extra: `/sys/class/hwmon` is readable. Drive temps are not hwmon on Unraid, use `disks.ini` |
| GPU (AMD/Intel) | nothing; `/sys/class/drm` readable. NVIDIA: Unraid Nvidia driver plugin + `--runtime=nvidia` + `NVIDIA_VISIBLE_DEVICES=all` (from memory, **UNVERIFIED**) |
| ICMP ping as non-root | Docker default capabilities already include `NET_RAW`; if you `--cap-drop=ALL`, add `--cap-add=NET_RAW` |
| Not needed | `--privileged` (the third-party "unraid-stats" CA template uses it plus `/proc` and `/sys` mapped to `/host/proc` and `/host/sys` with `ro,rslave`, `/var/run/mdstat` and the `mdcmd` socket, but our needs are read-only; [listing](https://ca.unraid.net/apps/unraid-stats-19sh84u0fr38gy)) |

### 4.4 Community Applications template for the VU Studio agent

Docker templates are XML (`Container version="2"`), read by the WebGUI's DockerMan and listed by Community Applications (CA) from a public Git repo. A user-installed copy lives in `/boot/config/plugins/dockerMan/templates-user/my-<Name>.xml`. Field names below are taken from the WebGUI source (`CreateDocker.php`: Config attributes `Name,Target,Default,Mode,Description,Type,Display,Required,Mask`; `Type` = `Path|Port|Variable|Label|Device`; `Display` = `always|always-hide|advanced|advanced-hide`) and the official sample [`Tailscale-Sidecar-UNRAID.xml`](https://github.com/unraid/docker-templates/blob/master/limetech/Tailscale-Sidecar-UNRAID.xml). Submission now goes through the portal <https://ca.unraid.net/submit> (field reference at `/submit/help/repository-xml`, **not fetchable here, so its exact required-field list is UNVERIFIED**); the repo also needs a `ca_profile.xml` maintainer profile (`<Maintainer><Icon/><Profile/><WebPage/><Forum/>...`).

```xml
<?xml version="1.0"?>
<Container version="2">
  <Name>vustudio-agent</Name>
  <Repository>ghcr.io/OWNER/vustudio-agent:latest</Repository>
  <Registry>https://github.com/OWNER/VuStudio/pkgs/container/vustudio-agent</Registry>
  <Network>host</Network>
  <MyIP/>
  <Shell>sh</Shell>
  <Privileged>false</Privileged>
  <Support>https://github.com/OWNER/VuStudio/issues</Support>
  <Project>https://github.com/OWNER/VuStudio</Project>
  <Overview>Reports CPU, RAM, network, disk, temperatures, array and parity state of this Unraid server to VU Studio dials.</Overview>
  <Category>Tools:System Status:Stable</Category>
  <WebUI>http://[IP]:[PORT:8741]/</WebUI>
  <TemplateURL>https://raw.githubusercontent.com/OWNER/VuStudio/main/unraid/vustudio-agent.xml</TemplateURL>
  <Icon>https://raw.githubusercontent.com/OWNER/VuStudio/main/unraid/icon.png</Icon>
  <ExtraParams>--pid=host --restart=unless-stopped</ExtraParams>
  <PostArgs/><CPUset/><DateInstalled/><DonateText/><DonateLink/><Requires/>
  <Config Name="Agent port" Target="8741" Default="8741" Mode="tcp" Description="Port VU Studio connects to" Type="Port" Display="always" Required="true" Mask="false">8741</Config>
  <Config Name="Access token" Target="VUS_TOKEN" Default="" Mode="" Description="Shared secret sent by VU Studio" Type="Variable" Display="always" Required="true" Mask="true"/>
  <Config Name="Unraid state files" Target="/host/emhttp" Default="/var/local/emhttp" Mode="ro,rslave" Description="Array, parity and disk status" Type="Path" Display="advanced" Required="false" Mask="false">/var/local/emhttp</Config>
  <Config Name="Array mounts" Target="/host/mnt" Default="/mnt" Mode="ro,rslave" Description="Free space of disks and pools" Type="Path" Display="advanced" Required="false" Mask="false">/mnt</Config>
  <Config Name="Docker socket (optional)" Target="/var/run/docker.sock" Default="" Mode="ro" Description="Container list. Grants root-equivalent access; leave empty to disable." Type="Path" Display="advanced" Required="false" Mask="false"/>
  <Config Name="Settings" Target="/config" Default="/mnt/user/appdata/vustudio-agent" Mode="rw" Description="Persistent settings" Type="Path" Display="advanced" Required="true" Mask="false">/mnt/user/appdata/vustudio-agent</Config>
</Container>
```

Notes: with `Network=host` the Port entry is informational; `[PORT:8741]` in `WebUI` is resolved from that Config. Prefer GHCR for `Repository` (Docker Hub rate-limits anonymous pulls). Multi-arch images are not needed for Unraid (x64). Publish a pinned tag as well as `latest`. Categories seen in official templates: `Tools:System`, `Network:VPN`, `Productivity:`; the full list lives in the CA portal docs (**UNVERIFIED**).

## 5. Steam Deck (SteamOS 3.x, Arch-based)

No Valve documentation exists for these paths; everything below is from kernel sources (Valve's `steamdeck-hwmon` driver, mainline `amdgpu`/`k10temp` docs) and from several independent open-source readers that run on real Decks: [mpsijben/steamdeck-mcp](https://github.com/mpsijben/steamdeck-mcp) (`hardware.py`), [dillon1000/deck-usb](https://github.com/dillon1000/deck-usb) (`deck-stats.hpp`), [skobkin/amdgputop-web](https://github.com/skobkin/amdgputop-web) (`quirks.go`), [AndersonQ/steamdeck-sensors-api](https://github.com/AndersonQ/steamdeck-sensors-api) (real `sensors -j` structure), MangoHud ([cpu.cpp](https://github.com/flightlessmango/MangoHud)). Nothing here was run on a Deck in this research, so **treat device names as "discover by `name`, never by index"**.

### 5.1 Identify the machine

`/sys/class/dmi/id/sys_vendor` = `Valve`; `/sys/class/dmi/id/product_name` (also `board_name`) = **`Jupiter` (LCD)** or **`Galileo` (OLED)**. APU PCI device id (`/sys/class/drm/card*/device/device`): `0x163f` Van Gogh (LCD), `0x1435` (OLED APU). Use these to switch on Deck-specific defaults (fan max, battery capacity).

### 5.2 Sensor map (all files world-readable; values are raw sysfs units)

Chip list in the data model of a third-party tool that wraps `sensors -j` on a Deck: `nvme-pci-0100`, `BAT1-acpi-0`, `amdgpu-pci-0400`, `steamdeck_hwmon-isa-0000`, `acpitz-acpi-0`; `k10temp` appears in newer readers' lookup tables (it exists in mainline for Van Gogh, family 17h model 90h: `PCI_DEVICE_ID_AMD_17H_M90H_DF_F3`, label `Tctl`) but is absent from the one `sensors -j` data model available (AndersonQ), so fall back in this order.

| Metric | Source (find the hwmon directory whose `name` file matches) | Unit and notes |
|---|---|---|
| **APU/CPU temperature** | `k10temp` `temp1_input` (Tctl); else `amdgpu` `temp1_input` (label `edge`, the APU die); else `acpitz` `temp1_input` | millidegree C. Do **not** use `steamdeck_hwmon`: its only `temp1_input` is the *battery* temperature (label "Battery Temp") |
| **GPU temperature** | `amdgpu` `temp1_input` (`edge`) | m°C |
| **Package power** | `amdgpu` `power1_average` (label `slowPPT`), `power1_input` (instantaneous), `power1_cap` (TDP limit), `power1_cap_default`; `power2_average`/`power2_cap` = fast PPT | **microwatt**; on APUs this includes the CPU ([amdgpu_pm.c doc](https://github.com/torvalds/linux/blob/master/drivers/gpu/drm/amd/pm/amdgpu_pm.c)). `power1_cap_default` is the stock TDP limit |
| **GPU busy** | `/sys/class/drm/card*/device/gpu_busy_percent` (the card whose `device/vendor` is `0x1002`) | 0-100 %; `mem_busy_percent` next to it |
| **GPU clock** | `amdgpu` hwmon `freq1_input` (SCLK, **Hz**); `device/pp_dpm_sclk` lists levels `0: 200Mhz`, `1: 1600Mhz *` (`*` = current level) | MHz in `pp_dpm_*`, Hz in hwmon |
| **GPU voltage** | `amdgpu` `in0_input` (vddgfx), `in1_input` (vddnb) | mV |
| **VRAM/GTT** | `device/mem_info_vram_used`, `mem_info_vram_total`, `mem_info_gtt_used`, `mem_info_gtt_total` | **bytes**. VRAM is only the BIOS carve-out (UMA); games mostly live in GTT, so show `vram_used + gtt_used` or system RAM instead |
| **Fan RPM** | `steamdeck_hwmon` `fan1_input` (older kernels registered it as `jupiter`: match both); also `fan1_target`, `fan1_fault` | RPM, 0 to about 7300 (driver clamps targets to 7300; community configs use 7309 as max). MangoHud treats it as true RPM (divisor 1) |
| **Battery** | `/sys/class/power_supply/BAT1/`: `capacity` (%), `status` (`Charging`, `Discharging`, `Not charging`, `Full`), `voltage_now` (**µV**), `current_now` (**µA**), `charge_now`/`charge_full`/`charge_full_design` (**µAh**; the Deck reports *charge*, not *energy*), `voltage_min_design`, `cycle_count` | Power W = `abs(current_now) * voltage_now / 1e12`; remaining Wh = `charge_now * voltage_min_design / 1e12`; health % = `charge_full / charge_full_design`. Direction comes from `status` (do not rely on the sign of `current_now`) |
| **Battery temperature** | `steamdeck_hwmon` `temp1_input` | m°C |
| **Charger / USB-PD** | `/sys/class/power_supply/ACAD/online` (1 = adapter present); `steamdeck_hwmon` `in0_input` (PD contract voltage, mV) and `curr1_input` (PD contract current, mA) | PD values are what the EC negotiated, not measured |
| **Charge limit** | `steamdeck_hwmon/max_battery_charge_level` (0 = no limit, else percent), `max_battery_charge_rate` | read-only for us (writes need root via `steamos-polkit-helpers/steamos-priv-write`) |
| **CPU frequency** | `/sys/devices/system/cpu/cpu*/cpufreq/scaling_cur_freq` | kHz |
| **RAM / swap / load / uptime / storage** | `/proc/meminfo` (`MemTotal - MemAvailable`), `/proc/loadavg`, `/proc/uptime`, `statvfs("/home")` and `/run/media/*` (SD card) | see section 6 |

Battery discovery rule that avoids picking a controller's battery: iterate `/sys/class/power_supply/*`, keep entries with `type == Battery` and `scope != Device` (HID peripherals report `scope=Device`). Also read `hwmon/*/name` fresh at start-up: hwmon numbers change between boots.

**LCD versus OLED.** Same sysfs layout. Differences that matter: DMI `Jupiter` vs `Galileo`, different APU PCI id, bigger battery on the OLED (about 50 Wh vs 40 Wh from public specs, not re-verified: read `charge_full_design` instead of hard-coding). `k10temp` presence on either model is **UNVERIFIED**.

### 5.3 Running the agent on SteamOS

* **Read-only root.** `/` and `/usr` are an A/B image replaced on every update; `sudo steamos-readonly disable` allows writes but anything under `/usr` (including pacman-installed packages and their keyrings) is **wiped by the next update**. `/etc` is an overlay whose changes are discarded on an atomic update except files listed in `/usr/lib/rauc/atomic-update-keep.conf` or in drop-ins under `/etc/atomic-update.conf.d/*.conf` (SteamOS 3.6+, per third-party installers such as [SteamDeck_rEFInd](https://github.com/jlobue10/SteamDeck_rEFInd)). **`/home` (the `deck` user) and `/var` survive updates.**
* **Install the agent in `$HOME`** as a self-contained single-file .NET binary: `~/.local/share/vustudio-agent/`, no pacman, no root, no `.NET` runtime install.
* **User systemd unit** (survives updates because it lives in `/home`):

```ini
# ~/.config/systemd/user/vustudio-agent.service
[Unit]
Description=VU Studio agent
[Service]
ExecStart=%h/.local/share/vustudio-agent/vustudio-agent --port 8741
Restart=always
RestartSec=5
[Install]
WantedBy=default.target
```

```bash
systemctl --user daemon-reload && systemctl --user enable --now vustudio-agent
sudo loginctl enable-linger deck      # optional but recommended; needs a sudo password set with `passwd` in Desktop Mode
```

  User units cannot order themselves after system targets such as `network-online.target`; rely on `Restart=always` and agent-side retry. SteamOS ships with lingering **off** (documented by third-party guides, e.g. [bristledev/steam-deck](https://github.com/bristledev/steam-deck)); with auto-login the `deck` user manager already starts at boot, and lingering keeps it alive across a Game Mode / Desktop Mode switch (a session change; behaviour **UNVERIFIED** on current SteamOS).
* **Game Mode vs Desktop Mode.** Game Mode runs `gamescope-session` (the `gamescope` process; detect it with `pgrep -x gamescope` / `gamescope-wl`), Desktop Mode runs Plasma. User-manager services keep running in both. The Deck suspends aggressively: the agent will vanish from the LAN during sleep, so VU Studio must show "offline/sleeping" instead of an error, and reconnect on its own. There is no firewall by default on SteamOS (**UNVERIFIED**).

### 5.4 FPS (difficulty: high, optional)

Steam's own overlay (`mangoapp`) reads `MANGOHUD_CONFIG`/`MANGOHUD_CONFIGFILE` (Steam points the latter at a temporary file in Game Mode: **UNVERIFIED**) and gets frame times from gamescope over a SysV message queue (`ftok("mangoapp",65)`, a single consumer: **do not read that queue**). Two feasible routes:

1. **MangoHud CSV logging per game** (medium): Steam launch option `MANGOHUD_CONFIG=output_folder=/home/deck/.cache/vu-mh,autostart_log=1,log_interval=250 mangohud %command%`. MangoHud appends and flushes one row per interval to `<output_folder>/<game>_<yyyy-mm-dd_hh-mm-ss>.csv`; tail the newest file. Format (MangoHud master): 2 header lines (`os,cpu,gpu,ram,kernel,driver,cpuscheduler` + values), then `fps,frametime,cpu_load,cpu_power,gpu_load,cpu_temp,gpu_temp,gpu_core_clock,gpu_mem_clock,gpu_vram_used,gpu_power,ram_used,swap_used,process_rss,cpu_mhz,elapsed` (`elapsed` in ns). Needs the user to edit launch options per game (or a Decky plugin).
2. **Global**: `mangoapp` also contains the logger and reads `MANGOHUD_CONFIG`/`MANGOHUD_CONFIGFILE`, so a global `output_folder` might log every Game Mode game; **UNVERIFIED on current SteamOS** (Steam may overwrite its config file). Alternatively poll `gpu_busy_percent` as a proxy for "game running".

## 6. Generic Linux metrics from `/proc` and `/sys` (agent design)

A pure-.NET agent needs no native dependency for any of this. Sources: [proc.rst](https://github.com/torvalds/linux/blob/master/Documentation/filesystems/proc.rst), [iostats.rst](https://github.com/torvalds/linux/blob/master/Documentation/admin-guide/iostats.rst), [hwmon sysfs ABI](https://github.com/torvalds/linux/blob/master/Documentation/ABI/testing/sysfs-class-hwmon), [power_supply_class.rst](https://github.com/torvalds/linux/blob/master/Documentation/power/power_supply_class.rst). Rules for all files: read with a fresh `FileStream`/`File.ReadAllText` each time (procfs/sysfs have no stable size, do not `Seek` and re-read), tolerate `IOException`/`UnauthorizedAccessException` per file, never assume a file exists, keep the previous sample per counter, and apply the counter rule of section 2.3 (reset or wrap gives "no value for one tick").

| Metric | File and parsing | Formula |
|---|---|---|
| **CPU usage** | `/proc/stat`, first line: `cpu  user nice system idle iowait irq softirq steal guest guest_nice` (ticks of `USER_HZ`, 100/s; `cpuN` lines per core). Real sample: `cpu  70040 0 22859 1437108 896 0 2225 4568 0 0` | `total = user+nice+system+idle+iowait+irq+softirq+steal` (**exclude guest/guest_nice: already inside user/nice**); `idle_all = idle+iowait`; `busy% = 100*(1 - d(idle_all)/d(total))`. iowait is documented as unreliable; counting it as idle is a common choice |
| **Memory** | `/proc/meminfo`, `Key:   value kB` (kibibytes). `MemTotal`, `MemAvailable`, `MemFree`, `Buffers`, `Cached`, `SwapTotal`, `SwapFree` | `used = MemTotal - MemAvailable` (kernel's estimate of reclaimable memory), `used% = 100*used/MemTotal`; swap `used = SwapTotal - SwapFree`. Containers see the host values |
| **Load, uptime** | `/proc/loadavg` (`0.52 0.61 0.58 2/871 12345`), `/proc/uptime` (`seconds idle_seconds`) | normalise load: `load1 / logical_cores * 100` |
| **Network** | `/proc/net/dev`: two header lines then `  iface: rx_bytes rx_packets rx_errs rx_drop rx_fifo rx_frame rx_compressed rx_multicast tx_bytes tx_packets tx_errs tx_drop tx_fifo tx_colls tx_carrier tx_compressed`. Real: `lo: 1420967974  124922 0 0 0 0 0 0 1420967974 124922 0 0 0 0 0 0`. Split on `:` first (names can touch the colon), then whitespace | `B/s = d(bytes)/dt`. The kernel prints 64-bit counters; still keep the reset rule (driver reloads, interface re-creation). Skip `lo`, `docker*`, `veth*`, `br-*`, `virbr*`. Link speed: `/sys/class/net/<if>/speed` (Mbit/s, -1 for Wi-Fi/virtual), `operstate`, `carrier` |
| **Disk I/O** | `/proc/diskstats` (or `/sys/block/<dev>/stat`, same 17 fields minus 3 prefix columns): `major minor name f1..f17`; f1 reads completed, f3 **sectors read**, f4 ms reading, f5 writes completed, f7 **sectors written**, f8 ms writing, f9 I/Os in flight (gauge), f10 **ms doing I/O**, f11 weighted ms. Newer kernels append discard/flush fields. Fields are `unsigned long` (iostats.rst): they wrap at 2^32 on 32-bit kernels (2 TiB of sectors), so keep the wrap rule. Real: `7 0 loop0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0` | **sector = 512 bytes always**, whatever the device sector size: `read B/s = d(f3)*512/dt`; `busy% = 100*d(f10)/(dt*1000)` clamped to 100. List whole disks only: `sd[a-z]+`, `nvme\d+n\d+`, `vd[a-z]+`, `mmcblk\d+`, `md\d+`; drop partitions (`sda1`, `nvme0n1p1`), `loop*`, `ram*`; keep `dm-*` only on demand (double counts the disks below) |
| **Filesystems** | `/proc/self/mounts` + `DriveInfo` (`TotalSize`, `AvailableFreeSpace`) or `statvfs` | exclude `tmpfs`, `devtmpfs`, `overlay`, `squashfs`, `proc`, `sysfs`, `cgroup*`, bind mounts of the same device; `used% = 100*(1 - avail/total)` |
| **hwmon** | `/sys/class/hwmon/hwmonN/` (N unstable across boots): `name` (driver: `k10temp`, `coretemp`, `amdgpu`, `nvme`, `acpitz`, `steamdeck_hwmon`, `cpu_thermal`, `pwmfan`...), `tempK_input` + optional `tempK_label`, `fanK_input`, `inK_input`, `currK_input`, `powerK_average`/`powerK_input`, `energyK_input`, `humidityK_input`, `freqK_input` (amdgpu). Numbering: `in` from 0, all others from 1. `device` symlink = PCI/platform path to tell two chips with the same `name` apart | units from the kernel ABI: **temp = millidegree C**, fan = RPM, in = **mV**, curr = **mA**, power = **µW**, energy = **µJ**, humidity = milli-percent. Identify a sensor by `(name, device path, label or tempK)`, not by `hwmonN`. A fan reading `EIO` means fault; treat a file read error as "no value" |
| **thermal zones** | `/sys/class/thermal/thermal_zoneN/temp` (millidegree C) with `type` (`x86_pkg_temp`, `acpitz`, `cpu-thermal` on Raspberry Pi, `soc_thermal`...); `cooling_deviceN/cur_state` | fallback when no useful hwmon exists; Raspberry Pi: `thermal_zone0/temp` equals `vcgencmd measure_temp` |
| **Batteries, chargers** | `/sys/class/power_supply/*/`: `type` (`Battery`, `Mains`, `USB`, `UPS`, `Wireless`), `scope` (`System` or `Device`), `status` (`Unknown`, `Charging`, `Discharging`, `Not charging`, `Full`), `capacity` (0-100 %), `voltage_now` (**µV**), `current_now` (**µA**; sign is driver dependent), `power_now` (**µW**, if present), `energy_now`/`energy_full`/`energy_full_design` (**µWh**) **or** `charge_now`/`charge_full`/`charge_full_design` (**µAh**) (a battery reports one family), `temp` (**tenths of °C**, not milli), `cycle_count`, `online` (Mains) | `power W = power_now/1e6` or `abs(current_now)*voltage_now/1e12`; `Wh = energy_now/1e6` or `charge_now*voltage_now/1e12`; `health% = full/full_design`. Pick `type==Battery && scope!=Device` |
| **CPU frequency** | `/sys/devices/system/cpu/cpuN/cpufreq/scaling_cur_freq` (kHz), `cpuinfo_max_freq` | average over cores |
| **AMD GPU** | `/sys/class/drm/cardN/device/` where `vendor == 0x1002`: `gpu_busy_percent`, `mem_busy_percent` (0-100), `mem_info_vram_used/total` (bytes), `mem_info_gtt_used/total`, `pp_dpm_sclk` / `pp_dpm_mclk` (`1: 1600Mhz *`), and `hwmon/hwmonX/`: `temp1_input` (edge), `temp2_input` (junction, dGPU), `power1_average` (µW), `fan1_input`, `pwm1` (0-255), `freq1_input` (Hz), `in0_input` (mV) | all world-readable ([amdgpu_pm.c](https://github.com/torvalds/linux/blob/master/drivers/gpu/drm/amd/pm/amdgpu_pm.c)); ignore `cardN-HDMI-*` connector entries |
| **NVIDIA GPU** | `nvidia-smi --query-gpu=index,name,utilization.gpu,utilization.memory,memory.used,memory.total,temperature.gpu,power.draw,power.limit,fan.speed,clocks.sm,clocks.mem --format=csv,noheader,nounits` | see below |

**nvidia-smi** (proprietary driver tool; field names as used by many open-source exporters, e.g. [Monitorix](https://github.com/mikaku/Monitorix) `nvidiagpu.pm`, [gpu-hot](https://github.com/psalias2006/gpu-hot); not re-fetched from NVIDIA docs): one line per GPU, comma+space separated, `nounits` strips units: `0, NVIDIA GeForce RTX 3080, 12, 5, 1024, 10240, 45, 35.20, 320.00, 30, 210, 405`. Units: % , % , MiB, MiB, °C, W, W, % (fan duty), MHz, MHz. Unsupported fields print `[N/A]` or `[Not Supported]` (parse defensively: laptops report no fan and sometimes no power). Cost: each call spawns a process and talks to the driver (tens of ms or more, measure): run **one long-lived process** with `-l 1` (seconds) or `-lms 1000` and read lines as they arrive. Windows path: `C:\Windows\System32\nvidia-smi.exe` (modern drivers) or `C:\Program Files\NVIDIA Corporation\NVSMI\`. On Windows prefer LibreHardwareMonitor (NVAPI) which the app already embeds.

**Raspberry Pi** ([vcgencmd man page](https://github.com/raspberrypi/utils/blob/master/vcgencmd/vcgencmd.1)):

| Command | Output | Notes |
|---|---|---|
| `vcgencmd measure_temp` | `temp=47.2'C` | SoC temperature; equals `/sys/class/thermal/thermal_zone0/temp / 1000`. `vcgencmd` talks to `/dev/vcio` (the user must typically be in the `video` group); sysfs needs nothing |
| `vcgencmd get_throttled` | `throttled=0x50005` | bit mask: **0** under-voltage now, **1** ARM frequency capped now, **2** throttled now, **3** soft temperature limit now; **16** under-voltage has occurred, **17** frequency capping has occurred, **18** throttling has occurred, **19** soft limit has occurred (latched since boot). `0x0` = healthy. Great for a red "power supply" alarm |
| `vcgencmd measure_clock arm` | `frequency(48)=1500398464` | Hz (also `core`, `gpu`...) |
| `vcgencmd measure_volts core` | `volt=0.8438V` | |
| Pi 5 fan | `/sys/class/hwmon/hwmonX/fan1_input` (driver name `pwmfan`) | **UNVERIFIED** driver name |

Process model for the agent: one timer thread at 1 Hz reading everything above (cost: roughly 30-100 small file reads), publishing a flat `Dictionary<string,double>` with ids like `cpu.total`, `mem.used_pct`, `net.eth0.rx_bps`, `disk.sda.busy_pct`, `hwmon.k10temp.Tctl`, `power.BAT1.watts`. Heavy items (filesystem usage, `nvidia-smi`, `vcgencmd`) get their own slower timers (10-60 s).

## 7. Router and internet connection

Typical owner topology: ISP router (TIM/Vodafone/Fastweb/Iliad, LAN e.g. `192.168.1.0/24`) -> **Google Nest Wifi router** on its WAN port (LAN `192.168.86.0/24`) -> PC. This is double NAT: SSDP multicast and UPnP from the ISP router never reach the PC. Layered plan: (1) ping/loss/latency (works everywhere), (2) Nest `status` JSON, (3) UPnP IGD counters of whichever router exposes them, queried by unicast.

### 7.1 Latency, packet loss, default gateway (.NET 10)

* `System.Net.NetworkInformation.Ping`: **use `SendPingAsync(host, timeoutMs, buffer, PingOptions)`** (`SendAsync` is the old void/event API). `PingReply`: `Status` (`Success`, `TimedOut`, `TtlExpired`, `DestinationHostUnreachable`...), `RoundtripTime` (ms, integer), `Address`, `Options.Ttl`. Tested on .NET 10: 127.0.0.1 -> `Success`, 1 ms, TTL 64.
* Privileges (source: [dotnet/runtime `Ping.Unix.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Ping/src/System/Net/NetworkInformation/Ping.Unix.cs)): Windows uses `IcmpSendEcho` (no admin). On Linux .NET uses a raw ICMP socket **if the process may** (root or `CAP_NET_RAW`; Docker's default caps include it), **otherwise it spawns the system `ping` binary** and parses its output (slower, needs `iputils`/busybox `ping` in the image; a distroless container without ping fails). On the Steam Deck the `deck` user falls back to `/usr/bin/ping`.
* Measure: 1 echo per second per target, 32-byte payload, timeout 1000 ms; keep a ring of the last N=30 results: `loss% = lost/N`, `latency = median(RoundtripTime)`, `jitter = mean(|rtt[i]-rtt[i-1]|)`. Targets: default gateway (LAN/Wi-Fi health), upstream ISP router (hop 2), two public anchors (`1.1.1.1`, `9.9.9.9` or `8.8.8.8`) so one provider outage is not read as "internet down". Some routers deprioritise or rate-limit ICMP to themselves: gateway RTT of 3-10 ms spikes does not mean a problem. If ICMP is blocked, fall back to the TCP connect time to `:53`/`:443`.
* **Default gateway, tested snippet** (route-based, picks the NIC that really carries internet traffic, ignores VPN/virtual adapters that have no gateway): connect a UDP socket to a public IP (nothing is sent) to learn the outgoing local IP, then find the `NetworkInterface` that owns it and return its first IPv4 `GatewayAddresses` entry. .NET parses `/proc/net/route` on Linux and `GetAdaptersAddresses` on Windows; it exposes no route metric, hence the trick.

```csharp
using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
s.Connect(IPAddress.Parse("1.1.1.1"), 53);
var local = ((IPEndPoint)s.LocalEndPoint!).Address;
var gw = NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
    .Select(n => n.GetIPProperties())
    .Where(p => p.UnicastAddresses.Any(u => u.Address.Equals(local)))
    .SelectMany(p => p.GatewayAddresses).Select(g => g.Address)
    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
```

* **Find the ISP router behind the Nest (double NAT)**: ping `1.1.1.1` with `new PingOptions(ttl: 2, dontFragment: true)`; a router answers `IPStatus.TtlExpired` and `reply.Address` is hop 2 (private address such as `192.168.1.1` = double NAT; a public/CGNAT `100.64.0.0/10` address = ISP side). Repeat with TTL 1 for hop 1. This is standard ICMP behaviour (routers may rate-limit or silently drop it, then fall back to the Nest status field below or ask the user for the IP).

### 7.2 Google Nest Wifi / Google Wifi local API

* **What exists.** An undocumented, unauthenticated HTTP JSON API on the router unit: `GET http://192.168.86.1/api/v1/status` (or `http://testwifi.here/api/v1/status`; the address is the Nest *router* point, not a mesh point). It was reverse-engineered for Google Wifi/OnHub ([olssonm/google-wifi-api](https://github.com/olssonm/google-wifi-api), 2017) and is still consumed in 2026 by the Home Assistant `google_wifi` integration and the MIT library [EReaso/GoogleWifiApi](https://github.com/EReaso/GoogleWifiApi) (release 0.1.2 on 2026-10-10), and by a Nest-Wifi-specific dashboard ([n0sfs/home-api-dashboard](https://github.com/n0sfs/home-api-dashboard)) that reads `meshInfo`. A Google Nest Community thread reports it working on Nest Wifi Pro as well (summary seen via search; **UNVERIFIED on your hardware and firmware: probe it**).
* **Response** (field names from the library's model and test fixture; values illustrative):

```json
{"dns":{"mode":"automatic","servers":["192.168.1.1"]},
 "setupState":"GWIFI_OOBE_COMPLETE",
 "software":{"softwareVersion":"15393.xxx","updateChannel":"stable-channel","updateNewVersion":"0.0.0.0","updateProgress":0.0,"updateRequired":false,"updateStatus":"idle","blockingUpdate":1},
 "system":{"uptime":3600,"hardwareId":"GALE ...","modelId":"...","groupRole":"root","lan0Link":true,"countryCode":"it","ledAnimation":"CONNECTED","ledIntensity":6},
 "meshInfo":{"mesh-channel":36},
 "vorlonInfo":{"migrationMode":"voobed"},
 "wan":{"online":true,"ethernetLink":true,"ipMethod":"dhcp","localIpAddress":"192.168.1.23","gatewayIpAddress":"192.168.1.1","ipPrefixLength":24,"nameServers":["192.168.1.1"],"leaseDurationSeconds":86400,"captivePortal":false,"pppoeDetected":false,"vlanScanComplete":true}}
```

  Useful for dials: `wan.online` (bool, "internet reachable" as judged by the router), `wan.ethernetLink`, `system.uptime` (s), `software.updateStatus`/`updateNewVersion` (`0.0.0.0` = none), `wan.localIpAddress` (the Nest's WAN address; despite the name it is the router's own WAN IP, i.e. a private LAN IP of the ISP router in double NAT), **`wan.gatewayIpAddress` = LAN IP of the upstream ISP router** (use it as the UPnP/ping target). Poll no faster than every 10 s (the dashboard clamps at 10 s and notes the API can stall while the router is busy, e.g. during a speed test).
* **Traffic counters: none.** The status JSON has no byte counters; the Google Home app's usage graphs are cloud-side. Other endpoints from the 2017 notes (`/api/v1/wan-configuration`, `set-led-state`, `diagnostic-report`...) are setup/diagnostic calls: do not use.
* **UPnP on Nest/Google Wifi.** The Google Home app reportedly has a UPnP toggle (**UNVERIFIED**), and a user reported that `upnpc` found a valid IGD on a Nest Wifi router, but with **`GetTotalBytesReceived/Sent` stuck at 4294967295 (0xFFFFFFFF)** while packet counters moved ([Google Nest Community thread "Is upnp IGD2 supported?"](https://www.googlenestcommunity.com/t5/Nest-Wifi/Is-upnp-IGD2-supported/td-p/427860), page not fetchable here; **UNVERIFIED for current firmware**). Treat 0xFFFFFFFF as "counter unsupported" and use the ISP router or the host NIC instead. Default state of the UPnP toggle: **UNVERIFIED**.

### 7.3 UPnP IGD: discovery, description, SOAP

Reference implementations read for this section: miniupnpc/miniupnpd ([miniupnp/miniupnp](https://github.com/miniupnp/miniupnp), BSD) and Home Assistant's `async_upnp_client` 0.49 ([StevenLooman/async_upnp_client](https://github.com/StevenLooman/async_upnp_client), Apache-2.0). Specs: UPnP IGD:1/2 and `WANCommonInterfaceConfig:1` ([upnp.org PDF](https://upnp.org/specs/gw/UPnP-gw-WANCommonInterfaceConfig-v1-Service.pdf), not fetched: behaviour verified from code instead).

**Step 1: SSDP M-SEARCH** (UDP). Send to `239.255.255.250:1900` from an ephemeral port, once per NIC (set `SocketOptionName.MulticastInterface` and bind to that NIC's IPv4; `MulticastTimeToLive` 2-4), wait about 3 s for unicast replies:

```text
M-SEARCH * HTTP/1.1\r\n
HOST: 239.255.255.250:1900\r\n
ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n
MAN: "ssdp:discover"\r\n
MX: 2\r\n
\r\n
```

Send one request per `ST`: `urn:schemas-upnp-org:device:InternetGatewayDevice:1`, `...:InternetGatewayDevice:2`, `urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1`, `urn:schemas-upnp-org:service:WANIPConnection:1`, `urn:schemas-upnp-org:service:WANPPPConnection:1`, `upnp:rootdevice` (miniupnpc's list; its `InternetGatewayDevice:2` / `WANIPConnection:2` entries are compiled out with `#if 0` in the current source, while `async_upnp_client` accepts both `:1` and `:2`). There is no need to bind UDP 1900 (Windows' SSDP Discovery service listens there); an ephemeral source port receives the unicast replies. Reply (headers case-insensitive; take `LOCATION`, `ST`, `USN`, `SERVER`):

```text
HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nUSN: uuid:...::urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nEXT:\r\nSERVER: Linux/4.9 UPnP/1.1 MiniUPnPd/2.2.1\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n
```

**Unicast variant for double NAT.** Multicast does not cross the Nest's NAT. Send the same M-SEARCH to `<router-ip>:1900` as a unicast datagram (the reply parsing was tested against a local responder; whether a given ISP router answers unicast is **UNVERIFIED**), or skip SSDP and fetch the description from commonly seen paths on the router IP (from memory, **UNVERIFIED**): `http://<ip>:5000/rootDesc.xml` (miniupnpd; example URL in the Gladys docs), `:49000/igddesc.xml` (AVM), `:52869/picsdesc.xml`, `:37215/`; or let the user paste the description URL (Home Assistant and Gladys do the same).

**Step 2: GET the description** (`LOCATION`, `Accept: text/xml`, 3 s timeout). Structure (miniupnpd `rootDesc.xml`; other vendors identical in shape, different URLs):

```xml
<root xmlns="urn:schemas-upnp-org:device-1-0"><specVersion><major>1</major><minor>0</minor></specVersion>
 <device><deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType><friendlyName>...</friendlyName><manufacturer>...</manufacturer><modelName>...</modelName>
  <deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
   <serviceList><service><serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType><serviceId>urn:upnp-org:serviceId:WANCommonIFC1</serviceId>
     <SCPDURL>/WANCfg.xml</SCPDURL><controlURL>/ctl/CmnIfCfg</controlURL><eventSubURL>/evt/CmnIfCfg</eventSubURL></service></serviceList>
   <deviceList><device><deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
     <serviceList><service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>
       <SCPDURL>/WANIPCn.xml</SCPDURL><controlURL>/ctl/IPConn</controlURL><eventSubURL>/evt/IPConn</eventSubURL></service></serviceList>
   </device></deviceList></device></deviceList></device></root>
```

Locate services by **prefix match** on `serviceType` (`urn:schemas-upnp-org:service:WANCommonInterfaceConfig:`, `...:WANIPConnection:` **or** `...:WANPPPConnection:`: PPPoE lines use the latter; versions 1 and 2 exist), as miniupnpc's `igd_desc_parse.c` does. Resolve `controlURL` against `<URLBase>` if present, else against the `LOCATION` origin. `/ctl/CmnIfCfg`, `/ctl/IPConn`, `/WANCfg.xml` are miniupnpd defaults, **never hard-code them** (other vendors use other paths). Some routers list two `WANDevice`s or several `WANConnectionDevice`s; pick the connection whose `GetStatusInfo` says `Connected` and whose external IP is not `0.0.0.0`.

**Step 3: SOAP call** (HTTP POST to `controlURL`; exact framing from miniupnpc `minisoap.c` and tested against a local fake IGD in .NET 10). No authentication; one connection per call is fine (`Connection: close`).

```http
POST /ctl/CmnIfCfg HTTP/1.1
Host: 192.168.1.1:5000
Content-Type: text/xml; charset="utf-8"
SOAPAction: "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1#GetTotalBytesReceived"
Content-Length: 296
Connection: close

<?xml version="1.0"?>
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body><u:GetTotalBytesReceived xmlns:u="urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1"></u:GetTotalBytesReceived></s:Body></s:Envelope>
```

Framing rules (from miniupnpc, then reproduced with .NET 10 `HttpClient` against a raw-socket fake IGD):

* The `SOAPAction` value keeps its **double quotes** and uses the **`serviceType` string exactly as listed in the description** (`...:1` or `...:2`).
* `Content-Length: 296` is the body above with CRLF after the XML declaration and after `</s:Envelope>`, as miniupnpc sends it.
* `HttpClient` recipe: `msg.Headers.TryAddWithoutValidation("SOAPAction", "\"" + serviceType + "#" + action + "\"")`, `msg.Headers.ConnectionClose = true`, `new StringContent(body, new UTF8Encoding(false), "text/xml")` and then `content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"")` (the constructor alone emits `charset=utf-8` without quotes). The request on the wire carried the same header values and body as the sample (only the header order differs).
* miniupnpc writes headers and body in **one `send()`** because, in the words of a comment in `minisoap.c`, "my old linksys router only took into account soap request that are sent into only one packet". In the test the fake server's first TCP read contained headers and body in 200 of 200 loopback requests; if a real router rejects the call anyway, fall back to a raw `TcpClient` and one `Write` of the whole request.

Response (HTTP 200, `Content-Type: text/xml`):

```xml
<?xml version="1.0"?>
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body><u:GetTotalBytesReceivedResponse xmlns:u="urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1"><NewTotalBytesReceived>4123456789</NewTotalBytesReceived></u:GetTotalBytesReceivedResponse></s:Body></s:Envelope>
```

Parse namespace-agnostically (`LocalName == "NewTotalBytesReceived"`; routers vary the prefixes). Error: HTTP **500** with `<s:Fault>...<detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>401</errorCode><errorDescription>Invalid Action</errorDescription>` (401 invalid action, 402 invalid args, 501 action failed, 606 not authorized): mark the capability unsupported and stop polling it.

| Action (service) | Out arguments (units) | Use |
|---|---|---|
| `GetTotalBytesReceived` (WANCommonInterfaceConfig) | `NewTotalBytesReceived` (ui4, **bytes, downstream = download**) | rate = delta/dt |
| `GetTotalBytesSent` | `NewTotalBytesSent` (ui4, bytes, upload) | rate |
| `GetTotalPacketsReceived` / `GetTotalPacketsSent` | `NewTotalPacketsReceived` / `NewTotalPacketsSent` (ui4) | pps; fallback when bytes are broken |
| `GetCommonLinkProperties` | `NewWANAccessType` (`DSL`, `POTS`, `Cable`, `Ethernet`), `NewLayer1UpstreamMaxBitRate`, `NewLayer1DownstreamMaxBitRate` (**bit/s**), `NewPhysicalLinkStatus` (`Up`, `Down`, `Initializing`, `Unavailable`) | link capacity = natural 100 % of the download needle |
| `GetStatusInfo` (WANIPConnection or WANPPPConnection) | `NewConnectionStatus` (`Unconfigured`, `Connecting`, `Connected`, `PendingDisconnect`, `Disconnecting`, `Disconnected`), `NewLastConnectionError`, `NewUptime` (s) | online LED + connection uptime |
| `GetExternalIPAddress` (same) | `NewExternalIPAddress` | public IP (private/CGNAT in double NAT) |

**Counter semantics and wraparound.** The spec defines these as 32-bit unsigned that roll over to 0 after 2^32-1. Real devices differ: miniupnpd (used by many routers) prints `unsigned long` (**64-bit values on 64-bit CPUs**) unless built with `UPNP_STRICT`, in which case it masks to 32 bits; the Nest was seen **saturated at 4294967295**. Parse as `ulong` and keep per-device state:

```text
if (v == 0xFFFFFFFF)               -> counter unsupported/saturated: stop using it
else if (v >= prev)                -> delta = v - prev
else if (prev <= 0xFFFFFFFF)       -> 32-bit wrap: delta = v + 2^32 - prev     // home-assistant/async_upnp_client does the same
else                                -> device reset: skip one sample
```

A 32-bit byte counter wraps in 2^32 / rate: 343 s at 100 Mbit/s, 34 s at 1 Gbit/s, so poll at 1-5 s (never slower than 30 s). On a double-NAT home the ISP router's WAN counters see **all** household traffic. Never call `AddPortMapping`/`DeletePortMapping` or other write actions (unauthenticated: they change the firewall).

### 7.4 Italian ISP routers (what is known)

| Router | UPnP IGD | Local API / notes |
|---|---|---|
| **TIM Hub / Hub+ / Hub Pro / Hub Ultra / Sagemcom 10 Gb** (Technicolor, Huawei, Askey, Sagemcom, TP-Link builds) | exists but TIM's own guides say it must be enabled manually in the modem UI (menu path differs per model: "Rete locale -> UPnP" on Hub+, "Service -> UPnP" on Huawei, "Port Forwarding -> Attivare UPnP IGD" on Sagemcom) ([TIM guide](https://www.tim.it/assistenza/assistenza-tecnica/guide-manuali/timhub-plus/port-mapping)). Default is therefore **off**. Counters behaviour: **UNVERIFIED** | no documented local API |
| **Vodafone Station** (Sercomm "Power Station" SHG3000/SHG3060, Technicolor THG3000/CGA6444VF, UltraHub 7) | present; users report it opening ports; default state **UNVERIFIED** | authenticated vendor web API with four firmware families, wrapped by `aiovodafone` (Apache-2.0) and Home Assistant's `vodafone_station` integration (uptime, external IP, firmware, device list, restart). Logins use model-specific challenge/SJCL crypto: brittle, skip |
| **Fastweb FASTGate / NeXXt / Internet Box** (Askey, Sagemcom, Technicolor builds) | present; forum reports say toggling UPnP on the FASTGate fixes console NAT; default **UNVERIFIED** | none found |
| **Iliadbox** (also reachable as `iliadboxos.it` in forum posts; LAN address reported as `192.168.1.254` in one thread, not `.1`) | no UPnP toggle found; incoming ports only via a fixed service list and a limited port slice (shared IPv4) | none found; the French Freebox OS API is **not** known to apply to the Italian box (**UNVERIFIED**) |

Decision for VU Studio: auto-test IGD on the ISP router (unicast), and when the test fails show a one-line explanation per brand ("enable UPnP in the modem, menu X") instead of promising counters. The cheap fallback that always works: the **PC's own NIC counters** (what the dial shows is then "this PC's throughput", not the household's).

### 7.5 Which device answers SSDP

On the PC behind the Nest, multicast M-SEARCH is answered only by the Nest (when its UPnP toggle is on) and by LAN devices (smart TVs, NAS, printers answer `upnp:rootdevice`); the ISP router is invisible. A PC plugged into the ISP router's LAN (or behind a Nest in bridge mode) sees the ISP router's IGD. The Nest IGD, if it counted correctly, would count only Nest-side WAN traffic. So: discover with multicast, additionally try unicast to `wan.gatewayIpAddress` from section 7.2 and to the hop-2 address from 7.1, and let the user choose which IGD to bind each dial to (show `friendlyName`, `manufacturer`, `modelName`, IP).

## 8. Home Assistant

Primary sources: [REST API](https://developers.home-assistant.io/docs/api/rest/), [WebSocket API](https://developers.home-assistant.io/docs/api/websocket/), [instance discovery](https://developers.home-assistant.io/docs/api/instance_discovery/) (all from the `home-assistant/developers.home-assistant` repo), plus `homeassistant/components/websocket_api/{commands,messages,const}.py` and `core.py` in `home-assistant/core` for the compressed format (read at `dev`, October 2026). License of HA: Apache-2.0; no separate API terms of use were found.

### 8.1 Access and auth

* Base URL `http://<host>:8123` (the developer docs say the default port is 80 on Home Assistant OS installations and 8123 otherwise; `SETUP_PORT` overrides). TLS/Nabu Casa instances use `https://` and `wss://`.
* **Long-lived access token**: user profile -> Security -> Long-lived access tokens. Sent as `Authorization: Bearer <token>` on REST and as `access_token` in the WebSocket `auth` message. Store it encrypted (DPAPI on Windows, a 0600 file on Linux). Use a dedicated HA user with a limited role if possible: the token inherits that user's permissions (non-admin users only see entities they may read, enforced in `subscribe_entities`).
* Status codes: 200/201 success; 400 bad request (also: `return_response` mismatch); 401 bad token; 404 unknown entity or resource; 405 method not allowed.

### 8.2 REST

| Call | Notes |
|---|---|
| `GET /api/` | `{"message":"API running."}`; the trailing slash is required |
| `GET /api/config` | `version`, `location_name`, `time_zone`, `unit_system` (`{"length":"km","mass":"g","temperature":"°C","volume":"L"}`), `components` |
| `GET /api/states` | array of **all** states (can be megabytes on big installs: use once for the entity picker, not for polling) |
| `GET /api/states/<entity_id>` | one state; 404 if absent. This is the polling call for one dial |
| `POST /api/services/<domain>/<service>` | body = service data, e.g. `{"entity_id":"light.desk","rgb_color":[255,0,0]}`; returns the list of states that changed; `?return_response` for services that return data |
| `POST /api/template` | body `{"template":"{{ float(states('sensor.a'), 0) + float(states('sensor.b'), 0) }}"}`; returns the rendered text/plain value. Lets the user compute derived numbers on the HA side with one cheap call |
| `GET /api/history/period/<ts>?filter_entity_id=...&minimal_response` | history for sparklines (not needed: VU Studio keeps its own) |

```json
{"entity_id":"sensor.living_room_temperature","state":"21.4",
 "attributes":{"unit_of_measurement":"°C","friendly_name":"Living room temperature","device_class":"temperature","state_class":"measurement"},
 "last_changed":"2026-10-12T15:00:00.123456+00:00","last_updated":"2026-10-12T15:00:00.123456+00:00",
 "context":{"id":"01J...","parent_id":null,"user_id":null}}
```

Parsing rules: `state` is **always a string**. Numeric sensors: `double.TryParse(InvariantCulture)`; the special values **`unavailable`** and **`unknown`** mean no data (drive the dial's "offline" behaviour); `on`/`off` for `binary_sensor`, `switch`, `light` (map to 100/0); `device_class` (`temperature`, `humidity`, `power`, `energy`, `battery`, `pressure`, `illuminance`, `signal_strength`...) and `unit_of_measurement` give a sensible default scale and unit for the dial; `state_class` is `measurement`, `total` or `total_increasing` (the last two are counters that never go down: compute rates yourself). Timestamps are ISO-8601 UTC. Attributes of other entities (e.g. `climate.x` `current_temperature`, `weather.x` `temperature`) need an attribute selector in the UI (`attributes.current_temperature`).

### 8.3 WebSocket (recommended transport)

URL `ws(s)://<host>:8123/api/websocket`. Flow (every message is one JSON text frame; versions, ids and timestamps are illustrative):

```text
S->C {"type":"auth_required","ha_version":"2026.10.0"}
C->S {"type":"auth","access_token":"<token>"}
S->C {"type":"auth_ok","ha_version":"2026.10.0"}            // or {"type":"auth_invalid","message":"Invalid password"} then the server closes
C->S {"id":1,"type":"subscribe_entities","entity_ids":["sensor.living_room_temperature","sensor.grid_power"]}
S->C {"id":1,"type":"result","success":true,"result":null}
S->C {"id":1,"type":"event","event":{"a":{                     // initial snapshot: "a" = added/full states
        "sensor.living_room_temperature":{"s":"21.4","a":{"unit_of_measurement":"°C","device_class":"temperature","friendly_name":"Living room temperature"},"c":"01J...","lc":1791817200.123},
        "sensor.grid_power":{"s":"412","a":{"unit_of_measurement":"W","device_class":"power"},"c":"01J...","lc":1791817190.0,"lu":1791817201.5}}}}
S->C {"id":1,"type":"event","event":{"c":{"sensor.grid_power":{"+":{"s":"430","lc":1791817205.2}}}}}      // "c" = changed: "+" additions/updates
S->C {"id":1,"type":"event","event":{"c":{"sensor.grid_power":{"+":{"a":{"icon":"mdi:flash"}},"-":{"a":["old_attr"]}}}}}   // attribute-only change, with removed attribute keys
S->C {"id":1,"type":"event","event":{"r":["sensor.grid_power"]}}                                       // "r" = removed
```

* **Compressed state keys** (`homeassistant/const.py`): `s` state, `a` attributes (object), `c` context (a plain string id when only the id is set, else `{"id","parent_id","user_id"}`), `lc` last_changed and `lu` last_updated as **Unix seconds (float)**, not ISO strings; `lu` is omitted when equal to `lc`. Diff keys: `a` (entity added), `c` (changed), `r` (removed list); inside a change `"+"` holds new/changed fields (`s`, `lc`, `lu`, `c`, and `a` containing only the changed attributes, to be merged) and `"-"` holds `{"a":[removed attribute names]}`.
* Client reducer: `states[id] = full` on `a`; on `c` patch `states[id].s/lc/lu/c`, merge `+.a` into the attribute dictionary, delete keys listed in `-.a`; delete on `r`. Always pass `entity_ids` (or `include`/`exclude` filters) so the snapshot stays small; omit it and you receive every entity.
* **`subscribe_entities` vs `subscribe_events`.** `subscribe_events` with `"event_type":"state_changed"` ([documented](https://developers.home-assistant.io/docs/api/websocket/)) delivers the verbose `{"data":{"entity_id","new_state":{...},"old_state":{...}}}` for every entity in the system (large, needs a separate `get_states` for the initial values, race-prone). `subscribe_entities` is what the HA frontend uses; it is **not in the public docs** (read from the core source), so keep a fallback to `get_states` + `subscribe_events`.
* Other commands: `{"id":N,"type":"get_states"}` (full dump for the picker), `{"id":N,"type":"ping"}` -> `{"id":N,"type":"pong"}`, `{"id":N,"type":"call_service","domain":"light","service":"turn_on","service_data":{...},"target":{"entity_id":"light.kitchen"}}` -> `{"id":N,"type":"result","success":true,"result":{"context":{...},"response":null}}`, `unsubscribe_events` with `"subscription":<id>`. Errors: `{"success":false,"error":{"code":"not_found|invalid_format|unauthorized|...","message":"..."}}`.
* **Keep-alive and limits.** The server pings every 55 s (aiohttp `heartbeat=55`); a client must answer WebSocket pings (.NET's `ClientWebSocket` answers automatically; leave `KeepAliveInterval` enabled) and should send its own `ping` message every 30-60 s to detect half-open connections. Message `id`s must be strictly increasing integers per connection. The server drops consumers that fall behind (pending queue limit 4096 messages), so read continuously and process on another thread. After an HA restart the socket closes: reconnect with exponential back-off (1 s, 2 s ... 30 s) and re-subscribe. Do not enable `supported_features: coalesce_messages` (it batches messages into arrays).

### 8.4 Discovery (mDNS)

HA advertises `_home-assistant._tcp.local.` once the HTTP server is up: instance name = `location_name`, host `<uuid>.local.`, port 8123 (80 on HAOS), TXT: `location_name`, `uuid` (stable 32-hex id), `version`, `internal_url`, `external_url`, `base_url` (deprecated), `requires_api_password` (legacy, always `True`; ignore). Use `internal_url` when non-empty, else `http://<resolved address>:<port>`. TXT values are capped at 230 bytes. See section 11 for the library choice.

## 9. MQTT

### 9.1 Payload conventions to support

All of these arrive as UTF-8 text; most are JSON. The design rule is: **subscribe to topic filters, extract with a path expression, never hard-code a device vendor**. Presets for the four conventions below are just prefilled topic + path pairs.

| Source | Topic and payload (verified against the vendor firmware source / docs / real logs) | Useful paths and units |
|---|---|---|
| **Tasmota** (firmware `my_user_config.h`: `FullTopic = %prefix%/%topic%/`, prefixes `cmnd`, `stat`, `tele`, `TelePeriod = 300` s, LWT payloads `Online`/`Offline`) | `tele/<topic>/SENSOR` e.g. `{"Time":"2026-10-12T17:00:00","ENERGY":{"TotalStartTime":"2025-01-01T00:00:00","Total":123.4,"Yesterday":1.2,"Today":0.8,"Period":0,"Power":45,"ApparentPower":50,"ReactivePower":22,"Factor":0.9,"Voltage":230,"Current":0.2}}`; env sensors: `{"Time":"...","DS18B20":{"Id":"...","Temperature":21.5},"TempUnit":"C"}`, `{"AM2301":{"Temperature":21.4,"Humidity":48.1,"DewPoint":10.2},"TempUnit":"C"}`. Also `tele/<topic>/STATE` (uptime, Wifi RSSI, relay `POWER`), `tele/<topic>/LWT`, `stat/<topic>/POWER` (`ON`/`OFF`) | `$.ENERGY.Power` (W), `.Voltage` (V), `.Current` (A), `.Today`/`.Total` (kWh), `$.AM2301.Humidity` (%). Telemetry every 300 s by default (user can set `TelePeriod 10`); for faster power data ask the user to lower it |
| **Shelly Gen2/Gen3/Gen4** (real MQTT log of a Gen4 1PM, 2026) | `<id>/status/switch:0` where `<id>` is the MQTT client id/prefix `shelly<model>-<mac12>`: `{"id":0,"source":"WS_in","output":true,"apower":0.0,"voltage":124.3,"freq":60.0,"current":0.000,"aenergy":{"total":12.000,"by_minute":[0.0,0.0,0.0],"minute_ts":1785787500},"ret_aenergy":{"total":0.0,...},"temperature":{"tC":42.5,"tF":108.4}}`; same pattern `status/cover:0`, `status/em:0`, `status/em1:0`, `status/pm1:0`, `status/temperature:0`; availability `<id>/online` = `true`/`false`; events on `<id>/events/rpc` (`NotifyStatus` with `params.switch:0`) | `$.apower` (W), `$.voltage` (V), `$.current` (A), `$.aenergy.total` (**Wh**), `$.temperature.tC` (°C), `$.output` (bool). MQTT status publishing must be enabled in the device's MQTT settings; from memory of the Shelly docs (**UNVERIFIED**, not fetchable here) the `MQTT.SetConfig` keys are `status_ntf` (the `status/<component>` topics) and `rpc_ntf` (`events/rpc`), shown in the web UI as "Generic status update over MQTT" and "RPC status notifications over MQTT". Gen1 uses `shellies/<id>/relay/0/power` etc. with plain-number payloads (**UNVERIFIED**, not researched). Vendor docs <https://shelly-api-docs.shelly.cloud/> not fetchable here |
| **Zigbee2MQTT** ([docs](https://www.zigbee2mqtt.io/guide/usage/mqtt_topics_and_messages.html)) | `zigbee2mqtt/<friendly_name>` always JSON, schema depends on the device "exposes": `{"temperature":27.34,"humidity":44.72,"battery":100,"linkquality":87}`, `{"occupancy":true}`, `{"action":"single"}`, `{"state":"ON","brightness":215,"color_temp":325}`; availability `zigbee2mqtt/<friendly_name>/availability` = `{"state":"online"}` (retained, when enabled); bridge `zigbee2mqtt/bridge/state`, `bridge/devices` (retained device list) | `$.temperature` (°C), `$.humidity` (%), `$.battery` (%), `$.power` (W) on plugs, `$.linkquality` (0-255). `friendly_name` may contain `/` (topic levels). Control topic `.../set` |
| **Home Assistant MQTT discovery** ([docs](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery)) | config topic `<discovery_prefix>/<component>/[<node_id>/]<object_id>/config` (prefix default `homeassistant`), JSON such as `{"device_class":"temperature","state_topic":"homeassistant/sensor/sensorBedroom/state","unit_of_measurement":"°C","value_template":"{{ value_json.temperature }}","unique_id":"temp01ae","device":{"identifiers":["bedroom01ae"],"name":"Bedroom"}}`; keys may be abbreviated (`stat_t`, `unit_of_meas`, `val_tpl`, `dev_cla`, `uniq_id`, `dev`, `o`...); HA publishes `online`/`offline` on `homeassistant/status` (birth/will) | Subscribing to `homeassistant/+/+/config` and `homeassistant/+/+/+/config` (retained) yields a **free sensor catalog with names, units, device classes and the state topic**; evaluate only the simple template `{{ value_json.<path> }}` / `value_json['key']` (anything else: fall back to manual path). `availability_topic` + `payload_available` gives online state |

Generic rules: topics are case-sensitive and may contain spaces; wildcards `+` (one level) and `#` (rest); retained messages arrive immediately after subscribing (give "last known value" with its age); numbers can be plain text (`21.5`), JSON scalars, or JSON objects; booleans arrive as `ON/OFF`, `true/false`, `1/0`, `online/offline`; reject payloads over, say, 64 KiB.

### 9.2 Extraction approach

Offer three modes per binding, evaluated left to right: (1) **raw number** (payload parses as a double), (2) **path** into JSON, (3) **regex** with one capture group. For path use **JSONPath (RFC 9535)** syntax so it matches the HTTP/JSON source of FR-DS-9: `$.ENERGY.Power`, `$.aenergy.total`, `$.temperature.tC`, `$['key with space']`, `$.items[0].v`. Library choice: `System.Text.Json` has no JSONPath, so either **JsonPath.Net** (json-everything, MIT, 3.0.2 on 2026-03-15, targets net8/9/10, RFC 9535) or **Newtonsoft.Json** `SelectToken` (MIT, 13.0.4 stable) or a 40-line evaluator that supports only `$`, `.name`, `['name']`, `[n]` (enough for every example above and AOT-friendly). Bind result types: number, bool (`ON/true/1/online` -> 1), string (shown on the face only). Keep per-binding: topic filter, path, scale/offset, unit, optional "stale after N seconds" (e.g. 3x the device's report interval, because Tasmota's default is 300 s).

### 9.3 MQTTnet 5.x client (verified: compiled and run on .NET 10 with MQTTnet **5.2.0.1603**, 2026-07-01, MIT, targets net8.0 and net10.0)

Changes from v4 that break old snippets: `MqttFactory` is now **`MqttClientFactory`**; the **ManagedClient was removed** (reconnect is your job); `MqttApplicationMessage.Payload` is a **`ReadOnlySequence<byte>`** (v4: `byte[]`; use `PayloadSegment` to set from a segment); default protocol version is **MQTT 5.0** (`WithProtocolVersion(MqttProtocolVersion.V311)` for old brokers); `UseTls(bool)` and options via `WithTlsOptions(o => ...)`.

```csharp
using MQTTnet;                                   // MqttClientFactory, MqttClientOptionsBuilder, MqttApplicationMessage
var factory = new MqttClientFactory();
using IMqttClient client = factory.CreateMqttClient();

client.ApplicationMessageReceivedAsync += e =>   // attach BEFORE ConnectAsync, otherwise queued/retained messages are lost
{
    string topic = e.ApplicationMessage.Topic;
    string? text = e.ApplicationMessage.ConvertPayloadToString();       // null for empty payload; Encoding.UTF8.GetString(ReadOnlySequence) also works
    bool retained = e.ApplicationMessage.Retain;
    return Task.CompletedTask;                   // return quickly; hand off to a Channel<T>
};
client.DisconnectedAsync += e => { /* e.ClientWasConnected, e.Reason, e.Exception: signal the supervisor loop */ return Task.CompletedTask; };

var options = new MqttClientOptionsBuilder()
    .WithTcpServer("192.168.1.10", 1883)         // or .WithConnectionUri("mqtts://host:8883") / .WithWebSocketServer(...)
    .WithClientId("vustudio-" + Environment.MachineName)
    .WithCredentials("user", "password")
    .WithCleanSession(true)                      // we resubscribe on every connect anyway
    .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
    .WithTlsOptions(o => o.UseTls(false))        // true for mqtts; self-signed broker: WithTrustChain(caCerts) or WithAllowUntrustedCertificates() / WithCertificateValidationHandler(...)
    .Build();

var subscribe = factory.CreateSubscribeOptionsBuilder()
    .WithTopicFilter("tele/+/SENSOR")
    .WithTopicFilter(f => f.WithTopic("zigbee2mqtt/#").WithAtLeastOnceQoS())
    .Build();

await client.ConnectAsync(options, ct);
await client.SubscribeAsync(subscribe, ct);      // retained messages follow immediately
```

Reconnect strategy (MQTTnet's own sample, recommended over reconnecting inside `DisconnectedAsync`, which can deadlock): a supervisor `while(!ct.IsCancellationRequested)` loop every 5 s: `if (!await client.TryPingAsync()) { await client.ConnectAsync(options, ct); await client.SubscribeAsync(subscribe, ct); }`, with exponential back-off after failures (5 s to 60 s) and a "broker offline" flag raised to the dial logic. Other API names: `PublishStringAsync(topic, payload, qos, retain)` or `PublishAsync(factory.CreateApplicationMessageBuilder().WithTopic(..).WithPayload(..).Build())`; `client.IsConnected`; `UnsubscribeAsync`. Publishing from VU Studio (e.g. its own sensors for HA) uses the same client. Mosquitto 2.x allows anonymous only on localhost by default, so expect username/password fields in the UI.

## 10. Online data

Poll slowly and cache: none of these sources changes faster than the intervals below, and every one is free-tier fair-use. Always set a `User-Agent: VUStudio/<version> (+https://github.com/<owner>/VuStudio)` header, 10 s timeout, honour `429`/`Retry-After`, and show "stale since HH:mm" instead of zero when a call fails (FR-MAP-3).

### 10.1 Open-Meteo (weather, geocoding, air quality): no key

Specs read from the server repo ([open-meteo/open-meteo](https://github.com/open-meteo/open-meteo) `openapi/forecast.yml`, `openapi/air-quality.yml`, `Sources/App/Helper/WeatherCode.swift`) and the geocoding repo ([open-meteo/geocoding-api](https://github.com/open-meteo/geocoding-api)); human docs at <https://open-meteo.com/en/docs>.

**Current conditions** (`GET https://api.open-meteo.com/v1/forecast`; params comma-separated, no spaces):

```text
?latitude=45.4642&longitude=9.19
&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,rain,showers,snowfall,weather_code,cloud_cover,pressure_msl,surface_pressure,wind_speed_10m,wind_direction_10m,wind_gusts_10m
&daily=temperature_2m_max,temperature_2m_min,precipitation_sum,precipitation_probability_max,weather_code,sunrise,sunset    // optional, for forecast faces
&hourly=temperature_2m,precipitation_probability&forecast_hours=24                                                         // optional, for sparklines
&timezone=auto&temperature_unit=celsius&wind_speed_unit=kmh&precipitation_unit=mm&timeformat=iso8601&forecast_days=3
```

| Param | Values (default) |
|---|---|
| `temperature_unit` | `celsius` (default), `fahrenheit` |
| `wind_speed_unit` | `kmh` (default), `ms`, `mph`, `kn` |
| `precipitation_unit` | `mm` (default), `inch` |
| `timeformat` | `iso8601` (default, local time without offset when `timezone` is set), `unixtime` |
| `timezone` | IANA name or `auto` (resolve from coordinates); default GMT |
| others | `forecast_days` 0-16 (7), `past_days` 0-92, `forecast_hours`, `past_hours`, `models` (e.g. `italia_meteo_arpae_icon_2i`; default best match), `apikey` (**commercial plans only**) |

```json
{"latitude":45.46,"longitude":9.19,"generationtime_ms":0.07,"utc_offset_seconds":7200,"timezone":"Europe/Rome","timezone_abbreviation":"GMT+2","elevation":122.0,
 "current_units":{"time":"iso8601","interval":"seconds","temperature_2m":"°C","relative_humidity_2m":"%","apparent_temperature":"°C","is_day":"","precipitation":"mm","weather_code":"wmo code","pressure_msl":"hPa","wind_speed_10m":"km/h","wind_direction_10m":"°"},
 "current":{"time":"2026-10-12T17:15","interval":900,"temperature_2m":18.4,"relative_humidity_2m":62,"apparent_temperature":17.9,"is_day":1,"precipitation":0.0,"weather_code":3,"pressure_msl":1016.2,"wind_speed_10m":7.9,"wind_direction_10m":240}}
```

(shape from the OpenAPI schema; numbers illustrative.) `current.interval` is the model step in seconds (900 = 15 min), so **poll every 10-15 min**; `is_day` is 0/1 (night icon); `weather_code` is a WMO code (table below); missing values are `null`. Errors: HTTP 400 with `{"error":true,"reason":"..."}`.

**WMO `weather_code`** (exactly the codes the server emits, `WeatherCode.swift`; Italian labels are suggested localisation, not from Open-Meteo):

| Code | English | Italiano | Code | English | Italiano |
|---|---|---|---|---|---|
| 0 | Clear sky | Sereno | 66 | Freezing rain, light | Pioggia gelata debole |
| 1 | Mainly clear | Prevalentemente sereno | 67 | Freezing rain, heavy | Pioggia gelata forte |
| 2 | Partly cloudy | Parzialmente nuvoloso | 71 | Snow fall, slight | Neve debole |
| 3 | Overcast | Coperto | 73 | Snow fall, moderate | Neve moderata |
| 45 | Fog | Nebbia | 75 | Snow fall, heavy | Neve forte |
| 48 | Depositing rime fog | Nebbia con brina | 77 | Snow grains | Granuli di neve |
| 51 | Drizzle, light | Pioggerella debole | 80 | Rain showers, slight | Rovesci deboli |
| 53 | Drizzle, moderate | Pioggerella moderata | 81 | Rain showers, moderate | Rovesci moderati |
| 55 | Drizzle, dense | Pioggerella intensa | 82 | Rain showers, violent | Rovesci violenti |
| 56 | Freezing drizzle, light | Pioggerella gelata debole | 85 | Snow showers, slight | Rovesci di neve deboli |
| 57 | Freezing drizzle, dense | Pioggerella gelata intensa | 86 | Snow showers, heavy | Rovesci di neve forti |
| 61 | Rain, slight | Pioggia debole | 95 | Thunderstorm, slight or moderate | Temporale |
| 63 | Rain, moderate | Pioggia moderata | 96 | Thunderstorm with slight hail | Temporale con grandine debole |
| 65 | Rain, heavy | Pioggia forte | 99 | Thunderstorm with heavy hail | Temporale con grandine forte |

Treat unknown codes as "unknown", never as an error (e-ink icon mapping: 0-1 sun/moon, 2-3 cloud, 45-48 fog, 51-67 and 80-82 rain, 71-77 and 85-86 snow, 95-99 bolt).

**Geocoding** (city name to coordinates for the settings UI): `GET https://geocoding-api.open-meteo.com/v1/search?name=Milano&count=5&language=it&format=json` (`name` at least 2 characters, may be `"City, Region"`; `count` 1-100, default 10; `language` default `en`; `countryCode` ISO-3166-1 alpha-2 filter; also `GET /v1/get?id=<geonames id>`). Response `{"results":[{"id":3173435,"name":"Milano","latitude":45.46427,"longitude":9.18951,"elevation":122.0,"feature_code":"PPLA","country_code":"IT","country":"Italia","admin1":"Lombardia","admin2":"...","timezone":"Europe/Rome","population":1371498,"postcodes":["20121"]}],"generationtime_ms":0.9}` (fields from `api.proto`; `results` is absent when nothing matches).

**Air quality**: `GET https://air-quality-api.open-meteo.com/v1/air-quality?latitude=45.4642&longitude=9.19&current=european_aqi,us_aqi,pm2_5,pm10,ozone,nitrogen_dioxide&timezone=auto` (also `domains=auto|cams_europe|cams_global`, pollen variables for Europe: `alder_pollen`, `birch_pollen`, `grass_pollen`, `olive_pollen`...). Same envelope as the forecast API with `current_units`; PM and gases in **µg/m3**, AQI unitless. European AQI bands as I recall them from the Open-Meteo docs page (**UNVERIFIED**, the OpenAPI file lists variables only): 0-20 good, 20-40 fair, 40-60 moderate, 60-80 poor, 80-100 very poor, above 100 extremely poor. Data: Copernicus CAMS, hourly.

**Terms and limits** (primary sources: "Terms & Privacy" and "Data License" in the [open-meteo/open-meteo README](https://github.com/open-meteo/open-meteo), plus `Sources/App/Helper/Vapor/RateLimiter.swift` and `Helper/Writer/ForecastApiResult.swift` in the same repo, read 2026-10; the web page [open-meteo.com/en/terms](https://open-meteo.com/en/terms) was not fetchable). The hosted API is "free for open-source developers and non-commercial use", with "no restriction but fair use"; applications above **10,000 requests per day** should contact the operator, who "reserves the right to block applications and IP addresses that misuse our service"; commercial use needs an arrangement with the operator (paid `apikey`, hosts `customer-api.open-meteo.com` etc.). The free server enforces per-IP defaults of **10,000 calls/day, 5,000/hour, 600/minute** (env-configurable in the open-source server, so the public instance may differ) and answers HTTP **429** with `{"error":true,"reason":"Minutely API request limit exceeded. Please try again in one minute."}` (variants "Hourly ..." and "Daily ..."); concurrency is 1 request per client at a time (extra ones queue server-side; more than 5 simultaneous fail with 429 `Too many concurrent requests`), so never issue parallel calls from one IP. A call is not always worth 1: `weight = max(1, max(v/10, v/10 * days/14))` per location, with `v` = number of requested variables (current + hourly + daily, times models) and `days` = covered time range; the sample URL above (24 variables, 3 days) costs about 2.4; failed calls count 1. At 15-minute polling one location costs 96 calls (about 230 weighted) per day. **Data licence CC BY 4.0**: "Include an attribution link next to any location where Open-Meteo data is displayed", for example `<a href="https://open-meteo.com/">Weather data by Open-Meteo.com</a>`. The server code is AGPL-3.0; the geocoding repo README still says its data is CC BY-NC 4.0 (inconsistent: treat as non-commercial). VU Studio is a free GPL app: show the attribution in the source picker and on the weather source card (an e-ink face cannot carry a link).

### 10.2 Crypto prices

**CoinGecko `simple/price`** (<https://docs.coingecko.com/>, not fetchable here; details from search-engine snippets of the official pages "Keyless Public API", "Error Codes" and "Pricing", so numbers are **UNVERIFIED** in detail):

```text
GET https://api.coingecko.com/api/v3/simple/price?ids=bitcoin,ethereum&vs_currencies=eur,usd&include_24hr_change=true&include_last_updated_at=true
-> {"bitcoin":{"eur":61234.12,"eur_24h_change":-1.23,"usd":66012.5,"usd_24h_change":-1.1,"last_updated_at":1760281200},"ethereum":{...}}
```

`ids` are CoinGecko slugs (`bitcoin`), found with `GET /api/v3/search?query=btc` (the full `/coins/list` is ~15k entries). Other flags: `include_market_cap`, `include_24hr_vol`, `precision`. **Keys and limits (2026):** the endpoint works **without any key** ("keyless"), but from a shared per-IP pool of roughly 10-30 calls/minute that CoinGecko says is not for production or scheduled polling; a free **Demo key** is sent as header **`x-cg-demo-api-key`** to the same host `api.coingecko.com` and gives a stable **100 calls/minute** with a monthly cap that third-party sources put at **10,000 calls** (so poll every 5 minutes or slower: 8,640 calls/month); paid keys use `pro-api.coingecko.com` + `x-cg-pro-api-key`. On HTTP 429 back off exponentially (all calls count, including errors). The free API requires attribution ("Powered by CoinGecko") (**UNVERIFIED**). Default design: keyless, 5-minute poll, optional Demo key field.

**Keyless alternative for live tickers: Binance market data** ([binance-spot-api-docs](https://github.com/binance/binance-spot-api-docs), `rest-api.md`, `faqs/market_data_only.md`): `GET https://data-api.binance.vision/api/v3/ticker/24hr?symbol=BTCEUR` (public market-data-only host, no API key; `api.binance.com` serves the same path):

```json
{"symbol":"BTCEUR","priceChange":"-756.10","priceChangePercent":"-1.22","weightedAvgPrice":"61100.5","prevClosePrice":"61990.2","lastPrice":"61234.12","bidPrice":"61234.0","askPrice":"61234.2","openPrice":"61990.2","highPrice":"62500.0","lowPrice":"60800.0","volume":"1234.5","quoteVolume":"75400000.0","openTime":1760194800000,"closeTime":1760281200000,"count":512345}
```

All numbers are **strings**; weight 2 per single-symbol call against a documented request-weight budget (the docs' sample shows 6000 per minute per IP); response header `X-MBX-USED-WEIGHT-1M`; HTTP 429 means slow down and **418 means the IP was auto-banned** (2 minutes to 3 days) when 429s are ignored; `Retry-After` is in seconds. Pair syntax is `BASEQUOTE` without separator (`BTCEUR`, `ETHEUR`). Regional availability and Binance's terms of use apply (**UNVERIFIED** for Italy).

### 10.3 Stock quotes without a key (risky)

* **Stooq CSV** `https://stooq.com/q/l/?s=aapl.us&f=sd2t2ohlcv&h&e=csv` (columns `Symbol,Date,Time,Open,High,Low,Close,Volume`, one row, `N/D` for unknown symbols): **no longer keyless**. Since March/April 2026 Stooq answers CSV requests with an HTML page demanding an API key obtained manually on the website (pandas-datareader issue #1012 "Stooq now requires API key", and the `qalydon/qf-localc` Stooq README; quota blocking and "Access denied" reports are single-sourced). The key's parameter name is **UNVERIFIED**. Do not ship Stooq as a default; offer it only as "enter your key".
* **Yahoo Finance chart endpoint** `GET https://query1.finance.yahoo.com/v8/finance/chart/AAPL?range=1d&interval=1m` (host `query2` also works; no key): JSON `chart.result[0].meta.regularMarketPrice` (last price), `meta.regularMarketTime` (Unix s), `meta.currency`, `meta.exchangeTimezoneName`, previous close in `meta.chartPreviousClose` or `meta.previousClose` (not present for every symbol, per yfinance's own comments: **field availability UNVERIFIED**), series in `chart.result[0].timestamp[]` + `indicators.quote[0].close[]`; failure: `chart.error` non-null. Unofficial and unstable: yfinance (2026) wraps it with `curl_cffi` browser impersonation, crumb/consent flows for other endpoints and a dedicated rate-limit exception on HTTP 429; its README states the API is "intended for personal use only" and defers to Yahoo's terms. Risks: sudden 429/403, blocked default .NET User-Agent (**UNVERIFIED**), data licence. Ship it, if at all, as an opt-in "experimental" source: user-entered symbol, 5-15 min poll, never during build/CI.
* **Official keyed free tiers** (Finnhub, Alpha Vantage, Twelve Data, Financial Modeling Prep): not researched here (limits **UNVERIFIED**); the generic HTTP/JSON source with a user key covers them. For currency rates, the ECB daily reference file `https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml` and the Frankfurter API `https://api.frankfurter.dev/v1/latest?base=EUR&symbols=USD` are keyless but were not fetched (**UNVERIFIED**).

### 10.4 Generic HTTP/JSON source (design notes)

Per source: URL (placeholders for secrets), method, headers, interval (min 5 s, default 60 s), JSONPath (RFC 9535, same engine as MQTT), scale/offset/unit, timeout 10 s, max body 1 MiB, follow at most 3 redirects, `http`/`https` only. Use conditional requests (`If-None-Match` / `If-Modified-Since`) when the server sends `ETag`/`Last-Modified`; cache the last good value with its timestamp. Resolve the host once per poll and refuse loopback/link-local targets unless the user ticks "local network" (the web panel can create sources remotely, so this prevents it being used as an SSRF proxy).

## 11. mDNS / DNS-SD discovery of VU Studio agents on the LAN

Goal: agents advertise `_vustudio-agent._tcp` and the desktop app/service browses for them; manual "add by IP:port" must always remain possible (mDNS is blocked on many guest/IoT VLANs and by some Wi-Fi extenders; Docker bridge networks do not forward it, so the Unraid container needs `--net=host`). Service name rules (RFC 6763 section 7.2): at most 15 characters after the underscore, letters/digits/hyphens: `vustudio-agent` is 14, valid.

### 11.1 Library comparison (NuGet metadata read 2026-10-12; tested on .NET 10.0.112)

| Library | Latest release | License | Advertise | Browse | Notes |
|---|---|---|---|---|---|
| **Makaretu.Dns.Multicast.New** ([jdomnitz/net-mdns](https://github.com/jdomnitz/net-mdns)) | 0.38.0 on 2024-11-23; last commit 2025-05-16 | MIT | **yes** (`ServiceDiscovery.Advertise/Announce/Unadvertise`) | yes (`QueryServiceInstances`, `ServiceInstanceDiscovered`) | Maintained fork of Richard Schneider's lib; targets net8.0/net9.0/netstandard2.0 (works on net10 through the compatible assets: **compiled and ran here**); IPv4+IPv6, multi-NIC, detects NIC changes; depends on `Common.Logging` 3.4.1 and `Makaretu.Dns.New` 3.1.2. README warns the DNS-SD *client* has no flood protection |
| Makaretu.Dns.Multicast (original, [richardschneider/net-mdns](https://github.com/richardschneider/net-mdns)) | 0.27.0 on **2019-11-05** | MIT (repo) | yes | yes | Stale; avoid |
| **Zeroconf** ([novotnyllc/Zeroconf](https://github.com/novotnyllc/Zeroconf)) | 3.7.16 on 2024-12-17; repo touched 2026-08 | MIT | **no** (browse only) | yes (`ZeroconfResolver.ResolveAsync("_vustudio-agent._tcp.local.", TimeSpan)`, `ListenForAnnouncementsAsync`) | net8.0/net6.0/netstandard2.0, depends on System.Reactive 5.0.0; fine as a *client-only* option |
| **Tmds.MDns** ([tmds/Tmds.MDns](https://github.com/tmds/Tmds.MDns)) | 0.10.1 on 2026-09-28 (very active) | MIT | no (finds services only) | yes (`ServiceBrowser`, events `ServiceAdded/Removed/Changed`) | net8.0/netstandard2.0, no dependencies; explicitly not a full RFC 6762 querier/cache |
| TinyDNS ([SmartHomeOS/TinyDNS](https://github.com/SmartHomeOS/TinyDNS)) | 0.9.0 on 2024-09-01 | **AGPL-3.0-or-later** | no | yes, with flood control | licence adds network-use obligations; skip |

Tested API sketch (Makaretu.Dns.Multicast.New 0.38.0, `Nullable` disabled, output captured in the sandbox where the advertiser discovered itself via multicast loopback):

```csharp
using Makaretu.Dns;
var profile = new ServiceProfile("vustudio-agent-nas", "_vustudio-agent._tcp", 8741);   // instance name, service type, port
profile.AddProperty("ver", "1"); profile.AddProperty("os", "linux"); profile.AddProperty("id", "<stable guid>");   // TXT; the lib prepends txtvers=1
using var sd = new ServiceDiscovery();
sd.Advertise(profile); sd.Announce(profile);              // answers queries + gratuitous announcement; sd.Unadvertise(profile) sends the goodbye on shutdown

sd.ServiceInstanceDiscovered += (s, e) =>                 // browsing side (QueryServiceInstances sends the PTR query)
{
    string instance = e.ServiceInstanceName.ToString();   // "vustudio-agent-nas._vustudio-agent._tcp.local"
    var srv = e.Message.AdditionalRecords.OfType<SRVRecord>().FirstOrDefault();      // srv.Target, srv.Port
    var ips = e.Message.AdditionalRecords.OfType<AddressRecord>().Select(a => a.Address);
    var txt = e.Message.AdditionalRecords.OfType<TXTRecord>().SelectMany(t => t.Strings);   // "txtvers=1","ver=1","os=linux","id=..."
};
sd.QueryServiceInstances("_vustudio-agent._tcp");         // repeat every 30-60 s; responses can also arrive unsolicited when an agent announces
```

The default SRV target in that test was `<instance>.vustudio-agent.local`, which does not resolve through the OS resolver: **use the `AddressRecord` addresses (or the packet's source IP) instead of resolving the SRV target name**, and prefer an IPv4 address on the same subnet as the NIC that received the packet. Answers may be partial (SRV without A in a different packet): keep a per-instance cache and issue explicit follow-up queries if addresses are missing.

### 11.2 Recommendation

1. Define `IAgentDiscovery` with two implementations: `MdnsAgentDiscovery` (Makaretu.Dns.Multicast.New for both advertise and browse) and `ManualAgentList` (IP:port saved in settings, always on). Optionally `SubnetScanDiscovery` (HTTP GET `/api/ping` on the agent port across the local /24, 254 parallel connects at 300 ms timeout) as the last resort when mDNS finds nothing after 5 s.
2. Pin the library version, keep the dependency behind the interface, and run a CI test that advertises and discovers on the loopback/multicast of the runner. If the fork goes stale or fails on a future .NET, switch to Plan B below; that is why the interface exists.
3. Security: mDNS data is unauthenticated, so the TXT record only carries a hint (`id`, `ver`, `os`); every connection still needs the agent token (NFR-4). Never put the token in TXT. Advertise only when the user enabled "allow LAN access" in the agent. On Windows expect a Defender Firewall prompt for inbound UDP 5353 the first time (**UNVERIFIED** wording); document it.

### 11.3 Plan B: minimal custom implementation (about 300 lines)

Needed only if the libraries disappoint. Facts (RFC 6762/6763/1035): UDP **224.0.0.251:5353** (IPv4) and `ff02::fb`; open one socket per usable IPv4 NIC (`SO_REUSEADDR`, `IP_MULTICAST_TTL`=255, `JoinMulticastGroup` on that NIC's address, `MulticastLoopback` on; skip loopback, down, virtual/Hyper-V/VPN adapters), accept packets from on-link sources only. Message = 12-byte DNS header (id 0, flags `0x8400` = response + authoritative for answers; queries flags 0) + sections, names as length-prefixed labels, compression pointers `0xC0xx` allowed. Records for instance `NAS._vustudio-agent._tcp.local`: `PTR _vustudio-agent._tcp.local -> NAS._vustudio-agent._tcp.local` (TTL 4500), `SRV` (priority 0, weight 0, port 8741, target `nas.local`; TTL 120), `TXT` (strings `key=value`, each at most 255 bytes, start with `txtvers=1`; TTL 4500), `A nas.local -> 192.168.1.20` (TTL 120); unique records (SRV/TXT/A) set the cache-flush bit (class `0x8001`). Behaviours: answer PTR queries for the service type (and ANY/SRV/TXT/A queries for the instance/host) by multicast with PTR+SRV+TXT+A in the same packet (additional records), honour the QU bit (unicast response), announce twice 1 s apart after start, send TTL=0 goodbye on exit, optionally probe the instance name (3 ANY queries 250 ms apart) and append `-2` on conflict. Browsing: send a PTR query (QU bit set on the first), collect responses for 3 s, follow up for missing SRV/TXT/A, deduplicate by instance name, expire by TTL, query again every 60 s with a known-answer list to be polite. Keep the whole module behind `IAgentDiscovery` so it is swappable.

## 12. Recommendations

Ranking key: **Value** = how much of what the owner wants to see is delivered (H/M/L); **Effort** = engineering cost on top of what the architecture already needs (S = days, M = 1-2 weeks, L = more); **Risk** = chance of breaking by itself (vendor API drift, missing hardware, legal). Terminology follows [`docs/architecture.md`](../architecture.md) (providers publishing into the `SensorHub`, ids `{providerId}/{localPath}`, the `/api/push` endpoint). Build the shared pieces first: the sensor hub and provider framework, a *pull* provider base (HTTP/JSON) and the *push* receiver for external programs and event streams (MQTT and Home Assistant subscriptions feed the hub the same way), then these sources in the order of the last table.

### 12.1 Per device type

**Unraid server (array, parity, disks, Docker)**
1. **Official GraphQL API** (7.2+; API key with role `VIEWER`): H value, M effort, low risk for the stable core (`array`, `parityCheckStatus`, `disks.temp`, `docker`, `metrics.cpu/memory`), medium risk for newer fields (probe by version, degrade gracefully). No container, nothing to maintain on the server. Do this first.
2. **VU Studio Agent as a Docker container with CA template** (section 4.4; `--net=host`, emhttp + `/mnt` read-only mounts): H value (works on every Unraid version, also gives NIC rates, hwmon, disk busy %), M effort *but shared with the Linux agent*; the template is a one-hour job plus a CA review wait. Do second, because the same binary serves Raspberry Pi, NAS and Steam Deck.
3. **Glances container** for users who already run it: S effort (generic source), covers CPU/RAM/network/sensors/containers but **not** parity or disk standby, so it complements, not replaces, 1 and 2. `node_exporter` the same.

**Steam Deck**
1. **VU Studio Agent for Linux x64, self-contained single-file, run as a user systemd service from `~/.local/share`** (section 5.3): H value (APU/GPU/battery/fan/charger/RAM/CPU, all verified sysfs paths), M effort shared with item 2 above, low risk (no root, no rootfs write, survives SteamOS updates; lingering via `sudo loginctl enable-linger deck`). Discover sensors by hwmon `name`, never by index.
2. FPS later, opt-in, through MangoHud CSV tailing (section 5.4): M effort, medium-high risk; make the dial "FPS" source disabled by default.
3. Not recommended: Glances/node_exporter on SteamOS (needs Python/pacman in a read-only image).

**Other Windows PCs**
1. **VU Studio Agent for Windows** (service or tray, same code base as Linux plus LibreHardwareMonitor): H value, M effort (installer, firewall rule, token), the only option with a uniform catalog, TLS-optional authentication and mDNS discovery.
2. **LibreHardwareMonitor's Remote Web Server** as the zero-install quick win: user enables *Options -> Remote Web Server* (default port 8085, optional Basic auth); poll `GET /data.json` (tree of `Children` with `Text`, `Value`, `Min`, `Max`, `Type`, `SensorId`, `RawValue`; read in `HttpServer.cs` of LibreHardwareMonitor master, October 2026). `Value` is a display string in the host's culture (decimal comma on Italian Windows, `°F` if the GUI is set so); `RawValue` is a plain JSON number on master, but a unit-suffixed string in the v0.9.6 release according to the sibling note `docs/integrations/windows-sources.md` section 5, so parse both forms. A Prometheus-like `GET /metrics` also exists but uses `name {"label"="value", ...} value` with quoted label names and converts units (Clock in Hz, Energy in Wh): a parser must accept quoted label names and blanks before `{` (the parser in section 2 does). S effort, M value (needs LHM running elevated), low risk. The other Windows-side providers (HWiNFO shared memory, LibreHardwareMonitorLib, RTSS/PresentMon, GSMTC, WASAPI) are researched in `docs/integrations/windows-sources.md`, not here.

**ISP router + Google Nest Wifi (double NAT)**
1. **ICMP latency/loss/jitter** to the default gateway, to the hop-2 router and to two public anchors (section 7.1): H value, S effort, no risk. First feature to ship in this group.
2. **Nest `GET /api/v1/status`** every 10-30 s: M value (internet-up flag as the router sees it, uptime, firmware flag, **upstream gateway IP** that unlocks step 3), S effort, medium risk (undocumented). Probe at setup time, hide the source if it fails.
3. **UPnP IGD counters of the ISP router, queried by unicast** (section 7.3): H value (true household WAN throughput and link capacity), M effort, risk = router support (TIM Hub ships UPnP off; Nest reports saturated byte counters). Ship with a "Test" button, per-brand help text, wrap handling, and a fallback to the PC's own NIC rate.
4. Skip vendor web APIs (Vodafone Station and others): brittle crypto, model zoo, unclear gain over UPnP.

**Home Assistant and MQTT**
1. **Home Assistant first** (it fronts Zigbee, Shelly, Tasmota, weather, energy meters and hundreds of others with names and units): start with REST `GET /api/states/<id>` + `POST /api/template` polling (S), then the WebSocket `subscribe_entities` client with the a/c/r reducer, reconnect and entity picker via `get_states` (M). H value. Keep the compressed protocol behind an interface because it is undocumented.
2. **MQTT** (MQTTnet 5.x, section 9): H value for users without HA or who want sub-second power data, M effort; ship presets for Tasmota, Shelly Gen2+, Zigbee2MQTT and HA-discovery catalog. Share the JSONPath engine with the HTTP source.

**Internet data**: Open-Meteo (S, H) with geocoding picker and attribution; crypto via CoinGecko keyless at 5-minute polling with optional Demo key, Binance public ticker as the fast alternative (S, M); stock quotes only as an experimental opt-in Yahoo source (S, high risk), no Stooq.

**Servers, NAS, Raspberry Pi**: Agent (arm64 and x64) first; accept Glances (v4 REST; version probe `/api/4/status`, `/api/3/status`) and Prometheus (`/metrics` scrape and optional PromQL) as generic sources because many users already run them; Netdata last (S effort, low value, v1 API flagged deprecated).

### 12.2 Suggested build order

| # | Item | Value | Effort | Risk | Needs |
|---|---|---|---|---|---|
| 1 | HTTP/JSON source + JSONPath engine, Open-Meteo preset | H | S | L | SensorHub |
| 2 | Ping/loss/gateway + double-NAT hint | H | S | L | none |
| 3 | Prometheus text parser + node_exporter/LHM `/metrics`, Glances v4 source (v3 compat) | M | S | L | #1 |
| 4 | VU Studio Agent v1: Linux (x64/arm64) from `/proc` `/sys`, token auth, HTTP pull as in the architecture diagram (optional WebSocket stream later), user-service installer for SteamOS, Docker image + Unraid CA template | H | M | L | #1 |
| 5 | Unraid GraphQL source | H | M | M | API key |
| 6 | Home Assistant REST then WebSocket | H | M | M | token |
| 7 | MQTT (MQTTnet 5) with presets | H | M | L | broker |
| 8 | Nest status + UPnP IGD (unicast + SSDP) | M | M | M | #2 |
| 9 | mDNS discovery (Makaretu.Dns.Multicast.New behind `IAgentDiscovery`) | M | M | M | #4 |
| 10 | Agent for Windows, LHM remote source, crypto sources | M | M | L | #4 |
| 11 | Experimental: Steam Deck FPS via MangoHud, Yahoo quotes, Netdata | L | S-M | H | |

### 12.3 Verify on real hardware before coding against it

The consolidated list of **UNVERIFIED** items, each cheap to test: (1) Nest Wifi `GET http://192.168.86.1/api/v1/status` on the owner's firmware (and `wan.gatewayIpAddress`), plus its IGD byte counters (`upnpc -s`); (2) UPnP state and counter behaviour of the actual ISP router, and unicast M-SEARCH on port 1900; (3) Unraid: `fsFree`/`temp` refresh cadence in `disks.ini`, API version actually bundled, whether `[fs] allow=shfs` shows `/mnt/user` in Glances; (4) Steam Deck: hwmon names present (`k10temp`, `steamdeck_hwmon` vs `jupiter`), `BAT1`/`ACAD` names on the OLED model, lingering behaviour across mode switches; (5) CoinGecko keyless limits and attribution wording; (6) Stooq key parameter name; (7) Yahoo chart endpoint with the default .NET User-Agent; (8) Shelly MQTT option names and Gen1 topics; (9) Glances `webui_allowed_hosts` behaviour behind a reverse proxy; (10) whether Windows prompts for inbound UDP 5353 when advertising.
