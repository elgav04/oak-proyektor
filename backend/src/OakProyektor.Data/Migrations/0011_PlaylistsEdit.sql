-- Migración 11: listas de culto editables (docs/DISENO_LISTAS.md). Las migraciones publicadas (1 a 10) NO se editan.
--
-- 1) Playlists gana Notes, Revision (concurrencia optimista: una vista desactualizada no puede mover ni borrar elementos), CreatedAt y UpdatedAt, y la fecha se VALIDA.
--    ServiceDate era texto libre: una fecha que no sea AAAA-MM-DD válida se normaliza a NULL (= plantilla) CONSERVANDO el texto original en Notes; nunca se aborta ni se pierde nada.
-- 2) PlaylistItems se RECONSTRUYE (el CHECK de ItemType y la forma cambian): nuevo tipo 'NOTE' (separador o recordatorio que no se proyecta), con Note y ClearOnActivate.
--    Patrón del repositorio: crear la nueva, copiar TODO (incluidos los elementos faltantes, con SongId NULL), DROP de la vieja, RENOMBRAR la nueva y recrear índices y triggers.
--    El runner ya apaga foreign_keys FUERA de la transacción y comprueba foreign_key_check antes del COMMIT.

ALTER TABLE Playlists ADD COLUMN Notes     TEXT NOT NULL DEFAULT '';
ALTER TABLE Playlists ADD COLUMN Revision  INTEGER NOT NULL DEFAULT 1;
ALTER TABLE Playlists ADD COLUMN CreatedAt TEXT;
ALTER TABLE Playlists ADD COLUMN UpdatedAt TEXT;

-- Fechas que no son AAAA-MM-DD válidas: el texto original pasa a Notes (si no estaba vacío) y la lista queda sin fecha.
UPDATE Playlists
SET Notes = CASE WHEN length(trim(ServiceDate)) > 0 THEN 'Fecha original: ' || trim(ServiceDate) ELSE Notes END,
    ServiceDate = NULL
WHERE ServiceDate IS NOT NULL
  AND NOT (ServiceDate GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' AND date(ServiceDate) = ServiceDate);

UPDATE Playlists SET CreatedAt = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), UpdatedAt = strftime('%Y-%m-%dT%H:%M:%fZ', 'now');

CREATE TRIGGER Playlists_date_bi BEFORE INSERT ON Playlists
WHEN NEW.ServiceDate IS NOT NULL
 AND NOT (NEW.ServiceDate GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' AND date(NEW.ServiceDate) = NEW.ServiceDate)
BEGIN
    SELECT RAISE(ABORT, 'La fecha del servicio debe tener la forma AAAA-MM-DD (y ser una fecha válida)');
END;

CREATE TRIGGER Playlists_date_bu BEFORE UPDATE OF ServiceDate ON Playlists
WHEN NEW.ServiceDate IS NOT NULL
 AND NOT (NEW.ServiceDate GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' AND date(NEW.ServiceDate) = NEW.ServiceDate)
BEGIN
    SELECT RAISE(ABORT, 'La fecha del servicio debe tener la forma AAAA-MM-DD (y ser una fecha válida)');
END;

CREATE TABLE PlaylistItems_new (
    Id                INTEGER PRIMARY KEY,
    PlaylistId        INTEGER NOT NULL REFERENCES Playlists(Id) ON DELETE CASCADE,
    SequenceIndex     INTEGER NOT NULL,
    ItemType          TEXT NOT NULL CHECK (ItemType IN ('SONG', 'PRESENTATION', 'PASSAGE', 'NOTE')),
    Title             TEXT NOT NULL,
    SongId            INTEGER REFERENCES Songs(Id) ON DELETE SET NULL,
    PresentationId    INTEGER REFERENCES Presentations(Id) ON DELETE SET NULL,
    BookNumber        INTEGER,
    Chapter           INTEGER,
    VerseStart        INTEGER,
    VerseEnd          INTEGER,
    BibleAbbreviation TEXT,
    -- Solo para NOTE: el texto del recordatorio y si, al activarla, se quita el texto de la pantalla (apagado por defecto).
    Note              TEXT,
    ClearOnActivate   INTEGER NOT NULL DEFAULT 0 CHECK (ClearOnActivate IN (0, 1)),
    ItemRefId         INTEGER GENERATED ALWAYS AS (COALESCE(SongId, PresentationId)) VIRTUAL,
    -- 1 si la canción o presentación ya no existe. Un pasaje o una nota NUNCA están «faltantes».
    IsMissing         INTEGER GENERATED ALWAYS AS (
                          CASE ItemType WHEN 'SONG' THEN SongId IS NULL WHEN 'PRESENTATION' THEN PresentationId IS NULL ELSE 0 END
                      ) VIRTUAL,
    CONSTRAINT CK_PlaylistItems_Shape CHECK (
        (ItemType = 'SONG'         AND PresentationId IS NULL AND BookNumber IS NULL AND Chapter IS NULL AND VerseStart IS NULL
                                   AND VerseEnd IS NULL AND BibleAbbreviation IS NULL AND Note IS NULL AND ClearOnActivate = 0) OR
        (ItemType = 'PRESENTATION' AND SongId IS NULL AND BookNumber IS NULL AND Chapter IS NULL AND VerseStart IS NULL
                                   AND VerseEnd IS NULL AND BibleAbbreviation IS NULL AND Note IS NULL AND ClearOnActivate = 0) OR
        (ItemType = 'PASSAGE'      AND SongId IS NULL AND PresentationId IS NULL AND Note IS NULL AND ClearOnActivate = 0
                                   AND BookNumber IS NOT NULL AND Chapter IS NOT NULL AND VerseStart IS NOT NULL
                                   AND BookNumber BETWEEN 1 AND 66 AND Chapter >= 1 AND VerseStart >= 1
                                   AND (VerseEnd IS NULL OR VerseEnd >= VerseStart)
                                   AND BibleAbbreviation IS NOT NULL AND length(trim(BibleAbbreviation)) > 0) OR
        (ItemType = 'NOTE'         AND SongId IS NULL AND PresentationId IS NULL AND BookNumber IS NULL AND Chapter IS NULL AND VerseStart IS NULL
                                   AND VerseEnd IS NULL AND BibleAbbreviation IS NULL AND length(trim(Title)) > 0)
    )
);

-- Copia de TODOS los elementos existentes tal cual (los faltantes, con SongId NULL, también: el trigger BEFORE INSERT se crea DESPUÉS de copiar).
INSERT INTO PlaylistItems_new (Id, PlaylistId, SequenceIndex, ItemType, Title, SongId, PresentationId, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation)
SELECT Id, PlaylistId, SequenceIndex, ItemType, Title, SongId, PresentationId, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation
FROM PlaylistItems;

DROP TABLE PlaylistItems;                              -- se lleva sus índices y sus triggers
ALTER TABLE PlaylistItems_new RENAME TO PlaylistItems;

CREATE INDEX IX_PlaylistItems_PlaylistId_SequenceIndex ON PlaylistItems (PlaylistId, SequenceIndex);
CREATE INDEX IX_PlaylistItems_SongId ON PlaylistItems (SongId) WHERE SongId IS NOT NULL;
CREATE INDEX IX_PlaylistItems_PresentationId ON PlaylistItems (PresentationId) WHERE PresentationId IS NOT NULL;

-- Mismas garantías de la migración 4: «faltante» solo nace de BORRAR el registro, nunca de crear o desvincular a mano.
CREATE TRIGGER PlaylistItems_bi BEFORE INSERT ON PlaylistItems
WHEN (NEW.ItemType = 'SONG' AND NEW.SongId IS NULL)
  OR (NEW.ItemType = 'PRESENTATION' AND NEW.PresentationId IS NULL)
BEGIN
    SELECT RAISE(ABORT, 'Un elemento nuevo del servicio debe referenciar un registro existente');
END;

CREATE TRIGGER PlaylistItems_bu BEFORE UPDATE OF SongId, PresentationId, ItemType ON PlaylistItems
WHEN (OLD.SongId IS NOT NULL AND NEW.SongId IS NULL AND EXISTS (SELECT 1 FROM Songs WHERE Id = OLD.SongId))
  OR (OLD.PresentationId IS NOT NULL AND NEW.PresentationId IS NULL AND EXISTS (SELECT 1 FROM Presentations WHERE Id = OLD.PresentationId))
  OR (NEW.ItemType <> OLD.ItemType AND ((NEW.ItemType = 'SONG' AND NEW.SongId IS NULL) OR (NEW.ItemType = 'PRESENTATION' AND NEW.PresentationId IS NULL)))
BEGIN
    SELECT RAISE(ABORT, 'Un elemento del servicio solo queda faltante al borrar su registro; no se puede desvincular a mano');
END;
