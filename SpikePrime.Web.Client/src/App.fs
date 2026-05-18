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
    abstract matrix: int[]        // 25 pixel brightness values (0–100), row-major; empty = no data
    abstract ports: IPort[]
    abstract blocks: IBlock[]

// ── Global reactive state ─────────────────────────────────────────────────────

let snapshot,     setSnapshot     = createSignal<ISnapshot option>(None)
let connected,    setConnected    = createSignal(false)   // WebSocket to server
let hubConnected, setHubConnected = createSignal(false)   // BLE hub ↔ server

// ── WebSocket ─────────────────────────────────────────────────────────────────

[<Emit("new WebSocket($0)")>]
let private newWs (url: string) : obj = jsNative

[<Emit("JSON.parse($0)")>]
let private jsonParse<'T> (s: string) : 'T = jsNative

let rec initWs () =
    let url = $"ws://{Dom.window.location.host}/ws"
    let ws  = newWs url
    ws?onopen    <- fun _ -> setConnected true |> ignore
    ws?onclose   <- fun _ ->
        setConnected false    |> ignore
        setHubConnected false |> ignore
        // Reconnect after 2 s — picks up server restarts automatically
        Dom.window.setTimeout((fun _ -> initWs()), 2000) |> ignore
    ws?onerror   <- fun _ -> setConnected false |> ignore
    ws?onmessage <- fun (e: obj) ->
        let msg : obj = jsonParse (!!e?data)
        // hubConnected is present in every server message (status or snapshot)
        setHubConnected (!!msg?hubConnected : bool) |> ignore
        // Only treat as a full snapshot when ports data is present
        if not (isNull (!!msg?ports)) then
            setSnapshot (Some (!!msg : ISnapshot)) |> ignore

// ── Byte annotation ───────────────────────────────────────────────────────────
// Returns {| hex; lbl; known |} for each byte — highlights gaps in the raw dump.

type ByteCell = {| hex: string; lbl: string; known: bool |}

let private annotate (typeName: string) (hexBytes: string[]) : ByteCell[] =
    let get i = if i < hexBytes.Length then hexBytes.[i] else "??"
    match typeName with
    | "imu" ->
        [| {| hex=get 0;  lbl="type";    known=true  |}
           {| hex=get 1;  lbl="faceUp";  known=true  |}
           {| hex=get 2;  lbl="yawRef?"; known=false |}  // always 0x00; set by reset_heading(); this codebase never calls it
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
        [| {| hex=get 0; lbl="type";    known=true  |}
           {| hex=get 1; lbl="port";    known=true  |}
           {| hex=get 2; lbl="colorId"; known=true  |}
           {| hex=get 3; lbl="reflect"; known=true  |}
           {| hex=get 4; lbl="R lo";    known=true  |}
           {| hex=get 5; lbl="R hi";    known=true  |}
           {| hex=get 6; lbl="G lo";    known=true  |}
           {| hex=get 7; lbl="G hi";    known=true  |}
           {| hex=get 8; lbl="B lo";    known=true  |}
           {| hex=get 9; lbl="B hi";    known=true  |} |]
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
    | "matrix" ->
        // 0x02 block: type byte + 25 pixel brightness values (5×5 row-major, 0–100)
        let pixelCell i =
            let row = (i - 1) / 5
            let col = (i - 1) % 5
            {| hex=get i; lbl=sprintf "r%dc%d" row col; known=true |}
        [| yield {| hex=get 0; lbl="type"; known=true |}
           yield! [| 1..25 |] |> Array.map pixelCell |]
    | _ ->
        let lbl i = if i = 0 then "type" else "?"
        hexBytes |> Array.mapi (fun i b -> {| hex=b; lbl=lbl i; known=(i=0) |})

// ── Hub 5×5 matrix display ───────────────────────────────────────────────────

[<SolidComponent>]
let MatrixDisplay (pixels: int[]) =
    div(class'="matrix-grid") {
        For(each=pixels) {
            yield fun pct _ ->
                div(class'="matrix-pixel",
                    style=(sprintf "background:rgba(250,204,21,%.2f)" (float pct * 0.01)),
                    title=(sprintf "%d%%" pct)) {}
        }
    }

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
        table(class'="imu-table") {
            thead() {
                tr() {
                    th() {}
                    th() { "Yaw" }
                    th() { "Pitch" }
                    th() { "Roll" }
                }
            }
            tbody() {
                tr() {
                    td(class'="imu-lbl") {
                        yield "Angle "
                        yield span(class'="imu-unit") { "(\u00B0)" }
                    }
                    td() { string snap.yaw }
                    td() { string snap.pitch }
                    td() { string snap.roll }
                }
            }
        }
        table(class'="imu-table imu-table-gyro") {
            thead() {
                tr() {
                    th() {}
                    th() { "X" }
                    th() { "Y" }
                    th() { "Z" }
                }
            }
            tbody() {
                tr() {
                    td(class'="imu-lbl") {
                        yield "Acc "
                        yield span(class'="imu-unit") { "(cm/s\u00B2)" }
                    }
                    td(class'="imu-acc") { string snap.accX }
                    td(class'="imu-acc") { string snap.accY }
                    td(class'="imu-acc") { string snap.accZ }
                }
                tr() {
                    td(class'="imu-lbl") {
                        yield "Gyro "
                        yield span(class'="imu-unit") { "(\u00B0/s)" }
                    }
                    td() { string snap.gyroX }
                    td() { string snap.gyroY }
                    td() { string snap.gyroZ }
                }
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
        let mm = !!r?mm : int
        Fragment() { div() { if mm = -1 then "n/a" else $"{mm} mm" } }
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
            }
            div(class'="panel matrix-panel") {
                h3() { "Display" }
                MatrixDisplay snap.matrix
            }
            div(class'="panel battery-panel") {
                h3() { "Battery" }
                p(class'="battery-emoji") { "\U0001F50B" }
                p(class'="battery-pct") {
                    if snap.battery >= 0 then $"{snap.battery}%%"
                    else "\u2014"
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
            span(class'= (if connected() then "badge badge-ok" else "badge badge-off")) {
                if connected() then "\u25CF Server" else "\u25CB Server"
            }
            span(class'= (if hubConnected() then "badge badge-ok" else "badge badge-off")) {
                if hubConnected() then "\u25CF Hub" else "\u25CB Hub"
            }
        }
        Show(when'= snapshot().IsSome,
             fallback = div(class'="waiting") { "\u23F3 Waiting for hub data\u2026" }) {
            SnapshotView ()
        }
    }
