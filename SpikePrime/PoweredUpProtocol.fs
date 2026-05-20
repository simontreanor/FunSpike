module SpikePrime.PoweredUpProtocol

open System

// ---------------------------------------------------------------------------
// GATT UUIDs — LEGO Wireless Protocol 3.0
// Used by Powered Up / Technic hubs and the 88010 remote control.
// ---------------------------------------------------------------------------

let [<Literal>] LwpServiceUuid = "00001623-1212-efde-1623-785feabcd123"
let [<Literal>] LwpCharUuid    = "00001624-1212-efde-1623-785feabcd123"

let LwpServiceGuid = Guid.Parse(LwpServiceUuid)
let LwpCharGuid    = Guid.Parse(LwpCharUuid)

// ---------------------------------------------------------------------------
// LWP message type bytes
// ---------------------------------------------------------------------------

/// Hub properties request/response (name, battery, BLE signal strength, etc.).
[<Literal>]
let MsgHubProperties = 0x01uy

/// Hub attached I/O notification — sent when a port gains or loses a device.
[<Literal>]
let MsgHubAttachedIo = 0x04uy

/// Port Input Format Setup (Single) — host → hub, subscribes to value-change
/// notifications for one port in one mode.
[<Literal>]
let MsgPortInputFormatSetupSingle = 0x41uy

/// Port Value (Single) — hub → host, one port's current value.
[<Literal>]
let MsgPortValueSingle = 0x45uy

// ---------------------------------------------------------------------------
// LWP message frame
// ---------------------------------------------------------------------------
//
// Wire format (no COBS, no XOR — plain bytes):
//   Byte 0      : message length, including this byte.
//                 If bit 7 is 0 → single-byte encoding (length 0–127).
//                 If bit 7 is 1 → two-byte: length = (byte0 & 0x7F) | (byte1 << 7).
//   Byte 1      : hub ID (always 0x00 for the 88010 remote).
//   Byte 2      : message type.
//   Bytes 3+    : type-specific payload.
//
// All messages from the 88010 remote are ≤ 20 bytes, so single-byte lengths
// are used throughout in practice.

/// A decoded LWP message received from a Powered Up device.
type LwpMessage =
    { MsgType : byte
      HubId   : byte
      Payload : byte[] }

/// Try to decode the first LWP message from a raw byte array.
/// Returns None if the data is too short or the length field is inconsistent.
let tryDecodeLwpMessage (data: byte[]) : LwpMessage option =
    if data.Length < 3 then None
    else
        let msgLen =
            if data.[0] &&& 0x80uy = 0uy then
                int data.[0]
            elif data.Length >= 2 then
                (int (data.[0] &&& 0x7Fuy)) ||| (int data.[1] <<< 7)
            else 0
        if msgLen < 3 || data.Length < msgLen then None
        else
            Some { MsgType = data.[2]
                   HubId   = data.[1]
                   Payload = if msgLen > 3 then data.[3 .. msgLen - 1] else [||] }

// ---------------------------------------------------------------------------
// Command builders
// ---------------------------------------------------------------------------

/// Build a Port Input Format Setup (Single) command (type 0x41).
/// Tells the hub to send a Port Value (Single) notification whenever the
/// port value changes by at least 1 unit in the given mode.
///
/// portId : 0 = left channel, 1 = right channel (88010 remote).
/// mode   : 0 = RC_KEY / KEYSD (button value as signed int8).
let buildPortInputFormatSetup (portId: byte) (mode: byte) : byte[] =
    // [length=10][hub=0x00][0x41][portId][mode][deltaInterval LE 4 bytes][notify=1]
    [| 0x0Auy; 0x00uy; MsgPortInputFormatSetupSingle
       portId; mode
       0x01uy; 0x00uy; 0x00uy; 0x00uy  // deltaInterval = 1 (little-endian uint32)
       0x01uy |]                        // notificationEnabled = true

/// Port Output Command (host → hub): drive output devices such as motors and LEDs.
[<Literal>]
let MsgPortOutputCmd = 0x81uy

/// Build a Hub Properties Subscribe command (0x01 / operation 0x02).
/// Tells the hub to push an Update whenever the given property changes.
/// Common property IDs: 0x02 = Button state, 0x06 = Battery voltage %.
let buildHubPropertiesSubscribe (propertyId: byte) : byte[] =
    // [length=5][hub_id=0][0x01 HubProperties][propertyId][0x02 Subscribe]
    [| 0x05uy; 0x00uy; MsgHubProperties; propertyId; 0x02uy |]

// ---------------------------------------------------------------------------
// Hub Properties / Hub Attached I/O — shared constants
// (used by city hub 88009, move hub 88006, and remote 88010)
// ---------------------------------------------------------------------------

/// Hub Properties: Update notification operation byte (hub → host, 0x06).
[<Literal>]
let HubPropOpUpdate = 0x06uy

/// Hub Properties: Button state property ID (0x02).
[<Literal>]
let HubPropIdButton = 0x02uy

/// Hub Properties: Battery voltage % property ID (0x06).
[<Literal>]
let HubPropIdBattery = 0x06uy

/// Hub Attached I/O: event — device was detached (0x00).
[<Literal>]
let IoEventDetached = 0x00uy

/// Hub Attached I/O: event — device was attached (0x01).
[<Literal>]
let IoEventAttached = 0x01uy

// ---------------------------------------------------------------------------
// IO device type → human-readable name
// ---------------------------------------------------------------------------

/// Return a human-readable name for a LWP 3.0 IO device type ID.
/// Falls back to "0xNNNN" for unknown types.
let ioDeviceName (id: uint16) =
    match id with
    | 0x0001us -> "Motor"
    | 0x0002us -> "Train Motor"
    | 0x000Bus -> "Boost Motor"
    | 0x0014us -> "Voltage"
    | 0x0015us -> "Current"
    | 0x0016us -> "Piezo"
    | 0x0017us -> "RGB LED"
    | 0x001Eus -> "PF Motor"
    | 0x0025us -> "Color/Dist Sensor"
    | 0x0026us -> "Interactive Motor"
    | 0x0028us -> "Move Hub Motor"
    | 0x002Eus -> "Large Motor"
    | 0x002Fus -> "XL Motor"
    | 0x0034us -> "3-Phase Motor"
    | 0x0039us -> "Medium Angular Motor"
    | 0x004Bus -> "Large Angular Motor"
    | other    -> sprintf "0x%04X" other
