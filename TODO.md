# FunSpike — To-Do List

## Hub hardware — reverse-engineering

### Centre button
- [ ] Determine whether button-pressed state is pushed by the hub (new message type?) or must be polled
- [ ] Find the MSG_TUNNEL (or other) command to set the centre button LED colour (green / white / red / orange)
- [ ] Expose button state in `DeviceSnapshot` once the protocol is known
- [ ] Add `setCentreButtonColourAsync` API in `Devices.fs`

### Left / right buttons
- [ ] Determine whether pressed states are pushed or must be polled
- [ ] Expose left/right button states in `DeviceSnapshot`

### Bluetooth button
- [ ] Determine whether the pressed state is exposed over BLE at all
- [ ] Find the command (if any) to control the BT button indicator LED (blue only?)
- [ ] Expose in API

### Speaker
- [ ] Capture a BLE trace of a Scratch program playing a tone and identify the MSG_TUNNEL payload
- [ ] Determine the command vocabulary: frequency, duration, volume, pre-set sounds
- [ ] Add `playSoundAsync`, `playToneAsync` API in `Devices.fs`

### USB charging port
- [ ] Determine whether charging status (charging / full / not connected) is accessible over BLE
- [ ] If so, expose as a field in `DeviceSnapshot`

---

## Protocol gaps — empirical confirmation needed

- [ ] **Streaming interval**: confirm what `[0x67, 0x00]` encodes (milliseconds? ticks?); experiment with other values to change frame rate
- [x] **IMU orientation mapping**: physically tested — empirically confirmed as TOP=0, BACK=1, RIGHT=2, BOTTOM=3, FRONT=4, LEFT=5; note FRONT/BACK are swapped vs the LEGO Python firmware constants; `PROTOCOL.md` table and `Devices.fs` updated accordingly
- [ ] **IMU byte[2] (yawRef)**: call `hub.imu.reset_heading()` from a MicroPython script and observe whether byte[2] changes to confirm the hypothesis
- [x] **Motor byte[2] (ioDeviceType)**: confirmed as LEGO I/O device type ID; values mapped to `IoDeviceType` DU in `Devices.parseIoDeviceType`
- [ ] **`relativePosition` reset**: determine whether the accumulator resets on BLE connect, on a specific command, or never; document and expose a reset command if one exists
- [x] **Color sensor byte layout**: confirmed — R/G/B are uint16 (10-bit ADC, 0–1023) at offsets 4–5, 6–7, 8–9; no unknown bytes remain
- [ ] **ColorId mapping**: enumerate all 16 colour IDs by presenting each LEGO colour brick to the sensor; build a lookup table (id → colour name + swatch) in `Devices.fs`
- [ ] **FirmwareVersion / HubInfo payloads**: decode the raw bytes from a known firmware into named fields; update `getFirmwareVersionAsync` and `getHubInfoAsync` to return typed records
- [ ] **Motor `end_state`**: confirm values 0 (coast), 1 (brake), 2 (hold) by testing each; update `motorStopAsync` to accept an `EndState` DU parameter
- [ ] **Matrix display intermediate brightness**: test pixel values 1–99 to confirm the hub renders them at fractional brightness (vs. snapping to 0/100)
- [ ] **Unsolicited message types**: capture a full BLE session (including button presses and speaker output) and catalogue any message types not yet in the protocol table

---

## Hub control — MSG_TUNNEL vocabulary

- [ ] **Write to 5×5 LED matrix**: reverse-engineer the command to push a custom 25-pixel image; add `setMatrixDisplayAsync (pixels: byte[]) hub` API (pixels 0–100 each)
- [ ] **Centre button LED**: see *Centre button* above
- [ ] **Speaker / beep**: see *Speaker* above
- [ ] **Program control**: determine whether there are commands to start/stop/list hub-stored programs over BLE
- [ ] **Reset heading / IMU zeroing**: find the command equivalent to `hub.imu.reset_heading()`

---

## F# API improvements

- [ ] **`motorStopAsync` end state**: accept `Coast | Brake | Hold` DU instead of hard-coding coast
- [ ] **`motorGoToPositionAsync`**: if a MSG_TUNNEL command exists for absolute position moves, wrap it
- [ ] **Hub button / LED APIs**: add once protocol is confirmed (see sections above)
- [ ] **Typed `FirmwareVersion` / `HubInfo`**: replace raw-byte records with decoded typed fields
- [ ] **ColorId DU**: add a `KnownColor` type (Red | Blue | Yellow | … | Unknown of byte) once the mapping is confirmed

---

## .llsp3 file parsing

- [ ] **Research the format**: `.llsp3` files are renamed ZIP archives containing a Scratch 3 JSON project; confirm structure by unzipping a sample
- [ ] **Understand the JSON schema**: identify the Scratch block types used by SPIKE Prime (motor, sensor, display, sound, control-flow, etc.)
- [ ] **Map Scratch blocks to F# concepts**: decide on a target AST or directly emit F# source
- [ ] **Implement the parser**: write an F# module (`Llsp3.fs` or similar) to deserialize the JSON and extract the program structure
- [ ] **Implement the code generator**: emit idiomatic F# using the `SpikePrime.Devices` API
- [ ] **Test**: use a known Scratch program (e.g. "drive forward 2 seconds, turn 90°") as a round-trip test case

---

## Visualiser (SpikePrime.Web + SpikePrime.Web.Client)

- [ ] **Hub controls panel**: add UI to send motor commands (speed slider, stop button) per port
- [ ] **Centre button LED picker**: colour picker → `setCentreButtonColourAsync` (once API exists)
- [ ] **Matrix display editor**: 5×5 grid of brightness sliders / pixel picker → `setMatrixDisplayAsync`
- [ ] **Speaker widget**: frequency + duration input → `playSoundAsync`
- [ ] **Button state indicators**: show live state of centre, left, right, BT buttons (once streamed)
- [ ] **ColorId legend**: show a colour swatch next to the colour sensor colorId once the mapping is known

---

## Misc / housekeeping

- [ ] **Multiple hub support**: `Hub` is a single-connection wrapper; consider a registry/pool if connecting to more than one hub simultaneously
- [ ] **Hub name configuration**: surface the hub name filter in the Web UI rather than hard-coding `"Apex"` in `Program.fs`
- [x] **Reconnection logic**: `HubStreamService` reconnects automatically via a `while` loop; `hub.Disconnected` cancels the inner wait to trigger an immediate retry — add exponential back-off if needed
- [ ] **ATT MTU negotiation**: `Transport.fs` hard-codes 20-byte chunks; investigate requesting a larger MTU to improve throughput
- [ ] **Streaming rate control**: allow the client (Web UI) to change the `[0x67, 0x00]` interval parameter at runtime
