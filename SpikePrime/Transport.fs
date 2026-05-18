module SpikePrime.Transport

open System
open System.Threading
open System.Threading.Tasks
open Windows.Devices.Bluetooth
open Windows.Devices.Bluetooth.Advertisement
open Windows.Devices.Bluetooth.GenericAttributeProfile
open Windows.Devices.Enumeration
open Windows.Storage.Streams

// ---------------------------------------------------------------------------
// GATT UUIDs — LEGO SPIKE Prime stock firmware BLE service (fd02)
// ---------------------------------------------------------------------------

let [<Literal>] LegoServiceUuid = "0000fd02-0000-1000-8000-00805f9b34fb"
let [<Literal>] LegoRxCharUuid  = "0000fd02-0001-1000-8000-00805f9b34fb"  // host → hub (WriteWithoutResponse)
let [<Literal>] LegoTxCharUuid  = "0000fd02-0002-1000-8000-00805f9b34fb"  // hub → host (Notify)

let LegoServiceGuid = Guid.Parse(LegoServiceUuid)
let LegoRxGuid      = Guid.Parse(LegoRxCharUuid)
let LegoTxGuid      = Guid.Parse(LegoTxCharUuid)

// ---------------------------------------------------------------------------
// BLE scanning — find the first advertising SPIKE Prime hub
// ---------------------------------------------------------------------------

/// Scanned hub advertisement result.
type HubAdvertisement =
    { BluetoothAddress : uint64
      DeviceName       : string
      HasLegoService   : bool
      Rssi             : int16 }

/// Scan for SPIKE Prime hubs advertising the LEGO fd02 BLE service.
/// The returned IObservable emits one item per detected hub advertisement.
/// Call the returned CancellationTokenSource to stop scanning.
let scan () : IObservable<HubAdvertisement> * CancellationTokenSource =
    let cts   = new CancellationTokenSource()
    let event = Event<HubAdvertisement>()

    let watcher = BluetoothLEAdvertisementWatcher()
    watcher.ScanningMode <- BluetoothLEScanningMode.Active
    // No AdvertisementFilter — the SPIKE Prime hub name appears in the scan response
    // packet, not the initial advertisement.  We match in the handler instead.

    watcher.add_Received(fun _ args ->
        if not cts.IsCancellationRequested then
            let name =
                if args.Advertisement.LocalName <> null && args.Advertisement.LocalName.Length > 0
                then args.Advertisement.LocalName
                else ""
            let hasLegoSvc =
                args.Advertisement.ServiceUuids |> Seq.exists (fun u -> u = LegoServiceGuid)
            let advType = args.AdvertisementType
            // Emit any device that has a local name or the LEGO service UUID.
            // Callers are responsible for filtering to the hub they want.
            if name.Length > 0 || hasLegoSvc then
                event.Trigger
                    { BluetoothAddress = args.BluetoothAddress
                      DeviceName       = name
                      HasLegoService   = hasLegoSvc
                      Rssi             = args.RawSignalStrengthInDBm })

    cts.Token.Register(fun () -> watcher.Stop()) |> ignore
    watcher.Start()

    event.Publish :> IObservable<HubAdvertisement>, cts

// ---------------------------------------------------------------------------
// BLE connection
// ---------------------------------------------------------------------------

/// A live connection to a SPIKE Prime hub.
type HubConnection(device: BluetoothLEDevice, rxChar: GattCharacteristic, txChar: GattCharacteristic) =

    let mtu = 20  // Conservative ATT payload (20 bytes); hub reassembles on CR delimiter

    let dataWritten = Event<byte[]>()
    let dataReceived = Event<byte[]>()

    member _.Device         = device
    member _.RxChar         = rxChar
    member _.TxChar         = txChar

    /// Fired whenever the hub sends a notification chunk.
    member _.DataReceived   = dataReceived.Publish :> IObservable<byte[]>

    /// Write bytes to the hub, splitting into MTU-sized chunks.
    member _.WriteAsync(data: byte[], ?cancellationToken: CancellationToken) : Task =
        task {
            let ct = defaultArg cancellationToken CancellationToken.None
            let chunkSize = mtu
            let mutable offset = 0
            while offset < data.Length do
                ct.ThrowIfCancellationRequested()
                let len   = min chunkSize (data.Length - offset)
                let chunk = data[offset .. offset + len - 1]
                use writer = new DataWriter()
                writer.WriteBytes(chunk)
                let buf = writer.DetachBuffer()
                let! result = rxChar.WriteValueWithResultAsync(buf, GattWriteOption.WriteWithoutResponse).AsTask()
                if result.Status <> GattCommunicationStatus.Success then
                    printfn "  [tx] WRITE FAILED: status=%A  protocolError=%A"
                        result.Status result.ProtocolError
                offset <- offset + len
        }

    member internal this.StartNotify() : Task =
        task {
            let! status = txChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                              GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask()
            printfn "  [notify] CCCD write status = %A" status
            if status <> GattCommunicationStatus.Success then
                failwithf "Failed to enable LEGO notify characteristic: %A" status
            txChar.add_ValueChanged(fun _ args ->
                use reader = DataReader.FromBuffer(args.CharacteristicValue)
                let bytes = Array.zeroCreate<byte> (int args.CharacteristicValue.Length)
                reader.ReadBytes(bytes)
                dataReceived.Trigger(bytes))
        }

    member _.Dispose() =
        try txChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None).AsTask()
                |> ignore
        with _ -> ()
        device.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

// ---------------------------------------------------------------------------
// Connect to a hub by Bluetooth address
// ---------------------------------------------------------------------------

/// Connect to a hub by its Bluetooth address (obtained from scanning).
/// Returns a HubConnection with notifications already enabled.
let connectAsync (bluetoothAddress: uint64) : Task<HubConnection> =
    task {
        let! device = BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask()
        if device = null then
            failwithf "Could not find BLE device with address %d" bluetoothAddress
        printfn "  [connect] device object obtained: %s" device.Name

        // Pair with the hub if not already paired.  The SPIKE Prime hub requires
        // a bonded connection before it will send GATT notifications.
        // DevicePairingProtectionLevel.None = "Just Works" — no PIN dialog.
        let pairing = device.DeviceInformation.Pairing
        printfn "  [connect] IsPaired=%b  CanPair=%b" pairing.IsPaired pairing.CanPair
        if not pairing.IsPaired && pairing.CanPair then
            printfn "  [connect] Initiating BLE pairing (Just Works)..."
            let! pairResult = pairing.PairAsync(DevicePairingProtectionLevel.None).AsTask()
            printfn "  [connect] Pairing result: %A" pairResult.Status
        elif pairing.IsPaired then
            printfn "  [connect] Already paired"

        // Establish an explicit GATT session and request connection maintenance.
        // Without this, FromBluetoothAddressAsync returns a lazy object that may
        // not have an ACL link yet; GATT queries and notifications can silently fail.
        let! session = Windows.Devices.Bluetooth.GenericAttributeProfile.GattSession
                           .FromDeviceIdAsync(device.BluetoothDeviceId).AsTask()
        session.MaintainConnection <- true
        printfn "  [connect] GattSession established, MaxPduSize=%d" session.MaxPduSize

        // Small delay to let the ACL connection complete
        do! Task.Delay(1000)

        // Enumerate all services then filter by UUID.
        // GetGattServicesForUuidAsync with Uncached can return count=0 even when the
        // service is present; GetGattServicesAsync is more reliable.
        let! allSvcResult = device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask()
        printfn "  [connect] GATT services: status=%A  count=%d"
            allSvcResult.Status allSvcResult.Services.Count
        let service =
            allSvcResult.Services
            |> Seq.tryFind (fun s -> s.Uuid = LegoServiceGuid)
        if allSvcResult.Status <> GattCommunicationStatus.Success || service.IsNone then
            for svc in allSvcResult.Services do
                printfn "    service %A" svc.Uuid
                let chResult = svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().Result
                for ch in chResult.Characteristics do
                    printfn "      char %-40A  props=%A" ch.Uuid ch.CharacteristicProperties
            failwithf "LEGO fd02 service not found on hub (status=%A)" allSvcResult.Status
        let service = service.Value

        // Get write (host→hub) and notify (hub→host) characteristics
        let! rxResult = service.GetCharacteristicsForUuidAsync(LegoRxGuid).AsTask()
        if rxResult.Status <> GattCommunicationStatus.Success || rxResult.Characteristics.Count = 0 then
            failwith "LEGO write characteristic not found"
        let rxChar = rxResult.Characteristics[0]

        let! txResult = service.GetCharacteristicsForUuidAsync(LegoTxGuid).AsTask()
        if txResult.Status <> GattCommunicationStatus.Success || txResult.Characteristics.Count = 0 then
            failwith "LEGO notify characteristic not found"
        let txChar = txResult.Characteristics[0]

        printfn "  [connect] LEGO write + notify characteristics found"
        let conn = new HubConnection(device, rxChar, txChar)
        do! conn.StartNotify()
        return conn
    }

/// Returns true if a device name looks like a LEGO SPIKE Prime hub.
let private looksLikeHub (name: string) =
    not (String.IsNullOrEmpty name)
    && (name.Contains("LEGO",  StringComparison.OrdinalIgnoreCase)
        || name.Contains("Spike", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Technic", StringComparison.OrdinalIgnoreCase))

/// Search Windows' BLE device cache for a hub by name.
/// GetDeviceSelectorFromDeviceName finds devices Windows has ever seen (not just bonded).
/// Falls back to the paired-only selector so we still catch bonded devices.
/// Pass a non-empty hubName to match by exact hub name (e.g. "Apex").
let findPairedHubAsync (hubName: string) : Task<HubAdvertisement option> =
    task {
        // Build two selectors and merge results:
        //   1. By exact device name   (catches cached / previously-seen devices)
        //   2. All paired BLE devices (catches bonded devices that may have a different cache entry)
        let selectors =
            [ if hubName.Length > 0 then
                  yield BluetoothLEDevice.GetDeviceSelectorFromDeviceName(hubName)
              yield BluetoothLEDevice.GetDeviceSelector() ]

        let mutable allDevices = ResizeArray<DeviceInformation>()
        for sel in selectors do
            let! devs = DeviceInformation.FindAllAsync(sel).AsTask()
            for d in devs do
                if not (allDevices |> Seq.exists (fun x -> x.Id = d.Id)) then
                    allDevices.Add(d)

        printfn "  [cache] %d BLE device(s) in Windows cache:" allDevices.Count
        let mutable found = None
        for dev in allDevices do
            printfn "    %-35s  id=%s" dev.Name dev.Id
            let isNameMatch =
                (hubName.Length > 0 && dev.Name.Equals(hubName, StringComparison.OrdinalIgnoreCase))
                || looksLikeHub dev.Name
            if isNameMatch && found.IsNone then
                let! bleDevice = BluetoothLEDevice.FromIdAsync(dev.Id).AsTask()
                if bleDevice <> null then
                    found <- Some { BluetoothAddress = bleDevice.BluetoothAddress
                                    DeviceName       = bleDevice.Name
                                    HasLegoService   = false
                                    Rssi             = 0s }
                    bleDevice.Dispose()
        return found
    }

/// Convenience: scan until the first hub is found (with a timeout), then connect.
/// Also checks paired/system-known BLE devices (bypasses advertisement watcher).
/// Pass hubName (e.g. Some "Apex") to match paired devices by name.
let scanAndConnectAsync (timeout: TimeSpan, hubName: string option) : Task<HubConnection> =
    task {
        let name = defaultArg hubName ""
        let tcs  = TaskCompletionSource<uint64>()

        // Path 1: advertisement watcher (finds advertising hubs)
        let obs, cts = scan ()
        use _ = obs |> Observable.subscribe (fun adv ->
            if not tcs.Task.IsCompleted then
                let isHub =
                    // Match by LEGO service UUID, explicit hub name, or well-known LEGO name patterns
                    adv.HasLegoService
                    || (name.Length > 0 && adv.DeviceName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    || looksLikeHub adv.DeviceName
                if isHub then
                    printfn "Found hub via advertisement: '%s' (address=%d, RSSI=%d dBm)"
                        adv.DeviceName adv.BluetoothAddress adv.Rssi
                    tcs.TrySetResult(adv.BluetoothAddress) |> ignore)

        // Path 2: paired device lookup runs concurrently but does NOT cut the scan short.
        // It resolves the TCS if the hub is found; the advertisement watcher continues
        // regardless until the timeout or a hub is found.
        let! _ =
            task {
                let! result = findPairedHubAsync name
                match result with
                | Some adv when not tcs.Task.IsCompleted ->
                    printfn "Found hub via paired device list: %s (address=%d)"
                        adv.DeviceName adv.BluetoothAddress
                    tcs.TrySetResult(adv.BluetoothAddress) |> ignore
                | _ -> ()
            }

        // Now wait for either the advertisement watcher to find a hub, or the timeout.
        let delay = Task.Delay(timeout)
        let! _ = Task.WhenAny(tcs.Task, delay)
        cts.Cancel()
        if tcs.Task.IsCompleted then
            return! connectAsync tcs.Task.Result
        else
            return failwith "No SPIKE Prime hub found within the scan timeout"
    }
