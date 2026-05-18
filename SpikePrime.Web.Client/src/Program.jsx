
import * as index from "./index.css";
import { App, initWs } from "./App.jsx";
import { render } from "solid-js/web";


initWs();

export function Root() {
    return <>
        {App()}
    </>;
}

render(Root, document.getElementById("root"));

