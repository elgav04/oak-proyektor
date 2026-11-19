# Revisión de las correcciones de la Fase 1 — ronda 2 (solo lectura)

- **Rango revisado:** `3d776be..HEAD` (HEAD = `13e0635`).
- **Fecha:** 2026-11-19.
- **Origen:** revisión hecha con el modelo Opus 5.5 en modo solo lectura; no se modificó nada.
- **Estado:** corregidos K1, I-A y varios menores (ver el registro al final); el resto sigue pendiente. Al corregir uno, anota aquí su estado y el commit.
- **Marcas:** [VERIFICADO] = comprobado leyendo código o ejecutando; [A PROBAR] = no se puede confirmar sin hardware real.

**Resumen:** hay un problema crítico, pero es de entrega, no de estabilidad en vivo: los tests de la base de datos no están en el repositorio. En la salida, la mensajería y la base de datos no se encontró nada crítico.

**Ejecución:** `dotnet build` sin advertencias; `dotnet test` 229/229 (incluidos los tests no versionados); `npm run typecheck`, `lint` y `npm test` (38/38) correctos.

## Crítico

**K1. Los tests de la base de datos nunca se subieron a git.** [VERIFICADO]
- **Dónde:** `.gitignore:14` (`data/`) junto con `core.ignorecase=true`.
- **Problema:** esa regla también ignora `backend/tests/OakProyektor.Tests/Data/`. `git check-ignore` lo confirma y `git ls-files` sobre esa carpeta no devuelve nada.
- **Qué falta:** los 11 archivos de esa carpeta. Entre ellos están `MigrationHashTests`, `SourceRulesTests`, `MigrationRunnerTests`, `PlaylistItemsTests`, `ReplaceAndTriggerTests` y `DatabaseReadinessTests`. Los anteriores a las correcciones (`CascadeTests`, `FtsTests`) tampoco están.
- **Consecuencia:** los 229 tests pasan solo en este equipo. Una copia del repositorio no los tiene. El commit 8a5ef9a y `CLAUDE.md` describen protecciones (I1, I2, M6, I13) que el repositorio no contiene.
- **Corrección:** cambiar la regla a `/data/`, añadir esa carpeta con `git add` y comprobarlo con `git ls-files`.

## Importante

**I-A. La pausa de emergencia no es atómica respecto al resto del ciclo de vida de la ventana.** [VERIFICADO leyendo el código; el efecto en pantalla, A PROBAR]
- **Dónde:** `OutputController.cs:304-316`, `:329`, `:371` y `:387`.
- **Escenario 1, los carriles:** el operador pulsa «Mover / reabrir» y enseguida «Recuperar control».
  - `output.recover` va por el carril de prioridad y se ejecuta primero, sobre la ventana vieja.
  - Después `output.open` crea una ventana nueva, a pantalla completa y por encima de todo.
  - Resultado: la última orden del operador fue pausar y la salida queda visible.
- **Escenario 2, ventana a medio crear:** si `_hwnd == 0` porque la ventana se está creando, `EmergencyPause` solo escribe en el log. No cambia el estado ni avisa al panel.
- **Escenario 3, sin comparar antes de escribir:** `EvaluateLocked` lee `paused` y `reason`, y actúa más tarde.
  - Si entre medias llega el atajo, `ResumeLocked` pone `paused = false` y deshace la emergencia.
  - O `PauseLocked` sustituye el motivo `Emergency` por `DisplayLost`, y la salida se reanuda sola cuando vuelve el monitor.
  - El margen de tiempo es estrecho, pero la emergencia es la red de seguridad.
- **Corrección:**
  - Marcar «pausa pedida» bajo `_gate`. `OpenLocked`/`CreateWindow` la respetan al terminar (minimizan) y, si `hwnd` aún es 0, se aplica en cuanto exista.
  - En pausa y reanudación, comparar antes de escribir: reanudar solo si sigue en pausa por el mismo motivo, y no rebajar nunca `Manual` o `Emergency`.

**I-B. «Reanudar salida» puede abrir la salida en otro monitor.** [VERIFICADO leyendo el código; con monitores reales, A PROBAR]
- **Dónde:** `DisplayManager.cs:73-78` (`Resolve`) y `App.tsx:93`.
- **Problema:** el botón envía el `displayId` viejo. Si ese ID ya no existe, `Resolve` elige en silencio el primer monitor que no aloja el panel. Esto pasa cuando el proyector vuelve con otra resolución o posición, o cuando todavía no ha vuelto.
- **Consecuencias:**
  - Con 3 monitores, la salida se abre a pantalla completa y por encima de todo en un monitor que nadie eligió.
  - Con 2 monitores y el proyector ausente, se cierra la ventana en pausa y se abre otra de 960×540 sobre el panel.
  - `output.cannotResume` es prácticamente inalcanzable.
  - Esto contradice «monitores por ID» de `CLAUDE.md`.
- **Corrección:** si se pide un ID explícito que no existe, devolver un error (por ejemplo `output.displayNotFound`). Solo `null` debe significar «automático».

**I-C. El ErrorBoundary de la salida usa `revision`, que con el protocolo v2 ya no sirve para eso.** [VERIFICADO; hoy es un riesgo latente]
- **Dónde:** `OutputScreen.tsx:13` (`resetKey={state?.revision}`) y `CLAUDE.md:103`.
- **Problema:** `revision` solo cambia con los controles maestros. Tras un error de render, un snapshot con otro texto no provoca el reintento. Cuando existan diapositivas, la salida se quedaría fija en la anterior hasta que alguien pulse un control maestro.
- **Corrección:** usar `seq` como `resetKey` y actualizar `CLAUDE.md`.

## Menor

- **El atajo de emergencia puede quedarse bloqueado.** [VERIFICADO el código; el efecto, A PROBAR] `NativeMethods.cs:111-115`. `SetWindowPos` y `ShowWindow` llamados desde otro hilo son síncronos: si el hilo de UI está bloqueado, el hilo del atajo también. Por tanto, «sin cola de UI» solo es cierto a medias. Corrección: `ShowWindowAsync` y `SWP_ASYNCWINDOWPOS`.
- **Al desconectar, el panel queda tapado hasta ~1 s.** [A PROBAR] `OutputSafety.cs:61-64`. Si Windows mueve la salida al monitor del panel, el antirrebote de `DisplayLost` se evalúa antes que el cruce con el panel. Corrección: si el rectángulo de la salida cruza el monitor del panel, pausar de inmediato aunque el monitor de la salida no aparezca.
- **Si el proyector vuelve con otro ID, la salida no se restaura sola.** [A PROBAR] El panel sigue diciendo «Proyector desconectado» aunque esté conectado. Corrección: un aviso del tipo «apareció un monitor nuevo» (relacionado con I-B).
- **Una salida en pausa restaurada desde la barra de tareas o con Alt+Tab** [VERIFICADO] queda visible mientras el estado sigue diciendo «en pausa». En pausa, la política ignora la posición (`OutputController.cs:333`). Corrección: comprobar `IsIconic` mientras está en pausa.
- **El atajo de respaldo de la página de salida** envía `output.recover`, así que el panel muestra «recuperación manual» en vez de «atajo de emergencia» (`OutputView.tsx:36`).
- **Copia previa con el disco lleno.** `MigrationRunner.cs:72-90`: si `VACUUM INTO` falla, la copia a medias se queda en disco y cada arranque añade otra. Corrección: borrarla al fallar. Además, en `:120` una excepción al reactivar `foreign_keys` taparía el error original.
- **La espera a la BD solo es una convención.** No existe ninguna implementación de repositorio, así que «los repositorios esperan a la BD» solo está escrito en la documentación de la interfaz. Nada impide escribir `new SqliteConnection(` fuera de la fábrica (un futuro importador perdería `recursive_triggers`). Corrección: añadir esa regla a `SourceRulesTests`. Su expresión regular tampoco detecta SQL partido en varias cadenas.
- **Los pasajes de `PlaylistItems` dependen del `Id` de libro de una Biblia concreta.** Reimportar una Biblia (borrar e importar) marcaría como faltantes todos los pasajes de todos los servicios. Además no hay guardia `BEFORE UPDATE`. Hay que decidirlo antes del importador.
- **El test de contrato** compara solo nombres de campos (no tipos ni nulabilidad) y no obliga a subir `v` cuando cambia el formato.
- **Segunda instancia durante el apagado de la primera.** [A PROBAR] Si se abre mientras la primera aún se cierra (el Mutex vive hasta ~3 s más), busca la ventana durante 3 s, no la encuentra y sale en silencio.
- **Mensajes descartados sin aviso.** `InboundProcessor.cs:45`: un carril lleno descarta el comando sin enviar `command.failed`.
- **El panel nunca vuelve a «desconectado».** `store.ts:85-87`: `connected` no vuelve a `false`; si el backend se atasca más tarde, el panel parece conectado.
- **Contradicciones en la documentación:**
  - `CLAUDE.md:103` dice «No hay aún un ejecutor de tests de JS», pero existe `npm test`.
  - `REVISION_FASE1.md:7` dice «ningún hallazgo está corregido todavía».
- **Tests de cosas que dependen de Photino.** [VERIFICADO] `OutputController` no tiene tests: el antirrebote, la integración de la política y la emergencia solo se comprobaron a mano. Lo que sí tiene tests es la política como función pura, el publicador, las colas y el cliente.

## Lo que se comprobó y está bien

- `foreign_keys = OFF` se ejecuta antes del `BEGIN` y se reactiva en un `finally`.
- La migración `0001` no se modificó.
- La relación de propiedad de ventanas sigue igual (`new PhotinoWindow(_main)`).
- El bloqueo de apagado de pantalla se libera en `OnClosed`, y Windows lo libera solo si el proceso muere.
- El atajo se libera con `WM_QUIT` al apagar, y Windows lo libera solo si el proceso cae. Si no se puede registrar, el panel lo indica.
- Los carriles tienen consumidores independientes, así que el carril normal no se queda sin turno.
- Un reply viejo pierde frente a un evento nuevo, porque las réplicas descartan `seq` menor o igual.

## Pruebas manuales pendientes con el proyector real

1. **Cable:** desconectarlo con la salida en Live. Medir cuánto tiempo queda tapado el panel, reconectar y comprobar que se restaura sola. Repetir conectando en otro puerto o con otra resolución (I-B).
2. **Win+P en modo «Duplicar»**, y vuelta a «Extender».
3. **Panel sobre el proyector:** arrastrarlo encima y de vuelta, y comprobar que no hay parpadeo ni bucle de pausa y reanudación.
4. **Atajo de emergencia:** con otra aplicación enfocada; con la combinación ya ocupada por otro programa (por ejemplo AutoHotkey), comprobar el aviso del panel y el respaldo dentro de la salida. Pulsarlo también justo después de «Mover / reabrir» (I-A).
5. **Restaurar desde la barra de tareas** una salida en pausa.
6. **Proyector con escalado distinto del 100 %** o resolución distinta a la del panel: comprobar que la pantalla completa es exacta y nítida.
7. **Tres monitores**, si es posible.
8. **Bloqueo de apagado:** dejar la salida abierta más allá del tiempo de apagado de pantalla. `powercfg /requests` debe mostrar el bloqueo y dejar de mostrarlo al cerrar la salida.
9. **Bucle infinito en el panel (I8):** repetirlo con el perfil de WebView2 nuevo, que `CLAUDE.md` deja pendiente.

## Registro de correcciones

| Hallazgo | Estado | Commit |
|---|---|---|
| K1 | Corregido (`/data/` en `.gitignore`; los 11 archivos de `Data/` versionados; verificado en una copia limpia) | 08abc97 |
| I-A | Corregido: `OutputPauseState` (máquina de estados pura), numeración de órdenes (`OrderClock`), pausa y lectura del handle bajo el mismo candado, comparar antes de escribir. **Falta la prueba con el proyector real** | 8f70942 |
| Menor: atajo de emergencia puede bloquearse | Corregido (`ShowWindowAsync` + `SWP_ASYNCWINDOWPOS`); no puede probarse sin colgar el hilo de UI a propósito | 8f70942 |
| Menor: panel tapado hasta ~1 s al desconectar | Corregido (pausa inmediata si la salida ya cruza el monitor del panel); falta probar con el cable real | 8f70942 |
| Menor: salida en pausa restaurada desde la barra de tareas | Corregido (el sondeo vuelve a minimizarla); falta probar a mano | 8f70942 |
| Menor: atajo de respaldo mostraba «recuperación manual» | Corregido (protocolo v3: `output.recover` con `reason`) | 8f70942 |
| I-B, I-C y el resto de menores | Pendiente | — |
