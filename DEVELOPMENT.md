# Funspike — Development Process

This document summarises the development of the `funspike` F# library and visualiser across nine LLM-assisted coding sessions spanning 2026-05-17 to 2026-05-19.

---

## Session 1 — 2026-05-17 · Protocol research and initial library

**Duration:** Several hours (largest session, ~2.2 MB transcript)

### Research
The session started with research into the LEGO SPIKE Prime hub (set 45678) BLE communication protocol. Initial working assumption was the Nordic UART Service (NUS — `6e400001-...`), but physical BLE capture using the LEGO Education web app revealed the hub actually uses a **proprietary LEGO `fd02` GATT service** with a JSON-RPC-style message protocol framed with COBS+XOR encoding.

The web app capture produced TX/RX byte sequences like:
```
[TX] char=0000fd02-0001... hex: 00 00 02
[RX] char=0000fd02-0002... hex: 54 54 00 07 2c ...
```
This gave enough detail to reverse-engineer the framing layer.

### Project scaffolding
- Created `funspike.slnx` solution targeting **.NET 10 / Windows 11 25H2** using WinRT BLE APIs (`net10.0-windows10.0.26200.0`, later bumped to `26200` and `28000` as new SDKs were installed)
- Two projects: `SpikePrime` (library) and `SpikePrime.Demo` (console)

### Library implementation
| File | Purpose |
|---|---|
| `Protocol.fs` | COBS encoding/decoding, XOR framing, `HubFrame` DU, frame decoder with reassembly |
| `Transport.fs` | WinRT BLE scan + connect, GATT characteristic access, chunked write, notification subscribe |
| `Hub.fs` | Connection lifecycle, request/response correlation via `ConcurrentDictionary`, `Notifications` observable |
| `Devices.fs` | `DeviceSnapshot` type, sensor stream parsing (type byte dispatch), motor/sensor typed readings |

### Connection debugging
Considerable effort went into debugging the initial BLE connection — the hub needed to be in advertising mode (bluetooth button press), there were pairing state issues, and a NUS vs fd02 service confusion had to be resolved. Once connected, the hub was found to send unsolicited streaming notifications rather than requiring per-sensor polling.

### Sensor streaming discovery
The hub sends periodic `0x3C` sensor-stream frames at ~10 Hz. Each frame contains variable-length "device blocks" prefixed by a type byte (`0x02`, `0x07`, `0x09`, `0x0A`, `0x0B`, `0x3C`). The initial parsing left several byte offsets as unknowns.

### Strong typing
Added F# units of measure and discriminated unions:
- `[<Measure>] type mm / deg / pct` 
- `type Port = A | B | C | D | E | F` — replaces `char`
- `MotorReading` with named fields replacing an opaque tuple

### Documentation
Created `README.md` (project overview, structure, run instructions) and `PROTOCOL.md` (canonical byte-level reference for the hub communication protocol, documenting all discovered frame layouts with known/unknown byte annotations).

---

## Session 2 — 2026-05-18 morning · IMU calibration, web visualiser, sensor fixes

**Duration:** Long session (~1.1 MB transcript)

### IMU unit calibration
Physical testing resolved the IMU data units:
- **Accelerometer**: z = 987 with hub flat → confirmed **cm/s²** (1 g = 981 cm/s²), not mg or m/s²
- **Gyroscope**: ±400 during a hard hand rotation → confirmed **degrees/s** (`dps`)
- **Orientation constants**: cross-referenced against LEGO MINDSTORMS Hub API docs — confirmed `TOP=0, FRONT=1, RIGHT=2, BOTTOM=3, BACK=4, LEFT=5`, though this was later empirically revised (see Session 3)
- **Motor fields**: the `absPos` and `Position` fields were named backwards; fixed so `Position : int16<deg>` (bounded 0–359°) and `RelativePosition : int32<deg>` (accumulating encoder)

### Web visualiser architecture decision
Decision was made to build a real-time web visualiser using:
- `SpikePrime.Web` — ASP.NET Core Kestrel server broadcasting hub data over WebSockets
- `SpikePrime.Web.Client` — Fable (F# → JS) frontend using **Oxpecker.Solid** (SolidJS bindings), bundled with Vite

The two processes communicate: server broadcasts JSON snapshots over WebSocket at ~10 Hz, client subscribes and renders reactively.

### Frontend implementation
Built `App.fs` (Fable/Oxpecker.Solid) with:
- Orientation panel, IMU panel, per-port sensor cards, battery indicator
- Raw byte blocks panel — shows every device block with colour-coded known/unknown bytes and hover tooltips for byte names
- Snapshot-driven reactive rendering via SolidJS signals

Encountered and resolved several Oxpecker.Solid / Fable constraints discovered empirically:
- No `let` bindings directly inside HTML computation expression bodies
- No `match` expressions inside CE bodies (plugin desugars them to `let`) — must extract to sub-components
- `input` is a `VoidNode` — used without `{}`
- `:.2f` format specifiers not supported by Fable; use `sprintf "%.2f"`

### Port device type identification
- Found that byte `[2]` of every motor block (`0x0A`) contains the **pybricks/LEGO I/O device type ID** (e.g. `0x30` = Medium Motor, `0x31` = Large Motor)
- Added `IoDeviceType` discriminated union + `parseIoDeviceType` / `ioDeviceTypeName`
- Port cards in the UI show device name subtitles (e.g. "Medium Motor · 45603")
- Ports sorted alphabetically (A → F) in JSON output

### Colour sensor investigation
Initial parsing read R/G/B as single bytes at positions `[4]`, `[5]`, `[6]`. Physical testing (pointing sensors at coloured LEGO bricks) showed results were wrong. Raw block inspection revealed the channels are **10-bit uint16 little-endian pairs** at `[4-5]`, `[6-7]`, `[8-9]`:
- `[5]`, `[7]`, `[9]` are always 0–3 (the high bytes), not independent channel values
- Each channel spans 0–1023

Fixed in `Devices.fs` to use `BitConverter.ToUInt16`; server divides by 4 to produce 0–255 display values.

### Connectivity improvements
- Dual status badges: one for WebSocket to server, one for BLE hub connection
- `hub.Disconnected` observable added to `Transport.fs` via WinRT `ConnectionStatusChanged` event
- Automatic BLE reconnect loop: when hub disconnects, server cancels the delay token, disposes, and re-runs `connectFirstAsync`
- Auto WebSocket reconnect on client: `initWs` reschedules itself 2 seconds after close

### Orientation mapping correction
Physical testing with the hub in all six orientations showed the LEGO firmware docs had `FRONT` and `BACK` swapped. The empirical mapping is `BACK=1, FRONT=4` (opposite to documentation). This was noted in `Devices.fs` with a comment and updated in `PROTOCOL.md`.

### Unknown byte resolved
The one remaining unknown IMU byte (byte `[2]`) was tested across all orientations and rotations — it never changed from `0x00`. Identified as the **yaw reference face**, a static configuration value set by `hub.imu.reset_heading()`, which is never called. Labelled `yawRef?` in the raw bytes panel.

### Generated file hygiene
Added entries to `.gitignore` for:
- `SpikePrime.Web.Client/fable_modules/` (Fable JS module copies)
- `SpikePrime.Web.Client/src/*.jsx` (Fable-transpiled output)
- `SpikePrime.Web/wwwroot/` (Vite production build output)

Tracked generated files already committed were removed from the git index with `git rm --cached`.

---

## Session 3 — 2026-05-18 afternoon · Visualiser layout polish and display parsing

### LED matrix decoding
The raw blocks panel revealed that the `0x02` block is the **5×5 LED matrix display**: type byte + 25 pixel brightness values (`0x00` = off, `0x64` = 100% bright). This was confirmed by looking at a heart pattern displayed on the hub matching the exact byte values. The block was previously filtered out as a "config block" and added to the panel with full annotation.

### Layout improvements
- Split battery and matrix display into separate panels alongside the orientation panel
- IMU readings compacted from verbose labels into a two-table layout:
  - **Table 1**: Tilt angle — columns Yaw / Pitch / Roll
  - **Table 2**: Accelerometer + Gyroscope — columns X / Y / Z
  - Units shown once in row headers (yellow, in brackets), not repeated per cell

### Scale fixes
- **Tilt angles** were in decidegrees (×10 too large): added `/ 10` at parse time in `Devices.fs`
- **Gyro** confirmed already in the correct dps scale
- **Accelerometer** confirmed correct scale (981 cm/s² at rest = 1G)
- **Distance sensor**: when sensor returns −1 (out of range), display shows `> 2000 mm` instead of a meaningless negative number

---

## Session 4 — 2026-05-18 evening · Documentation accuracy

### TODO.md created
Catalogued remaining unknowns in `TODO.md`:
- Centre button pressed state + LED colour control
- Left/right button pressed states
- Bluetooth button state + LED
- Speaker (tone command protocol)
- USB port / charging state detection
- Motor control commands (protocol known but not yet implemented)

### PROTOCOL.md and TODO.md corrections
Confirmed and documented that:
- Colour sensor RGB channels are `uint16` 10-bit ADC pairs (`[4-5]`, `[6-7]`, `[8-9]`)
- Motor byte `[2]` is the I/O device type ID (not an unknown)
- Both items ticked in `TODO.md`

---

## Session 5 — 2026-05-18 evening · Code quality review

### Mutable variable assessment
A review of all mutable variables in the codebase assessed whether functional alternatives were appropriate:
- `Protocol.fs` COBS encode/decode — mutable `codeIdx`/`i` are correct; `Array.unfold` cannot handle COBS encode because of its **back-patching** requirement (the code byte placeholder is written before its value is known)
- `Devices.fs` binary parsing `offset` variables — idiomatic for variable-width binary parsing
- Others assessed as genuinely necessary for their context

### Thread safety fix
`snapCount` in the server was incremented with `snapCount <- snapCount + 1` (non-atomic read-modify-write). Fixed with `Interlocked.Increment`, which atomically increments and returns the new value, eliminating the read-after-write race.

---

## Session 6 — 2026-05-18 evening · Button press investigation (planning)

Planned the approach to detecting whether the hub sends unsolicited notifications for button presses:
- All non-`0x3C` frames from `hub.Notifications` are currently silently discarded
- Added a filtered raw frame logger to `SpikePrime.Demo` to print every unsolicited frame that is **not** the sensor stream — so that pressing the centre/left/right buttons could be observed

This session was brief and primarily planning/research oriented.

---

## Session 7 — 2026-05-18 evening · Motor control implementation (attempted)

**Duration:** Long session (~369 KB transcript)

### Architecture review
Comprehensive architecture walkthrough produced a full data flow diagram:

```
SPIKE Prime Hub (BLE)
       ↕  GATT fd02 service
  Transport.fs  — WinRT BLE scanning, pairing, GATT
       ↕  raw byte chunks (20-byte ATT MTU, reassembled)
  Protocol.fs   — COBS+XOR framing, FrameDecoder
       ↕  HubFrame
  Hub.fs        — request/response correlation
       ↕  DeviceSnapshot
  Devices.fs    — sensor parsing, hub API
       ↕  JSON over WebSocket
  SpikePrime.Web — ASP.NET Kestrel + WS broadcast
       ↕  WebSocket
  SpikePrime.Web.Client — Fable/Solid visualiser
```

### Motor control design
Decided to extend the existing WebSocket **bidirectionally** (server already stored all client `WebSocket` objects; just needed a read loop). Commands arrive as JSON from the browser:
```json
{ "cmd": "motorStart", "port": "E", "speed": 50 }
{ "cmd": "motorStop",  "port": "E" }
```

### Implementation
- Server: `activeHub` module-level mutable, `parsePort`, `handleCommandAsync`, WS read loop dispatching text messages
- Client: `MotorControls` component — speed slider (−100 to 100), Start button, Stop button
- Slider interaction refined: `onInput` updates local SolidJS signal (keeps slider in sync), Start/Stop buttons send the actual BLE command — avoids flooding BLE with one command per pixel of drag
- Fixed `WebSocketException` crash when browser closes/reloads by wrapping WS read loop in try-catch

### Note
The motor control feature was subsequently **reverted in Session 8** due to the motor control commands causing the hub to crash. The `motorStartAsync`, `motorStopAsync`, and `SendMessageAsync` were removed from the codebase. The investigation concluded that the motor command encoding in `MSG_TUNNEL` needs further reverse-engineering before it can be safely used.

---

## Session 8 — 2026-05-19 morning · Motor control removal, README overhaul, RGB source fix

### Motor control removal
- Removed `motorStartAsync`, `motorStopAsync` from `Devices.fs`
- Removed `portIndex` helper (only used by those functions)
- Removed `SendMessageAsync` from `Hub.fs`
- Removed motor control UI (`MotorControls` component, CSS) from `App.fs`
- Updated README to remove all motor control references

### README overhaul
- Added favicon (SPIKE display image) to both dev and production HTML
- Project structure updated to include web projects
- Architecture diagram updated
- Demo section reframed as **"Console smoke-test"** — a lightweight connection verifier and code example, not a standalone demo
- Building section removed (implicit in `dotnet run`)
- Added screenshot of raw device inputs panel

### RGB scaling moved to source
The `/4` scaling (uint16 0–1023 → byte 0–255) was being applied separately in both `Web/Program.fs` and `Demo/Program.fs`, leading to a bug where the demo was printing the wrong values. Moved the scaling into `Devices.fs` at parse time:
- `ColorReading.Red/Green/Blue` changed from `uint16` to `byte`
- Applied `/ 4us |> byte` at the parsing site
- Both callsites simplified (no more explicit `/4`)

This follows the same pattern as tilt angle scaling (decidegrees → degrees done in `Devices.fs`, not at callsites).

---

## Session 9 — 2026-05-19 afternoon · Documentation accuracy pass

Fixed inaccuracies in `README.md` and `PROTOCOL.md` identified by a thorough codebase review:

| Document | Issue | Fix |
|---|---|---|
| `README.md` | Listed `SendMessageAsync` in Hub API table (method was removed in Session 8) | Removed |
| `PROTOCOL.md` | IMU orientation table showed `FRONT=1, BACK=4` (LEGO docs) | Corrected to empirical `BACK=1, FRONT=4` with note |
| `PROTOCOL.md` | Other stale entries from earlier in development | Corrected |

---

## Cumulative outcomes

### What was built
| Component | Description |
|---|---|
| `SpikePrime` library | F# .NET 10 library connecting to SPIKE Prime over BLE (WinRT), parsing the full sensor stream |
| `SpikePrime.Demo` | Console smoke-test app: connects, prints 10 snapshots of all sensor readings |
| `SpikePrime.Web` | ASP.NET Core server: BLE background service + WebSocket broadcast |
| `SpikePrime.Web.Client` | Fable/Oxpecker.Solid/Vite frontend: real-time sensor visualiser |

### Protocol knowledge gained
Through a combination of BLE traffic capture, open-source cross-referencing (LEGO MINDSTORMS Hub API, pybricks assigned-numbers), and physical empirical testing, the following was fully reverse-engineered:

| Block type | Contents | Status |
|---|---|---|
| `0x02` (26 B) | 5×5 LED matrix: 25 pixel brightness bytes | Fully decoded |
| `0x07` (10 B) | Colour sensor: colorId, reflectivity, R/G/B (uint16 LE, 10-bit ADC) | Fully decoded |
| `0x09` (7 B) | Distance/force sensor | Fully decoded |
| `0x0A` (12 B) | Motor: type ID, position, relPos, speed, power | Fully decoded |
| `0x0B` (2 B) | Battery level (%) | Fully decoded |
| `0x3C` (21 B) | IMU: face-up orientation, yaw ref (static), yaw/pitch/roll (decideg), acc (cm/s²), gyro (dps) | Fully decoded |

### Remaining unknowns (documented in TODO.md)
- Button states (centre, left, right, bluetooth) — not in the streaming data
- Speaker tone commands — protocol unknown
- USB charging state — not in the streaming notification data
- Motor control via `MSG_TUNNEL` — protocol partially understood but causes hub crashes; needs further investigation
