module SpikePrime.Devices

open System
open System.Threading.Tasks
open SpikePrime.Hub
open SpikePrime.Protocol

// ---------------------------------------------------------------------------
// High-level typed API built on the binary protocol.
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Units of measure
// ---------------------------------------------------------------------------

/// Millimetres — used for distance sensor readings.
[<Measure>] type mm
/// Degrees — used for motor position and IMU orientation angles.
[<Measure>] type deg
/// Percent (0–100) — used for motor power and speed.
[<Measure>] type pct
/// Degrees per second — used for gyroscope rate readings.
[<Measure>] type dps
/// Centimetres per second squared — used for accelerometer readings (981 = 1g ≈ 9.81 m/s²).
[<Measure>] type cms2

// ---------------------------------------------------------------------------
// Port
// ---------------------------------------------------------------------------

/// A physical port on the SPIKE Prime hub (A–F).
type Port = A | B | C | D | E | F

// ---------------------------------------------------------------------------
// I/O device type
// ---------------------------------------------------------------------------

/// Identifies the physical LEGO device attached to a port.
/// Motor subtypes come from byte 2 of the 0x0A motor block (pybricks assigned-numbers).
/// Sensor types are inferred from the notification block-type byte.
type IoDeviceType =
    | MediumMotor        // 0x30 — SPIKE Prime Medium Motor    (45603)
    | LargeMotor         // 0x31 — SPIKE Prime Large Motor     (45602)
    | ColourSensor       // 0x3D — Technic Colour Sensor        (45605)
    | DistanceSensor     // 0x3E — Technic Distance Sensor      (45604)
    | ForceSensor        // 0x3F — Technic Force Sensor         (45606)
    | ColourMatrix       // 0x40 — Technic 3×3 Colour Matrix    (45608)
    | SmallAngularMotor  // 0x41 — Technic Small Angular Motor  (45607)
    | MedAngularMotor    // 0x4B — Technic Med Angular Motor    (88018)
    | LargeAngularMotor  // 0x4C — Technic Lg Angular Motor     (88017)
    | UnknownDevice of byte

/// Parse a pybricks I/O device-type byte into an IoDeviceType.
let parseIoDeviceType : byte -> IoDeviceType = function
    | 0x30uy -> MediumMotor
    | 0x31uy -> LargeMotor
    | 0x3Duy -> ColourSensor
    | 0x3Euy -> DistanceSensor
    | 0x3Fuy -> ForceSensor
    | 0x40uy -> ColourMatrix
    | 0x41uy -> SmallAngularMotor
    | 0x4Buy -> MedAngularMotor
    | 0x4Cuy -> LargeAngularMotor
    | b      -> UnknownDevice b

/// Human-readable display name (includes LEGO set number) for an IoDeviceType.
let ioDeviceTypeName = function
    | MediumMotor        -> "Medium Motor \u00B7 45603"
    | LargeMotor         -> "Large Motor \u00B7 45602"
    | ColourSensor       -> "Colour Sensor \u00B7 45605"
    | DistanceSensor     -> "Distance Sensor \u00B7 45604"
    | ForceSensor        -> "Force Sensor \u00B7 45606"
    | ColourMatrix       -> "3\u00D73 Matrix \u00B7 45608"
    | SmallAngularMotor  -> "Small Angular Motor \u00B7 45607"
    | MedAngularMotor    -> "Med Angular Motor \u00B7 88018"
    | LargeAngularMotor  -> "Lg Angular Motor \u00B7 88017"
    | UnknownDevice b    -> sprintf "Unknown (0x%02X)" b

/// Convert a wire port index (0–5) to a Port value.
let private toPort = function
    | 0 -> Port.A | 1 -> Port.B | 2 -> Port.C
    | 3 -> Port.D | 4 -> Port.E | 5 -> Port.F
    | n -> failwith $"Unexpected port index: {n}"

/// Convert a Port value to its wire index (0–5).
let private portIndex (port: Port) =
    match port with
    | Port.A -> 0 | Port.B -> 1 | Port.C -> 2
    | Port.D -> 3 | Port.E -> 4 | Port.F -> 5

let inline private asDeg16 (x : int16) : int16<deg> = LanguagePrimitives.Int16WithMeasure x
let inline private asDeg32 (x : int32) : int32<deg> = LanguagePrimitives.Int32WithMeasure x
let inline private asMm    (x : int16) : int16<mm>  = LanguagePrimitives.Int16WithMeasure x
let inline private asPct16 (x : int16) : int16<pct>  = LanguagePrimitives.Int16WithMeasure x
let inline private asPct8  (x : int8)  : int8<pct>   = LanguagePrimitives.SByteWithMeasure x
let inline private asCms2  (x : int16) : int16<cms2> = LanguagePrimitives.Int16WithMeasure x
let inline private asDps   (x : int16) : int16<dps>  = LanguagePrimitives.Int16WithMeasure x

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

/// Motor position, relative position, speed, and power read from a port.
type MotorReading =
    { Position         : int16<deg>  // absolute shaft angle, [0..359]
      RelativePosition : int32<deg>  // accumulating encoder count; can be any signed value
      Speed            : int8<pct>   // motor speed, −100..100 %
      Power            : int16<pct> }// motor power, −100..100 %

/// Data read from a color sensor port.
type ColorReading =
    { ColorId : byte   // detected color [0..15]; 255 = no detection
      Reflect : byte   // reflected light intensity, 0–100 %
      Red     : byte   // red channel, 0–255
      Green   : byte   // green channel, 0–255
      Blue    : byte } // blue channel, 0–255

/// Data read from a single connected port in a device-notification frame.
type PortReading =
    | Motor    of MotorReading
    | Distance of int16<mm>
    | Color    of ColorReading
    | Force    of pct: byte * pressed: bool

/// Which face of the hub is currently pointing upward.
/// Values match the stock LEGO firmware Python constants (TOP=0, FRONT=1, RIGHT=2, BOTTOM=3, BACK=4, LEFT=5).
/// Exact mapping against the BLE byte stream still needs empirical confirmation.
type HubOrientation = Top | Front | RightSide | Bottom | Back | LeftSide

/// Parsed snapshot from one MSG_DEVICE_NOTIFICATION (0x3C) frame.
type DeviceSnapshot =
    { Battery     : byte option
      Ports       : (Port * PortReading) list
      Orientation : HubOrientation
      Yaw         : int16<deg>
      Pitch       : int16<deg>
      Roll        : int16<deg>
      GyroX       : int16<dps>  // gyroscope rate X, degrees per second
      GyroY       : int16<dps>  // gyroscope rate Y, degrees per second
      GyroZ       : int16<dps>  // gyroscope rate Z, degrees per second
      AccX        : int16<cms2>  // accelerometer X, cm/s² (981 = 1g)
      AccY        : int16<cms2>  // accelerometer Y, cm/s²
      AccZ        : int16<cms2> }// accelerometer Z, cm/s²

let private emptySnapshot =
    { Battery=None; Ports=[]; Orientation=Top
      Yaw=0s<deg>; Pitch=0s<deg>; Roll=0s<deg>
      GyroX=0s<dps>; GyroY=0s<dps>; GyroZ=0s<dps>
      AccX=0s<cms2>; AccY=0s<cms2>; AccZ=0s<cms2> }

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

/// Convert the raw faceUp byte from the IMU block to a HubOrientation.
/// Empirically verified: TOP=0, BACK=1, RIGHT=2, BOTTOM=3, FRONT=4, LEFT=5
/// (Note: LEGO firmware doc says FRONT=1/BACK=4 but physical testing shows these are swapped.)
let private toOrientation = function
    | 0uy -> Top | 1uy -> Back | 2uy -> RightSide
    | 3uy -> Bottom | 4uy -> Front | 5uy -> LeftSide
    | _   -> Top  // fallback for unseen values

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
            let yaw   = BitConverter.ToInt16(data, offset + 3)  |> asDeg16
            let pitch = BitConverter.ToInt16(data, offset + 5)  |> asDeg16
            let roll  = BitConverter.ToInt16(data, offset + 7)  |> asDeg16
            let accX  = BitConverter.ToInt16(data, offset + 9)  |> asCms2
            let accY  = BitConverter.ToInt16(data, offset + 11) |> asCms2
            let accZ  = BitConverter.ToInt16(data, offset + 13) |> asCms2
            let gyroX = BitConverter.ToInt16(data, offset + 15) |> asDps
            let gyroY = BitConverter.ToInt16(data, offset + 17) |> asDps
            let gyroZ = BitConverter.ToInt16(data, offset + 19) |> asDps
            s <- { s with
                     Orientation = toOrientation data.[offset+1]
                     Yaw    = yaw
                     Pitch  = pitch
                     Roll   = roll
                     AccX   = accX
                     AccY   = accY
                     AccZ   = accZ
                     GyroX  = gyroX
                     GyroY  = gyroY
                     GyroZ  = gyroZ }
            offset <- offset + 21

        | d when d = DevMotor && remaining >= 12 ->
            let port   = toPort (int data.[offset + 1])
            let pos    = BitConverter.ToInt16(data, offset + 3) |> asDeg16
            let power  = BitConverter.ToInt16(data, offset + 5) |> asPct16
            let speed  = int8 data.[offset + 7] |> asPct8
            let relPos = BitConverter.ToInt32(data, offset + 8) |> asDeg32
            s      <- { s with Ports = (port, Motor { Position = pos; RelativePosition = relPos; Speed = speed; Power = power }) :: s.Ports }
            offset <- offset + 12

        | d when d = DevForce && remaining >= 4 ->
            let port    = toPort (int data.[offset + 1])
            let pct     = data.[offset + 2]
            let pressed = data.[offset + 3] = 1uy
            s      <- { s with Ports = (port, Force(pct, pressed)) :: s.Ports }
            offset <- offset + 4

        | d when d = DevColor && remaining >= 10 ->
            let port = toPort (int data.[offset + 1])
            let cr   = { ColorId = data.[offset + 2]
                         Reflect = data.[offset + 3]   // byte layout needs empirical verification
                         Red     = data.[offset + 4]
                         Green   = data.[offset + 5]
                         Blue    = data.[offset + 6] }
            s      <- { s with Ports = (port, Color cr) :: s.Ports }
            offset <- offset + 10

        | d when d = DevDistance && remaining >= 4 ->
            let port = toPort (int data.[offset + 1])
            let mm   = BitConverter.ToInt16(data, offset + 2) |> asMm
            s      <- { s with Ports = (port, Distance mm) :: s.Ports }
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
// Raw block extraction — for protocol analysis and gap-filling
// ---------------------------------------------------------------------------

/// One raw device-data block as found inside a streaming frame.
/// Used to display raw bytes alongside parsed values in the visualiser.
type DeviceBlock =
    { TypeByte : byte    // device type code (0x00=battery, 0x01=imu, 0x0A=motor, …)
      Offset   : int     // byte offset within the device-data section
      Raw      : byte[] } // all bytes of this block

/// Walk a device-data byte array and extract the raw slice for each block.
/// Mirrors the length rules in parseDeviceData.
let private extractBlocks (data: byte[]) : DeviceBlock list =
    let blocks = ResizeArray<DeviceBlock>()
    let mutable offset = 0
    while offset < data.Length do
        let devType   = data.[offset]
        let remaining = data.Length - offset
        let len =
            match devType with
            | 0x02uy when remaining >= 26 -> 26
            | d when d = DevBattery  && remaining >= 2  -> 2
            | d when d = DevImu      && remaining >= 21 -> 21
            | d when d = DevMotor    && remaining >= 12 -> 12
            | d when d = DevForce    && remaining >= 4  -> 4
            | d when d = DevColor    && remaining >= 10 -> 10
            | d when d = DevDistance && remaining >= 4  -> 4
            | _ -> remaining  // unknown type — consume the rest
        if len > 0 then
            blocks.Add({ TypeByte = devType; Offset = offset; Raw = data.[offset .. offset + len - 1] })
            offset <- offset + len
        else
            offset <- data.Length  // safety: prevent infinite loop
    List.ofSeq blocks

/// Like deviceSnapshots but also yields the raw DeviceBlock list for each frame.
/// Used by the visualiser to show raw bytes alongside parsed values.
let deviceSnapshotsWithRaw (hub: Hub) : IObservable<DeviceBlock list * DeviceSnapshot> =
    streamingFrames hub
    |> Observable.choose (fun frame ->
        match unpackFrame (rawBytesOf frame) with
        | None -> None
        | Some payload ->
            if payload.Length < 3 || payload.[0] <> 0x3Cuy then None
            else
                let payloadSize = int (BitConverter.ToUInt16(payload, 1))
                if 3 + payloadSize > payload.Length then None
                else
                    let deviceData = payload.[3 .. 2 + payloadSize]
                    let blocks     = extractBlocks deviceData
                    let snap       = parseDeviceData deviceData emptySnapshot
                    Some (blocks, snap))

// ---------------------------------------------------------------------------
// Motor control  (MSG_TUNNEL JSON commands, COBS+XOR encoded)
// ---------------------------------------------------------------------------

/// Start a motor on the given Port at speed (–100 to 100 pct).
let motorStartAsync (port: Port) (speed: int<pct>) (hub: Hub) : Task =
    let p    = portIndex port
    let s    = max -100<pct> (min 100<pct> speed)
    let json = sprintf """{"m":"motor","p":{"port":%d,"speed":%d}}""" p (int s)
    hub.SendMessageAsync(encodeTunnelCommand json)

/// Coast-stop a motor on the given Port.
let motorStopAsync (port: Port) (hub: Hub) : Task =
    let p    = portIndex port
    let json = sprintf """{"m":"motor","p":{"port":%d,"speed":0,"end_state":1}}""" p
    hub.SendMessageAsync(encodeTunnelCommand json)