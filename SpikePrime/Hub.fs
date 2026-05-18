module SpikePrime.Hub

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open SpikePrime.Protocol
open SpikePrime.Transport

// ---------------------------------------------------------------------------
// Hub -- wraps a HubConnection with binary protocol framing.
//
// Outgoing requests (host -> hub):
//   SendRequestAsync(typeByte, cmdId, data) encodes and writes the frame, then
//   awaits the hub''s response (matched by cmdId).
//
// Incoming frames (hub -> host):
//   - Response frames (byte[1] even) are matched to a pending SendRequestAsync call.
//   - Unsolicited frames (no pending TCS) are published on Notifications.
// ---------------------------------------------------------------------------

/// Default timeout for a single request/response round-trip (ms).
[<Literal>]
let DefaultTimeoutMs = 5000

/// A connected, ready-to-use SPIKE Prime hub.
type Hub(conn: HubConnection) =

    let decoder   = FrameDecoder()
    let pending   = ConcurrentDictionary<byte, TaskCompletionSource<HubFrame>>()
    let notifEvt  = Event<HubFrame>()

    // Subscribe to raw BLE bytes and route decoded frames.
    let subscription =
        conn.DataReceived
        |> Observable.subscribe (fun chunk ->
            for frame in decoder.Feed(chunk) do
                // If there is a pending request with this cmdId, complete it.
                match pending.TryRemove(frame.CmdId) with
                | true, tcs -> tcs.TrySetResult(frame) |> ignore
                | _         -> notifEvt.Trigger(frame))

    /// Observable stream of unsolicited frames from the hub
    /// (streaming sensor data, hub state broadcasts, unmatched responses).
    member _.Notifications : IObservable<HubFrame> = notifEvt.Publish :> _

    /// Fired when the underlying BLE device disconnects.
    member _.Disconnected : IObservable<unit> = conn.Disconnected

    /// Send the init handshake (00 00 02) and wait briefly for the hub state packet.
    member _.InitAsync() : Task =
        task {
            do! conn.WriteAsync(initMessage)
            do! Task.Delay(400)  // hub sends state packet within ~100 ms
        }

    /// Send a request and await the hub''s correlated response (matched by cmdId).
    /// Raises OperationCanceledException if no response arrives within timeoutMs.
    member _.SendRequestAsync(typeByte: byte, cmdId: byte, data: byte[], ?timeoutMs: int) : Task<HubFrame> =
        task {
            let timeout = defaultArg timeoutMs DefaultTimeoutMs
            let tcs =
                TaskCompletionSource<HubFrame>(TaskCreationOptions.RunContinuationsAsynchronously)
            pending.[cmdId] <- tcs
            do! conn.WriteAsync(encodeRequest typeByte cmdId data)
            use cts = new CancellationTokenSource(timeout)
            cts.Token.Register(fun () ->
                match pending.TryRemove(cmdId) with
                | true, t -> t.TrySetCanceled() |> ignore
                | _       -> ()) |> ignore
            return! tcs.Task
        }
    /// Send a pre-packed COBS+XOR message to the hub without awaiting a response.
    /// Use this for fire-and-forget commands such as motor control.
    member _.SendMessageAsync(packedBytes: byte[]) : Task =
        conn.WriteAsync(packedBytes)
    member _.Dispose() =
        subscription.Dispose()
        for kvp in pending do
            kvp.Value.TrySetCanceled() |> ignore
        pending.Clear()
        conn.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

// ---------------------------------------------------------------------------
// Factory
// ---------------------------------------------------------------------------

/// Scan for a SPIKE Prime hub, connect, and return a ready-to-use Hub.
/// hubName: e.g. Some "Apex" to match by name; None to take any hub found.
let connectFirstAsync (timeout: TimeSpan, hubName: string option) : Task<Hub> =
    task {
        let! conn = scanAndConnectAsync(timeout, hubName)
        return new Hub(conn)
    }