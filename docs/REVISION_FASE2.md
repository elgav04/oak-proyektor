# Revisión de la Fase 2 (`b7835f7..HEAD`)

Revisión de solo lectura del motor de capas (IPC v7), el módulo de Biblias y la pestaña de Biblia (IPC v8). Sin críticos confirmados. Cada hallazgo lleva su marca: **[VERIFICADO]** (leído en el código) o **[A PROBAR]** (requiere prueba en hardware real). Estado y commit de cada uno: ver la columna «Estado».

Alcance: el importador de Biblias (commit `b7835f7`) queda fuera del rango; solo se revisaron `BuildMatch` y el parser de referencias.

Pruebas al momento de la revisión: C# 1021 correctas y 1 omitida; JS 166 correctas; typecheck y lint limpios.

| # | Prioridad | Resumen | Estado |
|---|-----------|---------|--------|
| 1 | Importante | Video de fondo: atasco sin aviso y falso fallo de carga con la ventana oculta | corregido (prompt A): motor con control de avance, carga suspendida con la ventana oculta y recarga única; verificado con tests y en la app real; la parte «Chromium difiere la carga» sigue [A PROBAR] en el proyector |
| 2 | Importante | Reaplicar el mismo archivo tras un fallo no reintenta | corregido (prompt A): protocolo v9 (`retry`, `output.retryBackground`), botón «Reintentar» en todas las pestañas; verificado en la app real |
| 3 | Importante | Texto recortado con otro escalado de monitor | pendiente (prompt C) |
| 4 | Importante | Una referencia enorme congela el panel | pendiente (prompt D) |
| 5 | Importante | La insignia «EN VIVO» no refleja el estado de C# | corregido (prompt B): la insignia se deriva del estado confirmado por el backend |
| 6 | Importante | `showContent` hace cola detrás de trabajo lento | corregido (prompt B): tres carriles de entrada (prioridad, en vivo, lento); latencia real con un disco colgado y USB físico [A PROBAR] |
| 7 | Menor | `fail()` de un medio saliente retira el fondo nuevo | corregido (prompt A) |
| 8 | Menor | El `OutputErrorBoundary` remonta `BackgroundLayer` | corregido (prompt A): el fondo va fuera del boundary |
| 9 | Menor | `MediaPathValidator` solo resuelve el enlace del archivo final | corregido (prompt A): ruta final + una comprobación en vuelo; unión de carpeta real probada |
| 10 | Menor | `SettingsStore` sin validación al cargar, sin copia ni flush | pendiente (prompt E) |
| 11 | Menor | `BibleSettings` guarda un borrador obsoleto | pendiente (prompt D) |
| 12 | Menor | `isTypingTarget` bloquea atajos tras casillas y selector | pendiente (prompt D) |
| 13 | Menor | `BuildMatch`: sin límite de términos y NFD | pendiente (prompt D) |
| 14 | Menor | Bloque de más de 4000 caracteres con letra muy pequeña | pendiente (prompt C) |
| 15 | Menor | Honestidad de tests y documentación | pendiente (prompt E) |

## Importante

### 1. Un fallo del video de fondo puede pasar inadvertido o fijarse sin motivo [A PROBAR]
`frontend/src/output/background/engine.ts:218-225, 235-237, 245-250`

- **Atasco sin aviso:** el aviso depende solo del evento `stalled`; con la ventana oculta ni se arma ni se rearma al volver. No hay ningún control de progreso de `currentTime`.
  - Escenario: un video `file://` se traba sin emitir `stalled`, o se atasca durante la pausa: fotograma congelado y el panel sin aviso.
- **Falso fallo de carga:** el límite de 10 s corre con la ventana minimizada.
  - Escenario: se cambia el fondo con la salida en pausa, Chromium no carga mientras está oculta, salta un falso «stalled», cae al color de reserva y, como ese fondo ya no se reintenta, al reanudar sigue sin video.
- **Corrección:** comprobar cada 2 s, con la ventana visible, que el video avanza; suspender el límite de carga mientras esté oculta y rearmarlo al mostrarse; tras un fallo por atasco o carga durante la pausa, permitir UN reintento al volver a ser visible.

### 2. Volver a aplicar el mismo archivo tras un fallo no hace nada [VERIFICADO]
`OutputState.WithBackground` descarta un fondo idéntico; el motor ignora el mismo medio (`engine.ts:261`).

- Escenario: se cae el USB, se reconecta y se pulsa «Aplicar archivo»: no pasa nada y el aviso del panel sigue ahí.
- **Corrección:** contador de reintento en `BackgroundLayer` (o limpiar el medio pedido tras un fallo) y botón «Reintentar» en el panel.

### 3. Puede recortarse texto en el proyector [A PROBAR]
- El panel y la salida pueden estar en monitores con distinto escalado (aquí, panel al 125 % y proyector al 100 %); el ancho del texto y los saltos de línea pueden variar.
- `projectionTheme` (`slide.ts:61-63`) usa `min(minFontSize, fontSize)`; `BibleSettings.tsx` baja el mínimo al bajar el tamaño: queda mínimo = tamaño, el ajuste automático no puede reducir la letra y la caja recorta.
- `tools\bible-layout-test.ps1` abrió ambas ventanas en el mismo monitor: el «0 px de diferencia» no cubre este caso.
- **Corrección:** mínimo de proyección siempre menor que el tamaño (p. ej. `floor(fontSize × 0,8)`); margen de medición (≈ 0,25 renglones); repetir la prueba con la salida en un monitor de otro escalado.

### 4. Una referencia enorme congela el panel [A PROBAR el tiempo]
`controller.ts`, rama `!sameChapter` de `lookupReference` y `computeBuilt`.

- «Sal 1-150» o «Gn 1-50» traen ~2400 versículos: JSON grande, filas sin virtualizar y paginación completa con medición DOM. Basta con escribirlo (la búsqueda se lanza al dejar de teclear).
- Mantener Mayús+↓ en un capítulo largo repaginan toda la selección en cada pulsación (coste cuadrático).
- **Corrección:** limitar versículos por consulta en el backend (≈ 200) y la selección (≈ 30); paginar solo al proyectar; virtualizar la lista.

### 5. La insignia «EN VIVO» no refleja el estado real de C# [VERIFICADO]
`BibleTab.tsx` la calcula con `projected`, marcado de forma optimista.

- Se equivoca tras `output.invalidContent`, tras Black, Clear o Logo (también por atajos globales), o si se envió texto desde Diagnóstico.
- **Corrección:** derivarla de `useControlStore().output.state` (slide actual y modo maestro).

### 6. La proyección de un versículo hace cola detrás de trabajo lento [VERIFICADO el carril; latencia A PROBAR]
`output.showContent` va por el carril normal, detrás de `output.setBackground` (validación de hasta 5 s con una unidad atascada, `Handlers.cs`) y de las consultas de Biblia.

- Escenario: se aplica un fondo desde un USB lento y justo después se pulsa Enter sobre un versículo: sale varios segundos tarde.
- **Corrección:** validar el fondo fuera del carril y confirmarlo con un comando aparte, o dar a contenido y maestros un carril propio.

## Menor

### 7. `fail()` no mira el estado del medio [VERIFICADO]
`engine.ts:144-154`. Si el fondo anterior, mientras se desvanece, da error, también retira el fondo nuevo ya visible. **Corrección:** un medio en salida que falla solo se libera.

### 8. Un error de render de la salida remonta el fondo [A PROBAR]
El fallback del `OutputErrorBoundary` monta otro árbol, incluido `BackgroundLayer`: el video se reinicia y se ve el color de base. **Corrección:** dejar el fondo fuera del boundary.

### 9. `MediaPathValidator` solo resuelve el enlace del archivo final [A PROBAR]
`MediaPathValidator.cs:57,144`. Una unión o enlace de carpeta intermedio (`C:\Fondos` → `\\servidor\…`) o una unidad `subst` pasan como «disco local». Si se agota el límite de 5 s queda un hilo de `Task.Run` bloqueado. **Corrección:** resolver la ruta final con `GetFinalPathNameByHandle` y rechazar UNC y red sobre ella; permitir abandonar la operación sin acumular hilos.

### 10. `SettingsStore` no valida al cargar
- Un tema inválido editado a mano en `settings.json` hace que se rechace cada proyección.
- Un archivo corrupto se reemplaza por los valores por defecto en el siguiente guardado, sin copia, y se escribe sin vaciar a disco antes de renombrar.
- **Corrección:** validar por bloque, guardar `settings.json.bad` y vaciar a disco antes de renombrar.

### 11. `BibleSettings.tsx` guarda un borrador tomado al abrir la pantalla
Pisa lo cambiado después con la casilla «Proyectar al navegar».

### 12. `shortcuts.ts`, `isTypingTarget`
Tras pulsar una casilla, los atajos no responden hasta mover el foco; con el selector de Biblia enfocado, ↓ cambia de Biblia. **Corrección:** tratar casillas como no-escritura y quitar el foco del selector tras elegir.

### 13. `BibleQueryService.BuildMatch` (fuera del rango)
Seguro frente a operadores FTS5 [VERIFICADO leyendo]. Falta limitar la longitud (texto pegado de miles de palabras) y `[\p{L}\p{N}]` parte palabras con tildes descompuestas (NFD). **Corrección:** normalizar a NFC, incluir `\p{M}`, límite ≈ 32 términos.

### 14. `ContentValidator.MaxBlockChars` (4000)
Con letra muy pequeña una página podría superarlo y el backend la rechazaría.

### 15. Honestidad
- El test «nunca a mitad de palabra» solo comprueba que la página termine en letra o coma.
- El test de atajos globales usa eventos sintéticos (`RegisterHotKey` los consume antes de llegar a la página).
- CLAUDE.md dice «0 px» sin aclarar que fue con el mismo escalado (ver 3).
- La línea «Biblias (v6)» de CLAUDE.md está corrupta («ible.import», «ttribution»); viene de `b7835f7`.

## Pruebas manuales pendientes con el proyector real
1. Panel al 125 % y salida al 100 % (u otra resolución): `tools\bible-layout-test.ps1` con la salida en el proyector y versículos largos con tamaño 40–48; buscar texto recortado o letra reducida.
2. Fondo en USB: desconectarlo en vivo, reconectarlo y reaplicar el mismo archivo (hallazgo 2).
3. Salida en pausa (cable desconectado): cambiar el fondo, esperar más de 10 s y reconectar (hallazgo 1).
4. MP4 largo en bucle 30–60 min con cambios de versículo, vigilando congelamientos sin aviso.
5. «sal 1-150» y Mayús+↓ en Sal 119: medir cuánto se congela el panel.
6. Black, Clear, Logo y Freeze con un versículo de varias páginas, comparando la salida con la insignia «EN VIVO».
7. Desconectar el proyector en plena ráfaga de flechas y reconectar: debe verse el último versículo, sin texto viejo.
