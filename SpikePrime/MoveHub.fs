module SpikePrime.MoveHub

open System
open System.Threading.Tasks
open Windows.Devices.Bluetooth
open Windows.Devices.Bluetooth.Advertisement
open Windows.Devices.Bluetooth.GenericAttributeProfile
open Windows.Devices.Enumeration
open Windows.Storage.Streams
open SpikePrime.PoweredUpProtocol
open SpikePrime.PoweredUpHub   // AttachedDevice, HubEvent, deviceName

// ---------------------------------------------------------------------------
// MoveHubConnection
// ---------------------------------------------------------------------------

/// A live BLE connection to a LEGO 88006 BOOST Move Hub.
/// Reuses HubEvent from PoweredUpHub — identical event model.
type MoveHubConnection(device: BluetoothLEDevice, char_: GattCharacteristic) =

    let hubEvt     = Event<HubEvent>()
    let disconnEvt = Event<unit>()
    let mutable ledPort = 0x32uy  // Default RGB LED port; confirmed from Hub Attached I/O

    let write (bytes: byte[]) : Task =
        task {
            use writer = new DataWriter()
            writer.WriteBytes(bytes)
            let! result =
                char_.WriteValueWithResultAsync(
                    writer.DetachBuffer(),
                    GattWriteOption.WriteWithoutResponse).AsTask()
            if result.Status <> GattCommunicationStatus.Success then
                printfn "  [hub88006] write failed: status=%A  protocolError=%A"
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
                | e when e = IoEventDetached ->
                    hubEvt.Trigger(PortDetached portId)
                | e when e = IoEventAttached && msg.Payload.Length >= 4 ->
                    let devType = uint16 msg.Payload.[2] ||| (uint16 msg.Payload.[3] <<< 8)
                    if devType = 0x0017us then ledPort <- portId  // track LED port
                    let dev = { PortId = portId; DeviceType = devType; Name = deviceName devType }
                    hubEvt.Trigger(PortAttached dev)
                | _ -> ()
            // ── Hub Properties (0x01): button / battery updates ───────────────────
            | t when t = MsgHubProperties
                     && msg.Payload.Length >= 3
                     && msg.Payload.[1] = HubPropOpUpdate ->
                match msg.Payload.[0] with
                | p when p = HubPropIdButton  -> hubEvt.Trigger(ButtonChanged  (msg.Payload.[2] = 0x01uy))
                | p when p = HubPropIdBattery -> hubEvt.Trigger(BatteryChanged (int msg.Payload.[2]))
                | _ -> ()
            | _ -> ()
        | None -> ())

    do device.add_ConnectionStatusChanged(fun dev _ ->
        if dev.ConnectionStatus = BluetoothConnectionStatus.Disconnected then
            disconnEvt.Trigger())

    /// Stream of hub events: port attach/detach, button presses, battery level.
    member _.Events     : IObservable<HubEvent> = hubEvt.Publish :> _

    /// Fires when the hub disconnects from BLE.
    member _.Disconnected : IObservable<unit>   = disconnEvt.Publish :> _

    /// Set motor power on any port.  power in -100..100; 0 = coast.
    member _.SetMotorPower (portId: byte) (power: int) : Task =
        let pwr = byte (max -100 (min 100 power))
        write [| 0x07uy; 0x00uy; MsgPortOutputCmd; portId; 0x10uy; 0x01uy; pwr |]

    /// Brake a motor (hold position).
    member _.BrakeMotor (portId: byte) : Task =
        write [| 0x07uy; 0x00uy; MsgPortOutputCmd; portId; 0x10uy; 0x01uy; 0x7Fuy |]

    /// Set the hub's built-in RGB LED.
    member _.SetLedColor (r: byte) (g: byte) (b: byte) : Task =
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

let private writeAsync (char_: GattCharacteristic) (bytes: byte[]) : Task =
    task {
        use writer = new DataWriter()
        writer.WriteBytes(bytes)
        let! result =
            char_.WriteValueWithResultAsync(
                writer.DetachBuffer(),
                GattWriteOption.WriteWithoutResponse).AsTask()
        if result.Status <> GattCommunicationStatus.Success then
            printfn "  [hub88006] write failed: status=%A  protocolError=%A"
                result.Status result.ProtocolError
    }

let private connectAtAddressAsync (bluetoothAddress: uint64) : Task<MoveHubConnection> =
    task {
        let! device = BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask()
        if device = null then
            failwithf "Could not obtain BLE device object for address %d" bluetoothAddress
        printfn "  [hub88006] device: %s" device.Name

        let pairing = device.DeviceInformation.Pairing
        printfn "  [hub88006] IsPaired=%b  CanPair=%b" pairing.IsPaired pairing.CanPair
        if not pairing.IsPaired && pairing.CanPair then
            printfn "  [hub88006] initiating BLE pairing…"
            let! pairResult = pairing.PairAsync(DevicePairingProtectionLevel.None).AsTask()
            printfn "  [hub88006] pair result: %A" pairResult.Status
        elif pairing.IsPaired then
            printfn "  [hub88006] already paired"

        let! session = GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask()
        session.MaintainConnection <- true

        do! Task.Delay(1000)

        let! allSvcResult = device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask()
        printfn "  [hub88006] GATT services: status=%A  count=%d"
            allSvcResult.Status allSvcResult.Services.Count
        let service = allSvcResult.Services |> Seq.tryFind (fun s -> s.Uuid = LwpServiceGuid)
        if service.IsNone then
            failwithf "LWP GATT service not found on Move Hub (status=%A)" allSvcResult.Status
        let service = service.Value

        let! charResult = service.GetCharacteristicsForUuidAsync(LwpCharGuid).AsTask()
        if charResult.Status <> GattCommunicationStatus.Success || charResult.Characteristics.Count = 0 then
            failwith "LWP characteristic not found"
        let char_ = charResult.Characteristics.[0]

        let! cccdStatus =
            char_.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask()
        if cccdStatus <> GattCommunicationStatus.Success then
            failwithf "Failed to enable LWP notifications: %A" cccdStatus
        printfn "  [hub88006] notifications enabled"

        let conn = new MoveHubConnection(device, char_)

        do! Task.Delay(500)

        do! writeAsync char_ (buildHubPropertiesSubscribe HubPropIdButton)
        do! writeAsync char_ (buildHubPropertiesSubscribe HubPropIdBattery)
        printfn "  [hub88006] subscribed to button and battery"

        return conn
    }

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// Scan for the first advertising BOOST Move Hub (88006) — LWP service UUID,
/// advertised name containing "move" — and connect to it.
/// Times out after the given duration.
let scanAndConnectMoveHubAsync (timeout: TimeSpan) : Task<MoveHubConnection> =
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
                    // Only accept devices whose name contains "move".
                    if name.ToLowerInvariant().Contains("move") then
                        printfn "  [hub88006] found via advertisement: '%s' (address=%d, RSSI=%d dBm)"
                            name args.BluetoothAddress args.RawSignalStrengthInDBm
                        tcs.TrySetResult(args.BluetoothAddress) |> ignore)

        watcher.Start()
        let! _ = Task.WhenAny(tcs.Task :> Task, Task.Delay(timeout))
        watcher.Stop()

        if tcs.Task.IsCompleted then
            return! connectAtAddressAsync tcs.Task.Result
        else
            return failwith "No BOOST Move Hub found within the scan timeout"
    }
