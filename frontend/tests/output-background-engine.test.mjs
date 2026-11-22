import assert from "node:assert/strict";
import test, { after } from "node:test";
import { setHidden, setupDom, sleep, trackVideos } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

const win = setupDom();
const created = trackVideos(win);
const { createSingleVideoEngine } = await loadTs("/src/output/background/engine.ts");
const engines = [];
after(() => { for (const e of engines) e.dispose(); });   // los motores con video en pantalla tienen un intervalo de vigilancia: sin esto el proceso de la prueba no termina
after(closeServer);

const video = (url, over = {}) => ({ kind: "video", color: "#102030", mediaUrl: url, muted: true, transitionMs: 0, retry: 0, ...over });
const image = (url, over = {}) => ({ kind: "image", color: "#102030", mediaUrl: url, muted: true, transitionMs: 0, retry: 0, ...over });
const color = (c = "#336699", ms = 0) => ({ kind: "color", color: c, mediaUrl: null, muted: true, transitionMs: ms });
const fire = (el, type) => el.dispatchEvent(new win.Event(type));
const setError = (el, code) => Object.defineProperty(el, "error", { configurable: true, value: { code } });

function setup(options = {}) {
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const failures = [];
  const engine = createSingleVideoEngine(container, { onFailure: (f) => failures.push(f), ...options });
  engines.push(engine);
  const videos = () => [...container.querySelectorAll("video")];
  const images = () => [...container.querySelectorAll("img")];
  return { container, engine, failures, videos, images, base: container.firstElementChild };
}

/** Deja el video listo (primer fotograma decodificado). */
const ready = (el) => fire(el, "loadeddata");

test("un video se crea en bucle, silenciado, con la URL validada, y se muestra al decodificar el primer fotograma", () => {
  const { engine, videos } = setup();
  engine.apply(video("file:///C:/v/a.mp4"));
  const [v] = videos();
  assert.equal(v.getAttribute("src"), "file:///C:/v/a.mp4");
  assert.equal(v.loop, true);
  assert.equal(v.muted, true);
  assert.equal(v.autoplay, true);
  assert.equal(v.style.opacity, "0");   // todavía no hay fotograma: no se muestra (queda el color de base, no un recuadro vacío)
  ready(v);
  assert.equal(v.style.opacity, "1");
});

test("apply con el MISMO medio no toca el elemento: mismo nodo, mismo currentTime, sin recargar", () => {
  const { engine, videos } = setup();
  engine.apply(video("file:///C:/v/a.mp4"));
  const [v] = videos();
  ready(v);
  v.currentTime = 12.5;
  const loadsBefore = v.__loads;
  for (let i = 0; i < 100; i++) engine.apply(video("file:///C:/v/a.mp4", { color: i % 2 ? "#ffffff" : "#000000", transitionMs: i }));
  assert.equal(videos().length, 1);
  assert.equal(videos()[0], v);
  assert.equal(v.currentTime, 12.5);
  assert.equal(v.getAttribute("src"), "file:///C:/v/a.mp4");
  assert.equal(v.__loads, loadsBefore);
});

test("solo cambia el silencio del video en pantalla, sin remontarlo", () => {
  const { engine, videos } = setup();
  engine.apply(video("file:///C:/v/a.mp4"));
  const [v] = videos();
  ready(v);
  engine.apply(video("file:///C:/v/a.mp4", { muted: false }));
  assert.equal(videos()[0], v);
  assert.equal(v.muted, false);
});

test("al cambiar de fondo se libera el video anterior DE VERDAD (pause, sin src, load, nodo fuera)", () => {
  const { engine, videos } = setup();
  engine.apply(video("file:///C:/v/a.mp4"));
  const [a] = videos();
  ready(a);
  engine.apply(video("file:///C:/v/b.mp4"));
  const b = videos().find((v) => v !== a);
  ready(b);   // al verse el nuevo, el anterior se libera (transicion 0 = ya)
  assert.equal(a.isConnected, false);
  assert.equal(a.hasAttribute("src"), false);
  assert.ok(a.__loads >= 1, "load() libera el decodificador y el archivo");
  assert.equal(a.paused, true);
  assert.deepEqual(videos(), [b]);
});

test("con fundido el anterior queda debajo, opaco, hasta que el nuevo termina de aparecer, y luego se libera", async () => {
  const { engine, videos } = setup();
  engine.apply(video("file:///C:/v/a.mp4", { transitionMs: 40 }));
  const [a] = videos(); ready(a);
  engine.apply(video("file:///C:/v/b.mp4", { transitionMs: 40 }));
  const b = videos().find((v) => v !== a); ready(b);
  assert.equal(videos().length, 2);
  assert.equal(b.style.opacity, "1");
  assert.equal(a.style.opacity, "0");   // empieza a salir; su transicion CSS dura transitionMs
  await sleep(200);
  assert.deepEqual(videos(), [b]);
  assert.equal(a.hasAttribute("src"), false);
});

test("un medio que aun no cargo y se reemplaza se libera sin llegar a verse", () => {
  const { engine, videos } = setup();
  engine.apply(video("file:///C:/v/a.mp4"));
  const [a] = videos(); ready(a);
  engine.apply(video("file:///C:/v/b.mp4"));        // b empieza a cargar...
  const b = videos().find((v) => v !== a);
  engine.apply(video("file:///C:/v/c.mp4"));        // ...y se cambia antes de que cargue
  assert.equal(b.isConnected, false);
  assert.equal(b.hasAttribute("src"), false);
  assert.equal(a.isConnected, true);                // a sigue en pantalla: no hay negro entre medias
});

test("200 cambios de fondo no acumulan nodos ni videos retenidos", async () => {
  const { engine, videos, container } = setup({ maxLeaving: 2 });
  const firstOfThisTest = created.length;
  let maxAlive = 0;
  for (let i = 0; i < 200; i++) {
    const url = `file:///C:/v/${i}.mp4`;
    engine.apply(video(url, { transitionMs: i % 3 === 0 ? 0 : 20 }));
    const loading = videos().find((v) => v.getAttribute("src") === url);
    if (i % 5 !== 0 && loading) ready(loading);   // 1 de cada 5 se reemplaza antes de cargar
    maxAlive = Math.max(maxAlive, videos().length);
    if (i % 7 === 0) engine.apply(color());        // y alterna con colores
  }
  engine.apply(video("file:///C:/v/final.mp4"));
  ready(videos().at(-1));
  await sleep(150);
  assert.equal(videos().length, 1, "solo queda el video final");
  assert.ok(maxAlive <= 4, `como mucho 1 cargando + 1 visible + 2 saliendo; hubo ${maxAlive}`);
  const final = videos()[0];
  const released = created.slice(firstOfThisTest).filter((v) => v !== final);
  assert.ok(released.length >= 200);
  for (const v of released) {
    assert.equal(v.isConnected, false);
    assert.equal(v.hasAttribute("src"), false, "src quitado");
    assert.ok(v.__loads >= 1, "load() llamado");
  }
  assert.equal(container.querySelectorAll("video, img").length, 1);
  engine.dispose();
  assert.equal(container.children.length, 0, "dispose deja el contenedor vacio");
});

test("pasar a color o a ninguno libera el video y deja el color de base", async () => {
  const { engine, videos, base } = setup();
  engine.apply(video("file:///C:/v/a.mp4", { color: "#aa0000" }));
  const [a] = videos(); ready(a);
  engine.apply(color("#00aa00", 0));
  assert.equal(videos().length, 0);
  assert.equal(a.hasAttribute("src"), false);
  assert.equal(base.style.backgroundColor, "#00aa00");
});

// ───── fallos ─────

test("error 4 (formato no soportado, p. ej. HEVC): cae al color de reserva e informa sin parpadeo", () => {
  const { engine, videos, failures, base } = setup();
  engine.apply(video("file:///C:/v/hevc.mp4", { color: "#445566" }));
  const [v] = videos();
  setError(v, 4);
  fire(v, "error");
  assert.deepEqual(failures.map((f) => [f.mediaUrl, f.reason, f.detail]), [["file:///C:/v/hevc.mp4", "unsupportedFormat", "MEDIA_ERR_SRC_NOT_SUPPORTED"]]);
  assert.equal(videos().length, 0, "el elemento roto se libera");
  assert.equal(v.hasAttribute("src"), false);
  assert.equal(base.style.backgroundColor, "#445566");   // color solido de reserva
  // el mismo error repetido no informa dos veces
  fire(v, "error");
  assert.equal(failures.length, 1);
});

test("los codigos de error del navegador se traducen a su motivo", () => {
  const cases = [[3, "decodeError"], [2, "readError"], [4, "unsupportedFormat"], [1, "readError"]];
  for (const [code, reason] of cases) {
    const { engine, videos, failures } = setup();
    engine.apply(video(`file:///C:/v/e${code}.mp4`));
    setError(videos()[0], code);
    fire(videos()[0], "error");
    assert.equal(failures[0].reason, reason, `código ${code}`);
  }
});

test("si el video que estaba en pantalla falla, se retira con fundido y queda el color de reserva", async () => {
  const { engine, videos, failures } = setup();
  engine.apply(video("file:///C:/v/a.mp4", { transitionMs: 30 }));
  const [a] = videos(); ready(a);
  setError(a, 3); fire(a, "error");
  assert.equal(failures[0].reason, "decodeError");
  assert.equal(a.style.opacity, "0");
  await sleep(150);
  assert.equal(videos().length, 0);
});

test("un fondo nuevo que falla tambien retira el anterior (la salida no muestra un video que no es el pedido)", () => {
  const { engine, videos, failures } = setup();
  engine.apply(video("file:///C:/v/a.mp4"));
  const [a] = videos(); ready(a);
  engine.apply(video("file:///C:/v/malo.mp4"));
  const b = videos().find((v) => v !== a);
  setError(b, 4); fire(b, "error");
  assert.equal(failures.length, 1);
  assert.equal(videos().length, 0);
  assert.equal(a.hasAttribute("src"), false);
});

test("tras un fallo el mismo fondo no se reintenta en bucle con cada snapshot; uno distinto si", () => {
  const { engine, videos, failures } = setup();
  engine.apply(video("file:///C:/v/malo.mp4"));
  setError(videos()[0], 4); fire(videos()[0], "error");
  const total = created.length;
  for (let i = 0; i < 20; i++) engine.apply(video("file:///C:/v/malo.mp4", { color: "#102030" }));
  assert.equal(created.length, total, "no se creo ningun video nuevo");
  assert.equal(failures.length, 1);
  engine.apply(video("file:///C:/v/bueno.mp4"));
  assert.equal(videos().length, 1);
});

/** Desactiva el control de avance (los tests de eventos no lo necesitan): intervalo enorme. */
const NO_WATCH = { watchMs: 3_600_000 };

test("stalled: solo falla si el video no avanza durante stallMs, y antes se recarga UNA vez", async () => {
  const { engine, videos, failures } = setup({ stallMs: 40, ...NO_WATCH });
  engine.apply(video("file:///C:/v/a.mp4")); ready(videos()[0]);
  fire(videos()[0], "stalled");
  await sleep(10);
  fire(videos()[0], "timeupdate");   // avanzo: era un atasco momentaneo
  await sleep(80);
  assert.equal(failures.length, 0);
  const v = videos()[0];
  const loads = v.__loads;
  fire(v, "stalled");
  await sleep(120);
  // Primer atasco real: una recarga del MISMO elemento (sin avisar al panel todavia).
  assert.equal(failures.length, 0);
  assert.equal(videos()[0], v);
  assert.equal(v.__loads, loads + 1, "se recargo");
  assert.equal(v.getAttribute("src"), "file:///C:/v/a.mp4");
  // La recarga tampoco avanza: ahora si es un fallo.
  fire(v, "stalled");
  await sleep(120);
  assert.deepEqual(failures.map((f) => f.reason), ["stalled"]);
  assert.equal(videos().length, 0);
});

test("stalled con la ventana oculta no se arma ni se juzga; al volver se vuelve a vigilar", async () => {
  const { engine, videos, failures } = setup({ stallMs: 30, ...NO_WATCH });
  engine.apply(video("file:///C:/v/a.mp4")); ready(videos()[0]);
  setHidden(win, true);
  fire(videos()[0], "stalled");
  await sleep(100);
  assert.equal(failures.length, 0);
  assert.equal(videos()[0].__loads, 0, "oculta: no se recarga nada");
  setHidden(win, false);
  fire(videos()[0], "stalled");   // visible otra vez: ahora si cuenta (recarga una vez)
  await sleep(80);
  assert.equal(videos()[0].__loads, 1);
});

test("un medio que nunca carga se recarga una vez y despues falla por tiempo de espera", async () => {
  const { engine, failures, videos } = setup({ loadTimeoutMs: 40, ...NO_WATCH });
  engine.apply(video("file:///C:/v/lento.mp4"));
  const v = videos()[0];
  await sleep(80);
  assert.equal(failures.length, 0, "el primer tiempo de espera solo recarga");
  assert.equal(v.__loads, 1);
  await sleep(100);
  assert.deepEqual(failures.map((f) => [f.reason, f.detail]), [["stalled", "load-timeout"]]);
  assert.equal(videos().length, 0);
});

// ───── H1: avance real (currentTime), carga con la ventana oculta ─────

test("H1: un video visible cuyo currentTime no avanza se detecta aunque no llegue ningun evento", async () => {
  const { engine, videos, failures } = setup({ stallMs: 40, watchMs: 10 });
  engine.apply(video("file:///C:/v/a.mp4")); const [v] = videos(); ready(v);
  v.currentTime = 5;           // congelado: ni `stalled` ni `error`
  await sleep(120);            // 1.er atasco -> recarga (todavia no es un fallo)
  assert.ok(v.__loads >= 1, "intento una recarga antes de rendirse");
  assert.equal(failures.length, 0);
  ready(v);                    // la recarga decodifica un fotograma pero el video sigue sin avanzar
  await sleep(150);
  assert.deepEqual(failures.map((f) => [f.reason, f.detail]), [["stalled", "no-progress"]]);
  assert.equal(videos().length, 0);
});

test("H1: un video que avanza (bucle incluido) nunca se da por atascado", async () => {
  const { engine, videos, failures } = setup({ stallMs: 40, watchMs: 10 });
  engine.apply(video("file:///C:/v/a.mp4")); const [v] = videos(); ready(v);
  for (let i = 0; i < 30; i++) { v.currentTime = i % 7; await sleep(10); }   // vuelve a 0 cada vuelta del bucle
  assert.equal(failures.length, 0);
  assert.equal(v.__loads, 0);
  assert.equal(videos()[0], v);
});

test("H1: el control de avance NO corre con la ventana oculta y se rearma al mostrarse", async () => {
  const { engine, videos, failures } = setup({ stallMs: 30, watchMs: 10 });
  engine.apply(video("file:///C:/v/a.mp4")); const [v] = videos(); ready(v);
  v.currentTime = 9;
  setHidden(win, true);
  await sleep(250);            // Chromium tiene el video en pausa: no es un atasco
  assert.equal(failures.length, 0);
  assert.equal(v.__loads, 0);
  setHidden(win, false);       // vuelve: la linea base se reinicia, no se acusa por el tiempo que estuvo oculta
  await sleep(15);
  assert.equal(failures.length, 0);
  assert.equal(v.__loads, 0);
  await sleep(200);            // y si sigue sin avanzar estando visible, entonces si
  assert.ok(v.__loads >= 1, "estando visible y sin avanzar, ahora si se recarga");
});

test("H1: el limite de carga NO corre con la ventana oculta: no hay falso fallo durante la pausa", async () => {
  const { engine, videos, failures } = setup({ loadTimeoutMs: 40, ...NO_WATCH });
  setHidden(win, true);
  engine.apply(video("file:///C:/v/en-pausa.mp4"));   // se cambia el fondo con la salida en pausa
  const v = videos()[0];
  await sleep(200);                                    // mucho mas que el limite
  assert.equal(failures.length, 0, "oculta: no se juzga la carga");
  assert.equal(v.__loads, 0);
  setHidden(win, false);                               // se reanuda: el limite se rearma desde cero
  await sleep(15);
  assert.equal(failures.length, 0);
  ready(v);                                            // y el video carga bien: se ve, sin color de reserva
  assert.equal(v.style.opacity, "1");
  await sleep(120);
  assert.equal(failures.length, 0);
});

test("H1: carga iniciada a la vista y ventana oculta a medias: el tiempo de espera se suspende y se rearma completo", async () => {
  const { engine, videos, failures } = setup({ loadTimeoutMs: 60, ...NO_WATCH });
  engine.apply(video("file:///C:/v/a.mp4"));
  const v = videos()[0];
  await sleep(30);
  setHidden(win, true);
  await sleep(150);
  assert.equal(failures.length, 0);
  assert.equal(v.__loads, 0);
  setHidden(win, false);
  await sleep(30);
  assert.equal(v.__loads, 0, "queda tiempo: todavia no se recarga");
  await sleep(40);
  assert.equal(v.__loads, 1, "agotado el tiempo rearmado: UNA recarga");
  assert.equal(failures.length, 0);
});

test("H1: un fallo blando que ocurre con la ventana oculta espera y se recarga UNA vez al volver a ser visible", async () => {
  const { engine, videos, failures } = setup({ loadTimeoutMs: 30, ...NO_WATCH });
  engine.apply(video("file:///C:/v/a.mp4"));
  const v = videos()[0];
  // La ventana pasa a oculta SIN evento (carrera): el temporizador de carga dispara con la ventana ya oculta.
  Object.defineProperty(win.document, "hidden", { configurable: true, get: () => true });
  await sleep(100);
  assert.equal(failures.length, 0);
  assert.equal(v.__loads, 0, "oculta: no recarga todavia");
  setHidden(win, false);       // vuelve a ser visible
  assert.equal(v.__loads, 1, "recarga UNA vez al volver");
  assert.equal(failures.length, 0, "y todavia no es un fallo");
  assert.equal(videos()[0], v);
});

test("H1: una recarga que termina bien deja el video visible y reinicia la vigilancia", async () => {
  const { engine, videos, failures } = setup({ stallMs: 40, ...NO_WATCH });
  engine.apply(video("file:///C:/v/a.mp4")); const [v] = videos(); ready(v);
  fire(v, "stalled");
  await sleep(100);
  assert.equal(v.__loads, 1);
  ready(v);                                      // la recarga decodifico
  assert.equal(v.style.opacity, "1");
  fire(v, "stalled");                            // un atasco posterior vuelve a empezar por la recarga (no es definitivo de entrada)
  await sleep(10);
  fire(v, "timeupdate");
  await sleep(80);
  assert.equal(failures.length, 0);
});

// ───── H2: reintentar el mismo archivo tras un fallo ─────

test("H2: reaplicar el MISMO archivo tras un fallo (retry mayor) lo intenta de nuevo; sin cambiar el contador no", () => {
  const { engine, videos, failures } = setup();
  engine.apply(video("file:///C:/v/usb.mp4", { retry: 0 }));
  setError(videos()[0], 3); fire(videos()[0], "error");     // se desconecto el USB
  assert.equal(failures.length, 1);
  const total = created.length;

  for (let i = 0; i < 10; i++) engine.apply(video("file:///C:/v/usb.mp4", { retry: 0 }));   // el mismo snapshot repetido: nunca en bucle
  assert.equal(created.length, total);

  engine.apply(video("file:///C:/v/usb.mp4", { retry: 1 }));  // «Reintentar» o reaplicar el archivo
  assert.equal(created.length, total + 1, "se creo un video nuevo");
  assert.equal(videos().length, 1);
  assert.equal(videos()[0].getAttribute("src"), "file:///C:/v/usb.mp4");
  ready(videos()[0]);
  assert.equal(videos()[0].style.opacity, "1");
  assert.equal(failures.length, 1, "no hay un aviso nuevo: se reproduce bien");

  // Un contador mayor con el video SANO no lo reinicia.
  const v = videos()[0]; v.currentTime = 20;
  engine.apply(video("file:///C:/v/usb.mp4", { retry: 2 }));
  assert.equal(videos()[0], v);
  assert.equal(v.currentTime, 20);
  assert.equal(created.length, total + 1);
});

test("H2: si el reintento vuelve a fallar se avisa de nuevo y se puede reintentar otra vez", () => {
  const { engine, videos, failures } = setup();
  engine.apply(video("file:///C:/v/usb.mp4"));
  setError(videos()[0], 3); fire(videos()[0], "error");
  engine.apply(video("file:///C:/v/usb.mp4", { retry: 1 }));
  setError(videos()[0], 2); fire(videos()[0], "error");
  assert.deepEqual(failures.map((f) => f.reason), ["decodeError", "readError"]);
  const total = created.length;
  engine.apply(video("file:///C:/v/usb.mp4", { retry: 2 }));
  assert.equal(created.length, total + 1);
});

test("H2: un archivo distinto no depende del contador; volver a uno anterior con retry 0 tambien funciona", () => {
  const { engine, videos } = setup();
  engine.apply(video("file:///C:/v/a.mp4", { retry: 3 }));
  setError(videos()[0], 4); fire(videos()[0], "error");
  engine.apply(video("file:///C:/v/b.mp4", { retry: 0 }));
  assert.equal(videos().length, 1);
  assert.equal(videos()[0].getAttribute("src"), "file:///C:/v/b.mp4");
});

test("H2: una imagen que fallo tambien se reintenta", () => {
  const { engine, images, failures } = setup();
  engine.apply(image("file:///C:/i/a.png"));
  fire(images()[0], "error");
  assert.equal(failures.length, 1);
  engine.apply(image("file:///C:/i/a.png", { retry: 1 }));
  assert.equal(images().length, 1);
  fire(images()[0], "load");
  assert.equal(images()[0].style.opacity, "1");
});

// ───── H7: un medio que ya esta saliendo y falla solo se libera ─────

test("H7: el video anterior que falla MIENTRAS se desvanece solo se libera: el fondo nuevo que ya se ve se queda", async () => {
  const { engine, videos, failures } = setup();
  engine.apply(video("file:///C:/v/a.mp4", { transitionMs: 500 }));
  const [a] = videos(); ready(a);
  engine.apply(video("file:///C:/v/b.mp4", { transitionMs: 500 }));
  const b = videos().find((v) => v !== a); ready(b);       // b visible, a saliendo
  assert.equal(a.style.opacity, "0");
  setError(a, 3); fire(a, "error");                          // el viejo se rompe a mitad del fundido
  assert.equal(failures.length, 0, "no es el fondo vigente: no se avisa al panel");
  assert.equal(a.isConnected, false, "el viejo se libero");
  assert.equal(b.isConnected, true);
  assert.equal(b.style.opacity, "1", "el nuevo no se retiro");
  assert.equal(videos().length, 1);
});

test("una imagen que no carga cae al color de reserva e informa imageError", () => {
  const { engine, images, failures } = setup();
  engine.apply(image("file:///C:/i/a.png"));
  const [img] = images();
  assert.equal(img.style.opacity, "0");
  fire(img, "load");
  assert.equal(img.style.opacity, "1");
  engine.apply(image("file:///C:/i/rota.png"));
  const broken = images().find((i) => i !== img);
  fire(broken, "error");
  assert.deepEqual(failures.map((f) => f.reason), ["imageError"]);
  assert.equal(images().length, 0);
});

// ───── pausa de la salida (ventana minimizada) ─────

test("al volver la ventana (tras la pausa de la salida) el video sigue: mismo nodo, se reanuda si el navegador lo pauso", () => {
  const { engine, videos } = setup();
  engine.apply(video("file:///C:/v/a.mp4")); const [v] = videos(); ready(v);
  v.currentTime = 33;
  const playsBefore = v.__plays;
  setHidden(win, true);           // salida en pausa: ventana minimizada
  v.pause(); fire(v, "pause");    // Chromium pausa los videos silenciados no visibles
  assert.equal(v.paused, true);
  setHidden(win, false);          // reanudar
  assert.equal(videos()[0], v);
  assert.equal(v.currentTime, 33);
  assert.ok(v.__plays > playsBefore, "se pidio play() de nuevo");
});

test("un pause mientras la ventana esta oculta no provoca play(); un pause estando visible si", () => {
  const { engine, videos } = setup();
  engine.apply(video("file:///C:/v/a.mp4")); const [v] = videos(); ready(v);
  setHidden(win, true);
  const n = v.__plays;
  v.pause(); fire(v, "pause");
  assert.equal(v.__plays, n);
  setHidden(win, false);
  assert.ok(v.__plays > n);
});

test("sin permiso para reproducir con sonido, reintenta silenciado", async () => {
  const { engine, videos } = setup();
  const original = win.HTMLVideoElement.prototype.play;
  let calls = 0;
  win.HTMLVideoElement.prototype.play = function () { calls++; return calls === 1 ? Promise.reject(new Error("NotAllowedError")) : original.call(this); };
  try {
    engine.apply(video("file:///C:/v/a.mp4", { muted: false }));
    await sleep(20);
    assert.equal(videos()[0].muted, true);
    assert.equal(calls, 2);
  } finally { win.HTMLVideoElement.prototype.play = original; }
});

test("dispose quita todo y deja de escuchar la visibilidad", () => {
  const { engine, videos, container } = setup();
  engine.apply(video("file:///C:/v/a.mp4")); const [v] = videos(); ready(v);
  engine.dispose();
  assert.equal(container.children.length, 0);
  assert.equal(v.hasAttribute("src"), false);
  const n = v.__plays;
  setHidden(win, false);
  assert.equal(v.__plays, n);
  engine.apply(video("file:///C:/v/otro.mp4"));   // tras dispose no hace nada
  assert.equal(container.children.length, 0);
});
