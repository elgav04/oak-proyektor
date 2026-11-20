// Entorno de DOM para los tests de la salida (happy-dom, devDependency fijada). Debe llamarse ANTES de cargar react-dom.
import { Window } from "happy-dom";

export function setupDom() {
  const win = new Window({ url: "file:///C:/oak/output.html", width: 1920, height: 1080 });
  const g = globalThis;
  g.window = win;
  g.document = win.document;
  for (const name of ["HTMLElement", "HTMLVideoElement", "HTMLImageElement", "HTMLDivElement", "Element", "Node", "Event", "MutationObserver", "SVGElement", "HTMLIFrameElement"]) {
    if (win[name]) g[name] = win[name];
  }
  g.IS_REACT_ACT_ENVIRONMENT = true;
  return win;
}

/** Marca el documento como oculto/visible (ventana minimizada) y avisa como lo haría el navegador. */
export function setHidden(win, hidden) {
  Object.defineProperty(win.document, "hidden", { configurable: true, get: () => hidden });
  win.document.dispatchEvent(new win.Event("visibilitychange"));
}

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Simula los registros de los `<video>` que crea el motor: los cuenta y observa su liberación. */
export function trackVideos(win) {
  const created = [];
  const original = win.document.createElement.bind(win.document);
  win.document.createElement = (tag, ...rest) => {
    const el = original(tag, ...rest);
    if (String(tag).toLowerCase() === "video") {
      el.__loads = 0;
      el.__plays = 0;
      const load = el.load.bind(el);
      el.load = () => { el.__loads++; load(); };
      const play = el.play.bind(el);
      el.play = () => { el.__plays++; return play(); };
      created.push(el);
    }
    return el;
  };
  return created;
}
