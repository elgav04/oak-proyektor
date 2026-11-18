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
└─ data/                            # BD y cachés locales; en .gitignore
```

El build va a `frontend/dist/` (en `.gitignore`); App lo copiará a su salida como `wwwroot` (pendiente). Los paquetes npm están fijados sin `^`; TypeScript se queda en 6.x porque typescript-eslint no soporta 7.

Dos páginas de entrada en Vite: `index.html` (control) y `output.html` (proyección). La de salida no carga shadcn ni Zustand si no hace falta.

## Comandos

```
dotnet build backend/Oak-Proyektor.sln
dotnet test  backend/Oak-Proyektor.sln
dotnet run   --project backend/src/OakProyektor.App
cd frontend && npm install && npm run dev      # servidor Vite
cd frontend && npm run build                   # genera estáticos para Photino
cd frontend && npm run typecheck && npm run lint
```

Orden: `npm run build` en `frontend/` ANTES de `dotnet build`/`dotnet run` (el build de App copia `frontend/dist` a `wwwroot`; sin él el panel muestra un aviso). Datos y logs: `data/` en la raíz del repo en desarrollo (se detecta por `CLAUDE.md`); `OAK_DATA_DIR` lo reemplaza; fuera del repo, `data/` junto al ejecutable.

Si un comando no existe todavía, créalo y documéntalo aquí.

## Convenciones C#

- `Nullable` y `ImplicitUsings` activados; `TreatWarningsAsErrors` en Release.
- Todo I/O es `async` con `CancellationToken`. Nada de `.Result` ni `.Wait()`.
- DTOs y mensajes como `sealed record`. Servicios por interfaz con inyección de dependencias (`Microsoft.Extensions.DependencyInjection`).
- Sin estado estático mutable.
- Logging con `ILogger`, a archivo rotativo en `data/logs/`. Handlers globales para excepciones no controladas (`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`): registrar, no cerrar la proyección.
- Un fallo en el panel de control **no** debe cerrar la ventana de salida.
- Nunca asumas que Photino es seguro entre hilos: serializa los envíos hacia las ventanas por un único canal y verifica en la documentación o el código del paquete cómo despachar al hilo de UI.
- **Photino (verificado con un spike en Windows 11, Photino.NET 4.0.16):**
  - `Main` debe ser `[STAThread]`; en MTA la ventana queda negra sin error.
  - `Monitors` solo se puede leer tras crear la ventana principal (usa `RegisterWindowCreatedHandler`). No existe evento de cambio de monitores: `DisplayManager` sondea `Monitors` cada 1–2 s y compara.
  - La ventana de salida se construye y arranca en el hilo de UI: `main.Invoke(() => { new PhotinoWindow(main)…; w.WaitForClose(); })`. Construirla en un hilo de trabajo provoca interbloqueo.
  - Las páginas cargan por `file://` (`Load(ruta)`); no hace falta esquema `app://`.
  - Ventana sin bordes con posición y tamaño exactos del monitor (`MonitorArea`, píxeles físicos); no hace falta `SetFullScreen`.
  - **No llames `SendWebMessage` a una ventana hasta que su página haya enviado su primer mensaje**: el WebView2 se inicializa de forma asíncrona y enviar antes provocó una violación de acceso nativa (`0xC0000005`) que cierra el proceso. `WindowRegistry` lo aplica; las páginas piden su estado al cargar (`output.getState`), así que nada se pierde.
  - Todo callback de Photino (handlers de mensajes, cierre, creación) debe capturar sus excepciones: no pueden cruzar la frontera nativa. Una excepción no controlada en un hilo de fondo termina el proceso en .NET y no se puede evitar, por eso todo trabajo de fondo captura las suyas.
  - Ambas ventanas comparten el hilo de UI: un handler lento en C# congela panel y salida a la vez. Los handlers solo encolan; el trabajo se hace en otro hilo.

## Convenciones TypeScript / React

- `strict: true`, prohibido `any`. Los mensajes IPC son uniones discriminadas por `type`.
- Zustand solo como **réplica** del estado que manda C#: el cliente envía comandos y reacciona a eventos; no muta estado de negocio localmente.
- Atajos de teclado centralizados (un solo archivo de mapa), nunca dispersos por componentes.
- Componentes del panel de control optimizados para operar rápido: foco por teclado, objetivos grandes, estados claros (EN VIVO / listo).

## Protocolo IPC

Sobre cada mensaje va un sobre común:

```json
{ "v": 1, "id": "uuid", "kind": "command|event|query|reply", "type": "output.showSlide", "payload": { } }
```

- `command`: la UI pide algo a C#. `event`: C# informa un cambio. `query`/`reply`: petición con respuesta correlacionada por `id`.
- Los tipos viven en C# (`OakProyektor.Ipc`) y se reflejan a mano en `frontend/src/ipc/`. Un test de contrato debe fallar si divergen.
- Versiona el protocolo (`v`). Ignora con log los mensajes desconocidos; no lances excepciones.
- La ventana de salida recibe snapshots completos de `OutputState` (idempotentes), no diffs. Cada snapshot lleva `revision` creciente; las réplicas descartan los más viejos.
- Mensajes actuales (catálogo en `IpcCatalog` y `frontend/src/ipc/protocol.ts`): commands `output.open`, `output.close`, `output.setMaster`; queries `display.list`, `output.getState` (reply con el mismo `type` y `id`); events `display.changed`, `output.stateChanged`. El payload de `output.stateChanged` y del reply de `output.getState` es el mismo.
- Entrada: los handlers de Photino solo encolan (`InboundQueue`, FIFO); un único hilo procesa y responde. Salida: un único canal (`Outbox`, FIFO) hacia ambas ventanas.
- Política de la ventana de salida: pantalla completa sin bordes solo en un monitor que NO aloja el panel (por defecto, el primero que no lo aloja). Si solo hay un monitor o se elige el del panel, se abre como ventana normal de 960×540. La salida nunca se cierra ni se mueve por sí sola (tampoco si desaparece su monitor; el panel avisa).

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




