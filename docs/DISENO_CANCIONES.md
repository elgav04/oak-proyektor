# Diseño del módulo de canciones (decisiones aprobadas; sin código todavía)

Basado en la lectura de las migraciones 1–8, `Models.cs`, `ISongRepository`, `IPlaylistRepository`, el motor de capas y la pestaña de Biblia. Ninguna letra real de canciones aparece aquí ni aparecerá en el repositorio (ver `CLAUDE.md`, «Canciones y derechos de autor»).

> El protocolo actual es **v11** (`IpcCatalog.ProtocolVersion`); el módulo lo sube a **v12**.

## Decisiones del operador aplicadas a este documento

1. **Hot-Edit descartado** (ni «Solo ahora», ni antirrebote hacia la salida). Ver §7: fuera de alcance, posible más adelante.
2. **Sin línea de derechos en pantalla: `OutputState` NO cambia.** Autor, derechos y CCLI se guardan en la ficha de la canción y **no se proyectan**.
3. **Editor: texto único con etiquetas.**
4. **«Guardar en la canción»** se mantiene en el editor, con copia previa por canción (últimas 20), «Deshacer último guardado» y «Restaurar versión…». El botón indica cuántas veces se repite la sección editada («Coro ×3»).
5. **Borrar una canción** sigue la política de `PlaylistItems`; sin papelera.
6. **Fondo por canción**: si la canción no tiene fondo, **no toca el actual**.
7. **Partición en 3 prompts**: (1) modelo, migración, repositorios, parser, importador, FTS e IPC; (2) editor; (3) pestaña de proyección.

---

## Estado de la implementación (prompt 1: backend) y desviaciones del diseño

Hecho y probado: migración 9, repositorio, parser/formateador/validador, decodificador de .txt, búsqueda, copias previas, borrado con la política de `PlaylistItems`, ajustes `songDisplay` y los 11 mensajes de la v12. **Pendiente**: el editor (prompt 2) y la pestaña de proyección (prompt 3). Cambios respecto a lo escrito más abajo, decididos al implementar:

- **El orden son POSICIONES, no ids**: `arrangement` es una lista de índices en `sections` (en `song.get`, `song.save` y `song.parseText`). Las secciones conservan su `Id` al guardar (se actualizan en su sitio), pero una sección nueva todavía no tiene id y así no hace falta una clave de cliente.
- **Duplicados con texto distinto**: se nombran en forma CANÓNICA, no «Coro (2)»: «Coro» → «Coro 2», «Verso 1» → «Verso 1 bis», «Parte A» → «Parte A (2)». Razón: «Coro (2)» se leería de nuevo como «Coro 2» y el ciclo texto → modelo → texto no sería estable.
- **`song.parseText`** recibe `{ text, mergeIdenticalBlocks }` (la casilla de la vista previa) en vez del `mode` sin definir del borrador.
- **`song.get` devuelve también `text`**: el texto único con etiquetas reconstruido por el backend (el editor no necesita un formateador en TypeScript).
- **`song.changed` lleva `usedInServices`** (al borrar, para decir «quedó como Falta en N servicios»).
- **`song.restoreRevision`** lleva `expectedRevision` (como `song.save`): restaurar sobre una revisión caducada falla con `song.conflict`.
- **Añadido: `--import-song <ruta.txt>`** (orden de consola; crea una canción nueva y muestra la codificación detectada y conteos). El decodificador de archivos vive en C# (`SongTextDecoder`); el panel usará `TextDecoder` cuando exista la importación por arrastrar y soltar.
- **El serializador IPC ya no cita `ex.Message`** de un JSON roto en el registro (podía citar un carácter de la letra).
- **Las líneas ambiguas se rechazan al guardar**: una línea de la letra con forma `[…]` o que empiece por «Orden:» no se podría releer igual.
---

## 1. Qué hay realmente en la BD

| Objeto | Qué es | ¿Se usa? |
|---|---|---|
| `Songs` (mig. 1) | `Id, Title, Author, Copyright, CcliNumber, CreatedAt`; índice por título NOCASE | **Sin uso.** Solo lo tocan tests. |
| `SongSlides` (mig. 1) | Lista plana: `SongId (CASCADE), SequenceIndex, Label, Content` | **Sin uso.** |
| `SongsFts` (mig. 1, 2) | FTS5 *independiente* con `Title, Content`; `Content` = `group_concat` de `SongSlides`. Triggers en `Songs` y `SongSlides`. Tokenizer `unicode61 remove_diacritics 2`. **No indexa `Author`.** | Probado por `FtsTests` (letra sintética); nada de producción lo consulta. |
| `Playlists` | `Id, Name, ServiceDate` (`ItemsJson` se eliminó en la mig. 3) | Solo la interfaz `IPlaylistRepository`. |
| `PlaylistItems` (mig. 3, 4) | Canción = `SongId` con FK `ON DELETE SET NULL` + `IsMissing` calculado + `Title` instantánea; triggers que impiden crear elementos huérfanos o desvincular a mano | Sin código que lo escriba. |
| `ISongRepository` | 3 métodos | **Solo la interfaz: no hay implementación** en `backend/src`, ni mensajes IPC, ni UI. |
| `Presentations`/`PresentationSlides` | Para `.pptx` | Fuera de este módulo. |

Consecuencias: (a) las tablas de canciones deberían estar vacías, pero **no se ha comprobado en tu BD** → la migración convierte lo que haya; (b) cambiar `ISongRepository` no rompe nada; (c) `PlaylistItems.SongId → Songs(Id)` existe, así que **no se reconstruye `Songs`**: solo `ALTER TABLE … ADD COLUMN`.

---

## 2. Modelo de datos

### Comparación y elección

| | A. `SongSlides` plana | B. Secciones + orden (**elegida**) |
|---|---|---|
| Repetir el coro | Copiar el texto N veces | Una sección, N referencias; editarla lo cambia en todas partes |
| Editar | Cada diapositiva es un texto aparte | Bloques naturales («Verso 1», «Coro»…); reordenar = mover chips |
| Diapositivas | Fijas, no se adaptan al tema | Derivadas al proyectar (corte manual + medición real) |
| Riesgo | Ninguno de efecto lateral | Editar un coro cambia todas sus repeticiones (se avisa en el botón de guardar, §5) |
| FTS | Letra repetida | Letra una vez |
| Servicios futuros | Copiar canciones para otro orden | Otro orden sin tocar el texto |

### Esquema (migración 9, `0009_SongSections.sql`)

```
Songs   (ALTER ADD COLUMN, sin reconstruir)
  + Notes TEXT
  + UpdatedAt TEXT                         -- NULL → se rellena con CreatedAt en la migración
  + Revision INTEGER NOT NULL DEFAULT 1    -- concurrencia optimista (§5)
  + BackgroundKind TEXT NULL CHECK (… IN ('image','video','color'))   -- NULL = «no tocar el fondo actual»
  + BackgroundPath TEXT NULL, BackgroundColor TEXT NULL, BackgroundMuted INTEGER NOT NULL DEFAULT 1
  (Author, Copyright, CcliNumber ya existen: se GUARDAN, no se proyectan)

SongSections
  Id INTEGER PRIMARY KEY, SongId → Songs(Id) ON DELETE CASCADE,
  Position INTEGER NOT NULL,
  Kind TEXT NOT NULL CHECK (Kind IN ('verse','chorus','prechorus','bridge','ending','intro','interlude','other')),
  Label TEXT NOT NULL,                     -- único por canción: UNIQUE (SongId, Label COLLATE NOCASE)
  Text TEXT NOT NULL                       -- una línea en blanco = corte manual de diapositiva

SongArrangements                           -- hoy solo UNO por canción
  Id, SongId → Songs ON DELETE CASCADE, Name TEXT NOT NULL, IsDefault INTEGER NOT NULL
  UNIQUE INDEX (SongId) WHERE IsDefault = 1

SongArrangementItems
  Id, ArrangementId → SongArrangements ON DELETE CASCADE, Position INTEGER NOT NULL,
  SectionId INTEGER NOT NULL → SongSections(Id)          -- NO ACTION

SongRevisions                              -- copias previas (§5)
  Id, SongId → Songs ON DELETE CASCADE, CreatedAt TEXT NOT NULL,
  Reason TEXT CHECK (Reason IN ('save','import','restore')),
  Snapshot TEXT NOT NULL                   -- JSON: metadatos + fondo + secciones + orden
```

- **Orden con tabla propia** aunque la interfaz solo muestre el predeterminado: los arreglos por servicio llegarán con una columna nullable en `PlaylistItems`, sin reconstruir nada.
- **`SectionId` con `NO ACTION`**: borrar una sección que el orden usa no la quita del orden en silencio, y borrar la canción entera sigue funcionando (SQLite comprueba al final de la sentencia, tras la cascada). Test dedicado. El editor borra la sección y sus referencias en UNA transacción.
- **Etiqueta única por canción** (sin distinguir mayúsculas): hace que `[Coro]` sea inequívocamente «la misma sección».
- Límites validados en el backend: sección ≤ 4000 caracteres (como `MAX_BLOCK_CHARS`), ≤ 60 secciones, ≤ 200 elementos del orden.
- **Las diapositivas no se guardan**: se derivan (§6), porque la medición real solo existe en el navegador del panel.

### FTS
`SongsFts` es derivado: la migración lo **elimina y recrea** como `fts5(Title, Author, Content, tokenize='unicode61 remove_diacritics 2')`, con triggers sobre `Songs` (`AFTER UPDATE OF Title, Author, Id`) y `SongSections` (`Content` = secciones por `Position`, **cada una una vez**), y lo repuebla con `INSERT … SELECT`.

### Migración y `SongSlides`
1. Migración **nueva** (nunca se editan las publicadas; hash en `MigrationHashTests`).
2. Cada fila de `SongSlides` → una sección (`Kind='other'`, etiqueta única) y un orden predeterminado en secuencia. Sin pérdida y sin abortar.
3. Se eliminan los triggers de `SongSlides`; **la tabla queda congelada** y se borra en una migración posterior (la copia previa del `MigrationRunner` y el respaldo diario cubren el intervalo).
4. `ISongRepository` y el record `SongSlide` se sustituyen (no hay implementación).
5. Pruebas: BD v8 vacía; con `SongSlides` sintéticas; una canción referenciada por `PlaylistItems` conserva `SongId`/`IsMissing`; `PRAGMA foreign_key_check` limpio.

---

## 3. Pegado e importación de texto

**Parser único en C#** (`SongTextParser`, Core, función pura que nunca lanza, como `ReferenceParser`), expuesto por `song.parseText` para la vista previa. Normaliza NFC, CRLF → LF, sin BOM, espacios finales recortados, ≥ 2 líneas en blanco = un corte; **nunca toca puntuación, tildes ni mayúsculas**.

1. **Con etiquetas**: `[Verso 1]` abre una sección; las líneas en blanco del cuerpo son cortes manuales.
2. **Etiqueta sola o repetida con el mismo texto = repetición**: añade una referencia al orden, no una sección. Repetida con texto DISTINTO: crea «Coro 2» (forma canónica; ver «Estado de la implementación») y avisa.
3. **Sin etiquetas**: cada bloque separado por línea en blanco es una sección («Sección 1…»); los idénticos se fusionan como repetición (casilla para desactivarlo). Una línea que sea exactamente un alias (`Coro`, `Verso 2:`) también se acepta como etiqueta, **marcada en la vista previa**.
4. **Alias** (sin distinguir mayúsculas ni tildes, número opcional): Verso/Estrofa → `verse`; Coro/Estribillo → `chorus`; Pre-coro/Precoro/Pre coro/Pre-estribillo → `prechorus`; Puente → `bridge`; Final/Coda/Outro → `ending`; Intro/Introducción → `intro`; Interludio/Instrumental → `interlude`; otras → `other` con su etiqueta literal. Lista en Core, con test.
5. Tope 200 KB y 60 secciones; se informa, nunca se recorta en silencio.
6. Líneas que parecen solo acordes: se **avisa con el conteo**, no se borran.

**Codificación**: el panel lee los bytes del `File` soltado (rechaza > 200 KB antes de leer): BOM UTF-8/UTF-16 → `TextDecoder('utf-8', {fatal:true})` → si lanza, `windows-1252`. **La vista previa muestra la codificación detectada** y avisa si el resultado tiene `�` o patrones de mojibake (`Ã©`, `Ã±`, `â€`), con selector para forzar otra y reintentar. Pegar desde el portapapeles ya es Unicode.

Importar = `song.parseText` (vista previa editable: título, secciones y tipo, orden, avisos) → «Guardar» manda `song.save`. No hay mensaje de importación aparte.

---

## 4. Editor visual (texto único con etiquetas)

```
┌ Editor ───────────────────────────────────────────────────────────────────────┐
│ [Título][Autor][Derechos][CCLI][Notas][Fondo…]       [Guardar (Coro ×3)] Ctrl+S│
├───────────────────────────────┬───────────────────────────────────────────────┤
│ TEXTO (formato con [etiquetas]) │ DIAPOSITIVAS (tema real, en miniatura)         │
│ [Verso 1]                     │ ┌──┐┌──┐┌──┐┌──┐   cada una con su sección y    │
│ …                             │ │V1││V1││C ││V2│   marca de corte manual /      │
│ (línea en blanco = corte)     │ └──┘└──┘└──┘└──┘   automático                    │
├───────────────────────────────┴───────────────────────────────────────────────┤
│ ORDEN:  [Verso 1] [Coro] [Verso 2] [Coro] [Puente] [Coro]   (+ sección)         │
└───────────────────────────────────────────────────────────────────────────────┘
```

- **Texto**: una sola copia de cada sección, mismo formato que §3. Cada cambio → `song.parseText` con antirrebote de 150 ms (solo hacia el backend; **no hay antirrebote hacia la salida: el editor no proyecta**). Las referencias del orden son por `SectionId`; al re-analizar se reconcilian secciones por etiqueta exacta y, si no, por posición cuando el número de secciones no cambió. Una referencia sin reconciliar se marca en rojo y **bloquea Guardar** hasta resolverla.
- **Diapositivas**: derivadas con el tema real de canciones y el MISMO motor de medición que la Biblia. Se distingue el corte automático del manual; el «ajuste manual» es añadir una línea en blanco. Se recalcula cuando el texto deja de cambiar (150 ms), con memoización por sección+texto+tema.
- **Orden**: chips con `@dnd-kit` (sensor de teclado incluido); «+» inserta una sección existente; quitar un chip no borra la sección; una sección fuera del orden se marca «sin usar».
- **Botón de guardado**: calcula (función pura) las secciones modificadas y cuántas veces aparecen en el orden: «Guardar» si ninguna se repite; «Guardar (Coro ×3)» o «Guardar (Coro ×3, Puente ×2)» si las hay. Una sección que se elimina estando en uso se avisa antes de guardar.
- **Atajos** (mapa único `control/shortcuts.ts`, con test de colisión con los globales Ctrl+Alt+Shift+M/B/C/L/R): `Ctrl+S` guardar; `Ctrl+Enter` guardar y volver a la lista; `Esc` salir (con cambios sin guardar se conserva un borrador y se avisa, sin `confirm`); `Alt+↑/↓` mover el chip enfocado; `Ctrl+D` duplicar el chip.
- **Sin bloqueos**: las páginas se calculan sección a sección cediendo el hilo (~50 ms por tarea); `song.parseText` es una consulta corta del carril en vivo.

---

## 5. Guardado, copia previa y recuperación

El editor **nunca autoguarda ni toca la salida**. Guardar es explícito (`Ctrl+S`) y atómico:

1. `song.save` lleva la canción completa y `expectedRevision`.
2. En UNA transacción (`BeginTransaction(deferred: true)`): se comprueba la revisión (si no coincide, `song.conflict` y no cambia nada) → se guarda **la copia previa** (instantánea del estado anterior en `SongRevisions`, motivo `save`) → se aplica el cambio actualizando las secciones en su sitio (conservan su `Id`; las eliminadas se borran explícitamente junto a sus referencias) → `Revision + 1` → se recortan las copias a **las últimas 20 por canción**. Crear una canción no genera copia (no hay estado previo).
3. **Deshacer último guardado**: restaura la copia más reciente. **Restaurar versión…**: lista (fecha, motivo; sin texto) y restaura la elegida. Restaurar **toma antes una copia del estado actual** (motivo `restore`), así que también se puede deshacer.

| Riesgo | Mitigación |
|---|---|
| Guardar un coro cambia todas sus repeticiones | El botón lo dice («Coro ×3») y la copia previa permite volver |
| Pegar encima y guardar sin querer | Copia previa en cada guardado; «Deshacer último guardado» |
| Secciones vacías, fuera de límites o referencias rotas | El backend rechaza (`song.invalidRequest`); el panel bloquea Guardar con referencias sin resolver |
| Guardado con una pestaña/estado viejo | `Revision` → `song.conflict`, nada cambia |
| La escritura espera el candado de la BD (hasta 30 s medido en el historial de la Biblia) | Comandos de escritura por el **carril lento**; un guardado nunca bloquea la proyección |
| Borrador perdido al recargar el panel (Ctrl+Alt+Shift+R) | Borrador en memoria del panel; se verificará en el prompt 2 si se puede persistir en `localStorage`/`sessionStorage` con `file://` (no comprobado) |

### Borrar una canción
Sin papelera. **Misma política que `PlaylistItems`** (mig. 3/4): el borrado no se bloquea, la FK pasa a `NULL` (`ON DELETE SET NULL`) y el elemento del servicio conserva su `Title` con `IsMissing = 1`, así que **no queda ninguna referencia rota en silencio**: quien abra el servicio avisa «Falta: …». El panel, antes de borrar, lee `usedInServices` (`song.get`) y, si es > 0, dice «usada en N servicios» y exige una segunda confirmación en la propia interfaz (sin `confirm`). Las copias previas se borran con la canción (cascada); la red de seguridad es el respaldo diario de la BD.

---

## 6. Proyección

- **Lista y búsqueda**: `song.search` consulta `SongsFts` (título, autor y letra, sin tildes; mismo saneado que la Biblia: NFC, solo palabras, ≤ 32 términos, nunca un operador FTS5 a `MATCH`; última palabra como prefijo; `bm25` con más peso al título). Sin texto: lista completa ordenada por título con comparación sin tildes **en C#** (NOCASE solo cubre ASCII), virtualizada. `song.search` va por el carril lento.
- **Tira de diapositivas** de la canción seleccionada, derivada de secciones+orden con el motor de la Biblia. Unidad de división: **línea > palabra > carácter**; nunca une trozos separados por un corte manual; la última no queda huérfana. `buildSongSlides` es pura y se prueba con medición simulada.
- **Proyectar**: `output.showContent` (sin mensajes nuevos de salida), con `autoFit` activado como red de seguridad (`projectionTheme`). `Slide.id` estable = canción + posición en el orden + índice de página. **`OutputState` no cambia.**
- **Teclas** (sin Ctrl/Alt, solo en la pestaña): `→`/`Av Pág` siguiente, `←`/`Re Pág` anterior (proyectan), `Enter` proyecta la seleccionada, `Esc` quita lo proyectado (`output.clearContent`), `/` busca, `1–9` salta a la diapositiva N con el foco fuera de los cuadros.
- **Al llegar al final**: se queda en la última diapositiva y la tira muestra una celda «FIN»; `→` no hace nada más. `Esc` limpia de forma explícita. Ajuste opcional «al final: quedarse / limpiar».
- **Tema propio**: `SongDisplaySettings` en `settings.json` (como `BibleDisplaySettings`: `Theme`, `TransitionMs`, `AtEnd`), carga por bloque, validado por el backend; un valor inválido vuelve a su defecto sin rechazar cada proyección.
- **Fondo por canción**: si la canción tiene fondo, al proyectar su PRIMERA diapositiva el panel manda `output.setBackground` (carril lento, `MediaPathValidator`) y luego `showContent`; con la misma ruta el motor no remonta ni reinicia el video; un fallo cae al color de reserva con el aviso existente. **Si no tiene fondo, no se toca el actual** (consecuencia aceptada: una canción con fondo deja su fondo puesto para la siguiente canción sin fondo, hasta que el operador lo cambie). Cambiar de diapositiva no reenvía el fondo.
- **Autor, derechos y CCLI**: se editan y guardan en la ficha; **no se proyectan** y no hay opción para ello.

---

## 7. Fuera de alcance y cómo el diseño lo deja posible

- **Edición rápida en vivo (Hot-Edit)** — *fuera de alcance, posible más adelante.* Porque las diapositivas se derivan de sección+orden y la salida solo recibe `output.showContent`, bastaría una UI nueva que proyecte el texto editado y guarde con `song.save`/`SongRevisions`, sin cambiar esquema ni protocolo (haría falta mapear página → sección).
- **Línea de derechos en pantalla** — descartada; exigiría un pie por slide en `OutputState`. Los campos ya están guardados.
- **Listas de culto con orden por servicio y arreglos por servicio**: `PlaylistItems` ya referencia canciones con FK e `IsMissing`; `SongArrangements` admite varios órdenes; bastaría `PlaylistItems.ArrangementId` (nullable, `SET NULL`) en una migración.
- **Importar la biblioteca de EasyWorship 6**: `song.parseText` + `song.save` son la puerta de entrada; un importador solo produciría texto etiquetado o el modelo de canción. `SongRevisions.Reason='import'` ya existe. El formato de EW no se ha investigado.
- **Acordes**: `SongSections.Text` no cambia; se añadiría una tabla `SongChords` sin migrar datos. El importador avisa de las líneas de acordes en vez de borrarlas.

---

## 8. Riesgos y preguntas abiertas

- **Reconciliación de secciones** al editar el texto (renombres): el mayor riesgo del modelo elegido. Ids estables, coincidencia por etiqueta y por posición, referencias sin resolver que bloquean el guardado y tests dedicados.
- **Privacidad**: la letra viaja por IPC (es lo que se proyecta) pero **no debe llegar a logs ni a errores**. Test: ni el log de un guardado ni el de un payload inválido (`IpcDispatcher` registra `ex`, no el JSON) contienen la letra sintética; `song.parseText` se registra solo con conteos.
- **`SongsFts` recreado**: repoblar; cubierto por el test de migración.
- **Archivos grandes o binarios**: tope de 200 KB antes de leer.
- **Peso de la medición**: ~40 diapositivas × 2–10 ms; por secciones, cediendo el hilo y con memoización.
- **Guardar mientras la canción está proyectada**: no cambia lo ya proyectado (la salida es «tonta»); solo la siguiente proyección usa el texto nuevo.
- **Abierta**: persistencia del borrador al recargar el panel (ver §5).
- **Abierta**: aviso del fondo heredado (ver §6); si resulta confuso en la práctica se puede añadir una acción explícita «quitar fondo» sin cambiar el modelo.

---

## 9. Mensajes IPC nuevos (protocolo **v12**)

| Tipo | Clase | Carril | Payload → resultado |
|---|---|---|---|
| `song.search` | query | lento | `{ query, limit }` → resúmenes (id, título, autor; fragmento de coincidencia) |
| `song.list` | query | en vivo | `{}` → todos los resúmenes (id, título, autor, actualizado), ≤ 2000 |
| `song.get` | query | en vivo | `{ id }` → metadatos, `revision`, fondo, `sections[]`, `arrangement[]` (ids de sección), `usedInServices` |
| `song.parseText` | query | en vivo | `{ text, mode }` → `{ sections, arrangement, warnings, detected }` (puro, sin BD) |
| `song.history` | query | en vivo | `{ songId }` → `{ id, createdAt, reason }[]` (sin texto) |
| `song.getSettings` | query | en vivo | → `SongDisplaySettings` (`Theme`, `TransitionMs`, `AtEnd`) |
| `song.setSettings` | command | en vivo | sustituye el conjunto; validado antes de guardar |
| `song.save` | command | lento | crea o actualiza la canción completa con `expectedRevision` |
| `song.restoreRevision` | command | lento | `{ songId, revisionId }` (también «Deshacer último guardado») |
| `song.delete` | command | lento | `{ id }` |
| `song.changed` | event | — | `{ commandId, songId, revision, kind: created\|updated\|deleted\|restored }` |

**11 mensajes** (6 queries, 4 commands, 1 event). Errores (`IpcException`): `song.notFound`, `song.invalidRequest`, `song.conflict`, `song.textTooLarge`, `song.tooManySections`, `song.busy`; sin letra en los mensajes. **Quitados respecto a la propuesta anterior:** `song.updateSection` (era el «guardar» del Hot-Edit; el editor guarda con `song.save`). Cada tipo debe figurar en `IpcPriority.LaneOf` y, al subir `v`: huella nueva en `PinnedFormat`, espejo en `frontend/src/ipc/` y los `v:11` de `tools/*.ps1`. `output.*` no cambia.

---

## 10. Plan de pruebas

Todo con **letras sintéticas inventadas** (`SyntheticSong`, análogo a `SyntheticBible`).

- **Core**: `SongTextParser` (etiquetas, alias con/sin tilde y número, repetición por etiqueta sola, repetición con texto distinto, sin etiquetas, bloques idénticos, acordes, límites, entrada vacía/basura, nunca lanza, `formato(parseo(x))` estable); `SongValidator`.
- **Data**: migración 9 (BD v8 vacía; con `SongSlides` sintéticas; canción en un servicio intacta; `foreign_key_check`; hash); `NO ACTION` (borrar sección en uso falla, borrar canción funciona); FTS (título, autor, letra sin tildes, coro una vez, triggers); concurrencia (`Revision`/`song.conflict`); `SongRevisions` (copia previa antes de guardar, tope de 20, restaurar y que restaurar sea reversible); transacción atómica; **borrar una canción usada en servicios** (el elemento queda con `Title` e `IsMissing = 1`, nada roto; el recuento `usedInServices` es correcto); `SourceRulesTests` verde.
- **IPC**: contrato (`ProtocolContractTests`, `ProtocolFormatTests` con la huella v12), carriles, handlers con BD temporal y **privacidad** (la letra sintética no aparece en logs ni errores).
- **Frontend**: `buildSongSlides` (cortes manuales, división automática con medición simulada, no cruza cortes, ids estables); decodificador de archivos (UTF-8, Windows-1252, UTF-16 con BOM, mojibake); reconciliación de secciones; cálculo del texto del botón («Coro ×3»); controlador con backend falso (v12); `SongsTab` con flujo de teclado y proyección hasta `OutputScreen`; fondo por canción (no se reenvía por diapositiva; sin fondo no se envía nada); atajos sin colisión con los globales.
- **Layout real** `tools\song-layout-test.ps1` (como `bible-layout-test.ps1`; solo letra sintética). Tras tocar el panel, repetir `tools/i8-frozen-panel-test.ps1 -Full`.
- **No verificable automáticamente**: apariencia fina (contorno/sombra), arrastrar y soltar con ratón real, proyector físico.
- **Pruebas eliminadas respecto a la propuesta anterior** (por el descarte del Hot-Edit y de la línea de derechos): antirrebote del Hot-Edit, `Esc` que revierte lo proyectado, Ctrl+S sobre un borrador huérfano, conflicto en `song.updateSection`, propiedad «las páginas reconstruyen el trozo» (rangos), y la medición del último bloque con la línea de derechos.

---

## 11. Partición del trabajo en 3 prompts

**Prompt 1 — Modelo, migración, repositorios, parser e importador, FTS e IPC (sin UI).** Migración 9 (esquema, conversión de `SongSlides`, FTS nuevo), modelos y `SongRepository`, `SongTextParser` + alias + formateador, importación (`song.parseText` + `song.save`, decodificación de texto en el backend si se necesita desde consola), validador, `SongRevisions` (copia previa, 20, restaurar), borrado con la política de `PlaylistItems`, handlers `song.*`, carriles, errores, protocolo v12 con huella, espejo TS de los tipos, `SyntheticSong`, actualización de `tools/*.ps1` y tests de C#. Termina con todo en verde y sin tocar el panel.

**Prompt 2 — Editor.** Editor de texto único con etiquetas y vista previa de diapositivas (motor de la Biblia: `buildSongSlides` pura con corte manual + medición), chips de orden con `@dnd-kit`, reconciliación de secciones, botón «Guardar (Coro ×3)», «Deshacer último guardado» y «Restaurar versión…», diálogo de importación (arrastrar/pegar `.txt`, detección de codificación y vista previa), atajos, borrador, borrado con aviso «usada en N servicios», tests de JS.

**Prompt 3 — Pestaña de proyección.** Lista virtualizada con `song.search`, tira de diapositivas, proyección con `output.showContent`, teclas, final de canción, `SongDisplaySettings` (tema propio), fondo por canción (sin fondo = no toca), tests de JS, `tools\song-layout-test.ps1` y `i8-frozen-panel-test.ps1 -Full`.

Cada prompt termina con commit propio y con el informe de qué se verificó y qué no.
