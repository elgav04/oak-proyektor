using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Core.Alerts;
using OakProyektor.Core.Output;
using OakProyektor.Data;
using OakProyektor.Data.Alerts;
using OakProyektor.Tests.Data;
using OakProyektor.Tests.Ipc;

namespace OakProyektor.Tests.Alerts;

/// <summary>Migración 12, repositorio y anotador de alertas sobre una BD real. Textos sintéticos; los registros no llevan nunca el texto.</summary>
public sealed class AlertDataTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly FakeClock _clock = new();
    private readonly ListLogger<AlertRepository> _log = new();
    private AlertRepository _repo = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _repo = new AlertRepository(_db.Connections, new AlwaysReady(), _log, new AlertRepositoryOptions { WriteTimeoutSeconds = 1 }, _clock);
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private async Task<long> CountAsync(string where = "1=1")
    {
        await using var c = await _db.OpenAsync();
        return await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM Alerts WHERE {where}");
    }

    [Fact]
    public async Task The_migration_adds_only_the_alerts_table()
    {
        await using var c = await _db.OpenAsync();
        Assert.Equal((long)OakProyektor.Data.DatabaseInitializer.LatestSchemaVersion, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Alerts'"));
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Alerts"));
        Assert.Empty(await c.QueryAsync("PRAGMA foreign_key_check"));
        Assert.Equal("ok", await c.ExecuteScalarAsync<string>("PRAGMA integrity_check"));
    }

    [Fact]
    public async Task Saving_creates_and_lists_pinned_alerts_in_stable_alphabetical_order()
    {
        await _repo.SaveAsync(null, "Zeta aviso de prueba", true, CancellationToken.None);
        await _repo.SaveAsync(null, "alfa aviso de prueba", true, CancellationToken.None);
        await _repo.SaveAsync(null, "Beta aviso de prueba", true, CancellationToken.None);

        var list = await _repo.ListAsync(CancellationToken.None);
        Assert.Equal(["alfa aviso de prueba", "Beta aviso de prueba", "Zeta aviso de prueba"], list.Pinned.Select(a => a.Text));
        Assert.Empty(list.Recent);
    }

    [Fact]
    public async Task The_same_text_ignoring_case_spaces_and_unicode_form_is_the_same_alert()
    {
        var a = await _repo.SaveAsync(null, "Aviso   de PRUEBA", false, CancellationToken.None);
        var b = await _repo.SaveAsync(null, "  aviso de prueba ", true, CancellationToken.None);
        var c = await _repo.SaveAsync(null, "Café de prueba", false, CancellationToken.None);
        var d = await _repo.SaveAsync(null, "Café de prueba", false, CancellationToken.None);

        Assert.Equal(a.Id, b.Id);
        Assert.True(b.Pinned);
        Assert.Equal(c.Id, d.Id);
        Assert.Equal(2, await CountAsync());
    }

    [Fact]
    public async Task Editing_changes_the_text_and_a_collision_with_another_alert_is_a_duplicate_error()
    {
        var a = await _repo.SaveAsync(null, "Aviso de prueba uno", true, CancellationToken.None);
        var b = await _repo.SaveAsync(null, "Aviso de prueba dos", true, CancellationToken.None);

        var edited = await _repo.SaveAsync(a.Id, "Aviso de prueba uno, corregido", true, CancellationToken.None);
        Assert.Equal((a.Id, "Aviso de prueba uno, corregido"), (edited.Id, edited.Text));

        var ex = await Assert.ThrowsAsync<AlertException>(() => _repo.SaveAsync(b.Id, "aviso DE prueba uno, corregido", true, CancellationToken.None));
        Assert.Equal(AlertErrorCodes.Duplicate, ex.Code);
        Assert.Equal("Aviso de prueba dos", (await _repo.ListAsync(CancellationToken.None)).Pinned.Single(p => p.Id == b.Id).Text);   // no cambió nada

        var missing = await Assert.ThrowsAsync<AlertException>(() => _repo.SaveAsync(999, "Aviso de prueba tres", false, CancellationToken.None));
        Assert.Equal(AlertErrorCodes.NotFound, missing.Code);
    }

    [Fact]
    public async Task An_invalid_text_is_rejected_before_touching_the_database()
    {
        foreach (var bad in new[] { "", "   ", new string('x', 301) })
        {
            var ex = await Assert.ThrowsAsync<AlertException>(() => _repo.SaveAsync(null, bad, false, CancellationToken.None));
            Assert.Equal(AlertErrorCodes.InvalidContent, ex.Code);
        }
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task At_most_30_alerts_can_be_pinned_and_unpinning_makes_room()
    {
        var saved = new List<SavedAlert>();
        for (var i = 1; i <= AlertLimits.MaxPinned; i++) saved.Add(await _repo.SaveAsync(null, $"Aviso fijo de prueba {i}", true, CancellationToken.None));

        var full = await Assert.ThrowsAsync<AlertException>(() => _repo.SaveAsync(null, "Aviso fijo de prueba 31", true, CancellationToken.None));
        Assert.Equal(AlertErrorCodes.Full, full.Code);
        Assert.Equal(AlertLimits.MaxPinned, await CountAsync("Pinned = 1"));

        // Re-guardar una YA fijada no cuenta contra el límite, y una sin fijar sí se puede guardar.
        await _repo.SaveAsync(saved[0].Id, "Aviso fijo de prueba 1 bis", true, CancellationToken.None);
        await _repo.SaveAsync(null, "Aviso suelto de prueba", false, CancellationToken.None);

        await _repo.SaveAsync(saved[1].Id, saved[1].Text, false, CancellationToken.None);   // desfijar
        Assert.NotNull(await _repo.SaveAsync(null, "Aviso fijo de prueba 31", true, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_is_idempotent()
    {
        var a = await _repo.SaveAsync(null, "Aviso de prueba", true, CancellationToken.None);
        Assert.True(await _repo.DeleteAsync(a.Id, CancellationToken.None));
        Assert.False(await _repo.DeleteAsync(a.Id, CancellationToken.None));
        Assert.False(await _repo.DeleteAsync(12345, CancellationToken.None));
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task Recording_a_use_creates_a_recent_alert_counts_repeats_and_keeps_pinned_ones_pinned()
    {
        await using (var c = await _db.OpenAsync())
        {
            await AlertStore.RecordUseAsync(c, "Aviso de prueba", _clock.GetUtcNow(), 1, CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(5));
            await AlertStore.RecordUseAsync(c, "AVISO DE PRUEBA", _clock.GetUtcNow(), 1, CancellationToken.None);   // el mismo aviso: no se duplica ni cambia su texto
        }
        var list = await _repo.ListAsync(CancellationToken.None);
        var recent = Assert.Single(list.Recent);
        Assert.Equal(("Aviso de prueba", 2, false), (recent.Text, recent.UseCount, recent.Pinned));

        await _repo.SaveAsync(recent.Id, recent.Text, true, CancellationToken.None);
        await using (var c = await _db.OpenAsync()) await AlertStore.RecordUseAsync(c, "aviso de prueba", _clock.GetUtcNow(), 1, CancellationToken.None);
        list = await _repo.ListAsync(CancellationToken.None);
        Assert.Empty(list.Recent);
        Assert.Equal((3, true), (list.Pinned.Single().UseCount, list.Pinned.Single().Pinned));
    }

    [Fact]
    public async Task Recent_alerts_are_the_last_10_used_within_14_days_newest_first_and_older_ones_are_not_shown()
    {
        await using (var c = await _db.OpenAsync())
        {
            await AlertStore.RecordUseAsync(c, "Aviso viejo de prueba", _clock.GetUtcNow(), 1, CancellationToken.None);
            _clock.Advance(TimeSpan.FromDays(15));
            for (var i = 1; i <= 12; i++)
            {
                await AlertStore.RecordUseAsync(c, $"Aviso reciente de prueba {i}", _clock.GetUtcNow(), 1, CancellationToken.None);
                _clock.Advance(TimeSpan.FromMinutes(1));
            }
        }
        var recent = (await _repo.ListAsync(CancellationToken.None)).Recent;
        Assert.Equal(AlertLimits.RecentShown, recent.Count);
        Assert.Equal("Aviso reciente de prueba 12", recent[0].Text);
        Assert.Equal("Aviso reciente de prueba 3", recent[^1].Text);
        Assert.DoesNotContain(recent, r => r.Text.Contains("viejo"));
        Assert.Equal(13, await CountAsync());   // el viejo sigue guardado (la poda es por cantidad), solo deja de mostrarse
    }

    [Fact]
    public async Task Non_pinned_alerts_are_pruned_to_50_oldest_first_and_pinned_ones_are_never_pruned()
    {
        await _repo.SaveAsync(null, "Aviso fijo de prueba", true, CancellationToken.None);
        await using (var c = await _db.OpenAsync())
            for (var i = 1; i <= 60; i++)
            {
                await AlertStore.RecordUseAsync(c, $"Aviso suelto de prueba {i}", _clock.GetUtcNow(), 1, CancellationToken.None);
                _clock.Advance(TimeSpan.FromSeconds(1));
            }
        Assert.Equal(AlertLimits.MaxRecentStored, await CountAsync("Pinned = 0"));
        Assert.Equal(1, await CountAsync("Pinned = 1"));
        Assert.Equal(0, await CountAsync("TextKey = 'aviso suelto de prueba 1'"));    // las más antiguas se fueron
        Assert.Equal(1, await CountAsync("TextKey = 'aviso suelto de prueba 60'"));
    }

    [Fact]
    public async Task The_log_never_contains_the_text_of_an_alert()
    {
        const string secret = "AVISO CONFIDENCIAL DE PRUEBA";
        var a = await _repo.SaveAsync(null, secret, true, CancellationToken.None);
        await _repo.SaveAsync(a.Id, secret + " editado", true, CancellationToken.None);
        await _repo.DeleteAsync(a.Id, CancellationToken.None);
        await Assert.ThrowsAsync<AlertException>(() => _repo.SaveAsync(null, new string('x', 400), true, CancellationToken.None));

        Assert.NotEmpty(_log.Entries);
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("CONFIDENCIAL", StringComparison.OrdinalIgnoreCase) || (e.Exception?.Message.Contains("CONFIDENCIAL", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    [Fact]
    public async Task The_background_recorder_writes_in_the_background_counts_uses_and_never_blocks_the_caller()
    {
        var recorderLog = new ListLogger<BackgroundAlertRecorder>();
        using var recorder = new BackgroundAlertRecorder(_db.Connections, new AlwaysReady(), recorderLog, _clock);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) recorder.Record("Aviso de prueba");
        recorder.Record("Otro aviso de prueba");
        Assert.True(clock.ElapsedMilliseconds < 200, $"Record tardó {clock.ElapsedMilliseconds} ms: debe ser solo encolar");

        Assert.True(await recorder.WaitIdleAsync(TimeSpan.FromSeconds(10)));
        await using var c = await _db.OpenAsync();
        Assert.Equal(20L, await c.ExecuteScalarAsync<long>("SELECT UseCount FROM Alerts WHERE TextKey = 'aviso de prueba'"));
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT UseCount FROM Alerts WHERE TextKey = 'otro aviso de prueba'"));
        Assert.Equal(0, recorder.Discarded);
    }

    [Fact]
    public async Task A_write_lock_held_by_another_writer_makes_the_recorder_discard_without_text_in_the_log_and_the_repository_report_busy()
    {
        var recorderLog = new ListLogger<BackgroundAlertRecorder>();
        using var recorder = new BackgroundAlertRecorder(_db.Connections, new AlwaysReady(), recorderLog, _clock) { WriteTimeout = TimeSpan.FromSeconds(1) };

        await using var holder = await _db.OpenAsync();
        await holder.ExecuteAsync("BEGIN IMMEDIATE");   // otro escritor (p. ej. una importación) retiene el bloqueo
        try
        {
            recorder.Record("AVISO CONFIDENCIAL DE PRUEBA");
            Assert.True(await recorder.WaitIdleAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(1, recorder.Discarded);
            Assert.DoesNotContain(recorderLog.Entries, e => e.Message.Contains("CONFIDENCIAL", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(recorderLog.Entries, e => e.Level == LogLevel.Warning);

            var busy = await Assert.ThrowsAsync<AlertException>(() => _repo.SaveAsync(null, "Aviso de prueba", false, CancellationToken.None));
            Assert.Equal(AlertErrorCodes.Busy, busy.Code);
        }
        finally { await holder.ExecuteAsync("ROLLBACK"); }
        Assert.Equal(0, await CountAsync());   // no cambió nada
    }
}
