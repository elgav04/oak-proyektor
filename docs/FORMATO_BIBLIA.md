# Formato de las Biblias: fuente aprobada, limpieza, numeración y verificación

> **Este documento no contiene texto bíblico.** Solo describe estructuras, nombres de campos, reglas y conteos. Las Biblias del operador (que tienen derechos de
> autor) viven en `data/` (fuera de git); ver «Reglas» al final.

Todo lo que sigue está implementado en `backend/src/OakProyektor.Core/Scripture/` (lectura, limpieza, numeración, parser) y `backend/src/OakProyektor.Data/Scripture/`
(importador, validador, verificación cruzada, consultas). El análisis que justifica la elección de la fuente se hizo con scripts locales que solo imprimían
conteos y referencias.

## 1. Fuente de importación aprobada: `youversion-json`

Clase `YouVersionJsonSource` (`IBibleSourceFormat.Id = "youversion-json"`, `CanImport = true`). Es el formato del archivo `RVR1960_vid_149.json` del operador
(23,6 MB, UTF-8 sin BOM, sin señales de codificación rota).

### Estructura

```text
{
  "version_id": <número>, "local_abbreviation": "…", "local_title": "…", "language": {…}, "repository": "…", "publisher": {…}, "copyright": {…},   ← se ignoran
  "books": [
    {
      "usfm": "GEN",                      ← código USFM del libro (66 libros, orden canónico)
      "human": "…",                       ← se ignora (los nombres vienen de CanonicalBooks)
      "chapters": [
        {
          "usfm": "GEN.1",                ← «LIBRO.capítulo»; el capítulo es lo que va tras el último punto
          "human": "…", "previous": …, "next": {…},                              ← se ignoran
          "html": "<div …>…</div>",       ← el HTML del capítulo: se DESCARTA (no se lee a memoria: el deserializador lo salta)
          "items": [
            { "type": "verse",    "verse_numbers": [1], "lines": ["…", "…"], "rlw_lines": […], "np": true },
            { "type": "heading1", "verse_numbers": [],  "lines": ["…"], … },     ← títulos de sección: se ignoran
            { "type": "label",    "verse_numbers": [],  "lines": ["…"], … },     ← rótulos (p. ej. de los salmos): se ignoran
            { "type": "section1", … }                                             ← se ignora
          ]
        }
      ]
    }
  ]
}
```

Solo cuentan los items de `type = "verse"`: su número es `verse_numbers[0]` y su texto, las `lines` unidas con un espacio. Los números de libro y de capítulo salen
de `usfm`; el número de libro canónico (1 a 66) es la posición del código en `CanonicalBooks` (`GEN` = 1 … `REV` = 66).

### Particularidades que el lector maneja (verificadas en el archivo real)

| Particularidad | Qué ocurre | Qué se hace |
|---|---|---|
| Libros de **un solo capítulo** (Abdías, Filemón, 2 Juan, 3 Juan, Judas) | El capítulo `X.1` aparece **dos veces** (1194 entradas de capítulo frente a 1189). Leerlo dos veces duplicaría el texto de cada versículo. | Se conserva la **primera** aparición de cada capítulo; las repetidas se descartan y se cuentan (`DuplicateChaptersDiscarded`: 5). |
| Versículos en varias **líneas** | Un item trae varias `lines` (poesía, párrafos). | Se unen con un espacio. |
| Versículos en varios **items** | Un mismo número de versículo aparece en dos items seguidos. | Se unen en orden con un espacio. |
| Items sin número de versículo | Títulos, rótulos, secciones. | Se ignoran. |
| Libro o capítulo con código ilegible | — | Se ignora y queda una **nota** en el informe (solo códigos, nunca texto). |
| Un item con varios `verse_numbers` | No ocurre en el archivo real. | El texto va al primer número y queda una nota. |

Conteos del archivo real tras estas reglas: 66 libros, 1189 capítulos, **31 104 versículos** (5 084 unidos desde varios trozos, 0 vacíos).

### Mapeo a nuestro modelo

| Fuente | Modelo |
|---|---|
| `Abbreviation` / nombre / idioma | Los da quien importa (`--abbr`, `--name`, `--lang`); `Bibles` se actualiza por `Abbreviation` y el `BibleId` se conserva |
| `books[].usfm` | `Books.BookNumber` (canónico 1 a 66); `Books.Name` y `Books.Testament` (`OT`/`NT`) salen de `CanonicalBooks`, no del archivo |
| `chapters[].usfm` (tras el último punto) | `Verses.Chapter` |
| `items[].verse_numbers[0]` | `Verses.VerseNumber` |
| `items[].lines` unidas | `Verses.Text`, ya limpio |

## 2. Limpieza mínima (`VerseCleaner`)

Acordada: **no se toca la puntuación, las tildes ni las mayúsculas.** Solo:

1. Normalización Unicode **NFC**.
2. Cualquier tramo de espacios en blanco (incluidos espacios no separables, saltos de línea y tabuladores) pasa a **un espacio**; se recortan los extremos.
3. Se eliminan los caracteres de control.

El importador cuenta cuántos versículos cambiaron (`CleanedVerses`) y lo informa **sin mostrar el texto**. El archivo aprobado ya viene limpio: la cuenta es 0.

## 3. Perfil de numeración (`NumberingProfile`)

Lista de **uniones de versículos** aplicadas al importar, definida en código solo con referencias (`NumberingProfiles`), activable por Biblia
(`Bibles.NumberingProfile`, migración 6; `NULL` = sin perfil). Sirve para que una fuente que numera distinto coincida con la Biblia impresa del operador.

| Id | Qué hace | Total |
|---|---|---|
| `none` (por defecto) | Respeta la numeración de la fuente. | 31 104 |
| `merge-ps47-3jn` | Une Salmos 47:9+10 y 3 Juan 1:14+15 (texto unido con un espacio). | 31 102 |

La fuente aprobada parte esos dos versículos (31 104); la numeración «habitual» del canon protestante tiene 31 102. **Cuál es la de tu Biblia impresa lo decides tú**
(compruébalo con `--show-verse "Sal 47:9" --bible RVR1960`).

Una unión solo se aplica si la fuente tiene **todos** los versículos del tramo; si no, se informa como «no aplicada».

### Reimportar con otro perfil

La importación **actualiza en el sitio** y conserva el `BibleId`. Los versículos que la fuente ya no trae (p. ej. el 47:10 al activar el perfil) se **eliminan con
`DELETE` explícito dentro de la misma transacción** (nunca por reemplazo), y el informe **avisa de cada elemento de `PlaylistItems` o del historial de citas que
apuntaba a un versículo eliminado** (`AffectedItems`). Los pasajes se guardan por referencia canónica, así que no se pierden: el rango se resuelve con lo que quede.

Protección: una reimportación que eliminaría más del 1 % de los versículos (mínimo 10) se **rechaza** (`bible.tooManyDeletions`) salvo `--allow-large-deletion`.
Casi siempre es un archivo equivocado o truncado.

## 4. Validación tras importar (`BibleValidator`)

**Avisa, no falla.** Conteos y referencias (códigos USFM), nunca texto:

* libros, capítulos y versículos; versículos vacíos;
* libros que faltan, y libros con un número de capítulos distinto del canon (la cifra de capítulos por libro del canon sí está en el código: 1189 en total);
* capítulos que no empiezan en el versículo 1 o tienen huecos (hasta 50 referencias);
* diferencia del total frente a los 31 102 versículos habituales.

**Límite honesto:** no hay una tabla de versículos por capítulo (1189 cifras que no se pueden verificar sin una Biblia de referencia), así que no se detecta, por
ejemplo, un versículo que falta al final de un capítulo. Para eso está la verificación cruzada.

Después se ejecuta el `integrity-check` del FTS (`INSERT INTO VersesFts(VersesFts, rank) VALUES('integrity-check', 1)`); si falla, se reconstruye el índice una vez y se
comprueba otra vez. Se informa el resultado.

## 5. Verificación cruzada (`BibleCrossChecker`, `--verify-bible`)

Compara la Biblia importada con **otro archivo** (que **no** se importa), solo con conteos y referencias. Al archivo se le aplica la misma limpieza y el mismo perfil de
numeración que tiene la Biblia. Clasifica las diferencias:

* **(a)** solo espacios o puntuación; **(b)** solo tildes o mayúsculas; **(c)** palabras distintas;
* **rótulo del Salmo 119**: una fuente puede arrastrar, pegado al final de los versículos 8, 16, … 168, el nombre de la letra hebrea de la estrofa siguiente (21 en
  total; Bet, Guímel, … Tau). Se reconoce comparando **letras** (la fuente también pierde espacios tras la puntuación y pega el rótulo a la última palabra) y que el
  rótulo sea el que corresponde a ese versículo; se cuenta aparte y **no** como diferencia de texto. Es un defecto de esa fuente, no del texto bíblico;
* versículos que solo están en un lado.

Resultado esperado (el que se acordó): **menos de 40 versículos con palabras distintas** (sin contar los 21 rótulos); cualquier otra cosa sale como aviso.
Con el archivo real y el segundo archivo del operador: 11 versículos con palabras distintas, 21 rótulos, 1 290 de tildes o mayúsculas, 0 versículos en un solo lado.

### Formato `named-books-json` (solo verificación)

Clase `NamedBooksJsonSource` (`CanImport = false`: el importador lo rechaza). Es el formato del segundo archivo del operador:

```text
{ "Nombre del libro": { "<capítulo>": { "<versículo>": "texto" } }, …, "lang": "…" }
```

Los libros se identifican por **nombre** con `BookNames` (incluidos los nombres irregulares de ese archivo: «S. Mateo», «S.Juan», «Cantares»); las claves que no son objetos
(`lang`) se ignoran. Tiene defectos conocidos (espacios sin normalizar, falta el espacio tras la puntuación en miles de versículos, el rótulo del Salmo 119), por eso
solo sirve para comparar.

## 6. Añadir otro formato

Se escribe una clase que implemente `IBibleSourceFormat` (`Id`, `Description`, `CanImport`, `ReadAsync(path, ct)` → `BibleSourceData`) y se añade a
`BibleSourceFormats.All`. El importador, el validador, la verificación cruzada, el CLI y el IPC **no cambian**. Reglas para el lector nuevo:

* devolver libros por número canónico (1 a 66) y texto **sin limpiar** (la limpieza es del importador);
* las notas de lectura (`Notes`) llevan solo códigos y conteos, **nunca texto del archivo**;
* los errores de formato son `JsonException`, `InvalidDataException` o `IOException` (el importador los convierte en `bible.sourceInvalid` sin mostrar contenido).

## 7. Reglas (resumen; el detalle está en CLAUDE.md)

* **Ningún texto bíblico con derechos de autor** en el repositorio, los tests, los logs, los eventos de progreso, los mensajes de error ni la documentación.
* **Ningún test depende de `data/`**: usan datos sintéticos inventados. Una prueba manual que necesite el archivo real se marca `[ManualFact("VARIABLE")]` y se omite si
  la variable o el archivo no existen. `RepositoryGuardTests` falla si algún archivo bajo `data/` está versionado o si una prueba referencia `data/imports`.
* El importador actualiza **en el sitio** y conserva el `BibleId`; **`INSERT OR REPLACE` está prohibido** (`SourceRulesTests`).
* Toda conexión sale de `ISqliteConnectionFactory` (con `recursive_triggers = ON`).
