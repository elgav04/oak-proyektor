# Diseño de las alertas rápidas (cinta inferior en la capa 3; propuesta, sin código todavía)

Estado: **PROPUESTA pendiente de aprobación (2026-11-26).** Reglas del repo que aplican: ningún texto real de la iglesia (avisos, nombres, placas, salas…) en el repositorio, los tests, los logs ni la documentación — los ejemplos de este documento y de los tests son SINTÉTICOS («AVISO DE PRUEBA 1…»); los logs y errores llevan solo ids y conteos, nunca el texto de una alerta.

Objetivo: escribir un aviso de último minuto y verlo en pantalla en segundos, **sin interrumpir lo que se proyecta** (ni el video de fondo ni el crossfade del texto), y poder quitarlo al instante aunque el panel esté congelado.

---

## 1. Qué existe y qué falta

**Existe:**

| Pieza | Estado real (verificado en el código) |
|---|---|
| `OutputState.Overlays` (`OverlayLayer(Items)`, `OverlayItem(Id, Kind)`) | Solo el contenedor; `Items` siempre vacío. `OverlayItem` no tiene texto, tema ni nada que pintar. Lo referencian `ProtocolContractTests` (líneas 129 y 134) y `ProtocolFormatTests` (58–59). |
| `OverlayLayer.tsx` (salida) | Un `<div data-layer="overlays">` vacío con `visibility` según `visibilityFor(master).overlays`. |
| `visibilityFor` | Ya decide: `live` y `freeze` → capas visibles; `clearText`, `black`, `logo` → superposiciones OCULTAS. |
| `freeze.ts` (`nextView`) | **Congela también las superposiciones** (definición vigente en `OutputState.cs` y CLAUDE.md). Esto choca con la alerta (ver §3.4). |
| Crossfade del texto | Se dispara solo por cambio de `Slide.Id`; un snapshot con el mismo slide no lo toca. El motor de fondo depende de primitivos, no del objeto del snapshot. Es lo que permite que una alerta no los reinicie (§5). |
| Atajos globales | `GlobalHotkeys` con `HotkeyBinding(action, tecla, texto, callback)`: M emergencia, B negro, C limpiar, L logo, R recargar. Se añaden en `ApplicationHost.cs:211-215`; el estado de registro viaja ya en `OutputWindowStatus.hotkeys` (`HotkeyStatus(Action, Keys, Registered)`), así que **un atajo nuevo no cambia el formato**. |
| Carriles de entrada | `IpcPriority`: Priority (`output.setMaster`, `output.recover`), Live (por defecto), Slow. `IpcEnvelope.Order` (`OrderClock`) resuelve carreras entre carriles. |
| Patrón de escritura en segundo plano | `BackgroundCitationRecorder` (cola de 64, descarta la más antigua, tiempo de espera de escritura ≈ 1 s): lo reutilizamos para anotar alertas sin tocar el carril en vivo. |
| Ajustes persistentes | `settings.json` con carga por bloque (`SongDisplay`, `PlaylistDisplay`): valor inválido → su valor por defecto + aviso en `settings.getStatus`. |

**Falta:**

1. El modelo de una alerta en `OutputState` (texto, modo, tema, visibilidad, caducidad) y su validación (`ContentValidator`-style).
2. **Un reloj en el backend** (caducidad, repetición): hoy no hay ningún temporizador de contenido; la salida es «tonta» y no debe contar tiempo.
3. Mensajes IPC `alert.*` (protocolo **v15**), carril y orden respecto a ocultar.
4. Persistencia de alertas fijadas y recientes (migración 12) + anotador en segundo plano.
5. Ajustes de presentación (`settings.json` → `alertDisplay`).
6. La capa visual en la salida (cinta y ticker), con su propio límite de errores.
7. Atajo global de ocultar (`Ctrl+Alt+Shift+A`) y su respaldo en la página de salida.
8. Interfaz del panel: cuadro siempre accesible, lista de fijadas/recientes, insignia derivada del estado confirmado, ajustes.
9. Excepción de Freeze para la alerta y regla de la pausa (decisiones §3.4 y §3.5).

---

## 2. Presentación

**Recomendación: ambos modos, cinta estática por defecto.** El ticker existe para el texto largo, no como la forma normal de avisar.

### 2.1 Cinta estática (`mode: "band"`, por defecto)

- Franja a todo el ancho del lienzo 1920×1080, **anclada abajo** (`position: "bottom"`; opción `"top"`). Sin margen lateral (a sangre); margen inferior 0 (es un «lower third» pegado al borde; el overscan de un proyector se arregla en el proyector, no aquí).
- **Altura automática por contenido**: una línea de 52 px con relleno vertical de 22 px ≈ 100 px (9 % del alto); dos líneas ≈ 160 px (15 %); tope duro de 3 líneas (≈ 30 % = 324 px). Nunca una altura fija que recorte.
- Fondo de la cinta opaco al ~85 % (`#000000D9` por defecto) para que se lea sobre cualquier video o letra. Texto blanco, centrado.
- Entrada con un fundido corto de 200 ms (opacidad, compositor; no mueve el layout). Salida igual. Reemplazar una alerta por otra cambia el texto en el sitio (sin hueco, sin salir y volver a entrar).

### 2.2 Cinta con desplazamiento (`mode: "ticker"`)

- La misma franja (misma altura de UNA línea), texto de derecha a izquierda en bucle mientras la alerta esté visible, a **velocidad constante en px/s del lienzo** (por defecto 180 px/s; rango 60–400). Un texto de 200 caracteres a 52 px ≈ 5 200 px ≈ 29 s por pasada.
- Animación CSS `transform: translateX` (compositor; no fuerza layout por fotograma). La duración se calcula UNA vez al montar (`ancho del texto / velocidad`) con una medición de `getBoundingClientRect`. El elemento tiene la **clave del id de la alerta**: un snapshot nuevo con la misma alerta no reinicia la animación.
- No hay «una sola pasada»: la alerta dura lo que diga su temporizador (§3). Si el operador quiere una pasada, elige una duración ≈ una pasada (el panel muestra «≈ 29 s por pasada»).

### 2.3 Tema propio (`AlertTheme`, independiente del tema de canciones y de la Biblia)

`fontFamily` (Arial por defecto), `fontSize` (52), `color` (#FFFFFF), `outlineWidth/outlineColor` (0 / #000000), `bandColor` (`#RRGGBBAA`, `#000000D9`), `align` (centro; solo cinta estática), `position` (`bottom`|`top`), `paddingY` (22), `maxLines` (2, 1–3), `autoFit` + `minFontSize` (por defecto `floor(52 × 0,6)` = 31; **siempre menor que el tamaño**, igual que `safeMinFontSize`), y `tickerSpeed` (180). Se valida en el backend ANTES de guardar o mostrar (colores `#RRGGBB[AA]`, fuente sin caracteres de CSS, rangos); un valor inválido → `alert.invalidSettings` / `alert.invalidContent`. Se guarda en `settings.json` (`alertDisplay`), carga por bloque con aviso. **No hay tema por alerta** (fuera de alcance, §7). La alerta lleva una COPIA del tema tomada al mostrarla (como el `Slide`): cambiar los ajustes no altera la que está en pantalla; se aplica a la siguiente.

### 2.4 Textos largos

Regla (sin trucos silenciosos, sin partir en dos alertas):

1. **Límite duro de 300 caracteres** (y sin caracteres de control) en el backend. Más → `alert.invalidContent` («Aviso demasiado largo»).
2. **Cinta estática**: el texto se ajusta a `maxLines` (2) a `fontSize`; si no cabe, `autoFit` baja la letra hasta `minFontSize`; si aun así no cabe, la cinta crece hasta 3 líneas (tope 30 % del alto).
3. **El panel mide antes de mostrar** (misma técnica de `bible/measure.ts`: reproduce la cinta fuera de pantalla en un lienzo de 1920×1080 y pregunta al layout) y muestra en la vista previa del cuadro «Cabe en 2 líneas / Cabe reduciendo la letra a 38 px / **No cabe: usa desplazamiento**» con un botón que cambia el modo. Si no cabe ni a 3 líneas con el mínimo, **«Mostrar» queda desactivado en modo cinta** con ese motivo (no se recorta nunca el aviso).
4. **Se descarta partir automáticamente en dos alertas**: un aviso cortado en mitad («Se busca al dueño del auto…» / «…placa XXX») que sale con dos pulsaciones o con una cola es justo el error que no queremos en vivo. Si el operador quiere dos mensajes, los escribe como dos alertas y cambia entre ellas (reemplazo instantáneo).
5. Umbral orientativo: ≤ ~140 caracteres → cinta de 2 líneas sin reducir; 140–300 → el panel sugiere desplazamiento (una sugerencia, no una imposición).

**Valores por defecto recomendados:** modo cinta estática, abajo, 52 px, Arial, blanco sobre `#000000D9`, 2 líneas, duración 30 s, ticker a 180 px/s.

**Aviso honesto:** abajo la cinta TAPA la parte inferior del contenido (la letra de una canción centrada verticalmente puede quedar parcialmente cubierta, y la referencia de un versículo suele ir al final). No se reflota ni se empuja el contenido (eso sería «interrumpir»): el operador elige «arriba» si lo ve necesario, y la cinta de 1 línea ocupa solo 9 %.

---

## 3. Ciclo de vida y reglas

### 3.1 Estado en el backend (única fuente de verdad)

El estado de la alerta vive en `OutputState.Overlays.Alert` (sustituye a `Items`, ver §6.1). Un **`AlertScheduler`** (Core, máquina de estados PURA con reloj inyectado y marca de tiempo MONÓTONA, no la hora del sistema: un cambio de hora de Windows no debe alargar ni acortar un aviso) decide cuándo se oculta/repite; una pieza delgada de App arma UN temporizador de un disparo hacia el próximo plazo. Cada `show` incrementa una **generación**; un disparo de un temporizador viejo no puede ocultar la alerta nueva (carrera clásica; test dedicado). Todo callback captura sus excepciones. El estado de la alerta **NO se persiste**: reiniciar la aplicación nunca revive un aviso.

### 3.2 Mostrar / ocultar / duración

- **Mostrar** = `alert.show` con el texto (y modo, duración, repetición opcionales; si faltan, los de `alertDisplay`).
- **Ocultar** = `alert.hide` (botón, atajo del panel, o el atajo global).
- **Duración**: `durationSec` ∈ {15, 30, 60, 120, 300} o «hasta quitarla» (`null`). **Por defecto 30 s**: la alerta olvidada es el peor caso, así que lo predeterminado es finito. «Hasta quitarla» tiene un **tope de cordura de 30 min** (`MaxStickySeconds = 1800`): ningún aviso queda para siempre; el panel muestra el tiempo transcurrido y a los 10 min lo resalta.
- **Repetición (opcional, apagada por defecto)**: `repeat = { gapSec, times }`: se muestra `durationSec`, se oculta `gapSec` (10–600 s) y se repite hasta `times` veces (2–20). Entre repeticiones la alerta SIGUE «activa» en el estado con `visible = false` (para que el panel diga «Esperando repetición 2 de 5» y se pueda quitar del todo). Solo se admite con duración finita. Cada cambio de fase es un snapshot completo, como cualquier otro. **Esta parte es separable**: si el prompt 1 crece, se difiere sin afectar al resto (el modelo ya la contempla).
- **Re-mostrar el mismo texto con el mismo modo y tema mientras ya está visible** reinicia el temporizador **sin cambiar de `id`** (la salida no parpadea ni reinicia el ticker).

### 3.3 Varias alertas a la vez

**Una sola alerta activa; mostrar otra REEMPLAZA a la actual al instante** (sin cola, sin lista activa). Razones: dos cintas superpuestas son ilegibles; una cola hace que un aviso viejo salga minutos después sin que nadie lo recuerde (el error que hay que evitar); y es lo más simple y predecible. El panel muestra siempre qué hay en pantalla, de modo que reemplazar es una decisión visible («Reemplazará: «…»»). Una rotación de 2–3 alertas queda como posible ampliación futura (§8, pregunta 7).

### 3.4 Controles maestros

| Modo | La alerta | Detalle |
|---|---|---|
| **Live** | Visible | |
| **Limpiar texto** | **Oculta** | Ya lo dice `visibilityFor` (`overlays: false`): el operador espera una pantalla limpia. El estado y el temporizador siguen: si caduca mientras tanto, no reaparece; una «hasta quitarla» vuelve al pasar a Live. El panel dice «ALERTA OCULTA · Texto limpio». |
| **Negro** | Oculta | Negro total. Igual que arriba. |
| **Logo** | Oculta | Igual. |
| **Freeze** | **Sigue activa: la alerta NO se congela** | **Excepción propuesta a la definición de Freeze** (hay que aprobarla): Freeze existe para sostener lo que estaba pintado si algo va mal; si también congelara la alerta, mostrarla u OCULTARLA no tendría efecto (ni siquiera el atajo global) y un aviso quedaría clavado en pantalla. Implementación: en `nextView`, al congelar, `shown = { ...prev.shown, overlays: snap.state.overlays }`; texto, slide y fondo siguen congelados. Se actualizan `OutputState.cs`, `freeze.ts`, `visibility.ts` y CLAUDE.md. |

Cambiar de modo sigue siendo instantáneo y nunca espera a nada (la alerta se oculta con `visibility`, no con una transición que bloquee).

### 3.5 Pausa de la salida

**Propuesta: una alerta no sobrevive a ninguna pausa** (emergencia, manual, monitor perdido, panel cruzado, modo Duplicar, `unstable`): al entrar en pausa el controlador quita la alerta (`ClearAlertLocked`) y avisa al panel («La alerta se quitó porque la salida se pausó»). Razón: tras reanudar (a veces solos, a veces minutos después) un aviso que el operador ya dio por perdido NO debe reaparecer. Coste: dos puntos de llamada en el camino de pausa (`PauseLocked` y `EmergencyPause`), que es código crítico — por eso la llamada es una línea bajo `_gate`, sin I/O, cubierta por tests (`OutputPolicyScenarioTests` no cambia). **Alternativa más conservadora** (pregunta 1): no tocar la pausa; la alerta sigue contando su tiempo (reloj monotónico) y, si aún no caducó, reaparece al reanudar.

### 3.6 Otros

- Cerrar la ventana de salida: el estado de la alerta se conserva con el resto del `OutputState` (como el slide) y sigue su reloj; al abrirla se pinta el estado vigente.
- El panel recargado (Ctrl+Alt+Shift+R) recupera la alerta de `output.getState`.
- Con la salida sin ventana nadie ve la alerta: el panel la marca «Salida cerrada».

---

## 4. Edición rápida y almacenamiento

### 4.1 Cuadro siempre accesible

Una **barra de alertas fija en el marco del panel** (en `App`, bajo las pestañas y visible en TODAS ellas, igual que el aviso de fallo de fondo): una fila con el cuadro de texto, **[Mostrar]**, **[Quitar]** y un botón para desplegar la lista. Contador de caracteres (`n / 300`), selector de modo (cinta / desplazamiento) y duración, con los valores por defecto de `alertDisplay`; vista previa a escala sobre el lienzo real al desplegar.

- **`Ctrl+Mayús+K`** enfoca el cuadro desde cualquier pestaña, incluso desde un cuadro de texto (`whileTyping`, como Ctrl+Mayús+A de la lista). **Enter** muestra; tras mostrar, el cuadro **suelta el foco** (las teclas de la pestaña —flechas del servicio— siguen funcionando) y conserva el texto con una marca «EN PANTALLA». **Esc** en el cuadro sale de él; **`Ctrl+Mayús+H`** quita la alerta desde el panel (con o sin foco en un cuadro). Ambos se verifican con teclado REAL antes de aprobarse (como se hizo con Ctrl+E, Ctrl+Mayús+A…): WebView2 reserva algunas combinaciones (p. ej. Ctrl+Mayús+Q es «salir» en Chromium, por eso NO se propone); si alguna no llega a la página se elige otra de la lista de candidatas. Nunca Ctrl+Alt+Shift (los globales); `RESERVED_GLOBAL_SHORTCUTS` gana la «A».
- Los atajos del panel se añaden al mapa único `shortcuts.ts`; un test comprueba que no chocan con los globales ni con los de las pestañas.

### 4.2 Fijadas y recientes (clic para repetir)

Sección «Fijadas ★» (las que el operador marcó; **orden alfabético estable**, no heurísticas ocultas) y «Recientes» (las últimas 10 mostradas en los últimos 14 días, la más nueva primero, con su antigüedad: «hace 3 d»). Cada fila:

- **Clic en el texto = carga la alerta en el cuadro** (para revisar o editar; NO la muestra).
- **Botón ▶ de la fila = mostrar YA** (un clic), con el reemplazo visible en la barra.
- ★ fija/desfija; ✕ borra.

Así «repetir con un clic» existe, pero un clic suelto en una fila no pone un aviso viejo en el proyector.

### 4.3 Dónde se guardan (migración 12) y cómo se evita un aviso viejo por error

`Data/Migrations/0012_Alerts.sql` (nunca se edita una publicada; se añade su hash a `MigrationHashTests`; es una tabla NUEVA, no toca nada existente):

```
Alerts(Id INTEGER PRIMARY KEY, Text TEXT NOT NULL CHECK (length(Text) BETWEEN 1 AND 300),
       TextKey TEXT NOT NULL UNIQUE,            -- NFC + espacios colapsados + minúsculas: «Aviso  1» y «aviso 1» son la misma
       Pinned INTEGER NOT NULL DEFAULT 0 CHECK (Pinned IN (0,1)),
       UseCount INTEGER NOT NULL DEFAULT 0, LastUsedAt TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)
INDEX (Pinned, LastUsedAt)
```

- **Límites**: ≤ 30 fijadas (`alert.full`), ≤ 50 no fijadas guardadas (al anotar se podan las más antiguas por `LastUsedAt`, en la misma transacción). El texto de una alerta es dato del usuario: vive SOLO en su base local, nunca en el repositorio.
- **Anotar al mostrar, fuera del carril en vivo**: `alert.show` NO toca la BD. El controlador encola el texto en un `BackgroundAlertRecorder` (patrón de `BackgroundCitationRecorder`: un consumidor, cola de 64 que descarta la más antigua, bloqueo de escritura ≈ 1 s; si no se logra, se descarta con un aviso en el log SIN texto). Escritura = `INSERT … ON CONFLICT(TextKey) DO UPDATE SET LastUsedAt…, UseCount = UseCount + 1` (nunca `REPLACE`), transacción diferida con la escritura primero, conexiones de la fábrica con `OpenWhenReadyAsync`. Un fallo de la BD no impide mostrar la alerta.
- **Evitar el aviso viejo por error**: (a) NADA se muestra solo: ni al abrir la aplicación ni al reconectar el panel; (b) el estado de la alerta no se persiste; (c) «recientes» envejece (14 días y 10 filas) y se poda (50); (d) cada fila lleva su antigüedad; (e) las FIJADAS son explícitas y están pensadas para avisos perennes («Apague su celular», «Baños al fondo»), no para los del día; (f) caducidad por defecto de 30 s y tope de 30 min; (g) lo que está en pantalla es siempre visible en la barra con un botón Quitar; (h) mostrar desde la lista exige el ▶ (§4.2).
- No hay papelera: la red de seguridad es el respaldo diario, como en canciones. Editar el texto de una guardada no usa revisión (no destruye nada y el último en guardar gana); borrar es idempotente.

---

## 5. Seguridad en vivo

1. **Nada lento por el carril en vivo.** `alert.show` y `alert.hide` solo tocan memoria (validación + estado + publicar snapshot) y van por el carril **Priority**, junto a `output.setMaster`/`recover`: así `show` y `hide` comparten FIFO (no se cruzan) y nunca esperan detrás de un fondo en un USB lento. Lo que toca disco va aparte: guardar/borrar/fijar alertas por **Slow**; lecturas (`alert.list`, `alert.getSettings`) por **Live** (consulta corta, como `playlist.list`); anotar el uso, en el hilo de fondo. `Every_catalog_message_has_a_deliberate_lane` se actualiza.
2. **Carrera con el atajo global**: `Ctrl+Alt+Shift+A` actúa directo en el controlador (como `ToggleMaster`), tomando su número de `OrderClock` al pulsarse; el controlador recuerda el último número de ocultar y **ignora un `alert.show` con número menor** (mismo mecanismo que la pausa de emergencia frente a «Mover/reabrir»). Así un `show` ya encolado no puede revivir una alerta que el operador acaba de quitar.
3. **No reinicia el video ni el crossfade**: la alerta cambia SOLO `overlays` del snapshot. `Slide.Id` y el fondo no cambian → el crossfade no se dispara (`ContentLayer` se dispara por `Slide.Id`) y el motor de fondo (efecto de primitivos) no recibe nada. El elemento de la alerta está en su propio contenedor (`OverlayLayer`, clave estable) sin tocar los nodos de contenido ni del `<video>`. Estos puntos tienen pruebas (§9), incluida la real: mismo nodo de video, `currentTime` continuo, fotogramas presentados durante el ticker.
4. **Aislamiento de errores**: la alerta va dentro de su PROPIO límite de errores (`AlertErrorBoundary`) dentro de `OutputForeground`. Un error de render de la alerta la oculta (y la reporta con su id, sin texto) pero **no** sustituye al contenido por el último estado pintado ni toca el fondo; hoy un error en la capa 3 caería en el boundary general y afectaría al contenido.
5. **Animaciones baratas**: solo `opacity` y `transform` (compositor). Sin `filter`, sin `box-shadow` animado. La sombra del texto es estática. Medición real de fotogramas del video con el ticker activo.
6. **Atajo global de emergencia: `Ctrl+Alt+Shift+A` = QUITAR la alerta** (solo quita; **nunca** la muestra ni la «vuelve a mostrar»: un interruptor podría resucitar un aviso viejo, al contrario que B/C/L, cuyo regreso es seguro). Evaluación de encaje: la «A» está libre (usadas: M, B, C, L, R); mismo modificador, mismo hilo de atajos, mismo informe de registro (`HotkeyStatus`, el panel avisa si otra aplicación la tiene); no pasa por el JavaScript del panel y funciona con el panel congelado (se añade a `tools/i8-frozen-panel-test.ps1 -Full`, que ya pulsa teclas reales). **Respaldo**: la misma combinación dentro de la página de salida envía `alert.hide` (como la de emergencia envía `output.recover`), por si el global no pudo registrarse. Como todo atajo global, no se puede probar con eventos sintéticos: comprobación manual con teclado real, repetirla al tocar `GlobalHotkeys`.
7. **Reloj**: temporizador de un disparo a la vez, con la generación de §3.1; deadlines con marca monótona; el sondeo de 500 ms existente NO se reutiliza (no mezclar con la política de monitores).
8. Registros: solo `alertId`, modo, duración, conteos y fase (`Alerta {AlertId} mostrada: modo {Mode}, {Duration} s`). Eventos y errores sin texto. El texto sí viaja en `alert.show`, `alert.list` y en el snapshot de la salida (como el texto de un slide): es lo que se proyecta.

---

## 6. Modelo y protocolo (versión siguiente: **v15**)

### 6.1 Estado

`OverlayLayer(Items)` + `OverlayItem` se SUSTITUYEN por `OverlayLayer(AlertOverlay? Alert)` (no hay ticker genérico ni otras superposiciones planeadas; dejar `Items` vacío sería peso muerto y un formato que nadie usa). `OutputState.SchemaVersion` pasa a 3 (cambia el significado de `overlays`).

```
AlertOverlay(Id: string, Text: string, Mode: "band"|"ticker", Theme: AlertTheme,
             Visible: bool,                       // false = entre repeticiones (sigue «activa»)
             ExpiresAt: string?,                  // ISO-8601 UTC; SOLO informativo para la cuenta atrás del panel; la salida lo ignora
             Cycle: int, Cycles: int)             // 1/1 si no hay repetición
```

`OutputState.WithAlert(AlertOverlay?)` devuelve la misma instancia si es igual (no se publica un snapshot repetido). `visibilityFor` no cambia.

### 6.2 Mensajes

| Mensaje | Tipo | Carril | Notas |
|---|---|---|---|
| `alert.show` | command | **Priority** | `{ text, mode?, durationSec? (null = hasta quitarla), repeat? { gapSec, times }, savedId? }`. Validación en el backend; fallo → `command.failed` con `alert.invalidContent` / `alert.invalidSettings`. El resultado es el snapshot (`output.stateChanged`). |
| `alert.hide` | command | **Priority** | Sin payload; idempotente. |
| `alert.getSettings` | query | Live | `{ settings: AlertDisplaySettings }`. |
| `alert.setSettings` | command | Live | Sustituye el conjunto, validado ANTES de guardar. |
| `alert.list` | query | Live | `{ pinned: SavedAlert[], recent: SavedAlert[] }`; `SavedAlert { id, text, pinned, useCount, lastUsedAt }`. ≤ 80 filas: sin virtualizar. |
| `alert.save` | command | Slow | `{ id?, text, pinned }` (crear, editar o fijar); `alert.full` si se pasa de 30 fijadas. |
| `alert.delete` | command | Slow | `{ id }`; idempotente. |
| `alert.changed` | event | — | `{ commandId, alertId, kind: saved\|deleted }` — SOLO ids; el panel relee `alert.list`. |

Total: 5 comandos, 2 consultas, 1 evento (= 8). Códigos de error `alert.*` en `Core/Alerts/AlertErrorCodes` (`alert.invalidContent`, `alert.invalidSettings`, `alert.notFound`, `alert.full`, `alert.busy`). Habrá que: subir `IpcCatalog.ProtocolVersion` y `PROTOCOL_VERSION` a 15; añadir `PinnedFormat[15]` (la huella de 14 no se toca); reflejar los tipos en `frontend/src/ipc/`; actualizar `ProtocolContractTests`/`ProtocolFormatTests` (se quitan `OverlayItem`, se añaden los records nuevos con su dirección); **actualizar el `v:14` escrito a mano en 7 scripts de `tools/`** (`bible-layout-test`, `i8-frozen-panel-test`, `i8-reload-isolation-test`, `list-edit-test`, `list-live-test`, `media-test`, `song-layout-test`; si no, el backend ignora sus comandos en silencio) y repetir los que cambian de comportamiento.

### 6.3 Ajustes (`settings.json` → `alertDisplay`)

`mode` (band), `durationSec` (30), `theme` (§2.3), `tickerSpeed` (180). Carga por bloque con aviso (`settings.getStatus`), escritura atómica durable como el resto.

---

## 7. Fuera de alcance

Audio/sonido de aviso; presentaciones personalizadas y PowerPoint; alertas con imágenes, logos o formato enriquecido; tema por alerta; cola o rotación de alertas; programar una alerta a una hora; alertas en Stage Display (no se implementa); superposiciones distintas de la alerta (reloj, contador…); sincronizar alertas entre equipos; traducción o plantillas con variables.

---

## 8. Riesgos y preguntas abiertas

**Riesgos**

1. **La cinta tapa contenido** (§2.4): letra inferior o la referencia bíblica. Mitigación: 1 línea por defecto en lo posible, opción «arriba», fondo opaco legible. No hay solución sin «interrumpir».
2. **Tocar el camino de pausa** (§3.5) y **cambiar la definición de Freeze** (§3.4): ambos son código/semántica documentada y crítica; van acotados y con pruebas, pero necesitan aprobación expresa.
3. **Ticker y rendimiento** en una GPU modesta: se mide (fotogramas del video presentados con y sin ticker en `tools/alert-test.ps1`); si degrada el video, el ticker se difiere y queda solo la cinta estática.
4. **Reloj**: cambios de hora de Windows (se usa marca monótona); suspensión del equipo (la salida impide suspender mientras está abierta); temporizador que dispara tarde bajo carga (la caducidad es «≥», nunca antes).
5. **El panel mide, el backend no**: un texto que no cabe solo lo detecta la medición del panel; la salida tiene la red de seguridad (autofit + tope de altura), no un recorte garantizado ausente. Se cubre con un caso de 300 caracteres a escalas 1,0/1,5/2,0 en la prueba real.
6. **Errores de render de la capa 3** (§5.4) y el tamaño de los snapshots (cada fase de repetición es un snapshot completo; el `Outbox` compacta si se llena).
7. **Datos del usuario**: el texto de las alertas se guarda en su base local (que nunca se versiona); si alguna vez el aviso de carpeta sincronizada aparece, esas notas viajarían con ella (ya documentado para la BD).
8. **Reutilizar `OverlayItem`**: quitarlo cambia el formato y los tests de contrato; es deliberado y solo cabe en una versión nueva del protocolo.

**Preguntas abiertas (decisiones tuyas)**

1. **Pausa**: ¿una alerta nunca sobrevive a una pausa (recomendado) o prefieres no tocar el camino de pausa y que reaparezca al reanudar si no caducó?
2. **Freeze**: ¿apruebas la excepción (la alerta NO se congela)?
3. **Tope de «hasta quitarla»**: ¿30 min te parece bien (0 = sin tope no se recomienda)?
4. **Repetición** en el primer prompt, o la dejamos para un tercero si crece.
5. **Ticker y cinta** en la v1 (recomendado), o solo la cinta estática y el ticker después.
6. **Atajos del panel** `Ctrl+Mayús+K` (enfocar) y `Ctrl+Mayús+H` (quitar): ¿te sirven? Se confirman con teclado real; hay alternativas.
7. **Una sola alerta activa** (recomendado) o una pequeña rotación de 2–3 alertas.
8. **`Ctrl+Alt+Shift+A` solo quita** (recomendado) o prefieres interruptor.
9. **Duración por defecto 30 s** y **límite de 300 caracteres**: ¿correctos para vuestros avisos?
10. **Posición por defecto abajo**: ¿o arriba, dado lo que cubre?

---

## 9. Plan de pruebas

**C# (xUnit; solo texto SINTÉTICO):**
- `AlertRulesTests`: validador (vacío, 300/301 caracteres, control, tema con colores/fuente/rangos inválidos, `minFontSize < fontSize`, duración fuera del conjunto, repetición inválida, tope de «hasta quitarla»); `OutputState.WithAlert` idempotente (misma instancia) y no toca `Content`/`Background`/`Master`.
- `AlertSchedulerTests` (reloj falso): caducidad exacta, reemplazo invalida el plazo viejo (generación), re-mostrar el mismo texto conserva el id y reinicia el plazo, repetición (fases, `times`, fin), «hasta quitarla» con tope, ocultar idempotente, plazo ya vencido al armar, disparo viejo tras `hide`+`show`.
- `OutputControllerAlertTests`: cada modo maestro con alerta (estado conservado, visibilidad), pausa de cada motivo quita la alerta (y no cambia el resto de la política: `OutputPolicyScenarioTests` intacta), hotkey de ocultar vs un `show` encolado con número menor, ventana cerrada, `show` con la salida en Freeze.
- `AlertRepositoryTests`: migración v11 → v12 sin tocar nada; alta, dedup por `TextKey` (mayúsculas, espacios, NFC), `UseCount`, poda a 50, límite de 30 fijadas, fijar/desfijar, borrar idempotente, bloqueo de escritura retenido (`alert.busy`), cola del anotador (descarta la más antigua; un fallo no impide `show`), privacidad de los registros (ningún texto).
- `AlertIpcTests`: de extremo a extremo `show` → snapshot → `hide`; `alert.show` y `alert.hide` en Priority (`LaneOf`), `list/getSettings` en Live, escrituras en Slow (`Every_catalog_message_has_a_deliberate_lane`); payload inválido → `command.failed` sin eco del texto; `alert.changed` solo con ids.
- `ProtocolContractTests`/`ProtocolFormatTests` (v15, `PinnedFormat[15]`, direcciones por mensaje), `MigrationHashTests` (hash de la 12), `SourceRulesTests` (sin `REPLACE`, sin `new SqliteConnection`), `AlertSettingsTests` (carga por bloque, `settings.json.bad`, escritura atómica).

**JS (`npm test`, happy-dom donde se indica; happy-dom NO mide):**
- `alert-layer` (salida): cinta, ticker (variable de duración, mismo elemento al repetirse el snapshot), reemplazo sin remontar, ocultar con fundido, `visible=false`, tope de altura; `AlertErrorBoundary` (un error de la alerta no cambia el contenido ni el fondo).
- `alert-isolation` (la clave): alternar la alerta **no remonta el nodo del `<video>`, no reinicia `currentTime`, no dispara el crossfade ni cambia los búferes del contenido** (mutación: si `OverlayLayer` pasara a depender de `content`, falla).
- `alert-freeze` / `alert-visibility`: la alerta pasa en Freeze mientras el texto sigue congelado; Negro/Texto limpio/Logo la ocultan; el estado se conserva.
- `alert-active` (pura): `alertLiveStatus` (EN PANTALLA, OCULTA · motivo, ESPERANDO REPETICIÓN n/N, salida cerrada/en pausa, ninguna), recuperación tras recarga.
- `alert-controller` (backend falso v15 sobre el cliente IPC real): mostrar, quitar, reemplazar, errores, fijadas/recientes, anotación sin esperar, `command.failed`, sin confirmación.
- `alert-bar` (happy-dom): Enter muestra y suelta el foco, ▶ muestra, clic en la fila solo carga, Esc, atajos con el foco en un cuadro, vista previa y «no cabe → desplazamiento», «Mostrar» desactivado si no cabe.
- `alert-shortcuts`: ninguno choca con los globales (incluida la «A» en `RESERVED_GLOBAL_SHORTCUTS`) ni con los de las pestañas.
- Validación por mutación de lo importante (sin generación en el temporizador, sin aislamiento, alerta congelada, sin comprobar el orden de ocultar).

**PRUEBA REAL: `tools\alert-test.ps1`** (app real Debug, salida en ventana normal —nunca en el proyector—, datos temporales, SOLO texto sintético; mismo estilo que `media-test.ps1`/`song-layout-test.ps1`): `show`/`hide` por IPC real y medición de la latencia hasta pintar; reemplazo; caducidad real (15 s); repetición; ticker (velocidad medida) y cinta de 1, 2 y 3 líneas y 300 caracteres a escalas 1,0/1,5/2,0 sin recorte; con un video en bucle de fondo: **mismo nodo, `currentTime` continuo y fotogramas presentados con la alerta puesta/quitada y con ticker**; crossfade del texto intacto; los cuatro modos maestros y Freeze; pausa de la salida con alerta; recarga del panel (la alerta se recupera); 0 tareas largas del hilo principal del panel; registro sin texto. Repetir con el panel congelado (`tools\i8-frozen-panel-test.ps1 -Full`, ampliado con una alerta y la pulsación REAL de `Ctrl+Alt+Shift+A`, y verificando que la salida no se afecta) y `i8-reload-isolation-test.ps1`.

**No verificable de forma automática (se anota como pendiente):** el atajo global con teclado real fuera de la prueba del panel congelado (registro por otra aplicación), el proyector físico, `-OutputOn other`, la apariencia a ojo (contorno, legibilidad sobre distintos fondos) y la lectura del ticker a distancia.

---

## 10. Partición en 2 prompts

**Prompt 1 — Backend y salida (sin interfaz del panel).**
Migración 12, Core (`AlertOverlay`, `AlertTheme`, validador, `AlertScheduler`, `OutputState.WithAlert`, schema 3, ajustes `alertDisplay`), Data (`AlertRepository` + `BackgroundAlertRecorder`), protocolo v15 (8 mensajes, carriles, tests de contrato, actualizar los 7 scripts de `tools/`), integración en `OutputController` (estado, temporizador con generación, modos maestros, pausa, orden de ocultar) y `GlobalHotkeys` (`Ctrl+Alt+Shift+A`), y en la página de salida: `AlertLayer` (cinta y ticker) con su boundary, excepción de Freeze, respaldo del atajo. Pruebas C# y JS de salida (`alert-layer`, `alert-isolation`, `alert-freeze`, `alert-visibility`). Verificación manual por IPC con un script mínimo (`tools\alert-test.ps1`, parte de salida: show/hide/caducidad/video intacto/ticker). La repetición puede diferirse aquí si crece. Actualiza CLAUDE.md (estructura, protocolo v15, Freeze, atajos globales).

**Prompt 2 — Panel y verificación real.**
Carpeta `frontend/src/control/alerts/` (`controller.ts`, `AlertBar` fijo en `App`, lista de fijadas/recientes, vista previa con medición real y sugerencia de desplazamiento, ajustes de tema, `active.ts` con `alertLiveStatus`, insignia «ALERTA EN PANTALLA» visible en todas las pestañas con cuenta atrás y Quitar), atajos en `shortcuts.ts` (verificados con teclado real), `RESERVED_GLOBAL_SHORTCUTS`, tests JS del panel, ampliación de `tools\alert-test.ps1` (UI real, layout a varias escalas, textos largos), y de `i8-frozen-panel-test.ps1 -Full` (alerta + tecla global real). Cierra la documentación (CLAUDE.md: pestaña/barra de alertas, pruebas y lo no verificado).

Cada prompt empieza con el árbol limpio (commit previo) y termina informando por separado lo verificado y lo no verificado.
