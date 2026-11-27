# Diseño del módulo de audio y medios (propuesta; sin código todavía)

Estado: **APROBADO con decisiones (2026-11-26; ver «Decisiones aprobadas»).** Prompt 1 (biblioteca) hecho; prompt 2 (reproducción y audio) en implementación; prompt 3 pendiente. Reglas del repo que aplican: ningún medio de terceros en el repositorio, los tests, el instalador ni la documentación (el material de prueba se GENERA: bytes sintéticos en los tests y `MediaRecorder` dentro de la propia salida en las pruebas reales); los registros llevan solo ids y conteos (nunca nombres de archivo ni rutas del operador). Este documento NO toca la política de monitores, la pausa ni los atajos globales existentes: propone un atajo global nuevo (§6) y una reacción a la pausa que solo LEE el estado (§3.4), con el mismo patrón que ya usan las alertas.

Objetivo: usar en un culto (a) videos de fondo, (b) videos con audio en primer plano, (c) pistas de audio sueltas y (d) imágenes, **sin depender de un USB ni de un disco de red durante el culto**, con control claro desde el panel y con una forma de callar todo al instante aunque el panel esté congelado.

---

## Decisiones aprobadas (2026-11-26)

1. **Dispositivo de audio: RESUELTO (2026-11-26).** Cableado real: el audio sale por una de dos rutas, que Windows ve como dos dispositivos distintos: (a) salida analógica auxiliar de la PC, o (b) interfaz USB hacia la mezcladora intermedia. Se usa una u otra según la ocasión, nunca las dos a la vez, y **Windows puede cambiar el dispositivo predeterminado por su cuenta al conectar o desconectar el USB**. Decisión:
   - Ajuste **«dispositivo de audio de la iglesia»**, guardado por **identificador estable** (no por nombre ni posición), que usan las pistas de audio (NAudio). Opción «usar el predeterminado de Windows».
   - El audio del **video en primer plano** sale por el **predeterminado de Windows** (limitación de WebView2). El panel muestra SIEMPRE el nombre del dispositivo predeterminado de Windows y del dispositivo de la iglesia, y **avisa de forma visible si no coinciden**. Se escuchan los cambios del predeterminado (`IMMNotificationClient`) y se avisa si cambia durante el servicio.
   - **Nunca se salta a otro dispositivo por cuenta propia**, y menos a los altavoces del portátil. Si el elegido desaparece, avisa y espera; si vuelve el mismo (mismo id), puede reanudar solo si el ajuste lo permite (**apagado por defecto** para pistas).
   - Botón **«Probar sonido»**: tono suave y corto por el dispositivo de la iglesia y otro por el predeterminado, para comprobar las rutas antes del servicio.
   - **No se cambia el dispositivo predeterminado de Windows por programa.**
   - **Alternativa futura (solo evaluación posterior, no se construye)**: reproducir el audio del video con el motor de C# con el `<video>` silenciado. Daría elección de dispositivo y la misma política de pérdida, pero añade riesgo de **desfase de labios** (sincronía audio/imagen con corrección de deriva), una segunda decodificación del mismo archivo y más carga de CPU; exige un spike propio con medición antes de aprobarla.
2. **Motor de audio: NAudio.** Verificado el 2026-11-26 con `dotnet package search` y la API de NuGet: última versión estable **3.3.0** (publicada el 2026-10-19; hay una 3.4.0-preview.1 que NO se usará), licencia **MIT**, destino `net9.0` (se consume sin problema desde `net10.0`; el SDK en uso es 10.0.404), dependencias de la misma versión (`NAudio.Core`, `NAudio.Wasapi`, `NAudio.WinMM`, `NAudio.Midi`), sin aviso de obsolescencia y **sin vulnerabilidades** registradas, con versiones frecuentes en 2026 (proyecto vivo). Sin problema que objetar. Se adopta en el **prompt 2** (no en el 1) con la versión **exacta fijada** (`[3.3.0]`, como Photino) y una prueba de reproducción real; la interfaz `IAudioBackend` se mantiene para no atar el diseño a la librería.
3. **Pausa de la salida y audio.** La pausa de la salida (manual, de emergencia o por monitor) **NO corta las pistas de audio** (motor de C#). Tampoco Negro, Texto limpio, Logo ni Freeze. Solo cortan el audio: el atajo global de **Silencio**, los controles del panel (⏹/⏸ del reproductor) y lo que el propio medio haga al terminar. El **video en primer plano SÍ se pausa y silencia con cualquier pausa de la salida (y con Negro, Texto limpio y Logo), conservando la posición.**
4. **HEVC**: se acepta si el equipo lo decodifica (verificación del primer fotograma), con una **advertencia al importar** que recomienda H.264.
5. **Atajo global `Ctrl+Alt+Shift+S` (Silencio)**: solo calla, nunca reanuda; calla **tanto el motor de pistas como el video en primer plano**.
6. **Preguntas abiertas resueltas con la recomendación del diseño**: fondo debajo del primer plano **sin pausarlo** (se mide en la P2; pausarlo solo si pierde fotogramas); Texto limpio / Negro / Logo con un video en primer plano → **pausar y silenciar**; `StartMode` por defecto **`PLAY` para video y `READY` para audio suelto**; límite por archivo **4 GiB** y reserva de disco **el mayor de 1 GiB y el 5 %**; **carpetas, sin etiquetas** en la v1; **`ffmpeg` solo como ayuda opcional** (nunca requerido ni incluido); audio **MP3, M4A, AAC, WAV y FLAC** (sin Ogg/Opus/WMA).
7. **Respaldos**: los respaldos diarios NO cubren la carpeta `media\`; se documenta en `CLAUDE.md` con cómo copiarla a mano.

---

## 0. Lo medido (spike de 2026-11-26, app real en este equipo; sin construir nada)

Script temporal fuera del repositorio, sobre la ventana de salida real (WebView2 con Photino, página por `file://`):

| Pregunta | Resultado |
|---|---|
| ¿Contexto seguro? ¿`setSinkId`? | `isSecureContext = true`; `HTMLMediaElement.setSinkId` y `AudioContext.setSinkId` existen. |
| **¿Se pueden listar y elegir dispositivos de salida desde JavaScript?** | **No de forma útil**: `enumerateDevices()` devuelve UN solo `audiooutput` **sin etiqueta y sin id** (Chromium oculta etiquetas e ids hasta que la página tenga permiso de micrófono, y `selectAudioOutput` no existe). `setSinkId` solo acepta ese dispositivo vacío = el predeterminado de Windows. Pedir el permiso mostraría un aviso de micrófono y Photino no expone el evento `PermissionRequested` de WebView2. |
| ¿Reproducción CON sonido sin gesto del usuario? | **Sí**: `audio.play()` y `video.play()` sin silenciar se resolvieron (`userActivation.hasBeenActive = false`). El motor de fondo ya cae a «silenciado» si el navegador lo rechaza; ese respaldo se mantiene (la política puede variar con el runtime). |
| ¿Códecs? (`canPlayType`, solo orientativo) | **Sí**: MP4 H.264 (+AAC, AC-3, E-AC-3), VP8/VP9, AV1, **HEVC «probably» (hvc1/hev1)** en ESTE equipo, MP3, AAC, FLAC, Ogg Vorbis/Opus, WebM Opus. **«maybe»**: MKV, WAV. **No**: MOV, AVI, WMV, WMA, AC-3 suelto, ALAC, MIDI. |
| ¿`ffmpeg`/`ffprobe`? | **No están instalados** (ni en el `PATH`). Todo el diseño funciona sin ellos. |
| Dispositivos de audio de Windows | 3 controladores (Realtek, Intel Bluetooth, Intel USB): hay más de un destino posible; el predeterminado de Windows es el único alcanzable desde la página. |
| Disco | 1 156 GB libres en `C:` (la biblioteca cabe de sobra aquí; en otros equipos hay que comprobarlo). |

**Consecuencias de diseño** (se explican en las secciones): (1) el audio con **elección de dispositivo** tiene que salir de **C#** (WASAPI), no del navegador; (2) el audio de un **video en primer plano** sale por el dispositivo predeterminado de Windows (o el que el operador asigne a la aplicación en Windows): es una limitación honesta, con alternativas en §4.4; (3) las miniaturas, la duración y la comprobación «¿se reproduce?» las hace el propio navegador de la aplicación, sin `ffmpeg`; (4) HEVC «probably» en este equipo **no garantiza** que funcione en el de la iglesia: la comprobación real es decodificar el primer fotograma.

---

## 1. Qué existe y qué falta

**Existe (se reutiliza):**

| Pieza | Estado |
|---|---|
| `MediaPathValidator` (Core) + `FinalPathResolver` | Ruta absoluta canónica, extensiones permitidas (video `.mp4 .m4v .webm`; imagen `.jpg .jpeg .png .webp`), sin red ni unidades de red, aviso de unidad extraíble, URL `file:///` escapada. Se reutiliza para SERVIR lo que ya está en la biblioteca. Su regla «sin red / sin extraíble» es justo la que **no** debe aplicarse al IMPORTAR (ahí se lee de un USB o de la red una vez; ver §2.4). |
| Motor de fondo (`background/engine.ts`) | Un `<video loop>` persistente; `BackgroundLayer.Muted` ya existe (por defecto silenciado) y el motor cae a silenciado si el navegador rechaza el sonido; no se remonta ni reinicia; recuperación de fallos blandos. **No cambia.** |
| `OutputState` por capas, controles maestros, Freeze, pausa de la salida | El patrón de las alertas (§3.4) ya resolvió «una capa que reacciona a la pausa y a los maestros sin tocar la máquina de pausa». |
| Fondo por canción | `Songs.Background*` guarda una RUTA: sirve, pero depende de que el archivo siga donde estaba. |
| `PlaylistItems` | Tipos `SONG`, `PASSAGE`, `NOTE` (y `PRESENTATION` sin usar); `IsMissing` por clave foránea `SET NULL`; navegación que salta lo no proyectable. |
| Protocolo: carriles, `OrderClock`, `AlertTimerGuard`-style | Priority/Live/Slow; el orden de llegada resuelve «quitar» vs «mostrar» en carriles distintos. |
| `ScreenWakeLock`, `DisplayManager` | El sondeo de monitores es el modelo (no se toca) para sondear dispositivos de audio. |

**Falta de verdad:**

1. **Biblioteca**: importar copiando, nombre, carpetas, duplicados, espacio, miniaturas, duración, códec, estado «reproducible».
2. **Capa de video en primer plano** (con audio, transporte, no en bucle) distinta del fondo.
3. **Motor de audio en C#**: pistas sueltas, dispositivo, volumen maestro, fundidos, pérdida de dispositivo.
4. **Estado y mensajes** de reproducción (`media.*`, `audio.*`) y su reflejo en el panel (reproductor).
5. **Tipo de elemento `MEDIA`** en las listas de culto y su comportamiento al activar y al terminar.
6. **Atajo global** para callar el audio con el panel congelado.
7. **Mensajes claros** para formatos y códecs no soportados.
8. Fondos e imágenes elegidos **de la biblioteca** (por id) en vez de pegar una ruta; fondo por canción por id.

---

## 2. Biblioteca local de medios

### 2.1 Qué es

Una carpeta `media\` dentro de la carpeta de datos (`data\media` en desarrollo, `%LOCALAPPDATA%\Oak-Proyektor\media` en producción, o `<OAK_DATA_DIR>\media`) con los archivos COPIADOS, más dos tablas en la base de datos (migración 13). **Reproducir nunca lee del USB ni de la red:** solo del disco local de la aplicación. El USB se usa UNA vez, al importar.

```
media\
  files\ab\ab12…ef.mp4          ← almacenamiento por contenido: sha256 en hex; subcarpeta = 2 primeros caracteres
  thumbs\{id}.jpg               ← miniaturas (≤ 320×180, ≈ 15 KB)
  tmp\{guid}.partial            ← copia en curso (se limpia al arrancar)
```

El nombre en disco es el **SHA-256** más la extensión en minúsculas de una lista cerrada: siempre ASCII, sin espacios ni `%`, `#`, `&`, acentos ni nombres reservados de Windows (`CON`, `NUL`…). El nombre que ve el operador es un dato de la base de datos. Esto resuelve de una vez los «nombres con caracteres raros» y las URL `file://` mal escapadas.

### 2.2 Esquema (migración 13, `0013_MediaLibrary.sql`; nunca se edita una publicada)

```
MediaFolders(Id, Name NOT NULL UNIQUE (sin distinguir mayúsculas, ≤ 80), CreatedAt)           -- un solo nivel
MediaItems(Id, Kind CHECK IN ('VIDEO','AUDIO','IMAGE'), Name NOT NULL (≤ 120), FolderId NULL → MediaFolders ON DELETE SET NULL,
           OriginalFileName, Ext, Bytes, Sha256 NOT NULL UNIQUE,
           DurationMs NULL, Width NULL, Height NULL, Container NULL, VideoCodec NULL, AudioCodec NULL, HasAudio,
           Status CHECK IN ('PENDING','OK','UNSUPPORTED','MISSING'), StatusCode NULL,        -- código estable, p. ej. media.videoCodecUnsupported
           HasThumbnail, Revision, CreatedAt, UpdatedAt)
INDEX (FolderId, Name COLLATE NOCASE); INDEX (Kind)
```

- **Carpetas y no etiquetas** en la v1: una lista plana de carpetas («Fondos», «Videos de anuncios», «Pistas») más búsqueda por nombre. Las etiquetas libres multiplican la interfaz y se pueden añadir después sin romper nada.
- **`Status`**: `PENDING` (copiado, aún no verificado en el reproductor), `OK`, `UNSUPPORTED` (se guarda pero no se ofrece para proyectar, con su motivo), `MISSING` (el archivo desapareció del disco: «Verificar biblioteca»).
- Sin FTS: ≤ 2 000 elementos, `LIKE` sobre el nombre con tope de 200 filas por consulta.
- Una migración de **datos**: ninguna (tablas nuevas). Los respaldos diarios (`VACUUM INTO`) cubren la BASE, **no los archivos de `media\`**: se documenta («copia la carpeta `media` aparte; si se pierde, se vuelve a importar de los originales»). El aviso de carpeta sincronizada (OneDrive…) vale también para `media\`.

### 2.3 Importar (copiar) — en segundo plano, una a la vez, cancelable

Orden de la importación (`MediaImportService`, patrón de `BibleImportService`; todo por el carril LENTO; nunca en el carril en vivo):

1. **Validar la fuente** (`ImportSourceValidator`, nueva, hermana de `MediaPathValidator`): ruta absoluta canónica, **sí** admite unidades extraíbles y de red (es el caso de uso), **no** rutas de dispositivo (`\\.\…`), flujos alternativos ni nombres reservados; extensión de la lista; archivo regular no vacío; tamaño ≤ `MaxMediaBytes` (por defecto 4 GiB, configurable).
2. **Espacio en disco**: antes de copiar, libre ≥ tamaño + reserva (el mayor de 1 GiB y el 5 % del volumen). Si no: `media.diskFull` con cuánto falta, sin tocar nada. Se vuelve a comprobar a mitad (un disco que se llena por otro programa) y un fallo de escritura borra el `.partial`. Comprobación inyectable (`IFreeSpaceProvider`) para probarlo.
3. **Copia en bloques de 1 MiB** a `tmp\{guid}.partial`, calculando el SHA-256 al pasar, con `Flush(true)` al final; **vigilancia de bloqueo**: sin avance durante 15 s (USB que se desconectó, red caída) → se aborta con `media.sourceStalled` y se borra el parcial. Cancelable (`media.cancelImport`); progreso por eventos solo con ids y bytes.
4. **Duplicados por contenido**: si el SHA-256 ya existe, NO se crea otra fila ni otra copia: la respuesta dice «ya está en la biblioteca como «X»» (el nombre solo viaja en la respuesta al panel, nunca al registro) y el panel ofrece abrirlo. Dos archivos con el mismo NOMBRE y distinto contenido sí conviven (el nombre no es único).
5. **Sonda de cabeceras** (`MediaProbe`, C# puro, §2.5) para contenedor, códecs, duración y dimensiones; rechazo temprano de lo no soportado con su mensaje (§7).
6. **Renombrado atómico** del `.partial` a su sitio definitivo y alta de la fila en UNA transacción diferida (la primera sentencia es una escritura, como el resto de repositorios); si la transacción falla, el archivo recién copiado se borra salvo que otra fila lo use.
7. Fila `PENDING`; el panel la **verifica en el reproductor** (§2.5) y reporta `media.reportProbe`; pasa a `OK` o `UNSUPPORTED`.

Nombre inicial = nombre del archivo sin extensión, recortado y normalizado (NFC, sin caracteres de control, ≤ 120); el operador lo renombra después. Borrar un elemento exige la lista de DÓNDE se usa (listas, fondo de canciones, fondo actual) y deja esos usos como «faltante», igual que las canciones (política `SET NULL`); el archivo se borra solo si ninguna otra fila lo referencia (hoy no puede haber más de una: el SHA es único; queda previsto para futuras variantes).

### 2.4 Dónde se rechaza cada cosa (resumen)

| Caso | Qué pasa |
|---|---|
| Nombre con tildes, `%`, `#`, espacios, emoji, 250 caracteres | Importa sin problema: el nombre en disco es el hash; el nombre visible se recorta a 120 y se normaliza. |
| Mismo archivo importado dos veces | No se duplica: «ya está como «X»». |
| USB retirado a mitad de copia | `media.sourceStalled` a los 15 s; parcial borrado; nada en la biblioteca. |
| Disco casi lleno | `media.diskFull` antes de copiar; si se llena durante la copia, aborta y limpia. |
| Cierre inesperado de la aplicación durante una copia | Al arrancar se borra `tmp\*.partial`; la base no tiene ninguna fila a medias. |
| Archivo enorme (> 4 GiB) | `media.tooLarge` con el límite. |
| Fuente en `\\servidor\…` | Se admite para IMPORTAR (una lectura); para REPRODUCIR ya está local. |

### 2.5 Duración, códec, miniaturas y verificación — sin `ffmpeg`

- **`MediaProbe` (C#, puro, acotado)**: lee solo cabeceras. MP4/M4V/M4A (átomos `moov`/`trak`/`stsd`: `avc1`, `hvc1`/`hev1`, `av01`, `mp4a`, `ac-3`, `ec-3`; `mvhd` para la duración; `tkhd` para las dimensiones; tolera `moov` al final leyendo la cola), WebM (EBML: `CodecID`, duración, dimensiones), MP3 (ID3v2 + cabecera de trama + Xing/Info; si no hay, duración APROXIMADA por bitrate y se marca así), WAV (RIFF `fmt `/`data`), FLAC (`STREAMINFO`), imágenes PNG/JPEG/WebP (dimensiones). Límites: ≤ 1 MiB de cabecera + ≤ 4 MiB de cola, profundidad de átomos ≤ 6, presupuesto de tiempo; entradas hostiles (átomos con tamaño absurdo, bucles) devuelven «desconocido», nunca lanzan. Se prueba con archivos SINTÉTICOS generados por código en los tests.
- **Verificación en el navegador** (la que manda): el panel carga el archivo en un `<video>`/`<audio>` oculto, espera `loadedmetadata`, y para video **un primer fotograma decodificado** (`requestVideoFrameCallback`) en ≤ 5 s; manda `media.reportProbe { mediaId, ok, durationMs, width, height, errorCode }`. Es la prueba real de «esto se reproduce EN ESTE EQUIPO» (cubre HEVC dependiente de GPU/extensión). Una a la vez, sin bloquear el hilo principal más de unos milisegundos.
- **Miniaturas**: el mismo elemento oculto se posiciona al 10 % (≥ 1 s), se dibuja en un canvas de 320×180 y se manda como JPEG (`media.setThumbnail`, ≤ 200 KB, el backend comprueba los bytes mágicos y las dimensiones). Imágenes: `createImageBitmap` con reducción. Audio: sin miniatura (icono de tipo). Si el panel no está abierto, las miniaturas pendientes se generan la próxima vez que se abre la biblioteca (cola de una en una).
- **Si `ffmpeg` aparece (opcional)**: se busca en `<datos>\tools\ffmpeg.exe` y luego en el `PATH`; nunca se descarga ni se incluye (sin binarios de terceros en el repositorio ni en el instalador; la licencia GPL/LGPL de las compilaciones es otro motivo). Con él, el panel ofrece «Convertir a MP4 (H.264 + AAC)» para lo no soportado y «Normalizar volumen» como tareas en segundo plano, cancelables, con el resultado como elemento nuevo de la biblioteca. **Sin él**: el módulo funciona entero; lo no soportado se rechaza con el mensaje de §7 que explica cómo convertirlo (HandBrake o VLC). Cómo conseguirlo queda en la ayuda: descargar una compilación y copiar `ffmpeg.exe` a esa carpeta.

### 2.6 Servir a la salida

Por id, nunca por ruta: `media.setBackground { mediaId }` resuelve el archivo, lo pasa por `MediaPathValidator` (existe, es local, no extraíble) y publica la URL validada, como hoy. `output.setBackground` con ruta se conserva (compatibilidad y la pestaña de Diagnóstico); la interfaz de usuario pasa a usar la biblioteca. Un elemento `MISSING` o `UNSUPPORTED` no se puede proyectar y se dice por qué.

---

## 3. Reproducción en primer plano (video con audio) y convivencia con el fondo

### 3.1 Capa nueva

Una **capa de primer plano** entre el contenido y las superposiciones: `OutputState.Foreground` (`ForegroundMedia?`, `schemaVersion` 4):

```
ForegroundMedia(Id, MediaId, Url, FileName, Phase: playing | paused, VolumePct (0–100, YA con el volumen maestro aplicado),
                Muted, StartMs, SeekMs?, SeekSeq, FadeInMs, FadeOutMs, Backdrop: black | background, OnEnd: back | stay | next, Origin? )
```

La salida sigue siendo «tonta»: pinta lo que dice el estado; el avance (posición, duración, fin, errores) lo **reporta** la página de salida (`media.reportProgress`, como `output.reportBackgroundFailure`) y el backend lo retransmite al panel en un evento `media.progress` (≈ 4 por segundo mientras haya video; NO forma parte del snapshot ni cambia `seq`). Reproducir, pausar, buscar y volumen son órdenes de transporte (`media.play/pause/resume/seek/setVolume/stop`) que cambian el estado; buscar sube `SeekSeq` para que dos «buscar» al mismo tiempo se distingan.

### 3.2 Cómo conviven fondo y primer plano

- El **fondo no se toca**: sigue cargado, en bucle y silenciado debajo (mismo nodo, `currentTime` continuo). El video de primer plano entra con fundido (`FadeInMs`, solo `opacity`) y sale con fundido, **descubriendo el fondo exactamente donde iba**.
- Con `Backdrop = black` (por defecto) la capa pinta primero un fondo negro opaco: las franjas de un video que no es 16:9 no dejan ver el fondo animado. Con `background` se ve el fondo por las franjas.
- **Decisión recomendada: no pausar el fondo mientras el primer plano lo cubre.** Un video H.264 por hardware cuesta ≈ 1,4 % de CPU; pausar y reanudar añade un estado más al motor de fondo (que hoy es lo más estable). La prueba real de la P2 mide los fotogramas del primer plano con el fondo corriendo; si se pierden, entonces se añade «pausar el fondo mientras el primer plano está opaco» como cambio acotado del motor. Es una pregunta abierta (§9).
- Texto y alertas: el contenido (capa 2) queda DEBAJO del video (lo tapa); las alertas (capa 3) quedan ENCIMA (un aviso urgente se ve sobre un testimonio).
- El fondo sigue silenciado (`Muted`); el primer plano es la única fuente de sonido del navegador.

### 3.3 Controles del panel (reproductor)

Un reproductor en el panel (visible en todas las pestañas junto a la barra de alertas, colapsable) con: ▶/⏸, barra de avance con buscar (arrastrar o flechas), tiempo transcurrido/total, volumen del clip, silenciar, ⏹ Detener (con fundido) y la insignia «EN PANTALLA / OCULTO · motivo», **derivada del estado confirmado** (`foreground` del `output.stateChanged` más `media.progress`), nunca de lo enviado; tras recargar el panel se recupera sola. El avance es cosmético: la verdad es la salida.

### 3.4 Controles maestros y pausa de la salida (qué hace cada uno)

| Situación | Video en primer plano | Audio (pistas, motor C#) |
|---|---|---|
| **Live** | Visible y sonando. | Suena. |
| **Texto limpio** | **Se oculta y se PAUSA y silencia** (conserva la posición; vuelve al pasar a Live). Un video oculto que sigue sonando es peor que uno pausado. | **No cambia** (la música sigue; limpiar texto no es callar). |
| **Negro** | Oculto, **pausado y silenciado** (igual). | **No cambia** (música de fondo durante una oración con pantalla negra es el uso típico). |
| **Logo** | Oculto, pausado y silenciado. | No cambia. |
| **Freeze** | **NO se congela** (excepción deliberada, como la alerta: poder pausar/detener siempre). Texto y fondo siguen congelados. | No cambia. |
| **Pausa de la salida, CUALQUIERA** (monitor perdido, panel cruzado, modo Duplicar, `unstable`, manual, emergencia) | **Se pausa y silencia, conservando la posición** (se queda en el estado, no se borra: una reanudación automática la deja lista; reanudarla es una acción del operador). | **No cambia en NINGUNA pausa** (decisión aprobada): lo que calla el audio es el atajo de Silencio y el panel, no la pausa de la salida. |

Implementación de la pausa **sin tocar** `OutputPauseState`, `OutputSafety` ni el sondeo de monitores: igual que `DropAlertIfPaused`, una reacción dentro de `Publish()` que LEE la pausa que el snapshot ya informa (`window.isPaused` y su motivo) y cambia solo `Foreground.Phase`/el motor de audio. Está acotada, no lanza y se prueba con la app real.

---

## 4. Audio

### 4.1 Dos caminos, por lo que permite WebView2

| | Audio de un **video en primer plano** | **Pistas de audio sueltas** |
|---|---|---|
| Quién lo reproduce | El navegador de la salida (`<video>`). | **Motor de audio en C#** (WASAPI). |
| Dispositivo | **El predeterminado de Windows** (o el que el operador asigne a la aplicación en Windows ▸ Sonido ▸ Mezclador de volumen: `msedgewebview2.exe`/Oak-Proyektor). La página no puede elegir otro (spike §0). | **Elegible** en la aplicación (por id de dispositivo de Windows). |
| Volumen / fundidos | Volumen del clip × maestro (JS, rampas de ≈ 20 pasos). | Ganancia en el motor (rampas por muestra). |
| Pérdida del dispositivo | Windows redirige al predeterminado nuevo (comportamiento del sistema; no controlable). | La aplicación lo detecta y **pausa avisando** (§4.3). |

La asimetría es una **limitación honesta**, no una elección: ver §4.4 para la alternativa si el operador necesita el audio del video también en la mezcladora y no quiere tocar Windows.

### 4.2 Motor de audio en C# (`AudioEngine`, App; hilos propios, nunca el de UI)

- **Tecnología**: WASAPI en modo compartido con decodificación por Media Foundation (MP3, AAC/M4A, FLAC, WAV; WMA queda fuera de la lista). Dos formas de obtenerlo, a decidir con una prueba de 1 hora al empezar la P2 (con medición, no por gusto): **(a) NAudio** (MIT; dependencia nueva, versión exacta fijada, justificada en una línea antes de añadirla) o **(b) `Windows.Media.Playback.MediaPlayer`** (WinRT: sin dependencia NuGet, `AudioDevice` elegible, pero obliga a un TFM `net10.0-windows10.0.x`). Se interpone una interfaz `IAudioBackend` (lista de dispositivos, abrir, reproducir, volumen, parar) para probar todo el motor con un respaldo falso y no atar el diseño a una librería. En Windows N sin Media Foundation, MP3 y WAV siguen funcionando con decodificadores administrados.
- **Orden de comandos**: una cola de un consumidor (`Channel`), cada comando termina en memoria; `Stop` urgente no pasa por la cola: activa una bandera atómica que el hilo de audio lee en cada búfer (silencio inmediato con un fundido de 100 ms para evitar el chasquido) — por eso el atajo global puede cortar aunque la cola esté ocupada.
- **Pistas**: una pista activa a la vez en la v1 (música de fondo o acompañamiento); `Loop` opcional; **fundido de entrada y salida** configurables (0–5 s; por defecto 500 ms al entrar y 800 ms al salir; el «Detener» de emergencia usa 100 ms fijos).
- **Volumen maestro** (0–100, por defecto 80, guardado en `settings.json`): multiplica el volumen de cada pista y el del video en primer plano. Un tope de seguridad al activar: el volumen de una pista nueva nunca supera el maestro, y el primer arranque tras instalar no pasa del 50 %.
- **Estado de audio propio** (`AudioState`, con su `seq`, NO dentro de `OutputState`: el audio no vive en la ventana de salida): `{ track?, phase, positionMs, durationMs, deviceId, deviceName, masterVolume, deviceLost }`, publicado por un evento `audio.stateChanged` (a ≈ 4 por segundo solo la posición, vía `media.progress`).

### 4.3 Elegir dispositivo y qué pasa si se desconecta

- Los dispositivos se listan en C# (`audio.getDevices`: id estable de Windows, nombre, si es el predeterminado, si está activo). Se guarda el **id** (no el índice ni el nombre), como se hizo con los monitores; el nombre es solo para mostrar. Un dispositivo no conectado se muestra «no conectado».
- Sondeo de cambios (cada 1 s, hilo propio, **independiente** de `DisplayManager`) o notificaciones de Windows (`IMMNotificationClient`): dispositivo agregado, quitado, predeterminado cambiado.
- **Política «Si el dispositivo de audio desaparece»** (ajuste, igual que la de monitores): **«Avisar y esperar» (por defecto)** = la pista se pausa conservando la posición, el panel muestra «Dispositivo de audio desconectado: audio en pausa» con un botón por dispositivo disponible; **nunca salta sola a los altavoces del portátil** (sería peor que el silencio); si el MISMO dispositivo vuelve y la reanudación automática está activa, se reanuda; **«Usar el predeterminado de Windows»** es opcional.
- Sin dispositivo configurado = predeterminado de Windows.

### 4.4 La limitación del video con audio, y las salidas

1. **Recomendada (v1)**: poner la mezcladora como dispositivo predeterminado de Windows durante el culto, o asignar `msedgewebview2.exe` a ella en el Mezclador de volumen de Windows (una vez). La interfaz lo explica en la pantalla de ajustes de audio, junto al dispositivo de las pistas.
2. Alternativa, SOLO si el operador la necesita: reproducir el audio del video con el motor de C# (el `<video>` va silenciado y el motor decodifica la pista de audio del mismo MP4 por Media Foundation) con corrección de deriva a ≈ 4 Hz. Da elección de dispositivo y la misma política de pérdida, pero añade riesgo de desfase de labios y una segunda decodificación. Se evalúa con un spike propio si la opción 1 no basta; **no se construye en estas tres fases**.
3. Descartada: pedir el permiso de micrófono de WebView2 para ver las etiquetas (avisos de permiso, y Photino no expone el evento).

---

## 5. Listas de culto: elemento de tipo medio

- **Migración 14** (`0014_PlaylistMedia.sql`): `PlaylistItems` se RECONSTRUYE (patrón de las migraciones 3, 4 y 11; el `CHECK` de `ItemType` cambia) con el tipo `MEDIA` y las columnas `MediaId` (clave foránea `ON DELETE SET NULL` → `IsMissing`, como las canciones), `StartMode` (`PLAY` | `READY`), `OnEnd` (`BACK` | `STAY` | `NEXT`, nulo = el valor de los ajustes) y `Loop`. Sin perder ningún elemento (test sobre una base v13 con faltantes y repetidos). `Songs` gana `BackgroundMediaId` (nulo; clave foránea `SET NULL`): el fondo por canción pasa a usar la biblioteca sin perder el actual por ruta.
- **Al activarlo** (Enter / Ctrl+→ saltando, como el resto): un **video** pasa a primer plano; un **audio** empieza en el motor; según `StartMode`: `PLAY` arranca (con el fundido de entrada y el volumen maestro) y `READY` lo deja cargado en pausa en 0:00 esperando ▶. Por defecto `PLAY` para video y `READY` para audio suelto (un sonido no pedido en plena oración es el peor error posible; el operador lo cambia por elemento).
- **Al terminar** (`OnEnd`):
  - **Volver a lo anterior (`BACK`, por defecto)**: el video sale con fundido y se descubre lo que había debajo (la diapositiva anterior sigue en el estado, no hace falta reenviarla); un audio simplemente termina.
  - **Quedarse (`STAY`)**: el video queda en pausa en su último fotograma; un audio se detiene. El operador sigue cuando quiere (Esc quita).
  - **Seguir con el siguiente (`NEXT`)**: el backend emite `media.ended { next: true, origin }` y **el panel** (que ya tiene la navegación de la lista) activa el siguiente elemento proyectable. Con el panel congelado NO encadena (cae a `STAY`, que es seguro). `BACK` y `STAY` los ejecuta el backend sin depender del panel.
- **Activo derivado del estado**: el elemento activo se obtiene del `origin` de `Foreground` (video) o de `AudioState.origin` (audio), nunca de marcas del panel; `SlideOrigin.ItemType` admite `"media"`. Un elemento `MEDIA` faltante o no reproducible se salta (`Projectable = false`), con el mismo aviso «Falta: …».
- **Modo EDITAR**: añadir un medio desde la biblioteca (buscador de la biblioteca como el de canciones), cambiar `StartMode`, `OnEnd` y `Loop`; duplicar y borrar usan los comandos pequeños existentes.

---

## 6. Atajo global de emergencia para el audio

- **`Ctrl+Alt+Shift+S` («Silencio»)**: calla TODO el audio y pausa el video en primer plano **al instante**, aunque el panel esté congelado. Solo detiene; **nunca** reanuda ni hace de interruptor (un segundo pulso no resucita nada). Mismo modificador y mismo hilo de atajos que los demás; informe de registro por el mismo `HotkeyStatus`, así que el panel avisa si otra aplicación tiene la combinación. La «S» está libre (usadas: M, B, C, L, R y A) y, al llevar Alt, no choca con ningún atajo del panel ni con los reservados de Chromium. Respaldo dentro de la página de salida (como los otros): si el global no se pudo registrar, la misma combinación en la ventana de salida envía `media.stopAll`.
- Camino: el atajo llama directamente a `AudioEngine.StopAllNow()` (la bandera atómica de §4.2: no pasa por colas ni por el panel) y a `OutputController.PauseForegroundMedia()` (que cambia el estado y lo publica; la PÁGINA DE SALIDA, otro proceso, lo aplica aunque el panel esté atascado). Toma su número de `OrderClock` al pulsarse: un `media.play` ya numerado antes y procesado después **se ignora** (el mismo mecanismo que ya impide que un «mostrar» reviva una alerta quitada).
- Se prueba como el atajo de alertas: pulsación REAL con el panel congelado (`tools\i8-frozen-panel-test.ps1 -Full` ampliado), y que el sonido cesó medido con el **medidor de picos de Windows** (`IAudioMeterInformation` del dispositivo): sin oídos humanos, «hay señal» antes y «cero» después.

---

## 7. Formatos y mensajes claros

Lista cerrada v1 (la validación real es en el backend y se verifica en el navegador):

| Tipo | Se acepta | Notas |
|---|---|---|
| Video | `.mp4`, `.m4v` (H.264 + AAC recomendado), `.webm` (VP8/VP9/AV1 + Opus/Vorbis) | HEVC se acepta **solo si este equipo lo decodifica** (verificación del primer fotograma); AC-3/E-AC-3 en MP4: se avisa. |
| Audio | `.mp3`, `.m4a`, `.aac`, `.wav`, `.flac` | Lo que el motor de C# decodifica en Windows; Ogg/Opus y WMA se rechazan con su motivo (no los decodifica Media Foundation en todos los equipos). |
| Imagen | `.jpg`, `.jpeg`, `.png`, `.webp` | Como hoy. |

Mensajes (códigos estables `media.*`, texto en español puesto por el backend, nunca con rutas ni nombres del operador en los registros):

- `media.unsupportedContainer` — «Este archivo (.avi) no se puede usar. Conviértelo a MP4 (H.264 + AAC) con HandBrake o VLC y vuelve a importarlo.»
- `media.videoCodecUnsupported` (HEVC) — «Este video usa HEVC (H.265) y este equipo no lo reproduce. Conviértelo a H.264.» / si SÍ lo reproduce: aviso amarillo «Funciona en este equipo, pero depende de Windows y de la tarjeta gráfica; se recomienda H.264.»
- `media.audioCodecUnsupported` — «El audio de este archivo usa un formato raro (AC-3/DTS/Opus…). Conviértelo a AAC o MP3.»
- `media.audioCodecWarning` — el video se reproduce pero el audio puede no oírse en otros equipos.
- `media.decodeFailed` — «No se pudo reproducir: el archivo está dañado o incompleto.»
- `media.noAudioTrack` — «Este video no tiene audio» (el volumen se desactiva en el reproductor).
- `media.tooLarge`, `media.diskFull`, `media.sourceStalled`, `media.duplicate`, `media.notFound`, `media.missingFile`, `media.deviceLost`, `media.deviceNotFound`, `media.busy`, `media.playerBusy`.

---

## 8. Protocolo, migraciones y mensajes (versión siguiente: **v17**; las dos siguientes, v18 y v19, una por prompt)

| Versión | Contenido | Migración |
|---|---|---|
| **v17** (prompt 1) | Biblioteca. Queries `media.list`, `media.get`, `media.getSettings`; comandos `media.import`, `media.cancelImport`, `media.rename`, `media.move`, `media.delete`, `media.createFolder/renameFolder/deleteFolder`, `media.reportProbe` y `media.setThumbnail` (los manda el panel tras verificar), `media.verifyLibrary`, `media.setBackground { mediaId }` y `media.setSettings`; eventos `media.importProgress`, `media.importFinished`, `media.changed`. | 13 |
| **v18** (prompt 2) | Reproducción. `OutputState.Foreground` (`schemaVersion` 4); comandos `media.play`, `media.pause`, `media.resume`, `media.seek`, `media.setVolume`, `media.stop`, `media.stopAll`, `media.reportProgress` (lo manda la salida); queries `audio.getDevices`, `audio.getState`; comandos `audio.setDevice`, `audio.setMasterVolume`, `audio.setSettings`; eventos `audio.stateChanged`, `media.progress`, `media.ended`. | — (ajustes en `settings.json`, carga por bloque con aviso) |
| **v19** (prompt 3) | Listas. `PlaylistItemDto` y `PlaylistNewItemDto` con `mediaId`, `startMode`, `onEnd`, `loop`; `SlideOrigin.ItemType` admite `media`; ajuste `playlistDisplay.mediaOnEnd`; `song.get`/`song.save` con `backgroundMediaId`. | 14 |

**Carriles**: `media.stop`, `media.stopAll`, `media.pause`, `media.resume`, `media.setVolume`, `audio.setMasterVolume` → **Priority** (solo memoria y colas; nunca esperan); `media.play`, `media.seek` → **Slow** (resolver el archivo y validarlo toca el disco; con número de llegada para resolver la carrera con «detener»); `media.import`, `media.cancelImport`, `media.rename/move/delete`, carpetas, `media.setThumbnail`, `media.verifyLibrary`, `media.setBackground` → **Slow**; consultas (`media.list`, `audio.getDevices`…) → **Live** (cortas); `media.reportProgress` → Live (memoria). `Every_catalog_message_has_a_deliberate_lane` se actualiza.

**Estado**: `OutputState.Foreground` (§3.1) y `AudioState` aparte (§4.2). Freeze y `visibilityFor` se amplían como con las alertas (la capa no se congela; se oculta con Texto limpio, Negro y Logo). `ProtocolFormatTests`/`ProtocolContractTests` con la huella fijada por versión; los scripts de `tools/` y los fixtures de JS toman la versión de su constante única (ya existe `tests/protocol-version.mjs`).

**Privacidad**: los nombres de archivo y de elemento viajan solo en las consultas y en las respuestas que los necesitan; los eventos, errores y registros llevan ids y conteos. La ruta de origen de una importación viaja una vez (el comando) y no se registra.

---

## 9. Riesgos y preguntas abiertas

**Riesgos**

1. **GPU y decodificadores**: dos videos a la vez (fondo + primer plano) más el audio. Se mide en la P2 (fotogramas presentados, CPU/GPU); si degrada, pausar el fondo mientras el primer plano lo cubre.
2. **Audio por dos caminos** (§4.1): el del video no es elegible desde la aplicación. Es la limitación más visible para quien tenga una mezcladora externa.
3. **El navegador decide si un archivo «se reproduce»** (§2.5): un video que funciona al importar podría fallar si cambia el equipo o el runtime; por eso se reverifica con «Verificar biblioteca» y se comprueba al activarlo.
4. **Disco**: la biblioteca crece (4 GiB por archivo); no entra en los respaldos de la base; en una carpeta sincronizada (OneDrive) sería una subida enorme y bloqueos (el aviso ya existe).
5. **Importar desde una unidad que falla**: lecturas que se cuelgan; mitigado con el tiempo sin avance de 15 s, pero el controlador de la unidad puede tardar más en devolver el control (un hilo de copia aislado, nunca el carril en vivo ni el panel).
6. **Dependencia nueva de audio** (NAudio o WinRT): licencia, versión fijada, comportamiento en Windows N. Se justifica y verifica al empezar la P2.
7. **Latencia y desconexión de Bluetooth**: WASAPI compartido añade 100–200 ms y un dispositivo Bluetooth tarda en caerse; la política «avisar y esperar» lo cubre, no lo elimina.
8. **Hilo de UI de Photino**: el audio y la importación nunca corren en él; los comandos solo encolan (regla ya vigente).
9. **Miniaturas generadas por el panel**: dependen de que el panel esté abierto; no bloquean (una a la vez, cediendo el hilo).
10. **Cambio de `OutputState`** (`schemaVersion` 4): la salida y los tests de contrato deben cambiar a la vez.

**Derechos de autor** (solo se menciona, sin decisiones legales): la música, los videos y las imágenes que la iglesia importe pueden tener derechos de autor y de ejecución pública; **la licencia de uso es responsabilidad de la iglesia**; la aplicación no incluye ni descarga contenido, no gestiona licencias ni registra qué se ha reproducido más allá de contadores internos. Los medios importados viven solo en la carpeta de datos local, que no se versiona.

**Preguntas abiertas (decisiones tuyas)**

1. **Audio del video en primer plano**: ¿te basta con «dispositivo predeterminado de Windows / asignarlo en el Mezclador de volumen» (v1), o necesitas ya elegirlo en la aplicación (entonces hace falta el spike de §4.4 y se retrasa la v1)?
2. **Motor de audio**: ¿NAudio (dependencia nueva) o `MediaPlayer` de WinRT (cambia el TFM)? Se decide con la prueba corta de la P2; ¿tienes preferencia?
3. **Fondo debajo del primer plano**: ¿seguir sin pausarlo (recomendado) o pausarlo desde el principio?
4. **Pausa manual/de emergencia y el audio**: ¿callar la música también (propuesto) o dejarla sonando como en las pausas automáticas?
5. **Texto limpio / Negro / Logo y un video en primer plano**: ¿pausar y silenciar (propuesto) o solo ocultar?
6. **`StartMode` por defecto**: `PLAY` para video y `READY` para audio suelto (propuesto).
7. **Atajo `Ctrl+Alt+Shift+S` («Silencio»)** y que no sea interruptor.
8. **Límite por archivo** (4 GiB) y reserva de disco (1 GiB o 5 %).
9. **Etiquetas**: ¿bastan las carpetas en la v1?
10. **HEVC**: ¿se acepta cuando el equipo lo reproduce (propuesto) o se rechaza siempre?
11. **`ffmpeg`**: ¿lo dejamos como ayuda opcional (propuesto) o descartas incluso eso?
12. **Formatos de audio**: ¿alcanza con MP3/M4A/AAC/WAV/FLAC (sin Ogg/Opus/WMA)?

---

## 10. Plan de pruebas

**C# (xUnit; solo datos SINTÉTICOS generados por código):**
- `MediaProbeTests`: MP4 sintético mínimo (átomos escritos a mano) con `moov` al principio y al final, H.264/HEVC/AV1 y AAC/AC-3; WebM, MP3 con y sin Xing (duración aproximada marcada), WAV, FLAC, imágenes; entradas hostiles (átomos con tamaño absurdo, anidación profunda, archivo truncado, ceros): nunca lanza ni excede el presupuesto; 3 000 entradas aleatorias con semilla fija.
- `ImportSourceValidatorTests`: rutas de dispositivo, flujos alternativos, nombres reservados, red y extraíble ADMITIDAS, vacío, tamaño.
- `MediaLibraryRepositoryTests`: alta, duplicado por SHA, mismo nombre distinto contenido, renombrar, carpetas, mover, borrar con usos (listas, fondo de canción, fondo actual → faltante), `MISSING` tras borrar el archivo, límites de nombre, privacidad de los registros.
- `MediaImportServiceTests` (sistema de archivos y espacio libre FALSOS): disco lleno antes y durante, bloqueo sin avance (15 s con reloj falso), cancelación (parcial borrado), cierre a medias (limpieza al arrancar), una importación a la vez, ids y bytes en los eventos.
- `MediaTransportTests` (máquina de estados pura con reloj falso): play/pausa/seek/volumen/fin, `OnEnd` `BACK`/`STAY`/`NEXT`, maestros y pausa de la salida, orden de llegada (detener antes de reproducir), fundidos.
- `AudioEngineTests` con `IAudioBackend` falso: lista de dispositivos, ids estables, pérdida y regreso del dispositivo con cada política, rampas de fundido, volumen acotado, `StopAllNow` inmediato aunque la cola esté ocupada.
- `MediaIpcTests`, `MediaMigrationTests` (13 y 14: v12 → v13 y v13 → v14 sin perder elementos, patrón de `PlaylistMigrationTests`), contrato y huella por versión, carriles.

**JS (`npm test`, un archivo cada vez con tope de memoria):** capa de primer plano (`OutputScreen`) con el fondo intacto (mismo nodo, `currentTime`), respaldo a silenciado si el navegador rechaza el sonido, ocultar/pausar con cada maestro, Freeze que no la congela, fundidos; reproductor del panel (insignia derivada, buscar, volumen, tras recarga); biblioteca (lista virtualizada, importar con progreso, miniaturas una a una sin pasar de ≈ 50 ms por tarea); medición de carpetas y búsqueda; elemento `MEDIA` de la lista.

**Reales (app real Debug, salida en ventana normal, datos temporales; el material se GENERA dentro de la salida con `MediaRecorder`, como `media-test.ps1`):**
- `tools\media-library-test.ps1`: importar un MP4 generado y uno con nombre extraño, duplicado, espacio en disco simulado, verificación y miniatura reales.
- `tools\media-playback-test.ps1`: primer plano con el fondo corriendo (mismo nodo, `currentTime` continuo, fotogramas presentados), cada maestro, Freeze, pausa de la salida, buscar y volumen.
- Audio: con el **medidor de picos de Windows** (`IAudioMeterInformation`) sobre el dispositivo: señal > 0 al reproducir, 0 tras detener, y 0 tras el atajo real.
- `tools\i8-frozen-panel-test.ps1 -Full` ampliado: con el panel congelado, `Ctrl+Alt+Shift+S` (pulsación REAL) calla el audio y pausa el video; la salida sigue a ~62 fps.
- No automatizable (queda como prueba manual con instrucciones): desconectar un dispositivo USB/Bluetooth real a mitad de una pista, la mezcladora real, el proyector físico.

---

## 11. Partición en 3 prompts

**Prompt 1 — Biblioteca (protocolo v17, migración 13).** Tablas y repositorio; `ImportSourceValidator`, `MediaImportService` (copia con hash, espacio, vigilancia de bloqueo, cancelación, limpieza), `MediaProbe` (cabeceras), servir por id con el validador existente, `media.setBackground`; mensajes `media.*` de la biblioteca y sus carriles; en el panel, la **pestaña «Biblioteca»** (carpetas, búsqueda virtualizada, importar con progreso, renombrar/mover/borrar con usos, miniaturas y verificación en el navegador, elegir fondo/imagen desde ella). Sin audio ni primer plano. Pruebas C# y JS, `tools\media-library-test.ps1` y documentación en `CLAUDE.md`.

**Prompt 2 — Reproducción y audio (protocolo v18).** Empieza con la prueba corta NAudio vs `MediaPlayer` y la medición de GPU con fondo + primer plano. `OutputState.Foreground` y su capa en la salida; transporte `media.*`; `MediaTransport` puro; reacción a la pausa y a los maestros (patrón de las alertas); `AudioEngine` + `IAudioBackend` (dispositivo, volumen maestro, fundidos, pérdida del dispositivo con política); atajo global `Ctrl+Alt+Shift+S` y su respaldo en la salida; reproductor del panel, ajustes de audio y su insignia derivada. Pruebas C#/JS y las reales con el medidor de picos y el panel congelado.

**Prompt 3 — Listas, fondo por canción y cierre (protocolo v19, migración 14).** Tipo `MEDIA` en `PlaylistItems` (reconstrucción sin perder nada), `StartMode`/`OnEnd`/`Loop`, navegación y activo derivados del `origin`, `media.ended { next }` con el panel, modo EDITAR (añadir desde la biblioteca), `Songs.BackgroundMediaId`, mensajes de formatos no soportados completos, prueba de resistencia (`soak`) con video en primer plano + audio + texto, y la documentación final en `CLAUDE.md`.

Cada prompt empieza con el árbol limpio (commit previo) y termina informando por separado lo verificado y lo no verificado.


## Estado del prompt 1 (biblioteca de medios; 2026-11-26)

Hecho: migración 13, importación con copia y SHA-256, duplicados por contenido, disco lleno, abandono a los 15 s sin avance, sonda de cabeceras (duración y códec), verificación y miniaturas en el navegador del panel, pestaña «Biblioteca», protocolo v17 y scripts de `tools/` con la versión leída de `protocol.ts`.

Desviaciones respecto al diseño:
- No existen `media.getSettings/setSettings` en la v17: los límites (4 GiB, reserva `max(1 GiB, 5 %)`, 15 s, 200 KB de miniatura) son constantes en `MediaLimits`. Se pueden pasar a ajustes cuando haga falta.
- Un formato claramente no soportado se rechaza al importar por sus cabeceras; `unsupported` como estado solo aparece cuando el navegador no logra reproducir un archivo ya importado.
- `media.cancelImport` va por el carril en vivo (solo activa un token), no por el lento.

Decisiones tomadas en las preguntas abiertas (recomendaciones del diseño): el fondo sigue sin pausarse bajo un video en primer plano; Texto limpio/Negro/Logo pausan y silencian el video en primer plano; modo de inicio PLAY para video y READY para audio; límite de 4 GiB por archivo y reserva de disco `max(1 GiB, 5 %)`; carpetas planas sin etiquetas; ffmpeg solo como ayuda opcional (no es necesario); audio MP3/M4A/AAC/WAV/FLAC.