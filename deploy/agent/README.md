# VU Studio agent

A small cross-platform HTTP service that exposes the sensors of **another machine** (Unraid, Steam Deck, Raspberry Pi, a second Linux or Windows PC) to the main VU Studio service. VU Studio polls it and shows the sensors next to the local ones, with the agent's name as the host.

* Default port **5342**, plain HTTP, Bearer token. When the agent listens on the network (the default) and no token is configured, it generates one on the first start, saves it and logs it once in a box: copy it into VU Studio. Set `VUSTUDIO_AGENT_INSECURE=1` to run without a token on purpose.
* Requests must use a known `Host`: an IP address, `localhost`, the machine name (also `.local`, `.lan`, `.home`), any `*.local` name, or a name listed in `VUSTUDIO_AGENT_ALLOWED_HOSTS`. This stops a web page from reading the sensors through DNS rebinding.
* Linux: CPU load (total and per core) and frequency, load average, temperatures (hwmon and thermal zones), memory and swap, disk usage per mount, disk I/O rates, network rates per interface, uptime, batteries, AMD GPUs (`gpu_busy_percent`, VRAM, temperature, power; this covers the Steam Deck APU), NVIDIA GPUs through `nvidia-smi` when installed.
* Unraid, when `/var/local/emhttp/var.ini` is readable: array state and capacity, parity check progress, speed and ETA, disk temperatures (last known value while a disk is spun down), per-disk usage and errors. SMART is never read, so disks are not woken up.
* Windows: the basic subset of the `system` provider (CPU, memory, disks, network, uptime). For full hardware sensors install LibreHardwareMonitor or HWiNFO on that PC and use VU Studio's own providers.
* The agent is advertised over mDNS as `_vustudio._tcp` (TXT `role=agent`, `v=1`, `name=...`), so VU Studio can find it. Adding it by address always works.

## API

All endpoints need `Authorization: Bearer <token>` when a token is configured or generated.

| Method | Path | Result |
|---|---|---|
| GET | `/api/v1/info` | `{ name, version, os, hostname, uptimeSeconds }` |
| GET | `/api/v1/sensors` | `{ name, version, time, catalogVersion, sensors: [...], values: { "<id>": number \| string \| null } }` |

`sensors` holds the same metadata VU Studio uses everywhere (id, name, device, category, kind, unit, min, max, icon, valueKind, staleAfterSeconds). Values are in canonical units (°C, %, bytes, bytes/s, MHz, W, V, s). A `null` value means the sensor has no fresh reading.

## Sensor ids

| Id | Meaning |
|---|---|
| `system/cpu/load`, `system/cpu/<n>/load` | CPU load, total and per core (%) |
| `system/cpu/freq`, `system/cpu/<n>/freq` | clock (MHz) |
| `system/loadavg/1`, `/5`, `/15` | load average |
| `system/hwmon/<chip>/temp<n>`, `fan<n>`, `power<n>`... | hwmon channels (°C, RPM, W, V) |
| `system/mem/usage`, `used`, `available`, `total` | memory |
| `system/swap/usage`, `used`, `total` | swap |
| `system/disk/<mount>/usage`, `used`, `free` | filesystem per mount (pseudo filesystems skipped) |
| `system/diskio/<disk>/read`, `write`, `busy` | disk I/O (B/s, %) |
| `system/net/<iface>/rx`, `tx`, `system/net/rx`, `tx` | network rates (B/s) |
| `system/uptime`, `system/process/count` | host |
| `system/battery/<name>/capacity`, `status`, `power` | batteries (Steam Deck) |
| `system/gpu/amd<n>/load`, `vram/used`, `vram/total`, `vram/usage` | AMD GPU (temperature and power come from the hwmon ids) |
| `unraid/array/state`, `started`, `usage`, `used`, `free`, `total`, `problem-disks` | Unraid array |
| `unraid/parity/state`, `progress`, `speed`, `eta`, `errors` | parity check or sync |
| `unraid/disk/<name>/temp`, `usage`, `spun-down`, `errors` | Unraid disks (`parity`, `disk1`, `cache`...) |

## Settings

Environment variables (prefix `VUSTUDIO_AGENT_`) or command-line switches; the command line wins.

| Variable | Switch | Default | |
|---|---|---|---|
| `VUSTUDIO_AGENT_PORT` | `--port` | `5342` | |
| `VUSTUDIO_AGENT_TOKEN` | `--token` | generated | Bearer token. Without one (and with a non-loopback bind) a random token is generated on the first start and stored in the state file below. |
| `VUSTUDIO_AGENT_TOKEN_FILE` | `--token-file` | none | Read the token from a file (Docker secrets). |
| `VUSTUDIO_AGENT_DATA` | `--data` | see below | Directory for the generated token (`<dir>/token`). The Docker image sets `/data`. |
| `VUSTUDIO_AGENT_INSECURE` | `--insecure` | `false` | `1` runs without a token even on the network. Anyone on the LAN can then read the sensors. |
| `VUSTUDIO_AGENT_ALLOWED_HOSTS` | `--allowed-hosts` | none | Extra host names accepted in the `Host` header, comma separated (e.g. `tower.example.lan`). |
| `VUSTUDIO_AGENT_NAME` | `--name` | host name | Name shown in VU Studio. |
| `VUSTUDIO_AGENT_BIND` | `--bind` | `0.0.0.0` | `127.0.0.1` to keep it local. |
| `VUSTUDIO_AGENT_MDNS` | `--mdns` | `true` | Advertise over mDNS. |
| `VUSTUDIO_AGENT_INTERVAL` | `--interval` | `1` | Seconds between readings. |
| `VUSTUDIO_AGENT_PER_CORE` | `--per-core` | `true` | Per-core CPU sensors. |
| `VUSTUDIO_AGENT_NVIDIA_SMI` | `--nvidia-smi` | `true` | Read NVIDIA GPUs with `nvidia-smi`. |
| `VUSTUDIO_AGENT_UNRAID_PATH` | `--unraid-path` | `/var/local/emhttp` | Directory with Unraid's state files. |

In VU Studio add an **VU Studio Agent** source with the address (`http://host:5342`) and the token.

The generated token is kept in `$VUSTUDIO_AGENT_DATA/token`; without that variable in `$STATE_DIRECTORY/token` (systemd `StateDirectory=`), else `$XDG_STATE_HOME/vustudio-agent/token` (`~/.local/state/vustudio-agent/token`), and on Windows `%LOCALAPPDATA%\vustudio-agent\token`. Delete the file to get a new token. If the file cannot be written, the agent logs an error and uses a new token at every start.

## Docker

Build from the repository root:

```bash
docker build -f deploy/agent/Dockerfile -t vustudio-agent .
```

What the collectors read inside a container:

* `/proc/stat`, `/proc/meminfo`, `/proc/loadavg`, `/proc/diskstats`, `/proc/uptime`, `/proc/cpuinfo`: the container's own `/proc` already shows the host's values. **Do not** bind-mount `/proc` (`-v /proc:/proc` is rejected by runc).
* `/sys/class/hwmon`, `/sys/class/drm`, `/sys/class/power_supply`, `/sys/devices/system/cpu`, `/sys/block`: Docker mounts the host's sysfs read-only by default; nothing to add.
* Network rates (`/proc/net/dev` and `/sys/class/net`) are per network namespace: use `--network host`, which mDNS needs too.
* The process count lists `/proc/<pid>`, which is per PID namespace: add `--pid host` to count the host's processes (optional).
* Free space is read from the container's own mount table: mount the host filesystems you want to see (read-only).

```bash
docker run -d --name vustudio-agent --restart unless-stopped \
  --network host --pid host \
  -e VUSTUDIO_AGENT_NAME="My server" \
  -v vustudio-agent-data:/data \
  vustudio-agent
docker logs vustudio-agent   # shows the generated token once
```

Set `-e VUSTUDIO_AGENT_TOKEN=...` instead if you prefer your own token. `/data` must be writable by the image's `app` user (uid 1654): a named volume is; for a bind mount run `chown 1654 <dir>` first.

Unraid extras (array, parity, disk temperatures, free space of the array) need two more read-only mounts, plus the path variable:

```bash
  -v /var/local/emhttp:/host/emhttp:ro,rslave \
  -v /mnt:/mnt:ro,rslave \
  -e VUSTUDIO_AGENT_UNRAID_PATH=/host/emhttp
```

Mount the `emhttp` directory, not single files: emhttpd rewrites them. No `--privileged` and no Docker socket are needed.

## Unraid

Copy `deploy/agent/unraid/vustudio-agent.xml` to `/boot/config/plugins/dockerMan/templates-user/` on the server (or submit it to Community Applications), then add the container from the Docker tab. The template uses host networking, the host PID namespace, the two mounts above, a read-only root filesystem and `/mnt/user/appdata/vustudio-agent` as `/data`. Either type an access token in the template, or leave it empty and copy the generated token from the container log (it is kept in `appdata/vustudio-agent/token`; if the log says it could not be saved, run `chown 1654 /mnt/user/appdata/vustudio-agent`).

## Steam Deck

Run in Desktop Mode, in a terminal, as the normal user (no root, no `pacman`, no read-only-filesystem changes):

```bash
# With a prebuilt self-contained publish (folder or .tar.gz):
./install.sh --from ~/Downloads/vustudio-agent-linux-x64.tar.gz --name "Steam Deck"

# Or from a repository checkout with the .NET 10 SDK installed:
./install.sh
```

It installs into `~/.local/share/vustudio-agent`, writes the settings (with a random token, printed once) to `~/.config/vustudio-agent/agent.env`, and starts a systemd **user** service. Everything lives in `$HOME`, which SteamOS keeps across system updates. The script also tries `loginctl enable-linger` so the agent survives switching between Game Mode and Desktop Mode.

* Logs: `journalctl --user -u vustudio-agent -f`
* Uninstall: `./install.sh --uninstall`
* The Deck disappears from the network while it sleeps; VU Studio shows the sensors as offline and reconnects by itself.
* SteamOS has no firewall by default. Keep the token.

To build the bundle on another machine:

```bash
dotnet publish src/VuStudio.Agent -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
tar -czf vustudio-agent-linux-x64.tar.gz -C publish .
```

## Other Linux machines (Raspberry Pi, servers)

Publish for the target (`-r linux-x64` or `-r linux-arm64`), copy the folder to `/opt/vustudio-agent`, and install the unit:

```bash
sudo install -m 600 deploy/agent/systemd/vustudio-agent.env.example /etc/vustudio-agent.env   # edit the token
sudo cp deploy/agent/systemd/vustudio-agent.service /etc/systemd/system/
sudo systemctl enable --now vustudio-agent
```

The unit runs as a transient unprivileged user (`DynamicUser`) with a read-only view of the system. Everything the agent reads works without root; open TCP 5342 in the firewall if there is one. If the env file sets no token, the agent generates one and keeps it in `/var/lib/vustudio-agent/token` (`StateDirectory=`); `journalctl -u vustudio-agent` shows it once.

## Windows

`vustudio-agent.exe` (from `dotnet publish -r win-x64 --self-contained`) runs from a terminal or as a service created with `sc.exe create`. Only the basic `system` subset is available there.

## Notes

* Run `curl -H "Authorization: Bearer <token>" http://host:5342/api/v1/sensors` to see exactly what VU Studio receives.
* mDNS does not cross Docker bridge networks, VLANs or some Wi-Fi extenders. If the agent is not found, add it by address.
