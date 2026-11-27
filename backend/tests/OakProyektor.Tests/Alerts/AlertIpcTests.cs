using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Handlers;
using OakProyektor.Core.Alerts;
using OakProyektor.Core.Output;
using OakProyektor.Data;
using OakProyektor.Data.Alerts;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using OakProyektor.Tests.App;
using OakProyektor.Tests.Data;
using OakProyektor.Tests.Ipc;

namespace OakProyektor.Tests.Alerts;

/// <summary>Los mensajes alert.* de extremo a extremo (despachador + handlers reales + BD real): validación, errores sin texto, eventos solo con ids, carriles y orden de llegada.</summary>
public sealed class AlertIpcTests : IAsyncLifetime
{
    private sealed class FakeRecorder : IAlertRecorder
    {
        public List<string> Texts { get; } = [];
        public void Record(string normalizedText) => Texts.Add(normalizedText);
    }

    private readonly TestDatabase _db = new();
    private readonly Outbox _outbox = new(NullLogger<Outbox>.Instance);
    private readonly InMemorySettings _settings = new();
    private readonly FakeOutputController _output = new();
    private readonly FakeRecorder _recorder = new();
    private readonly ListLogger<IpcDispatcher> _dispatcherLog = new();
    private IpcDispatcher _dispatcher = null!;
    private AlertShowHandler _show = null!;
    private AlertHideHandler _hide = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var alerts = new AlertRepository(_db.Connections, new AlwaysReady(), null, new AlertRepositoryOptions { WriteTimeoutSeconds = 1 });
        _show = new AlertShowHandler(_output, _recorder, _settings);
        _hide = new AlertHideHandler(_output);
        _dispatcher = new IpcDispatcher(
            [_show, _hide, new AlertGetSettingsHandler(_settings), new AlertSetSettingsHandler(_settings), new AlertListHandler(alerts), new AlertSaveHandler(alerts, _outbox), new AlertDeleteHandler(alerts, _outbox)],
            _dispatcherLog);
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private static string Msg(string kind, string type, object payload, string id) =>
        $$"""{"v":{{IpcCatalog.ProtocolVersion}},"id":"{{id}}","kind":"{{kind}}","type":"{{type}}","payload":{{JsonSerializer.Serialize(payload, IpcSerializer.Options)}}}""";

    private async Task<IpcEnvelope> QueryAsync(string type, object payload) => (await _dispatcher.DispatchAsync(Msg(IpcKind.Query, type, payload, "q1"), CancellationToken.None))!;
    private Task<IpcEnvelope?> CommandAsync(string type, object payload, string id = "c1") => _dispatcher.DispatchAsync(Msg(IpcKind.Command, type, payload, id), CancellationToken.None);

    private async Task<List<IpcEnvelope>> PublishedAsync()
    {
        var list = new List<IpcEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var (_, json) in _outbox.ReadAllAsync(cts.Token))
            {
                Assert.True(IpcSerializer.TryParse(json, out var envelope, out var error), error);
                list.Add(envelope!);
            }
        }
        catch (OperationCanceledException) { /* se vació la cola */ }
        return list;
    }

    private static IpcEnvelope ShowEnvelope(string text, AlertMode mode = AlertMode.Band, int seconds = 30, long order = 0) =>
        IpcEnvelope.Create(IpcKind.Command, IpcTypes.AlertShow, new AlertShowPayload(text, mode, seconds)) with { Order = order };

    [Fact]
    public async Task Show_normalizes_the_text_passes_the_arrival_number_and_records_the_use_in_the_background()
    {
        await _show.HandleAsync(ShowEnvelope("  AVISO  DE\tPRUEBA 1 ", AlertMode.Ticker, 60, order: 42), CancellationToken.None);

        var shown = Assert.Single(_output.Alerts);
        Assert.Equal(("AVISO DE PRUEBA 1", AlertMode.Ticker, 60, 42L), shown);
        Assert.Equal(["AVISO DE PRUEBA 1"], _recorder.Texts);
    }

    [Fact]
    public async Task A_show_the_controller_ignored_is_not_recorded()
    {
        _output.AlertApplied = false;   // el operador la quitó después de pedirla
        await _show.HandleAsync(ShowEnvelope("AVISO DE PRUEBA 1"), CancellationToken.None);
        Assert.Empty(_recorder.Texts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    public async Task An_empty_alert_fails_with_a_code_and_nothing_reaches_the_output(string text)
    {
        var reply = await CommandAsync(IpcTypes.AlertShow, new AlertShowPayload(text, AlertMode.Band, 30));
        var failed = Assert.IsType<CommandFailedPayload>(reply!.GetPayload<CommandFailedPayload>());
        Assert.Equal(AlertErrorCodes.InvalidContent, failed.Error.Code);
        Assert.Empty(_output.Alerts);
        Assert.Empty(_recorder.Texts);
    }

    [Fact]
    public async Task The_300_character_limit_is_enforced_by_the_backend_and_the_error_never_quotes_the_text()
    {
        var okText = new string('a', 300);
        Assert.Null(await CommandAsync(IpcTypes.AlertShow, new AlertShowPayload(okText, AlertMode.Band, 30)));
        Assert.Single(_output.Alerts);

        const string secret = "AVISO CONFIDENCIAL DE PRUEBA ";
        var tooLong = secret + new string('b', 290);
        var failed = (await CommandAsync(IpcTypes.AlertShow, new AlertShowPayload(tooLong, AlertMode.Band, 30)))!.GetPayload<CommandFailedPayload>();
        Assert.Equal(AlertErrorCodes.InvalidContent, failed.Error.Code);
        Assert.DoesNotContain("CONFIDENCIAL", failed.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_output.Alerts);   // la segunda no llegó
        Assert.DoesNotContain(_dispatcherLog.Entries, e => e.Message.Contains("CONFIDENCIAL", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1801)]
    [InlineData(-3)]
    public async Task A_duration_out_of_range_is_rejected(int seconds)
    {
        var failed = (await CommandAsync(IpcTypes.AlertShow, new AlertShowPayload("AVISO DE PRUEBA 1", AlertMode.Band, seconds)))!.GetPayload<CommandFailedPayload>();
        Assert.Equal(AlertErrorCodes.InvalidContent, failed.Error.Code);
        Assert.Empty(_output.Alerts);
    }

    [Fact]
    public async Task Until_removed_is_accepted_with_duration_zero_and_an_unknown_mode_is_rejected()
    {
        Assert.Null(await CommandAsync(IpcTypes.AlertShow, new AlertShowPayload("AVISO DE PRUEBA 1", AlertMode.Band, 0)));
        Assert.Equal(0, _output.Alerts.Single().DurationSec);

        // Un modo desconocido ni siquiera deserializa: el despachador lo rechaza sin que llegue nada a la salida.
        var raw = await _dispatcher.DispatchAsync($$$"""{"v":{{{IpcCatalog.ProtocolVersion}}},"id":"c9","kind":"command","type":"alert.show","payload":{"text":"AVISO DE PRUEBA 1","mode":"volando","durationSec":30}}""", CancellationToken.None);
        Assert.Equal(IpcTypes.CommandFailed, raw!.Type);
        Assert.Single(_output.Alerts);   // solo la de duración 0
    }

    [Fact]
    public async Task With_the_ticker_disabled_in_the_settings_a_ticker_alert_is_rejected_but_the_band_still_works()
    {
        await _settings.UpdateAsync(s => s with { AlertDisplay = new AlertDisplaySettings(AlertMode.Band, 30, null, TickerEnabled: false) }, CancellationToken.None);

        var failed = (await CommandAsync(IpcTypes.AlertShow, new AlertShowPayload("AVISO DE PRUEBA 1", AlertMode.Ticker, 30)))!.GetPayload<CommandFailedPayload>();
        Assert.Equal(AlertErrorCodes.InvalidContent, failed.Error.Code);
        Assert.Contains("desactivado", failed.Error.Message);
        Assert.Empty(_output.Alerts);

        Assert.Null(await CommandAsync(IpcTypes.AlertShow, new AlertShowPayload("AVISO DE PRUEBA 1", AlertMode.Band, 30)));
        Assert.Single(_output.Alerts);
    }

    [Fact]
    public async Task Settings_that_make_the_ticker_the_default_while_disabling_it_are_rejected_and_the_flag_round_trips()
    {
        var bad = (await CommandAsync(IpcTypes.AlertSetSettings, new AlertSettingsPayload(AlertMode.Ticker, 30, AlertTheme.Default, false)))!.GetPayload<CommandFailedPayload>();
        Assert.Equal(AlertErrorCodes.InvalidSettings, bad.Error.Code);

        Assert.Null(await CommandAsync(IpcTypes.AlertSetSettings, new AlertSettingsPayload(AlertMode.Band, 30, AlertTheme.Default, false)));
        var reply = ((JsonElement)(await QueryAsync(IpcTypes.AlertGetSettings, new { })).GetPayload<IpcReply>().Result!).Deserialize<AlertSettingsPayload>(IpcSerializer.Options)!;
        Assert.False(reply.TickerEnabled);
    }

    [Fact]
    public async Task Hide_goes_to_the_controller_with_its_arrival_number()
    {
        await _hide.HandleAsync(IpcEnvelope.Create(IpcKind.Command, IpcTypes.AlertHide, new { }) with { Order = 9 }, CancellationToken.None);
        Assert.Equal(["hideAlert"], _output.Calls);
        Assert.Equal([9L], _output.Orders);
    }

    [Fact]
    public void Show_and_hide_travel_on_the_priority_lane_and_the_database_commands_on_the_slow_one_and_reads_stay_live()
    {
        IpcLane Lane(string kind, string type) => IpcPriority.LaneOf(new IpcEnvelope(IpcCatalog.ProtocolVersion, "x", kind, type, default));
        Assert.Equal(IpcLane.Priority, Lane(IpcKind.Command, IpcTypes.AlertShow));
        Assert.Equal(IpcLane.Priority, Lane(IpcKind.Command, IpcTypes.AlertHide));
        Assert.Equal(IpcLane.Slow, Lane(IpcKind.Command, IpcTypes.AlertSave));
        Assert.Equal(IpcLane.Slow, Lane(IpcKind.Command, IpcTypes.AlertDelete));
        Assert.Equal(IpcLane.Live, Lane(IpcKind.Query, IpcTypes.AlertList));
        Assert.Equal(IpcLane.Live, Lane(IpcKind.Query, IpcTypes.AlertGetSettings));
        Assert.Equal(IpcLane.Live, Lane(IpcKind.Command, IpcTypes.AlertSetSettings));
    }

    [Fact]
    public async Task Saving_and_deleting_publish_events_with_only_ids_and_the_list_query_returns_the_text()
    {
        const string text = "Aviso de prueba fijado";
        Assert.Null(await CommandAsync(IpcTypes.AlertSave, new AlertSavePayload(null, text, true), "save-1"));
        var saved = Assert.Single(await PublishedAsync(), e => e.Type == IpcTypes.AlertChanged).GetPayload<AlertChangedPayload>();
        Assert.Equal(("save-1", "saved"), (saved.CommandId, saved.Kind));

        var reply = (await QueryAsync(IpcTypes.AlertList, new { })).GetPayload<IpcReply>();
        var list = ((JsonElement)reply.Result!).Deserialize<AlertListPayload>(IpcSerializer.Options)!;
        Assert.Equal([text], list.Pinned.Select(a => a.Text));
        Assert.Equal(saved.AlertId, list.Pinned[0].Id);

        Assert.Null(await CommandAsync(IpcTypes.AlertDelete, new AlertDeletePayload(saved.AlertId), "del-1"));
        var deleted = Assert.Single(await PublishedAsync(), e => e.Type == IpcTypes.AlertChanged).GetPayload<AlertChangedPayload>();
        Assert.Equal(("del-1", "deleted", saved.AlertId), (deleted.CommandId, deleted.Kind, deleted.AlertId));
        Assert.DoesNotContain("Aviso", JsonSerializer.Serialize(deleted, IpcSerializer.Options));   // el evento nunca lleva el texto
    }

    [Fact]
    public async Task Repository_errors_become_protocol_errors_with_their_code()
    {
        var failed = (await CommandAsync(IpcTypes.AlertSave, new AlertSavePayload(777, "Aviso de prueba", false)))!.GetPayload<CommandFailedPayload>();
        Assert.Equal(AlertErrorCodes.NotFound, failed.Error.Code);
    }

    [Fact]
    public async Task Settings_are_validated_before_saving_and_a_rejected_change_keeps_the_previous_ones()
    {
        var read = async () => ((JsonElement)(await QueryAsync(IpcTypes.AlertGetSettings, new { })).GetPayload<IpcReply>().Result!).Deserialize<AlertSettingsPayload>(IpcSerializer.Options)!;
        var initial = await read();
        Assert.Equal((AlertMode.Band, 30, AlertTheme.Default), (initial.Mode, initial.DurationSec, initial.Theme));

        var theme = AlertTheme.Default with { FontSize = 64, BandColor = "#102030F0", Position = AlertPosition.Top };
        Assert.Null(await CommandAsync(IpcTypes.AlertSetSettings, new AlertSettingsPayload(AlertMode.Ticker, 60, theme, true)));
        Assert.Equal((AlertMode.Ticker, 60, theme), ((await read()).Mode, (await read()).DurationSec, (await read()).Theme));

        foreach (var bad in new[]
        {
            new AlertSettingsPayload(AlertMode.Band, 30, AlertTheme.Default with { BandColor = "negro" }, true),
            new AlertSettingsPayload(AlertMode.Band, 3, AlertTheme.Default, true),
            new AlertSettingsPayload(AlertMode.Band, 30, AlertTheme.Default with { MinFontSize = 999 }, true),
        })
        {
            var failed = (await CommandAsync(IpcTypes.AlertSetSettings, bad))!.GetPayload<CommandFailedPayload>();
            Assert.Equal(AlertErrorCodes.InvalidSettings, failed.Error.Code);
        }
        Assert.Equal((AlertMode.Ticker, 60, theme), ((await read()).Mode, (await read()).DurationSec, (await read()).Theme));   // nada cambió
    }
}
