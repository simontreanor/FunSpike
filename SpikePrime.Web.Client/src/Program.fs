open Browser
open App
open Oxpecker.Solid
open Fable.Core.JsInterop

importAll "./index.css"

// Initialise WebSocket before mounting
initWs ()

[<SolidComponent>]
let Root () =
    Fragment() {
        App()
    }

render (Root, Dom.document.getElementById "root")
