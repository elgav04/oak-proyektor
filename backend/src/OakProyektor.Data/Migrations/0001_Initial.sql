-- Migración 1: esquema completo inicial.
-- Fechas: TEXT en ISO-8601 UTC. Ids: INTEGER PRIMARY KEY (alias de rowid, necesario para FTS5 de contenido externo).

CREATE TABLE Bibles (
    Id           INTEGER PRIMARY KEY,
    Abbreviation TEXT NOT NULL UNIQUE,
    Name         TEXT NOT NULL,
    Language     TEXT NOT NULL
);

CREATE TABLE Books (
    Id         INTEGER PRIMARY KEY,
    BibleId    INTEGER NOT NULL REFERENCES Bibles(Id) ON DELETE CASCADE,
    BookNumber INTEGER NOT NULL,
    Name       TEXT NOT NULL,
    Testament  TEXT NOT NULL
);
CREATE UNIQUE INDEX IX_Books_BibleId_BookNumber ON Books (BibleId, BookNumber);

CREATE TABLE Verses (
    Id          INTEGER PRIMARY KEY,
    BookId      INTEGER NOT NULL REFERENCES Books(Id) ON DELETE CASCADE,
    Chapter     INTEGER NOT NULL,
    VerseNumber INTEGER NOT NULL,
    Text        TEXT NOT NULL
);
CREATE INDEX IX_Verses_BookId_Chapter_VerseNumber ON Verses (BookId, Chapter, VerseNumber);

CREATE TABLE Songs (
    Id         INTEGER PRIMARY KEY,
    Title      TEXT NOT NULL,
    Author     TEXT,
    Copyright  TEXT,
    CcliNumber TEXT,
    CreatedAt  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);
CREATE INDEX IX_Songs_Title ON Songs (Title COLLATE NOCASE);

CREATE TABLE SongSlides (
    Id            INTEGER PRIMARY KEY,
    SongId        INTEGER NOT NULL REFERENCES Songs(Id) ON DELETE CASCADE,
    SequenceIndex INTEGER NOT NULL,
    Label         TEXT,
    Content       TEXT NOT NULL
);
CREATE INDEX IX_SongSlides_SongId_SequenceIndex ON SongSlides (SongId, SequenceIndex);

CREATE TABLE Playlists (
    Id          INTEGER PRIMARY KEY,
    Name        TEXT NOT NULL,
    ServiceDate TEXT,
    ItemsJson   TEXT NOT NULL DEFAULT '[]'
);
CREATE INDEX IX_Playlists_ServiceDate ON Playlists (ServiceDate);

CREATE TABLE Presentations (
    Id         INTEGER PRIMARY KEY,
    Title      TEXT NOT NULL,
    SourceType TEXT NOT NULL CHECK (SourceType IN ('CUSTOM', 'PPTX')),
    CreatedAt  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE TABLE PresentationSlides (
    Id             INTEGER PRIMARY KEY,
    PresentationId INTEGER NOT NULL REFERENCES Presentations(Id) ON DELETE CASCADE,
    SlideIndex     INTEGER NOT NULL,
    ImagePath      TEXT,
    ContentJson    TEXT NOT NULL DEFAULT '{}'
);
CREATE INDEX IX_PresentationSlides_PresentationId_SlideIndex ON PresentationSlides (PresentationId, SlideIndex);

-- Búsqueda de versículos: FTS5 con contenido externo (Verses). Sin acentos: "Jesús" coincide con "jesus".
CREATE VIRTUAL TABLE VersesFts USING fts5(
    Text,
    content = 'Verses',
    content_rowid = 'Id',
    tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TRIGGER Verses_ai AFTER INSERT ON Verses BEGIN
    INSERT INTO VersesFts (rowid, Text) VALUES (new.Id, new.Text);
END;

CREATE TRIGGER Verses_ad AFTER DELETE ON Verses BEGIN
    INSERT INTO VersesFts (VersesFts, rowid, Text) VALUES ('delete', old.Id, old.Text);
END;

CREATE TRIGGER Verses_au AFTER UPDATE ON Verses BEGIN
    INSERT INTO VersesFts (VersesFts, rowid, Text) VALUES ('delete', old.Id, old.Text);
    INSERT INTO VersesFts (rowid, Text) VALUES (new.Id, new.Text);
END;

-- Búsqueda de canciones por título y letra. Tabla FTS5 propia (no de contenido externo) porque
-- la letra vive en otra tabla (SongSlides): Content = letra concatenada de todas las diapositivas.
CREATE VIRTUAL TABLE SongsFts USING fts5(
    Title,
    Content,
    tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TRIGGER Songs_ai AFTER INSERT ON Songs BEGIN
    INSERT INTO SongsFts (rowid, Title, Content) VALUES (new.Id, new.Title, '');
END;

CREATE TRIGGER Songs_au AFTER UPDATE OF Title ON Songs BEGIN
    UPDATE SongsFts SET Title = new.Title WHERE rowid = new.Id;
END;

CREATE TRIGGER Songs_ad AFTER DELETE ON Songs BEGIN
    DELETE FROM SongsFts WHERE rowid = old.Id;
END;

CREATE TRIGGER SongSlides_ai AFTER INSERT ON SongSlides BEGIN
    UPDATE SongsFts SET Content = (
        SELECT COALESCE(group_concat(Content, char(10)), '')
        FROM (SELECT Content FROM SongSlides WHERE SongId = new.SongId ORDER BY SequenceIndex, Id)
    ) WHERE rowid = new.SongId;
END;

CREATE TRIGGER SongSlides_au AFTER UPDATE ON SongSlides BEGIN
    UPDATE SongsFts SET Content = (
        SELECT COALESCE(group_concat(Content, char(10)), '')
        FROM (SELECT Content FROM SongSlides WHERE SongId = old.SongId ORDER BY SequenceIndex, Id)
    ) WHERE rowid = old.SongId;
    UPDATE SongsFts SET Content = (
        SELECT COALESCE(group_concat(Content, char(10)), '')
        FROM (SELECT Content FROM SongSlides WHERE SongId = new.SongId ORDER BY SequenceIndex, Id)
    ) WHERE rowid = new.SongId;
END;

CREATE TRIGGER SongSlides_ad AFTER DELETE ON SongSlides BEGIN
    UPDATE SongsFts SET Content = (
        SELECT COALESCE(group_concat(Content, char(10)), '')
        FROM (SELECT Content FROM SongSlides WHERE SongId = old.SongId ORDER BY SequenceIndex, Id)
    ) WHERE rowid = old.SongId;
END;

