-- Migración 8: el historial de citas guarda la referencia COMPLETA (todos los tramos).
--
-- La migración 7 solo guardaba el primer tramo en CitationHistory (capítulo, VerseStart, VerseEnd): «Mt 5:3,5,7» quedaba como «Mt 5:3» y «Gn 1-2» como «Gn 1».
-- El texto canónico ya está entero en Label; ahora cada tramo tiene su fila. Como el resto del historial, es una referencia CANÓNICA: sin clave foránea a ninguna Biblia
-- y sin texto bíblico.
--
-- NO se toca ninguna fila existente de CitationHistory. Las entradas anteriores a esta migración no tienen filas aquí; el código las resuelve leyendo su Label
-- (que es la forma canónica y se interpreta con ReferenceParser) y, si no se pudiera, con las columnas de la propia fila.

CREATE TABLE CitationHistorySegments (
    Id           INTEGER PRIMARY KEY,
    HistoryId    INTEGER NOT NULL REFERENCES CitationHistory (Id) ON DELETE CASCADE,
    Position     INTEGER NOT NULL CHECK (Position >= 0),
    StartChapter INTEGER NOT NULL CHECK (StartChapter >= 1),
    StartVerse   INTEGER CHECK (StartVerse IS NULL OR StartVerse >= 1),
    EndChapter   INTEGER NOT NULL CHECK (EndChapter >= StartChapter),
    EndVerse     INTEGER CHECK (EndVerse IS NULL OR EndVerse >= 1),
    -- Capítulos completos: ambos versículos nulos; si no, ambos presentes.
    CONSTRAINT CK_CitationHistorySegments_Verses CHECK ((StartVerse IS NULL) = (EndVerse IS NULL)),
    CONSTRAINT CK_CitationHistorySegments_Order CHECK (EndChapter > StartChapter OR StartVerse IS NULL OR EndVerse >= StartVerse),
    CONSTRAINT UQ_CitationHistorySegments UNIQUE (HistoryId, Position)
);
