# Diseño de las listas de culto (propuesta; sin código todavía)

Estado: **diseño APROBADO con decisiones (2026-11-25); backend implementado** (ver «Estado de la implementación» abajo). La interfaz (pestaña EN VIVO y EDITAR) sigue pendiente. Reglas del repo que aplican: ninguna letra ni texto bíblico real en el repo, tests, logs ni documentación; los logs llevan solo ids y conteos (nunca títulos, notas ni letra).

---

## Estado de la implementación (prompt 1: backend) y decisiones aprobadas

**Decisiones del operador aplicadas:** (1) `origin` en `output.showContent`: SÍ, con SOLO ids (nunca texto), nulo si la proyección no viene de una lista; `OutputState.schemaVersion` = 2; el elemento activo se deriva del `origin` confirmado por el backend, no de la igualdad de texto. (2) Fin de canción: `→` se queda con «FIN · siguiente: …»; configurable a «pasar al siguiente» o «limpiar» (`playlistDisplay.onItemEnd`). (3) Las notas NO limpian la pantalla por defecto (`ClearOnActivate` existe, apagado). (4) Sin elemento «fondo/video» por ahora. (5) Teclas aprobadas; atajo para «Añadir a la lista» desde la Biblia: **Ctrl+Shift+A** (propuesto; ver abajo). (6) Una lista sin fecha es plantilla; duplicar con nombre y fecha nuevos omite los faltantes y dice cuántos. (7) Arreglo por servicio: fuera de alcance (`ADD COLUMN ArrangementId` más adelante).

**Desviaciones respecto al diseño original (y por qué):**
- **El `origin` es un record estructurado, no una cadena `pl:<item>:<n>`**: `{ playlistId, itemId, itemType: "song"|"passage", position }` (solo ids y números; el tipo es un conjunto cerrado). Se valida su FORMA; NO se comprueba contra la BD (proyectar no depende de la base de datos). Un origen de un elemento inexistente se proyecta igual.
- **Son 13 mensajes, no 14** (el documento original sumaba mal: 3 queries + 9 commands + 1 event = 13). Además `output.showContent` gana `origin`; `song.get` y `song.changed` ganan `usedInLists`.
- **Los comandos de escritura publican `playlist.changed`** con `kind`: `created | updated | itemAdded | itemMoved | itemRemoved | itemUpdated | duplicated | deleted`; solo ids y conteos (`itemId`, `sourceId`, `copied`, `skipped`).
- **`playlist.get` entrega `projectable` por elemento**, calculado en el backend (`PlaylistNavigation`): «siguiente» y «anterior» saltan faltantes, notas y pasajes sin resolver; el panel no tiene que volver a derivarlo.
- **Mover al mismo sitio no sube la revisión** (no invalida a nadie); **añadir sí la sube** aunque no la exija.
- **El título de un pasaje** (instantánea) es `Libro N c:v[-v] · BIBLIA` (solo números) si el panel no manda uno.

**Atajos verificados con teclado REAL (2026-11-25):** Ctrl+E, Ctrl+Shift+A, Ctrl+←/→ y Ctrl+L llegan al panel (WebView2 no se los queda); en un cuadro de texto Ctrl+←/→ siguen moviendo el cursor por palabras (19 → 13 → 8 → 13 en «uno dos tres cuatro»). La pestaña de listas debe IGNORARLOS con el foco en un cuadro de texto (regla ya vigente). **Ctrl+Shift+A** se propone para «Añadir a la lista» porque no usa Alt (los globales son Ctrl+Alt+Shift+M/B/C/L/R) y no repite la «L» del logo; no se probó dentro de la pestaña Biblia porque esa UI aún no existe.

**Prompt 2 hecho (2026-11-25): pestaña «Lista» en modo EN VIVO** (ver CLAUDE.md, «Pestaña «Lista»»): el activo se deriva del `origin`; `Ctrl+Shift+A` para «Añadir a la lista» sigue pendiente (va con el modo EDITAR, prompt 3). Desviaciones: sin activo, la miniatura «Siguiente» muestra lo que `→` va a proyectar (el elemento del cursor); `Esc` + `→` continúa tras la última diapositiva mostrada; el modo EDITAR es por ahora un aviso.

**Prompt 3 hecho (2026-11-25): modo EDITAR** (ver CLAUDE.md, «Modo EDITAR»): cada edición = un comando con la revisión de lo que se ve, encadenadas; conflicto = aviso + recarga conservando lo escrito; Ctrl+Mayús+A y botón desde la Biblia; arrastrar con `@dnd-kit` y Alt+flechas; faltantes con reemplazar/quitar; aviso al pasar a EN VIVO con una edición pendiente. Desviaciones: **reemplazar un faltante son dos comandos** (añadir en su posición y quitar el faltante; el backend no tiene «cambiar la canción de un elemento»); el aviso del duplicado nombra lo omitido; la notas se añaden **después de la selección** y las canciones al final con Enter o después con Ctrl+Enter; sigue sin implementar el arreglo por servicio.

**No verificado todavía (backend):** nada de la interfaz; el comportamiento de las listas con el proyector físico; el paso real de la salida con un `origin` (la salida lo ignora y el test de la página de salida con un slide con `origin` queda para el prompt 2).
---

## 1. Qué existe y qué falta de verdad

**Existe (y se reutiliza tal cual):**

| Pieza | Estado |
|---|---|
| `Playlists` (`Id`, `Name`, `ServiceDate` TEXT sin validar) y `PlaylistItems` (migraciones 1, 3, 4) | Esquema con `SequenceIndex`, `Title` (instantánea), `SongId` FK `ON DELETE SET NULL`, `PresentationId`, pasaje por referencia canónica (`BookNumber`, `Chapter`, `VerseStart/End`, `BibleAbbreviation` como TEXTO), `IsMissing` calculado, triggers que impiden crear o desvincular a mano un elemento «faltante». |
| `PassageResolver.ResolvePlaylistAsync` | Resuelve pasajes: `Resolved`, `BibleNotInstalled` (+ `Offered` si la Biblia por defecto lo tiene), `PassageNotFound`. Nunca modifica el elemento. Probado. Desde la revisión (2026-11-26) resuelve todos los pasajes de la lista con UNA consulta. |
| Canciones (migraciones 9–10) | `song.get` devuelve secciones y orden; `SongArrangements` admite varios órdenes por canción (hoy el editor solo usa el predeterminado); `usedInServices` ya cuenta servicios; borrar una canción deja el elemento con `IsMissing = 1`. |
| Biblia | `bible.lookup` (con `record: false` no anota historial), paginación por medición real, `CitationHistory`. |
| Proyección | `output.showContent`, `output.clearContent`, `output.setBackground` (carril lento), y la derivación de EN VIVO desde el estado confirmado (`bibleLiveStatus` / `songLiveStatus`). |

**Falta de verdad** (todo lo demás ya está):

1. **Ninguna implementación de `IPlaylistRepository` ni escritura alguna.** Solo hay una interfaz de lectura; los tests insertan con SQL a mano. No hay crear, renombrar, añadir, mover, quitar, duplicar ni borrar.
2. **Ningún mensaje IPC `playlist.*` y ninguna pestaña.**
3. **No existe el tipo de elemento «nota/separador».** El `CHECK` de `ItemType` solo admite `SONG | PRESENTATION | PASSAGE`: hay que reconstruir la tabla (migración nueva).
4. `Playlists` no tiene `Revision`, `UpdatedAt` ni validación de `ServiceDate` (hoy es texto libre).
5. **La salida no sabe de qué elemento viene el texto.** `output.showContent` solo lleva `{ blocks, theme, transitionMs }`; hoy EN VIVO se deriva por **igualdad de texto** con una pista de la vista (`songLiveStatus`). En una lista eso falla si la misma canción aparece dos veces, o si el panel se recarga (Ctrl+Alt+Shift+R) y pierde la pista. Ver §6, decisión del `origin`.
6. `PlaylistItems.PresentationId` y `Presentations` existen pero **no hay quien las cree** (fase `.pptx` posterior): se dejan intactos y las listas no los ofrecen.

---

## 2. Modelo de una lista de culto

### Lista (`Playlists`)

- `Name` (≤ 120), `ServiceDate` (`AAAA-MM-DD` o NULL), `Notes` (≤ 2 000, texto libre), `Revision`, `CreatedAt`, `UpdatedAt`.
- **Plantilla = lista sin fecha.** No hay un indicador aparte: la pantalla agrupa «Próximas / Pasadas / Plantillas (sin fecha)». Cero esquema extra.
- Límite de 100 elementos por lista (`PlaylistLimits`; un culto real tiene ~10–25).

### Tipos de elemento

| Tipo | Se proyecta | Notas de diseño |
|---|---|---|
| `SONG` | sí | FK a `Songs` (existente). |
| `PASSAGE` | sí | Referencia canónica + Biblia preferida (existente). |
| **`NOTE`** (nuevo) | **no** | Separador/recordatorio: `Title` («Ofrendas», «Anuncios»), `Note` opcional (≤ 500: «Lee el pastor…»), `ClearOnActivate` (0/1). |
| `PRESENTATION` | — | Se conserva en el esquema, **no se ofrece** hasta la fase `.pptx`. |

**Recomendación sobre «limpiar pantalla» y «fondo/video»** (según uso real):

- **Limpiar pantalla: no es un tipo.** Ya existe `Esc` y el maestro «Texto limpio». Un `NOTE` con `ClearOnActivate = 1` cubre el caso real («al pasar a Anuncios, quita la última diapositiva») sin un tipo más. **Por defecto 0**: activar una nota no cambia nada en la pantalla (conducta predecible).
- **Fondo/video: ahora NO.** Hoy el panel no tiene ningún selector de fondo general (solo el fondo propio de cada canción y el diagnóstico provisional), así que un elemento «fondo» no tendría de dónde nacer ni cómo previsualizarse. Para el uso real (un video durante la oración/ofrenda) la vía actual es poner ese fondo y activar un `NOTE`. **Cómo queda posible**: un tipo `BACKGROUND` con las mismas columnas `Background*` de `Songs`, activado por `output.setBackground` (carril lento). Añadirlo exige otra reconstrucción de `PlaylistItems` por el `CHECK` (patrón ya usado en las migraciones 3 y 4); es barato y no se hace por adelantado.

### Faltantes: nunca se pierde un elemento ni se rompe la lista

- **Canción borrada**: el elemento sigue ahí con su `Title` instantánea, `IsMissing = 1`, en rojo «Falta: …». Al abrir una lista con faltantes, una barra lo dice («2 elementos faltan») con la lista de cuáles. Activarlo **no cambia la pantalla** y avisa; la navegación «siguiente/anterior» **los salta** (con un aviso breve «se omitió: Falta …»), porque pararse en algo que no se puede proyectar bajo presión es peor. Se puede quitar o dejar.
- **Biblia no instalada / pasaje no encontrado**: ya resuelto por `PassageResolver`; `playlist.get` devuelve el estado de cada pasaje (`resolved | bibleNotInstalled (+offered) | passageNotFound`). «Biblia no instalada» ofrece la Biblia por defecto con un botón; nunca se aplica sola.
- **Título actual vs instantánea**: `playlist.get` devuelve el título ACTUAL de la canción (join) y se usa; la instantánea solo se muestra si falta.
- **Duplicar con faltantes**: el trigger `PlaylistItems_bi` impide crear una canción sin referencia, así que `playlist.duplicate` **omite** los elementos faltantes y dice cuántos («Se copiaron 14; 1 omitido porque faltaba la canción»). Los pasajes (incluso con Biblia no instalada) y las notas se copian siempre.

### Duplicar y fechas

- `playlist.duplicate { id, name, serviceDate }`: una transacción (`INSERT … SELECT`), conserva el orden, genera ids nuevos. El panel propone nombre «Copia de …» y la fecha del siguiente domingo (editable); también sirve para «crear una lista desde una plantilla».
- Orden de la pantalla de listas: próximas por fecha ascendente (la de hoy, resaltada), luego pasadas descendente, luego plantillas. Al abrir el panel se propone la de hoy o la próxima.
- Borrar una lista: barra inline de confirmación (sin `confirm`), sin papelera; la red de seguridad es el respaldo diario (como las canciones).

### Arreglo por servicio (orden distinto de secciones sin tocar la canción)

**Decisión: queda para después; el diseño lo deja posible sin reconstruir nada.**

- Motivo: hoy el editor solo crea y edita UN orden por canción (el predeterminado); no hay interfaz para definir órdenes alternativos. Hacer lista y editor de órdenes a la vez duplica el riesgo. Mientras tanto, **en vivo ya se puede saltar a cualquier diapositiva** de la tira (clic en una miniatura), que cubre «hoy repetimos el coro dos veces más».
- Cómo queda abierto: `PlaylistItems.ArrangementId INTEGER NULL REFERENCES SongArrangements(Id) ON DELETE SET NULL` se añade más adelante con `ALTER TABLE … ADD COLUMN` (SQLite lo admite con valor por defecto NULL; **sin reconstrucción**). NULL = orden predeterminado de la canción. Si el arreglo se borra, el elemento vuelve al predeterminado (no queda roto). Un arreglo es del propio documento de la canción, así que sobrevive a editar la letra (las secciones conservan su `Id`; `SectionId` es `NO ACTION`).
- Sin esta columna, el resto del diseño no cambia: `slides` de un elemento = `buildSongSlides(sections, arrangement)` y solo cambia qué `arrangement` se pasa.

---

## 3. Interfaz de operación en vivo

Una pestaña nueva **«Lista»** con dos modos separados: **EN VIVO** (por defecto al abrir) y **EDITAR**. El modo es estado de VISTA; el backend no lo conoce.

### Modo EN VIVO (lo que se ve y qué hace cada tecla)

```
┌ Domingo 29 nov ───────────── [🔒 EN VIVO]  (Ctrl+E → Editar) ─┐
│ 1  ♪ Canción A            ◄ ACTUAL · EN VIVO  diap 3/6        │
│ 2  ♪ Canción B                                                │
│ 3  — Ofrendas (nota)                                          │
│ 4  📖 Jn 3:16-18 · RVR1960                                    │
│ 5  ♪ Canción C   ✖ Falta                                      │
├ Tira de la diapositiva del elemento activo ───────────────────┤
├ SIGUIENTE: ♪ Canción B  [miniatura de su 1.ª diapositiva]     │
└───────────────────────────────────────────────────────────────┘
```

- **Elemento activo claro**: fila con borde y la insignia **EN VIVO / LISTO / OCULTO · …** que ya existe, **derivada del estado confirmado** (§4). El «cursor» (fila seleccionada, aún no proyectada) es solo vista y se distingue (fondo suave) del activo (borde fuerte + insignia).
- **Teclas** (mapa único `shortcuts.ts`; sin Ctrl+Alt+Shift; la pestaña no coexiste con Biblia/Canciones):
  - `↑` / `↓`: mover el cursor entre elementos (**no proyecta**).
  - `Enter`: activar el elemento del cursor (proyecta su primera diapositiva/página).
  - `→` / `Av Pág` y `←` / `Re Pág`: diapositiva o página siguiente/anterior **dentro del elemento activo** (las mismas teclas que ya existen en Canciones y Biblia; proyectan).
  - `Ctrl+→` / `Ctrl+←`: **saltar al elemento siguiente/anterior y proyectar su primera diapositiva** (relativo al ACTIVO; si no hay activo, al cursor).
  - `Esc`: quitar el texto (`output.clearContent`), sin cambiar el activo.
  - `Ctrl+E`: alternar EN VIVO / EDITAR. `/`: buscar (solo en EDITAR).
- **«Siguiente» al terminar un elemento** (ajuste `playlistDisplay.onItemEnd`):
  - **`stay` (por defecto)**: `→` en la última diapositiva no hace nada salvo mostrar «FIN · siguiente: Canción B» (nombre del siguiente, con su miniatura). Pasar de elemento es una acción **explícita** (`Ctrl+→` o `Enter` en otra fila). Razón: igual que decidimos en las canciones, una pulsación de más no debe proyectar otra canción mientras el pastor ora o habla.
  - `next`: `→` en la última diapositiva proyecta la primera del siguiente elemento proyectable (los separadores y faltantes se saltan).
  - `clear`: `→` en la última quita el texto.
  - Se cambia en un selector de la pestaña (se guarda en `settings.json`, validado por el backend, carga por bloque como los demás ajustes).
- **Previsualización del siguiente**: tras ~300 ms de reposo en el elemento activo se **precarga** el siguiente proyectable (`song.get` / `bible.lookup` sin anotar historial), se calcula su primera diapositiva con la medición real en tandas (cediendo el hilo, ya existe `warmChunks`) y se pinta como miniatura 1920×1080 escalada, con el título. Es solo vista: no toca la salida. Si el siguiente es una nota, muestra su texto; si falta, «Falta: …».
- **Candado = el modo**: en EN VIVO **no hay** arrastre, borrar, renombrar ni añadir (los controles ni se dibujan, no solo se desactivan); no se puede mover la lista por error. Pasar a EDITAR es deliberado (`Ctrl+E` o botón con el candado) y la insignia del modo está siempre visible. Cuando la salida está en vivo (insignia EN VIVO) y se entra a EDITAR, aparece una línea de aviso («La salida está proyectando: editar no la cambia»); editar nunca altera lo proyectado.

### Modo EDITAR (armar la lista rápido)

- **Panel lateral de búsqueda de canciones** (`song.search`, ya virtualizada): `↓/↑` recorren, `Enter` **añade al final**, `Ctrl+Enter` **inserta después del cursor**. Un botón «＋» por fila.
- **Desde la Biblia**: un cuadro de referencia en el mismo panel lateral (usa el parser; muestra la referencia canónica resuelta y la Biblia por defecto) y, en la pestaña Biblia, un botón «Añadir a la lista» (atajo propuesto: Ctrl+Shift+A, ver «Estado de la implementación») sobre el pasaje seleccionado. Necesita una lista «abierta»: un almacén de vista a nivel de la aplicación (no ligado a la pestaña montada) guarda cuál es (en `localStorage`, de mejor esfuerzo; si no, la de hoy/próxima).
- **Arrastrar para reordenar** con `@dnd-kit` (ratón y teclado en el asa), más alternativas: `Alt+↑/↓` mover, `Supr` quitar, `Ctrl+D` duplicar el elemento, botón «＋ Nota».
- Quitar un elemento deja «Deshacer» (un paso, solo en la sesión de vista: reinserta el elemento en su posición).
- Todo cambio es **un comando pequeño y atómico** (§7) y la vista se actualiza con el estado confirmado (`playlist.changed` + `playlist.get`), con el arrastre mostrado de forma optimista y corregido si el backend lo rechaza.

---

## 4. Coherencia con lo existente

- **Backend = fuente de verdad.** Las listas viven en SQLite; la vista guarda solo cursor, modo y la lista abierta. Nada de estado de negocio en el panel.
- **EN VIVO y el elemento activo se derivan del estado confirmado** (`output.stateChanged`): se reutiliza `bibleLiveStatus`, y para ubicar *qué elemento y qué diapositiva* se usa el `origin` (§6); sin `origin` (texto de otra pestaña o diagnóstico) nada de la lista está «activo». Si el panel se recarga, el activo se recupera solo de `output.getState`.
- **Notas y faltantes no pueden estar «en vivo»** (no hay slide); su marca es el cursor.
- **Carriles**: toda consulta rápida (`playlist.list/get/getSettings`) va en el carril **en vivo** (lecturas cortas); toda escritura (`playlist.*` comandos) va por el **lento** (puede esperar el bloqueo de escritura de SQLite), y se añade a `IpcPriority.LaneOf` (`Every_catalog_message_has_a_deliberate_lane` lo vigila). **Activar un elemento no escribe NADA en la BD** (solo `output.*` y lecturas); la anotación del historial de citas sigue sin hacerse al proyectar desde la lista (`record: false`).
- **Fondo**: se conserva la regla de Canciones. Al activar un elemento con fondo propio (solo canciones con fondo) se manda UNA vez `output.setBackground` por el carril **lento** antes del primer texto; con el mismo medio el motor no remonta ni reinicia el video. Un elemento **sin** fondo propio **no manda nada**: no corta el video actual. Cambiar de diapositiva o de elemento sin fondo nunca toca el fondo.
- **Salida «tonta»**: no cambia nada en la ventana de proyección ni en el motor de capas.
- **Higiene del panel**: nada de bucles largos (la precarga y la medición van en tandas, cancelables); listas ≤ 100 elementos, sin virtualizar, pero la búsqueda lateral sí (reutiliza `SongList`); sin `alert`/`confirm`; atajos sin chocar con los globales.
- **Prueba obligatoria al terminar**: `tools/i8-frozen-panel-test.ps1 -Full` (la salida no puede depender de la pestaña).

---

## 5. Fuera de alcance

Audio asociado a elementos · alertas y ticker · presentaciones personalizadas · PowerPoint (`.pptx`) · elemento «fondo/video» (ver §2) · arreglo por servicio (§2, diseñado pero diferido) · Stage Display · importar servicios de EasyWorship 6 · cuenta atrás/temporizador.

---

## 6. Migraciones propuestas (la 10 es la última publicada; no se edita ninguna)

### Migración 11 — `0011_PlaylistsEdit.sql`

1. **`Playlists`** (`ALTER TABLE … ADD COLUMN`, sin reconstruir): `Notes TEXT NOT NULL DEFAULT ''`, `Revision INTEGER NOT NULL DEFAULT 1`, `CreatedAt TEXT`, `UpdatedAt TEXT` (anulables: SQLite no admite un valor por defecto no constante al añadir columna; luego un `UPDATE` rellena las filas existentes). Triggers `BEFORE INSERT/UPDATE OF ServiceDate` que rechazan una fecha que no sea `AAAA-MM-DD` válida (`GLOB` + `date()`), sin tocar filas existentes (guardia: si hubiera fechas inválidas, se **normalizan a NULL** conservando el texto en `Notes` —nunca se aborta ni se pierde nada—; el resultado se cuenta en el registro).
2. **`PlaylistItems` se RECONSTRUYE** (el `CHECK` de `ItemType` y la forma cambian), con el patrón documentado: crear `_new`, copiar, `DROP` de la vieja, **renombrar la nueva**, recrear índices (`IX_PlaylistItems_PlaylistId_SequenceIndex`, `…_SongId`, `…_PresentationId`) y los triggers `PlaylistItems_bi` y `PlaylistItems_bu` (el `DROP` se los lleva), con `foreign_keys = OFF` y `foreign_key_check` como ya hace el runner. Cambios: `ItemType IN ('SONG','PRESENTATION','PASSAGE','NOTE')`; columnas nuevas `Note TEXT` y `ClearOnActivate INTEGER NOT NULL DEFAULT 0 CHECK (ClearOnActivate IN (0,1))`; forma `NOTE`: sin ninguna referencia, `Title` no vacío, sin Biblia. `IsMissing` sigue siendo 0 para pasajes y notas. Se copian todos los datos tal cual (incluidos los elementos con `SongId` NULL = faltantes: la copia usa `INSERT … SELECT`, y el trigger `BEFORE INSERT` se crea DESPUÉS de copiar, como en la migración 4).
3. Guardia de datos: ninguna (la migración solo amplía). Si algo no se puede convertir, aborta con copia previa (comportamiento del runner).

Más adelante (no ahora): **migración 12** `ADD COLUMN ArrangementId` (arreglo por servicio, sin reconstrucción); otra reconstrucción para `BACKGROUND` si se decide.

Se añaden: el hash en `MigrationHashTests`, `Playlists`/`PlaylistItems` en `DatabaseInitializerTests` (si cambian las listas de tablas/columnas esperadas) y un test de migración sobre una BD v10 con servicios (canciones, un faltante, pasajes de Biblias instaladas y no instaladas).

### Decisión del `origin` (protocolo)

`output.showContent` gana un campo opcional **`origin`** (cadena opaca ≤ 64, solo `[A-Za-z0-9:._-]`, validada en `ContentValidator`), que se guarda en el `Slide` y viaja en los snapshots; la página de salida lo ignora. Formato de la lista: `pl:<itemId>:<índice de diapositiva>`. **Por qué**: hace exacta la derivación del elemento activo (misma canción dos veces, coro repetido) y sobrevive a recargar el panel. **Coste**: cambia el formato de `OutputState` (sube `OutputState.CurrentSchemaVersion` a 2, nueva huella y espejo en TS). Alternativa: no tocar la salida y derivar por texto con pista (como Canciones): más simple pero ambigua con duplicados y se pierde al recargar. **Recomendado: `origin`.** Canciones y Biblia pueden seguir sin enviarlo.

---

## 7. Mensajes IPC nuevos (protocolo **v13**; hoy es la v12)

| Tipo | Clase | Carril | Payload → resultado |
|---|---|---|---|
| `playlist.list` | query | en vivo | `{}` → resúmenes: id, nombre, fecha, nº de elementos, nº de faltantes, revisión (≤ 500) |
| `playlist.get` | query | en vivo | `{ id }` → lista + elementos: id, tipo, posición, título actual/instantánea, `songId`, referencia del pasaje, `note`, `clearOnActivate`, `isMissing`, y por pasaje `resolution` (`resolved | bibleNotInstalled | passageNotFound`, `offered`). **Sin letra ni texto bíblico.** |
| `playlist.getSettings` | query | en vivo | → `PlaylistDisplaySettings` (`onItemEnd`: `stay|next|clear`) |
| `playlist.setSettings` | command | en vivo | sustituye el conjunto; validado antes de guardar (`playlist.invalidSettings`) |
| `playlist.create` | command | lento | `{ name, serviceDate?, notes? }` |
| `playlist.update` | command | lento | `{ id, name, serviceDate, notes, expectedRevision }` |
| `playlist.addItem` | command | lento | `{ playlistId, item (song/passage/note), position? }`; **añadir no exige `expectedRevision`** (conmuta con otras ediciones) |
| `playlist.moveItem` | command | lento | `{ playlistId, itemId, toIndex, expectedRevision }` |
| `playlist.removeItem` | command | lento | `{ playlistId, itemId, expectedRevision }` |
| `playlist.updateItem` | command | lento | `{ playlistId, itemId, title?, note?, clearOnActivate?, bibleAbbreviation?, expectedRevision }` (notas y la Biblia preferida de un pasaje, para aceptar la ofrecida) |
| `playlist.duplicate` | command | lento | `{ id, name, serviceDate? }` → resultado con `copied` y `skipped` |
| `playlist.delete` | command | lento | `{ id, expectedRevision }` (v14: revisión caducada → `playlist.conflict`, no borra nada) |
| `playlist.changed` | event | — | `{ commandId, playlistId, revision, kind: created\|updated\|itemsChanged\|duplicated\|deleted, … }` (solo ids y conteos; el fallo llega como `command.failed`) |

**13 mensajes** (3 queries, 9 commands, 1 event; además `output.showContent` gana `origin`, y `song.get`/`song.changed` ganan `usedInLists`). **Operaciones pequeñas en vez de «guardar la lista entera»**: una vista desactualizada no puede borrar elementos por accidente (un `save` completo reemplazaría todo), y cada comando es una transacción corta. Errores `playlist.*` (`IpcException`, sin letra ni títulos): `playlist.notFound`, `playlist.itemNotFound`, `playlist.invalidRequest`, `playlist.conflict`, `playlist.full`, `playlist.busy` (bloqueo de escritura ≈ 10 s, como Canciones), `playlist.invalidSettings`. Al subir `v`: huella nueva en `PinnedFormat`, espejo en `frontend/src/ipc/protocol.ts`, `Every_catalog_message_has_a_deliberate_lane`, y los `v:12` de `tools/*.ps1` y `media-test.ps1`/`bible-layout-test.ps1`/`song-layout-test.ps1`.

**Privacidad**: los registros llevan solo `playlistId`, `itemId`, conteos y revisión (`LogInformation("Lista {PlaylistId}: {Items} elementos, revisión {Revision}")`); ni títulos de lista, ni notas, ni letra.

---

## 8. Plan de pruebas (todo con datos sintéticos: `SyntheticSong`, `SyntheticBible`, nombres inventados)

- **Core**: validación de nombre/fecha/nota/límites (`PlaylistLimits`), formato y validación del `origin`; `PlaylistDisplaySettings` (carga por bloque, valor inválido → por defecto).
- **Data** (BD temporal): migración 11 sobre BD v10 (servicios con canciones, faltante, pasajes con Biblia instalada/no instalada, fechas inválidas normalizadas sin perder nada, `foreign_key_check`, trigger de la fecha, hash); repositorio: crear, actualizar con revisión (conflicto), añadir (al final y en posición), mover (reindexa sin huecos ni duplicados), quitar, **atomicidad** (un fallo a mitad no deja medio cambio), escritura concurrente y bloqueo retenido (`playlist.busy`), duplicar (orden, faltantes omitidos y contados, pasajes con Biblia ausente copiados), borrar una lista (cascada de elementos, canciones intactas), **borrar una canción usada** (el elemento queda `IsMissing`, `playlist.get` lo informa), reimportar una Biblia (los pasajes no se pierden), `SourceRulesTests` verde (sin `REPLACE`, conexiones de la fábrica).
- **IPC**: contrato (`ProtocolContractTests`, `ProtocolFormatTests` con la huella v13), carriles, handlers de extremo a extremo, **privacidad** (ni título, nota ni letra sintética en logs ni errores), `output.showContent` con `origin` (válido/ inválido/ omitido) y snapshot de la salida con `origin`.
- **Frontend** (`npm test`, backend falso con sobres v13): controlador (cursor vs activo, derivación EN VIVO/OCULTO desde `output.stateChanged` con y sin `origin`, recarga del panel recupera el activo, misma canción dos veces), navegación (`↓/↑`, `Ctrl+→/←` saltan faltantes y notas, `→` al final con `stay/next/clear`), precarga del siguiente con medición simulada (cancelable, no corre con el panel congelado), modo EN VIVO sin controles de edición, EDITAR (añadir desde la búsqueda, desde Biblia, mover con teclado, quitar + deshacer, duplicar), atajos sin colisión con los globales ni con otras pestañas, fondo (una vez, sin fondo = no manda nada), `lista` + proyección hasta `OutputScreen`.
- **Real**: `tools\playlist-layout-test.ps1` (como `song-layout-test.ps1`, app real, datos temporales, solo texto sintético): lista de ~20 elementos, activar y navegar con teclas reales, ráfaga, comparar la SALIDA real con la insignia, recarga del panel (activo recuperado), elemento faltante. Repetir `tools/i8-frozen-panel-test.ps1 -Full`.
- **No verificable automáticamente**: arrastrar con ratón real (`@dnd-kit` no se simula en happy-dom), apariencia fina, proyector físico.

---

## 9. Riesgos y preguntas abiertas

**Riesgos**

1. **Cambiar `OutputState` (`origin`)** toca la salida, el protocolo, huellas y scripts; es el cambio más delicado. Se aísla en el prompt 1 con sus propios tests y la prueba del panel congelado.
2. **Reconstruir `PlaylistItems`** (patrón ya probado en las migraciones 3 y 4): riesgo de perder triggers o índices; lo cubre un test que compara el esquema final y los datos antes/después.
3. **Precarga del siguiente elemento**: coste de medición en el hilo principal. Mitigación: tandas con cesión del hilo, cancelación al cambiar de elemento, solo un elemento por delante, y sin precarga si el panel va lento.
4. **Teclas nuevas** (`Ctrl+→/←`): comprobar que WebView2/Windows no las usa (en campos de texto mueven por palabras: no se interpretan con el foco en un cuadro de texto, regla existente).
5. **Borrado accidental de una lista**: sin papelera; mitigado por la barra de confirmación y el respaldo diario.
6. **Duplicados de contenido**: la misma canción dos veces es válida; por eso el `origin` lleva el id del ELEMENTO, no el de la canción.

**Preguntas para ti**

1. ¿Apruebas el campo `origin` en `output.showContent` (cambio en la salida) o prefieres derivar por texto como en Canciones (más simple, pero se pierde el activo al recargar el panel y es ambiguo con duplicados)? **Recomiendo `origin`.**
2. `→` al final de una canción: ¿`stay` por defecto (recomendado) o `next`?
3. ¿Los separadores deben **limpiar la pantalla al activarse por defecto**? Propongo que no (`ClearOnActivate = 0`).
4. ¿Hace falta de verdad un elemento «fondo/video» en tus cultos? Si sí, ¿de dónde sale el video (hoy no hay selector general de fondos)?
5. ¿Teclas: te sirven `↑/↓` (cursor), `Enter` (activar), `Ctrl+→/←` (saltar de elemento)? ¿Algún atajo para «Añadir a la lista» desde la pestaña Biblia (propongo solo botón, sin atajo, para no chocar)?
6. ¿Una lista sin fecha como «plantilla» te basta, o quieres una marca explícita?
7. El arreglo por servicio, ¿lo quieres justo después de las listas (migración 12 + editor de órdenes) o puede esperar?

---

## 10. Partición del trabajo en 3 prompts

**Prompt 1 — Backend completo (sin UI).** Migración 11 (+ hash y tests de migración), modelos y `PlaylistRepository` (todas las operaciones de §7 con transacciones diferidas y bloqueo de escritura limitado), `playlist.get` con resolución de pasajes, duplicar, ajustes `PlaylistDisplaySettings`, handlers y carriles, errores, `origin` en `output.showContent`/`Slide`/validador, protocolo **v13** (huella, espejo TS de tipos, `v:13` en `tools/*.ps1`), tests de C# con datos sintéticos y privacidad. Termina en verde y sin tocar el panel, salvo el espejo de tipos y un test de JS del cliente.

**Prompt 2 — Modo EN VIVO.** Pestaña «Lista»: lista de listas, apertura de una, elemento activo derivado del estado confirmado (con `origin`), cursor, navegación (`↑/↓`, `Enter`, `→/←`, `Ctrl+→/←`, `Esc`), fin de elemento (`stay/next/clear`) con su ajuste, tira del elemento activo, precarga y miniatura del siguiente, faltantes/Biblia no instalada, fondo por canción, tests de JS (controlador, pestaña, extremo a extremo hasta `OutputScreen`), `tools\playlist-layout-test.ps1` (parte en vivo) y `i8-frozen-panel-test.ps1 -Full`.

**Prompt 3 — Modo EDITAR.** Candado/modo, crear/renombrar/fechar/duplicar/borrar listas, panel lateral de búsqueda de canciones y de referencias bíblicas, añadir desde la pestaña Biblia, arrastrar y soltar (`@dnd-kit`) con alternativas de teclado, notas y separadores, quitar + deshacer, atajos, tests de JS y la parte de edición de `playlist-layout-test.ps1`; actualizar CLAUDE.md y este documento con lo realmente implementado.
