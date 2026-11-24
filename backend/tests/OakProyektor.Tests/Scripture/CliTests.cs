using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Cli;
using OakProyektor.Core.Scripture;

namespace OakProyektor.Tests.Scripture;

public class CliArgumentsTests
{
    private static CliCommand Ok(params string[] args)
    {
        var (command, error) = CliArguments.Parse(args);
        Assert.True(command is not null, $"Debía interpretarse: {error}");
        return command!;
    }

    private static string Fails(params string[] args)
    {
        var (command, error) = CliArguments.Parse(args);
        Assert.Null(command);
        Assert.False(string.IsNullOrWhiteSpace(error));
        return error!;
    }

    [Fact]
    public void The_documented_import_command_parses()
    {
        var c = Ok("--import-bible", @"C:\datos\b.json", "--abbr", "RVR1960", "--name", "Reina-Valera 1960", "--lang", "es");

        Assert.Equal(CliCommandKind.ImportBible, c.Kind);
        Assert.Equal((@"C:\datos\b.json", "RVR1960", "Reina-Valera 1960", "es"), (c.Get("import-bible"), c.Get("abbr"), c.Get("name"), c.Get("lang")));
        Assert.Null(c.Get("numbering"));
        Assert.False(c.Has("allow-large-deletion"));
    }

    [Fact]
    public void Optional_options_and_switches_are_read()
    {
        var c = Ok("--import-bible", "x.json", "--abbr", "A", "--name", "N", "--lang", "es", "--numbering", "merge-ps47-3jn", "--attribution", "Aviso", "--format", "youversion-json", "--allow-large-deletion");

        Assert.Equal(("merge-ps47-3jn", "Aviso", "youversion-json"), (c.Get("numbering"), c.Get("attribution"), c.Get("format")));
        Assert.True(c.Has("allow-large-deletion"));
    }

    [Fact]
    public void The_allow_large_change_switch_parses_and_the_help_explains_the_protections()
    {
        var c = Ok("--import-bible", "x.json", "--abbr", "SIN", "--name", "N", "--lang", "es", "--allow-large-change", "--allow-large-deletion");
        Assert.True(c.Has("allow-large-change"));
        Assert.True(c.Has("allow-large-deletion"));
        Assert.False(Ok("--import-bible", "x.json", "--abbr", "SIN", "--name", "N", "--lang", "es").Has("allow-large-change"));
        var help = CliArguments.HelpText();
        Assert.Contains("--allow-large-change", help);
        Assert.Contains("CONSERVA el perfil", help);
        Assert.Contains("copia de seguridad", help);
    }

    [Fact]
    public void Show_verse_verify_and_list_parse()
    {
        var show = Ok("--show-verse", "Sal 47:9", "--bible", "RVR1960");
        Assert.Equal((CliCommandKind.ShowVerse, "Sal 47:9", "RVR1960"), (show.Kind, show.Get("show-verse"), show.Get("bible")));
        Assert.Null(Ok("--show-verse", "Jn 3:16").Get("bible"));

        var verify = Ok("--verify-bible", "--abbr", "RVR1960", "--against", "otra.json", "--against-format", "named-books-json");
        Assert.Equal((CliCommandKind.VerifyBible, "RVR1960", "otra.json"), (verify.Kind, verify.Get("abbr"), verify.Get("against")));

        Assert.Equal(CliCommandKind.ListBibles, Ok("--list-bibles").Kind);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Help_is_recognized_in_every_form_and_wins_over_anything_else(string flag)
    {
        Assert.Equal(CliCommandKind.Help, Ok(flag).Kind);
        Assert.Equal(CliCommandKind.Help, Ok("--import-bible", "x", flag).Kind);
        Assert.Contains("--import-bible", CliArguments.HelpText());
        Assert.Contains("--show-verse", CliArguments.HelpText());
        Assert.Contains("merge-ps47-3jn", CliArguments.HelpText());      // lista los perfiles reales
    }

    [Fact]
    public void Mistakes_are_explained()
    {
        Assert.Contains("Falta la orden", Fails());
        Assert.Contains("Falta la orden", Fails("--abbr", "X"));
        Assert.Contains("--abbr", Fails("--import-bible", "x.json", "--name", "N", "--lang", "es"));
        Assert.Contains("--import-bible", Fails("--abbr", "A", "--import-bible"));            // sin ruta
        Assert.Contains("Falta el valor de --abbr", Fails("--import-bible", "x.json", "--abbr", "--name", "N"));
        Assert.Contains("desconocida", Fails("--import-bible", "x", "--inventada", "y"));
        Assert.Contains("inesperado", Fails("--import-bible", "x", "suelto"));
        Assert.Contains("repetida", Fails("--import-bible", "x", "--abbr", "A", "--abbr", "B"));
        Assert.Contains("una sola orden", Fails("--list-bibles", "--show-verse", "Jn 3:16"));
        Assert.Contains("--against", Fails("--verify-bible", "--abbr", "A"));
        Assert.Contains("--show-verse", Fails("--show-verse", " "));
    }

    [Theory]
    [InlineData(new[] { "--import-bible", "x" }, true)]
    [InlineData(new[] { "--show-verse", "Jn 3:16" }, true)]
    [InlineData(new[] { "--verify-bible" }, true)]
    [InlineData(new[] { "--list-bibles" }, true)]
    [InlineData(new[] { "--help" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--otra-cosa" }, false)]
    public void Only_the_console_commands_make_the_app_skip_the_windows(string[] args, bool cli)
    {
        Assert.Equal(cli, CliArguments.IsCliInvocation(args));
    }
}

public sealed class CliRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oak-proyektor-tests", "cli-" + Guid.NewGuid().ToString("N"));

    public CliRunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (Exception) { /* best-effort */ }
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var paths = new ApplicationPaths(Path.Combine(_root, "datos"), Path.Combine(_root, "datos", "logs"), Path.Combine(_root, "web"), "prueba");
        var (command, problem) = CliArguments.Parse(args);
        Assert.True(command is not null, problem);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await new CliRunner(paths, NullLoggerFactory.Instance, output, error).RunAsync(command!, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private string File1(IEnumerable<SyntheticBible.V> verses, string name = "fuente.json") => SyntheticBible.WriteYouVersion(Path.Combine(_root, "archivos"), verses, fileName: name);

    private static readonly string[] SyntheticWords = ["Sintetico", "lorem", "ipsum", "dolor"];

    [Fact]
    public async Task Import_creates_the_database_imports_and_reports_without_opening_any_window_or_showing_text()
    {
        var (code, output, error) = await RunAsync("--import-bible", File1(SyntheticBible.Small()), "--abbr", "SIN", "--name", "Biblia sintética", "--lang", "es");

        Assert.Equal(CliRunner.Ok, code);
        Assert.Equal("", error);
        Assert.Contains("Informe de importación", output);
        Assert.Contains("creada", output);
        Assert.Contains("[leyendo]", output);
        Assert.Contains("[guardando]", output);
        Assert.Contains("[validando]", output);
        Assert.Contains("Validación: 5 libros", output);
        Assert.Contains("correcto", output);                                    // el índice de búsqueda
        foreach (var word in SyntheticWords) Assert.DoesNotContain(word, output, StringComparison.OrdinalIgnoreCase);   // ni una palabra del texto
        Assert.True(File.Exists(Path.Combine(_root, "datos", "oak-proyektor.db")));
    }

    [Fact]
    public async Task Importing_again_updates_in_place()
    {
        var file = File1(SyntheticBible.Small());
        await RunAsync("--import-bible", file, "--abbr", "SIN", "--name", "N", "--lang", "es");

        var (code, output, _) = await RunAsync("--import-bible", file, "--abbr", "SIN", "--name", "N", "--lang", "es");

        Assert.Equal(CliRunner.Ok, code);
        Assert.Contains("actualizada en el sitio", output);
        Assert.Contains($"sin cambios {SyntheticBible.Small().Count}", output.Replace(".", ""));
    }

    private static List<SyntheticBible.V> OtherTranslation() => [.. SyntheticBible.Small().Select(v => v with { Text = "Otra traduccion inventada " + v.Verse })];

    [Fact]
    public async Task Another_translation_with_the_same_abbreviation_exits_with_an_error_until_allow_large_change_and_then_makes_a_backup()
    {
        await RunAsync("--import-bible", File1(SyntheticBible.Small()), "--abbr", "SIN", "--name", "N", "--lang", "es");
        var other = File1(OtherTranslation(), "otra.json");

        var refused = await RunAsync("--import-bible", other, "--abbr", "SIN", "--name", "N", "--lang", "es");
        Assert.Equal(CliRunner.Failed, refused.Code);
        Assert.Contains("bible.tooManyChanges", refused.Error);
        Assert.Contains("sobrescribiría", refused.Error);

        var allowed = await RunAsync("--import-bible", other, "--abbr", "SIN", "--name", "N", "--lang", "es", "--allow-large-change");
        Assert.Equal(CliRunner.Ok, allowed.Code);
        Assert.Contains("Textos modificados:", allowed.Output);
        var backupLine = allowed.Output.Split('\n').Single(l => l.StartsWith("Copia de seguridad previa:", StringComparison.Ordinal));
        var backup = backupLine["Copia de seguridad previa:".Length..].Trim();
        Assert.True(File.Exists(backup), backup);
        Assert.StartsWith(Path.Combine(_root, "datos", "backups"), backup);
        foreach (var word in SyntheticWords) Assert.DoesNotContain(word, allowed.Output + allowed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_reimport_without_numbering_keeps_the_stored_profile_and_says_so_while_none_changes_it()
    {
        var file = File1(SyntheticBible.Small());
        await RunAsync("--import-bible", file, "--abbr", "SIN", "--name", "N", "--lang", "es", "--numbering", "merge-ps47-3jn");

        var kept = await RunAsync("--import-bible", file, "--abbr", "SIN", "--name", "N", "--lang", "es");
        Assert.Equal(CliRunner.Ok, kept.Code);
        Assert.Contains("Perfil de numeración aplicado: merge-ps47-3jn (conservado", kept.Output);

        var none = await RunAsync("--import-bible", file, "--abbr", "SIN", "--name", "N", "--lang", "es", "--numbering", "none");
        Assert.Equal(CliRunner.Ok, none.Code);
        Assert.Contains("Perfil de numeración aplicado: none", none.Output);
        Assert.DoesNotContain("conservado", none.Output.Split('\n').Single(l => l.StartsWith("Perfil de numeración aplicado", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_windows_1252_file_exits_with_a_clear_utf8_message_not_an_unexpected_error()
    {
        var path = Path.Combine(_root, "latin1.json");
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes("""{"books":[{"usfm":"GEN","chapters":[{"usfm":"GEN.1","items":[{"type":"verse","verse_numbers":[1],"lines":["acentuado: áéíóú"]}]}]}]}"""));

        var result = await RunAsync("--import-bible", path, "--abbr", "SIN", "--name", "N", "--lang", "es");

        Assert.Equal(CliRunner.Failed, result.Code);
        Assert.Contains("bible.sourceInvalid", result.Error);
        Assert.Contains("no está en UTF-8", result.Error);
        Assert.DoesNotContain("inesperado", result.Error);
        Assert.Contains("No hay Biblias instaladas", (await RunAsync("--list-bibles")).Output);
    }

    [Fact]
    public async Task A_failing_import_exits_non_zero_with_a_clear_message_and_nothing_imported()
    {
        var (code, output, error) = await RunAsync("--import-bible", Path.Combine(_root, "no-existe.json"), "--abbr", "SIN", "--name", "N", "--lang", "es");

        Assert.Equal(CliRunner.Failed, code);
        Assert.Contains("bible.fileMissing", error);
        Assert.DoesNotContain("Informe de importación", output);

        var broken = Path.Combine(_root, "roto.json"); File.WriteAllText(broken, "{ no es json");
        var second = await RunAsync("--import-bible", broken, "--abbr", "SIN", "--name", "N", "--lang", "es");
        Assert.Equal(CliRunner.Failed, second.Code);
        Assert.Contains("bible.sourceInvalid", second.Error);

        var list = await RunAsync("--list-bibles");
        Assert.Contains("No hay Biblias instaladas", list.Output);
    }

    [Fact]
    public async Task A_bad_option_value_exits_non_zero()
    {
        var file = File1(SyntheticBible.Small());
        var (code, _, error) = await RunAsync("--import-bible", file, "--abbr", "SIN", "--name", "N", "--lang", "es", "--numbering", "inventado");
        Assert.Equal(CliRunner.Failed, code);
        Assert.Contains("Perfil de numeración desconocido", error);
    }

    [Fact]
    public async Task The_numbering_profile_option_merges_and_the_report_says_which_merges_were_applied()
    {
        var (code, output, _) = await RunAsync("--import-bible", File1(SyntheticBible.Small()), "--abbr", "SIN", "--name", "N", "--lang", "es", "--numbering", "merge-ps47-3jn");

        Assert.Equal(CliRunner.Ok, code);
        Assert.Contains("Uniones de numeración aplicadas: PSA 47:9-10, 3JN 1:14-15", output);
        Assert.Contains("perfil de numeración merge-ps47-3jn", output);
    }

    [Fact]
    public async Task Changing_the_profile_on_reimport_reports_what_was_deleted_and_what_pointed_to_it()
    {
        var file = File1(SyntheticBible.Small());
        await RunAsync("--import-bible", file, "--abbr", "SIN", "--name", "N", "--lang", "es");
        // un elemento de un servicio apunta al versículo que se va a eliminar
        var factory = new OakProyektor.Data.SqliteConnectionFactory(OakProyektor.Data.DatabaseOptions.ForDataDirectory(Path.Combine(_root, "datos")));
        await using (var connection = await factory.OpenAsync(CancellationToken.None))                              // por la fábrica, como todo
        {
            await Dapper.SqlMapper.ExecuteAsync(connection, "INSERT INTO Playlists (Name) VALUES ('Culto')");
            await Dapper.SqlMapper.ExecuteAsync(connection, "INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, BibleAbbreviation) VALUES (1, 0, 'PASSAGE', 'x', 19, 47, 10, 'SIN')");
        }

        var (code, output, _) = await RunAsync("--import-bible", file, "--abbr", "SIN", "--name", "N", "--lang", "es", "--numbering", "merge-ps47-3jn");

        Assert.Equal(CliRunner.Ok, code);
        Assert.Contains("eliminados 2", output);
        Assert.Contains("ATENCIÓN: 1 elemento(s) apuntaban a versículos eliminados", output);
        Assert.Contains("playlist #1 (servicio 1): PSA 47:10", output);
    }

    [Fact]
    public async Task Show_verse_prints_the_passage_for_comparing_with_a_printed_bible_and_does_not_record_history()
    {
        await RunAsync("--import-bible", File1(SyntheticBible.Small()), "--abbr", "SIN", "--name", "N", "--lang", "es", "--attribution", "Aviso inventado");

        var (code, output, error) = await RunAsync("--show-verse", "Sal 47:9-10", "--bible", "SIN");

        Assert.Equal(CliRunner.Ok, code);
        Assert.Equal("", error);
        Assert.Contains("Sal 47:9-10 — SIN", output);
        Assert.Contains($"47:9  {SyntheticBible.TextOf(19, 47, 9)}", output);
        Assert.Contains($"47:10  {SyntheticBible.TextOf(19, 47, 10)}", output);
        Assert.Contains("[Aviso inventado]", output);

        var history = await RunAsync("--show-verse", "Sal 47:1");
        Assert.Equal(CliRunner.Ok, history.Code);
    }

    [Fact]
    public async Task Show_verse_errors_exit_non_zero_with_a_clear_message()
    {
        await RunAsync("--import-bible", File1(SyntheticBible.Small()), "--abbr", "SIN", "--name", "N", "--lang", "es");

        var invalid = await RunAsync("--show-verse", "Xyz 1:1");
        Assert.Equal(CliRunner.Failed, invalid.Code);
        Assert.Contains("bible.invalidReference", invalid.Error);

        var missing = await RunAsync("--show-verse", "Jn 3:16", "--bible", "SIN");
        Assert.Equal(CliRunner.Failed, missing.Code);
        Assert.Contains("bible.passageNotFound", missing.Error);

        var noBible = await RunAsync("--show-verse", "Mt 5:3", "--bible", "OTRA");
        Assert.Equal(CliRunner.Failed, noBible.Code);
        Assert.Contains("bible.notFound", noBible.Error);
    }

    [Fact]
    public async Task Verify_reports_counts_and_references_only()
    {
        var small = SyntheticBible.Small();
        await RunAsync("--import-bible", File1(small), "--abbr", "SIN", "--name", "N", "--lang", "es");
        var other = SyntheticBible.WriteNamedBooks(Path.Combine(_root, "archivos"), small.Select(v => v is { Book: 1, Chapter: 1, Verse: 2 } ? v with { Text = v.Text.ToUpperInvariant() } : v));

        var (code, output, _) = await RunAsync("--verify-bible", "--abbr", "SIN", "--against", other);

        Assert.Equal(CliRunner.Ok, code);
        Assert.Contains("Verificación cruzada", output);
        Assert.Contains("(b) solo tildes o mayúsculas: 1", output);
        Assert.Contains("GEN 1:2", output);
        foreach (var word in SyntheticWords) Assert.DoesNotContain(word, output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_shows_the_installed_bibles_and_help_needs_no_database()
    {
        await RunAsync("--import-bible", File1(SyntheticBible.Small()), "--abbr", "SIN", "--name", "Mi Biblia", "--lang", "es");

        var list = await RunAsync("--list-bibles");
        Assert.Contains("SIN\tMi Biblia\tes", list.Output);
        Assert.Contains("aviso de derechos: vacío", list.Output);

        var help = await RunAsync("--help");
        Assert.Equal(CliRunner.Ok, help.Code);
        Assert.Contains("--import-bible", help.Output);
    }

    [Fact]
    public void The_report_writer_prints_a_cross_check_with_no_text()
    {
        var report = new OakProyektor.Data.Scripture.CrossCheckReport("fmt", "none", 10, 10, 7, 1, 1, 1, 0, 0, 0, ["GEN 1:2"], ["GEN 1:4"], [], [], [], []);
        using var w = new StringWriter();

        CliReportWriter.Write(report, w);

        Assert.Contains("GEN 1:2", w.ToString());
        Assert.Contains("Sin avisos", w.ToString());
    }
}
