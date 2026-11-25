using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Cli;
using OakProyektor.Core.Output;
using OakProyektor.Core.Songs;
using OakProyektor.Data;
using OakProyektor.Data.Songs;

namespace OakProyektor.Tests.Songs;

/// <summary>El bloque songDisplay de settings.json (carga por bloque) y la orden de consola --import-song. Letra sintética.</summary>
public sealed class SongSettingsAndCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oak-proyektor-tests", "songcli-" + Guid.NewGuid().ToString("N"));
    public SongSettingsAndCliTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // ───────────────────────────── settings.json ─────────────────────────────

    private SettingsStore Store() => new(new ApplicationPaths(_dir, Path.Combine(_dir, "logs"), _dir), NullLogger<SettingsStore>.Instance);

    [Fact]
    public async Task Song_display_settings_persist_with_the_enum_written_as_text_and_are_read_back()
    {
        var a = Store();
        await a.UpdateAsync(s => s with { SongDisplay = new SongDisplaySettings(SongDisplaySettings.DefaultTheme with { FontSize = 90 }, 350, SongAtEnd.Clear) }, CancellationToken.None);

        Assert.Contains("\"clear\"", await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json")));
        var b = Store();
        await b.LoadAsync(CancellationToken.None);
        Assert.Equal((90, 350, SongAtEnd.Clear), (b.Current.Songs.EffectiveTheme.FontSize, b.Current.Songs.TransitionMs, b.Current.Songs.AtEnd));
        Assert.Empty(b.Warnings);
    }

    [Fact]
    public async Task A_settings_file_from_before_songs_loads_with_the_song_defaults_and_no_warnings()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), """{ "autoRestoreOutput": false }""");
        var store = Store();

        await store.LoadAsync(CancellationToken.None);

        Assert.False(store.Current.AutoRestoreOutput);
        Assert.Equal((SongDisplaySettings.DefaultTheme, SongDisplaySettings.DefaultTransitionMs, SongAtEnd.Stay), (store.Current.Songs.EffectiveTheme, store.Current.Songs.TransitionMs, store.Current.Songs.AtEnd));
        Assert.Empty(store.Warnings);
    }

    [Theory]
    [InlineData("""{ "songDisplay": { "theme": { "fontFamily": "Arial", "fontSize": 60, "color": "no-es-un-color", "outlineWidth": 0, "outlineColor": "#000000", "shadowBlur": 0, "shadowOffset": 0, "shadowColor": "#000000", "align": "center", "verticalAlign": "middle", "lineHeight": 1.2, "marginTop": 10, "marginRight": 10, "marginBottom": 10, "marginLeft": 10, "autoFit": true, "minFontSize": 20 }, "transitionMs": 300, "atEnd": "clear" } }""", "tema")]
    [InlineData("""{ "songDisplay": { "transitionMs": -4, "atEnd": "clear" } }""", "crossfade")]
    [InlineData("""{ "songDisplay": { "transitionMs": 300, "atEnd": "explotar" } }""", "songDisplay")]
    [InlineData("""{ "songDisplay": { "transitionMs": "rápido" } }""", "songDisplay")]
    [InlineData("""{ "songDisplay": "texto" }""", "songDisplay")]
    public async Task An_invalid_song_block_goes_back_to_its_defaults_with_a_warning_and_never_breaks_the_rest(string json, string warningFragment)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), json.Replace("{ \"songDisplay\"", "{ \"autoRestoreOutput\": false, \"songDisplay\""));
        var store = Store();

        await store.LoadAsync(CancellationToken.None);

        Assert.False(store.Current.AutoRestoreOutput);                                                  // el resto de la configuración sobrevive
        Assert.Contains(store.Warnings, w => w.Contains(warningFragment));
        Assert.Null(ContentValidator.ValidateTheme(store.Current.Songs.EffectiveTheme));                 // lo que quedó es proyectable: no habrá output.invalidContent
        Assert.Null(ContentValidator.ValidateTransition(store.Current.Songs.TransitionMs, "Canciones"));
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.bad")));                              // y se guardó una copia de lo que había escrito el operador
    }

    [Fact]
    public async Task A_bad_crossfade_keeps_a_valid_theme_and_a_bad_theme_keeps_a_valid_crossfade()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"), """{ "songDisplay": { "transitionMs": 99999999, "atEnd": "clear", "theme": null } }""");
        var store = Store();

        await store.LoadAsync(CancellationToken.None);

        Assert.Equal((SongDisplaySettings.DefaultTransitionMs, SongAtEnd.Clear), (store.Current.Songs.TransitionMs, store.Current.Songs.AtEnd));
    }

    // ───────────────────────────── --import-song ─────────────────────────────

    private (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var paths = new ApplicationPaths(Path.Combine(_dir, "data"), Path.Combine(_dir, "data", "logs"), Path.Combine(_dir, "data"));
        var code = CliHost.Execute(args, paths, output, error, SingleInstanceGuard.TryAcquire, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private string Write(string name, byte[] bytes) { var path = Path.Combine(_dir, name); File.WriteAllBytes(path, bytes); return path; }

    [Fact]
    public void The_arguments_are_parsed_and_the_command_is_recognised_as_a_console_order()
    {
        Assert.True(CliArguments.IsCliInvocation(["--import-song", "a.txt"]));
        var (command, problem) = CliArguments.Parse(["--import-song", "a.txt", "--title", "Mi canción", "--author", "Autor", "--no-merge"]);

        Assert.Null(problem);
        Assert.Equal(CliCommandKind.ImportSong, command!.Kind);
        Assert.Equal(("a.txt", "Mi canción", "Autor", true), (command.Get("import-song"), command.Get("title"), command.Get("author"), command.Has("no-merge")));
        Assert.NotNull(CliArguments.Parse(["--import-song"]).Error);                                          // falta el valor
        Assert.NotNull(CliArguments.Parse(["--import-song", "a.txt", "--list-bibles"]).Error);                // una sola orden a la vez
        Assert.Contains("--import-song", CliArguments.HelpText());
    }

    [Fact]
    public async Task Importing_a_utf8_file_creates_a_song_and_reports_only_counts_and_what_was_detected()
    {
        var text = "[Verso 1]\nLinea sintética ñandú\n\n[Coro]\nCoro inventado " + SyntheticSong.Sentinel + "\n\nOrden: V1 C V1 C";
        var path = Write("cancion.txt", Encoding.UTF8.GetBytes(text));

        var (code, output, error) = Run("--import-song", path, "--title", "Título inventado", "--author", "Autora");

        Assert.True(code == 0, error + output);
        Assert.Contains("Codificación detectada: utf-8", output);
        Assert.Contains("2 sección(es), orden de 4 elemento(s)", output);
        Assert.Contains("Canción creada: id 1", output);
        Assert.DoesNotContain("Xilofon", output + error);          // nunca la letra
        Assert.DoesNotContain("Título inventado", output + error); // ni el título
        await using var c = await OpenAsync();
        var song = (await new SongRepository(new SqliteConnectionFactory(OakProyektor.Data.DatabaseOptions.ForDataDirectory(Path.Combine(_dir, "data"))), new AlwaysReady()).GetAsync(1, CancellationToken.None))!;
        Assert.Equal(("Título inventado", "Autora"), (song.Title, song.Author));
        Assert.Equal([0, 1, 0, 1], song.Arrangement);
        Assert.Equal(["Verso 1", "Coro"], song.Sections.Select(s => s.Label));
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SongRevisions"));   // crear no deja copia previa
    }

    [Fact]
    public async Task A_windows_1252_file_is_read_with_the_fallback_and_says_so()
    {
        byte[] bytes = [.. "[Verso 1]\nLumar"u8, 0xED, .. " se"u8, 0xF1, .. "or"u8];   // «Lumarí señor» en Windows-1252
        var path = Write("antiguo.txt", bytes);

        var (code, output, error) = Run("--import-song", path);

        Assert.True(code == 0, error);
        Assert.Contains("Codificación detectada: windows-1252 (el archivo no era UTF-8 válido)", output);
        Assert.Contains("Aviso: El archivo no está en UTF-8", output);
        var song = (await new SongRepository(new SqliteConnectionFactory(OakProyektor.Data.DatabaseOptions.ForDataDirectory(Path.Combine(_dir, "data"))), new AlwaysReady()).GetAsync(1, CancellationToken.None))!;
        Assert.Equal("Lumarí señor", song.Sections[0].Text);
        Assert.Equal("antiguo", song.Title);   // sin --title: el nombre del archivo
    }

    [Fact]
    public void A_file_that_is_not_text_a_missing_file_and_a_huge_file_fail_clearly_without_creating_anything()
    {
        var docx = Write("documento.txt", [0x50, 0x4B, 0x03, 0x04, 0, 0]);
        var big = Write("grande.txt", new byte[SongLimits.MaxFileBytes + 1]);

        var notText = Run("--import-song", docx);
        var missing = Run("--import-song", Path.Combine(_dir, "no-existe.txt"));
        var tooBig = Run("--import-song", big);

        Assert.Equal(1, notText.Code);
        Assert.Contains("[song.notText]", notText.Error);
        Assert.Contains("Word o PowerPoint", notText.Error);
        Assert.Equal(1, missing.Code);
        Assert.Contains("[song.fileMissing]", missing.Error);
        Assert.Equal(1, tooBig.Code);
        Assert.Contains("[song.fileTooLarge]", tooBig.Error);
    }

    [Fact]
    public async Task A_text_with_parser_errors_or_no_sections_saves_nothing()
    {
        var withError = Write("mal.txt", Encoding.UTF8.GetBytes("[Verso 1]\nuno\n\nOrden:"));
        var empty = Write("vacio.txt", Encoding.UTF8.GetBytes("   \n\n"));

        var a = Run("--import-song", withError);
        var b = Run("--import-song", empty);

        Assert.Equal(1, a.Code);
        Assert.Contains("orden está vacío", a.Error);
        Assert.Contains("No se guardó nada", a.Error);
        Assert.Equal(1, b.Code);
        Assert.Contains("ninguna sección", b.Error);
        await using var c = await OpenAsync();
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Songs"));
    }

    [Fact]
    public void Importing_is_refused_while_the_application_holds_the_data_folder()
    {
        var path = Write("a.txt", Encoding.UTF8.GetBytes("[Verso 1]\nuno"));
        var output = new StringWriter();
        var error = new StringWriter();
        var paths = new ApplicationPaths(Path.Combine(_dir, "data"), Path.Combine(_dir, "data", "logs"), Path.Combine(_dir, "data"));

        var code = CliHost.Execute(["--import-song", path], paths, output, error, _ => null, CancellationToken.None);   // otro proceso tiene la instancia única

        Assert.Equal(1, code);
        Assert.Contains("No se cambió nada", error.ToString());
    }

    private async Task<Microsoft.Data.Sqlite.SqliteConnection> OpenAsync() =>
        await new SqliteConnectionFactory(OakProyektor.Data.DatabaseOptions.ForDataDirectory(Path.Combine(_dir, "data"))).OpenAsync(CancellationToken.None);
}
