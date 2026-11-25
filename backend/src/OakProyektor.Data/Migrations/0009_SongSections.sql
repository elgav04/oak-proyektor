-- Migración 9: módulo de canciones. Las canciones pasan de una lista plana de diapositivas (SongSlides) a SECCIONES con etiqueta (Verso 1, Coro…) y un ORDEN de
-- reproducción (SongArrangements): repetir el coro no duplica su texto y editarlo lo cambia en todas partes. Las diapositivas ya no se guardan: se derivan al
-- proyectar (corte manual = línea en blanco dentro de la sección, más división automática por medición real en el panel).
--
-- Songs NO se reconstruye (PlaylistItems.SongId la referencia con ON DELETE SET NULL): solo se le añaden columnas.
-- SongSlides queda CONGELADA (nadie la escribe ni la lee) y se borrará en una migración posterior, cuando el módulo lleve un tiempo en uso; la copia previa del
-- MigrationRunner y el respaldo diario cubren el intervalo. Lo que haya en ella se CONVIERTE aquí sin suponer que está vacía.

-- ───────────── 1. Se retira el índice de búsqueda antiguo (alimentado por SongSlides). SongsFts es derivado: se recrea y se repuebla abajo ─────────────
DROP TRIGGER Songs_ai;
DROP TRIGGER Songs_au;
DROP TRIGGER Songs_ad;
DROP TRIGGER SongSlides_ai;
DROP TRIGGER SongSlides_au;
DROP TRIGGER SongSlides_ad;
DROP TABLE SongsFts;

-- ───────────── 2. Columnas nuevas de Songs ─────────────
ALTER TABLE Songs ADD COLUMN Notes TEXT;
ALTER TABLE Songs ADD COLUMN UpdatedAt TEXT;
-- Concurrencia optimista: guardar con una revisión caducada falla sin cambiar nada.
ALTER TABLE Songs ADD COLUMN Revision INTEGER NOT NULL DEFAULT 1;
-- Fondo propio (opcional). NULL = «no tocar el fondo actual de la salida».
ALTER TABLE Songs ADD COLUMN BackgroundKind TEXT CHECK (BackgroundKind IS NULL OR BackgroundKind IN ('image', 'video', 'color'));
ALTER TABLE Songs ADD COLUMN BackgroundPath TEXT;
ALTER TABLE Songs ADD COLUMN BackgroundColor TEXT;
ALTER TABLE Songs ADD COLUMN BackgroundMuted INTEGER NOT NULL DEFAULT 1;
UPDATE Songs SET UpdatedAt = CreatedAt;

-- ───────────── 3. Tablas nuevas ─────────────
CREATE TABLE SongSections (
    Id       INTEGER PRIMARY KEY,
    SongId   INTEGER NOT NULL REFERENCES Songs(Id) ON DELETE CASCADE,
    Position INTEGER NOT NULL,
    Kind     TEXT NOT NULL CHECK (Kind IN ('verse', 'chorus', 'prechorus', 'bridge', 'ending', 'intro', 'interlude', 'other')),
    Label    TEXT NOT NULL CHECK (length(trim(Label)) > 0),
    Text     TEXT NOT NULL
);
CREATE INDEX IX_SongSections_SongId_Position ON SongSections (SongId, Position);

-- Un orden por canción hoy (IsDefault = 1); los arreglos por servicio llegarán con otra columna en PlaylistItems, sin reconstruir nada.
CREATE TABLE SongArrangements (
    Id        INTEGER PRIMARY KEY,
    SongId    INTEGER NOT NULL REFERENCES Songs(Id) ON DELETE CASCADE,
    Name      TEXT NOT NULL,
    IsDefault INTEGER NOT NULL CHECK (IsDefault IN (0, 1))
);
CREATE UNIQUE INDEX IX_SongArrangements_SongId_Default ON SongArrangements (SongId) WHERE IsDefault = 1;

-- SectionId SIN «ON DELETE CASCADE»: borrar una sección que el orden usa no debe quitarla del orden en silencio (NO ACTION: se comprueba al final de la sentencia,
-- así borrar la canción entera sigue funcionando porque la cascada ya quitó los elementos).
CREATE TABLE SongArrangementItems (
    Id            INTEGER PRIMARY KEY,
    ArrangementId INTEGER NOT NULL REFERENCES SongArrangements(Id) ON DELETE CASCADE,
    Position      INTEGER NOT NULL,
    SectionId     INTEGER NOT NULL REFERENCES SongSections(Id)
);
CREATE INDEX IX_SongArrangementItems_ArrangementId_Position ON SongArrangementItems (ArrangementId, Position);
CREATE INDEX IX_SongArrangementItems_SectionId ON SongArrangementItems (SectionId);

-- Copias previas por canción (se conservan las últimas 20; las recorta el código). Snapshot = JSON con metadatos, fondo, secciones y orden.
CREATE TABLE SongRevisions (
    Id        INTEGER PRIMARY KEY,
    SongId    INTEGER NOT NULL REFERENCES Songs(Id) ON DELETE CASCADE,
    CreatedAt TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    Reason    TEXT NOT NULL CHECK (Reason IN ('save', 'import', 'restore')),
    Snapshot  TEXT NOT NULL
);
CREATE INDEX IX_SongRevisions_SongId_Id ON SongRevisions (SongId, Id);

-- ───────────── 4. Conversión de SongSlides → secciones + orden (sin pérdida y sin abortar) ─────────────
-- Cada diapositiva pasa a ser una sección de tipo «other». Etiqueta = la suya si no está vacía y es la primera con ese nombre en la canción; las demás («Sección n»,
-- «Coro (2)»…) se desambiguan. Se inserta primero SIN índice único y, tras un último arreglo de las que aún choquen, se crea el índice.
INSERT INTO SongSections (SongId, Position, Kind, Label, Text)
SELECT SongId,
       Position,
       'other',
       CASE
           WHEN Base IS NULL THEN 'Sección ' || (Position + 1)
           WHEN Rank = 1 THEN Base
           ELSE Base || ' (' || Rank || ')'
       END,
       Content
FROM (
    SELECT SongId,
           ROW_NUMBER() OVER (PARTITION BY SongId ORDER BY SequenceIndex, Id) - 1 AS Position,
           NULLIF(substr(trim(replace(replace(replace(COALESCE(Label, ''), '[', ''), ']', ''), ',', '')), 1, 50), '') AS Base,
           ROW_NUMBER() OVER (PARTITION BY SongId, lower(trim(COALESCE(Label, ''))) ORDER BY SequenceIndex, Id) AS Rank,
           Content,
           SequenceIndex,
           Id
    FROM SongSlides
)
ORDER BY SongId, Position;

-- Choques que quedaran (p. ej. una diapositiva llamada literalmente «Coro (2)»): se desambigua con el Id, que es único.
UPDATE SongSections SET Label = Label || ' #' || Id
WHERE Id IN (
    SELECT s.Id FROM SongSections s
    WHERE EXISTS (SELECT 1 FROM SongSections o WHERE o.SongId = s.SongId AND o.Id < s.Id AND lower(o.Label) = lower(s.Label))
);

CREATE UNIQUE INDEX IX_SongSections_SongId_Label ON SongSections (SongId, Label COLLATE NOCASE);

-- Todas las canciones tienen su orden predeterminado (también las que no tenían diapositivas).
INSERT INTO SongArrangements (SongId, Name, IsDefault) SELECT Id, 'Principal', 1 FROM Songs;
INSERT INTO SongArrangementItems (ArrangementId, Position, SectionId)
SELECT a.Id, s.Position, s.Id
FROM SongSections s JOIN SongArrangements a ON a.SongId = s.SongId AND a.IsDefault = 1;

-- ───────────── 5. SongsFts nuevo: título, autor y letra (cada sección UNA vez, aunque el coro se repita; sin tildes) ─────────────
CREATE VIRTUAL TABLE SongsFts USING fts5(
    Title,
    Author,
    Content,
    tokenize = 'unicode61 remove_diacritics 2'
);

INSERT INTO SongsFts (rowid, Title, Author, Content)
SELECT s.Id, s.Title, COALESCE(s.Author, ''),
       COALESCE((SELECT group_concat(Text, char(10)) FROM (SELECT Text FROM SongSections WHERE SongId = s.Id ORDER BY Position, Id)), '')
FROM Songs s;

CREATE TRIGGER Songs_ai AFTER INSERT ON Songs BEGIN
    INSERT INTO SongsFts (rowid, Title, Author, Content) VALUES (new.Id, new.Title, COALESCE(new.Author, ''), '');
END;

-- Solo cuando cambia algo que el índice usa (título, autor o el propio Id, que es el rowid del FTS).
CREATE TRIGGER Songs_au AFTER UPDATE OF Title, Author, Id ON Songs BEGIN
    DELETE FROM SongsFts WHERE rowid = old.Id;
    INSERT INTO SongsFts (rowid, Title, Author, Content) VALUES (
        new.Id, new.Title, COALESCE(new.Author, ''),
        COALESCE((SELECT group_concat(Text, char(10)) FROM (SELECT Text FROM SongSections WHERE SongId = new.Id ORDER BY Position, Id)), ''));
END;

CREATE TRIGGER Songs_ad AFTER DELETE ON Songs BEGIN
    DELETE FROM SongsFts WHERE rowid = old.Id;
END;

CREATE TRIGGER SongSections_ai AFTER INSERT ON SongSections BEGIN
    UPDATE SongsFts SET Content = COALESCE((SELECT group_concat(Text, char(10)) FROM (SELECT Text FROM SongSections WHERE SongId = new.SongId ORDER BY Position, Id)), '')
    WHERE rowid = new.SongId;
END;

CREATE TRIGGER SongSections_au AFTER UPDATE OF SongId, Position, Text, Id ON SongSections BEGIN
    UPDATE SongsFts SET Content = COALESCE((SELECT group_concat(Text, char(10)) FROM (SELECT Text FROM SongSections WHERE SongId = old.SongId ORDER BY Position, Id)), '')
    WHERE rowid = old.SongId;
    UPDATE SongsFts SET Content = COALESCE((SELECT group_concat(Text, char(10)) FROM (SELECT Text FROM SongSections WHERE SongId = new.SongId ORDER BY Position, Id)), '')
    WHERE rowid = new.SongId;
END;

CREATE TRIGGER SongSections_ad AFTER DELETE ON SongSections BEGIN
    UPDATE SongsFts SET Content = COALESCE((SELECT group_concat(Text, char(10)) FROM (SELECT Text FROM SongSections WHERE SongId = old.SongId ORDER BY Position, Id)), '')
    WHERE rowid = old.SongId;
END;
