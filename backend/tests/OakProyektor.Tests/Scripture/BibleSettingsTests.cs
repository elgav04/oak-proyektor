using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Handlers;
using OakProyektor.Core.Output;
using OakProyektor.Core.Scripture;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using OakProyektor.Tests.App;
using OakProyektor.Tests.Ipc;

namespace OakProyektor.Tests.Scripture;

/// <summary>Configuración de la proyección de Biblia (v8): valores por defecto, validación ANTES de guardar, persistencia en settings.json. Solo datos sintéticos.</summary>
public sealed class BibleSettingsTests : IAsyncLifetime
{
    private BibleEnv _env = null!;
    private InMemorySettings _settings = null!;
    private IpcDispatcher _dispatcher = null!;

    public async Task InitializeAsync()
    {
        _env = await BibleEnv.CreateAsync();
        _settings = new InMemorySettings();
        _dispatcher = new IpcDispatcher([new BibleGetSettingsHandler(_settings), new BibleSetSettingsHandler(_settings, _env.Query)], NullLogger<IpcDispatcher>.Instance);
        var path = SyntheticBible.WriteYouVersion(_env.FilesDirectory, SyntheticBible.Small(), fileName: "SIN.json");
        await _env.Importer.ImportAsync(new OakProyektor.Data.Scripture.BibleImportRequest(path, "SIN", "Biblia sintética", "es"), null, CancellationToken.None);
    }

    public Task DisposeAsync() { _env.Dispose(); return Task.CompletedTask; }

    private static string Msg(string kind, string type, object payload) =>
        $$"""{"v":{{IpcCatalog.ProtocolVersion}},"id":"q1","kind":"{{kind}}","type":"{{type}}","payload":{{JsonSerializer.Serialize(payload, IpcSerializer.Options)}}}""";

    private async Task<BibleSettingsPayload> GetAsync() =>
        (await _dispatcher.DispatchAsync(Msg(IpcKind.Query, IpcTypes.BibleGetSettings, new EmptyPayload()), CancellationToken.None))!.ReplyResult<BibleSettingsPayload>();

    private static BibleSetSettingsPayload Valid(Func<BibleSetSettingsPayload, BibleSetSettingsPayload>? change = null)
    {
        var s = BibleDisplaySettings.DefaultTheme;
        var p = new BibleSetSettingsPayload("SIN", true, false, true, 250, s, [new BibleShortNameDto("SIN", "Sint.")]);
        return change?.Invoke(p) ?? p;
    }

    private async Task<string?> SetAsync(BibleSetSettingsPayload payload)
    {
        var response = await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.BibleSetSettings, payload), CancellationToken.None);
        return response is null ? null : response.GetPayload<CommandFailedPayload>().Error.Code;
    }

    [Fact]
    public async Task Defaults_are_a_short_crossfade_superscript_on_attribution_off_and_the_default_theme()
    {
        var s = await GetAsync();
        Assert.Equal(280, s.TransitionMs);                        // ≈ 250–300 ms: navegar rápido no debe sentirse lento
        Assert.True(s.SuperscriptNumbers);
        Assert.False(s.ShowAttribution);                          // por defecto no se muestra ningún aviso
        Assert.False(s.ProjectOnNavigate);
        Assert.Null(s.DefaultBible);
        Assert.Equal(BibleDisplaySettings.DefaultTheme, s.Theme);
        Assert.Empty(s.ShortNames);
    }

    [Fact]
    public async Task A_valid_set_is_stored_and_read_back_including_the_default_bible()
    {
        Assert.Null(await SetAsync(Valid()));
        var s = await GetAsync();
        Assert.Equal(("SIN", true, false, true, 250), (s.DefaultBible, s.ProjectOnNavigate, s.SuperscriptNumbers, s.ShowAttribution, s.TransitionMs));
        Assert.Equal("Sint.", s.ShortNames.Single().ShortName);
        Assert.Equal("SIN", _settings.Current.DefaultBible);      // la consulta de Biblias lee AppSettings.DefaultBible
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("theme")]
    [InlineData("shortname")]
    [InlineData("duplicate")]
    public async Task An_invalid_value_is_rejected_with_a_code_and_changes_nothing(string what)
    {
        var before = _settings.Current;
        var bad = what switch
        {
            "transition" => Valid(p => p with { TransitionMs = 99999 }),
            "theme" => Valid(p => p with { Theme = p.Theme with { Color = "rojo" } }),
            "shortname" => Valid(p => p with { ShortNames = [new BibleShortNameDto("SIN", new string('x', 41))] }),
            _ => Valid(p => p with { ShortNames = [new BibleShortNameDto("SIN", "a"), new BibleShortNameDto("SIN", "b")] }),
        };
        Assert.Equal("bible.invalidSettings", await SetAsync(bad));
        Assert.Same(before, _settings.Current);
    }

    [Fact]
    public async Task A_default_bible_that_is_not_installed_is_rejected()
    {
        Assert.Equal("bible.notFound", await SetAsync(Valid(p => p with { DefaultBible = "NOEXISTE" })));
        Assert.Null(_settings.Current.DefaultBible);
    }

    [Fact]
    public async Task An_empty_default_bible_means_automatic()
    {
        Assert.Null(await SetAsync(Valid(p => p with { DefaultBible = "  " })));
        Assert.Null((await GetAsync()).DefaultBible);
    }

    [Fact]
    public void The_short_name_falls_back_to_the_abbreviation()
    {
        var s = new BibleDisplaySettings(ShortNames: [new BibleShortName("SIN", "Sint.")]);
        Assert.Equal("Sint.", s.ShortNameOf("SIN"));
        Assert.Equal("OTRA", s.ShortNameOf("OTRA"));
    }

    [Fact]
    public async Task Settings_survive_a_restart_and_an_older_settings_json_without_the_block_still_loads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oak-proyektor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            SettingsStore Create() => new(new ApplicationPaths(dir, Path.Combine(dir, "logs"), dir), NullLogger<SettingsStore>.Instance);

            var old = Create();                                   // settings.json de una versión anterior: sin «bibleDisplay»
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), """{ "autoRestoreOutput": false, "defaultBible": "SIN" }""");
            await old.LoadAsync(CancellationToken.None);
            Assert.Equal(280, old.Current.Bible.TransitionMs);
            Assert.Equal("SIN", old.Current.DefaultBible);

            var display = new BibleDisplaySettings(true, false, true, 300, BibleDisplaySettings.DefaultTheme with { FontSize = 80 }, [new BibleShortName("SIN", "S")]);
            await old.UpdateAsync(s => s with { BibleDisplay = display }, CancellationToken.None);

            var next = Create();
            await next.LoadAsync(CancellationToken.None);
            Assert.Equal(300, next.Current.Bible.TransitionMs);
            Assert.Equal(80, next.Current.Bible.EffectiveTheme.FontSize);
            Assert.Equal("S", next.Current.Bible.ShortNameOf("SIN"));
            var json = await File.ReadAllTextAsync(Path.Combine(dir, "settings.json"));
            Assert.Contains("\"bibleDisplay\"", json);
            Assert.DoesNotMatch("\"bible\"\\s*:\\s*\\{", json);   // la propiedad calculada «Bible» no se guarda
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
