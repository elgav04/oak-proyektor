-- Migración 7: historial de citas (pasajes que el operador consultó o proyectó).
--
-- Se guarda por REFERENCIA CANÓNICA, igual que los pasajes de los servicios (migración 4): número de libro (1 a 66), capítulo, versículos y la
-- abreviatura de la Biblia como TEXTO, sin clave foránea. Borrar o reimportar una Biblia no toca el historial. El límite de entradas lo aplica el
-- código al insertar (configurable en settings.json); la tabla no lo impone.

CREATE TABLE CitationHistory (
    Id                INTEGER PRIMARY KEY,
    BibleAbbreviation TEXT NOT NULL CHECK (length(trim(BibleAbbreviation)) > 0),
    BookNumber        INTEGER NOT NULL CHECK (BookNumber BETWEEN 1 AND 66),
    Chapter           INTEGER NOT NULL CHECK (Chapter >= 1),
    VerseStart        INTEGER CHECK (VerseStart IS NULL OR VerseStart >= 1),
    VerseEnd          INTEGER,
    -- Referencia como se mostró («Jn 3:16»): solo la referencia, nunca texto bíblico.
    Label             TEXT NOT NULL,
    CitedAt           TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    CONSTRAINT CK_CitationHistory_Range CHECK (VerseEnd IS NULL OR (VerseStart IS NOT NULL AND VerseEnd >= VerseStart))
);
CREATE INDEX IX_CitationHistory_CitedAt ON CitationHistory (CitedAt DESC, Id DESC);
