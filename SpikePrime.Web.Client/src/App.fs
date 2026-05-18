module App

open Oxpecker.Solid
open Browser
open Fable.Core
open Fable.Core.JsInterop

// ── JSON shape — mirrors the backend DTO ─────────────────────────────────────

type IReading =
    abstract ``type``: string

type IPort =
    abstract port: string
    abstract reading: IReading
    abstract deviceType: string  // human-readable device name resolved by server (Devices.ioDeviceTypeName)

type IBlock =
    abstract typeByte: int
    abstract typeName: string
    abstract raw: string

type ISnapshot =
    abstract battery: int         // -1 = not available
    abstract orientation: string
    abstract yaw: int
    abstract pitch: int
    abstract roll: int
    abstract gyroX: int
    abstract gyroY: int
    abstract gyroZ: int
    abstract accX: int
    abstract accY: int
    abstract accZ: int
    abstract ports: IPort[]
    abstract blocks: IBlock[]

// ── Global reactive state ─────────────────────────────────────────────────────

let snapshot, setSnapshot = createSignal<ISnapshot option>(None)
let connected, setConnected = createSignal(false)

// ── WebSocket ─────────────────────────────────────────────────────────────────

[<Emit("new WebSocket($0)")>]
let private newWs (url: string) : obj = jsNative

[<Emit("JSON.parse($0)")>]
let private jsonParse<'T> (s: string) : 'T = jsNative

let initWs () =
    let url = $"ws://{Dom.window.location.host}/ws"
    let ws  = newWs url
    ws?onopen    <- fun _ -> setConnected true  |> ignore
    ws?onclose   <- fun _ -> setConnected false |> ignore
    ws?onerror   <- fun _ -> setConnected false |> ignore
    ws?onmessage <- fun (e: obj) ->
        let s : ISnapshot = jsonParse (!!e?data)
        setSnapshot (Some s) |> ignore

[<Emit("parseInt($0, 16)")>]
let private hexToInt (s: string) : int = jsNative

/// Short label for the config block annotation — resolves hex string → pybricks name.
let private configByteLabel (portLetter: string) (hexStr: string) =
    let id = hexToInt hexStr
    let name =
        match id with
        | 0    -> "empty"
        | 0x30 -> "MedMtr"
        | 0x31 -> "LgMtr"
        | 0x3D -> "Color"
        | 0x3E -> "Dist"
        | 0x3F -> "Force"
        | 0x40 -> "Matrix"
        | 0x41 -> "SAngMtr"
        | 0x4B -> "MAngMtr"
        | 0x4C -> "LAngMtr"
        | _    -> "???"
    $"{portLetter}:{name}?"

// ── Byte annotation ───────────────────────────────────────────────────────────
// Returns {| hex; lbl; known |} for each byte — highlights gaps in the raw dump.

type ByteCell = {| hex: string; lbl: string; known: bool |}

let private annotate (typeName: string) (hexBytes: string[]) : ByteCell[] =
    let get i = if i < hexBytes.Length then hexBytes.[i] else "??"
    match typeName with
    | "imu" ->
        [| {| hex=get 0;  lbl="type";    known=true  |}
           {| hex=get 1;  lbl="faceUp";  known=true  |}
           {| hex=get 2;  lbl="???";     known=false |}  // gap: unknown byte
           {| hex=get 3;  lbl="yaw lo";  known=true  |}
           {| hex=get 4;  lbl="yaw hi";  known=true  |}
           {| hex=get 5;  lbl="pit lo";  known=true  |}
           {| hex=get 6;  lbl="pit hi";  known=true  |}
           {| hex=get 7;  lbl="rol lo";  known=true  |}
           {| hex=get 8;  lbl="rol hi";  known=true  |}
           {| hex=get 9;  lbl="aX lo";   known=true  |}
           {| hex=get 10; lbl="aX hi";   known=true  |}
           {| hex=get 11; lbl="aY lo";   known=true  |}
           {| hex=get 12; lbl="aY hi";   known=true  |}
           {| hex=get 13; lbl="aZ lo";   known=true  |}
           {| hex=get 14; lbl="aZ hi";   known=true  |}
           {| hex=get 15; lbl="gX lo";   known=true  |}
           {| hex=get 16; lbl="gX hi";   known=true  |}
           {| hex=get 17; lbl="gY lo";   known=true  |}
           {| hex=get 18; lbl="gY hi";   known=true  |}
           {| hex=get 19; lbl="gZ lo";   known=true  |}
           {| hex=get 20; lbl="gZ hi";   known=true  |} |]
    | "color" ->
        [| {| hex=get 0; lbl="type";     known=true  |}
           {| hex=get 1; lbl="port";     known=true  |}
           {| hex=get 2; lbl="colorId";  known=true  |}
           {| hex=get 3; lbl="reflect?"; known=true  |}  // inferred, unconfirmed
           {| hex=get 4; lbl="red?";     known=true  |}  // inferred, unconfirmed
           {| hex=get 5; lbl="green?";   known=true  |}  // inferred, unconfirmed
           {| hex=get 6; lbl="blue?";    known=true  |}  // inferred, unconfirmed
           {| hex=get 7; lbl="???";      known=false |}  // gap
           {| hex=get 8; lbl="???";      known=false |}  // gap
           {| hex=get 9; lbl="???";      known=false |} |]  // gap
    | "motor" ->
        [| {| hex=get 0;  lbl="type";     known=true  |}
           {| hex=get 1;  lbl="port";     known=true  |}
           {| hex=get 2;  lbl="typeId";   known=true  |}  // pybricks I/O device type ID (confirmed)
           {| hex=get 3;  lbl="pos lo";   known=true  |}
           {| hex=get 4;  lbl="pos hi";   known=true  |}
           {| hex=get 5;  lbl="pwr lo";   known=true  |}
           {| hex=get 6;  lbl="pwr hi";   known=true  |}
           {| hex=get 7;  lbl="speed";    known=true  |}
           {| hex=get 8;  lbl="rP b0";    known=true  |}
           {| hex=get 9;  lbl="rP b1";    known=true  |}
           {| hex=get 10; lbl="rP b2";    known=true  |}
           {| hex=get 11; lbl="rP b3";    known=true  |} |]
    | "distance" ->
        [| {| hex=get 0; lbl="type";  known=true |}
           {| hex=get 1; lbl="port";  known=true |}
           {| hex=get 2; lbl="mm lo"; known=true |}
           {| hex=get 3; lbl="mm hi"; known=true |} |]
    | "force" ->
        [| {| hex=get 0; lbl="type";   known=true |}
           {| hex=get 1; lbl="port";   known=true |}
           {| hex=get 2; lbl="pct";    known=true |}
           {| hex=get 3; lbl="press";  known=true |} |]
    | "battery" ->
        [| {| hex=get 0; lbl="type"; known=true |}
           {| hex=get 1; lbl="bat";  known=true |} |]
    | "config" ->
        // 0x02 block: [type][header?] then 6 ports × 4 bytes (hypothesis).
        // First byte of each 4-byte entry = suspected pybricks I/O device type ID.
        let ports = [| "A"; "B"; "C"; "D"; "E"; "F" |]
        let portEntry p off =
            [| {| hex=get off;       lbl=configByteLabel p (get off); known=false |}
               {| hex=get (off+1);   lbl=p + ":???";                  known=false |}
               {| hex=get (off+2);   lbl=p + ":???";                  known=false |}
               {| hex=get (off+3);   lbl=p + ":???";                  known=false |} |]
        Array.concat
            [| [| {| hex=get 0; lbl="type"; known=true  |}
                  {| hex=get 1; lbl="hdr?"; known=false |} |]
               portEntry ports.[0] 2
               portEntry ports.[1] 6
               portEntry ports.[2] 10
               portEntry ports.[3] 14
               portEntry ports.[4] 18
               portEntry ports.[5] 22 |]
    | _ ->
        let lbl i = if i = 0 then "type" else "?"
        hexBytes |> Array.mapi (fun i b -> {| hex=b; lbl=lbl i; known=(i=0) |})

// ── Hub face cross layout ─────────────────────────────────────────────────────
// 3-column unfolded cube cross:
//   row 0:  _      Top     _
//   row 1:  Left   Front   Right
//   row 2:  _      Back    _
//   row 3:  _      Bottom  _

let private crossLabels =
    [| ""; "Top"; ""; "LeftSide"; "Front"; "RightSide"; ""; "Back"; ""; ""; "Bottom"; "" |]

let private faceDesc = function
    | "Top"       -> "matrix-display face up"
    | "Bottom"    -> "battery face up"
    | "Front"     -> "USB-port face up"
    | "Back"      -> "speaker face up"
    | "LeftSide"  -> "ports A/C/E face up"
    | "RightSide" -> "ports B/D/F face up"
    | other       -> other

let private shortLabel = function
    | "LeftSide"  -> "L"
    | "RightSide" -> "R"
    | "Bottom"    -> "BOT"
    | other       -> other

// ── Components ────────────────────────────────────────────────────────────────

[<SolidComponent>]
let OrientationGrid (orientation: string) =
    div(class'="orient-cross") {
        For(each=crossLabels) {
            yield fun label _ ->
                if label = "" then
                    div(class'="face-empty") {}
                else
                    // Inline the class expression — no let binding inside yield lambda
                    div(class'= "face " + (if label = orientation then "face-active" else "face-inactive")) {
                        shortLabel label
                    }
        }
    }

[<SolidComponent>]
let ImuPanel (snap: ISnapshot) =
    div(class'="panel imu-panel") {
        h3() { "IMU" }
        div(class'="imu-grid") {
            div(class'="imu-row") {
                span(class'="lbl") { "Yaw" }
                span(class'="val") { $"{snap.yaw}°" }
                span(class'="lbl") { "Pitch" }
                span(class'="val") { $"{snap.pitch}°" }
                span(class'="lbl") { "Roll" }
                span(class'="val") { $"{snap.roll}°" }
            }
            div(class'="imu-row") {
                span(class'="lbl") { "Acc X" }
                span(class'="val acc") { string snap.accX }
                span(class'="lbl") { "Acc Y" }
                span(class'="val acc") { string snap.accY }
                span(class'="lbl") { "Acc Z" }
                span(class'="val acc") { $"{snap.accZ} cm/s\u00B2" }
            }
            div(class'="imu-row") {
                span(class'="lbl") { "Gyro X" }
                span(class'="val") { string snap.gyroX }
                span(class'="lbl") { "Gyro Y" }
                span(class'="val") { string snap.gyroY }
                span(class'="lbl") { "Gyro Z" }
                span(class'="val") { $"{snap.gyroZ} \u00B0/s" }
            }
        }
    }

// ReadingView: match at function scope — NOT inside an HTML CE body
[<SolidComponent>]
let ReadingView (r: IReading) =
    match r.``type`` with
    | "motor" ->
        Fragment() {
            div() { $"pos {!!r?position}\u00B0" }
            div() { $"rel {!!r?relPos}\u00B0" }
            div() { $"spd {!!r?speed}%%" }
            div() { $"pwr {!!r?power}%%" }
        }
    | "color" ->
        Fragment() {
            div() { $"id  {!!r?colorId}" }
            div() { $"ref {!!r?reflect}%%" }
            div() { $"rgb ({!!r?red},{!!r?green},{!!r?blue})" }
            div(style= $"background:rgb({!!r?red},{!!r?green},{!!r?blue});width:1.2rem;height:1.2rem;border-radius:3px;margin-top:4px") {}
        }
    | "distance" ->
        Fragment() { div() { $"{!!r?mm} mm" } }
    | "force" ->
        Fragment() {
            div() { $"{!!r?pct}%%" }
            div() { if (!!r?pressed : bool) then "\u25AE pressed" else "\u25AF not pressed" }
        }
    | t ->
        Fragment() { div() { t } }

[<SolidComponent>]
let PortCard (port: IPort) =
    div(class'="port-card") {
        div(class'="port-label") { port.port }
        div(class'="port-device") { port.deviceType }
        div(class'="port-reading") {
            ReadingView port.reading
        }
    }

[<SolidComponent>]
let BlockRow (block: IBlock) =
    let bytes     = block.raw.Split(' ')
    let annotated = annotate block.typeName bytes
    div(class'="block-row") {
        span(class'="block-name") { block.typeName.ToUpper() }
        span(class'="block-size") { $" ({bytes.Length}B)" }
        div(class'="block-bytes") {
            For(each=annotated) {
                yield fun cell _ ->
                    div(class'= (if cell.known then "byte-cell" else "byte-cell byte-unknown"),
                        title = cell.lbl) {
                        div(class'="byte-hex") { cell.hex }
                        div(class'="byte-lbl") { cell.lbl }
                    }
            }
        }
    }

// ── SnapshotView ──────────────────────────────────────────────────────────────
// Extracted so the Show body never contains a `let` binding.
// (Oxpecker.Solid plugin constraint: `let` inside HTML CEs cannot be
//  converted to JSX.)
// snapshot().Value is safe here because SolidJS only mounts this component
// when Show's `when` condition (snapshot().IsSome) is true.

[<SolidComponent>]
let SnapshotView () =
    let snap = snapshot().Value
    Fragment() {
        div(class'="top-row") {
            div(class'="panel orient-panel") {
                h3() { "Orientation" }
                OrientationGrid snap.orientation
                p(class'="orient-desc") { faceDesc snap.orientation }
                p(class'="battery") {
                    if snap.battery >= 0 then $"\U0001F50B {snap.battery}%%"
                    else "\U0001F50B \u2014"
                }
            }
            ImuPanel snap
        }

        div(class'="panel") {
            h3() { "Ports" }
            Show(when'= (snap.ports.Length > 0),
                 fallback = p(class'="muted") { "No devices connected." }) {
                div(class'="ports-row") {
                    For(each=snap.ports) {
                        yield fun port _ -> PortCard port
                    }
                }
            }
        }

        div(class'="panel") {
            h3() { "Raw Device Blocks" }
            p(class'="hint") {
                "\U0001F534 red = unknown/gap bytes  \u00B7  ? suffix = inferred, not empirically confirmed"
            }
            For(each=snap.blocks) {
                yield fun block _ -> BlockRow block
            }
        }
    }

// ── App root ──────────────────────────────────────────────────────────────────

[<SolidComponent>]
let App () =
    div(class'="app") {
        header(class'="app-header") {
            span(class'="app-title") { "SPIKE Prime Visualiser" }
            // Inline conditional — no `let` binding in CE body
            span(class'= (if connected() then "badge badge-ok" else "badge badge-off")) {
                if connected() then "\u25CF Connected" else "\u25CB Disconnected"
            }
        }
        Show(when'= snapshot().IsSome,
             fallback = div(class'="waiting") { "\u23F3 Waiting for hub data\u2026" }) {
            SnapshotView ()
        }
    }
