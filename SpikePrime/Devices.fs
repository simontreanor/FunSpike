module SpikePrime.Devices

open System
open System.Threading.Tasks
open SpikePrime.Hub
open SpikePrime.Protocol

// ---------------------------------------------------------------------------
// High-level typed API built on the binary protocol.
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Firmware / hub info
// ---------------------------------------------------------------------------

/// Raw firmware version payload (bytes before the frame terminator).
type FirmwareVersion =
    { Raw : byte[] }
    override this.ToString() =
        this.Raw |> Array.map (sprintf "%02x") |> String.concat " "

/// Raw hub info payload (bytes before the frame terminator).
type HubInfo =
    { Raw : byte[] }
    override this.ToString() =
        this.Raw |> Array.map (sprintf "%02x") |> String.concat " "

/// Query the hub firmware version.
/// TX: 07 1b 02   RX: 0b 1a [data] 02
let getFirmwareVersionAsync (hub: Hub) : Task<FirmwareVersion> =
    task {
        let! frame = hub.SendRequestAsync(0x07uy, 0x0duy, [||])
        return ({ Raw = frame.Data } : FirmwareVersion)
    }

/// Query hub hardware/build info.
/// TX: 07 19 02   RX: 05 18 [data] 02
let getHubInfoAsync (hub: Hub) : Task<HubInfo> =
    task {
        let! frame = hub.SendRequestAsync(0x07uy, 0x0cuy, [||])
        return ({ Raw = frame.Data } : HubInfo)
    }

// ---------------------------------------------------------------------------
// Sensor / state streaming
// ---------------------------------------------------------------------------

/// Subscribe to continuous sensor and state streaming from the hub.
/// After this call, Hub.Notifications fires ~10 times/second with frames.
/// TX: 06 2b 67 00 02   RX: 07 2a 00 02 (ACK) then periodic 06 3f [data] 02
let startStreamingAsync (hub: Hub) : Task =
    task {
        let! _ = hub.SendRequestAsync(0x06uy, 0x15uy, [| 0x67uy; 0x00uy |])
        ()  // ACK received — streaming has started
    }

/// Filter Hub.Notifications to only raw streaming data frames (type=0x06, cmdId=0x1f).
let streamingFrames (hub: Hub) : IObservable<HubFrame> =
    hub.Notifications
    |> Observable.filter (fun f -> f.TypeByte = 0x06uy && f.CmdId = 0x1fuy)

// ---------------------------------------------------------------------------
// Device-notification parsing
// ---------------------------------------------------------------------------

/// Data read from a single connected port in a device-notification frame.
type PortReading =
    | Motor    of position: int32 * speed: int8 * power: int16
    | Distance of mm: int16
    | Color    of colorId: byte
    | Force    of pct: byte * pressed: bool

/// Parsed snapshot from one MSG_DEVICE_NOTIFICATION (0x3C) frame.
type DeviceSnapshot =
    { Battery : byte option
      Ports   : (int * PortReading) list   // (portId 0-5) × reading
      Yaw     : int16
      Pitch   : int16
      Roll    : int16
      AccX    : int16
      AccY    : int16
      AccZ    : int16
      FaceUp  : byte }

let private emptySnapshot =
    { Battery=None; Ports=[]; Yaw=0s; Pitch=0s; Roll=0s; AccX=0s; AccY=0s; AccZ=0s; FaceUp=0uy }

// Device type codes in the notification payload.
[<Literal>]
let private DevBattery  = 0x00uy
[<Literal>]
let private DevImu      = 0x01uy
[<Literal>]
let private DevMotor    = 0x0Auy
[<Literal>]
let private DevForce    = 0x0Buy
[<Literal>]
let private DevColor    = 0x0Cuy
[<Literal>]
let private DevDistance = 0x0Duy

/// Reconstruct the raw BLE frame bytes from a HubFrame for COBS+XOR unpacking.
let private rawBytesOf (f: HubFrame) : byte[] =
    [| yield f.TypeByte
       yield (f.CmdId * 2uy + (if f.IsHubPush then 1uy else 0uy))
       yield! f.Data
       yield FrameEnd |]

/// Parse the device-data bytes inside a device-notification logical payload.
let private parseDeviceData (data: byte[]) (snap: DeviceSnapshot) : DeviceSnapshot =
    let mutable s      = snap
    let mutable offset = 0
    while offset < data.Length do
        let devType   = data.[offset]
        let remaining = data.Length - offset
        match devType with
        | 0x02uy when remaining >= 26 ->
            // Hub/port-configuration block: 2-byte header + 6 ports × 4 bytes = 26 total.
            // No sensor values; skip.
            offset <- offset + 26

        | d when d = DevBattery && remaining >= 2 ->
            s      <- { s with Battery = Some data.[offset + 1] }
            offset <- offset + 2

        | d when d = DevImu && remaining >= 21 ->
            let yaw   = BitConverter.ToInt16(data, offset + 3)
            let pitch = BitConverter.ToInt16(data, offset + 5)
            let roll  = BitConverter.ToInt16(data, offset + 7)
            let accX  = BitConverter.ToInt16(data, offset + 9)
            let accY  = BitConverter.ToInt16(data, offset + 11)
            let accZ  = BitConverter.ToInt16(data, offset + 13)
            s <- { s with
                     FaceUp = data.[offset+1]
                     Yaw    = yaw
                     Pitch  = pitch
                     Roll   = roll
                     AccX   = accX
                     AccY   = accY
                     AccZ   = accZ }
            offset <- offset + 21

        | d when d = DevMotor && remaining >= 12 ->
            let portId = int data.[offset + 1]
            let absPos = BitConverter.ToInt16(data, offset + 3)
            let power  = BitConverter.ToInt16(data, offset + 5)
            let speed  = int8 data.[offset + 7]
            // position (int32) at bytes 8-11
            let pos    = BitConverter.ToInt32(data, offset + 8)
            s      <- { s with Ports = (portId, Motor(pos, speed, power)) :: s.Ports }
            offset <- offset + 12

        | d when d = DevForce && remaining >= 4 ->
            let portId  = int data.[offset + 1]
            let pct     = data.[offset + 2]
            let pressed = data.[offset + 3] = 1uy
            s      <- { s with Ports = (portId, Force(pct, pressed)) :: s.Ports }
            offset <- offset + 4

        | d when d = DevColor && remaining >= 10 ->
            let portId = int data.[offset + 1]
            s      <- { s with Ports = (portId, Color(data.[offset + 2])) :: s.Ports }
            offset <- offset + 10

        | d when d = DevDistance && remaining >= 4 ->
            let portId = int data.[offset + 1]
            let mm     = BitConverter.ToInt16(data, offset + 2)
            s      <- { s with Ports = (portId, Distance mm) :: s.Ports }
            offset <- offset + 4

        | _ -> offset <- data.Length   // unknown device type — skip remainder
    s

/// Unpack a raw streaming HubFrame and parse it as a DeviceSnapshot.
/// Returns None if the frame is not a valid MSG_DEVICE_NOTIFICATION (0x3C).
let tryParseDeviceSnapshot (frame: HubFrame) : DeviceSnapshot option =
    match unpackFrame (rawBytesOf frame) with
    | None -> None
    | Some payload ->
        // Logical bytes: [0x3C, payloadSizeLo, payloadSizeHi, device_data...]
        if payload.Length < 3 || payload.[0] <> 0x3Cuy then None
        else
            let payloadSize = int (BitConverter.ToUInt16(payload, 1))
            if 3 + payloadSize > payload.Length then None
            else
                let deviceData = payload.[3 .. 2 + payloadSize]
                Some (parseDeviceData deviceData emptySnapshot)

/// Observable stream of parsed DeviceSnapshots (~10 Hz).
let deviceSnapshots (hub: Hub) : IObservable<DeviceSnapshot> =
    streamingFrames hub
    |> Observable.choose tryParseDeviceSnapshot

// ---------------------------------------------------------------------------
// Motor control  (MSG_TUNNEL JSON commands, COBS+XOR encoded)
// ---------------------------------------------------------------------------

/// Map a port letter (A-F) to its numeric port ID (0-5).
let private portId (port: char) =
    match Char.ToUpper(port) with
    | 'A' -> 0 | 'B' -> 1 | 'C' -> 2
    | 'D' -> 3 | 'E' -> 4 | 'F' -> 5
    | c   -> failwith $"Invalid port letter: {c}"

/// Start a motor at the given port letter (A-F) and speed (-100 to 100 %).
let motorStartAsync (port: char) (speed: int) (hub: Hub) : Task =
    let p    = portId port
    let s    = Math.Max(-100, Math.Min(100, speed))
    let json = sprintf """{"m":"motor","p":{"port":%d,"speed":%d}}""" p s
    hub.SendMessageAsync(encodeTunnelCommand json)

/// Stop a motor (brake) on the given port letter.
let motorStopAsync (port: char) (hub: Hub) : Task =
    let p    = portId port
    let json = sprintf """{"m":"motor","p":{"port":%d,"speed":0,"end_state":1}}""" p
    hub.SendMessageAsync(encodeTunnelCommand json)