module SpikePrime.Demo.Program

open System
open System.Threading.Tasks
open SpikePrime.Hub
open SpikePrime.Devices

[<EntryPoint>]
let main _ =
    task {
        printfn "Ready. Press the hub's centre button NOW, then wait..."
        use! hub = connectFirstAsync(TimeSpan.FromSeconds 60.0, Some "Apex")
        printfn "Connected."

        // 1. Init handshake
        printfn "\n[1] Init..."
        do! hub.InitAsync()

        // 2. Firmware version
        printfn "[2] Firmware version..."
        let! fw = getFirmwareVersionAsync hub
        printfn "    firmware: %s" (string fw)

        // 3. Hub info
        printfn "[3] Hub info..."
        let! info = getHubInfoAsync hub
        printfn "    hub info: %s" (string info)

        // 4. Subscribe streaming
        printfn "[4] Starting streaming..."
        do! startStreamingAsync hub

        // 5. Parse device snapshots for 3 seconds
        printfn "[5] Sensor stream (3 s)..."
        let mutable snapCount = 0
        use _sub =
            deviceSnapshots hub
            |> Observable.subscribe (fun s ->
                snapCount <- snapCount + 1
                if snapCount <= 10 then
                    printfn "    snap[%d]  bat=%s  yaw=%-5d pitch=%-5d roll=%-5d  face=%d"
                        snapCount
                        (s.Battery |> Option.map (sprintf "%d%%") |> Option.defaultValue "?")
                        s.Yaw s.Pitch s.Roll s.FaceUp
                    for port, reading in s.Ports do
                        match reading with
                        | Distance d         -> printfn "        port %A  distance = %d mm" port d
                        | Motor m            -> printfn "        port %A  motor pos=%d speed=%d power=%d" port m.Position m.Speed m.Power
                        | Color cid          -> printfn "        port %A  color id=%d" port cid
                        | Force(pct,pressed) -> printfn "        port %A  force=%d%% pressed=%b" port pct pressed)

        do! Task.Delay(3000)
        printfn "    %d snapshots parsed." snapCount

        printfn "\nPress Enter to exit."
        Console.ReadLine() |> ignore
        return 0
    }
    |> fun t -> t.GetAwaiter().GetResult()

