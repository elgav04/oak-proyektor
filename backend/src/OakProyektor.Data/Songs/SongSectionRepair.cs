using System.Globalization;
using Microsoft.Data.Sqlite;
using OakProyektor.Core.Songs;

namespace OakProyektor.Data.Songs;

/// <summary>
/// Paso de código de la migración 10: aplica <see cref="SongLabelRepair"/> a las secciones de cada canción, dentro de la transacción de la migración. Solo toca lo que cambia;
/// sobre secciones ya válidas no hace nada. El resumen lleva únicamente conteos (nunca letra, títulos ni etiquetas).
/// </summary>
internal static class SongSectionRepair
{
    private sealed record Row(long Id, long SongId, int Position, string Kind, string Label, string Text);

    public static async Task<string?> RunAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        var rows = new List<Row>();
        await using (var read = c.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT Id, SongId, Position, Kind, Label, Text FROM SongSections ORDER BY SongId, Position, Id";
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(new Row(reader.GetInt64(0), reader.GetInt64(1), (int)reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }

        int removed = 0, relabeled = 0, retexted = 0, rekinded = 0;
        foreach (var song in rows.GroupBy(r => r.SongId))
        {
            var byId = song.ToDictionary(r => r.Id);
            var repaired = SongLabelRepair.Repair([.. song.Select(r => new RepairSectionInput(r.Id, r.Position, r.Label, r.Text))]);

            foreach (var gone in repaired.Where(r => r.Remove))
            {
                await ExecAsync(c, tx, "DELETE FROM SongArrangementItems WHERE SectionId = $id", ct, ("$id", gone.Id)).ConfigureAwait(false);
                await ExecAsync(c, tx, "DELETE FROM SongSections WHERE Id = $id", ct, ("$id", gone.Id)).ConfigureAwait(false);
                removed++;
            }

            var changes = repaired.Where(r => !r.Remove && (r.Label != byId[r.Id].Label || r.Text != byId[r.Id].Text || r.Kind != byId[r.Id].Kind)).ToList();
            // Etiquetas provisionales (con un carácter de control, que ninguna etiqueta válida lleva): intercambiar nombres no choca con el índice único.
            foreach (var change in changes.Where(r => r.Label != byId[r.Id].Label))
                await ExecAsync(c, tx, "UPDATE SongSections SET Label = char(1) || Id WHERE Id = $id", ct, ("$id", change.Id)).ConfigureAwait(false);
            foreach (var change in changes)
            {
                var before = byId[change.Id];
                await ExecAsync(c, tx, "UPDATE SongSections SET Label = $label, Kind = $kind, Text = $text WHERE Id = $id", ct,
                    ("$label", change.Label), ("$kind", change.Kind), ("$text", change.Text), ("$id", change.Id)).ConfigureAwait(false);
                if (change.Label != before.Label) relabeled++;
                if (change.Text != before.Text) retexted++;
                if (change.Kind != before.Kind) rekinded++;
            }
        }
        return string.Create(CultureInfo.InvariantCulture, $"secciones de canciones reparadas: {removed} vacías quitadas, {relabeled} etiquetas, {retexted} textos, {rekinded} tipos (de {rows.Count})");
    }

    private static async Task ExecAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}