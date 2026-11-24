# Revisión del módulo de Biblias (commit `b7835f7`)

Revisión de solo lectura del importador, las fuentes, el perfil de numeración, las migraciones 5 a 7, las consultas y el FTS, el historial, las órdenes de consola y los manejadores `bible.*`. Sin críticos. Cada hallazgo lleva su marca: **[VERIFICADO]** (leído en el código) o **[A PROBAR]** (requiere ejecutarlo).

Pruebas al momento de la revisión: C# 1086 correctas y 1 omitida; JS 213 correctas.

Alcance y aclaraciones:
- **Zefania XML no existe en el código.** Solo hay `youversion-json` (el único que importa) y `named-books-json` (solo verifica). No hay ningún `XmlReader`, así que XXE y DTD no aplican hoy; si se añade un lector XML, hay que desactivar el DTD y la resolución de entidades externas desde el primer día.
- **La importación desde el panel no existía** en el momento de la revisión; se añadió después (commit `ae2a911`) con protecciones en la interfaz que esquivan en parte los hallazgos 1 y 3.

| # | Prioridad | Resumen | Estado |
|---|-----------|---------|--------|
| 1 | Importante | Reimportar con la abreviatura de otra traducción sobrescribe todo el texto sin protección | corregido: `bible.tooManyChanges` (>1 % de textos, nombre, textos que pasan a vacío; `--allow-large-change`) |
| 2 | Importante | El tope del 1 % deja borrar libros enteros | corregido: `bible.booksWouldDisappear` |
| 3 | Importante | Reimportar sin `--numbering` cambia la numeración guardada | corregido: sin `--numbering` se conserva el perfil guardado |
| 4 | Importante | Un libro repetido en el archivo duplica el texto de sus versículos | corregido: duplicados por (libro, capítulo) en todo el archivo, con aviso de los distintos |
| 5 | Importante | Una importación puede frenar la proyección (bloqueo de escritura de SQLite) | corregido: la anotación va en segundo plano (`BackgroundCitationRecorder`); medido que el `BEGIN` espera 30 s |
| 6 | Importante | Una referencia con miles de tramos inexistentes genera miles de consultas | corregido: máximo 50 tramos |
| 7 | Menor | «1° Juan 3:16», «1.ª Juan» y «1a Juan» no se reconocen | corregido: «°», «.ª», «a», «o» |
| 8 | Menor | El aviso de afectados por el historial es incompleto | corregido: migración 8 con todos los tramos y aviso por tramo |
| 9 | Menor | Un archivo en Windows-1252 da un error poco claro | corregido: validación UTF-8 y mensaje claro; verificado con tests y la consola |
| 10 | Menor | Se aceptan capítulos fuera del canon y versículos mayores de 176 | corregido: se descartan con aviso y conteo |
| 11 | Menor | La transacción de importación es diferida | corregido: `BEGIN IMMEDIATE`; el choque `SQLITE_BUSY_SNAPSHOT` en sí no se reprodujo, solo que el bloqueo de escritura se toma y se suelta [A PROBAR] |
| 12 | Menor | Consola e instancia única: sin test, mensaje engañoso, órdenes de solo lectura migran | corregido: `CliHost.Execute` probado, marca de consola, solo lectura sin migrar, Ctrl+C |
| 13 | Menor | Abreviatura sensible a mayúsculas y con caracteres de control | corregido: recorte, sin caracteres de control y sin distinguir mayúsculas (sin migración) |
| 14 | Menor | Memoria con un archivo de unos 24 MB sin medir | medido y reducido: 38,8 → 30,4 MB (primera) y 43-50 → 31,0 MB (reimportación) |
| 15 | Menor | Honestidad de los tests | corregido: test de privacidad con logs y errores, y cancelación a mitad de escritura |

## Importante

### 1. Una Biblia equivocada sobrescribe toda la existente sin protección [VERIFICADO]
`backend/src/OakProyektor.Data/Scripture/BibleImporter.cs:119-136`

- **Escenario:** se importa otra traducción con `--abbr RVR1960` por error. Cambian los 31 102 textos y el nombre, no se borra nada y la importación sale con código 0. Lo mismo con un archivo cuyos versículos llegan vacíos: los textos existentes pasan a `""` sin aviso.
- **Por qué pasa el tope:** el límite del 1 % solo cuenta versículos borrados, no modificados.
- **Corrección:** rechazar si cambia más de un porcentaje pequeño de los textos, o si cambia el nombre de una Biblia ya instalada, salvo un `--allow-large-change` explícito.

### 2. El tope del 1 % deja borrar libros enteros [VERIFICADO]
`BibleImporter.cs:133`

- **Escenario:** el cupo es `max(10, total/100)`, es decir 311 versículos. Un archivo válido pero incompleto, sin Judas, Filemón, 2 y 3 Juan o Abdías, borra esos libros completos (con sus filas de `Books`) y sale con código 0. Solo queda constancia en el informe.
- **Corrección:** rechazar también si desaparece algún libro o capítulo entero, sea cual sea el número de versículos.

### 3. Reimportar sin `--numbering` cambia la numeración guardada [VERIFICADO]
`NumberingProfile.cs:40`, `BibleImporter.cs:87`, `Cli/CliRunner.cs:72`

- **Escenario:** la Biblia se importó con `merge-ps47-3jn`. Se reimporta sin la opción: `TryGet(null)` devuelve `none`, reaparecen Sal 47:10 y 3 Jn 1:15 y se actualizan 47:9 y 1:14. No se borra nada, así que el tope no salta; solo queda un aviso en el informe, y los servicios con «Sal 47:9» pasan a mostrar medio versículo.
- **Corrección:** sin `--numbering`, conservar el perfil guardado; cambiarlo solo si se pide de forma explícita.

### 4. Un libro repetido en el archivo duplica el texto de cada versículo [VERIFICADO]
`BibleSources.cs:94`

- **Escenario:** el control `seenChapters` va por libro, no global. Un archivo con dos entradas de GEN (p. ej. dos exportaciones concatenadas) une los mismos capítulos: cada versículo queda «texto texto». Solo se cuenta como `JoinedVerses`.
- **Corrección:** controlar duplicados por (libro, capítulo) en todo el archivo y contar los descartados.

### 5. Una importación desde el panel puede frenar la proyección [A PROBAR el tiempo]
`BibleImporter.cs:69`, `BibleQueryService.cs:88`

- **Escenario:** la importación retiene el bloqueo de escritura de SQLite durante segundos. `bible.lookup { record: true }` va por el carril en vivo y su escritura del historial espera ese bloqueo (Microsoft.Data.Sqlite reintenta hasta 30 s). El siguiente `showContent` queda en cola detrás.
- **Corrección:** anotar el historial fuera del carril en vivo (comando aparte en el carril lento o tarea de fondo), o con un tiempo de espera corto que se descarta si falla.

### 6. Una referencia con miles de versículos inexistentes genera miles de consultas [A PROBAR]
`BibleQueryService.cs:76`

- **Escenario:** el tope de 200 solo cuenta versículos devueltos. «Jn 3:99,99,99,…» pasa el parser (el límite es 176 por versículo) y no devuelve filas, así que lanza una consulta por tramo en el carril en vivo.
- **Corrección:** limitar el número de tramos en `ReferenceParser`, por ejemplo a 50.

## Menor

### 7. «1° Juan 3:16» falla con «Falta el libro» [VERIFICADO]
`Core/Scripture/ReferenceParser.cs:105-111`

`SplitBookAndRest` solo une el ordinal al libro si el carácter siguiente es una letra. «º» y «ª» lo son; «°» (signo de grado) no, aunque `NormalizeKey` sí lo trata. «1.ª Juan» y «1a Juan» también fallan. Funcionan «I Juan», «1Jn», «1º» y las colisiones Jud/Jue, Fil/Flm y Jn/1 Jn. **Corrección:** aceptar «°», «.ª» y «a/o» tras el dígito.

### 8. El aviso de afectados es incompleto para el historial [VERIFICADO]
`BibleImporter.cs:250-267` y `RecordHistoryAsync`

Una cita que cruza capítulos se guarda con `VerseEnd = null` y solo se comprueba el primer versículo. De «Mt 5:3,5,7» solo se guarda el primer tramo, y de «Gn 1-2» solo el capítulo 1. Los pasajes de servicios sí están bien (son de un solo capítulo).

### 9. Un archivo en Windows-1252 o Latin-1 [A PROBAR]
`BibleImporter.cs:35`

Probablemente lanza `InvalidOperationException` al transcodificar, que no está en el filtro de errores esperados. La consola diría «Error inesperado: InvalidOperationException» en vez de un error de formato claro. No se escribe nada en la base de datos. **Corrección:** añadir esa excepción al filtro o validar UTF-8 antes de leer.

### 10. Capítulos fuera del canon y versículos mayores de 176 [VERIFICADO]
`BibleImporter.cs:50`

Se guardan, pero el parser nunca deja consultarlos; el validador solo avisa. **Corrección:** descartarlos al importar, con aviso.

### 11. Transacción de importación diferida [A PROBAR]
`BibleImporter.cs:69`

Lee primero y escribe después. Si en medio se confirma una escritura del historial, puede fallar con `SQLITE_BUSY_SNAPSHOT`, que el reintento no resuelve; se informaría como «importación fallida». **Corrección:** abrir la transacción como `BEGIN IMMEDIATE`.

### 12. Consola e instancia única
- **Funciona:** `--import-bible` usa el mismo mutex que la aplicación gráfica y la rechaza con código 1. [VERIFICADO leyendo]
- **Sin test:** `CliTests` prueba `CliRunner`, no `CliHost`, así que la regla «falla si la aplicación está abierta» no tiene prueba automática.
- **Aviso engañoso:** si se abre la aplicación gráfica durante una importación de consola, aparece «ciérralo desde el Administrador de tareas». Hacerlo revierte la importación; no se pierde nada, pero el mensaje confunde.
- **Órdenes de solo lectura:** `--list-bibles`, `--show-verse` y `--verify-bible` no toman la instancia pero ejecutan `DatabaseInitializer`. Eso puede aplicar migraciones con la aplicación gráfica abierta (si el `.exe` es más nuevo) o crear una base de datos vacía en una carpeta equivocada.
- **Ctrl+C:** termina el proceso sin el mensaje «Cancelado»; la transacción se revierte igual.

### 13. Abreviatura [VERIFICADO]
`BibleImporter.cs:230`

«rvr1960» crea una Biblia distinta de «RVR1960», y la abreviatura admite caracteres de control.

### 14. Memoria con un archivo de unos 24 MB [A PROBAR]
Se mantienen a la vez varias copias del texto (objetos leídos, `pieces`, `prepared`, `incoming`, `current` y `toWrite`). `BiblePerformanceTests` mide tiempos, no memoria.

### 15. Honestidad de los tests
- **Privacidad:** el test «sin texto bíblico» solo revisa el informe en JSON, no los logs ni los mensajes de error. Leyendo el código no hay texto en ellos; como mucho, 15 caracteres de un código USFM ilegible o un carácter suelto en el mensaje de un `JsonException`.
- **Atomicidad:** `If_the_write_fails_halfway…` sí prueba un fallo real a mitad de escritura, con reversión.
- **Cancelación:** solo se prueba antes de escribir.

## Correcto y verificado leyendo
- **Atomicidad:** todo se escribe en una sola transacción con `await using` (se revierte al fallar o cancelar). Un JSON truncado falla antes de tocar la base de datos.
- **Entradas malformadas:** el anidamiento profundo está limitado a 64 niveles; los números se leen en modo estricto (un desbordamiento o un valor no numérico da error); los números cero o negativos se descartan con aviso; los libros desconocidos se ignoran.
- **FTS:** los triggers de INSERT, UPDATE (solo de `Text` e `Id`) y DELETE son correctos con contenido externo. `recursive_triggers` se activa en cada conexión de `SqliteConnectionFactory`, también en la consola y en las migraciones. El integrity-check tiene reconstrucción probada.
- **Eliminación explícita:** se borra con `DELETE`, nunca con `REPLACE`, y los pasajes de servicios afectados se detectan bien.
- **Privacidad:** no hay texto bíblico en logs ni eventos. Los tests de guardia de `data/` existen y se prueban a sí mismos.

## Pruebas pendientes
1. Importar a propósito un archivo de otra traducción con la misma abreviatura, contra una base de datos de prueba (hallazgo 1).
2. Importar un archivo válido sin Judas (hallazgo 2).
3. Importar un archivo guardado en Windows-1252 (hallazgo 9).
4. Escribir «Jn 3:99» repetido unas 5000 veces y medir cuánto tarda en responder (hallazgo 6).
5. Proyectar versículos mientras corre una importación (hallazgo 5).
6. Importar el archivo real de unos 24 MB vigilando la memoria del proceso (hallazgo 14).
