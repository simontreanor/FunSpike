module SpikePrime.Web.Program

open System
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open SpikePrime.Devices

// ── JSON serialisation ────────────────────────────────────────────────────────

let private jsonOpts =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let private toHex (bs: byte[]) =
    bs |> Array.map (sprintf "%02X") |> String.concat " "

let private blockTypeName = function
    | 0x00uy -> "battery"
    | 0x01uy -> "imu"
    | 0x02uy -> "matrix"
    | 0x0Auy -> "motor"
    | 0x0Buy -> "force"
    | 0x0Cuy -> "color"
    | 0x0Duy -> "distance"
    | b      -> sprintf "unknown_%02X" b

let private portReadingDto (r: PortReading) =
    match r with
    | Motor m ->
        box {| ``type`` = "motor"
               position = int m.Position
               relPos   = int m.RelativePosition
               speed    = int m.Speed
               power    = int m.Power |}
    | Color c ->
        box {| ``type`` = "color"
               colorId  = int c.ColorId
               reflect  = int c.Reflect
               red      = int c.Red   / 4  // scale 0-1023 → 0-255
               green    = int c.Green / 4
               blue     = int c.Blue  / 4 |}
    | Distance d ->
        box {| ``type`` = "distance"; mm = int d |}
    | Force(pct, pressed) ->
        box {| ``type`` = "force"; pct = int pct; pressed = pressed |}

/// Index of each port (A=0 … F=5).
let private portIndex = function
    | A -> 0 | B -> 1 | C -> 2 | D -> 3 | E -> 4 | F -> 5

/// Build port-index → IoDeviceType from all port-type notification blocks.
/// Motor block (0x0A): byte 1 = portIdx, byte 2 = LEGO device-type ID.
/// Sensor blocks (0x0B/0x0C/0x0D): byte 1 = portIdx; type fixed by block kind.
let private portDeviceTypes (blocks: DeviceBlock list) : Map<int, IoDeviceType> =
    blocks
    |> List.choose (fun b ->
        match b.TypeByte with
        | 0x0Auy when b.Raw.Length >= 3 -> Some (int b.Raw.[1], parseIoDeviceType b.Raw.[2])
        | 0x0Buy when b.Raw.Length >= 2 -> Some (int b.Raw.[1], ForceSensor)
        | 0x0Cuy when b.Raw.Length >= 2 -> Some (int b.Raw.[1], ColourSensor)
        | 0x0Duy when b.Raw.Length >= 2 -> Some (int b.Raw.[1], DistanceSensor)
        | _ -> None)
    |> Map.ofList

let private toJson (blocks: DeviceBlock list) (snap: DeviceSnapshot) : string =
    let deviceTypes = portDeviceTypes blocks
    let dto =
        {| battery     = snap.Battery |> Option.map int |> Option.defaultValue -1
           orientation = string snap.Orientation
           yaw         = int snap.Yaw
           pitch       = int snap.Pitch
           roll        = int snap.Roll
           gyroX       = int snap.GyroX
           gyroY       = int snap.GyroY
           gyroZ       = int snap.GyroZ
           accX        = int snap.AccX
           accY        = int snap.AccY
           accZ        = int snap.AccZ
           matrix      = snap.MatrixDisplay |> Option.map (Array.map int) |> Option.defaultValue [||]
           ports =
               snap.Ports
               |> List.sortBy (fun (p, _) -> portIndex p)
               |> List.map (fun (p, r) ->
                   let deviceType =
                       deviceTypes
                       |> Map.tryFind (portIndex p)
                       |> Option.map ioDeviceTypeName
                       |> Option.defaultValue ""
                   {| port       = string p
                      reading    = portReadingDto r
                      deviceType = deviceType |})
           blocks =
               blocks  // config block included — visible in raw bytes panel
               |> List.map (fun b ->
                   {| typeByte = int b.TypeByte
                      typeName = blockTypeName b.TypeByte
                      raw      = toHex b.Raw |})
           hubConnected = true |}
    JsonSerializer.Serialize(dto, jsonOpts)

// ── WebSocket client registry ─────────────────────────────────────────────────

let private clients = ConcurrentDictionary<Guid, WebSocket>()

let private broadcast (json: string) =
    let bytes = Encoding.UTF8.GetBytes(json)
    let seg   = ArraySegment<byte>(bytes)
    for kvp in clients do
        if kvp.Value.State = WebSocketState.Open then
            // Fire-and-forget: this is a single-user debug tool
            kvp.Value.SendAsync(seg, WebSocketMessageType.Text, true, CancellationToken.None)
            |> ignore

let private broadcastHubStatus (connected: bool) =
    broadcast (JsonSerializer.Serialize({| hubConnected = connected |}, jsonOpts))

// ── BLE background service ────────────────────────────────────────────────────

type HubStreamService(logger: ILogger<HubStreamService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            while not ct.IsCancellationRequested do
                logger.LogInformation("Waiting for hub…")
                try
                    use! hub = SpikePrime.Hub.connectFirstAsync(TimeSpan.FromSeconds 120.0, None)
                    logger.LogInformation("Hub connected!")
                    do! hub.InitAsync()
                    do! startStreamingAsync hub
                    logger.LogInformation("Streaming started — broadcasting at ~10 Hz.")
                    broadcastHubStatus true
                    // Per-connection CTS so a disconnect unblocks Task.Delay and loops back
                    use hubCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    use _disconnectSub =
                        hub.Disconnected
                        |> Observable.subscribe (fun () ->
                            logger.LogInformation("Hub disconnected — will reconnect.")
                            broadcastHubStatus false
                            hubCts.Cancel())
                    use _sub =
                        deviceSnapshotsWithRaw hub
                        |> Observable.subscribe (fun (blocks, snap) ->
                            broadcast (toJson blocks snap))
                    try
                        do! Task.Delay(Timeout.Infinite, hubCts.Token)
                    with :? OperationCanceledException -> ()
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    logger.LogError(ex, "Hub stream error — retrying in 3 s.")
                    broadcastHubStatus false
                    if not ct.IsCancellationRequested then
                        do! Task.Delay(3000, ct)
        }

// ── Entry point ───────────────────────────────────────────────────────────────

[<EntryPoint>]
let main _args =
    let builder = WebApplication.CreateBuilder()
    builder.Services.AddHostedService<HubStreamService>() |> ignore
    builder.Services.AddLogging(fun cfg ->
        cfg.AddSimpleConsole(fun o -> o.SingleLine <- true) |> ignore) |> ignore

    let app = builder.Build()

    // Serve the built Fable/Solid client from wwwroot/
    app.UseDefaultFiles() |> ignore
    app.UseStaticFiles() |> ignore

    // WebSocket upgrade must come before routing
    app.UseWebSockets() |> ignore

    // WebSocket endpoint — branch via middleware so the upgrade works correctly
    app.Use(Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
        task {
            if ctx.Request.Path = PathString "/ws" then
                if ctx.WebSockets.IsWebSocketRequest then
                    use! ws = ctx.WebSockets.AcceptWebSocketAsync()
                    let id  = Guid.NewGuid()
                    clients.[id] <- ws
                    try
                        let buf = Array.zeroCreate<byte> 512
                        let mutable running = true
                        while running && ws.State = WebSocketState.Open do
                            let! result = ws.ReceiveAsync(ArraySegment<byte>(buf), CancellationToken.None)
                            if result.MessageType = WebSocketMessageType.Close then
                                do! ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                                running <- false
                    finally
                        clients.TryRemove(id) |> ignore
                else
                    ctx.Response.StatusCode <- 400
            else
                return! next.Invoke(ctx)
        })) |> ignore

    app.Run()
    0
