# funspike

A purely F# library for connecting to and reading sensors from a **LEGO SPIKE Prime** hub over Bluetooth LE on Windows — no Python, no C# wrappers.

## How it works

The SPIKE Prime hub runs stock LEGO firmware ("atlantis", FW3) and exposes a proprietary GATT service. Frames are COBS-encoded, XOR-obfuscated with `0x03`, and terminated with `0x02`.

| GATT | UUID |
|---|---|
| Service | `0000fd02-0000-1000-8000-00805f9b34fb` |
| Write (host → hub, WriteWithoutResponse) | `0000fd02-0001-1000-8000-00805f9b34fb` |
| Notify (hub → host) | `0000fd02-0002-1000-8000-00805f9b34fb` |

The hub is identified during BLE scanning by the presence of this service UUID in its `ConnectableUndirected` advertisement packets (the user-visible hub name appears in separate `ScanResponse` packets and is not used for matching).

## Requirements

- Windows 11 (10.0.26100 or later)
- .NET SDK 10.0.300+
- Bluetooth LE adapter
- LEGO SPIKE Prime hub (45678) with stock firmware, powered on and in range
- Node.js (for the web visualiser only)

## Project structure

```
funspike/
├── funspike.slnx
├── SpikePrime/               # Library (net10.0-windows10.0.26100.0)
│   ├── Protocol.fs           # COBS+XOR codec, HubFrame, FrameDecoder, packMessage, encodeTunnelCommand
│   ├── Transport.fs          # BLE scanner, HubConnection, scanAndConnectAsync
│   ├── Hub.fs                # Hub type, connectFirstAsync, InitAsync, SendRequestAsync, SendMessageAsync, Notifications
│   └── Devices.fs            # High-level typed API (see below)
├── SpikePrime.Demo/          # Console demo (net10.0-windows10.0.26100.0)
│   └── Program.fs            # Connect, init, firmware query, sensor stream
├── SpikePrime.Web/           # ASP.NET Core WebSocket server (net10.0-windows10.0.26100.0)
│   └── Program.fs            # BLE background service + WebSocket broadcast → browser
└── SpikePrime.Web.Client/    # Fable/SolidJS browser visualiser
    └── src/
        ├── App.fs            # SolidJS components: orientation, display, IMU, ports, raw byte dump
        └── Program.fs        # App entry point
```

## Library API (`SpikePrime`)

### `Protocol`
- `packMessage payload` — COBS-encode + XOR + append `0x02` frame terminator
- `unpackFrame frame` — strip terminator + XOR + COBS-decode → raw payload bytes
- `encodeTunnelCommand json` — wrap a JSON string in a `MSG_TUNNEL` (0x32) frame ready to send
- `HubFrame` — `{ TypeByte; CmdId; IsHubPush; Data }` decoded from a raw BLE notification
- `FrameDecoder` — stateful accumulator; `Feed(chunk)` returns a sequence of complete `HubFrame` values

### `Transport`
- `scan()` — `IObservable<HubAdvertisement>` of all BLE devices advertising the LEGO service UUID or with a non-empty local name, plus a `CancellationTokenSource`
- `scanAndConnectAsync(timeout, name)` — scan and connect to the first hub matching the optional name filter; returns a `HubConnection`
- `HubConnection` — wraps GATT characteristics; `WriteAsync` chunks data into 20-byte ATT payloads, `DataReceived : IObservable<byte[]>`

### `Hub`
- `Hub` — routes decoded frames: response frames are matched to pending `SendRequestAsync` calls; unsolicited frames are published on `Notifications : IObservable<HubFrame>`
- `connectFirstAsync(timeout, name)` — scan + connect + return a ready `Hub`
- `InitAsync()` — send the required init handshake before any other commands
- `SendRequestAsync(typeByte, cmdId, data)` — fire a framed request and await the hub's response

### `Devices`
| Symbol | Description |
|---|---|
| `getFirmwareVersionAsync hub` | Raw firmware version bytes from the hub |
| `getHubInfoAsync hub` | Raw hardware/build info bytes |
| `startStreamingAsync hub` | Request continuous ~10 Hz device notifications |
| `streamingFrames hub` | `IObservable<HubFrame>` of raw streaming frames |
| `deviceSnapshots hub` | `IObservable<DeviceSnapshot>` — parsed sensor state at each frame |
| `deviceSnapshotsWithRaw hub` | `IObservable<DeviceBlock list * DeviceSnapshot>` — parsed snapshot paired with raw block bytes (used by the visualiser) |
| `tryParseDeviceSnapshot frame` | Parse a single `HubFrame` → `DeviceSnapshot option` |

**`IoDeviceType`** — identifies the physical device on a port:
`MediumMotor` · `LargeMotor` · `ColourSensor` · `DistanceSensor` · `ForceSensor` · `ColourMatrix` · `SmallAngularMotor` · `MedAngularMotor` · `LargeAngularMotor` · `UnknownDevice of byte`

**`DeviceSnapshot`** fields:

| Field | Type | Description |
|---|---|---|
| `Battery` | `byte option` | Battery percentage |
| `Orientation` | `HubOrientation` | Which face is up: `Top \| Front \| Back \| LeftSide \| RightSide \| Bottom` |
| `Yaw` / `Pitch` / `Roll` | `int16<deg>` | IMU orientation angles |
| `GyroX` / `GyroY` / `GyroZ` | `int16<dps>` | Gyroscope rate (degrees/second) |
| `AccX` / `AccY` / `AccZ` | `int16<cms2>` | Accelerometer (cm/s²; 981 ≈ 1g) |
| `MatrixDisplay` | `byte[] option` | 25 pixel brightness values (0–100), row-major; absent until first frame |
| `Ports` | `(Port * PortReading) list` | Per-port readings |

**`PortReading`** cases: `Motor of MotorReading` (position, relativePosition, speed, power) · `Distance of int16<mm>` · `Color of ColorReading` (colorId, reflect, red/green/blue 0–255) · `Force(pct, pressed)`

## Web Visualiser

A browser-based live dashboard that streams sensor data from the hub and displays it in real time.

![SPIKE Prime Visualiser](SPIKE%20Prime%20Visualiser.png)

The dashboard shows:
- **Orientation** — which face of the hub is up, displayed as an unfolded cube cross
- **Display** — live 5×5 LED matrix brightness grid
- **Battery** — percentage from the hub
- **IMU** — yaw/pitch/roll angles and X/Y/Z accelerometer and gyroscope readings
- **Ports** — live reading per connected device (motor, colour sensor, distance sensor, force sensor)
- **Raw Device Blocks** — annotated hex dump of each block in the streaming frame, with unknown bytes highlighted in red

![Raw Device Blocks](SPIKE%20Prime%20Raw%20Device%20Blocks.png)

Early in the reverse-engineering process, far more bytes were red — the 5×5 matrix display block was entirely unknown, and the colour sensor block had several unidentified bytes before the hi/lo pairs for R, G, and B were worked out. The raw bytes panel was the primary tool for narrowing these down.

### Architecture

```
Hub ──BLE──▶ SpikePrime.Web (ASP.NET Core, :5000)
                     │  WebSocket /ws
                     ▼
             Browser (SolidJS/Fable, :5173)
```

The server runs a `HubStreamService` background service that scans for and connects to a hub, parses the ~10 Hz sensor stream, and broadcasts JSON snapshots to all connected WebSocket clients. The browser reconnects automatically after 2 seconds if the connection drops.

### Running the visualiser

```powershell
# Terminal 1 — backend server
dotnet run --project SpikePrime.Web

# Terminal 2 — Fable compiler + Vite dev server (combined)
cd SpikePrime.Web.Client
npm start
```

Then open http://localhost:5173.

## Console smoke-test

A minimal console app for verifying hub connectivity and sensor streaming without a browser. Useful as a quick sanity check and as a concise example of using the library directly.

```powershell
dotnet run --project SpikePrime.Demo
```

Press the hub's centre button when prompted. It connects to a hub named `"Apex"` (edit `Program.fs` to change the name or pass `None` to match any hub), sends the init handshake, queries firmware and hub info, then prints 10 `DeviceSnapshot` values from the ~10 Hz sensor stream.

Example output:
```
snap[1]  bat=10%  yaw=-2    pitch=29    roll=27     face=Top
    port A  motor pos=-91 relPos=-91 speed=0% power=0%
    port B  color id=7 reflect=94% rgb=(32,24,21)
    port D  distance = -1 mm
    port F  motor pos=98 relPos=98 speed=0% power=0%
```
