# VU1 hub serial protocol (reference)

This is the protocol between a computer and the Streacom/Karanović **VU1 hub**. We derived it by
studying the open-source [VU-Server](https://github.com/SasaKaranovic/VU-Server) (`dial_driver.py`,
`serial_driver.py`, `dials/Comms_Hub_Server.py`) and its tests. VU Studio reimplements it from scratch
in `src/VuStudio.Hardware`.

## 1. Physical link

* USB serial via FTDI FT231X: **VID `0x0403`, PID `0x6015`**. On Windows it is a COM port; on Linux `/dev/ttyUSB*`.
* **115200 baud, 8 data bits, no parity, 1 stop bit**, no flow control.
* Only one process can own the port. VU-Server must be stopped before VU Studio can connect.

## 2. Framing

All traffic is **ASCII text**. Every byte travels as two uppercase hex characters.

### Request (host → hub)

```
'>' CC TT LLLL DATA... '\r\n'
```

| Field | Size | Meaning |
|---|---|---|
| `>` | 1 char | start of request |
| `CC` | 2 hex | command code (§4) |
| `TT` | 2 hex | data type (§3) |
| `LLLL` | 4 hex | payload length **in bytes** (big-endian) |
| `DATA` | 2 hex per byte | payload |
| `\r\n` | 2 chars | terminator |

Examples:
* `>0C010000` — rescan bus, no payload.
* `>030400020132` — set dial #1 to 50 %: type 0x04, 2 bytes `01 32`.

### Response (hub → host)

The hub answers each request with **one line starting with `<`**. Before that line it may print
other lines; ignore those.

```
'<' CC TT LLLL DATA... '\r\n'
```

* `CC` echoes the request's command code. A `<` line whose `CC` differs belongs to another
  (earlier) command and must be skipped.
* `TT` = `05` (STATUS_CODE): `DATA` is a **4-byte big-endian status**. `00000000` means OK, anything
  else is an error (§5). Example: `<0305000400000000` → percent-set acknowledged.
* Otherwise `DATA` is the command's payload as hex. Treat `LLLL` as advisory and take the payload
  as everything after the 9-character header.

### Transaction rules (proven by VU-Server)

* **One outstanding command at a time.** Write the request, then read lines until a `<` line with
  the matching `CC` arrives, or a timeout expires.
* Before writing, **drain and discard** anything already in the receive buffer. Stale replies must
  never be matched to the new command.
* Typical latency is ~15–20 ms per command. Suggested timeouts:
  * 500 ms for value and backlight sets,
  * 2–5 s for everything else (rescan, image chunks).
* A timeout means the dial or hub did not answer. Retry with exponential backoff (1 s → 30 s). After
  5 consecutive failures, mark the dial unresponsive until the next rescan.

## 3. Data types (`TT`)

| Code | Name |
|---|---|
| `0x01` | NONE |
| `0x02` | SINGLE_VALUE |
| `0x03` | MULTIPLE_VALUE |
| `0x04` | KEY_VALUE_PAIR |
| `0x05` | STATUS_CODE (responses) |

The hub does not seem to validate `TT` strictly. Use the type shown in the command table, to
match VU-Server's behaviour.

## 4. Commands

`idx` is the dial's **bus index**, one byte (0–99). Multi-byte integers are **big-endian**.

| Code | Name | Type | Payload | Response |
|---|---|---|---|---|
| `0x01` | SET_DIAL_RAW_SINGLE | KV | `idx, hi, lo` (raw DAC value) | status |
| `0x02` | SET_DIAL_RAW_MULTIPLE | KV | `idx, hi, lo` × n | status |
| `0x03` | SET_DIAL_PERC_SINGLE | KV | `idx, percent` (0–100) | status |
| `0x04` | SET_DIAL_PERC_MULTIPLE | KV | `idx, percent` × n | status |
| `0x05` | SET_DIAL_CALIBRATE_MAX | KV | `idx, u32 value` (raw value for 100 %) | status |
| `0x06` | SET_DIAL_CALIBRATE_HALF | KV | `idx, u32 value` (raw value for 50 %) | status |
| `0x07` | GET_DEVICES_MAP | NONE | — | hex string, one byte per bus slot: `01` = present |
| `0x08` | PROVISION_DEVICE | NONE | — | status (assigns addresses to new dials) |
| `0x09` | RESET_ALL_DEVICES | NONE | — | status |
| `0x0A` | DIAL_POWER | SINGLE | `0` off / `1` on | status |
| `0x0B` | GET_DEVICE_UID | SINGLE | `idx` | 12-byte UID as 24 hex chars (used verbatim as the dial id) |
| `0x0C` | RESCAN_BUS | NONE | — | status |
| `0x0D` | DISPLAY_CLEAR | SINGLE | `idx, color` (`0` = white, `1` = black) | status |
| `0x0E` | DISPLAY_GOTO_XY | SINGLE | `idx, x u16, y u16` | status |
| `0x0F` | DISPLAY_IMG_DATA | SINGLE | `idx, bytes…` (≤ 1000 image bytes per frame) | status |
| `0x10` | DISPLAY_SHOW_IMG | SINGLE | `idx` | status (starts the e-paper refresh) |
| `0x11` | RX_BUFFER_SIZE | SINGLE | `idx` | first 4 bytes = u32 buffer size |
| `0x12` | RESET_CFG | — | — | status (not used) |
| `0x13` | SET_RGB_BACKLIGHT | MULTIPLE | `idx, r, g, b, w` (each 0–100) | status |
| `0x14` | SET_DIAL_EASING_STEP | SINGLE | `idx, u32 step` (% per period) | status |
| `0x15` | SET_DIAL_EASING_PERIOD | SINGLE | `idx, u32 period` (ms) | status |
| `0x16` | SET_BACKLIGHT_EASING_STEP | SINGLE | `idx, u32 step` | status |
| `0x17` | SET_BACKLIGHT_EASING_PERIOD | SINGLE | `idx, u32 period` | status |
| `0x18` | GET_EASING_CONFIG | SINGLE | `idx` | 16 bytes: dial step, dial period, backlight step, backlight period (u32 each) |
| `0x19` | GET_BUILD_INFO | SINGLE | `idx` | ASCII (hex-encoded) firmware build hash |
| `0x20` | GET_FW_INFO | SINGLE | `idx` | ASCII firmware version |
| `0x21` | GET_HW_INFO | SINGLE | `idx` | ASCII hardware version |
| `0x22` | GET_PROTOCOL_INFO | SINGLE | `idx` | ASCII protocol version |
| `0xF3` | DEBUG_I2C_SCAN | NONE | — | debug output |
| `0xF4`–`0xFD` | bootloader / firmware update | — | — | **not used by VU Studio** |

Note the code gap: after `0x19` the codes continue at `0x20`. Bootloader commands are deliberately
unsupported.

## 5. Status codes (u32)

| Code | Meaning | Code | Meaning |
|---|---|---|---|
| `0x0000` | OK | `0x000D` | NOT_IMPLEMENTED |
| `0x0001` | FAIL | `0x000E` | MALFORMED_PACKAGE |
| `0x0002` | BUSY | `0x0010` | RECURSIVE_CALL |
| `0x0003` | TIMEOUT | `0x0011` | DATA_MISMATCH |
| `0x0004` | BAD_DATA | `0x0012` | DEVICE_OFFLINE |
| `0x0005` | PROTOCOL_ERROR | `0x0013` | MODULE_NOT_INIT |
| `0x0006` | NO_MEMORY | `0x0014` | I2C_ERROR |
| `0x0007` | INVALID_ARGUMENT | `0x0015` | USART_ERROR |
| `0x0008` | BAD_ADDRESS | `0x0016` | SPI_ERROR |
| `0x0009` | FORBIDDEN | `0xE001` | BTL_NO_DEVICE |
| `0x000B` | ALREADY_EXISTS | `0xE002` | BTL_INVALID_STATE |
| `0x000C` | UNSUPPORTED | `0xE003` | BTL_INVALID_REQUEST |

## 6. Start-up sequence

1. Open the port and flush both buffers.
2. `RESCAN_BUS` (0x0C), then `GET_DEVICES_MAP` (0x07). Every `01` byte at position *i* means
   bus index *i* is present.
3. For each present index: `GET_DEVICE_UID` (0x0B). Optionally read FW/HW/protocol info and the easing
   config.
4. If no dial is present, send `PROVISION_DEVICE` (0x08) up to 3 times (200 ms apart), then rescan.
5. Apply the stored easing config. Set every needle to 0 and every backlight to the configured state.

Bus indices can change after a rescan. **Always key dials by UID** and map to the index only
when sending.

## 7. E-paper image upload

The display is **200 × 144, 1 bit per pixel**. Encoding:

* Scan **column by column**, x = 0 … 199. In each column take pixels top to bottom (y = 0 … 143),
  8 at a time, each group forming one byte, **MSB = topmost pixel**.
* Bit value **1 = white** (grey > 127) and **0 = black**.
* Each column is 18 bytes. The total is **3600 bytes**.

Upload procedure for one dial:

1. `DISPLAY_CLEAR` (`idx, 0`) — white background.
2. `DISPLAY_GOTO_XY` (`idx, 0, 0`).
3. `DISPLAY_IMG_DATA` frames of at most **1000 bytes** (`idx` + data). VU-Server waits ~200 ms between
   frames. Use that gap to send pending needle/backlight commands to other dials.
4. `DISPLAY_SHOW_IMG` (`idx`) — triggers the e-paper refresh. The refresh takes a few seconds and
   flashes the panel.

Skip the upload when the new bitmap is identical to the last one sent (compare hashes).
Rate-limit dynamic faces to protect the panel.

## 8. Needle and backlight semantics

* Percent-set positions the needle using the dial's internal calibration. On-device easing moves the
  needle toward the target by `step` % every `period` ms. VU-Server's defaults are:
  * needle: step 2, period 50 → 40 %/s,
  * backlight: step 5, period 100 → 50 %/s.
* Calibration stores the raw DAC values for the 100 % and 50 % points inside the dial.
  `SET_DIAL_RAW_SINGLE` moves the needle by raw value while the user tunes the calibration.
* Backlight channels are **0–100 each**: red, green, blue and a separate **white** LED.

## 9. Face geometry

Measured from the stock faces (see [requirements](requirements.md#3-vu1-hardware-facts)):

* pivot at **(99.5, 225.5)** in display pixels, i.e. 81.5 px below the bottom edge, centred;
* 0 % = −40°, 50 % = 0°, 100 % = +40° (degrees from vertical, clockwise positive);
* stock ticks run from radius ≈ 137 to 152.5 px; labels sit around radius 120–128 px.

VU Studio keeps these as configurable defaults (`NeedleGeometry`) in case some units differ.
