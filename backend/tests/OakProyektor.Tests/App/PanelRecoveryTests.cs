using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;

namespace OakProyektor.Tests.App;

/// <summary>Identificar los procesos renderizadores del PANEL (y solo esos) por su línea de comandos. Es lo que hace seguro terminar uno atascado.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class PanelRecoveryTests
{
    private const string Panel = @"C:\Users\x\AppData\Local\Oak-Proyektor\cache\WebView2";
    private const string Output = @"C:\Users\x\AppData\Local\Oak-Proyektor\cache\WebView2-output";

    private static string Cmd(string type, string profile) =>
        $"\"C:\\Program Files (x86)\\Microsoft\\EdgeWebView\\Application\\157.0\\msedgewebview2.exe\" --type={type} --user-data-dir={profile}\\EBWebView --webview-exe-name=OakProyektor.App.exe";

    [Fact]
    public void A_renderer_of_the_panel_profile_matches()
    {
        Assert.True(PanelRecovery.IsRendererOfProfile(Cmd("renderer", Panel), Panel));
        Assert.True(PanelRecovery.IsRendererOfProfile(Cmd("renderer", Panel.ToUpperInvariant()), Panel));        // Windows no distingue mayúsculas
        Assert.True(PanelRecovery.IsRendererOfProfile(Cmd("renderer", Panel).Replace("--user-data-dir=", "--user-data-dir=\"") .Replace("\\EBWebView", "\\EBWebView\""), Panel));   // con comillas
    }

    [Fact]
    public void The_output_renderer_never_matches_the_panel_profile_even_though_the_folder_name_is_a_prefix()
    {
        // «…\WebView2» es prefijo de «…\WebView2-output»: por eso se compara con «\EBWebView» detrás. Si esto fallara, se terminaría el renderizador de la PROYECCIÓN.
        Assert.False(PanelRecovery.IsRendererOfProfile(Cmd("renderer", Output), Panel));
        Assert.True(PanelRecovery.IsRendererOfProfile(Cmd("renderer", Output), Output));
    }

    [Theory]
    [InlineData("browser")]
    [InlineData("gpu-process")]
    [InlineData("utility")]
    [InlineData("crashpad-handler")]
    public void Only_renderers_match_never_the_browser_gpu_or_utility_processes(string type)
    {
        Assert.False(PanelRecovery.IsRendererOfProfile(Cmd(type, Panel), Panel));
    }

    [Fact]
    public void The_browser_process_has_no_type_and_never_matches()
    {
        Assert.False(PanelRecovery.IsRendererOfProfile($"msedgewebview2.exe --user-data-dir={Panel}\\EBWebView", Panel));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_unreadable_command_line_matches_nothing(string? commandLine)
    {
        Assert.False(PanelRecovery.IsRendererOfProfile(commandLine, Panel));
    }

    [Fact]
    public void Without_a_profile_nothing_matches()
    {
        Assert.False(PanelRecovery.IsRendererOfProfile(Cmd("renderer", Panel), ""));
    }

    [Fact]
    public void The_command_line_of_a_process_can_be_read_through_ntdll()
    {
        // Prueba el P/Invoke real contra un proceso conocido: el de las pruebas.
        var own = PanelRecovery.GetCommandLine(Environment.ProcessId);
        Assert.False(string.IsNullOrEmpty(own));
        Assert.Contains("testhost", own, StringComparison.OrdinalIgnoreCase);
        Assert.Null(PanelRecovery.GetCommandLine(int.MaxValue));   // un PID que no existe: null, sin excepción
    }

    [Fact]
    public void Killing_renderers_of_a_profile_that_has_none_touches_nothing()
    {
        Assert.Equal(0, PanelRecovery.KillRenderersOf(@"C:\no\existe\este\perfil", NullLogger.Instance));
    }
}
