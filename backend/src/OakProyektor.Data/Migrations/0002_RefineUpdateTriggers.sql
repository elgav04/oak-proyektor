-- Migración 2 (hallazgo M5): los triggers de ACTUALIZACIÓN solo se disparan cuando cambia algo que afecta al índice FTS.
-- Antes Verses_au y SongSlides_au se disparaban con cualquier UPDATE (p. ej. cambiar solo Chapter o Label), y Songs_au no
-- cubría un cambio de Id (dejaba la fila de SongsFts con el rowid viejo). No se edita la migración 1: ya está publicada.

DROP TRIGGER Verses_au;
CREATE TRIGGER Verses_au AFTER UPDATE OF Text, Id ON Verses BEGIN
    INSERT INTO VersesFts (VersesFts, rowid, Text) VALUES ('delete', old.Id, old.Text);
    INSERT INTO VersesFts (rowid, Text) VALUES (new.Id, new.Text);
END;

DROP TRIGGER Songs_au;
CREATE TRIGGER Songs_au AFTER UPDATE OF Title, Id ON Songs BEGIN
    DELETE FROM SongsFts WHERE rowid = old.Id;
    INSERT INTO SongsFts (rowid, Title, Content) VALUES (
        new.Id,
        new.Title,
        (SELECT COALESCE(group_concat(Content, char(10)), '')
         FROM (SELECT Content FROM SongSlides WHERE SongId = new.Id ORDER BY SequenceIndex, Id)));
END;

DROP TRIGGER SongSlides_au;
CREATE TRIGGER SongSlides_au AFTER UPDATE OF SongId, SequenceIndex, Content, Id ON SongSlides BEGIN
    UPDATE SongsFts SET Content = (
        SELECT COALESCE(group_concat(Content, char(10)), '')
        FROM (SELECT Content FROM SongSlides WHERE SongId = old.SongId ORDER BY SequenceIndex, Id)
    ) WHERE rowid = old.SongId;
    UPDATE SongsFts SET Content = (
        SELECT COALESCE(group_concat(Content, char(10)), '')
        FROM (SELECT Content FROM SongSlides WHERE SongId = new.SongId ORDER BY SequenceIndex, Id)
    ) WHERE rowid = new.SongId;
END;