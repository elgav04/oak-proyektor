# Oak-Proyektor

Software de proyección para iglesias. Reemplazo de EasyWorship 6 para uso interno (no comercial).
Prioridad absoluta: **estabilidad en vivo**. La ventana de proyección nunca debe congelarse, parpadear ni cerrarse sola durante un culto.

## Principios (en orden de prioridad)

1. **Estabilidad > funciones.** Ante la duda, la opción más simple y predecible.
2. **El backend C# es la única fuente de verdad del estado.** React y la ventana de proyección son vistas; no guardan estado de negocio.
3. **La salida es "tonta".** La ventana de proyección solo renderiza el `OutputState` que recibe; no consulta BD ni decide nada.
4. **Offline-first.** Nada depende de internet para funcionar en un culto.
5. **Datos del usuario a salvo.** Escrituras en SQLite transaccionales; nunca perder canciones o servicios por un cierre inesperado.

## Stack

- **Runtime:** .NET 10 (LTS). `net8.0` y `net9.0` están fuera de soporte desde el 10-nov-2026; no uses esas versiones. Antes de empezar, verifica con `dotnet --list-sdks`.
- **Desktop:** Photino.NET sobre WebView2 (Windows 10/11 es el objetivo principal).
- **BD:** SQLite con `Microsoft.Data.Sqlite` + Dapper. FTS5 para búsqueda.
- **Frontend:** React + TypeScript (strict) + Vite + Tailwind CSS + shadcn/ui + Zustand + `@dnd-kit` + `react-hotkeys-hook`.
- **IPC:** mensajes JSON tipados (Command / Event / Query / Reply) sobre `SendWebMessage` y `RegisterWebMessageReceivedHandler` de Photino.

## Estructura

```
Oak-Proyektor/
├─ CLAUDE.md
├─ .gitignore
├─ backend/
│  ├─ Oak-Proyektor.sln
│  ├─ Directory.Build.props         # net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors en Release
│  ├─ src/
│  │  ├─ OakProyektor.App/          # Program.cs, composición, Photino (ejecutable)
│  │  ├─ OakProyektor.Core/         # dominio, OutputState, comandos (sin dependencias de UI ni BD)
│  │  ├─ OakProyektor.Data/         # SQLite, esquema, migraciones, repositorios
│  │  └─ OakProyektor.Ipc/          # protocolo de mensajes, serialización
│  └─ tests/
│     └─ OakProyektor.Tests/        # xUnit
├─ frontend/
│  └─ src/
│     ├─ control/                   # panel del operador
│     ├─ output/                    # página de la ventana de proyección (mínima y ligera)
│     ├─ ipc/                       # cliente y tipos del protocolo
│     ├─ components/ui/             # shadcn
│     └─ lib/utils.ts               # cn() de shadcn
└─ data/                            # BD, backups/ (copias previas a migrar), logs/, settings.json; en .gitignore
```

El build va a `frontend/dist/` (en `.gitignore`); el build de App lo copia a su salida como `wwwroot`; el target de MSBuild `CheckFrontendBuild` **falla en Release y avisa en Debug (OAK001)** si `frontend/dist` falta o es más viejo que las fuentes del frontend (se desactiva con `-p:FrontendCheck=false`). Los paquetes npm están fijados sin `^`; TypeScript se queda en 6.x porque typescript-eslint no soporta 7.

Dos páginas de entrada en Vite: `index.html` (control) y `output.html` (proyección). La de salida no carga shadcn ni Zustand si no hace falta.

## Comandos

```
dotnet build backend/Oak-Proyektor.sln
dotnet test  backend/Oak-Proyektor.sln
dotnet run   --project backend/src/OakProyektor.App
cd frontend && npm install && npm run dev      # servidor Vite
cd frontend && npm run build                   # genera estáticos para Photino
cd frontend && npm run typecheck && npm run lint
cd frontend && npm test                        # tests de JS (node:test + Vite; sin dependencias extra)
```

Orden: `npm run build` en `frontend/` ANTES de `dotnet build`/`dotnet run` (el build de App copia `frontend/dist` a `wwwroot`; en Release, un `dist` desactualizado es un error). **Dónde se guardan los datos** (`ApplicationPaths`): en **producción** `%LOCALAPPDATA%\Oak-Proyektor` (junto al ejecutable, p. ej. `Program Files`, no se puede escribir); en **desarrollo** `data/` del repositorio (se detecta subiendo desde el ejecutable hasta `CLAUDE.md`); `OAK_DATA_DIR` manda sobre ambos. Dentro: `oak-proyektor.db`, `backups/`, `logs/`, `settings.json` y `cache/` (incluye el perfil de WebView2 en `cache/WebView2`). `OAK_NO_DIALOG=1` desactiva el cuadro de error de arranque (pruebas automáticas). La configuración persistente (`AppSettings`) está en `settings.json`; un archivo ausente o corrupto no impide arrancar.

Si un comando no existe todavía, créalo y documéntalo aquí.

## Convenciones C#

- `Nullable` y `ImplicitUsings` activados; `TreatWarningsAsErrors` en Release.
- Todo I/O es `async` con `CancellationToken`. Nada de `.Result` ni `.Wait()`. **Única excepción documentada:** `Main` hace `RunAsync().GetAwaiter().GetResult()`, porque `Main` no puede ser `async` (Roslyn no copia `[STAThread]` al punto de entrada sintetizado y WebView2 exige STA); el apagado (`ApplicationHost.DisposeAsync`) es asíncrono, con un límite de 3 s.
- DTOs y mensajes como `sealed record`. Servicios por interfaz con inyección de dependencias (`Microsoft.Extensions.DependencyInjection`).
- Sin estado estático mutable. Las opciones compartidas de serialización (`IpcSerializer.Options`) son de solo lectura desde su creación (`MakeReadOnly`).
- Logging con `ILogger`, a archivo rotativo en `data/logs/`. Handlers globales para excepciones no controladas (`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`): registrar, no cerrar la proyección.
- Un fallo en el panel de control **no** debe cerrar la ventana de salida.
- Nunca asumas que Photino es seguro entre hilos: serializa los envíos hacia las ventanas por un único canal y verifica en la documentación o el código del paquete cómo despachar al hilo de UI.
- **Photino (verificado con un spike en Windows 11, Photino.NET 4.0.16):**
  - `Main` debe ser `[STAThread]`; en MTA la ventana queda negra sin error.
  - `Monitors` solo se puede leer tras crear la ventana principal (usa `RegisterWindowCreatedHandler`). No existe evento de cambio de monitores: `DisplayManager` sondea `Monitors` cada 500 ms y compara.
  - La ventana de salida se construye y arranca en el hilo de UI: `main.Invoke(() => { new PhotinoWindow(main)…; w.WaitForClose(); })`. Construirla en un hilo de trabajo provoca interbloqueo.
  - Las páginas cargan por `file://` (`Load(ruta)`); no hace falta esquema `app://`.
  - Ventana sin bordes con posición y tamaño exactos del monitor (`MonitorArea`, píxeles físicos); no hace falta `SetFullScreen`.
  - **No llames `SendWebMessage` a una ventana hasta que su página haya enviado su primer mensaje**: el WebView2 se inicializa de forma asíncrona y enviar antes provocó una violación de acceso nativa (`0xC0000005`) que cierra el proceso. `WindowRegistry` lo aplica; las páginas piden su estado al cargar (`output.getState`), así que nada se pierde.
  - Todo callback de Photino (handlers de mensajes, cierre, creación) debe capturar sus excepciones: no pueden cruzar la frontera nativa. Una excepción no controlada en un hilo de fondo termina el proceso en .NET y no se puede evitar, por eso todo trabajo de fondo captura las suyas.
  - Ambas ventanas comparten el hilo de UI: un handler lento en C# congela panel y salida a la vez. Los handlers solo encolan; el trabajo se hace en otro hilo.
  - **El proceso no es DPI-aware**: Photino y `DisplayInfo` usan píxeles físicos, pero `GetWindowRect`/`SetWindowPos` devuelven coordenadas virtualizadas en monitores escalados (verificado: 1440×740 con Photino vs 1152×592 con Win32 al 125 %). Toda llamada Win32 con coordenadas va dentro de `NativeMethods.UsePhysicalCoordinates()`; no cambies la conciencia DPI del proceso (afectaría a Photino).
  - Pausa y restauración de la salida se hacen con Win32 sobre su `WindowHandle` (`ShowWindow`, `SetWindowPos`), despachadas al hilo de UI. Única excepción: la pausa de emergencia actúa directamente, sin cola de UI ni candado de ciclo de vida, para funcionar aunque haya una operación lenta en curso.
  - El atajo global usa `RegisterHotKey` con `hWnd` nulo en un hilo propio con su bucle de mensajes (Photino no expone el suyo). `SetThreadExecutionState` es por hilo: siempre se llama en el hilo de UI, que vive toda la sesión.
  - La ventana de salida se registra en cuanto se construye (antes de configurarla) y se cierra con hasta 3 reintentos más `WM_CLOSE`; si sigue existiendo, NO se declara cerrada (el panel la sigue mostrando y se puede reintentar). El handler de cierre captura sus excepciones.
  - **Versiones fijadas a exactas** (hallazgo I7): `Photino.NET` `[4.0.16]` y `Photino.Native` `[4.0.22]` (los corchetes; `4.0.16` a secas significa «esa o superior»). `PhotinoPinTests` falla si cambian: es el recordatorio de repetir la prueba manual de abajo. `OutputController.CreateWindow` lleva una advertencia junto a `WaitForClose()`: depende de un comportamiento no documentado (dentro de `main.Invoke` crea la ventana y vuelve; desde el handler `WindowCreated` BLOQUEA hasta que se cierra la ventana).
  - **Perfil de WebView2** (hallazgo I10): por defecto Photino lo deja en `%LOCALAPPDATA%\Photino\EBWebView`, compartido con cualquier otra aplicación Photino; se movió a `<datos>\cache\WebView2` con `SetTemporaryFilesPath`, en **AMBAS** ventanas y con la MISMA carpeta (`WebViewProfile`), porque así comparten entorno (ver I8). Si solo una ventana lo cambiara, dejarían de compartirlo. Es una caché regenerable y la interfaz es prioritaria: `WebViewProfile.Choose` comprueba que se pueda escribir y, si no, cae a `%TEMP%\Oak-Proyektor\WebView2` y luego al predeterminado de Photino. **Sin ese respaldo una carpeta de datos imposible deja el panel en NEGRO** (WebView2 no avisa: no puede crear su perfil y no dibuja), y el operador no vería ni el aviso de la base de datos (verificado).
  - **Ni Serilog ni WebView2 avisan si no pueden escribir** (`Serilog.Sinks.File` abre el archivo al primer mensaje y el error se pierde): se comprueba la escritura de verdad con un archivo de prueba (`WritableDirectory`) antes de confiar en una carpeta. El logger (`LoggerSetup`) prueba carpeta de datos → `%TEMP%\Oak-Proyektor\logs` → silencioso, y nunca impide arrancar; un fallo de arranque muestra un cuadro de diálogo (`StartupFailure`).
  - **Instancia única** (hallazgo I11): un Mutex con nombre (`Local\Oak-Proyektor-<hash de la carpeta de datos>`) que NO se toma (no depende del hilo). Dos procesos sobre la misma carpeta de datos chocarían en el log y la BD; una instalada y otra de desarrollo (carpetas distintas) conviven. Se comprueba ANTES de crear el log. La segunda instancia trae al frente la ventana de control de la primera (solo si el proceso ejecuta el mismo `.exe`; restaura si está minimizada) y sale con código 0.
  - **Panel minimizado** (hallazgo M10): Windows lo aparca en (-32000, -32000), fuera de todos los monitores; `PanelCenterTracker` conserva la última posición real para que siga contando como «aloja el panel» y la salida automática no se abra sobre el monitor del operador.
  - **PRUEBA MANUAL OBLIGATORIA tras actualizar Photino.NET, Photino.Native o el runtime de WebView2 (aislamiento panel/salida, hallazgo I8).** Panel y salida comparten el entorno de WebView2 predeterminado, y que un bloqueo del JS del panel no afecte a la salida depende de cómo WebView2 reparte los procesos de renderizado (puede cambiar entre versiones). Pasos: (1) `npm run build` y compilación **Debug** (DevTools solo se habilita en Debug); (2) abrir la salida en el proyector y poner Live para que muestre algo; (3) en el panel abrir DevTools (clic derecho → Inspeccionar) y en la consola ejecutar `while (true) {}`; (4) esperado: el panel se congela pero la salida sigue mostrando su contenido, sin parpadear ni ponerse en negro, y el atajo Ctrl+Alt+Shift+M sigue minimizándola (prueba que el hilo de UI de C# sigue vivo). Además, comprueba (a) que abrir la salida NO congela el panel (el `WaitForClose` de arriba) y (b) la estructura de procesos de WebView2 con las dos ventanas abiertas: `Get-CimInstance Win32_Process` filtrando `msedgewebview2.exe` entre los descendientes de la app debe dar 1 navegador, **2 renderizadores** (uno por ventana), GPU, crashpad y 2 utilidades, todos con `--user-data-dir=<datos>\cache\WebView2\EBWebView` (línea base 2026-11-19). Si la salida se congela o se pone en negro: el aislamiento se rompió; dar a cada ventana su propio entorno de WebView2 (`SetTemporaryFilesPath`) y repetir la prueba. **No cambies el entorno de WebView2 mientras la prueba pase.** Última verificación del bucle infinito: 2026-11-18 con Photino.NET 4.0.16 (Photino.Native 4.0.22) y WebView2 157.0.4341.60: la salida siguió funcionando. Tras mover el perfil de WebView2 (2026-11-19) se verificó la estructura de procesos (idéntica a la anterior), pero **el bucle infinito en el panel no se repitió**: repítelo a mano antes del primer culto con esta versión. Al repetirla, actualiza esta línea y el comentario de `ApplicationHost.cs`.

## Convenciones TypeScript / React

- `strict: true`, prohibido `any`. Los mensajes IPC son uniones discriminadas por `type`.
- Zustand solo como **réplica** del estado que manda C#: el cliente envía comandos y reacciona a eventos; no muta estado de negocio localmente.
- Atajos de teclado centralizados (un solo archivo de mapa), nunca dispersos por componentes.
- Componentes del panel de control optimizados para operar rápido: foco por teclado, objetivos grandes, estados claros (EN VIVO / listo).
- La página de salida está protegida por `OutputErrorBoundary` (M13): ante un error de render conserva el último estado que sí se pintó (sin negro) y reintenta cuando llega un snapshot de otra `seq` (`OutputScreen` le pasa la secuencia global como `resetKey`; NO `revision`, que desde el protocolo v2 solo cuenta cambios de los controles maestros: un snapshot con otro texto y la misma revisión nunca provocaría el reintento, hallazgo I-C). Sin boundary, React desmonta todo el árbol y la salida queda en negro aunque llegue un snapshot válido (reproducido en la ventana real). Prueba manual: `npm run dev` y abrir `/output-probe.html` (solo desarrollo; no entra en el build). Los tests de JS se ejecutan con `npm test`.

## Protocolo IPC

Sobre cada mensaje va un sobre común:

```json
{ "v": 3, "id": "uuid", "kind": "command|event|query|reply", "type": "output.showSlide", "payload": { } }
```

- `command`: la UI pide algo a C#. `event`: C# informa un cambio. `query`/`reply`: petición con respuesta correlacionada por `id`.
- Los tipos viven en C# (`OakProyektor.Ipc`) y se reflejan a mano en `frontend/src/ipc/`. Un test de contrato debe fallar si divergen.
- Versiona el protocolo (`v`; **actual: 3**, en `IpcCatalog.ProtocolVersion` y `PROTOCOL_VERSION`). Un mensaje de otra versión no se interpreta: se ignora con log. Cambia `v` cuando cambie el formato de cualquier mensaje.
- La ventana de salida recibe snapshots completos de `OutputState` (idempotentes), no diffs. Cada snapshot lleva `revision` creciente; las réplicas descartan los más viejos.
- Mensajes actuales (catálogo en `IpcCatalog` y `frontend/src/ipc/protocol.ts`; un test de contrato compara ambos): commands `output.open` (`{ displayId }`), `output.close`, `output.setMaster`, `output.recover` (pausa del operador, `{ reason: "manual" | "emergency" }`; v3), `settings.setAutoRestore`; queries `display.list`, `output.getState`; events `display.changed`, `output.stateChanged`, `command.failed`, `database.statusChanged`; query `database.getStatus` (estado de la BD: `initializing | ready | failed`, con `message` y `backupPath` si falló).
- **Entrada con dos carriles** (`InboundProcessor`): los handlers de Photino solo encolan (`InboundQueue`); un enrutador interpreta y reparte. `output.setMaster` y `output.recover` (`IpcPriority`) van por su propio carril y NUNCA esperan detrás de comandos lentos como `output.open`; el resto va por el carril normal (FIFO, un mensaje a la vez). El orden entre carriles NO está garantizado; dentro de cada uno sí. Para resolver las carreras entre carriles, el enrutador numera cada mensaje al recibirlo (`OrderClock` → `IpcEnvelope.Order`, que NO viaja en el JSON) y el atajo global toma su número al pulsarse: `output.open` y la pausa comparan esos números (ver «Emergencia» abajo). Salida: un único canal FIFO (`IOutbox`/`Outbox`) hacia ambas ventanas; si se llena, conserva solo el último snapshot de cada tipo y destino (los replies, errores y demás no se compactan).
- **Secuencia global (`seq`)**: todo snapshot (`output.stateChanged`, `display.changed` y los replies de `output.getState` / `display.list`) lleva un `seq` que cambia con CUALQUIER cambio de su estado, incluido el de la ventana de salida; si nada cambió, no se publica. Construir el snapshot, asignarle el `seq` y encolarlo ocurre bajo un mismo candado (`SequencedSnapshotPublisher`), y un reply publica antes cualquier cambio pendiente. Las réplicas (panel y salida) descartan snapshots con `seq` menor o IGUAL al último aplicado (`isNewer`). `OutputState.revision` solo cuenta cambios de los controles maestros y ya no sirve para ordenar.
- **Monitores por ID, nunca por índice**: `DisplayInfo.Id` (`DisplayId.From`, derivado del rectángulo porque Photino no da ID de dispositivo) es la única forma de referirse a un monitor en el protocolo (`output.open.displayId`, `window.displayId`) y en la UI. «Monitor N» es solo una etiqueta de presentación. `window.displayId` sigue indicando el monitor asignado aunque ya no esté conectado. **Solo `null` significa «automático»** (`DisplayResolver`, función pura en Core): un `displayId` explícito que no está conectado falla con `output.displayNotFound` y NUNCA cae a otro monitor (antes abría la salida a pantalla completa en uno que nadie eligió); la ventana existente, incluida la que está en pausa porque el proyector se desconectó, se conserva tal cual. En el panel, «Reanudar salida» vuelve siempre al monitor que ya tenía la salida y solo se ofrece si sigue conectado; abrirla en otro monitor es una acción distinta y explícita («Abrir en el monitor elegido»). Si la salida está en pausa por `displayLost`, su monitor no está y hay OTRO que no aloja el panel (el proyector volvió con otro ID, otra resolución o puerto), el panel avisa «Apareció un monitor distinto al de la salida» con un botón por monitor disponible; nunca se abre sola. Si el monitor de la salida ya está conectado de nuevo, el panel NO dice «Proyector desconectado» (`describeOutput`, función pura en `frontend/src/control/outputNotice.ts`).
- **Errores correlacionados por id**: todo reply es `{ ok, result, error }`; una query fallida (o desconocida) responde `ok: false` con `{ code, message }` y el cliente rechaza la promesa con ese error (`IpcRequestError`) al instante, sin esperar el timeout. Un comando fallido (o desconocido) genera el evento `command.failed` (`{ commandId, commandType, error }`) hacia la ventana que lo envió; el panel lo muestra y se puede cerrar. Los handlers lanzan `IpcException(code, message)` para fallos esperados (`output.pageMissing`, `output.openFailed`, `output.closeFailed`, `output.cannotResume`, `output.displayNotFound`); cualquier otra excepción se reporta como `internal` sin detalles (van al log). Solo se descartan en silencio los mensajes sin id correlacionable (JSON roto, otra versión) y los `event`/`reply` entrantes.
- **Conexión del panel**: reintenta INDEFINIDAMENTE con espera creciente (500 ms … 8 s, `createRetrier`), se marca conectado con CUALQUIER mensaje válido, y tiene el botón «Reconectar» (reintenta ya y reinicia la espera). La página de salida pide su estado inicial con el mismo reintento.
- **Política de seguridad de la salida** (`OutputSafetyPolicy`, función pura en Core; la aplica `OutputController` en cada ciclo de sondeo de 500 ms). Verificada con un arnés sobre el equipo real, salvo la desconexión física del cable:
  1. Pantalla completa sin bordes solo en un monitor que NO aloja el panel (por defecto, el primero que no lo aloja). Con un solo monitor, o si se elige el del panel, se abre como ventana normal de 960×540, sin topmost, y esta política no aplica.
  2. Topmost solo mientras la ventana esté TOTALMENTE dentro del monitor de la salida y este no sea el del panel.
  3. Si el monitor de la salida desaparece (con antirrebote de 2 ciclos ≈ 1 s, para que un parpadeo de enumeración no deje la proyección en negro), o la salida se cruza con el monitor del panel (o el panel pasa al monitor de la salida): se quita topmost y se minimiza (**pausa**), sin cerrar la ventana ni perder el `OutputState`. El panel muestra «Proyector desconectado: salida en pausa» (o el aviso del motivo). El monitor se identifica por su ID (`DisplayId`, derivado del rectángulo; Photino no da ID de dispositivo).
  4. Si reaparece el mismo monitor (mismo ID) y no aloja el panel, se restaura en su posición exacta y vuelve a topmost, solo si `AutoRestoreOutput` está activo (por defecto sí; casilla en el panel; se guarda en `data/settings.json`). Solo se reanudan solas las pausas automáticas (`displayLost`, `panelCrossed`); las manuales y de emergencia esperan al operador («Reanudar salida» = `output.open`).
  5. Emergencia: el atajo global **Ctrl+Alt+Shift+M** (funciona con cualquier ventana enfocada) y el botón «Recuperar control» (`output.recover`) minimizan la salida y le quitan topmost. Respaldo: la misma combinación dentro de la página de salida (envía `output.recover` con `reason: "emergency"`, así que el panel dice «atajo de emergencia» y no «recuperación manual»), por si el global no pudo registrarse (otra aplicación lo usa); el panel indica cuál está activo. **La pausa es ATÓMICA y nunca se deshace por una carrera** (`OutputPauseState`, máquina de estados pura en Core, probada sin Photino; el controlador solo llama a sus transiciones bajo `_gate`): prioridad de motivos `Emergency` > `Manual` > automáticos (`displayLost`, `panelCrossed`), un motivo nunca se rebaja y solo los automáticos se reanudan solos, y solo si el motivo sigue siendo el que se evaluó; si el operador pulsa «Mover / reabrir» y enseguida «Recuperar control», el resultado final es pausada aunque el carril de prioridad atienda la pausa primero (el `output.open` numerado antes de esa pausa no la deshace; la ventana nueva nace en pausa); una pausa pedida con la ventana aún creándose cambia el estado y el aviso al panel al instante y se aplica en cuanto la ventana existe; la pausa de emergencia minimiza con `ShowWindowAsync` + `SWP_ASYNCWINDOWPOS`, sin esperar al hilo de UI; y mientras haya pausa, el sondeo de 500 ms vuelve a minimizar la ventana si alguien la restaura (barra de tareas, Alt+Tab). Si el monitor de la salida desaparece y la ventana YA cruza el monitor del panel (Windows la movió allí), la pausa es inmediata, sin el antirrebote de ~1 s.
  6. Mientras la salida esté abierta (también en pausa) se impide que la pantalla se apague o el equipo se suspenda (`ScreenWakeLock`).
  La salida nunca se cierra ni se mueve por sí sola.

## Modelo de salida por capas

`OutputState` describe tres capas más controles maestros:

1. **Fondo:** color, imagen o video en loop. **No debe reiniciarse ni remontarse al cambiar el texto** (clave estable en React, elemento `<video>` persistente).
2. **Contenido:** texto con transición crossfade.
3. **Superposiciones:** alertas, lower third / ticker.

Controles maestros: `Live`, `ClearText` (deja solo fondo), `Black`, `Logo`, `Freeze`. Deben responder de forma instantánea y tener prioridad sobre cualquier transición en curso.

## Base de datos

- `PRAGMA foreign_keys = ON`, modo WAL, versión de esquema con `PRAGMA user_version` y migraciones numeradas.
- FTS5 con tabla de contenido externo y triggers de sincronización. Tokenizer `unicode61 remove_diacritics 2` para que "Jesús" coincida con "jesus".
- Las búsquedas por referencia (`Jn 3:16`) usan un parser propio, no FTS.
- Crear la BD automáticamente al primer arranque. Todo cambio de esquema pasa por una migración.
- **Migraciones** (`Data/Migrations/NNNN_Nombre.sql`, embebidas; las aplica `MigrationRunner`):
  - **Una migración publicada NO se edita nunca**: quien ya la aplicó no volvería a ejecutarla. Para corregirla, crea una nueva. `MigrationHashTests` fija el SHA-256 de cada una (ignora BOM y saltos de línea) y falla si cambia; al añadir una migración nueva, agrega su hash en ese test.
  - Garantías del runner: antes de aplicar la primera pendiente sobre una BD con datos hace una copia con `VACUUM INTO` en `data/backups/` (si la copia falla, NO migra); `PRAGMA foreign_keys = OFF` ANTES del `BEGIN` (dentro de una transacción no tiene efecto) y `PRAGMA foreign_key_check` ANTES del `COMMIT` (si hay referencias rotas, se revierte); `foreign_keys = ON` siempre al terminar; cada migración y su `user_version` van en UNA transacción. Si una falla, la BD queda en la versión anterior, la copia sigue ahí y el panel recibe `database.statusChanged` con `failed`, el mensaje y la ruta de la copia.
  - Reconstruir una tabla: crear la nueva, copiar, `DROP` de la vieja y **renombrar la nueva**; NUNCA renombrar la vieja y crear otra (SQLite ≥ 3.26 reescribe las claves foráneas de las hijas hacia la tabla renombrada). Después, recrear índices y triggers (el `DROP` se los lleva) y reconstruir el FTS (`INSERT INTO X(X) VALUES('rebuild')`).
- **`INSERT OR REPLACE` y `REPLACE INTO` están PROHIBIDOS** (`SourceRulesTests` falla si aparecen en `backend/src`): `REPLACE` borra la fila vieja por debajo, con cascada silenciosa sobre las hijas (reemplazar un `Book` borra sus `Verses`) y sin disparar los triggers DELETE salvo que `recursive_triggers` esté activo. Usa `INSERT … ON CONFLICT(…) DO UPDATE SET …`. Toda conexión debe salir de `ISqliteConnectionFactory`, que activa `PRAGMA foreign_keys = ON` y `PRAGMA recursive_triggers = ON` (este último es por conexión: una conexión creada a mano lo pierde). Tras una importación masiva, ejecuta el `integrity-check` del FTS (`INSERT INTO VersesFts(VersesFts, rank) VALUES('integrity-check', 1)`).
- Los triggers de actualización se declaran con `AFTER UPDATE OF <columnas que el índice usa>` (incluido `Id`, que es el rowid del FTS).
- **Servicios (`PlaylistItems`)**: cada elemento referencia con una clave foránea real a su canción, presentación o libro (`ON DELETE SET NULL`) y guarda un `Title` instantánea. Borrar un registro incluido en un servicio NO se bloquea (una canción usada una vez sería imborrable) pero tampoco deja una referencia rota: el elemento queda con `IsMissing = 1`. Quien abra el servicio DEBE avisar al operador de los elementos faltantes («Falta: …») y nunca presentarlos como si estuvieran. Los elementos nuevos deben referenciar un registro existente.
- **La BD se inicializa en segundo plano** y su resultado es una señal (`IDatabaseReadiness`): todo repositorio abre sus conexiones con `OpenWhenReadyAsync` (espera a que esté lista; si falló lanza `DatabaseUnavailableException`). La proyección no depende de la BD y sigue funcionando si esta falla. Dapper devuelve `INTEGER` como `Int64`: los modelos con `int` necesitan mapeo explícito.

## Biblias y derechos de autor

- **El texto de la RVR1960 tiene derechos de autor** y la marca "Reina-Valera 1960" requiere licencia. **Nunca incluyas ningún texto bíblico con derechos en el repositorio, en tests ni en el instalador.**
- Las biblias se cargan con un importador (Zefania XML, JSON). Para tests usa un texto de dominio público o datos sintéticos pequeños.
- `data/` está en `.gitignore`.

## Alcance actual

- **No implementar Stage Display.**
- El procesador de `.pptx` es una fase posterior. No lo empieces hasta que se pida. La rasterización sin Office instalado no está resuelta: se evaluará LibreOffice en modo headless u otra alternativa.
- No agregues funciones fuera del pedido actual.

## Cómo trabajar en este repo

- Trabaja en pasos pequeños y **compila y ejecuta los tests después de cada paso**. No des por hecho que algo funciona sin haberlo corrido.
- **No inventes APIs.** Si no estás seguro de un método de Photino, Dapper o shadcn, lee el código fuente o la documentación del paquete instalado, o escribe un spike mínimo para comprobarlo.
- Antes de añadir una dependencia, justifícala en una línea y verifica su versión real (`dotnet package search`, `npm view`).
- Para cambios que toquen varios archivos o el diseño, propón un plan breve antes de escribir código.
- Al terminar cada tarea, informa por separado: qué **verificaste** (compilación, tests, ejecución) y qué **no pudiste verificar** (por ejemplo, comportamiento con un segundo monitor real).
- Si falta información o hay ambigüedad que cambia el diseño, pregunta; si es un detalle menor, decide y documenta la decisión.
- Commits pequeños, con mensaje descriptivo en español o inglés (consistente). No hagas commit de `data/`, `node_modules/`, `bin/` ni `obj/`.
- **Haz `git commit` del trabajo pendiente antes de empezar cada nuevo prompt/tarea**, para que cada tarea parta de un árbol limpio y se pueda revertir por separado.
- La revisión de la Fase 1 (riesgos de IPC, ventanas/hilos, SQLite/FTS y contradicciones con este archivo) está en `docs/REVISION_FASE1.md`, con prioridad, archivo:línea y corrección propuesta. Léela antes de tocar IPC, ventanas o migraciones, y anota allí el estado y el commit de cada hallazgo que corrijas.




