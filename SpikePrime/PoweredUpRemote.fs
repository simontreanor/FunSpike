module SpikePrime.PoweredUpRemote

open System
open System.Threading.Tasks
open Windows.Devices.Bluetooth
open Windows.Devices.Bluetooth.Advertisement
open Windows.Devices.Bluetooth.GenericAttributeProfile
open Windows.Devices.Enumeration
open Windows.Storage.Streams
open SpikePrime.PoweredUpProtocol

// ---------------------------------------------------------------------------
// Domain types
// ---------------------------------------------------------------------------

/// The two physical button channels on the 88010 remote.
type RemoteChannel = Left | Right

/// The button state reported for a single channel.
/// Released means no button on that channel is currently held.
type RemoteButton =
    | Plus      // + (up) button pressed
    | Minus     // - (down) button pressed
    | Stop      // Stop button pressed (centre button of each channel group)
    | Released  // No button held on this channel

/// A single button-state change event from the remote.
/// Fired on both press (Plus/Minus/Stop) and release (Released).
type ButtonEvent =
    { Channel : RemoteChannel
      Button  : RemoteButton }

// ---------------------------------------------------------------------------
// Parsing helpers
// ---------------------------------------------------------------------------

// 88010 button values (Mode 0 — RC_KEY / KEYSD, 1-byte signed int):
//   0x01  (+1)  : + button held
//   0xFF  (-1)  : − button held
//   0x7F (+127) : Stop button held
//   0x00  (0)   : released

let private parseButton : byte -> RemoteButton = function
    | 0x01uy -> Plus
    | 0xFFuy -> Minus
    | 0x7Fuy -> Stop
    | _      -> Released  // 0x00 = released; any unrecognised value treated as Released

// Port assignments on the 88010 remote:
//   Port 0 : Left channel buttons
//   Port 1 : Right channel buttons

let private parseChannel : byte -> RemoteChannel option = function
    | 0uy -> Some Left
    | 1uy -> Some Right
    | _   -> None  // port 2 (LED indicator) or unknown — ignore

// ---------------------------------------------------------------------------
// RemoteConnection
// ---------------------------------------------------------------------------

/// A live BLE connection to a LEGO Powered Up remote (88010).
/// Raises ButtonEvents whenever a button is pressed or released.
type RemoteConnection(device: BluetoothLEDevice, char_: GattCharacteristic) =

    let buttonEvt   = Event<ButtonEvent>()
    let greenBtnEvt = Event<bool>()
    let batteryEvt  = Event<int>()
    let disconnEvt  = Event<unit>()

    // Route incoming LWP Port Value (Single) notifications to ButtonEvents.
    do char_.add_ValueChanged(fun _ args ->
        use reader = DataReader.FromBuffer(args.CharacteristicValue)
        let bytes  = Array.zeroCreate<byte> (int args.CharacteristicValue.Length)
        reader.ReadBytes(bytes)
        match tryDecodeLwpMessage bytes with
        | Some msg when msg.MsgType = MsgPortValueSingle && msg.Payload.Length >= 2 ->
            // Payload: [portId][value]
            match parseChannel msg.Payload.[0] with
            | Some ch -> buttonEvt.Trigger { Channel = ch; Button = parseButton msg.Payload.[1] }
            | None    -> ()
        | Some msg when msg.MsgType = MsgHubProperties && msg.Payload.Length >= 3 ->
            // Payload: [propertyId][operation][value…]
            let propId = msg.Payload.[0]
            let op     = msg.Payload.[1]
            if op = 0x06uy then  // HubPropUpdate
                match propId with
                | 0x02uy -> greenBtnEvt.Trigger(msg.Payload.[2] <> 0uy)  // green button
                | 0x06uy -> batteryEvt.Trigger(int msg.Payload.[2])      // battery %
                | _      -> ()
        | _ -> ())

    do device.add_ConnectionStatusChanged(fun dev _ ->
        if dev.ConnectionStatus = BluetoothConnectionStatus.Disconnected then
            disconnEvt.Trigger())

    /// Observable stream of button events, fired on every press and release.
    member _.ButtonEvents  : IObservable<ButtonEvent> = buttonEvt.Publish :> _

    /// Fires true when the green power/BLE button is pressed, false when released.
    member _.GreenButton   : IObservable<bool> = greenBtnEvt.Publish :> _

    /// Fires with battery percentage (0–100) whenever it changes.
    member _.Battery       : IObservable<int>  = batteryEvt.Publish :> _

    /// Fired when the remote disconnects from BLE.
    member _.Disconnected  : IObservable<unit> = disconnEvt.Publish :> _

    member _.Dispose() =
        try char_.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None).AsTask()
                |> ignore
        with _ -> ()
        device.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

// ---------------------------------------------------------------------------
// Low-level connect
// ---------------------------------------------------------------------------

/// Write a byte array to the LWP characteristic (WriteWithoutResponse).
let private writeAsync (char_: GattCharacteristic) (bytes: byte[]) : Task =
    task {
        use writer = new DataWriter()
        writer.WriteBytes(bytes)
        let! result =
            char_.WriteValueWithResultAsync(
                writer.DetachBuffer(),
                GattWriteOption.WriteWithoutResponse).AsTask()
        if result.Status <> GattCommunicationStatus.Success then
            printfn "  [remote] write failed: status=%A  protocolError=%A"
                result.Status result.ProtocolError
    }

/// Connect to a Powered Up remote at the given Bluetooth address.
let private connectAtAddressAsync (bluetoothAddress: uint64) : Task<RemoteConnection> =
    task {
        let! device = BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask()
        if device = null then
            failwithf "Could not obtain BLE device object for address %d" bluetoothAddress
        printfn "  [remote] device: %s" device.Name

        // Pair if not already paired (Just Works — no PIN required).
        let pairing = device.DeviceInformation.Pairing
        printfn "  [remote] IsPaired=%b  CanPair=%b" pairing.IsPaired pairing.CanPair
        if not pairing.IsPaired && pairing.CanPair then
            printfn "  [remote] initiating BLE pairing..."
            let! pairResult = pairing.PairAsync(DevicePairingProtectionLevel.None).AsTask()
            printfn "  [remote] pair result: %A" pairResult.Status
        elif pairing.IsPaired then
            printfn "  [remote] already paired"

        // Hold an explicit GattSession to keep the ACL connection alive.
        let! session = GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask()
        session.MaintainConnection <- true

        do! Task.Delay(1000)  // Let the ACL link and GATT service discovery settle.

        let! allSvcResult = device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask()
        printfn "  [remote] GATT services: status=%A  count=%d"
            allSvcResult.Status allSvcResult.Services.Count
        let service =
            allSvcResult.Services |> Seq.tryFind (fun s -> s.Uuid = LwpServiceGuid)
        if service.IsNone then
            failwithf "LWP GATT service not found on remote (status=%A)" allSvcResult.Status
        let service = service.Value

        let! charResult = service.GetCharacteristicsForUuidAsync(LwpCharGuid).AsTask()
        if charResult.Status <> GattCommunicationStatus.Success || charResult.Characteristics.Count = 0 then
            failwith "LWP characteristic not found"
        let char_ = charResult.Characteristics.[0]

        // Enable GATT notifications so the remote can push messages to us.
        let! cccdStatus =
            char_.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask()
        if cccdStatus <> GattCommunicationStatus.Success then
            failwithf "Failed to enable LWP notifications: %A" cccdStatus
        printfn "  [remote] notifications enabled"

        let conn = new RemoteConnection(device, char_)

        // Brief pause to let the remote finish sending its Hub Attached I/O messages.
        do! Task.Delay(500)

        // Subscribe to port 0 (left channel) and port 1 (right channel) in Mode 0 (RC_KEY).
        do! writeAsync char_ (buildPortInputFormatSetup 0uy 0uy)
        do! writeAsync char_ (buildPortInputFormatSetup 1uy 0uy)
        printfn "  [remote] subscribed to left and right button channels"

        // Subscribe to Hub Properties: green button (0x02) and battery % (0x06).
        do! writeAsync char_ (buildHubPropertiesSubscribe 0x02uy)
        do! writeAsync char_ (buildHubPropertiesSubscribe 0x06uy)
        printfn "  [remote] subscribed to button and battery properties"

        return conn
    }

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// Scan for the first advertising Powered Up 88010 remote (LWP service UUID)
/// and connect to it.  Times out after the given duration.
let scanAndConnectRemoteAsync (timeout: TimeSpan) : Task<RemoteConnection> =
    task {
        let tcs     = TaskCompletionSource<uint64>()
        let watcher = BluetoothLEAdvertisementWatcher()
        watcher.ScanningMode <- BluetoothLEScanningMode.Active

        watcher.add_Received(fun _ args ->
            if not tcs.Task.IsCompleted then
                let hasLwpService =
                    args.Advertisement.ServiceUuids |> Seq.exists (fun u -> u = LwpServiceGuid)
                if hasLwpService then
                    let name =
                        if args.Advertisement.LocalName <> null
                           && args.Advertisement.LocalName.Length > 0
                        then args.Advertisement.LocalName
                        else "(unknown)"
                    printfn "  [remote] found via advertisement: '%s' (address=%d, RSSI=%d dBm)"
                        name args.BluetoothAddress args.RawSignalStrengthInDBm
                    tcs.TrySetResult(args.BluetoothAddress) |> ignore)

        watcher.Start()
        let! _ = Task.WhenAny(tcs.Task :> Task, Task.Delay(timeout))
        watcher.Stop()

        if tcs.Task.IsCompleted then
            return! connectAtAddressAsync tcs.Task.Result
        else
            return failwith "No Powered Up remote found within the scan timeout"
    }
