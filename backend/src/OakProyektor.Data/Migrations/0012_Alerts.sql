-- Migración 12: alertas rápidas (docs/DISENO_ALERTAS.md). Las migraciones publicadas (1 a 11) NO se editan; esta solo AÑADE una tabla nueva y no toca ninguna existente.
--
-- Alerts guarda los avisos que el operador fijó (Pinned = 1: perennes, explícitos) y los recientes (Pinned = 0: se anotan solos al mostrarlos, con límite).
-- El estado de la alerta que está en pantalla NO se guarda aquí ni en ninguna parte: reiniciar la aplicación nunca revive un aviso.
-- TextKey es el texto normalizado y en minúsculas: «Aviso  1» y «aviso 1» son el mismo aviso (único). El texto es dato del usuario y vive SOLO en esta base local.

CREATE TABLE Alerts (
    Id         INTEGER PRIMARY KEY,
    Text       TEXT NOT NULL CHECK (length(Text) BETWEEN 1 AND 300),
    TextKey    TEXT NOT NULL,
    Pinned     INTEGER NOT NULL DEFAULT 0 CHECK (Pinned IN (0, 1)),
    UseCount   INTEGER NOT NULL DEFAULT 0 CHECK (UseCount >= 0),
    LastUsedAt TEXT NULL,
    CreatedAt  TEXT NOT NULL,
    UpdatedAt  TEXT NOT NULL
);

CREATE UNIQUE INDEX IX_Alerts_TextKey ON Alerts (TextKey);
CREATE INDEX IX_Alerts_Pinned_LastUsedAt ON Alerts (Pinned, LastUsedAt);
