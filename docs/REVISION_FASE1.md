# Revisión de la Fase 1 (solo lectura)

- **Commit revisado:** `3d776be` (las líneas citadas corresponden a ese commit; pueden moverse al corregir).
- **Fecha:** 2026-11-18.
- **Alcance:** `CLAUDE.md`, App, Ipc, Data, la migración SQL y el frontend (`ipc/`, `control/`, `output/`).
- **Origen:** revisión hecha con el modelo Opus 5.5 en modo solo lectura; no se modificó nada.
- **Estado:** **ningún hallazgo está corregido todavía.** Al corregir uno, anota aquí su estado y el commit.

> Cómo usar este documento: los hallazgos están ordenados por prioridad. Antes de tocar C1, C2 o I8, léelos completos: C1 e I8 son hipótesis que hay que probar a mano; C2 requiere una decisión del usuario porque cambia una regla de `CLAUDE.md`.

## Respuesta corta a las cuatro preguntas

1. **IPC.** El orden está garantizado (una cola FIFO de entrada y otra de salida) y los mensajes inválidos no rompen nada. Los puntos débiles: una carrera al publicar snapshots, no hay respuestas de error, los índices de monitor no son estables, y los controles maestros esperan detrás de comandos lentos.
2. **Ventanas e hilos.** Una excepción en el panel (JS o C#) no tumba la salida. El mayor riesgo está en la relación de propiedad entre ventanas y en lo que pasa al desconectar el proyector, no en las excepciones. Hay dos riesgos críticos sin verificar.
3. **SQLite.** No hay corrupción con el código actual. Hay dos riesgos latentes serios: migraciones futuras que reconstruyan tablas, y `INSERT OR REPLACE` sobre tablas con FTS.
4. **Contradicciones con `CLAUDE.md`.** Son pocas y menores, salvo que los controles maestros no tienen prioridad (I5).

## Crítico

### C1. Minimizar el panel podría ocultar la proyección — **NO se confirma**
> Verificado por el usuario con el proyector real: minimizar el panel no oculta la salida. No cambiar cómo se crea la ventana de salida ni su relación con el panel.

- **Dónde:** `backend/src/OakProyektor.App/OutputController.cs:75`, `new PhotinoWindow(main)`.
- **Problema:** la salida se crea con el panel como padre. En Win32, si una ventana es "poseída" por otra, Windows la **oculta al minimizar la propietaria**. No se leyó el código nativo de Photino para confirmar que pasa ese padre como propietario. Si lo hace, minimizar el panel durante el culto hace desaparecer la proyección.
- **Corrección:** probarlo a mano ya (abrir la salida y minimizar el panel). Si se confirma, crear la salida sin padre (`new PhotinoWindow()`), con un spike nuevo para comprobar que la regla de "construir en el hilo de UI" sigue funcionando sin padre.

### C2. Al desconectar el proyector, la salida puede tapar el panel y dejar al operador sin control — **CORREGIDO (pendiente de prueba con el cable real)**
> Confirmado por el usuario con el proyector real (la salida tapó el panel por completo). Política nueva en `CLAUDE.md` (sección «Protocolo IPC», «Política de seguridad de la salida»).

- **Dónde:** `OutputController.cs:83` (`SetChromeless(true)…SetTopMost(true)`), `DisplayManager.cs:64-72` y `CLAUDE.md:107`.
- **Problema:** la salida es topmost, sin bordes y sin menú contextual. Si se desconecta el proyector, Windows suele mover esa ventana al monitor que queda, el del panel, y lo cubre entero por encima de todo. Pasa lo mismo si el operador arrastra el panel al monitor de la salida. `DisplayManager` solo avisa, y la política documentada prohíbe mover la salida. Sin bordes ni menú, la única salida es Alt+F4, y eso no es obvio.
- **Corrección propuesta:** decidir la política. Cuando cambian los monitores, si el monitor de la salida desapareció o la salida se cruza con el monitor del panel, quitar `TopMost` y pasarla a ventana normal, sin cerrarla. Añadir además un atajo de emergencia en la salida. Requiere cambiar la regla de `CLAUDE.md:107`. **Pendiente de decisión del usuario.**

## Importante

### I1. Las migraciones futuras pueden borrar datos en cascada
- **Dónde:** `backend/src/OakProyektor.Data/DatabaseInitializer.cs:36-45` y `SqliteConnectionFactory.cs:12`.
- **Problema:** las migraciones corren con `foreign_keys = ON` y dentro de una transacción, donde `PRAGMA foreign_keys = OFF` no tiene efecto. Una migración que reconstruya una tabla (el patrón habitual en SQLite: crear nueva, copiar, `DROP` de la vieja) borraría en cascada sus hijas; por ejemplo, `DROP TABLE Songs` borra todas las `SongSlides`.
- **Corrección:** en el runner, poner `foreign_keys = OFF` antes del `BEGIN`, ejecutar `PRAGMA foreign_key_check` antes del `COMMIT` y reactivarlo después. Además, hacer una copia de la BD (`VACUUM INTO`) antes de aplicar cualquier migración.

### I2. `INSERT OR REPLACE` desincroniza el índice FTS y puede corromperlo
- **Dónde:** `backend/src/OakProyektor.Data/Migrations/0001_Initial.sql:80-91` y `:101-136`.
- **Problema:** cuando `REPLACE` borra una fila para resolver un conflicto, SQLite **no dispara los triggers DELETE** salvo que `recursive_triggers` esté activado. En `VersesFts`, que usa contenido externo, eso deja entradas fantasma. Los `delete` posteriores con valores que no coinciden pueden dejar el índice inconsistente o corrupto. Un importador de Biblias es justo el código que tendería a usar `REPLACE`.
- **Corrección:** `PRAGMA recursive_triggers = ON` en cada conexión, o prohibir `REPLACE` y usar `ON CONFLICT DO UPDATE`. Ejecutar el `integrity-check` de FTS tras cada importación.

### I3. Carrera de snapshots: el panel puede mostrar «SALIDA ABIERTA» con la salida ya cerrada
- **Dónde:** `OutputController.cs:146-147` y `:40`.
- **Problema:** `Publish()` toma el snapshot y lo encola en dos pasos no atómicos. Además, `revision` solo cambia con los controles maestros, no con el estado de la ventana. Si la salida se cierra con Alt+F4 (en el hilo de UI) mientras se procesa un comando, el snapshot viejo puede llegar después. Como tiene la misma `revision`, la réplica lo acepta (`frontend/src/control/store.ts:17` descarta solo si es menor).
- **Corrección:** tomar el snapshot y escribirlo en la cola bajo un mismo candado, y añadir una secuencia global que crezca con cada cambio de cualquier parte del estado.

### I4. El panel puede quedarse deshabilitado para siempre
- **Dónde:** `frontend/src/control/store.ts:36-45` y `:18`.
- **Problema:** `connected` solo pasa a `true` cuando se aplica un snapshot. Si los 5 intentos iniciales fallan (25 s), todos los botones quedan deshabilitados y ningún evento llegará, porque los eventos solo nacen de comandos que ya no se pueden enviar.
- **Corrección:** reintentar indefinidamente con espera creciente, marcar como conectado con cualquier mensaje válido, y añadir un botón «Reconectar».

### I5. Los controles maestros no tienen prioridad (contradice `CLAUDE.md:117`)
- **Dónde:** `backend/src/OakProyektor.App/IpcPump.cs:16-21` y `OutputController.cs:49-101`.
- **Problema:** todo pasa por una sola cola FIFO. Un `output.open` (refresco de monitores y creación de ventana, varios segundos en las pruebas) retrasa un «Black» pulsado justo después.
- **Corrección:** un carril aparte para `output.setMaster`, o que `Open` no bloquee la cola.

### I6. Los índices de monitor no son estables — **PARCIAL**
> `OutputWindowStatus.displayIndex` ahora se deriva del rectángulo del monitor de la salida (null si ya no está). Pendiente: que `output.open` y la UI identifiquen monitores por rectángulo en vez de por índice.

- **Dónde:** `DisplayManager.cs:41`, `OutputController.cs:52,96` y `frontend/src/control/App.tsx:31,55,69`.
- **Problema:** el índice que muestra y envía la UI cambia si se reconecta un monitor. Consecuencias: abrir la salida en otro monitor, «salida aquí» marcado en el monitor equivocado, y el aviso «monitor desconectado» falso o ausente.
- **Corrección:** identificar cada monitor por su rectángulo o por ID de dispositivo en `DisplayInfo` y en `OutputWindowStatus`.

### I7. Dependencia de un comportamiento no documentado de Photino
- **Dónde:** `OutputController.cs:93`.
- **Problema:** `w.WaitForClose()` "no bloquea" solo cuando se llama dentro de un `Invoke`. En el spike, dentro del handler `Created`, sí bloqueaba. Una actualización de Photino podría colgar el hilo de UI y congelar las dos ventanas.
- **Corrección:** mantener la versión fijada, documentar la prueba manual obligatoria al actualizar Photino, y dejar un comentario de advertencia junto a la dependencia.

### I8. Aislamiento entre panel y salida en WebView2 — **VERIFICADO A MANO (sin cambios de código)**
> Verificado por el usuario el 2026-11-18 con el proyector real (Photino.NET 4.0.16, WebView2 157.0.4341.60): un bucle infinito en el panel desde DevTools congeló el panel y la proyección siguió funcionando. NO cambiar el entorno de WebView2. La prueba es obligatoria al actualizar Photino o WebView2 (ver `CLAUDE.md`) y quedó documentada en `ApplicationHost.cs`.

- **Dónde:** `ApplicationHost.cs:22` y `OutputController.cs:75`.
- **Problema:** las dos ventanas usan la misma carpeta de datos de WebView2, es decir, el mismo entorno. No se verificó si comparten proceso de navegador o de renderizado. Si lo comparten, un bucle infinito o un fallo del JS del panel podría congelar la salida.
- **Corrección:** probarlo forzando un bucle en el panel. Si se confirma, dar a cada ventana su propio entorno con `SetTemporaryFilesPath`.

### I9. La BD se inicializa en segundo plano sin señal de "lista"
- **Dónde:** `ApplicationHost.cs:99-104`.
- **Problema:** los futuros repositorios podrían consultar antes de que termine la migración. Si la inicialización falla, solo queda en el log y el operador no se entera.
- **Corrección:** exponer la tarea de inicialización para que los repositorios la esperen, y avisar al panel si falla.

### I10. Ruta de datos en producción no escribible
- **Dónde:** `ApplicationPaths.cs:15`.
- **Problema:** fuera del repo, los datos van a `data/` junto al ejecutable. En `Program Files` no se puede escribir: no habría BD ni logs, y Serilog falla en silencio.
- **Corrección:** usar `%LOCALAPPDATA%\Oak-Proyektor`.

### I11. No hay instancia única
- **Dónde:** `Program.cs`.
- **Problema:** un doble clic en el icono abre dos procesos: dos salidas sobre el proyector, y dos escritores sobre el mismo log y la misma BD.
- **Corrección:** un `Mutex` con nombre, y traer al frente la instancia que ya está abierta.

### I12. El protocolo no tiene respuestas de error
- **Dónde:** `backend/src/OakProyektor.Ipc/IpcDispatcher.cs:45-49` y `:56-60`.
- **Problema:** una query que falla obliga al cliente a esperar 5 s de timeout. Un comando que falla (por ejemplo, falta `output.html`) no da ninguna señal al panel. Esto pesará cuando crezca el protocolo.
- **Corrección:** respuestas `reply` con `ok`/`error`, o un kind `error` correlacionado por `id`.

### I13. `Playlists.ItemsJson` sin integridad referencial
- **Dónde:** `0001_Initial.sql:48-53`.
- **Problema:** borrar una canción deja un servicio con una referencia rota, que puede aparecer en pleno culto. Fue un requisito literal del usuario, así que es una decisión de diseño.
- **Corrección:** una tabla `PlaylistItems` con claves foráneas, o validar las referencias al cargar el servicio.

### I14. Nada impide que la pantalla se apague durante el culto — **CORREGIDO**
- **Dónde:** fuera de lo pedido, pero es un riesgo en vivo.
- **Problema:** el ahorro de energía o el salvapantallas pueden apagar el proyector.
- **Corrección:** `SetThreadExecutionState` mientras la salida esté abierta.

## Menor

| # | Archivo:línea | Problema | Corrección |
|---|---|---|---|
| M1 | `OutputController.cs:91` | El handler de cierre de la salida no captura excepciones (contradice `CLAUDE.md:83`). **CORREGIDO.** | Envolverlo en try/catch. |
| M2 | `ApplicationHost.cs:126` | `Task.WaitAll` contradice `CLAUDE.md:70`. Está acotado a 3 s. | Apagado asíncrono, o documentar la excepción. |
| M3 | `IpcSerializer.cs:11` | Opciones estáticas públicas que se pueden modificar hasta su primer uso (contradice `CLAUDE.md:72`). | Llamar a `MakeReadOnly()`. |
| M4 | `MessageQueues.cs:14,33` | Con la cola llena, comandos y eventos se descartan solo con un log. | En la salida, conservar solo el último snapshot de cada tipo. |
| M5 | `0001_Initial.sql:88,105` | `Verses_au` se dispara con cualquier `UPDATE`. `Songs_au` no cubre un cambio de `Id`. | `AFTER UPDATE OF Text`, y prohibir cambiar `Id`. |
| M6 | `Migrations/Migration.cs` | No se guarda un hash de las migraciones aplicadas: editar la 0001 tras publicarla pasa desapercibido. | Un test que fije el hash de cada migración ya publicada. |
| M7 | `CLAUDE.md:48` | Dice que `wwwroot` está «pendiente», pero ya existe (`OakProyektor.App.csproj:23`). | Actualizar el texto. |
| M8 | `OakProyektor.App.csproj:23` | Sin `npm run build` se empaqueta un `dist` viejo. El test de contrato compara las fuentes, no el `dist`. | Un target de MSBuild que lo compruebe, o un handshake con hash de build. |
| M9 | `ApplicationHost.cs:71` y `OutputController.cs:92` | La navegación del WebView no está restringida: cualquier página que se cargue tendría `window.external`. | Bloquear la navegación fuera de las páginas propias. |
| M10 | `DisplayManager.cs:37-42,53` | Con el panel minimizado (posición −32000), ningún monitor «aloja el panel» y la opción por defecto sería el monitor del panel. Afecta a futuros atajos globales. | Usar la última posición conocida del panel. |
| M11 | `OutputController.cs:72-98,109-111` | Si la creación falla a mitad o `Close` falla, queda una ventana topmost huérfana que el panel no controla. **CORREGIDO.** | Registrar la ventana justo al crearla y reintentar el cierre. |
| M12 | `IpcPump.cs:23` | `catch (OperationCanceledException) { throw; }` sin filtro: un OCE ajeno al apagado detendría la cola de entrada para siempre. Hoy no pasa porque el dispatcher no lo propaga. | Añadir `when (ct.IsCancellationRequested)`. |
| M13 | `frontend/src/output/OutputView.tsx` | La salida no tiene `ErrorBoundary`: un error de render la deja en negro hasta recargar. **CORREGIDO** (`OutputErrorBoundary`). | Un boundary que conserve el último estado pintado. |
| M14 | `Program.cs:12-20` | La resolución de rutas y la creación del logger quedan fuera del `try`: si fallan, no queda registro. | Moverlas dentro del `try` con un respaldo. |

## Qué probar primero, a mano
1. **C1:** abrir la salida y minimizar el panel.
2. **C2:** con la salida abierta, desconectar el cable del proyector.
3. **I8:** con la salida abierta, forzar un bucle infinito en el panel (desde DevTools en Debug).

## Registro de correcciones

| Hallazgo | Estado | Commit |
|---|---|---|
| C1 | No se confirma (verificado por el usuario) | — |
| C2 | Corregido; falta probar con el cable real (ver pruebas manuales en el commit) | cd56327 |
| I14 | Corregido (`ScreenWakeLock`, `SetThreadExecutionState` en el hilo de UI) | cd56327 |
| M1 | Corregido | cd56327 |
| M11 | Corregido (registro inmediato + 3 reintentos y `WM_CLOSE`; no se declara cerrada si sigue existiendo) | cd56327 |
| I6 | Parcial: el índice mostrado en el estado se deriva del rectángulo | cd56327 |
| I8 | Verificado a mano el 2026-11-18; sin cambios de entorno; repetir al actualizar Photino/WebView2 | 0f59c06 |
| M13 | Corregido: `OutputErrorBoundary` conserva el último estado pintado; probado en la ventana real y con `/output-probe.html` | 0f59c06 |
| resto | Pendiente | — |
