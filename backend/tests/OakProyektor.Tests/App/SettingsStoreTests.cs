using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;

namespace OakProyektor.Tests.App;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oak-proyektor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private SettingsStore Create() => new(new ApplicationPaths(_dir, Path.Combine(_dir, "logs"), _dir), NullLogger<SettingsStore>.Instance);

    [Fact]
    public async Task Defaults_to_auto_restore_on_when_there_is_no_file()
    {
        var store = Create();
        await store.LoadAsync(CancellationToken.None);
        Assert.True(store.Current.AutoRestoreOutput);
    }

    [Fact]
    public async Task Update_persists_and_a_new_instance_reads_it_back()
    {
        var a = Create();
        await a.UpdateAsync(s => s with { AutoRestoreOutput = false }, CancellationToken.None);
        Assert.False(a.Current.AutoRestoreOutput);

        var b = Create();
        await b.LoadAsync(CancellationToken.None);
        Assert.False(b.Current.AutoRestoreOutput);
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json.tmp")));   // la escritura atómica no deja temporales
    }

    [Theory]
    [InlineData("{ esto no es json")]
    [InlineData("")]
    [InlineData("null")]
    public async Task A_corrupt_file_falls_back_to_defaults_instead_of_failing(string content)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), content);
        var store = Create();

        await store.LoadAsync(CancellationToken.None);

        Assert.True(store.Current.AutoRestoreOutput);
    }

    [Fact]
    public async Task Preferred_display_policy_and_recent_displays_persist_by_stable_id_and_the_enum_is_written_as_text()
    {
        var a = Create();
        await a.UpdateAsync(s => s with { PreferredDisplayId = @"dev:\\?\display#vtk2150#4&1331027&2&uid4145#{x}", OnDisplayLost = OakProyektor.Core.Output.DisplayLostPolicy.MoveToBest, RecentDisplayIds = ["B", "A"] }, CancellationToken.None);

        Assert.Contains("\"moveToBest\"", await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json")));
        var b = Create();
        await b.LoadAsync(CancellationToken.None);
        Assert.Equal(@"dev:\\?\display#vtk2150#4&1331027&2&uid4145#{x}", b.Current.PreferredDisplayId);
        Assert.Equal(OakProyektor.Core.Output.DisplayLostPolicy.MoveToBest, b.Current.OnDisplayLost);
        Assert.Equal(["B", "A"], b.Current.RecentDisplayIds);
    }

    [Fact]
    public async Task A_settings_file_from_before_these_options_keeps_working_with_the_safe_defaults()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), """{ "autoRestoreOutput": false }""");
        var store = Create();

        await store.LoadAsync(CancellationToken.None);

        Assert.False(store.Current.AutoRestoreOutput);
        Assert.Null(store.Current.PreferredDisplayId);
        Assert.Equal(OakProyektor.Core.Output.DisplayLostPolicy.WarnAndWait, store.Current.OnDisplayLost);   // por defecto: avisar y esperar
        Assert.Null(store.Current.RecentDisplayIds);
    }

    [Fact]
    public async Task An_unknown_policy_value_in_the_file_does_not_stop_the_app_and_falls_back_to_warn_and_wait()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), """{ "onDisplayLost": "explotar" }""");
        var store = Create();

        await store.LoadAsync(CancellationToken.None);

        Assert.Equal(OakProyektor.Core.Output.DisplayLostPolicy.WarnAndWait, store.Current.OnDisplayLost);
    }

    [Fact]
    public async Task A_failing_disk_write_still_applies_the_setting_in_memory()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "settings.json"));   // un directorio donde debería ir el archivo: File.Move fallará
        var store = Create();

        await store.UpdateAsync(s => s with { AutoRestoreOutput = false }, CancellationToken.None);

        Assert.False(store.Current.AutoRestoreOutput);
    }

    // ───── H10: carga por bloque, copia .bad y escritura durable ─────

    private string SettingsPath => Path.Combine(_dir, "settings.json");
    private string BadPath => SettingsPath + ".bad";
    private async Task WriteFileAsync(string content) { Directory.CreateDirectory(_dir); await File.WriteAllTextAsync(SettingsPath, content); }

    private const string ValidTheme = """{"fontFamily":"Arial","fontSize":64,"color":"#FFFFFF","outlineWidth":2,"outlineColor":"#000000","shadowBlur":8,"shadowOffset":3,"shadowColor":"#000000CC","align":"center","verticalAlign":"middle","lineHeight":1.3,"marginTop":70,"marginRight":120,"marginBottom":70,"marginLeft":120,"autoFit":true,"minFontSize":40}""";

    [Fact]
    public async Task A_valid_file_loads_without_warnings_and_without_a_bad_copy()
    {
        await WriteFileAsync($$"""{ "autoRestoreOutput": false, "bibleDisplay": { "projectOnNavigate": true, "transitionMs": 300, "theme": {{ValidTheme}}, "shortNames": [ { "bible": "SIN", "shortName": "S" } ] } }""");
        var store = Create();
        await store.LoadAsync(CancellationToken.None);

        Assert.Empty(store.Warnings);
        Assert.False(File.Exists(BadPath));
        Assert.False(store.Current.AutoRestoreOutput);
        Assert.True(store.Current.Bible.ProjectOnNavigate);
        Assert.Equal(300, store.Current.Bible.TransitionMs);
        Assert.Equal("S", store.Current.Bible.ShortNameOf("SIN"));
        Assert.True(store.Loaded.IsCompleted);
    }

    [Fact]
    public async Task A_missing_file_has_no_warnings_and_marks_the_load_as_finished()
    {
        var store = Create();
        await store.LoadAsync(CancellationToken.None);
        Assert.Empty(store.Warnings);
        Assert.True(store.Loaded.IsCompleted);
    }

    [Fact]
    public async Task An_invalid_hand_edited_theme_goes_back_to_the_default_theme_alone_and_every_bible_slide_stays_valid()
    {
        // Un color que no es un color y un tamaño imposible: antes eso rechazaba CADA proyección de un versículo con output.invalidContent.
        const string Original = """{ "autoRestoreOutput": false, "preferredDisplayId": "dev:X", "bibleDisplay": { "projectOnNavigate": true, "transitionMs": 300, "shortNames": [ { "bible": "SIN", "shortName": "S" } ], "theme": { "fontFamily": "Arial", "fontSize": 9999, "color": "rojo", "outlineWidth": 2, "outlineColor": "#000000", "shadowBlur": 8, "shadowOffset": 3, "shadowColor": "#000000CC", "align": "center", "verticalAlign": "middle", "lineHeight": 1.3, "marginTop": 70, "marginRight": 120, "marginBottom": 70, "marginLeft": 120, "autoFit": true, "minFontSize": 40 } } }""";
        await WriteFileAsync(Original);
        var store = Create();
        await store.LoadAsync(CancellationToken.None);

        var bible = store.Current.Bible;
        Assert.Equal(OakProyektor.Core.Scripture.BibleDisplaySettings.DefaultTheme, bible.EffectiveTheme);              // SOLO el tema vuelve al valor por defecto
        Assert.Null(OakProyektor.Core.Output.ContentValidator.ValidateTheme(bible.EffectiveTheme));
        Assert.Null(OakProyektor.Core.Scripture.BibleDisplaySettings.Validate(bible));                                  // y la configuración entera es válida
        Assert.True(bible.ProjectOnNavigate);                                                                           // lo demás se conserva
        Assert.Equal(300, bible.TransitionMs);
        Assert.Equal("S", bible.ShortNameOf("SIN"));
        Assert.False(store.Current.AutoRestoreOutput);
        Assert.Equal("dev:X", store.Current.PreferredDisplayId);

        Assert.Contains(store.Warnings, w => w.Contains("tema de la Biblia"));
        Assert.Contains(store.Warnings, w => w.Contains("settings.json.bad"));
        Assert.Equal(Original, await File.ReadAllTextAsync(BadPath));                                                   // copia EXACTA de lo que escribió el operador
        Assert.Equal(Original, await File.ReadAllTextAsync(SettingsPath));                                              // cargar no reescribe el archivo
    }

    [Theory]
    [InlineData("{ esto no es json", "dañado")]
    [InlineData("", "dañado")]
    [InlineData("null", "dañado")]
    [InlineData("[1,2,3]", "dañado")]
    public async Task A_corrupt_file_keeps_a_bad_copy_warns_and_starts_with_defaults(string content, string expected)
    {
        await WriteFileAsync(content);
        var store = Create();
        await store.LoadAsync(CancellationToken.None);

        Assert.True(store.Current.AutoRestoreOutput);
        Assert.Equal(new OakProyektor.Core.AppSettings().CitationHistoryLimit, store.Current.CitationHistoryLimit);
        Assert.Contains(store.Warnings, w => w.Contains(expected));
        Assert.Contains(store.Warnings, w => w.Contains("settings.json.bad"));
        Assert.Equal(content, await File.ReadAllTextAsync(BadPath));
    }

    [Fact]
    public async Task The_next_save_replaces_the_corrupt_file_but_the_bad_copy_survives()
    {
        const string Corrupt = "{ \"autoRestoreOutput\": false, ";
        await WriteFileAsync(Corrupt);
        var store = Create();
        await store.LoadAsync(CancellationToken.None);
        await store.UpdateAsync(s => s with { ShowPanelAlertOnOutput = true }, CancellationToken.None);

        Assert.Equal(Corrupt, await File.ReadAllTextAsync(BadPath));
        var again = Create();
        await again.LoadAsync(CancellationToken.None);
        Assert.Empty(again.Warnings);                       // el archivo nuevo es válido
        Assert.True(again.Current.ShowPanelAlertOnOutput);
    }

    [Fact]
    public async Task Each_bad_block_falls_back_alone_and_the_rest_of_the_file_is_kept()
    {
        await WriteFileAsync("""{ "autoRestoreOutput": false, "onDisplayLost": "explotar", "citationHistoryLimit": 5000, "showPanelAlertOnOutput": "si", "recentDisplayIds": ["A", "", "B"], "preferredDisplayId": "dev:Y", "defaultBible": "SIN" }""");
        var store = Create();
        await store.LoadAsync(CancellationToken.None);

        var s = store.Current;
        Assert.False(s.AutoRestoreOutput);                                                                              // válido: se conserva
        Assert.Equal(OakProyektor.Core.Output.DisplayLostPolicy.WarnAndWait, s.OnDisplayLost);                          // enum desconocido: su valor por defecto
        Assert.Equal(OakProyektor.Core.AppSettings.DefaultCitationHistoryLimit, s.CitationHistoryLimit);                // fuera de rango
        Assert.False(s.ShowPanelAlertOnOutput);                                                                         // tipo equivocado
        Assert.Equal(["A", "B"], s.RecentDisplayIds);                                                                   // las entradas vacías se descartan
        Assert.Equal("dev:Y", s.PreferredDisplayId);
        Assert.Equal("SIN", s.DefaultBible);
        Assert.Equal(3, store.Warnings.Count(w => !w.Contains(".bad")));
    }

    [Fact]
    public async Task A_bad_transition_or_bad_short_names_fall_back_without_losing_the_valid_theme()
    {
        await WriteFileAsync($$"""{ "bibleDisplay": { "transitionMs": 99999, "theme": {{ValidTheme}}, "shortNames": [ { "bible": "A", "shortName": "x" }, { "bible": "A", "shortName": "y" } ] } }""");
        var store = Create();
        await store.LoadAsync(CancellationToken.None);

        var bible = store.Current.Bible;
        Assert.Equal(OakProyektor.Core.Scripture.BibleDisplaySettings.DefaultTransitionMs, bible.TransitionMs);
        Assert.Equal("A", bible.ShortNameOf("A"));                                  // nombres duplicados: se usan las abreviaturas
        Assert.NotNull(bible.Theme);                                                // el tema válido se conserva
        Assert.Null(OakProyektor.Core.Scripture.BibleDisplaySettings.Validate(bible));
        Assert.Equal(2, store.Warnings.Count(w => !w.Contains(".bad")));
    }

    [Fact]
    public async Task A_bibleDisplay_with_a_wrong_type_goes_back_to_defaults_and_warns()
    {
        await WriteFileAsync("""{ "autoRestoreOutput": false, "bibleDisplay": { "transitionMs": "rápido" } }""");
        var store = Create();
        await store.LoadAsync(CancellationToken.None);

        Assert.Null(store.Current.BibleDisplay);
        Assert.False(store.Current.AutoRestoreOutput);
        Assert.Contains(store.Warnings, w => w.Contains("bibleDisplay"));
    }

    [Fact]
    public async Task A_bad_copy_that_cannot_be_written_never_stops_the_start()
    {
        await WriteFileAsync("{ roto");
        Directory.CreateDirectory(BadPath);                                          // un directorio donde iría la copia: File.Copy falla
        var store = Create();

        await store.LoadAsync(CancellationToken.None);

        Assert.True(store.Current.AutoRestoreOutput);
        Assert.Contains(store.Warnings, w => w.Contains("dañado"));
        Assert.DoesNotContain(store.Warnings, w => w.Contains("settings.json.bad"));   // no dice que hizo una copia que no hizo
        Assert.True(store.Loaded.IsCompleted);
    }

    [Fact]
    public async Task The_status_handler_returns_the_load_warnings_and_waits_for_the_load()
    {
        var fake = new InMemorySettings { Warnings = ["aviso uno"] };
        var handler = new OakProyektor.App.Handlers.SettingsGetStatusHandler(fake);
        var reply = (OakProyektor.Ipc.Messages.SettingsStatusPayload)(await handler.HandleAsync(new OakProyektor.Ipc.IpcEnvelope(OakProyektor.Ipc.IpcCatalog.ProtocolVersion, "q", "query", "settings.getStatus", default), CancellationToken.None))!;
        Assert.Equal(["aviso uno"], reply.Warnings);
    }

    [Fact]
    public async Task Saving_writes_a_complete_temp_file_to_disk_before_renaming_and_leaves_no_temp_behind()
    {
        var store = Create();
        await store.UpdateAsync(s => s with { PreferredDisplayId = "dev:Z", RecentDisplayIds = ["1", "2"] }, CancellationToken.None);

        Assert.False(File.Exists(SettingsPath + ".tmp"));
        var again = Create();
        await again.LoadAsync(CancellationToken.None);
        Assert.Empty(again.Warnings);
        Assert.Equal("dev:Z", again.Current.PreferredDisplayId);
        Assert.Equal(["1", "2"], again.Current.RecentDisplayIds);
    }

    [Fact]
    public async Task The_durable_write_puts_every_byte_in_the_file_even_for_large_content()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "durable.tmp");
        var content = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();

        await SettingsStore.WriteDurablyAsync(path, content, CancellationToken.None);

        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        // y se puede volver a escribir encima (el temporal de un intento anterior no estorba)
        await SettingsStore.WriteDurablyAsync(path, [1, 2, 3], CancellationToken.None);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Many_concurrent_saves_always_leave_a_complete_valid_file_matching_the_final_state()
    {
        var store = Create();
        await Task.WhenAll(Enumerable.Range(0, 60).Select(i => store.UpdateAsync(s => s with { RecentDisplayIds = [$"id{i}"], CitationHistoryLimit = i }, CancellationToken.None)));

        var again = Create();
        await again.LoadAsync(CancellationToken.None);
        Assert.Empty(again.Warnings);
        Assert.Equal(store.Current.CitationHistoryLimit, again.Current.CitationHistoryLimit);
        Assert.Equal(store.Current.RecentDisplayIds, again.Current.RecentDisplayIds);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }}
