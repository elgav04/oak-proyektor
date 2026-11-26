-- Migración 13: biblioteca local de medios (docs/DISENO_MEDIOS.md). Las migraciones publicadas (1 a 12) NO se editan; esta solo AÑADE tablas nuevas y no toca ninguna existente.
--
-- Los archivos se COPIAN a <datos>\media\files\<2 primeros del hash>\<sha256><ext>: el nombre en disco es el hash (siempre ASCII, sin espacios ni caracteres raros) y el nombre que ve
-- el operador es solo un dato de esta tabla. Sha256 es UNICO: el mismo contenido no se importa dos veces. Los respaldos diarios cubren esta base, NO los archivos de media\.
-- Status: PENDING (copiado, sin verificar en el reproductor), OK, UNSUPPORTED (no se puede proyectar; StatusCode dice por que) o MISSING (el archivo desaparecio del disco).
-- StatusCode tambien guarda un AVISO de un elemento OK (p. ej. media.videoCodecWarning para HEVC).

CREATE TABLE MediaFolders (
    Id        INTEGER PRIMARY KEY,
    Name      TEXT NOT NULL CHECK (length(Name) BETWEEN 1 AND 80),
    NameKey   TEXT NOT NULL,
    CreatedAt TEXT NOT NULL
);
CREATE UNIQUE INDEX IX_MediaFolders_NameKey ON MediaFolders (NameKey);

CREATE TABLE MediaItems (
    Id               INTEGER PRIMARY KEY,
    Kind             TEXT NOT NULL CHECK (Kind IN ('VIDEO', 'AUDIO', 'IMAGE')),
    Name             TEXT NOT NULL CHECK (length(Name) BETWEEN 1 AND 120),
    FolderId         INTEGER NULL REFERENCES MediaFolders (Id) ON DELETE SET NULL,
    OriginalFileName TEXT NOT NULL,
    Ext              TEXT NOT NULL,
    Bytes            INTEGER NOT NULL CHECK (Bytes > 0),
    Sha256           TEXT NOT NULL CHECK (length(Sha256) = 64),
    DurationMs       INTEGER NULL,
    Width            INTEGER NULL,
    Height           INTEGER NULL,
    Container        TEXT NULL,
    VideoCodec       TEXT NULL,
    AudioCodec       TEXT NULL,
    HasAudio         INTEGER NOT NULL DEFAULT 0 CHECK (HasAudio IN (0, 1)),
    Status           TEXT NOT NULL DEFAULT 'PENDING' CHECK (Status IN ('PENDING', 'OK', 'UNSUPPORTED', 'MISSING')),
    StatusCode       TEXT NULL,
    HasThumbnail     INTEGER NOT NULL DEFAULT 0 CHECK (HasThumbnail IN (0, 1)),
    Revision         INTEGER NOT NULL DEFAULT 1,
    CreatedAt        TEXT NOT NULL,
    UpdatedAt        TEXT NOT NULL
);
CREATE UNIQUE INDEX IX_MediaItems_Sha256 ON MediaItems (Sha256);
CREATE INDEX IX_MediaItems_Folder_Name ON MediaItems (FolderId, Name COLLATE NOCASE);
CREATE INDEX IX_MediaItems_Kind ON MediaItems (Kind);
