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
}
