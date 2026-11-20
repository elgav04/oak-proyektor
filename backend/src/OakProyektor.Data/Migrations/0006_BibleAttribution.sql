-- Migración 6: aviso de derechos / atribución por Biblia y perfil de numeración.
--
-- Attribution: texto de atribución o aviso de derechos de la Biblia, EDITABLE por el operador. Vacío por defecto: la aplicación NO trae ningún
-- texto de aviso (el texto de una Biblia y su licencia dependen de cada una; lo escribe quien la instala).
-- NumberingProfile: id del perfil de numeración aplicado al importar (uniones de versículos; ver NumberingProfiles en Core). NULL = sin perfil.
-- Vive en la Biblia para que una reimportación sepa con qué perfil se importó y avise si cambia.

ALTER TABLE Bibles ADD COLUMN Attribution TEXT NOT NULL DEFAULT '';
ALTER TABLE Bibles ADD COLUMN NumberingProfile TEXT;
