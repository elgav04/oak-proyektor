-- Migración 4 (hallazgo de la ronda 2, menor «pasajes de PlaylistItems dependen del Id de libro»).
-- La migración 3 NO se edita: ya está publicada.
--
-- PROBLEMA: un pasaje de un servicio apuntaba con una clave foránea (BookId) al libro de UNA Biblia concreta. Reimportar esa Biblia
-- (borrar e importar de nuevo) borra sus libros: la clave pasaba a NULL (ON DELETE SET NULL) y TODOS los pasajes de TODOS los servicios
-- quedaban marcados como faltantes.
--
-- DECISIÓN: un pasaje se guarda por REFERENCIA CANÓNICA, no por clave foránea:
--     BookNumber (1 a 66, número canónico del libro) + Chapter + VerseStart + VerseEnd + BibleAbbreviation (la Biblia preferida, como TEXTO).
-- Se RESUELVE al cargar el servicio (PassageResolver): si esa Biblia no está instalada el elemento sigue existiendo y se muestra como
-- «Biblia no instalada» (se ofrece usar la Biblia por defecto); nunca se pierde el pasaje ni se marca como faltante por borrar una Biblia.
-- Solo las canciones y presentaciones conservan clave foránea (ON DELETE SET NULL → «Falta: …»).
--
-- REGLA DEL IMPORTADOR DE BIBLIAS (documentada en CLAUDE.md): actualiza EN EL SITIO (upsert dentro de una transacción) y conserva el
-- BibleId; no borra e importa de nuevo. Por eso esta migración hace ÚNICO el índice de Verses por (BookId, Chapter, VerseNumber): sin un
-- índice único no existe ON CONFLICT(BookId, Chapter, VerseNumber). (Books ya es único por (BibleId, BookNumber) y Bibles por Abbreviation.)

-- Guardias: si hubiera datos que no se pueden convertir sin adivinar, se ABORTA (se revierte y queda la copia previa) en vez de perderlos
-- en silencio. Mismo patrón que la migración 3.
CREATE TEMP TABLE MigracionGuardia (Ok INTEGER NOT NULL,
    CONSTRAINT Hay_pasajes_cuyo_libro_ya_no_existe_y_no_se_puede_saber_cual_era CHECK (Ok = 1));
INSERT INTO MigracionGuardia (Ok)
    SELECT CASE WHEN EXISTS (SELECT 1 FROM PlaylistItems WHERE ItemType = 'PASSAGE' AND BookId IS NULL) THEN 0 ELSE 1 END;
DROP TABLE MigracionGuardia;

CREATE TEMP TABLE MigracionGuardia (Ok INTEGER NOT NULL,
    CONSTRAINT Hay_pasajes_de_un_libro_con_numero_fuera_de_1_a_66 CHECK (Ok = 1));
INSERT INTO MigracionGuardia (Ok)
    SELECT CASE WHEN EXISTS (
        SELECT 1 FROM PlaylistItems pi JOIN Books b ON b.Id = pi.BookId
        WHERE pi.ItemType = 'PASSAGE' AND b.BookNumber NOT BETWEEN 1 AND 66) THEN 0 ELSE 1 END;
DROP TABLE MigracionGuardia;

-- Imprescindible para el upsert del importador (INSERT … ON CONFLICT(BookId, Chapter, VerseNumber) DO UPDATE).
DROP INDEX IX_Verses_BookId_Chapter_VerseNumber;
CREATE UNIQUE INDEX IX_Verses_BookId_Chapter_VerseNumber ON Verses (BookId, Chapter, VerseNumber);

-- PlaylistItems se RECONSTRUYE (el CHECK y las columnas cambian): crear la nueva, copiar, DROP de la vieja y RENOMBRAR la nueva.
CREATE TABLE PlaylistItems_new (
    Id                INTEGER PRIMARY KEY,
    PlaylistId        INTEGER NOT NULL REFERENCES Playlists(Id) ON DELETE CASCADE,
    SequenceIndex     INTEGER NOT NULL,
    ItemType          TEXT NOT NULL CHECK (ItemType IN ('SONG', 'PRESENTATION', 'PASSAGE')),
    Title             TEXT NOT NULL,
    SongId            INTEGER REFERENCES Songs(Id) ON DELETE SET NULL,
    PresentationId    INTEGER REFERENCES Presentations(Id) ON DELETE SET NULL,
    -- Referencia canónica del pasaje. BibleAbbreviation es TEXTO, deliberadamente NO una clave foránea.
    BookNumber        INTEGER,
    Chapter           INTEGER,
    VerseStart        INTEGER,
    VerseEnd          INTEGER,
    BibleAbbreviation TEXT,
    -- El registro referenciado de una canción o presentación (un pasaje no tiene: se resuelve por referencia canónica).
    ItemRefId         INTEGER GENERATED ALWAYS AS (COALESCE(SongId, PresentationId)) VIRTUAL,
    -- 1 si la canción o presentación ya no existe. Un pasaje NUNCA está «faltante» por borrar una Biblia: su estado se calcula al resolverlo.
    IsMissing         INTEGER GENERATED ALWAYS AS (
                          CASE ItemType WHEN 'SONG' THEN SongId IS NULL WHEN 'PRESENTATION' THEN PresentationId IS NULL ELSE 0 END
                      ) VIRTUAL,
    CONSTRAINT CK_PlaylistItems_Shape CHECK (
        (ItemType = 'SONG'         AND PresentationId IS NULL AND BookNumber IS NULL AND Chapter IS NULL AND VerseStart IS NULL
                                   AND VerseEnd IS NULL AND BibleAbbreviation IS NULL) OR
        (ItemType = 'PRESENTATION' AND SongId IS NULL AND BookNumber IS NULL AND Chapter IS NULL AND VerseStart IS NULL
                                   AND VerseEnd IS NULL AND BibleAbbreviation IS NULL) OR
        (ItemType = 'PASSAGE'      AND SongId IS NULL AND PresentationId IS NULL
                                   -- IS NOT NULL explícito: en SQLite un CHECK con NULL se da por cumplido (BookNumber BETWEEN … con NULL no falla).
                                   AND BookNumber IS NOT NULL AND Chapter IS NOT NULL AND VerseStart IS NOT NULL
                                   AND BookNumber BETWEEN 1 AND 66 AND Chapter >= 1 AND VerseStart >= 1
                                   AND (VerseEnd IS NULL OR VerseEnd >= VerseStart)
                                   AND BibleAbbreviation IS NOT NULL AND length(trim(BibleAbbreviation)) > 0)
    )
);

-- Migración de los datos existentes: el número de libro y la abreviatura salen del libro y la Biblia a los que apuntaba BookId.
INSERT INTO PlaylistItems_new (Id, PlaylistId, SequenceIndex, ItemType, Title, SongId, PresentationId, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation)
SELECT pi.Id, pi.PlaylistId, pi.SequenceIndex, pi.ItemType, pi.Title, pi.SongId, pi.PresentationId,
       b.BookNumber, pi.Chapter, pi.VerseStart, pi.VerseEnd, bi.Abbreviation
FROM PlaylistItems pi
LEFT JOIN Books b  ON b.Id = pi.BookId
LEFT JOIN Bibles bi ON bi.Id = b.BibleId;

DROP TABLE PlaylistItems;                              -- se lleva sus índices y su trigger
ALTER TABLE PlaylistItems_new RENAME TO PlaylistItems;

CREATE INDEX IX_PlaylistItems_PlaylistId_SequenceIndex ON PlaylistItems (PlaylistId, SequenceIndex);
-- Para saber en qué servicios se usa un registro antes de borrarlo, y para que el SET NULL no recorra la tabla entera.
CREATE INDEX IX_PlaylistItems_SongId ON PlaylistItems (SongId) WHERE SongId IS NOT NULL;
CREATE INDEX IX_PlaylistItems_PresentationId ON PlaylistItems (PresentationId) WHERE PresentationId IS NOT NULL;

-- Un elemento NUEVO de canción o presentación debe referenciar un registro existente: SET NULL solo existe para registros borrados
-- DESPUÉS de añadirlos. (Un pasaje no tiene referencia que comprobar: lo valida el CHECK de la tabla.)
CREATE TRIGGER PlaylistItems_bi BEFORE INSERT ON PlaylistItems
WHEN (NEW.ItemType = 'SONG' AND NEW.SongId IS NULL)
  OR (NEW.ItemType = 'PRESENTATION' AND NEW.PresentationId IS NULL)
BEGIN
    SELECT RAISE(ABORT, 'Un elemento nuevo del servicio debe referenciar un registro existente');
END;

-- El guardia que faltaba: lo mismo para las ACTUALIZACIONES. «Faltante» solo puede nacer de borrar el registro (la clave foránea pasa a NULL),
-- nunca de un UPDATE a mano. Se impide (1) desvincular una canción o presentación que SIGUE existiendo y (2) cambiar el tipo a canción o
-- presentación dejando la referencia vacía. Reapuntar a otro registro existente sí se permite. El borrado del registro (ON DELETE SET NULL)
-- sigue funcionando: entonces el registro ya no existe.
CREATE TRIGGER PlaylistItems_bu BEFORE UPDATE OF SongId, PresentationId, ItemType ON PlaylistItems
WHEN (OLD.SongId IS NOT NULL AND NEW.SongId IS NULL AND EXISTS (SELECT 1 FROM Songs WHERE Id = OLD.SongId))
  OR (OLD.PresentationId IS NOT NULL AND NEW.PresentationId IS NULL AND EXISTS (SELECT 1 FROM Presentations WHERE Id = OLD.PresentationId))
  OR (NEW.ItemType <> OLD.ItemType AND ((NEW.ItemType = 'SONG' AND NEW.SongId IS NULL) OR (NEW.ItemType = 'PRESENTATION' AND NEW.PresentationId IS NULL)))
BEGIN
    SELECT RAISE(ABORT, 'Un elemento del servicio solo queda faltante al borrar su registro; no se puede desvincular a mano');
END;
