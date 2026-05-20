module SpikePrime.PoweredUpHub

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

/// A device detected on an external port via a Hub Attached I/O message.
type AttachedDevice =
    { PortId     : byte
      DeviceType : uint16
      Name       : string }

/// Events emitted by a live 88009 HubConnection.
type HubEvent =
    | PortAttached  of AttachedDevice
    | PortDetached  of portId: byte
    | ButtonChanged of pressed: bool
    | BatteryChanged of pct: int

// ---------------------------------------------------------------------------
// Parsing helpers
// ---------------------------------------------------------------------------

let private deviceName (id: uint16) =
    match id with
    | 0x0001us -> "Motor"
    | 0x0002us -> "Train Motor"
    | 0x000Bus -> "Boost Motor"
    | 0x0017us -> "RGB LED"
    | 0x001Eus -> "PF Motor"
    | 0x0026us -> "Interactive Motor"
    | 0x002Eus -> "Large Motor"
    | 0x002Fus -> "XL Motor"
    | 0x0034us -> "3-Phase Motor"
    | 0x0039us -> "Medium Angular Motor"
    | 0x004Bus -> "Large Angular Motor"
    | 0x0014us -> "Voltage"
    | 0x0015us -> "Current"
    | 0x0016us -> "Piezo"
    | other    -> sprintf "0x%04X" other

// LWP constants (local to this module)
[<Literal>]
let private HubPropUpdate = 0x06uy  // Hub Properties: operation = Update (hub → host)
[<Literal>]
let private PropButton    = 0x02uy  // Hub Properties: Button state (0=released, 1=pressed)
[<Literal>]
let private PropBattery   = 0x06uy  // Hub Properties: Battery voltage (%)
[<Literal>]
let private IoDetached    = 0x00uy  // Hub Attached I/O: device removed
[<Literal>]
let private IoAttached    = 0x01uy  // Hub Attached I/O: device connected

// ---------------------------------------------------------------------------
// HubConnection
// ---------------------------------------------------------------------------

/// A live BLE connection to a LEGO 88009 Powered Up Hub.
/// Raises Events (port attach/detach, button presses, battery level updates)
/// and exposes motor/LED write commands.
type HubConnection(device: BluetoothLEDevice, char_: GattCharacteristic) =

    let hubEvt     = Event<HubEvent>()
    let disconnEvt = Event<unit>()
    let mutable ledPort = 0x32uy  // Default LED port for 88009; updated from Hub Attached I/O

    // Helper: write bytes to the LWP characteristic (WriteWithoutResponse).
    let write (bytes: byte[]) : Task =
        task {
            use writer = new DataWriter()
            writer.WriteBytes(bytes)
            let! result =
                char_.WriteValueWithResultAsync(
                    writer.DetachBuffer(),
                    GattWriteOption.WriteWithoutResponse).AsTask()
            if result.Status <> GattCommunicationStatus.Success then
                printfn "  [hub88009] write failed: status=%A  protocolError=%A"
                    result.Status result.ProtocolError
        }

    do char_.add_ValueChanged(fun _ args ->
        use reader = DataReader.FromBuffer(args.CharacteristicValue)
        let bytes = Array.zeroCreate<byte> (int args.CharacteristicValue.Length)
        reader.ReadBytes(bytes)
        match tryDecodeLwpMessage bytes with
        | Some msg ->
            match msg.MsgType with
            // ── Hub Attached I/O (0x04): port gained or lost a device ──────────────
            | t when t = MsgHubAttachedIo && msg.Payload.Length >= 2 ->
                let portId  = msg.Payload.[0]
                let evtType = msg.Payload.[1]
                match evtType with
                | e when e = IoDetached ->
                    hubEvt.Trigger(PortDetached portId)
                | e when e = IoAttached && msg.Payload.Length >= 4 ->
                    let devType = uint16 msg.Payload.[2] ||| (uint16 msg.Payload.[3] <<< 8)
                    if devType = 0x0017us then ledPort <- portId  // track LED port
                    let dev = { PortId = portId; DeviceType = devType; Name = deviceName devType }
                    hubEvt.Trigger(PortAttached dev)
                | _ -> ()
            // ── Hub Properties (0x01): button / battery updates ───────────────────
            | t when t = MsgHubProperties
                     && msg.Payload.Length >= 3
                     && msg.Payload.[1] = HubPropUpdate ->
                match msg.Payload.[0] with
                | p when p = PropButton  -> hubEvt.Trigger(ButtonChanged  (msg.Payload.[2] = 0x01uy))
                | p when p = PropBattery -> hubEvt.Trigger(BatteryChanged (int msg.Payload.[2]))
                | _ -> ()
            | _ -> ()
        | None -> ())

    do device.add_ConnectionStatusChanged(fun dev _ ->
        if dev.ConnectionStatus = BluetoothConnectionStatus.Disconnected then
            disconnEvt.Trigger())

    /// Stream of hub events: port attach/detach, button presses, battery level updates.
    member _.Events     : IObservable<HubEvent> = hubEvt.Publish :> _

    /// Fires when the hub disconnects from BLE.
    member _.Disconnected : IObservable<unit>   = disconnEvt.Publish :> _

    /// Set motor power on a port.  power in -100..100; 0 = coast.
    member _.SetMotorPower (portId: byte) (power: int) : Task =
        // Port Output Command (0x81): StartPower — immediate, no feedback (0x10)
        let pwr = byte (max -100 (min 100 power))
        write [| 0x07uy; 0x00uy; MsgPortOutputCmd; portId; 0x10uy; 0x01uy; pwr |]

    /// Brake a motor (hold position).
    member _.BrakeMotor (portId: byte) : Task =
        // StartPower with 0x7F = Brake signal
        write [| 0x07uy; 0x00uy; MsgPortOutputCmd; portId; 0x10uy; 0x01uy; 0x7Fuy |]

    /// Set the hub's built-in RGB LED.
    member _.SetLedColor (r: byte) (g: byte) (b: byte) : Task =
        // WriteDirectModeData (0x51) to the LED port, mode 1 (RGB: R, G, B bytes)
        write [| 0x0Auy; 0x00uy; MsgPortOutputCmd; ledPort; 0x10uy; 0x51uy; 0x01uy; r; g; b |]

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

/// Write bytes to the LWP characteristic (module-level helper for connect code).
let private writeAsync (char_: GattCharacteristic) (bytes: byte[]) : Task =
    task {
        use writer = new DataWriter()
        writer.WriteBytes(bytes)
        let! result =
            char_.WriteValueWithResultAsync(
                writer.DetachBuffer(),
                GattWriteOption.WriteWithoutResponse).AsTask()
        if result.Status <> GattCommunicationStatus.Success then
            printfn "  [hub88009] write failed: status=%A  protocolError=%A"
                result.Status result.ProtocolError
    }

/// Connect to a Powered Up hub at the given Bluetooth address.
let private connectAtAddressAsync (bluetoothAddress: uint64) : Task<HubConnection> =
    task {
        let! device = BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask()
        if device = null then
            failwithf "Could not obtain BLE device object for address %d" bluetoothAddress
        printfn "  [hub88009] device: %s" device.Name

        let pairing = device.DeviceInformation.Pairing
        printfn "  [hub88009] IsPaired=%b  CanPair=%b" pairing.IsPaired pairing.CanPair
        if not pairing.IsPaired && pairing.CanPair then
            printfn "  [hub88009] initiating BLE pairing…"
            let! pairResult = pairing.PairAsync(DevicePairingProtectionLevel.None).AsTask()
            printfn "  [hub88009] pair result: %A" pairResult.Status
        elif pairing.IsPaired then
            printfn "  [hub88009] already paired"

        // Hold an explicit GattSession to keep the ACL connection alive.
        let! session = GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask()
        session.MaintainConnection <- true

        do! Task.Delay(1000)  // Let ACL link and GATT service discovery settle.

        let! allSvcResult = device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask()
        printfn "  [hub88009] GATT services: status=%A  count=%d"
            allSvcResult.Status allSvcResult.Services.Count
        let service = allSvcResult.Services |> Seq.tryFind (fun s -> s.Uuid = LwpServiceGuid)
        if service.IsNone then
            failwithf "LWP GATT service not found on hub (status=%A)" allSvcResult.Status
        let service = service.Value

        let! charResult = service.GetCharacteristicsForUuidAsync(LwpCharGuid).AsTask()
        if charResult.Status <> GattCommunicationStatus.Success || charResult.Characteristics.Count = 0 then
            failwith "LWP characteristic not found"
        let char_ = charResult.Characteristics.[0]

        // Enable GATT notifications so the hub can push messages to us.
        let! cccdStatus =
            char_.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask()
        if cccdStatus <> GattCommunicationStatus.Success then
            failwithf "Failed to enable LWP notifications: %A" cccdStatus
        printfn "  [hub88009] notifications enabled"

        let conn = new HubConnection(device, char_)

        // Brief pause to let the hub finish sending its Hub Attached I/O messages.
        do! Task.Delay(500)

        // Subscribe to button presses (Hub Property 0x02) and battery level (Hub Property 0x06).
        do! writeAsync char_ (buildHubPropertiesSubscribe PropButton)
        do! writeAsync char_ (buildHubPropertiesSubscribe PropBattery)
        printfn "  [hub88009] subscribed to button and battery"

        return conn
    }

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// Scan for the first advertising Powered Up 88009 hub (LWP service UUID,
/// advertised name not containing "Remote" or "Handset") and connect to it.
/// Times out after the given duration.
let scanAndConnectHubAsync (timeout: TimeSpan) : Task<HubConnection> =
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
                        else ""
                    // Skip Remote Control / Handset devices — they share the LWP service UUID.
                    let nameLower = name.ToLowerInvariant()
                    let isRemote  = nameLower.Contains("remote") || nameLower.Contains("handset")
                    if not isRemote then
                        printfn "  [hub88009] found via advertisement: '%s' (address=%d, RSSI=%d dBm)"
                            (if name = "" then "(unknown)" else name)
                            args.BluetoothAddress
                            args.RawSignalStrengthInDBm
                        tcs.TrySetResult(args.BluetoothAddress) |> ignore)

        watcher.Start()
        let! _ = Task.WhenAny(tcs.Task :> Task, Task.Delay(timeout))
        watcher.Stop()

        if tcs.Task.IsCompleted then
            return! connectAtAddressAsync tcs.Task.Result
        else
            return failwith "No Powered Up hub found within the scan timeout"
    }
