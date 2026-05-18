# LEGO SPIKE Prime BLE Protocol — Technical Reference

Reverse-engineered from BLE captures and runtime observation against stock LEGO firmware ("atlantis", FW3) on a SPIKE Prime hub (set 45678). All byte offsets are 0-based. All multi-byte integers are little-endian unless noted.

---

## 1. BLE Transport

### GATT profile

| Role | UUID |
|---|---|
| Service | `0000fd02-0000-1000-8000-00805f9b34fb` |
| Write characteristic (host → hub, WriteWithoutResponse) | `0000fd02-0001-1000-8000-00805f9b34fb` |
| Notify characteristic (hub → host) | `0000fd02-0002-1000-8000-00805f9b34fb` |

### Advertisement

The hub broadcasts two BLE advertisement packet types:
- **ConnectableUndirected** — carries the service UUID above in the ServiceUuids field. This is the reliable way to identify a SPIKE Prime hub.
- **ScanResponse** — carries the hub's local name (e.g. `"Apex"`). This packet is separate and may not be present before scanning.

> **Gap**: The exact relationship and timing between ConnectableUndirected and ScanResponse packets is not fully characterised. Filtering by service UUID is reliable; filtering by hub name requires the ScanResponse to have been received.

### ATT MTU

The BLE ATT MTU is effectively 20 bytes. Outgoing write payloads longer than 20 bytes must be chunked into 20-byte ATT writes. Incoming notifications may similarly arrive as multiple chunks that need to be reassembled before COBS decoding.

---

## 2. Wire Encoding

Every logical message is encoded as follows:

```
pack:   COBS-encode(payload)  →  XOR each byte with 0x03  →  append 0x02
unpack: strip trailing 0x02   →  XOR each byte with 0x03  →  COBS-decode
```

- **Frame terminator**: `0x02`
- **XOR mask**: `0x03` applied to every byte of the COBS-encoded form
- **COBS block limit**: 84 bytes (empirically chosen; hub seems to accept this)

---

## 3. Logical Message Format

After unpacking, each logical message has the form:

```
[type_byte] [payload...]
```

The `type_byte` determines how to interpret the payload. For request/response pairs the hub echoes the type byte back with a flag bit set (see `HubFrame.IsHubPush`).

### Known message types

| Type | Direction | Name | Notes |
|---|---|---|---|
| `0x00` | host → hub | MSG_INFO_REQUEST | Init handshake; sent as `[0x00, 0x00, 0x02]` (raw, pre-packed) |
| `0x01` | hub → host | MSG_INFO_RESPONSE | Hub capabilities reply to init |
| `0x06` | host → hub | MSG_DEVICE_NOTIFICATION_REQUEST | Start sensor streaming; payload = `[0x15, 0x67, 0x00]` |
| `0x07` | host → hub | MSG_QUERY | Firmware/hardware info queries |
| `0x28` | hub → host | MSG_DEVICE_NOTIFICATION_RESPONSE | ACK for streaming start |
| `0x32` | host → hub | MSG_TUNNEL | JSON command envelope (motor, etc.) |
| `0x3C` | hub → host | MSG_DEVICE_NOTIFICATION | Sensor stream frame, ~10 Hz |

> **Gap**: The full set of message types is unknown. The table above covers only the types encountered during development. `MSG_INFO_RESPONSE` payload structure is not decoded — we only use the fact that it arrives as confirmation the hub is ready.

> **Gap**: The streaming interval parameter `[0x67, 0x00]` (= 103 LE) gives approximately 10 frames/second. The unit (ms? some other interval?) is unknown.

---

## 4. MSG_DEVICE_NOTIFICATION (0x3C) Payload

After COBS+XOR unpacking, the logical payload of a `0x3C` frame is:

```
[0x3C] [size_lo] [size_hi] [device_entry...] [device_entry...] ...
```

- Bytes 0: `0x3C` (message type, redundant)
- Bytes 1–2: `payloadSize` (uint16 LE) — number of device-data bytes that follow
- Bytes 3 onwards: concatenated device entries, each starting with a device type byte

### Device entry formats

#### 0x00 — Battery (2 bytes)

```
[0x00] [level]
```

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | byte | type | `0x00` |
| 1 | byte | level | Battery percent 0–100 |

---

#### 0x01 — IMU (21 bytes)

```
[0x01] [faceUp] [?] [yaw i16] [pitch i16] [roll i16] [accX i16] [accY i16] [accZ i16] [? ? ? ? ? ?]
```

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | byte | type | `0x01` |
| 1 | byte | faceUp | Physical face pointing up; maps to one of 6 orientations (see below) |
| 2 | byte | **unknown** | Documented in some captures as "yawFace"; exact meaning unclear |
| 3–4 | int16 | yaw | Degrees |
| 5–6 | int16 | pitch | Degrees |
| 7–8 | int16 | roll | Degrees |
| 9–10 | int16 | accX | Accelerometer X, cm/s² (981 = 1g ≈ 9.81 m/s²) |
| 11–12 | int16 | accY | Accelerometer Y, cm/s² |
| 13–14 | int16 | accZ | Accelerometer Z, cm/s² |
| 15–16 | int16 | gyroX | Gyroscope rate X, degrees per second |
| 17–18 | int16 | gyroY | Gyroscope rate Y, degrees per second |
| 19–20 | int16 | gyroZ | Gyroscope rate Z, degrees per second |

**Orientation values (from LEGO stock firmware Python constants — BLE byte stream not yet empirically verified):**

| faceUp byte | Orientation | Hub surface |
|---|---|---|
| 0 | Top | Side with 5×5 LED matrix |
| 1 | Front | Side with USB port |
| 2 | Right side | Side with ports B, D, F |
| 3 | Bottom | Battery compartment |
| 4 | Back | Side with speaker |
| 5 | Left side | Side with ports A, C, E |

> **Gap**: IMU byte [2] — meaning unclear; may indicate a secondary orientation or gesture state.
> **Gap**: Orientation mapping is derived from stock firmware constants (`hub.TOP=0`, `hub.FRONT=1`, etc.);
> exact correspondence to the BLE wire byte needs empirical confirmation.

---

#### 0x02 — Hub/port configuration (26 bytes)

```
[0x02] [? × 25]
```

Appears at the start of each device-notification frame. Contains port configuration (which sensor types are attached to which port). Internal structure not reverse-engineered. We skip all 26 bytes.

> **Gap**: Full structure unknown. Likely encodes `[type, someHeader, port0type, port0flags, port1type, port1flags, ...]` or similar, but this has not been verified.

---

#### 0x0A — Motor (12 bytes)

```
[0x0A] [portId] [?] [position i16] [power i16] [speed i8] [relativePosition i32]
```

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | byte | type | `0x0A` |
| 1 | byte | portId | 0 = A, 1 = B, … 5 = F |
| 2 | byte | **unknown** | Possibly motor sub-type or mode; always observed as 0 |
| 3–4 | int16 | position | Absolute shaft angle, [0..359]° (modulo, wraps at 360) |
| 5–6 | int16 | power | Motor power, −100..100 % |
| 7 | int8 | speed | Motor speed, −100..100 % |
| 8–11 | int32 | relativePosition | Accumulating encoder count in degrees; can grow past ±360 |

> **Gap**: Motor byte [2] (subType) — unknown. Possibly distinguishes large/medium/small motor variants.
> **Gap**: `relativePosition` exact semantics — does it reset on connect, on a specific command, or never?

---

#### 0x0B — Force sensor (4 bytes)

```
[0x0B] [portId] [pct] [pressed]
```

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | byte | type | `0x0B` |
| 1 | byte | portId | 0 = A … 5 = F |
| 2 | byte | pct | Force as percent 0–100 |
| 3 | byte | pressed | 1 = button pressed, 0 = not pressed |

---

#### 0x0C — Color sensor (10 bytes)

```
[0x0C] [portId] [colorId] [reflect] [red] [green] [blue] [? ? ?]
```

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | byte | type | `0x0C` |
| 1 | byte | portId | 0 = A … 5 = F |
| 2 | byte | colorId | Detected color [0..15]; 255 = no color / too dark |
| 3 | byte | reflect | Reflected light intensity, 0–100 % |
| 4 | byte | red | Red channel, 0–255 |
| 5 | byte | green | Green channel, 0–255 |
| 6 | byte | blue | Blue channel, 0–255 |
| 7–9 | 3 bytes | **unknown** | Purpose not yet determined |

**Known colorId values:** 0–15 represent specific colours; exact colour-to-index mapping not yet catalogued. 255 = no detection.

> **Gap**: Byte layout for reflect/RGB (offsets 3–6) is inferred, not yet empirically confirmed.
> **Gap**: colorId-to-colour mapping table unknown.

---

#### 0x0D — Distance sensor (4 bytes)

```
[0x0D] [portId] [mm i16]
```

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | byte | type | `0x0D` |
| 1 | byte | portId | 0 = A … 5 = F |
| 2–3 | int16 | mm | Distance in mm; −1 = no object detected; valid range ~40–2000 mm |

Derived values (not on wire; compute from mm):
- **cm** = mm / 10 → [4..200]
- **in** = mm / 25.4 → [2..79]
- **pct** = (mm − 40) / (2000 − 40) × 100 → [0..100]

---

#### 0x0E — 3×3 matrix (11 bytes, output device)

An output (display) device attached to a port; not a sensor. Ignored currently. 11 bytes total.

---

## 5. Outgoing Commands

### Init handshake

Sent as raw bytes (not COBS+XOR encoded) immediately after connecting:

```
00 00 02
```

Must be sent before any other command.

### Firmware version query

```
SendRequestAsync(typeByte=0x07, cmdId=0x0D, data=[])
```

Response payload: raw bytes. Exact field layout unknown — we expose `FirmwareVersion.Raw`.

### Hub info query

```
SendRequestAsync(typeByte=0x07, cmdId=0x0C, data=[])
```

Response payload: raw bytes. Exact field layout unknown — we expose `HubInfo.Raw`.

### Start streaming

```
SendRequestAsync(typeByte=0x06, cmdId=0x15, data=[0x67, 0x00])
```

ACK received with type `0x28`. After ACK, `0x3C` notifications start arriving at ~10 Hz. The data parameter `[0x67, 0x00]` encodes the interval; unit and exact semantics unknown.

### Motor control (MSG_TUNNEL JSON)

Motor commands are sent as JSON strings inside a `MSG_TUNNEL` (0x32) envelope, COBS+XOR encoded.

**Start motor:**
```json
{"m":"motor","p":{"port":<0–5>,"speed":<−100..100>}}
```

**Stop motor (coast):**
```json
{"m":"motor","p":{"port":<0–5>,"speed":0,"end_state":1}}
```

Known `end_state` values: `0` = coast, `1` = brake, `2` = hold. Only coast (1) confirmed working.

> **Gap**: Full MSG_TUNNEL command vocabulary unknown. Other commands (LED, display matrix, sound, program execution) likely exist but have not been reverse-engineered for this firmware version.

---

## 6. Port Numbering

| Wire index | Port label |
|---|---|
| 0 | A |
| 1 | B |
| 2 | C |
| 3 | D |
| 4 | E |
| 5 | F |

---

## 7. Known Issues / Observations

- **Distance returns −1** when no object is in range (not zero).
- **Color returns 255** when no color is detected or the sensor is in darkness.
- **Motor position** accumulates past ±360° (odometer-style); it does not wrap.
- **Hub must be power-cycled** if BLE goes into an unreachable state; there is no software reset command known.
- **`encodeRequest` / `decodeFrame`** in Protocol.fs are legacy functions that happen to produce correct bytes for the handful of commands captured during initial reverse-engineering. They are kept for compatibility but should not be used for new commands.
