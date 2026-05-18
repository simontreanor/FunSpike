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
    | 0x02uy -> "config"
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
               red      = int c.Red
               green    = int c.Green
               blue     = int c.Blue |}
    | Distance d ->
        box {| ``type`` = "distance"; mm = int d |}
    | Force(pct, pressed) ->
        box {| ``type`` = "force"; pct = int pct; pressed = pressed |}

let private toJson (blocks: DeviceBlock list) (snap: DeviceSnapshot) : string =
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
           ports =
               snap.Ports
               |> List.map (fun (p, r) ->
                   {| port = string p; reading = portReadingDto r |})
           blocks =
               blocks
               |> List.filter (fun b -> b.TypeByte <> 0x02uy) // skip config block (26 opaque bytes)
               |> List.map (fun b ->
                   {| typeByte = int b.TypeByte
                      typeName = blockTypeName b.TypeByte
                      raw      = toHex b.Raw |}) |}
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

// ── BLE background service ────────────────────────────────────────────────────

type HubStreamService(logger: ILogger<HubStreamService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            logger.LogInformation("Waiting for hub — press the centre button on the SPIKE Prime…")
            try
                use! hub = SpikePrime.Hub.connectFirstAsync(TimeSpan.FromSeconds 120.0, None)
                logger.LogInformation("Hub connected!")
                do! hub.InitAsync()
                do! startStreamingAsync hub
                logger.LogInformation("Streaming started — broadcasting at ~10 Hz.")
                use _sub =
                    deviceSnapshotsWithRaw hub
                    |> Observable.subscribe (fun (blocks, snap) ->
                        broadcast (toJson blocks snap))
                do! Task.Delay(Timeout.Infinite, ct)
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.LogError(ex, "Hub stream error")
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
