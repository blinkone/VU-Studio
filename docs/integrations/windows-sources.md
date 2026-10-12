# Windows data sources and platform integration: research notes

> Research only. No application code lives here; snippets are illustrative.
> Written 2026-10-12 for the VU Studio implementers (`VuStudio.Server`, `VuStudio.Tray`, `VuStudio.Sources.Windows`,
> `VuStudio.Hardware`). Names follow [architecture.md](../architecture.md) and [requirements.md](../requirements.md).

## 0. How to read this document

* **No tag** = confirmed in a primary source (vendor header, project source at a cited tag/commit, official docs, NuGet
  metadata) or reproduced locally (Appendix B lists the experiments).
* **UNVERIFIED** = not confirmed from a primary source. Treat as a hypothesis and test on real hardware.
* Offsets are decimal byte offsets, little-endian, unless stated.
* Microsoft Learn, hwinfo.com and rivatuner/guru3d were **not reachable** from the research sandbox. Microsoft pages were
  read as Markdown in the public `MicrosoftDocs/*` GitHub repos (the source of learn.microsoft.com); the links point to the
  published page. HWiNFO and RTSS vendor headers were read from open-source copies (cited); HWiNFO forum statements come
  from search summaries and are marked as such.
* "Today" is 2026-10-12. Versions quoted (NAudio 3.1.0, PresentMon 2.6.0, LibreHardwareMonitorLib 0.9.6, ...) are as of that date.

### 0.1 Decisions at a glance

| Topic | Decision | Runs in | Biggest risk |
|---|---|---|---|
| HWiNFO shared memory | Own ~150-line reader (no NuGet dependency), poll 1 Hz, optional provider | Service (`Global\` mapping) | Free HWiNFO switches SHM off after 12 h; vendor licence text for the header is unclear |
| RTSS FPS | Own reader of `RTSSSharedMemoryV2`, foreground app via `dwLastForegroundApp` | Tray (same session) first; service only after a session-0 test | RTSS missing or not hooking the game; namespace reachability from session 0 is **UNVERIFIED** |
| PresentMon | Bundled MIT console exe as a child process, CSV on stdout | Service (LocalSystem has ETW rights) | Admin / "Performance Log Users" needed; CSV schema changes between versions |
| LibreHardwareMonitorLib 0.9.6 | Pin exactly, one update thread, history window = 0 | Service | PawnIO driver must be installed separately; memory growth if `ValuesTimeWindow` is not zeroed |
| LHM web server | HTTP client of `data.json` for other PCs | Service | Culture-formatted numbers in the JSON |
| NAudio | `NAudio.Wasapi` exact pin (3.1.0), never a floating version | Tray (user session) | `NAudio.Wasapi 22.0.0` is a mis-versioned, unlisted, genuine package that `Version="*"` still resolves |
| GSMTC media | `Windows.Media.Control` straight from the TFM, no package | Tray | Apps publish stale or missing timelines |
| User signals | `GetLastInputInfo`, `SystemEvents.SessionSwitch`, foreground PID, `UISettings` | Tray | `UISettings.ColorValuesChanged` is documented as unsupported in desktop apps |
| Service lifetime | Subclass `WindowsServiceLifetime`, enable power + session events | Service | Power events are queued to the thread pool; the OS does not wait for them |
| Hub port discovery | Per-OS enumerator, VID/PID filter plus protocol probe | Service (`VuStudio.Hardware`) | VID/PID `0403:6015` is shared by many FT231X devices |

---

## 1. HWiNFO shared memory sensor interface ("SMS", file `hwisenssm2.h`)

### 1.1 Names and interfaces

| Item | Value | Source |
|---|---|---|
| Local mapping | `Global\HWiNFO_SENS_SM2` | header, every reader below |
| Remote machines | `Global\HWiNFO_SENS_SM2_REMOTE_<n>`, n = 0, 1, ... (HWiNFO "Remote Sensor Monitoring") | [qdel/myrsm header](https://github.com/qdel/myrsm/blob/bb389d86de528a6de3348d047e58fbcee78f8222/hwisenssm2.h), [Seraksab reader](https://github.com/Seraksab/Hwinfo.SharedMemory.Net/blob/1d09864597f4342e2466880399ad0872892f870a/Hwinfo.SharedMemory/SharedMemoryReader.cs) |
| Mutex | `Global\HWiNFO_SM2_MUTEX`: "acquired when accessing the Shared Memory space. Release as quick as possible" | header comment |
| Enable switch | HWiNFO Settings, "Shared Memory Support". Default **off** since v7.00. HWiNFO must be running with sensors started | forum summary, below |
| Registry alternative ("Gadget") | `HKCU\SOFTWARE\HWiNFO64\VSB` and `HKLM\SOFTWARE\HWiNFO64\VSB`, see 1.6 | [Rainmeter docs](https://docs.rainmeter.net/tips/hwinfo/) |

The header declares the data as `#pragma pack(1)`; the area is `[header][sensor[]][reading[]]`. Never assume the offsets,
always use the three `dwOffsetOf...` / `dwSizeOf...` / `dwNum...` pairs of the header.

### 1.2 Layout (pack(1), measured by compiling the header and by parsing a real snapshot)

Header, 44 bytes for `dwRevision` 0, **48 bytes** for `dwRevision` >= 1:

| Off | Size | Type | Field | Meaning |
|---:|---:|---|---|---|
| 0 | 4 | DWORD | `dwSignature` | bytes `H W i S` (LE uint32 `0x53695748`) = live. `DEAD` per header comment when HWiNFO shut it down (byte order of `DEAD` **UNVERIFIED**) |
| 4 | 4 | DWORD | `dwVersion` | 1 = initial, 2 = UTF-8 strings added (HWiNFO v7.33+) |
| 8 | 4 | DWORD | `dwRevision` | 0 = HWiNFO <= 6.11, 1 = adds `dwPollingPeriod` (v6.11-3917+) |
| 12 | 8 | `__time64_t` | `poll_time` | last poll, Unix seconds (1 s resolution) |
| 20 | 4 | DWORD | `dwOffsetOfSensorSection` | 48 in practice |
| 24 | 4 | DWORD | `dwSizeOfSensorElement` | stride, 264 (v1) or 392 (v2) |
| 28 | 4 | DWORD | `dwNumSensorElements` | |
| 32 | 4 | DWORD | `dwOffsetOfReadingSection` | = 48 + sensors * stride in practice |
| 36 | 4 | DWORD | `dwSizeOfReadingElement` | stride, 316 (v1) or 460 (v2) |
| 40 | 4 | DWORD | `dwNumReadingElements` | |
| 44 | 4 | DWORD | `dwPollingPeriod` | ms, only if `dwRevision` >= 1 |

Sensor element (`HWiNFO_SENSORS_SENSOR_ELEMENT`), stride from header (264 / 392):

| Off | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 4 | `dwSensorID` | with `dwSensorInst` forms the unique sensor id (e.g. `0xF0000300`) |
| 4 | 4 | `dwSensorInst` | instance number |
| 8 | 128 | `szSensorNameOrig` | ANSI, English |
| 136 | 128 | `szSensorNameUser` | ANSI, may be renamed by the user |
| 264 | 128 | `utfSensorNameUser` | UTF-8, only if stride >= 392 |

Reading element (`HWiNFO_SENSORS_READING_ELEMENT`), stride from header (316 / 460):

| Off | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 4 | `tReading` | `SENSOR_READING_TYPE`, enum stored as 4 bytes |
| 4 | 4 | `dwSensorIndex` | index into the sensor array (changes if sensors come and go) |
| 8 | 4 | `dwReadingID` | unique within the sensor (e.g. `0x8000000`) |
| 12 | 128 | `szLabelOrig` | ANSI, English (stable) |
| 140 | 128 | `szLabelUser` | ANSI, user-renamed |
| 268 | 16 | `szUnit` | ANSI, e.g. `RPM` |
| 284 | 8 | `double Value` | **not 8-byte aligned**, read with `BinaryPrimitives` / `BitConverter`, never cast to `double*` |
| 292 | 8 | `double ValueMin` | |
| 300 | 8 | `double ValueMax` | |
| 308 | 8 | `double ValueAvg` | |
| 316 | 128 | `utfLabelUser` | UTF-8, only if stride >= 460 |
| 444 | 16 | `utfUnit` | UTF-8, only if stride >= 460 |

`HWiNFO_SENSORS_STRING_LEN2` = 128, `HWiNFO_UNIT_STRING_LEN` = 16. Enum `SENSOR_READING_TYPE` (values 0..8):
`NONE`=0, `TEMP`=1, `VOLT`=2, `FAN`=3, `CURRENT`=4, `POWER`=5, `CLOCK`=6, `USAGE`=7, `OTHER`=8. Typical units: `°C`, `V`, `RPM`,
`A`, `W`, `MHz`, `%`; `OTHER` carries anything (`MB`, `%`, `x`, ...). Use `szUnit`/`utfUnit` as the truth, not the enum.

**Ground truth.** The test snapshot of the MIT-licensed
[Hwinfo.SharedMemory.Net](https://github.com/Seraksab/Hwinfo.SharedMemory.Net/blob/1d09864597f4342e2466880399ad0872892f870a/Tests/TestData/hwinfo-sm2-snapshot.bin)
(captured 2026-08-15 on a Ryzen 9 7900X) parses as: signature `HWiS`, version 2, revision 1, sensor off/stride/count =
48/392/25, reading off/stride/count = 9848/460/470 (9848 = 48 + 25*392), `dwPollingPeriod` = 2000, total 226 048 bytes =
9848 + 470*460. This matches the field table above and the independent offset constants in
[moeilijk/hwinfo-streamdeck `layout.go`](https://github.com/moeilijk/hwinfo-streamdeck/blob/5a523fde3cdfef0f25f05dabf1b28fe55e51aca0/internal/hwinfo/layout.go).

### 1.3 Versions, strings, keys

| `dwVersion` | `dwRevision` | HWiNFO | Header | Sensor stride | Reading stride |
|---:|---:|---|---:|---:|---:|
| 1 | 0 | <= 6.11 | 44 | 264 | 316 |
| 1 | 1 | 6.11-3917 .. 7.32 | 48 | 264 | 316 |
| 2 | 1 | 7.33+ (snapshot above) | 48 | 392 | 460 |

* **Strings.** All `char` fields are ANSI in the *system* code page ("Current language for non-Unicode programs", machine-wide, so
  the same for LocalSystem and for users). The v2 `utf*` fields are UTF-8 and the header says to prefer them. Rule: use the UTF-8
  field when `dwSizeOf...Element` covers it, otherwise decode ANSI (register `CodePagesEncodingProvider` and use
  `Encoding.GetEncoding((int)GetACP())`, kernel32 `GetACP` = the active system ANSI code page; the UI culture is not the same thing). Fields are fixed buffers: stop at the first NUL,
  never trust a terminator to exist. The degree sign differs between ANSI (`0xB0`) and UTF-8 (`C2 B0`), so wrong decoding shows
  up as `Â°C`.
* **Never hard-code the strides.** Readers must accept larger strides (HWiNFO appends fields) and read only what they know.
* **Stable id** for the sensor catalog: `"{dwSensorID:X8}:{dwSensorInst}:{dwReadingID:X8}"`; keep `szLabelOrig` (English) as a
  secondary key because `dwSensorIndex` and the user labels change. The Stream Deck plugin uses the same triple.

### 1.4 Reading algorithm (torn-read safe)

HWiNFO republishes on every poll: it drops the reading count to zero and counts it back up while it refills the section
([Seraksab README](https://github.com/Seraksab/Hwinfo.SharedMemory.Net/blob/1d09864597f4342e2466880399ad0872892f870a/README.md#synchronization-with-hwinfo)).
Safe pattern (what the MIT reader does; do not hold pointers into the live view):

```csharp
using var mmf  = MemoryMappedFile.OpenExisting(@"Global\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);   // FileNotFoundException => off
using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
// optional: Mutex.TryOpenExisting(@"Global\HWiNFO_SM2_MUTEX", out m) -> m.WaitOne(1000) ; catch AbandonedMutexException (still owned),
//           UnauthorizedAccessException (HWiNFO elevated, mutex DACL denies us) => continue without the mutex
// 1) read 48 bytes; signature must be 0x53695748, else "unavailable"
// 2) total = readingOff + readingStride * readingCount ; require total <= view.Capacity and strides >= 264 / 316
// 3) byte[] copy = new byte[total]; view.ReadArray(0, copy, 0, total);        // one bulk copy (~226 KB here)
// 4) re-read the header: if poll_time, counts or offsets differ from step 1 => Thread.SpinWait(backoff), retry (max 5)
// 5) parse only from 'copy'; poll_time unchanged since last read => values unchanged (1 s resolution, see 1.5)
```

Also handle: mapping disappears (HWiNFO restart) so drop the cached view; `poll_time` older than ~1 min means an orphan mapping
(Seraksab reopens after `StalenessTimeout`, default 1 min).

### 1.5 Update rate

* Published once per HWiNFO sensor poll. Polling period is a user setting, default 2000 ms
  ([Rainmeter docs](https://docs.rainmeter.net/tips/hwinfo/): "every 2000ms"), reported in `dwPollingPeriod` (snapshot: 2000).
* `poll_time` has only 1 s resolution, so below 1 s you cannot tell new values from repeats by `poll_time`.
* A forum answer says SHM is exported at the same rate as the polling period (search summary, thread "Shared Memory Update Rate?").
  Minimum selectable period: 20 ms and 50 ms are both reported by third parties, **UNVERIFIED**.
* Recommendation: poll the mapping at 1 Hz (or `max(250 ms, dwPollingPeriod)`), publish only when `poll_time` or any value changed.

### 1.6 Free-version limit (12 hours)

* From HWiNFO v7.00 the Shared Memory interface of the non-Pro HWiNFO64 is **run-time limited to 12 hours**, then switched off with
  a notification; re-enabling restarts the countdown. Pro has no limit. The same announcement says the default of "Shared Memory
  Support" becomes *Disabled*, Remote Sensor Monitoring is limited to 5 machines (free), and that the interface details "will be
  made fully public". Source: HWiNFO forum
  [IMPORTANT CHANGES TO HWiNFO64 COMING SOON](https://www.hwinfo.com/forum/threads/important-changes-to-hwinfo64-coming-soon.7092/)
  (read through a search summary, page not fetchable here), and
  [Shared Memory Support setting becomes unchecked after a few hours](https://www.hwinfo.com/forum/threads/shared-memory-support-setting-becomes-unchecked-after-a-few-hours.7187/),
  [hwinfo-streamdeck issue 81](https://github.com/moeilijk/hwinfo-streamdeck/issues/81).
* Third-party workaround (kill and restart HWiNFO before 12 h): [Kaan88/HWiNFO-12-hour-limit-refresh](https://github.com/Kaan88/HWiNFO-12-hour-limit-refresh).
  Not a basis for a design.
* UX consequence: the provider must show "HWiNFO shared memory switched off (free edition 12 h limit)" when the mapping vanishes
  or `poll_time` stops moving, and recover automatically when it returns.

**"HWiNFO Gadget" registry interface (free, personal/non-commercial per HWiNFO's author).**

| Item | Value |
|---|---|
| Keys | `HKCU\SOFTWARE\HWiNFO64\VSB` (user running HWiNFO) and `HKLM\SOFTWARE\HWiNFO64\VSB` (written too; HWiNFO is normally elevated) |
| Values per sensor element N | `SensorN` (parent sensor name), `LabelN` (element name), `ValueN` (formatted, e.g. `4,198 MHz`, thousands separators + unit), `ValueRawN` (unformatted number as text, e.g. `4198`) |
| N | the "Index" HWiNFO shows in *Sensor Settings, HWiNFO Gadget tab*; changes when elements are added or removed |
| Opt-in | tick "Enable reporting to Gadget", then "Report value in Gadget" **per element** (manual); requires v7.02+ |
| Update | every sensor poll (default 2 s) |
| Limits | current value only (no min/max/avg), no remote machines, entries vanish while a sensor reports invalid values (feature-request thread, search summary), no documented count limit (**UNVERIFIED**) |

Source: [Rainmeter docs, Using HWiNFO with Rainmeter](https://docs.rainmeter.net/tips/hwinfo/) (written with HWiNFO's author; repo copy
`rainmeter/rainmeter-docs/source/tips/hwinfo.html`). Because the user must tick every element, it is a **fallback** only: read
`HKLM\...\VSB` from the service (64-bit view), parse `ValueRawN` with `InvariantCulture`, treat missing values as offline.

### 1.7 Access from a LocalSystem service

* The mapping and mutex live in the `Global\` namespace, which a session-0 service sees by default. HWiNFO normally runs elevated;
  the section still opens read-only for normal users, the mutex may not ([Seraksab README](https://github.com/Seraksab/Hwinfo.SharedMemory.Net/blob/1d09864597f4342e2466880399ad0872892f870a/README.md#synchronization-with-hwinfo)).
* A real `ServiceBase` precedent:
  [HWiNFO_Transfer_to_LCD `Service1.cs`](https://github.com/zipferot3000/HWiNFO_Transfer_to_LCD/blob/5776d32e3ac65d0be41e7e8b9355006f9477177a/HWiNFO%20Transfer%20to%20LCD/Service1.cs)
  (`OpenExisting("Global\\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read)`); the same call from a normal process:
  [Artemis HwInfo64Reader](https://github.com/Artemis-RGB/Artemis.Plugins.HardwareMonitors/blob/df3c7db56aeabc173529b843d526f50d55392a8d/src/Artemis.Plugins.HardwareMonitors.HWiNFO64/HwInfo64Reader.cs).
  The account of that service was not verified and the section's DACL is not documented (**UNVERIFIED**, test with LocalSystem).
* HWiNFO's sensors window must be running ("sensors started"); a forum report says the viewer could not connect until the sensors
  window had been shown at startup (search summary). Tell users to enable "Sensors" at startup.

### 1.8 Cross-check implementations and licensing

| Project | Language | Licence | Use |
|---|---|---|---|
| [Seraksab/Hwinfo.SharedMemory.Net](https://github.com/Seraksab/Hwinfo.SharedMemory.Net) 4.0.0 (NuGet owner `Seraksab`, Aug 2026) | C#, .NET 10 | MIT | best reference; has tests and a real snapshot |
| [moeilijk/hwinfo-streamdeck](https://github.com/moeilijk/hwinfo-streamdeck) | Go | see repo | independent offsets (`layout.go`) |
| [layer07/HWHash](https://github.com/layer07/HWHash), NuGet `HWHash` (owner `DLain`) | C# | none declared on NuGet | alternative |
| [Artemis-RGB/Artemis.Plugins.HardwareMonitors](https://github.com/Artemis-RGB/Artemis.Plugins.HardwareMonitors) | C# | see repo | plain `MemoryMappedFile` use |

Header licence: the early forum terms from the v3.83 era (search summary of
[Shared memory support](https://www.hwinfo.com/forum/threads/shared-memory-support.18/)) said the spec was only shared with developers
whose app is free of charge and that open-source apps must not publish the spec; the later v7.00 announcement says the details will be
made fully public, and `hwisenssm2.h` is mirrored in dozens of public repos (GitHub code search: 45 hits for the struct name). The
current formal licence of the header is **UNVERIFIED**.
**Recommendation:** write our own struct/offset constants from the tables above (facts about a binary layout), do not paste the
vendor header or its comments, do not bundle HWiNFO, and ask HWiNFO's author for written confirmation before release.

---

## 2. RivaTuner Statistics Server (RTSS) shared memory

### 2.1 Mapping and header

* Name: `RTSSSharedMemoryV2` (no prefix in nearly every reader found; `Global\` and `Local\` variants appear in a few,
  see 2.5). Open read-only (`FILE_MAP_READ`); most samples use `FILE_MAP_ALL_ACCESS` only because they also write the OSD.
* Header `RTSS_SHARED_MEMORY` (default alignment; offsets computed by compiling the SDK header in x64). Source of the layout:
  [RTSSSharedMemory.h](https://github.com/CXWorld/CapFrameX/blob/1b5f53da907f0a1de6fde2b75ff8267c79b08b2b/source/CapFrameX.RTSSInterface/Include/RTSSSharedMemory.h)
  (SDK copy in CapFrameX); official sample style:
  [RTSSSharedMemoryInterface.cpp "created by Unwinder"](https://github.com/jfoster/obs-rtss/blob/4c87ffa513459bb6d5b2d8c9476baa9fc32466c8/RTSSSharedMemoryInterface.cpp).

| Off | Size | Field | Since | Notes |
|---:|---:|---|---|---|
| 0 | 4 | `dwSignature` | | uint32 `0x52545353` (`'RTSS'`; the bytes in memory read `SSTR`) = valid, `0xDEAD` = being freed |
| 4 | 4 | `dwVersion` | | `(major<<16) + minor`; v2.21 = `0x00020015` |
| 8 | 4 | `dwAppEntrySize` | 2.0 | stride of one app entry |
| 12 | 4 | `dwAppArrOffset` | 2.0 | offset of the app array |
| 16 | 4 | `dwAppArrSize` | 2.0 | number of slots (256 in the SDK struct) |
| 20 / 24 / 28 | 4 each | `dwOSDEntrySize`, `dwOSDArrOffset`, `dwOSDArrSize` | 2.0 | OSD slots, not needed |
| 32 | 4 | `dwOSDFrame` | 2.0 | |
| 36 | 4 | `dwBusy` | 2.14 (`0x0002000E`) | OSD write lock |
| 40 / 44..63 | 4 / 20 | desktop video capture flags / stats | 2.15 | |
| **64** | 4 | `dwLastForegroundApp` | 2.16 (`0x00020010`) | **index** in the app array of the last foreground 3D application |
| **68** | 4 | `dwLastForegroundAppProcessID` | 2.16 | **PID** of the same application |
| 72 / 76 | 4 / 4 | `dwProcessPerfCountersEntrySize` / `...ArrOffset` | 2.18 | |
| 80 / 88 | 8 / 8 | `qwLatencyMarkerSet/ResetTimestamp` | 2.19 | |
| 96 | | `arrOSD[8]` ... then `arrApp[256]` | | use the header offsets: the SDK struct would put `arrApp` at 2 396 256 with 12 416-byte entries (v2.21), real values differ per RTSS version |

### 2.2 App entry (`RTSS_SHARED_MEMORY_APP_ENTRY`), offsets inside one entry

Fields only ever get appended, so an offset stays valid across versions; read a field only if `dwAppEntrySize` covers it **and**
`dwVersion` has reached the "since" version.

| Off | Size | Field | Since | Meaning |
|---:|---:|---|---|---|
| 0 | 4 | `dwProcessID` | 2.0 | 0 = empty slot |
| 4 | 260 | `char szName[MAX_PATH]` | 2.0 | executable name, often a full path (readers take the last path segment) |
| 264 | 4 | `dwFlags` | 2.0 (encoding below: 2.10) | from 2.10: low 16 bits = API (1 OGL, 2 DD, 3 D3D8, 4 D3D9, 5 D3D9EX, 6 D3D10, 7 D3D11, 8 D3D12, 9 D3D12AFR, 10 Vulkan), `0x10000` = x64, `0x20000` = UWP; 2.9 and older used a different bit layout (`APPFLAG_DEPRECATED_*` in the header), not worth supporting |
| **268** | 4 | `dwTime0` | 2.0 | start of the measurement period, ms; **must be non-zero** |
| **272** | 4 | `dwTime1` | 2.0 | end of the period, ms |
| **276** | 4 | `dwFrames` | 2.0 | frames rendered in `dwTime1 - dwTime0` |
| **280** | 4 | `dwFrameTime` | 2.0 | frame time of the last frame, microseconds |
| 284..312 | 4 each | `dwStatFlags/Time0/Time1/Frames/Count`, `dwStatFramerateMin/Avg/Max` | 2.0 | statistics-recording period (only meaningful while recording) |
| 316..907 | | OSD, capture, audio fields | 2.1 - 2.4 | not needed |
| 908 / 912 / 916 / 920 | 4 each | `dwStatFrameTimeMin/Avg/Max/Count` | 2.5 | frame-time statistics, microseconds |
| 924 | 4096 | `dwStatFrameTimeBuf[1024]` | 2.5 | ring buffer of recent frame times, microseconds |
| 5020 | 4 | `dwStatFrameTimeBufPos` | 2.5 | ring position (newest vs next slot, +-1: **UNVERIFIED**) |
| 5024 | 4 | `dwStatFrameTimeBufFramerate` | 2.5 | smoothed framerate **x10** (CapFrameX divides by 10) |
| 5072 | 8 | `qwStatTotalTime` | 2.13 | |
| 5080 | 4096 | `dwStatFrameTimeLowBuf[1024]` | 2.13 | |
| 9176 / 9180 | 4 each | `dwStatFramerate1Dot0PercentLow`, `...0Dot1PercentLow` | 2.13 | |
| 9184 / 9188 | 4 each | `dw1Dot0PercentLowBufPos`, `dw0Dot1PercentLowBufPos` | 2.17 | |
| 9224 / 9228 | 4 each | `dwResolutionX`, `dwResolutionY` | 2.20 (`0x00020014`) | render resolution, 0 until measured |
| 9232..9343 | | per-frame timestamps `qw*Time`, `dwGpuActiveRenderTime`, `dwGpuFrameTime` | 2.21 | |
| 9344 | 3072 | `arrPerfCounters[256]` | 2.18 | access via offsets only |

Entry size is 12 416 in the v2.21 layout; older RTSS versions are smaller (DynamicFPSLimiter assumes 4 485 160 bytes in total).
Map the whole object (`CreateViewAccessor(0, 0)`) and trust only the header.

### 2.3 FPS and frame time, exactly

```csharp
// e = bytes of one entry: base + dwAppArrOffset + i * dwAppEntrySize ; skip if dwProcessID == 0 ; version = header dwVersion
uint t0 = U32(e, 268), t1 = U32(e, 272), frames = U32(e, 276), ftUs = U32(e, 280);
double fpsPeriod  = (t0 != 0 && unchecked(t1 - t0) > 0) ? 1000.0 * frames / unchecked(t1 - t0) : 0; // header: "framerate calculated once per second"
double fpsInstant = ftUs != 0 ? 1_000_000.0 / ftUs : 0;                                           // header: "calculated once per frame"
double frameMs    = ftUs / 1000.0;
double fpsSmooth  = version >= 0x00020005 ? U32(e, 5024) / 10.0 : fpsPeriod;                      // CapFrameX uses this one for its text value
```

Source of both formulas: the struct comments in the SDK header; the `/10` smoothing and the 1024-entry microsecond ring are what
[CapFrameX `RTSSCoreControl.cpp`](https://github.com/CXWorld/CapFrameX/blob/1b5f53da907f0a1de6fde2b75ff8267c79b08b2b/source/CapFrameX.RTSSInterface/RTSSCoreControl.cpp)
(`GetCurrentFramerate`, `GetFrameTimesInterval`) does. Use `dwFrameTime` for a needle that must react per frame, `fpsPeriod` for text.
Frame time shown to the user = `1000 / fps` (CapFrameX does this since its issue 394: the raw last sample jitters).

Staleness (an entry stays in the array while the game is minimised or paused): treat an entry as dead when `dwTime0`/`dwTime1` did not
change between two polls (what [DynamicFPSLimiter](https://github.com/SameSalamander5710/DynamicFPSLimiter/blob/ba5526ce3f35a01cdca09d8f185408cef908671d/src/core/rtss_interface.py)
does with `last_dwTime0s`) for ~2 s. Comparing `dwTime1` with `GetTickCount()` is natural but the clock source is not documented (**UNVERIFIED**).

### 2.4 Picking the active 3D application

1. If `dwVersion >= 0x00020010`: `pid = U32(header, 68)`, `idx = U32(header, 64)`; take that entry (check `entry.dwProcessID == pid`).
2. Else (or if the entry is stale): `GetForegroundWindow()` -> `GetWindowThreadProcessId` and match `dwProcessID` (what DynamicFPSLimiter does).
3. Else: the entry with the most recent change of `dwTime1`.
Skip entries whose name is in an ignore list (launchers, overlays) and whose `dwFrames == 0`.

### 2.5 Namespace, session and licensing

* Every reader opens the unprefixed name, i.e. the **session-local** namespace of the caller; two readers try `Global\` or `Local\`
  ([JoystickGremlinEx](https://github.com/muchimi/JoystickGremlinEx/blob/6dc334337c4cde35b9a8c19c438a3cae156431ad/gremlin/ui/obs_overlay/sys_stats.py),
  [GameIndex](https://github.com/atxoxx/GameIndex/blob/ebcc911d498f7d704a5eccb21302dff3bc0297ba/src-tauri/src/rtss_reader.rs)). Whether RTSS also
  publishes in `Global\` is **UNVERIFIED**. A service in session 0 therefore **cannot be assumed to see it**; read it in the tray app
  (same interactive session) and push `session/game/*` sensors to the service. Test `Global\RTSSSharedMemoryV2` from LocalSystem before
  deciding to read it in the service.
* RTSS is proprietary freeware (no redistribution clause found, **UNVERIFIED**); do not bundle it. Detect it (mapping exists,
  signature valid) and show "install RTSS (ships with MSI Afterburner) or use PresentMon". Reading the mapping does not inject anything
  into games; RTSS itself does (the user's choice).
* Licence of the header: bundled in tools such as CapFrameX and OBS plugins; formal terms **UNVERIFIED**. Same recommendation as HWiNFO:
  own constants for the ~15 fields we read.

---

## 3. PresentMon (Intel)

### 3.1 Version, distribution, licence

* Repo [GameTechDev/PresentMon](https://github.com/GameTechDev/PresentMon), `main` at 2026-10-08, latest tag **v2.6.0** (a news report dates it
  2026-09-21; release notes mirrored at [newreleases.io](https://newreleases.io/project/github/GameTechDev/PresentMon/release/v2.6.0)).
  Tags v2.0.0 .. v2.6.0 exist; `Version.props` says 2.6.0.
* Three deliverables ([README](https://github.com/GameTechDev/PresentMon/blob/main/README.md)):
  1. **Console application**, standalone, ETW based, CSV to file or stdout. Binary name pattern `PresentMon-<ver>-x64.exe`
     ([README-ConsoleApplication.md](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md) names `PresentMon-2.3.1-x64.exe`;
     the exact 2.6.0 asset names were not visible from here, **UNVERIFIED**). This is what we want.
  2. **PresentMon Service + SDK** (`PresentMonAPI2.dll`, C API, loader library). Installed by Intel's installer to
     `Program Files\Intel\PresentMon\SDK`; "if a client ships their own copy of PresentMonAPI2.dll, binary compatibility with the service will
     not be guaranteed" ([README-Service.md](https://github.com/GameTechDev/PresentMon/blob/main/README-Service.md)). Heavier; milestone 2 at best.
  3. **Capture application** (GUI + overlay). Not relevant.
* **Licence: MIT** (`LICENSE.txt`, "Copyright (C) 2017-2024 Intel Corporation"). Redistribution of the exe inside our installer is allowed;
  ship `LICENSE.txt` and `THIRD_PARTY.txt` (AMD ADL and NVIDIA NVAPI notices, both MIT) next to it. Compatible with GPL-3.0.

### 3.2 Command line (verified against `PresentMon/CommandLine.cpp`)

| Need | Flag |
|---|---|
| One process (repeatable, case-insensitive, no path/extension needed) | `--process_name game.exe` |
| One PID | `--process_id 1234` |
| All processes | omit `--process_name`; optionally `--exclude dwm.exe` (repeatable) |
| CSV to stdout | `--output_stdout` (forces console stats off; rows are flushed per row) |
| No CSV at all | `--no_csv` |
| Separate ETW session | `--session_name VuStudio` (default name `PresentMon`), `--stop_existing_session`, `--terminate_existing_session` (stop and exit) |
| Less work per frame | `--no_track_gpu --no_track_input` (keeps display tracking; `--no_track_display` additionally requires both) |
| Stable legacy columns | `--v1_metrics` (PresentMon 1.x names) ; `--v2_metrics` (2.x names) |
| Exit when target ends | `--terminate_on_proc_exit` ; `--timed <s>`, `--delay <s>` |

```text
PresentMon-2.6.0-x64.exe --output_stdout --session_name VuStudio --stop_existing_session --v1_metrics --exclude dwm.exe
PresentMon-2.6.0-x64.exe --output_stdout --session_name VuStudio --stop_existing_session --process_name eldenring.exe
```

### 3.3 CSV columns (verified against `PresentMon/CsvOutput.cpp`, v2.6.0)

All time columns are milliseconds. The first line is a header; parse **by column name, case-insensitively**, never by index
(the set depends on flags and version, and `--v1_metrics` uses lower-camel `ms...` names).

| Purpose | Default (2.6.0) | `--v1_metrics` | `--v2_metrics` |
|---|---|---|---|
| Process | `Application`, `ProcessID`, `SwapChainAddress` | same | same |
| API | `PresentRuntime` | `Runtime` | `PresentRuntime` |
| Time since start | `TimeInSeconds` and `CPUStartTimeInSeconds` (both present) | `TimeInSeconds` | `CPUStartTime` |
| **Frame time (FPS = 1000 / x)** | `MsBetweenPresents` | `msBetweenPresents` | `FrameTime` |
| Displayed frame interval | `MsBetweenDisplayChange` (`NA` if not displayed) | `msBetweenDisplayChange` | `DisplayedTime` |
| CPU / GPU | `MsCPUBusy`, `MsCPUWait`, `MsGPUTime`, `MsGPUBusy`, `MsGPUWait`, `MsGPULatency` | `msGPUActive` | `CPUBusy`, `CPUWait`, `GPUTime`, `GPUBusy`, `GPUWait` |
| Latency | `MsUntilDisplayed`, `MsRenderPresentLatency`, `MsClickToPhotonLatency`, `MsAllInputToPhotonLatency`, `MsBetweenAppStart` | `msUntilDisplayed`, `msSinceInput` | `DisplayLatency`, `ClickToPhotonLatency` |
| Present mode | `AllowsTearing`, `PresentMode` | `Dropped`, `AllowsTearing`, `PresentMode` | `AllowsTearing`, `PresentMode` |

One row = one presented frame. FPS: `fps = 1000 / MsBetweenPresents` per row, or (better for a needle) the number of rows in the last 1 s
window per `ProcessID`/`SwapChainAddress`; displayed FPS from `MsBetweenDisplayChange`. Frames of the same process on several swap chains
(launchers, overlays, DWM) must be separated by `SwapChainAddress`; pick the chain with the highest rate.

### 3.4 Privileges and behaviour

* Needs administrator **or** membership of "Performance Log Users" (error "failed to start trace session (access denied)"), see README
  "User access denied". Without admin it cannot resolve other users' or short-lived processes (`<unknown>`) and cannot target them by name
  ([README](https://github.com/GameTechDev/PresentMon/blob/main/README.md#user-access-denied)). `--restart_as_admin` exists. The service
  runs as LocalSystem, so spawn it from the service, not from the tray.
* It is a real-time ETW consumer (no DLL injection), so it works with DX9 to DX12, OpenGL, Vulkan, UWP, and does not hook games.
* A killed process can leave the named ETW session running: always start with `--stop_existing_session` and a unique `--session_name`;
  on shutdown run `--terminate_existing_session --session_name VuStudio`.
* All-process mode emits a row per Present of every swap chain on the machine (DWM, browsers): high line rate. Prefer `--process_name` once the
  foreground game is known (restart the child when it changes, debounce 2 s), or parse everything and discard cheaply.
* Process management: `ProcessStartInfo { RedirectStandardOutput = true, CreateNoWindow = true }`, read lines on a dedicated task, watchdog if
  no row for N seconds while a game is foreground, restart with backoff.

---

## 4. LibreHardwareMonitorLib 0.9.6 (NuGet)

### 4.1 Package facts

| Item | Value |
|---|---|
| Id / version | `LibreHardwareMonitorLib` 0.9.6 (stable; tag `v0.9.6`, commit `3d331e3`, 2026-02-14). Pre-release line `0.9.7-pre7xx` exists |
| NuGet owner / licence | profile `lhm`; **MPL-2.0** |
| Layout | `ref/{net472,netstandard2.0,net8.0,net9.0,net10.0}` and `runtimes/{win-x64,win-x86,win-arm64}/lib/<tfm>/LibreHardwareMonitorLib.dll`. There is **no** `lib/` folder |
| Consequence (probe 1) | A RID-less `dotnet build` of a `net10.0-windows10.0.19041.0` project compiles and copies the three RID DLLs to `bin/.../runtimes/*/lib/net10.0/`; `deps.json` lists them as `runtimeTargets`, the host picks by RID. Windows-only at run time: guard registration with `OperatingSystem.IsWindows()` |
| Dependencies (net10.0 group) | DiskInfoToolkit 1.1.2 (MPL-2.0), HidSharp 2.6.4 (Apache-2.0), Mono.Posix.NETStandard 1.0.0, RAMSPDToolkit-NDD 1.4.2 (MPL-2.0), System.IO.FileSystem.AccessControl 5.0.0, System.IO.Ports 10.0.3, System.Management 10.0.2; transitive BlackSharp.Core (MPL-2.0) |

Sources: [nuspec 0.9.6](https://api.nuget.org/v3-flatcontainer/librehardwaremonitorlib/0.9.6/librehardwaremonitorlib.nuspec),
[`LibreHardwareMonitorLib.csproj` at v0.9.6](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/LibreHardwareMonitorLib.csproj),
[README (sample code)](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/README.md#developer-information).

### 4.2 `Computer` (namespace `LibreHardwareMonitor.Hardware`)

All flags default `false`; they can also be flipped after `Open()` (groups are added or removed live). Group wiring from `Computer.AddGroups()`:

| Property | Adds | Needs PawnIO | Notes |
|---|---|---|---|
| `IsMotherboardEnabled` | SMBIOS board + Super I/O (`SubHardware`) + embedded controller | yes | `Motherboard.Update()` does nothing, its `SubHardware` must be updated |
| `IsCpuEnabled` | Intel / AMD CPUs | yes for DTS temperature, package power, SMU (MSR based) | total/core load comes from OS counters; which clock/power sensors survive without the driver is **UNVERIFIED** |
| `IsMemoryEnabled` | `/ram`, `/vram`, DIMM SPD (temperature) | SPD only | |
| `IsGpuEnabled` | AMD (ADL), NVIDIA (NVAPI), Intel (only if `IsCpuEnabled`) | Intel iGPU yes | |
| `IsStorageEnabled` | SATA/NVMe SMART, temperatures, usage | no | see `StorageDevice.ThrottleInterval` |
| `IsNetworkEnabled` | NIC load/throughput | no | raises `HardwareAdded/Removed` on hot-plug |
| `IsControllerEnabled` | T-Balancer, Heatmaster, AquaComputer, AeroCool, NZXT, Razer, Arctic, MSI fan/liquid controllers | no | **opens serial ports**, see 4.7 |
| `IsPsuEnabled` | Corsair / MSI PSUs (HID) | no | |
| `IsBatteryEnabled` | laptop battery | no | |
| `IsPowerMonitorEnabled` | WireView Pro II (STM32 `0483:5740`) | no | leave off |

Source: [`Computer.cs` v0.9.6](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Computer.cs).
Members: `Open()`, `Close()`, `Reset()` (removes and re-adds all groups), `Accept(IVisitor)`, `Traverse(IVisitor)`, `Hardware` (copy of the top-level list),
`GetReport()`, `SMBios`, events `HardwareAdded` / `HardwareRemoved`.

### 4.3 Lifecycle and the visitor pattern

```csharp
sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer c)  => c.Traverse(this);
    public void VisitHardware(IHardware h)  { h.Update(); foreach (var s in h.SubHardware) s.Accept(this); }   // SubHardware: required for SuperIO
    public void VisitSensor(ISensor s)      { }
    public void VisitParameter(IParameter p) { }
}
var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true,
                              IsStorageEnabled = true, IsNetworkEnabled = true };           // controllers/PSU/battery: opt-in, see 4.7
computer.Open();                                                                             // slow (seconds); not on the host start path
computer.Accept(new SensorVisitor(s => s.ValuesTimeWindow = TimeSpan.Zero));                 // IMPORTANT, see 4.8; repeat after Reset()/HardwareAdded
// loop, ONE dedicated thread, every 1000 ms:
computer.Accept(updateVisitor);                                                              // Update() every hardware; then read ISensor.Value
// on resume from sleep:  computer.Reset();   on shutdown: computer.Close();
```

`SensorVisitor(SensorEventHandler)` is a public LHM class (calls the handler for every active sensor, descends into `SubHardware` through
`Motherboard.Traverse`). The GUI uses the same trick ([`MainForm.cs`](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitor/UI/MainForm.cs)).
The README `UpdateVisitor` never calls `VisitSensor` (sensors are only read afterwards).

### 4.4 `IHardware`, `ISensor`, identifiers

* `IHardware`: `HardwareType`, `Identifier`, `Name` (settable), `Parent`, `Sensors` (**allocates a copy per access**, `_active.ToArray()`),
  `SubHardware`, `Properties`, `Update()`, `GetReport()`, `Accept/Traverse`, events `SensorAdded` / `SensorRemoved`.
* `ISensor`: `Identifier`, `Index`, `Name` (settable), `SensorType`, `Hardware`, `Value` (`float?`, `null` = no reading, can also be NaN),
  `Min` / `Max` (`float?`, since open or `ResetMin/ResetMax`), `IsDefaultHidden`, `Parameters`, `Control`, `Values` (history), `ValuesTimeWindow`, `ClearValues()`.
* `Identifier.ToString()` = `/hardware-path/<sensortype lower-case>/<index>`. Hardware paths seen in source: `/intelcpu/0`, `/amdcpu/0`, `/ram`, `/vram`,
  `/memory/dimm/<n>`, `/gpu-nvidia/<n>`, `/gpu-amd/<n>`, `/gpu-intel/<id>`, `/gpu-intel-integrated/<id>`, `/nvme/<n>`, `/hdd/<n>`, `/nic/<adapter guid>`,
  `/lpc/<chip>/<n>`, `/lpc/ec`, `/battery/<name>_<tag>`, `/psu/corsair|msi/<n>`. Examples: `/intelcpu/0/temperature/0`, `/gpu-nvidia/0/load/0`.
  Suggested VU Studio sensor id: `lhm` + identifier, as in the architecture doc (`lhm/cpu/0/temperature/1` style). Indexes can shift when hardware order changes:
  keep `Name` in the catalog entry and let users re-bind.
* Cache `ISensor` references in a dictionary and update them from `HardwareAdded/Removed` and `SensorAdded/Removed`; do not walk the tree each tick.

### 4.5 `SensorType` (enum order as in 0.9.6) and `HardwareType`

| SensorType | Unit (source comment) | Canonical unit in VU Studio |
|---|---|---|
| `Voltage` | V | `V` |
| `Current` | A | `A` |
| `Power` | W | `W` |
| `Clock` | MHz | `MHz` |
| `Temperature` | °C | `°C` |
| `Load` | % | `%` |
| `Frequency` | Hz | `Hz` (not in canonical list: add) |
| `Fan` | RPM | `RPM` |
| `Flow` | L/h | `L/h` |
| `Control` | % | `%` |
| `Level` | % | `%` |
| `Factor` | 1 (dimensionless) | `""` |
| `Data` | GB = 2^30 bytes | convert to `B` |
| `SmallData` | MB = 2^20 bytes | convert to `B` |
| `Throughput` | B/s | `B/s` |
| `TimeSpan` | seconds | `s` |
| `Timing` | ns | convert to `ms` |
| `Energy` | mWh | convert to `Wh` |
| `Noise` | dBA | `dB` |
| `Conductivity` | µS/cm | custom |
| `Humidity` | % | `%` |

`HardwareType`: `Motherboard`, `SuperIO`, `Cpu`, `Memory`, `GpuNvidia`, `GpuAmd`, `GpuIntel`, `Storage`, `Network`, `Cooler`, `EmbeddedController`,
`Psu`, `Battery`, `PowerMonitor`. Sources: [`ISensor.cs`](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/ISensor.cs),
[`HardwareType.cs`](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/HardwareType.cs).
Do not copy LHM's own Prometheus exporter factors (it treats `Data` as 10^9 bytes, contradicting the 2^30 comment).

### 4.6 Kernel driver in 2025-2026: WinRing0 gone, PawnIO required (separately)

* 0.9.6 contains **no WinRing0**; ring-0 access goes through [PawnIO](https://github.com/namazso/PawnIO). Twelve PawnIO modules (`AMDFamily0F/10/17`, `IntelMSR`,
  `IsaBridgeEC`, `LpcACPIEC`, `LpcCrOSEC`, `LpcIO`, `RyzenSMU`, `SmbusI801`, `SmbusNCT6793`, `SmbusPIIX4`) are **embedded as resources** in the DLL, but the
  **driver is not bundled**: the NuGet package has only managed DLLs. The user (or our installer) must install PawnIO.
* How the lib behaves (`PawnIo.cs` v0.9.6): static `PawnIo.IsInstalled` / `PawnIo.Version` read
  `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO\DisplayVersion` (also the 64-bit view); modules load by opening
  `\\?\GLOBALROOT\Device\PawnIO` and sending the module with `DeviceIoControl`. If the device cannot be opened, the module object is "not loaded",
  `Execute` returns a zero-filled array and nothing throws. Effect on Intel CPUs: the DTS valid bit (`eax & 0x80000000`) is never set, so core temperatures
  are `null`; Super I/O, EC and SPD sensors simply do not appear. The lib never tells you; **VU Studio must call `PawnIo.IsInstalled` itself** and surface
  "CPU temperature unavailable, install PawnIO" (plus HWiNFO as an alternative).
* Installer facts ([winget manifest `namazso.PawnIO` 2.2.0](https://github.com/microsoft/winget-pkgs/tree/master/manifests/n/namazso/PawnIO/2.2.0)): `PawnIO_setup.exe`
  from [namazso/PawnIO.Setup releases](https://github.com/namazso/PawnIO.Setup), released 2026-03-15, scope machine, elevates itself, silent switch
  `-install -silent`; release notes: the device is admin-only by default, "possible to expose it to non-administrators, although not recommended". The service
  (LocalSystem) satisfies this.
* Licences: driver source GPL-2.0-or-later with an exception for programs that talk to it only through the IOCTL interface (so LHM and VU Studio are fine);
  modules LGPL-2.1; the winget manifest labels the signed installer "Proprietary (Freeware)". Redistribution of the installer binary inside ours is
  **UNVERIFIED**. **Recommendation:** do not bundle; a settings button runs `winget install --id namazso.PawnIO -e --silent` (or downloads the
  release asset) after explicit consent, and a rescan follows.
* Why the swap happened: WinRing0 (CVE-2020-14979, arbitrary physical-memory access for any local user) has been flagged by Windows Defender since March 2025
  ([Neowin](https://www.neowin.net/news/windows-1110-is-flagging-winring0-on-your-pc-monitoring-fan-control-apps-heres-why/),
  [Slashdot](https://it.slashdot.org/story/25/03/14/1351225/windows-defender-now-flags-winring0-driver-as-security-threat-breaking-multiple-pc-monitoring-tools));
  LHM replaced it with PawnIO (release-notes entry "Swap WinRing0 to PawnIO", via search summary). Compatibility of PawnIO with Memory Integrity (HVCI)
  was not verified (**UNVERIFIED**).

### 4.7 Side effects on serial ports and global mutexes (verified in source)

`Computer.Open()` with `IsControllerEnabled` probes devices. Checked against the VU1 hub (FT231X, `0403:6015`, 115200 baud):

| Group | What it opens | Touches the hub? |
|---|---|---|
| Heatmaster | COM ports registered under `HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_10C4&PID_EA60[&MI_00]\*\Device Parameters\PortName`, opens at 38400 baud, writes `0xAA` | No (CP210x only) |
| T-Balancer | FTDI D2XX (`ftd2xx.dll`) devices of type `FT_DEVICE_232BM`, writes `0x38` | No (FT231X is another device type) |
| WireView Pro II (`IsPowerMonitorEnabled`) | COM ports with VID/PID `0483:5740` (SetupAPI present-only search) | No |

Also: LHM creates/opens world-accessible global mutexes `Global\Access_ISABUS.HTP.Method`, `Global\Access_PCI`, `Global\Access_EC`,
`Global\RazerReadWriteGuardMutex`, `Global\Access_USB_Sensors`, shared with other monitoring tools. Running LHM and HWiNFO on the same Super I/O
at the same time is a double-polling risk; prefer one source per chip (e.g. disable LHM `IsMotherboardEnabled` while HWiNFO SHM is live), conflicts **UNVERIFIED**.

### 4.8 Threading, cost, memory (the part that bites)

* **`Sensor.ValuesTimeWindow` defaults to 24 hours** (`TimeSpan.FromDays(1.0)` in `Sensor.cs`). Every 4th `Value` assignment appends the average of the last four values
  to a `List<SensorValue>` (two equal consecutive samples collapse into one, so flat sensors do not grow) and old entries are trimmed with `RemoveAt(0)`: large
  per-sensor history and O(n) front removal. Worst case with values that change every sample (estimate, not measured): at 1 s polling about 21 600 retained samples per
  sensor per day, 16 bytes each (`float` + `DateTime`, padded), i.e. roughly 0.35 MB per sensor and on the order of 100 MB for a few hundred sensors. The GUI even
  offers 30 s .. 24 h. Set it to `TimeSpan.Zero` for every sensor (clears and disables the history)
  right after `Open()`, after `Reset()`, and on `HardwareAdded`; keep our own history (`SensorHub`/SQLite). This is mandatory for NFR-3 (< 150 MB, < 2 % CPU).
* `Update()` is not thread-safe per hardware; use one dedicated thread. `Computer.Hardware` and `Traverse` lock internally; `Traverse` uses index loops
  "to avoid a collection modified exception after sleep".
* Interval: the GUI offers 250 ms to 10 s, default 1 s. Use 1 s for the catalog; slower for storage: `StorageDevice.ThrottleInterval = TimeSpan.FromSeconds(30)`
  (public static) throttles SMART queries (disk wake-ups, stutter); NVMe temperature is cheap.
* After sleep/resume the GUI calls `computer.Reset()` (`PowerModeChanged`). Upstream `master` shipped and reverted "Fix hang after hibernation (#2452 / #2572)"
  on 2026-10-12, so hangs around resume are real. Mitigations: `Reset()` on the update thread after `ResumeAutomatic` (see section 9), per-pass watchdog
  (if one pass exceeds ~10 s, abandon the thread and recreate `Computer`), optionally host LHM in a child process we can kill.
* Measured cost: **not measured** (no Windows hardware in the research environment). Budget: `Open()` seconds; one pass tens of ms (**UNVERIFIED**).
* Admin: the README says some sensors need administrator rights; with PawnIO that means the process must be able to open the device (LocalSystem can).
  Precedent for LHM inside `UseWindowsService`: [divoom-monitor `hardware-monitor-cli`](https://github.com/Pisyukaev/divoom-monitor/blob/a8921164dfa2464d5e1cfaa27db40c81694102af/hardware-monitor-cli/Program.cs).

### 4.9 Licence and GPL-3.0

MPL-2.0 is file-level copyleft. §3.3 lets a Larger Work be distributed under other terms, and when the Larger Work combines Covered Software with a work under a
Secondary License and the files are not marked "Incompatible With Secondary Licenses", the Covered Software may additionally be distributed under that
Secondary License; §1.12 lists GPL 2.0 and later (so GPL-3.0). LHM's source headers carry no Exhibit B marker (grep over v0.9.6), so using the unmodified NuGet package
in a GPL-3.0 application is fine. Obligations: keep MPL notices, point to the LHM source tag, ship LHM's `THIRD-PARTY-NOTICES.txt` (includes PawnIO.Modules LGPL-2.1 text).
Dependency licences: MPL-2.0 (DiskInfoToolkit, RAMSPDToolkit-NDD, BlackSharp.Core), Apache-2.0 (HidSharp), MIT (Microsoft packages): all GPL-3.0-compatible.

---

## 5. LibreHardwareMonitor remote web server (`data.json`)

The server is part of the **GUI app** `LibreHardwareMonitor.exe` ("Options, Remote Web Server, Run"), not of the NuGet library. Source:
[`HttpServer.cs` v0.9.6](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitor/Utilities/HttpServer.cs),
[`MainForm.cs`](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitor/UI/MainForm.cs) (settings keys).

| Item | Value |
|---|---|
| URL | `http://<host>:8085/data.json` (settings `listenerPort` default **8085**; `listenerIp` default `?` which falls back to `+` = all interfaces) |
| Auth | off by default (`authenticationEnabled=false`); when on: HTTP **Basic**, realm "Libre Hardware Monitor", user name compare + SHA-256 of the password. Cleartext over HTTP: LAN-only, treat as weak |
| Headers | `Cache-Control: no-cache`, `Access-Control-Allow-Origin: *`, `Content-Encoding: gzip` when the client sends `Accept-Encoding: gzip` |
| Other GETs | `/metrics` (OpenMetrics-like text, built from the sensor *history*), `/Sensor?action=Get&id=<SensorId>` -> `{"result":"ok","value":42.0,"min":..,"max":..,"format":"{0:F1} °C"}` (JSON numbers, culture independent), `/ResetAllMinMax` |
| POST | `/Sensor?action=Set&id=<SensorId>&value=<n\|null>` sets a fan/control value. Never call it from VU Studio |

Tree shape: `{"id":0,"Text":"Sensor","Min":"Min","Value":"Value","Max":"Max","ImageURL":"","Children":[ <computer node> ]}`. Every node has `id` (depth-first counter,
**regenerated per request, not stable**), `Text`, `Min`, `Value`, `Max` (strings, empty for non-sensors), `ImageURL`, `Children`. Extra keys: hardware nodes `HardwareId`;
sensor nodes `SensorId` (= `ISensor.Identifier`), `Type` (= `SensorType` name), `RawMin`, `RawValue`, `RawMax`. Levels: computer (`Text` = `Environment.MachineName`),
hardware (nested for sub-hardware), type group (`Text` = "Temperatures", "Load", "Clocks", "Fans", "Powers", "Voltages", "Data", "Throughput", ...), sensor. Hidden sensors are included.

```json
{"id":0,"Text":"Sensor","Min":"Min","Value":"Value","Max":"Max","ImageURL":"","Children":[
 {"id":1,"Text":"GAMING-PC","Min":"","Value":"","Max":"","ImageURL":"images_icon/computer.png","Children":[
  {"id":2,"Text":"AMD Ryzen 9 7900X","Min":"","Value":"","Max":"","HardwareId":"/amdcpu/0","ImageURL":"images_icon/cpu.png","Children":[
   {"id":3,"Text":"Temperatures","Min":"","Value":"","Max":"","ImageURL":"images_icon/temperature.png","Children":[
    {"id":4,"Text":"Core (Tctl/Tdie)","Min":"36.1 °C","Value":"52.3 °C","Max":"81.4 °C","SensorId":"/amdcpu/0/temperature/2","Type":"Temperature",
     "RawMin":"36.1 °C","RawValue":"52.3 °C","RawMax":"81.4 °C","ImageURL":"images/transparent.png","Children":[]}]}]}]}]}
```

(Constructed from the serialiser code, not captured from a live server.)

Parsing gotchas (all from `GenerateJsonForNode` / `SensorNode.ValueToString`):

* `Value`/`Min`/`Max` are **display strings**: `string.Format(sensor.Format, value)` with the *current culture of the LHM machine* (Italian Windows gives `52,3 °C`),
  temperatures turn into `°F` when the GUI is set to Fahrenheit, throughput becomes `KB/s`/`MB/s` or `Mbps`, `TimeSpan` uses the `g` format.
* `RawValue` is documented in code as "unformatted ... consistent readings", but it is still `string.Format(Format, ...)`: it keeps the unit suffix and the host culture
  (decimal comma), it just avoids the Fahrenheit and KB/s conversions.
* Parse robustly: take the leading numeric token, accept `,` or `.` as decimal separator, no thousands separators occur with `F0/F1/F3` formats, `-` means no value.
  Prefer `RawValue`; map the unit via `Type`, not via the suffix.
* For a handful of important sensors, `/Sensor?action=Get&id=...` returns real JSON numbers; for bulk use `data.json` once per second at most, with gzip.
* The agent flavour of the same data is the architecture's `LHM-remote` provider; sensor id `lhm-remote-<name>/<SensorId>`.

---

## 6. NAudio for WASAPI metering

### 6.1 Package verdict: `NAudio.Wasapi 22.0.0`

**Verdict: genuine, published by the real maintainer by mistake, unlisted, superseded. Not a typosquat. But it is still resolvable and `Version="*"` picks it.**

| Evidence | Finding | Source |
|---|---|---|
| Owner | `NAudio`, `NAudio.Core`, `NAudio.Wasapi` (and 22.0.0's own page): single owner **`markheath`**, "Prefix Reserved" badge, authors "Mark Heath", MIT, project `github.com/naudio/NAudio` | [nuget.org/packages/NAudio.Wasapi](https://www.nuget.org/packages/NAudio.Wasapi), [/22.0.0](https://www.nuget.org/packages/NAudio.Wasapi/22.0.0) |
| Status | The 22.0.0 page says "The owner has unlisted this package"; the registration API reports `listed=false` and `published=1900-01-01` (NuGet's marker for unlisted) | [registration5-semver1](https://api.nuget.org/v3/registration5-semver1/naudio.wasapi/index.json) |
| Contents | `lib/netstandard2.0` and `lib/uap10.0.18362` DLLs, depends on `NAudio.Core 2.2.0`, nuspec copyright "© Mark Heath 2022"; DLL `FileVersion 22.0.0.0`, `ProductVersion 22.0`; same file size (179 200 B) as the corrected 2.2.1 | [nuspec](https://api.nuget.org/v3-flatcontainer/naudio.wasapi/22.0.0/naudio.wasapi.nuspec), package inspected locally |
| Cause | `NAudio.Wasapi.csproj` had `<Version>22.0</Version>` in the 2.2 release (22 Aug 2023) instead of `2.2.0`. Issue [#1066 "NAudio.Wasapi has version 22.0.0, not 2.2.0"](https://github.com/naudio/NAudio/issues/1066) (2023-08-25) | git history |
| Fix | Commit [`b5d5ff8` "Fixing #1066 - correct version of NAudio.Wasapi"](https://github.com/naudio/NAudio/commit/b5d5ff83fd378f046398891fe5cd99426ce44732) (2023-09-04); `RELEASE_NOTES.md` 2.2.1: "Incorrectly versioned NAudio.Wasapi.dll (was 22.0) retired and replaced with 2.2.1" | repo |
| Side effect | `NAudio.Wasapi` has **no 2.2.0** at all (2.1.0, then 2.2.1) while `NAudio` / `NAudio.Core` have 2.2.0 | flat container |

Restore experiments (SDK 10.0.112, TFM `net10.0-windows10.0.19041.0`, 2026-10-12):

| `PackageReference Version=` | Resolved |
|---|---|
| `*` | **`NAudio.Wasapi 22.0.0`** + `NAudio.Core 2.2.0` (unlisted versions are still found by floating resolution) |
| `2.*` | 2.4.0 |
| `[2.0.0,)` | 2.0.0 (lowest applicable) |
| `2.2.0` | warning NU1603, resolves 2.2.1 |
| `[2.2.0]` | error NU1102 (no such version) |
| `22.0.0` | resolves |

**Rules:** exact versions only, centrally (`Directory.Packages.props`, `<PackageVersion Include="NAudio.Wasapi" Version="3.1.0" />`); never `*`, never open
ranges; enable lock files (`RestorePackagesWithLockFile`) and `--locked-mode` in CI; tell Dependabot/Renovate to ignore `NAudio*` versions >= 20.
**The task premise "latest official version is 2.2.x" is stale:** on 2026-10-12 NuGet lists stable 2.2.1 (2023-09-04), 2.3.0 (2026-03-12), 2.4.0 (2026-08-26),
3.0.0 (2026-08-15), 3.0.1 (08-18), 3.1.0 (2026-09-07) and pre-release 3.1.1-preview.2 (2026-09-21).

### 6.2 Which line

| | 2.4.0 | 3.1.0 |
|---|---|---|
| Assets used on `net10.0-windows10.0.19041.0` | `lib/netstandard2.0` (the `net9.0-windows10.0.26100` leg is not compatible with a lower OS version; probe 2) | `lib/net9.0` only, "Windows-only at runtime" |
| Device change API | implement `IMMNotificationClient`, `MMDeviceEnumerator.RegisterEndpointNotificationCallback` | `MMDeviceEnumerator.CreateNotificationClient()` and events; the interface and register methods are `internal` ([migration guide](https://github.com/naudio/NAudio/blob/v3.1.0/Docs/MigratingFromNAudio2.md)) |
| Peak / volume API | `MMDevice.AudioMeterInformation`, `.AudioEndpointVolume` | identical names |
| Footprint | `NAudio.Wasapi` + `NAudio.Core` | 467 KB + 349 KB + `System.Numerics.Tensors` 411 KB |
| Maturity | servicing line | NAudio 3 is two months old; 3.0.0 regressions fixed in 3.0.1/3.1.0; "COM objects leaks" issue #1293 closed |

Recommendation: `NAudio.Wasapi` **3.1.0** exact pin (transitively `NAudio.Core 3.1.0`, no other NAudio package), hidden behind a small `IAudioLevelSource`
seam so it can be replaced by CsWin32-generated COM interop if NAudio 3 misbehaves. Both lines compile with the snippets below (probes 1 and 2).

### 6.3 Reading the default render endpoint (3.x; 2.x identical except notifications)

```csharp
using var en  = new MMDeviceEnumerator();
using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);   // Console/Multimedia follow the UI default; Communications can be a separate device
var meter = dev.AudioMeterInformation;
float peakMax = meter.MasterPeakValue;                      // 0..1, max over channels
int   ch      = meter.PeakValues.Count;                     // read once; GetChannelsPeakValues needs the exact count (else E_INVALIDARG)
float left = meter.PeakValues[0], right = ch > 1 ? meter.PeakValues[1] : left;   // NAudio's indexer allocates and pins per call: ok at 60 Hz
var vol = dev.AudioEndpointVolume;
float master = vol.MasterVolumeLevelScalar;                 // 0..1
bool  muted  = vol.Mute;
vol.OnVolumeNotification += d => { /* d.MasterVolume, d.Muted, d.Channels, d.ChannelVolume */ };   // COM thread: copy and return
```

Semantics (Learn: [IAudioMeterInformation](https://learn.microsoft.com/windows/win32/api/endpointvolume/nn-endpointvolume-iaudiometerinformation),
[GetPeakValue](https://learn.microsoft.com/windows/win32/api/endpointvolume/nf-endpointvolume-iaudiometerinformation-getpeakvalue),
[Peak Meters](https://learn.microsoft.com/windows/win32/coreaudio/peak-meters)):

* A **sample peak** (not RMS), normalised 0.0 to 1.0, recorded over one device period (about 10 ms) and available during the next. Sample at 30-60 Hz and build VU/PPM
  ballistics in software (attack/release, FR-MAP-2).
* Measured **before** the endpoint volume control attenuates the stream: the reading does not follow the master slider. To approximate what is heard multiply by
  `MasterVolumeLevelScalar`; whether peaks continue while muted is **UNVERIFIED** (mute belongs to the endpoint volume, so expect yes).
* Exclusive-mode streams bypass the software meter, which then reads 0.0 (a hardware meter still works; `QueryHardwareSupport`, NAudio `HardwareSupport`).
* Sensors to publish (canonical units): `session/audio/peak-l`, `peak-r`, `peak` (0..1 -> `%` or dBFS `dB`), `master-volume` (`%`), `master-mute` (0/1).
  dBFS = `20 * log10(peak)`; clamp at -60 dB.
* Dispose `MMDevice`, enumerators and notification clients explicitly; keep every NAudio object on one dedicated thread (apartment behaviour of the 2.x RCWs is
  **UNVERIFIED**).

### 6.4 Microphone (capture endpoint)

```csharp
using var mic = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);   // fall back to Role.Console if absent
float micPeak = mic.AudioMeterInformation.MasterPeakValue;
bool micMuted = mic.AudioEndpointVolume.Mute;  float micGain = mic.AudioEndpointVolume.MasterVolumeLevelScalar;
```

For a capture device the meter reports the maximum sample of the *input stream*. Whether it moves while **no client has the endpoint open** is **UNVERIFIED**
(the software meter sits in the audio-engine path, so 0 when idle is likely; NAudio's own [level-meter tutorial](https://github.com/naudio/NAudio/blob/v3.1.0/Docs/RecordingLevelMeter.md)
says you must start recording to get levels). Plan B: open a shared-mode capture (`WasapiRecorder` in 3.x, `WasapiCapture` in 2.x), compute peaks from the buffers, discard the audio.
Windows' microphone privacy switch for desktop apps can also silence or deny capture (**UNVERIFIED**; surface the error).

### 6.5 Default-device change notifications

```csharp
// 3.x
using var notif = en.CreateNotificationClient(useSynchronizationContext: false);    // true (default) posts to the captured SynchronizationContext
notif.DefaultDeviceChanged += (_, e) => requestReacquire.Set();                    // e.Flow, e.Role, e.DeviceId ; handler runs on the audio worker thread: never block or dispose audio objects here
notif.DeviceStateChanged   += (_, e) => requestReacquire.Set();                    // e.NewState (unplug, disable)
// 2.x: class C : IMMNotificationClient { OnDefaultDeviceChanged(DataFlow flow, Role role, string id) ... }  +  en.RegisterEndpointNotificationCallback(c)
```

The event typically fires once per role (console, multimedia, communications; **UNVERIFIED** count): debounce ~250 ms, then re-resolve the default device and rebuild the meter/volume objects. Also rebuild when
a call throws `CoreAudioException` (device gone). `PropertyValueChanged` fires on volume changes: leave it unsubscribed.

### 6.6 Session 0 or user session?

* Documented: a WASAPI **loopback** client "in a service running in session 0" can capture the mix of all sessions ([loopback recording](https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording));
  Remote Desktop audio creates extra devices that exist only inside that session.
* **Not documented** (**UNVERIFIED**): that `IAudioMeterInformation` on `GetDefaultAudioEndpoint` from LocalSystem sees the interactive user's default device and live peaks.
  Searches found no authoritative statement either way.
* Decision: **meter in the tray app** (user session) and push a batched `session/audio/*` frame over the session WebSocket (30-60 Hz, only on change). Test the service-side variant once
  on a Windows machine before ever relying on it; if it works it is simpler (no tray dependency for headless use).

---

## 7. Windows media session (GSMTC)

### 7.1 Using it from .NET 10

* `Windows.Media.Control.*` needs **no NuGet package**: with `<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>` the SDK adds the `Microsoft.Windows.SDK.NET.Ref`
  targeting package ([Learn: Call WinRT APIs in desktop apps](https://learn.microsoft.com/windows/apps/desktop/modernize/winrt-apis-desktop-apps); 19041 = Windows 10 2004).
  Verified: all calls below compile on exactly this TFM with no other reference (probe 3).
* The API itself is Windows 10 1809 (RS5, `ms.custom: RS5`). To keep 1809 supported: `<SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion>`.
* Output cost: `Microsoft.Windows.SDK.NET.dll` (24.9 MB) and `WinRT.Runtime.dll` (0.5 MB) are copied next to the tray exe.
* Desktop support: Learn's list of WinRT APIs unsupported in desktop apps does not contain `Windows.Media.Control` (it lists `SystemMediaTransportControls`, a different class).
  The reference pages mention the `globalMediaControl` capability; packaging rules for unpackaged apps are **UNVERIFIED** in docs, but unpackaged WinForms/WPF apps use it widely
  (Raymond Chen's [How can I get information about media playing on the system](https://devblogs.microsoft.com/oldnewthing/20231108-00/?p=108980), community wrapper `Dubya.WindowsMediaController`).

### 7.2 Calls and types (compile-verified)

```csharp
var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();   // keep one instance for the process lifetime
mgr.CurrentSessionChanged += (_, _) => Rebind();  mgr.SessionsChanged += (_, _) => Rebind();
var s = mgr.GetCurrentSession();                  // "the session the system believes the user would most likely want to control"; null if none
// per session:  s.MediaPropertiesChanged / PlaybackInfoChanged / TimelinePropertiesChanged  -> re-query, debounce 150-300 ms
GlobalSystemMediaTransportControlsSessionMediaProperties p = await s.TryGetMediaPropertiesAsync();   // Title, Artist, AlbumTitle, AlbumArtist, Subtitle, TrackNumber, AlbumTrackCount, Genres, PlaybackType, Thumbnail
var pi = s.GetPlaybackInfo();                     // PlaybackStatus, PlaybackRate (double?), PlaybackType, IsShuffleActive, AutoRepeatMode, Controls.*
var tl = s.GetTimelineProperties();               // StartTime, EndTime, MinSeekTime, MaxSeekTime, Position (TimeSpan), LastUpdatedTime (DateTimeOffset, UTC)
string app = s.SourceAppUserModelId;              // identify the player; allow a user filter
```

`PlaybackStatus`: `Closed`=0, `Opened`=1, `Changing`=2, `Stopped`=3, `Playing`=4, `Paused`=5. Sensors to publish: `session/media/title|artist|album` (Text),
`state` (0 stopped, 1 paused, 2 playing), `progress` (0..1), `position` / `duration` (`s`), `art-color` (Color). When `GetCurrentSession()` is null publish state "none" and mark progress offline.
With several players, `GetSessions()` and prefer the one with `Playing` (then `CurrentSession`).

### 7.3 Position while playing

Learn: `Position` is "current as of `LastUpdatedTime`". Extrapolate, re-anchoring on every `TimelinePropertiesChanged`, `PlaybackInfoChanged` and session change:

```csharp
TimeSpan pos = tl.Position;
if (pi.PlaybackStatus == Playing && tl.LastUpdatedTime.Year > 1)                 // default(DateTimeOffset) means "never updated"
    pos += TimeSpan.FromTicks((long)((DateTimeOffset.UtcNow - tl.LastUpdatedTime).Ticks * (pi.PlaybackRate ?? 1.0)));
TimeSpan len = tl.EndTime - tl.StartTime;
double progress = len > TimeSpan.Zero ? Math.Clamp((pos - tl.StartTime) / len, 0, 1) : double.NaN;   // NaN: live stream or app publishes no timeline
```

Players differ: some publish the timeline only on state changes (extrapolation is essential), some never (`EndTime` = 0). Which apps do what is **UNVERIFIED**; treat NaN as "no progress".
Evaluate at the engine rate from cached values; do not call the WinRT getters at 20 Hz.

### 7.4 Thumbnail to dominant colour

```csharp
IRandomAccessStreamReference? r = p.Thumbnail;                 // can be null; may arrive in a later MediaPropertiesChanged
using IRandomAccessStreamWithContentType rs = await r.OpenReadAsync();
BitmapDecoder dec = await BitmapDecoder.CreateAsync(rs);
var tr = new BitmapTransform { ScaledWidth = 32, ScaledHeight = 32, InterpolationMode = BitmapInterpolationMode.Fant };
byte[] bgra = (await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, tr,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();   // 32*32*4 bytes
```

(`AsStreamForRead()` is also available if you prefer `System.Drawing`.) Learn: the parameterless `GetPixelDataAsync()` applies EXIF orientation and colour management to sRGB; the
overload above is the finer-grained one. Dominant colour (engineering recommendation, not from a source): for each pixel with saturation >= 0.25 and value >= 0.2 add weight
`S*V` to one of 24 hue bins, take the heaviest bin and average its pixels, then raise V to >= 0.6 for the backlight; if nothing is colourful use the mean colour or white.
Cache by `(title, artist, SourceAppUserModelId)`; decode only when the thumbnail changed.

### 7.5 Session requirement

GSMTC aggregates sessions of apps in the **interactive user's session**. Nothing in the docs says a session-0 service can see them; it very probably cannot (**UNVERIFIED**, search found no statement).
Run it in the tray app only, and publish through `/api/session`.

---

## 8. User-session signals for the tray app

Optional shortcut: Microsoft.Windows.CsWin32 0.3.360 (source generator, `PrivateAssets=all`; LHM uses it) with `NativeMethods.txt` entries `GetLastInputInfo`, `GetForegroundWindow`,
`GetWindowThreadProcessId`, `OpenProcess`, `QueryFullProcessImageName`, `SHQueryUserNotificationState`, `DwmGetColorizationColor`, `SetWinEventHook`, `WTSRegisterSessionNotification`.

### 8.1 Idle time

```csharp
[StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
[DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
uint idleMs = GetLastInputInfo(ref lii) ? unchecked((uint)Environment.TickCount64 - lii.dwTime) : 0;    // both in the 32-bit GetTickCount domain, wraps after 49.7 days
```

Learn ([GetLastInputInfo](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getlastinputinfo)): **per session**, not system-wide; the tick "is not guaranteed to be incremental"
(can be lower than a previous one: clamp negative or huge deltas to 0); `SendInput` supplies its own tick count (synthetic input resets idle). Gamepad-only play is not keyboard/mouse input and
usually does not reset it (**UNVERIFIED**), so gate "idle -> dim" with the fullscreen/game check in 8.5. Poll at 1 Hz.

### 8.2 Lock / unlock, logon, console/remote switch

* WinForms: `SystemEvents.SessionSwitch += (s, e) => ...; e.Reason` (`SessionSwitchReason`: `ConsoleConnect`=1, `ConsoleDisconnect`=2, `RemoteConnect`=3, `RemoteDisconnect`=4,
  `SessionLogon`=5, `SessionLogoff`=6, `SessionLock`=7, `SessionUnlock`=8, `SessionRemoteControl`=9). Raised on the SystemEvents thread, only while a message pump runs (always true in a WinForms
  tray); static event: unsubscribe on exit ([docs](https://learn.microsoft.com/dotnet/api/microsoft.win32.systemevents.sessionswitch)). `SystemEvents.PowerModeChanged` (`Suspend`=3, `Resume`=1, `StatusChange`=2) is the tray-side power hook.
* Direct: `WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION /*0*/ or NOTIFY_FOR_ALL_SESSIONS /*1*/)` then handle `WM_WTSSESSION_CHANGE` (`0x02B1`); `wParam` has the same codes 1-8,
  `lParam` = session id. Unregister (`WTSUnRegisterSessionNotification`) before the window is destroyed. Right after logon the call can fail with `RPC_S_INVALID_BINDING` until the event
  `Global\TermSrvReadyEvent` is set: retry ([docs](https://learn.microsoft.com/windows/win32/api/wtsapi32/nf-wtsapi32-wtsregistersessionnotification)).
* Initial state at start: assume unlocked (tray starts at logon); `SHQueryUserNotificationState` returns `QUNS_NOT_PRESENT` for screensaver, locked machine or inactive Fast User Switching session (cheap cross-check).
* The service has its own `OnSessionChange` (section 9) for all sessions; the tray report is for the user's session only.

### 8.3 Foreground application

```csharp
IntPtr hwnd = GetForegroundWindow();                       // 0 during switches, on the lock screen, on the UAC secure desktop
GetWindowThreadProcessId(hwnd, out uint pid);
IntPtr hp = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
var sb = new StringBuilder(1024); uint n = (uint)sb.Capacity;
if (hp != IntPtr.Zero && QueryFullProcessImageNameW(hp, 0, sb, ref n)) exe = Path.GetFileName(sb.ToString());   // "game.exe"
```

`QueryFullProcessImageName` accepts a handle with `PROCESS_QUERY_INFORMATION` **or** `PROCESS_QUERY_LIMITED_INFORMATION` ([docs](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew)).
Same-user elevated processes normally open with the limited right from a non-elevated tray; protected/other-user processes fail: fall back to `Process.GetProcessById(pid).ProcessName` (no extension).
UWP apps: the top-level window belongs to `ApplicationFrameHost.exe`; the real process is a child window (class `Windows.UI.Core.CoreWindow`, different PID) found with `EnumChildWindows`
(widely reported, **UNVERIFIED** in Microsoft docs). Event-driven alternative to polling at 1 Hz: `SetWinEventHook(EVENT_SYSTEM_FOREGROUND=0x0003, ..., WINEVENT_OUTOFCONTEXT=0)` on a thread with a message loop,
keep the delegate rooted ([docs](https://learn.microsoft.com/windows/win32/winauto/event-constants)). Profile rules compare the exe name case-insensitively.

### 8.4 Accent colour

* `new Windows.UI.ViewManagement.UISettings().GetColorValue(UIColorType.Accent)` returns a `Windows.UI.Color` (A, R, G, B bytes). `UIColorType`: `Background`=0, `Foreground`=1, `AccentDark3`=2, `AccentDark2`=3,
  `AccentDark1`=4, **`Accent`=5**, `AccentLight1`=6, `AccentLight2`=7, `AccentLight3`=8, `Complement`=9 (not supported, throws). Compile-verified; works from a plain desktop process.
* **Change notification:** Learn's desktop-support page lists `UISettings.ColorValuesChanged` among the events that are **not supported in desktop apps**
  ([WinRT APIs in desktop apps](https://learn.microsoft.com/windows/apps/desktop/modernize/winrt-api-desktop-app-support)). Do not depend on it. Use the documented
  `WM_DWMCOLORIZATIONCOLORCHANGED` (`0x0320`) from a hidden window, plus `WM_SETTINGCHANGE` with lParam `"ImmersiveColorSet"` (common practice, **UNVERIFIED** in docs), debounce 200 ms and re-read
  `UISettings`; or simply poll every 2-5 s.
* DWM alternative: `DwmGetColorizationColor(out uint color, out bool opaque)` returns `0xAARRGGBB` (not `COLORREF`), described as the "colour used for DWM glass composition ... based on the current colour scheme"
  ([docs](https://learn.microsoft.com/windows/win32/api/dwmapi/nf-dwmapi-dwmgetcolorizationcolor)). It is the title-bar colourisation, not guaranteed to equal the Settings accent (automatic accent,
  per-surface options): equality is **UNVERIFIED**. Order: `UISettings.Accent`, then DWM, then (undocumented) `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Accent\AccentColorMenu` (`0xAABBGGRR`).
* Sensor: `session/system/accent` (value kind `Color`).

### 8.5 Fullscreen / game detection (cheap)

`SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE)` ([docs](https://learn.microsoft.com/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state)):
`QUNS_NOT_PRESENT`=1 (screensaver, locked, inactive FUS session), `QUNS_BUSY`=2 (full-screen app or presentation settings), `QUNS_RUNNING_D3D_FULL_SCREEN`=3 (exclusive D3D),
`QUNS_PRESENTATION_MODE`=4, `QUNS_ACCEPTS_NOTIFICATIONS`=5, `QUNS_QUIET_TIME`=6 (first hour of a new profile), `QUNS_APP`=7 (Store app full screen). "Game" = 2 or 3 (and 7 optionally), 1 Hz.
Borderless windowed games reporting `BUSY` is **UNVERIFIED**. Additional signals: an RTSS entry or a PresentMon row for the foreground PID (section 2.4); foreground window rect equal to its monitor
(`MonitorFromWindow` + `GetMonitorInfo`) and not the shell/desktop window; or a user-maintained process list in profile rules.

---

## 9. Windows Service specifics (.NET 10 generic host)

### 9.1 Packages and what `AddWindowsService` does

* `Microsoft.Extensions.Hosting.WindowsServices` 10.0.12 (stable at 2026-10-12; `net10.0` asset depends on `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging.EventLog`,
  `System.ServiceProcess.ServiceController`). `ServiceBase` therefore comes transitively.
* `services.AddWindowsService(o => o.ServiceName = "VuStudio")` (or `UseWindowsService`) does something **only if the parent process is `services.exe`**
  (`WindowsServiceHelpers.IsWindowsService()`): adds the Event Log logger provider (source = application name), registers `WindowsServiceLifetime` as `IHostLifetime`
  and sets `WindowsServiceLifetimeOptions.ServiceName`. In a console it is a no-op, so dev runs, Linux and `--simulate` keep `ConsoleLifetime`.
  Source: [`WindowsServiceLifetimeHostBuilderExtensions.cs`](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/Microsoft.Extensions.Hosting.WindowsServices/src/WindowsServiceLifetimeHostBuilderExtensions.cs).
* `WindowsServiceLifetime` (release/10.0) is `public class ... : ServiceBase, IHostLifetime`, **not sealed**, public 4- and 5-argument constructors, and its constructor only sets
  `CanShutdown = true`. It overrides `OnStart`, `OnStop`, `OnShutdown` (both call `StopApplication()` and wait `HostOptions.ShutdownTimeout`).
  Source: [`WindowsServiceLifetime.cs`](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/Microsoft.Extensions.Hosting.WindowsServices/src/WindowsServiceLifetime.cs).
* A service starts with `%SystemRoot%\System32` as working directory: use absolute paths (`%ProgramData%\VU Studio\...` per architecture.md, `AppContext.BaseDirectory` for the install dir).
  Whether the Web SDK host fixes the content root itself when it detects a service was not checked (**UNVERIFIED**): set it explicitly.

### 9.2 Power and session events: subclass `WindowsServiceLifetime`

`ServiceBase.CanHandlePowerEvent` (accepted control `SERVICE_ACCEPT_POWEREVENT`) and `CanHandleSessionChangeEvent` (`SERVICE_ACCEPT_SESSIONCHANGE`) default to false and **must be set
before the service starts**: the setters throw `InvalidOperationException` once the command properties are frozen. `OnPowerEvent(PowerBroadcastStatus)` and
`OnSessionChange(SessionChangeDescription)` are `protected virtual`. Source: [`ServiceBase.cs`](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.ServiceProcess.ServiceController/src/System/ServiceProcess/ServiceBase.cs).

```csharp
sealed class VuServiceLifetime : WindowsServiceLifetime
{
    private readonly ISystemEventSink _sink;                              // ours: enqueues into a Channel<SystemEvent>
    public VuServiceLifetime(IHostEnvironment env, IHostApplicationLifetime life, ILoggerFactory lf,
                             IOptions<HostOptions> host, IOptions<WindowsServiceLifetimeOptions> svc, ISystemEventSink sink)
        : base(env, life, lf, host, svc)
    {
        _sink = sink;
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;
    }
    protected override bool OnPowerEvent(PowerBroadcastStatus status)       { _sink.Power(status, Stopwatch.GetTimestamp()); return true; }
    protected override void OnSessionChange(SessionChangeDescription d)     { _sink.Session(d.Reason, d.SessionId, Stopwatch.GetTimestamp()); }
}
// Program.cs
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Services.AddWindowsService(o => o.ServiceName = "VuStudio");
    builder.Services.AddSingleton<ISystemEventSink, SystemEventChannel>();
    builder.Services.AddSingleton<IHostLifetime, VuServiceLifetime>();      // last IHostLifetime registration wins over the default one
}
```

Behaviour that matters (all from the `ServiceBase` source and Learn):

* **Asynchronous.** The control handler does `ThreadPool.QueueUserWorkItem(_ => DeferredPowerEvent(...))` / `DeferredSessionChange(...)` and returns `NO_ERROR` immediately. The return value of
  `OnPowerEvent` is ignored and the OS does **not** wait for the handler; events can also run out of order on the pool. Do the minimum, order them yourself (timestamp + a single consumer).
* Power events seen by a service: `Suspend` (PBT 4), `ResumeAutomatic` (18, "delivered every time the system resumes and does not indicate whether a user is present"), `ResumeSuspend` (7, only
  after a `Suspend` was seen), `ResumeCritical` (6, resume without a prior `Suspend`), `PowerStatusChange`, `BatteryLow`, `OemEvent`. `QuerySuspend`/`QuerySuspendFailed` are never sent ("support removed in
  Windows Vista", [PBT_APMQUERYSUSPEND](https://learn.microsoft.com/windows/win32/power/pbt-apmquerysuspend)); you cannot veto sleep. Treat `ResumeAutomatic | ResumeSuspend | ResumeCritical` all as
  "resumed" and make the handler idempotent. Behaviour on Modern Standby laptops is **UNVERIFIED** (the target machine is a desktop).
* On `Suspend`: send one pre-built "night" frame (backlight off, needles to zero) without waiting for the e-ink; assume it may be cut off. On resume: wait 2-5 s for USB re-enumeration, then reconnect
  the hub with backoff, `computer.Reset()` (LHM), reopen HWiNFO view, restart the PresentMon child.
* Session events carry `SessionId` and `Reason` (`SessionChangeReason`: `ConsoleConnect`=1, `ConsoleDisconnect`=2, `RemoteConnect`=3, `RemoteDisconnect`=4, `SessionLogon`=5, `SessionLogoff`=6,
  `SessionLock`=7, `SessionUnlock`=8, `SessionRemoteControl`=9). They arrive for **every** session: compare `SessionId` with `WTSGetActiveConsoleSessionId()`
  ([docs](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-wtsgetactiveconsolesessionid)) before acting (FR-PRO-3 night mode on lock). Keep the tray's `SessionSwitch` as a second path; merge idempotently.
* `CanShutdown` is already true: `OnShutdown` fires at system shutdown; the SCM gives it little time, so the same "one pre-built frame" rule applies.

### 9.3 Installing and removing the service

Commands (a space after every `=` is mandatory; in Windows PowerShell 5.1 write `sc.exe`, because `sc` is an alias of `Set-Content`):

```bat
sc.exe create VuStudio binPath= "C:\Program Files\VU Studio\VuStudio.Server.exe" start= auto obj= LocalSystem DisplayName= "VU Studio"
sc.exe description VuStudio "Drives Streacom VU1 dials and serves the VU Studio UI"
sc.exe failure VuStudio reset= 86400 actions= restart/5000/restart/30000/restart/60000
sc.exe failureflag VuStudio 1
sc.exe start VuStudio
sc.exe stop VuStudio
sc.exe delete VuStudio
:: uninstall = stop + delete; deleting a running service only marks it for deletion until it stops
```

* `create` parameters (`type`, `start= {boot|system|auto|demand|disabled|delayed-auto}`, `obj=`, `binpath=`, `depend=`, `displayname=`): [sc.exe create](https://learn.microsoft.com/windows-server/administration/windows-commands/sc-create).
  `auto` = "starts each time the computer is restarted and runs even if no one logs on". `delayed-auto` postpones the start after boot; not needed for a USB hub.
* Recovery: .NET's own tutorial uses `sc.exe failure "<name>" reset= 0 actions= restart/60000/restart/60000/run/1000` ([Create a Windows Service using BackgroundService](https://learn.microsoft.com/dotnet/core/extensions/windows-service));
  `reset=` is the number of seconds without failures before the count resets (`INFINITE` = never), `actions=` is `restart|reboot|run|` + `/delay-ms`.
  Failure actions run only when the process dies **without reporting `SERVICE_STOPPED`**, or, with `failureflag 1`, also when it stops with a non-zero exit code
  ([SERVICE_FAILURE_ACTIONS_FLAG](https://learn.microsoft.com/windows/win32/api/winsvc/ns-winsvc-service_failure_actions_flag); the flag change "takes effect the next time the system is started").
  Since .NET 6 an unhandled `BackgroundService` exception stops the host *cleanly* (`BackgroundServiceExceptionBehavior.StopHost`), which SCM treats as a normal stop: catch it, set a non-zero `Environment.ExitCode`
  or call `Environment.Exit(1)` so the restart actions fire (same tutorial).
* **Account.** LocalSystem (the default when `obj=` is omitted), as in architecture.md: PawnIO's device is admin-only, ETW sessions for PresentMon, `Global\` objects of HWiNFO, COM port access and `HKLM` all work.
  `LocalService`/`NetworkService` would very likely fail on PawnIO and ETW (**UNVERIFIED**). Compensate for the power of SYSTEM with NFR-4: loopback bind, no script execution in the service (the tray runs scripts),
  ACL `%ProgramData%\VU Studio` to Administrators + SYSTEM. Later hardening candidates, to be tested before use: `sc.exe sidtype VuStudio unrestricted`, `sc.exe privs`.
* **Installer** (roadmap says Inno Setup). The `sc.exe` pattern below is used by many installers, e.g. [ubuntu/adsys `installer/setup.iss`](https://github.com/ubuntu/adsys/blob/32325ebe6ca08fe30eab24112068d8330c31da9c/installer/setup.iss) and
  [aelassas/wexflow `setup/setup-windows-x64.iss`](https://github.com/aelassas/wexflow/blob/6baeec4bb71a5bfa960dbcafdf4ae46ac7139760/setup/setup-windows-x64.iss); `RunOnceId` is used on every `[UninstallRun]` line in those scripts:

```ini
[Run]
Filename: "{sys}\sc.exe"; Parameters: "create VuStudio binPath= ""{app}\VuStudio.Server.exe"" start= auto obj= LocalSystem DisplayName= ""VU Studio"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure VuStudio reset= 86400 actions= restart/5000/restart/30000/restart/60000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failureflag VuStudio 1"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start VuStudio"; Flags: runhidden waituntilterminated
[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop VuStudio"; Flags: runhidden waituntilterminated; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete VuStudio"; Flags: runhidden waituntilterminated; RunOnceId: "DelSvc"
```

  Upgrade path: stop the service and the tray (`taskkill /im VuStudio.Tray.exe`) before copying files, wait until the exe is released, then start again. The Event Log source is created on first write (the service is admin).
  WiX alternative: `<ServiceInstall>` + `<util:ServiceConfig FirstFailureActionType="restart" ... RestartServiceDelayInSeconds ResetPeriodInDays>` (seen in windows_exporter and others; it cannot set the failure *flag*).

---

## 10. Locating the VU1 hub serial port (FTDI FT231X, VID 0x0403, PID 0x6015)

The IDs match [VU-Server](https://github.com/SasaKaranovic/VU-Server) (`dial_driver.py`: `port.vid == 1027 and port.pid == 24597`, via pyserial `comports()`); protocol 115200 8N1 per
[vu1-protocol.md](../vu1-protocol.md). **VID/PID alone is not unique** (every FT231X board uses 0403:6015): treat the filter as "candidates", then confirm with the protocol's hub query on a short timeout,
skip ports that return access-denied (another program, e.g. VU-Server, owns them), and honour the manual override (`hardware.port`).

### 10.1 Windows

Reference algorithm: pyserial [`list_ports_windows.py`](https://github.com/pyserial/pyserial/blob/master/serial/tools/list_ports_windows.py). In order of preference:

**A. SetupAPI, present devices only (primary).** `SetupDiClassGuidsFromName("Ports")` (+ `"Modem"`) -> `SetupDiGetClassDevs(guid, null, 0, DIGCF_PRESENT)` ("only devices currently present") -> per device
`SetupDiOpenDevRegKey(..., DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ)` -> value `PortName`; `SetupDiGetDeviceInstanceId` gives the instance id to match. Ports class GUID `{4D36E978-E325-11CE-BFC1-08002BE10318}`
(well-known; not found in the cloned docs, prefer `SetupDiClassGuidsFromName`).
Ready-made and compile-verified in the package we already reference: `LibreHardwareMonitor.Interop.PowerMonitor.Stm32PortFinder.FindMatchingComPorts(0x0403, 0x6015)` returns `["COM5", ...]` (present Ports-class
devices whose hardware id contains `VID_0403` and `PID_6015`, name parsed from the friendly name `... (COM5)`); the class name is misleading, the logic is generic. Own CsWin32 code is a small alternative. `VuStudio.Hardware` is portable, so keep the Windows locator behind an `IHubLocator` implemented in `VuStudio.Sources.Windows` (or the server project), not in the hardware project.

**B. Registry (no P/Invoke), then intersect with live ports.**

| Driver | Key | Value |
|---|---|---|
| FTDI VCP (`ftdibus.sys` + `ftser2k.sys`) | `HKLM\SYSTEM\CurrentControlSet\Enum\FTDIBUS\VID_0403+PID_6015+<SERIAL>A\0000\Device Parameters` | `PortName` = `COM5` (REG_SZ) |
| Generic USB serial (e.g. `usbser`) | `HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_0403&PID_6015\<instance>\Device Parameters` (composite: `...&MI_00`) | `PortName` |

* The FTDIBUS instance form `VID_0403+PID_6015+<serial>` (note `+`, and the trailing `A` channel letter) is what pyserial parses
  (`VID_([0-9a-f]{4})\+PID_([0-9a-f]{4})(\+(\w+))?`, comment "USB location is hidden by FTDI driver"). A device without serial number gets another child name (`FTDIBUS\COMPORT&VID_0403&PID_6015\...`, **UNVERIFIED**).
  Whether the FT231X's own `Enum\USB\VID_0403&PID_6015\<serial>` node also carries `PortName` with the FTDI driver is **UNVERIFIED**: try both and union the results.
  The second key shape is exactly what LHM's Heatmaster code reads for `VID_10C4&PID_EA60`.
* **Registry entries persist for unplugged devices** (old hubs, other serials, other COM numbers). Always intersect with the ports that exist now:
  `SerialPort.GetPortNames()` on Windows reads `HKLM\HARDWARE\DEVICEMAP\SERIALCOMM` (value data = `COMn`; [source](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.IO.Ports/src/System/IO/Ports/SerialPort.Win32.cs)), which lists
  present serial devices only. Compare case-insensitively. Several matches: sort by (present, last successful port, serial) and probe in that order.

**C. WMI (last resort: needs `System.Management`, slow first call).** `_` is a WQL single-character wildcard, so escape it as `[_]`:

```sql
SELECT DeviceID, Name, PNPDeviceID FROM Win32_SerialPort WHERE PNPDeviceID LIKE '%VID[_]0403%PID[_]6015%'
SELECT Name, PNPDeviceID, Present, Status FROM Win32_PnPEntity
  WHERE ClassGuid = '{4d36e978-e325-11ce-bfc1-08002be10318}' AND PNPDeviceID LIKE '%VID[_]0403%PID[_]6015%'
```

`Win32_SerialPort.DeviceID` is `COMn`; for `Win32_PnPEntity` parse `COMn` out of `Name` ("USB Serial Port (COM5)"). Both classes expose `PNPDeviceID`; `Win32_PnPEntity` also exposes `ClassGuid`, `HardwareID[]`, `Present`
([Win32_PnPEntity](https://learn.microsoft.com/windows/win32/cimwin32prov/win32-pnpentity), [Win32_SerialPort](https://learn.microsoft.com/windows/win32/cimwin32prov/win32-serialport)).
Whether `Win32_PnPEntity` also returns non-present devices is **UNVERIFIED**; filter on `Present`.

**Hot-plug / reconnect.** While disconnected, re-run A or B every 2 s (a registry read is cheap); the COM number can change after replugging into another USB port with the FTDI driver's default
"COM port number" assignment, so never persist only the port name, persist (VID, PID, serial) and resolve per attempt.

### 10.2 Linux

* `SerialPort.GetPortNames()` lists `/dev/ttyUSB*`, `/dev/ttyACM*` from `/sys/class/tty` and `/sys/bus/usb-serial/devices/` ([source](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.IO.Ports/src/System/IO/Ports/SerialPort.Unix.cs)).
* VID/PID from sysfs (pyserial [`list_ports_linux.py`](https://github.com/pyserial/pyserial/blob/master/serial/tools/list_ports_linux.py)): `dev = realpath("/sys/class/tty/ttyUSB0/device")`;
  `subsystem = basename(realpath(dev/subsystem))`; `usb-serial` (ftdi_sio): `iface = dirname(dev)`; `usb` (cdc_acm, `ttyACM*`): `iface = dev`; `usbDevice = dirname(iface)`; then read
  `usbDevice/idVendor` (`0403`), `idProduct` (`6015`), `serial`, `manufacturer`, `product` (hex, per the kernel ABI doc
  [sysfs-bus-usb](https://github.com/torvalds/linux/blob/master/Documentation/ABI/testing/sysfs-bus-usb)). For `ttyUSB0` that is `/sys/class/tty/ttyUSB0/device/../../idVendor`; a robust implementation walks
  parent directories until `idVendor` exists.
* Stable names: udev's `60-serial.rules` ([systemd](https://github.com/systemd/systemd/blob/main/rules.d/60-serial.rules)) creates `/dev/serial/by-id/<bus>-<ID_SERIAL>-if<NN>[-port<P>]` and `/dev/serial/by-path/...`
  for `ttyUSB*|ttyACM*`. The exact hub string (likely `usb-FTDI_FT231X_USB_UART_<serial>-if00-port0`) depends on the EEPROM strings: **UNVERIFIED**; use it only as a hint.
* Permissions: VU-Server's own Linux notes ([Running_from_source_on_linux.md](https://github.com/SasaKaranovic/VU-Server/blob/master/Running_from_source_on_linux.md)) document the `Permission denied: '/dev/ttyUSBx'`
  problem (`chmod 666` workaround); the proper fix is group membership (`dialout`/`uucp`) or a udev rule matching `idVendor=0403`, `idProduct=6015` with `TAG+="uaccess"` or `MODE="0660"` (standard udev, not verified here).

### 10.3 macOS (out of scope for sensors, in scope for the hub)

* `.NET` `GetPortNames()` returns `/dev/tty.*` and `/dev/cu.*`. Use the **`cu`** (callout) node: `/dev/cu.usbserial-*` is the FTDI naming convention (suffix = USB serial number if present); the suffix
  rule is **UNVERIFIED** in Apple documentation. A glob plus protocol probe is enough.
* Exact VID/PID needs IOKit: pyserial matches `IOSerialBSDClient`, reads `IOCalloutDevice`, then the parent `IOUSBHostDevice` (or legacy `IOUSBDevice`) for `idVendor`, `idProduct`, `USB Serial Number`
  ([`list_ports_osx.py`](https://github.com/pyserial/pyserial/blob/master/serial/tools/list_ports_osx.py)). Not worth a native dependency now.

---

## 11. Recommendations and risks

| Topic | Do this | Phase | Main risks and mitigations |
|---|---|---|---|
| LibreHardwareMonitorLib 0.9.6 | Pin 0.9.6 exactly; `VuStudio.Sources.Windows` provider with one update thread; `SensorVisitor` zeroes `ValuesTimeWindow`; `IsCpu/Gpu/Memory/Motherboard/Storage/Network` on, controller/PSU/battery/power-monitor opt-in; `StorageDevice.ThrottleInterval` 30 s; `Reset()` on resume; check `PawnIo.IsInstalled` and show an actionable state | M1 | Driver not installed (CPU temps null): UI prompt + optional `winget` install with consent. Hang after resume: watchdog + recreate. Double polling with HWiNFO: prefer HWiNFO for chips it exposes. History memory: zero the window |
| HWiNFO SHM | Own reader (section 1.4), 1 Hz, stable id `{SensorID:X8}:{Inst}:{ReadingID:X8}`, UTF-8 strings, `dwPollingPeriod`-aware; registry `VSB` as optional fallback | M1 | 12 h limit in free build (show state); header licence unclear (own constants, ask the author); DACL for LocalSystem must be tested |
| RTSS FPS | Own reader of the 15 fields in 2.2, version-gated, foreground app via `dwLastForegroundApp`; host it in the **tray** (publish `session/game/fps`, `frametime`, `app`) | M1 | Per-session namespace (test `Global\` from LocalSystem before moving it); RTSS absent: detect and show "not installed"; stale entries |
| PresentMon | Bundle the MIT console exe; service spawns it with `--output_stdout --session_name VuStudio --stop_existing_session`; parse by column name | M2 (as roadmap) | Schema drift (pin the bundled version, parse case-insensitively, accept `MsBetweenPresents` or `FrameTime`); stuck ETW sessions; CPU cost in all-process mode (filter by process once known) |
| LHM remote web server | HTTP client of `data.json`, 1-2 s poll, gzip, optional Basic auth; ids `lhm-remote-<name>/<SensorId>` | M1 | Culture-formatted strings (parse tolerant, prefer `RawValue`), unstable node `id`, cleartext auth; treat host as unreliable (timeouts) |
| Audio | Tray: NAudio.Wasapi 3.1.0 exact pin behind `IAudioLevelSource`; 30-60 Hz sampling of `MasterPeakValue` + L/R; master volume/mute; mic peak with capture-stream fallback; re-acquire on `DefaultDeviceChanged` | M1 | Floating NAudio version resolves the mis-versioned 22.0.0; exclusive-mode apps read 0; mic idle meter (**UNVERIFIED**); NAudio 3 youth (keep a CsWin32 fallback) |
| Media (GSMTC) | Tray: `Windows.Media.Control` via TFM, extrapolated position, 32x32 dominant colour, events with debounce | M1 | Missing/stale timelines (NaN progress); thumbnails arrive late; services cannot see user sessions (**UNVERIFIED**) |
| Idle / lock / foreground / accent / fullscreen | Tray: `GetLastInputInfo` 1 Hz, `SystemEvents.SessionSwitch`, foreground PID -> exe, `UISettings` accent with `WM_DWMCOLORIZATIONCOLORCHANGED` + polling, `SHQueryUserNotificationState` | M1 | `ColorValuesChanged` unsupported on desktop; gamepad input not counted as activity; UWP foreground host process |
| Service lifetime | `VuServiceLifetime : WindowsServiceLifetime` with power + session events feeding one ordered channel; LocalSystem; `sc.exe` failure actions + `failureflag`; Inno Setup `[Run]`/`[UninstallRun]` | M1 | Events are async and unordered; sleep cannot be vetoed; clean-stop exceptions bypass recovery |
| Hub discovery | `IHubLocator` per OS: Windows SetupAPI (LHM helper or CsWin32) -> registry + `SerialPort.GetPortNames()` -> WMI; Linux sysfs; macOS glob; always confirm with a protocol probe; cache (VID, PID, serial) | M1 | VID/PID shared with other FT231X devices; stale registry entries; Linux permissions |

### 11.1 Roll-up of everything marked UNVERIFIED (test on a real Windows machine, in this order)

1. LocalSystem can open `Global\HWiNFO_SENS_SM2` (and the mutex), and what `Global\RTSSSharedMemoryV2` does from session 0.
2. `IAudioMeterInformation` on the default render and capture endpoints from session 0, and the capture meter with no open stream.
3. GSMTC from a service (expected: no sessions).
4. LHM without PawnIO: which sensors survive; cost of `Open()` and of one `Update` pass; `Reset()` after resume; behaviour of `Computer` when a PawnIO driver is installed but Memory Integrity is on.
5. Accent: `UISettings.Accent` versus `DwmGetColorizationColor`; which messages fire on an accent change.
6. FTDIBUS registry layout for the real hub, `Win32_SerialPort` output, and `by-id` name on Linux.
7. Licences: HWiNFO header terms, RTSS redistribution, PawnIO installer redistribution (write to the authors).
8. Modern Standby behaviour of service power events, if laptops matter.

---

## Appendix A. Sources

* HWiNFO: [qdel/myrsm `hwisenssm2.h`](https://github.com/qdel/myrsm/blob/bb389d86de528a6de3348d047e58fbcee78f8222/hwisenssm2.h) (closest copy to the vendor header),
  [Seraksab reader + snapshot](https://github.com/Seraksab/Hwinfo.SharedMemory.Net/tree/1d09864597f4342e2466880399ad0872892f870a), [hwinfo-streamdeck `layout.go`](https://github.com/moeilijk/hwinfo-streamdeck/blob/5a523fde3cdfef0f25f05dabf1b28fe55e51aca0/internal/hwinfo/layout.go),
  [Rainmeter docs](https://docs.rainmeter.net/tips/hwinfo/), forum threads 7092, 7187, 18 (via search summaries).
* RTSS: [RTSSSharedMemory.h (CapFrameX copy)](https://github.com/CXWorld/CapFrameX/blob/1b5f53da907f0a1de6fde2b75ff8267c79b08b2b/source/CapFrameX.RTSSInterface/Include/RTSSSharedMemory.h),
  [obs-rtss sample by Unwinder](https://github.com/jfoster/obs-rtss/blob/4c87ffa513459bb6d5b2d8c9476baa9fc32466c8/RTSSSharedMemoryInterface.cpp), CapFrameX `RTSSCoreControl.cpp`, DynamicFPSLimiter `rtss_interface.py`,
  [Sandboxie `Templates.ini`](https://github.com/sandboxie-plus/Sandboxie/blob/43b629be6c17628e6298befccfec279774895ba5/Sandboxie/install/Templates.ini).
* PresentMon: [GameTechDev/PresentMon](https://github.com/GameTechDev/PresentMon) (`README*.md`, `LICENSE.txt`, `THIRD_PARTY.txt`, `PresentMon/CommandLine.cpp`, `PresentMon/CsvOutput.cpp`).
* LibreHardwareMonitor: [tag v0.9.6](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6), [NuGet 0.9.6](https://www.nuget.org/packages/LibreHardwareMonitorLib/0.9.6),
  [PawnIO](https://github.com/namazso/PawnIO), [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules), [PawnIO.Setup](https://github.com/namazso/PawnIO.Setup), winget manifest above, Neowin/Slashdot WinRing0 articles.
* NAudio: [naudio/NAudio](https://github.com/naudio/NAudio) (tags v2.4.0, v3.1.0; issue 1066; commit b5d5ff8; `RELEASE_NOTES.md`; `Docs/MigratingFromNAudio2.md`), NuGet pages and API for `NAudio*`.
* Microsoft (read from `MicrosoftDocs/sdk-api`, `winrt-api`, `win32`, `windows-dev-docs`, `windowsserverdocs`, `dotnet/docs`, `dotnet/dotnet-api-docs`, published on learn.microsoft.com), and `dotnet/runtime` release/10.0.
* Hub discovery: pyserial `list_ports_*.py`, VU-Server, Linux `Documentation/ABI/testing/sysfs-bus-usb`, systemd `rules.d/60-serial.rules`.

## Appendix B. What was reproduced locally (2026-10-12, SDK 10.0.112 on Linux with `EnableWindowsTargeting`)

| # | Experiment | Result |
|---|---|---|
| 1 | `net10.0-windows10.0.19041.0` project referencing `LibreHardwareMonitorLib 0.9.6` + `NAudio.Wasapi 3.1.0` + WinRT calls (`Computer`, `SensorVisitor`, `PawnIo.IsInstalled`, `StorageDevice.ThrottleInterval`, `Stm32PortFinder`, `MMDeviceEnumerator`, `CreateNotificationClient`, GSMTC, `BitmapDecoder`, `UISettings`) | builds with 0 warnings; LHM DLLs land in `runtimes/win-*/lib/net10.0/`; `NAudio.Wasapi` asset `lib/net9.0` |
| 2 | Same with `NAudio.Wasapi 2.4.0` and an `IMMNotificationClient` implementation | builds; asset `lib/netstandard2.0` |
| 3 | GSMTC type shapes (`LastUpdatedTime` is `DateTimeOffset`, `PlaybackRate` is `double?`, ...) | compile-verified |
| 4 | `PackageReference Version=` `*`, `2.*`, `[2.0.0,)`, `2.2.0`, `22.0.0`, `[2.2.0]` for `NAudio.Wasapi` | `*` resolves 22.0.0 (table in 6.1) |
| 5 | Struct offsets of `hwisenssm2.h` and `RTSSSharedMemory.h` computed with g++ on x86-64 (natural alignment, `pack(1)` for HWiNFO) | tables in 1.2 and 2.1 / 2.2 |
| 6 | Parsed the HWiNFO v2 snapshot shipped in the Seraksab repo | header 48 B, strides 392 / 460, 25 sensors, 470 readings, poll period 2000 ms |
| 7 | Inspected NuGet packages: `NAudio.Wasapi` 22.0.0 vs 2.2.1 (file versions, sizes, nuspec), `LibreHardwareMonitorLib` 0.9.6 layout and dependencies, licences of dependencies | as reported above |
