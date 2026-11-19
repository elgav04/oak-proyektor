using OakProyektor.App;

namespace OakProyektor.Tests.Startup;

/// <summary>I10: dónde van la BD, los logs y las cachés en producción y en desarrollo.</summary>
public sealed class ApplicationPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oak-paths-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private static Func<string, string?> NoEnv => _ => null;

    /// <summary>Un repositorio de mentira: carpeta con CLAUDE.md y backend/.</summary>
    private string FakeRepo()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "backend", "src", "OakProyektor.App", "bin"));
        File.WriteAllText(Path.Combine(repo, "CLAUDE.md"), "x");
        return repo;
    }

    /// <summary>Una instalación de mentira: ejecutable en «Program Files» sin ningún CLAUDE.md por encima.</summary>
    private string FakeInstall()
    {
        var install = Path.Combine(_root, "Program Files", "Oak-Proyektor");
        Directory.CreateDirectory(install);
        return install;
    }

    [Fact]
    public void In_production_everything_goes_to_LocalAppData_and_never_next_to_the_executable()
    {
        var install = FakeInstall();
        var local = Path.Combine(_root, "AppData", "Local");

        var p = ApplicationPaths.Resolve(NoEnv, install, local, Path.GetTempPath());

        var data = Path.Combine(local, "Oak-Proyektor");
        Assert.Equal(data, p.DataDirectory);
        Assert.Equal(Path.Combine(data, "logs"), p.LogsDirectory);
        Assert.Equal(Path.Combine(data, "cache"), p.CacheDirectory);
        Assert.Equal(Path.Combine(data, "cache", "WebView2"), p.WebViewDataDirectory);
        Assert.Equal("LocalAppData", p.Origin);
        foreach (var dir in new[] { p.DataDirectory, p.LogsDirectory, p.CacheDirectory, p.WebViewDataDirectory })
            Assert.DoesNotContain(install, dir);     // «Program Files» no es escribible
    }

    [Fact]
    public void In_development_everything_goes_to_data_in_the_repository()
    {
        var repo = FakeRepo();
        var exeDir = Path.Combine(repo, "backend", "src", "OakProyektor.App", "bin");

        var p = ApplicationPaths.Resolve(NoEnv, exeDir, Path.Combine(_root, "Local"), Path.GetTempPath());

        Assert.Equal(Path.Combine(repo, "data"), p.DataDirectory);
        Assert.Equal(Path.Combine(repo, "data", "logs"), p.LogsDirectory);
        Assert.Equal(Path.Combine(repo, "data", "cache", "WebView2"), p.WebViewDataDirectory);
        Assert.Equal("repositorio (desarrollo)", p.Origin);
        Assert.False(p.DataDirectory.StartsWith(Path.Combine(_root, "Local")), "en desarrollo los datos no deben ir al AppData");
    }

    [Fact]
    public void OAK_DATA_DIR_overrides_both()
    {
        var repo = FakeRepo();
        var exeDir = Path.Combine(repo, "backend", "src", "OakProyektor.App", "bin");
        var custom = Path.Combine(_root, "otra-carpeta");

        var inRepo = ApplicationPaths.Resolve(n => n == "OAK_DATA_DIR" ? custom : null, exeDir, Path.Combine(_root, "Local"), Path.GetTempPath());
        var inProd = ApplicationPaths.Resolve(n => n == "OAK_DATA_DIR" ? custom : null, FakeInstall(), Path.Combine(_root, "Local"), Path.GetTempPath());

        Assert.Equal(custom, inRepo.DataDirectory);
        Assert.Equal(custom, inProd.DataDirectory);
        Assert.Equal("OAK_DATA_DIR", inProd.Origin);
        Assert.Equal(Path.Combine(custom, "logs"), inProd.LogsDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_OAK_DATA_DIR_is_ignored(string blank)
    {
        var p = ApplicationPaths.Resolve(n => n == "OAK_DATA_DIR" ? blank : null, FakeInstall(), Path.Combine(_root, "Local"), Path.GetTempPath());
        Assert.Equal("LocalAppData", p.Origin);
    }

    [Fact]
    public void Without_LocalAppData_it_falls_back_to_a_temporary_folder_instead_of_failing()
    {
        var temp = Path.Combine(_root, "Temp");

        var p = ApplicationPaths.Resolve(NoEnv, FakeInstall(), "", temp);

        Assert.Equal(Path.Combine(temp, "Oak-Proyektor"), p.DataDirectory);
        Assert.Contains("temporal", p.Origin);
    }

    [Fact]
    public void The_web_root_prefers_wwwroot_next_to_the_executable_and_falls_back_to_the_repo_dist_in_development()
    {
        var install = FakeInstall();
        Directory.CreateDirectory(Path.Combine(install, "wwwroot"));
        Assert.Equal(Path.Combine(install, "wwwroot"), ApplicationPaths.Resolve(NoEnv, install, Path.Combine(_root, "L"), Path.GetTempPath()).WebRoot);

        var repo = FakeRepo();
        var exeDir = Path.Combine(repo, "backend", "src", "OakProyektor.App", "bin");   // sin wwwroot
        Assert.Equal(Path.Combine(repo, "frontend", "dist"), ApplicationPaths.Resolve(NoEnv, exeDir, Path.Combine(_root, "L"), Path.GetTempPath()).WebRoot);
    }

    [Fact]
    public void The_real_resolution_in_this_test_run_is_the_development_one_and_does_not_touch_the_users_AppData()
    {
        var p = ApplicationPaths.Resolve();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (Environment.GetEnvironmentVariable("OAK_DATA_DIR") is null)
        {
            Assert.Equal("repositorio (desarrollo)", p.Origin);
            Assert.False(p.DataDirectory.StartsWith(local, StringComparison.OrdinalIgnoreCase), "en desarrollo los datos no deben ir al AppData real");
        }
    }
}