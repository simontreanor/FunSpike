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

## Project structure

```
funspike/
├── funspike.slnx
├── SpikePrime/          # Library (net10.0-windows10.0.26100.0)
│   ├── Protocol.fs      # COBS+XOR codec, HubFrame, FrameDecoder, packMessage, encodeTunnelCommand
│   ├── Transport.fs     # BLE scanner, HubConnection, scanAndConnectAsync
│   ├── Hub.fs           # Hub type, connectFirstAsync, InitAsync, SendRequestAsync, Notifications
│   └── Devices.fs       # High-level typed API (see below)
└── SpikePrime.Demo/     # Console demo (net10.0-windows10.0.26100.0)
    └── Program.fs       # Connect, init, firmware query, sensor stream
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
| `tryParseDeviceSnapshot frame` | Parse a single `HubFrame` → `DeviceSnapshot option` |
| `motorStartAsync port speed hub` | Start motor on port `'A'`–`'F'` at speed −100..100 |
| `motorStopAsync port hub` | Coast-stop a motor |

**`DeviceSnapshot`** fields: `Battery : byte option`, `Yaw / Pitch / Roll : int16`, `AccX / AccY / AccZ : int16`, `FaceUp : byte`, `Ports : (int * PortReading) list`

**`PortReading`** cases: `Motor(position, speed, power)` · `Distance(mm)` · `Color(colorId)` · `Force(pct, pressed)`

## Running the demo

```powershell
dotnet run --project SpikePrime.Demo
```

Press the hub's centre button when prompted. The demo:
1. Connects to a hub named `"Apex"` (edit `Program.fs` to change the name or pass `None` to match any hub)
2. Sends the init handshake
3. Queries firmware version and hub info
4. Starts the ~10 Hz sensor stream
5. Prints up to 10 `DeviceSnapshot` values — IMU orientation, battery %, and per-port readings (motor position, distance, colour, force)

Example output:
```
snap[1]  bat=10%  yaw=-2    pitch=29    roll=27     face=0
    port A  motor pos=-91 speed=0 power=0
    port B  color id=255
    port D  distance = -1 mm
    port F  motor pos=98 speed=0 power=0
```

## Building

```powershell
dotnet build
```
