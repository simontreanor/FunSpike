
import { createSignal } from "solid-js";
import { split, concat } from "../fable_modules/fable-library-js.5.0.0/String.js";
import { mapIndexed, item } from "../fable_modules/fable-library-js.5.0.0/Array.js";
import { int32ToString } from "../fable_modules/fable-library-js.5.0.0/Util.js";
import { value as value_4 } from "../fable_modules/fable-library-js.5.0.0/Option.js";

export const patternInput$004039 = createSignal(undefined);

export const snapshot = patternInput$004039[0];

export const setSnapshot = patternInput$004039[1];

export const patternInput$004040$002D1 = createSignal(false);

export const setConnected = patternInput$004040$002D1[1];

export const connected = patternInput$004040$002D1[0];

export function initWs() {
    const url = concat("ws://", window.location.host, ..."/ws");
    const ws = new WebSocket(url);
    ws.onopen = ((_arg) => {
        const value = setConnected(true);
    });
    ws.onclose = ((_arg_1) => {
        const value_1 = setConnected(false);
    });
    ws.onerror = ((_arg_2) => {
        const value_2 = setConnected(false);
    });
    ws.onmessage = ((e) => {
        const s = JSON.parse(e.data);
        const value_3 = setSnapshot(s);
    });
}

function annotate(typeName, hexBytes) {
    const get$ = (i) => {
        if (i < hexBytes.length) {
            return item(i, hexBytes);
        }
        else {
            return "??";
        }
    };
    switch (typeName) {
        case "imu":
            return [{
                hex: get$(0),
                known: true,
                lbl: "type",
            }, {
                hex: get$(1),
                known: true,
                lbl: "faceUp",
            }, {
                hex: get$(2),
                known: false,
                lbl: "???",
            }, {
                hex: get$(3),
                known: true,
                lbl: "yaw lo",
            }, {
                hex: get$(4),
                known: true,
                lbl: "yaw hi",
            }, {
                hex: get$(5),
                known: true,
                lbl: "pit lo",
            }, {
                hex: get$(6),
                known: true,
                lbl: "pit hi",
            }, {
                hex: get$(7),
                known: true,
                lbl: "rol lo",
            }, {
                hex: get$(8),
                known: true,
                lbl: "rol hi",
            }, {
                hex: get$(9),
                known: true,
                lbl: "aX lo",
            }, {
                hex: get$(10),
                known: true,
                lbl: "aX hi",
            }, {
                hex: get$(11),
                known: true,
                lbl: "aY lo",
            }, {
                hex: get$(12),
                known: true,
                lbl: "aY hi",
            }, {
                hex: get$(13),
                known: true,
                lbl: "aZ lo",
            }, {
                hex: get$(14),
                known: true,
                lbl: "aZ hi",
            }, {
                hex: get$(15),
                known: true,
                lbl: "gX lo",
            }, {
                hex: get$(16),
                known: true,
                lbl: "gX hi",
            }, {
                hex: get$(17),
                known: true,
                lbl: "gY lo",
            }, {
                hex: get$(18),
                known: true,
                lbl: "gY hi",
            }, {
                hex: get$(19),
                known: true,
                lbl: "gZ lo",
            }, {
                hex: get$(20),
                known: true,
                lbl: "gZ hi",
            }];
        case "color":
            return [{
                hex: get$(0),
                known: true,
                lbl: "type",
            }, {
                hex: get$(1),
                known: true,
                lbl: "port",
            }, {
                hex: get$(2),
                known: true,
                lbl: "colorId",
            }, {
                hex: get$(3),
                known: true,
                lbl: "reflect?",
            }, {
                hex: get$(4),
                known: true,
                lbl: "red?",
            }, {
                hex: get$(5),
                known: true,
                lbl: "green?",
            }, {
                hex: get$(6),
                known: true,
                lbl: "blue?",
            }, {
                hex: get$(7),
                known: false,
                lbl: "???",
            }, {
                hex: get$(8),
                known: false,
                lbl: "???",
            }, {
                hex: get$(9),
                known: false,
                lbl: "???",
            }];
        case "motor":
            return [{
                hex: get$(0),
                known: true,
                lbl: "type",
            }, {
                hex: get$(1),
                known: true,
                lbl: "port",
            }, {
                hex: get$(2),
                known: false,
                lbl: "subTyp?",
            }, {
                hex: get$(3),
                known: true,
                lbl: "pos lo",
            }, {
                hex: get$(4),
                known: true,
                lbl: "pos hi",
            }, {
                hex: get$(5),
                known: true,
                lbl: "pwr lo",
            }, {
                hex: get$(6),
                known: true,
                lbl: "pwr hi",
            }, {
                hex: get$(7),
                known: true,
                lbl: "speed",
            }, {
                hex: get$(8),
                known: true,
                lbl: "rP b0",
            }, {
                hex: get$(9),
                known: true,
                lbl: "rP b1",
            }, {
                hex: get$(10),
                known: true,
                lbl: "rP b2",
            }, {
                hex: get$(11),
                known: true,
                lbl: "rP b3",
            }];
        case "distance":
            return [{
                hex: get$(0),
                known: true,
                lbl: "type",
            }, {
                hex: get$(1),
                known: true,
                lbl: "port",
            }, {
                hex: get$(2),
                known: true,
                lbl: "mm lo",
            }, {
                hex: get$(3),
                known: true,
                lbl: "mm hi",
            }];
        case "force":
            return [{
                hex: get$(0),
                known: true,
                lbl: "type",
            }, {
                hex: get$(1),
                known: true,
                lbl: "port",
            }, {
                hex: get$(2),
                known: true,
                lbl: "pct",
            }, {
                hex: get$(3),
                known: true,
                lbl: "press",
            }];
        case "battery":
            return [{
                hex: get$(0),
                known: true,
                lbl: "type",
            }, {
                hex: get$(1),
                known: true,
                lbl: "bat",
            }];
        default: {
            const lbl_53 = (i_1) => {
                if (i_1 === 0) {
                    return "type";
                }
                else {
                    return "?";
                }
            };
            return mapIndexed((i_2, b) => ({
                hex: b,
                known: i_2 === 0,
                lbl: lbl_53(i_2),
            }), hexBytes);
        }
    }
}

const crossLabels = ["", "Top", "", "LeftSide", "Front", "RightSide", "", "Back", "", "", "Bottom", ""];

function faceDesc(_arg) {
    switch (_arg) {
        case "Top":
            return "matrix-display face up";
        case "Bottom":
            return "battery face up";
        case "Front":
            return "USB-port face up";
        case "Back":
            return "speaker face up";
        case "LeftSide":
            return "ports A/C/E face up";
        case "RightSide":
            return "ports B/D/F face up";
        default: {
            const other = _arg;
            return other;
        }
    }
}

function shortLabel(_arg) {
    switch (_arg) {
        case "LeftSide":
            return "L";
        case "RightSide":
            return "R";
        case "Bottom":
            return "BOT";
        default: {
            const other = _arg;
            return other;
        }
    }
}

export function OrientationGrid(orientation) {
    return <div class="orient-cross">
        <For each={crossLabels}>
            {(label, _arg) => ((label === "") ? <div class="face-empty" /> : <div class={"face " + ((label === orientation) ? "face-active" : "face-inactive")}>
                {shortLabel(label)}
            </div>)}
        </For>
    </div>;
}

export function ImuPanel(snap) {
    return <div class="panel imu-panel">
        <h3>
            IMU
        </h3>
        <div class="imu-grid">
            <div class="imu-row">
                <span class="lbl">
                    Yaw
                </span>
                <span class="val">
                    {`${snap.yaw}°`}
                </span>
                <span class="lbl">
                    Pitch
                </span>
                <span class="val">
                    {`${snap.pitch}°`}
                </span>
                <span class="lbl">
                    Roll
                </span>
                <span class="val">
                    {`${snap.roll}°`}
                </span>
            </div>
            <div class="imu-row">
                <span class="lbl">
                    Acc X
                </span>
                <span class="val acc">
                    {int32ToString(snap.accX)}
                </span>
                <span class="lbl">
                    Acc Y
                </span>
                <span class="val acc">
                    {int32ToString(snap.accY)}
                </span>
                <span class="lbl">
                    Acc Z
                </span>
                <span class="val acc">
                    {`${snap.accZ} cm/s²`}
                </span>
            </div>
            <div class="imu-row">
                <span class="lbl">
                    Gyro X
                </span>
                <span class="val">
                    {int32ToString(snap.gyroX)}
                </span>
                <span class="lbl">
                    Gyro Y
                </span>
                <span class="val">
                    {int32ToString(snap.gyroY)}
                </span>
                <span class="lbl">
                    Gyro Z
                </span>
                <span class="val">
                    {`${snap.gyroZ} °/s`}
                </span>
            </div>
        </div>
    </div>;
}

export function ReadingView(r) {
    const matchValue = r.type;
    switch (matchValue) {
        case "motor":
            return <>
                <div>
                    {`pos ${r.position}°`}
                </div>
                <div>
                    {`rel ${r.relPos}°`}
                </div>
                <div>
                    {`spd ${r.speed}%`}
                </div>
                <div>
                    {`pwr ${r.power}%`}
                </div>
            </>;
        case "color":
            return <>
                <div>
                    {`id  ${r.colorId}`}
                </div>
                <div>
                    {`ref ${r.reflect}%`}
                </div>
                <div>
                    {`rgb (${r.red},${r.green},${r.blue})`}
                </div>
                <div style={`background:rgb(${r.red},${r.green},${r.blue});width:1.2rem;height:1.2rem;border-radius:3px;margin-top:4px`} />
            </>;
        case "distance":
            return <>
                <div>
                    {`${r.mm} mm`}
                </div>
            </>;
        case "force":
            return <>
                <div>
                    {`${r.pct}%`}
                </div>
                <div>
                    {r.pressed ? ("▮ pressed") : ("▯ not pressed")}
                </div>
            </>;
        default: {
            const t = matchValue;
            return <>
                <div>
                    {t}
                </div>
            </>;
        }
    }
}

export function PortCard(port) {
    return <div class="port-card">
        <div class="port-label">
            {port.port}
        </div>
        <div class="port-reading">
            {ReadingView(port.reading)}
        </div>
    </div>;
}

export function BlockRow(block) {
    const bytes = split(block.raw, [" "], undefined, 0);
    const annotated = annotate(block.typeName, bytes);
    return <div class="block-row">
        <span class="block-name">
            {block.typeName.toLocaleUpperCase()}
        </span>
        <span class="block-size">
            {` (${bytes.length}B)`}
        </span>
        <div class="block-bytes">
            <For each={annotated}>
                {(cell, _arg) => <div class={cell.known ? "byte-cell" : "byte-cell byte-unknown"}
                    title={cell.lbl}>
                    <div class="byte-hex">
                        {cell.hex}
                    </div>
                    <div class="byte-lbl">
                        {cell.lbl}
                    </div>
                </div>}
            </For>
        </div>
    </div>;
}

export function SnapshotView() {
    let text_2;
    const snap = value_4(snapshot());
    return <>
        <div class="top-row">
            <div class="panel orient-panel">
                <h3>
                    Orientation
                </h3>
                {OrientationGrid(snap.orientation)}
                <p class="orient-desc">
                    {faceDesc(snap.orientation)}
                </p>
                <p class="battery">
                    {(snap.battery >= 0) ? ((text_2 = (`🔋 ${snap.battery}%`), text_2)) : ("🔋 —")}
                </p>
            </div>
            {ImuPanel(snap)}
        </div>
        <div class="panel">
            <h3>
                Ports
            </h3>
            <Show when={snap.ports.length > 0}
                fallback={<p class="muted">
                    No devices connected.
                </p>}>
                <div class="ports-row">
                    <For each={snap.ports}>
                        {(port, _arg) => PortCard(port)}
                    </For>
                </div>
            </Show>
        </div>
        <div class="panel">
            <h3>
                Raw Device Blocks
            </h3>
            <p class="hint">
                🔴 red = unknown/gap bytes  ·  ? suffix = inferred, not empirically confirmed
            </p>
            <For each={snap.blocks}>
                {(block, _arg_1) => BlockRow(block)}
            </For>
        </div>
    </>;
}

export function App() {
    return <div class="app">
        <header class="app-header">
            <span class="app-title">
                SPIKE Prime Visualiser
            </span>
            <span class={connected() ? "badge badge-ok" : "badge badge-off"}>
                {connected() ? ("● Connected") : ("○ Disconnected")}
            </span>
        </header>
        <Show when={snapshot() != null}
            fallback={<div class="waiting">
                ⏳ Waiting for hub data…
            </div>}>
            {SnapshotView()}
        </Show>
    </div>;
}

