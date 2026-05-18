module SpikePrime.Protocol

// ---------------------------------------------------------------------------
// Binary framing for the LEGO SPIKE Prime FW3 BLE protocol ("atlantis").
//
// Wire encoding (TX and RX):  COBS encode → XOR with 0x03 → append 0x02
//
// Logical message format (before packing):
//   Byte 0      : message type
//   Bytes 1..N  : payload
//
// Known logical message types:
//   0x00  MSG_INFO_REQUEST              host → hub (init handshake)
//   0x01  MSG_INFO_RESPONSE             hub → host
//   0x28  MSG_DEVICE_NOTIFICATION_REQUEST  host → hub (start streaming, 2-byte interval LE)
//   0x29  MSG_DEVICE_NOTIFICATION_RESPONSE hub → host (ACK)
//   0x32  MSG_TUNNEL                    host → hub (JSON command: motor, display, …)
//   0x3C  MSG_DEVICE_NOTIFICATION       hub → host (sensor stream, ~10 Hz)
//
// Device-notification device type codes (within a 0x3C payload):
//   0x00  battery    2  bytes: [level]
//   0x01  IMU       21  bytes: [faceUp, yawFace, yaw(i16), pitch(i16), roll(i16), accX(i16), accY(i16), accZ(i16), …]
//   0x02  5×5 matrix 26  bytes: [type, pix0..pix24] — 25 pixel brightness values 0–100, row-major
//   0x0A  motor     12  bytes: [portId, subType, position(i16,[0..359]°), power(i16,%), speed(i8,%), relativePosition(i32,°)]
//   0x0B  force      4  bytes: [portId, pct, pressed]
//   0x0C  color     10  bytes: [portId, colorId[0..15], reflect%, R, G, B, ???×3]
//   0x0D  distance   4  bytes: [portId, mm(i16 LE)]
//   0x0E  3×3 matrix 11  bytes (output device, ignored)
//
// Tunnel motor command JSON:
//   start : {"m":"motor","p":{"port":<0-5>,"speed":<-100..100>}}
//   stop  : {"m":"motor","p":{"port":<0-5>,"speed":0,"end_state":<0=coast|1=brake|2=hold>}}
//
// NOTE: The legacy encodeRequest / decodeFrame functions below accidentally produce
//       correct COBS+XOR bytes for the handful of commands that were reverse-engineered
//       from BLE captures.  They remain unchanged to preserve compatibility.  New
//       outgoing commands use packMessage / encodeTunnelCommand instead.
// ---------------------------------------------------------------------------

/// Frame terminator byte.
[<Literal>]
let FrameEnd = 0x02uy

/// The 3-byte init handshake that must be sent before the hub responds.
let initMessage = [| 0x00uy; 0x00uy; 0x02uy |]

// ---------------------------------------------------------------------------
// Frame type
// ---------------------------------------------------------------------------

/// A fully decoded frame received from the hub.
type HubFrame =
    { /// Type byte (byte[0]): e.g. 0x06 for sensor/streaming, 0x07 for config.
      TypeByte  : byte
      /// Command identifier: byte[1] >> 1.
      CmdId     : byte
      /// True when byte[1] is odd (unsolicited hub push or host request echo).
      IsHubPush : bool
      /// Data payload (bytes between the cmd byte and the frame terminator).
      Data      : byte[] }

// ---------------------------------------------------------------------------
// Encoder / decoder
// ---------------------------------------------------------------------------

/// Encode a host -> hub request frame.
/// cmdId   : command identifier; encoded as cmdId*2+1 in byte[1].
/// data    : optional payload bytes.
let encodeRequest (typeByte: byte) (cmdId: byte) (data: byte[]) : byte[] =
    [| yield typeByte
       yield (cmdId * 2uy + 1uy)
       yield! data
       yield FrameEnd |]

/// Decode a complete raw frame (last byte must be 0x02).
/// Returns None if the frame is malformed.
let decodeFrame (frame: byte[]) : HubFrame option =
    if frame.Length < 2 || frame.[frame.Length - 1] <> FrameEnd then None
    else
        let typeByte = frame.[0]
        let cmdByte  = frame.[1]
        Some
            { TypeByte  = typeByte
              CmdId     = cmdByte >>> 1
              IsHubPush = cmdByte &&& 1uy = 1uy
              Data      = if frame.Length > 2 then frame.[2 .. frame.Length - 2] else [||] }

// ---------------------------------------------------------------------------
// Frame decoder (streaming reassembly)
// ---------------------------------------------------------------------------

/// Accumulates raw BLE notification bytes and emits complete frames when
/// the 0x02 terminator is encountered.  BLE packets may arrive in chunks
/// smaller than a full frame; this type handles reassembly.
type FrameDecoder() =
    let buf = System.Collections.Generic.List<byte>(256)

    /// Feed a chunk of raw bytes received from a BLE notification.
    /// Returns zero or more complete decoded frames.
    member _.Feed(chunk: byte[]) : HubFrame list =
        let frames = System.Collections.Generic.List<HubFrame>()
        for b in chunk do
            buf.Add(b)
            if b = FrameEnd then
                match decodeFrame (buf.ToArray()) with
                | Some f -> frames.Add(f)
                | None   -> ()
                buf.Clear()
        frames |> Seq.toList

// ---------------------------------------------------------------------------
// COBS encode / decode  (custom LEGO variant, MAX_BLOCK_SIZE=84, OFFSET=2)
// ---------------------------------------------------------------------------

[<Literal>]
let private MaxBlockSize = 84
[<Literal>]
let private CobsOffset = 2

let private cobsEncode (data: byte[]) : byte[] =
    let buf = System.Collections.Generic.List<byte>()
    buf.Add(0uy)                 // placeholder for first code byte
    let mutable codeIdx = 0
    for b in data do
        if b <= 2uy then
            let delimBase    = int b * MaxBlockSize
            let blockOffset  = (buf.Count - codeIdx) + CobsOffset
            buf.[codeIdx]   <- byte (delimBase + blockOffset)
            codeIdx          <- buf.Count
            buf.Add(0uy)
        else
            buf.Add(b)
            if buf.Count - codeIdx >= MaxBlockSize then
                buf.[codeIdx] <- byte ((buf.Count - codeIdx) + CobsOffset)
                codeIdx        <- buf.Count
                buf.Add(0uy)
    buf.[codeIdx] <- byte ((buf.Count - codeIdx) + CobsOffset)
    buf.ToArray()

let private cobsDecode (data: byte[]) : byte[] =
    let result = System.Collections.Generic.List<byte>()
    let mutable i = 0
    while i < data.Length do
        let code      = int data.[i] - CobsOffset
        let delimiter = code / MaxBlockSize
        let len       = code % MaxBlockSize
        for j in 1 .. len - 1 do
            if i + j < data.Length then result.Add(data.[i + j])
        if delimiter <= 2 && (i + len) < data.Length then
            result.Add(byte delimiter)
        i <- i + len
    result.ToArray()

/// Pack a logical payload for sending: COBS encode → XOR with 0x03 → append 0x02.
let packMessage (payload: byte[]) : byte[] =
    let cobs  = cobsEncode payload
    let xored = cobs |> Array.map (fun b -> b ^^^ 0x03uy)
    Array.append xored [| 0x02uy |]

/// Unpack a received 0x02-terminated BLE frame to its logical payload.
/// Returns None if the frame is malformed.
let unpackFrame (frame: byte[]) : byte[] option =
    if frame.Length = 0 || frame.[frame.Length - 1] <> FrameEnd then None
    else
        let start   = if frame.[0] = 0x01uy then 1 else 0
        let inner   = frame.[start .. frame.Length - 2]
        let xored   = inner |> Array.map (fun b -> b ^^^ 0x03uy)
        Some (cobsDecode xored)

/// Encode a JSON string as a MSG_TUNNEL (0x32) COBS+XOR message.
/// Format: [0x32, len_lo, len_hi, utf8_json...]
let encodeTunnelCommand (json: string) : byte[] =
    let jsonBytes = System.Text.Encoding.UTF8.GetBytes(json)
    let len       = jsonBytes.Length
    let payload   =
        [| yield 0x32uy
           yield  byte (len &&& 0xFF)
           yield  byte ((len >>> 8) &&& 0xFF)
           yield! jsonBytes |]
    packMessage payload