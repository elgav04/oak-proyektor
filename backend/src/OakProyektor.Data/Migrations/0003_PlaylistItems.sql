-- Migración 3 (hallazgo I13): Playlists.ItemsJson (un JSON sin integridad referencial) se reemplaza por la tabla PlaylistItems.
--
-- QUÉ PASA AL BORRAR UNA CANCIÓN (O PRESENTACIÓN, O LIBRO) INCLUIDA EN UN SERVICIO:
--   NO se bloquea el borrado (una canción usada alguna vez en un servicio pasado sería imborrable para siempre) y NO queda una
--   referencia rota: la clave foránea pasa a NULL (ON DELETE SET NULL) y el elemento se conserva con su Title (instantánea del
--   nombre al añadirlo). La columna calculada IsMissing vale 1 y quien abra el servicio debe AVISAR al operador («Falta: …»).
--   Nunca debe presentarse un elemento faltante como si estuviera.

-- Guardia: ningún código escribió jamás en ItemsJson (siempre es '[]'). Si hubiera contenido, su formato es desconocido y no se
-- puede convertir sin adivinar: se ABORTA la migración (se revierte y queda la copia previa) en vez de perder datos en silencio.
CREATE TEMP TABLE MigracionGuardia (Ok INTEGER NOT NULL, CONSTRAINT Playlists_ItemsJson_tiene_datos_que_no_se_pueden_migrar CHECK (Ok = 1));
INSERT INTO MigracionGuardia (Ok)
    SELECT CASE WHEN EXISTS (SELECT 1 FROM Playlists WHERE ItemsJson IS NOT NULL AND TRIM(ItemsJson) NOT IN ('', '[]')) THEN 0 ELSE 1 END;
DROP TABLE MigracionGuardia;

CREATE TABLE PlaylistItems (
    Id             INTEGER PRIMARY KEY,
    PlaylistId     INTEGER NOT NULL REFERENCES Playlists(Id) ON DELETE CASCADE,
    SequenceIndex  INTEGER NOT NULL,
    ItemType       TEXT NOT NULL CHECK (ItemType IN ('SONG', 'PRESENTATION', 'PASSAGE')),
    Title          TEXT NOT NULL,
    SongId         INTEGER REFERENCES Songs(Id) ON DELETE SET NULL,
    PresentationId INTEGER REFERENCES Presentations(Id) ON DELETE SET NULL,
    BookId         INTEGER REFERENCES Books(Id) ON DELETE SET NULL,
    Chapter        INTEGER,
    VerseStart     INTEGER,
    VerseEnd       INTEGER,
    -- El registro referenciado, sea del tipo que sea (una FK por tipo permite claves foráneas reales; una sola columna
    -- polimórfica ItemRefId no podría tenerlas).
    ItemRefId      INTEGER GENERATED ALWAYS AS (COALESCE(SongId, PresentationId, BookId)) VIRTUAL,
    -- 1 si el registro referenciado ya no existe.
    IsMissing      INTEGER GENERATED ALWAYS AS (
                       CASE ItemType WHEN 'SONG' THEN SongId IS NULL WHEN 'PRESENTATION' THEN PresentationId IS NULL ELSE BookId IS NULL END
                   ) VIRTUAL,
    CONSTRAINT CK_PlaylistItems_Shape CHECK (
        (ItemType = 'SONG'         AND PresentationId IS NULL AND BookId IS NULL AND Chapter IS NULL AND VerseStart IS NULL AND VerseEnd IS NULL) OR
        (ItemType = 'PRESENTATION' AND SongId IS NULL AND BookId IS NULL AND Chapter IS NULL AND VerseStart IS NULL AND VerseEnd IS NULL) OR
        (ItemType = 'PASSAGE'      AND SongId IS NULL AND PresentationId IS NULL AND Chapter IS NOT NULL AND VerseStart IS NOT NULL
                                   AND (VerseEnd IS NULL OR VerseEnd >= VerseStart))
    )
);
CREATE INDEX IX_PlaylistItems_PlaylistId_SequenceIndex ON PlaylistItems (PlaylistId, SequenceIndex);
-- Para saber en qué servicios se usa un registro antes de borrarlo, y para que el SET NULL no recorra la tabla entera.
CREATE INDEX IX_PlaylistItems_SongId ON PlaylistItems (SongId) WHERE SongId IS NOT NULL;
CREATE INDEX IX_PlaylistItems_PresentationId ON PlaylistItems (PresentationId) WHERE PresentationId IS NOT NULL;
CREATE INDEX IX_PlaylistItems_BookId ON PlaylistItems (BookId) WHERE BookId IS NOT NULL;

-- Un elemento NUEVO debe referenciar un registro existente: SET NULL solo existe para registros borrados DESPUÉS de añadirlos.
CREATE TRIGGER PlaylistItems_bi BEFORE INSERT ON PlaylistItems
WHEN (NEW.ItemType = 'SONG' AND NEW.SongId IS NULL)
  OR (NEW.ItemType = 'PRESENTATION' AND NEW.PresentationId IS NULL)
  OR (NEW.ItemType = 'PASSAGE' AND NEW.BookId IS NULL)
BEGIN
    SELECT RAISE(ABORT, 'Un elemento nuevo del servicio debe referenciar un registro existente');
END;

ALTER TABLE Playlists DROP COLUMN ItemsJson;