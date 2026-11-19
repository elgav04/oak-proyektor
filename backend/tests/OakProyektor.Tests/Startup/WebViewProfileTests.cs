using OakProyektor.App;

namespace OakProyektor.Tests.Startup;

/// <summary>Regresión: una carpeta de datos imposible dejaba el panel en negro porque WebView2 no podía crear su perfil.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]   // usa ACL de Windows para simular una carpeta de solo lectura; la aplicación es solo para Windows
public sealed class WebViewProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oak-profile-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private string Blocked(string name)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, name);
        File.WriteAllText(file, "archivo, no carpeta");
        return file;   // cualquier ruta DENTRO de este archivo es imposible de crear
    }

    private ApplicationPaths PathsWithData(string data) => new(data, Path.Combine(data, "logs"), _root);

    [Fact]
    public void Uses_the_profile_inside_the_data_folder_when_it_is_writable()
    {
        var data = Path.Combine(_root, "datos");

        var profile = WebViewProfile.Choose(PathsWithData(data), Path.Combine(_root, "temp"));

        Assert.Equal(Path.Combine(data, "cache", "WebView2"), profile.Directory);
        Assert.Null(profile.Note);
        Assert.True(Directory.Exists(profile.Directory));
        Assert.False(Directory.Exists(Path.Combine(_root, "temp")));
    }

    [Fact]
    public void Falls_back_to_a_temporary_profile_when_the_data_folder_is_not_writable()
    {
        var data = Path.Combine(Blocked("bloqueado"), "datos");
        var temp = Path.Combine(_root, "temp");

        var profile = WebViewProfile.Choose(PathsWithData(data), temp);

        Assert.Equal(Path.Combine(temp, "Oak-Proyektor", "WebView2"), profile.Directory);
        Assert.Contains("carpeta de datos", profile.Note);
    }

    [Fact]
    public void Falls_back_to_the_photino_default_when_nothing_is_writable()
    {
        var blocked = Blocked("bloqueado");

        var profile = WebViewProfile.Choose(PathsWithData(Path.Combine(blocked, "datos")), Path.Combine(blocked, "temp"));

        Assert.Null(profile.Directory);                 // null = no tocar el predeterminado de Photino
        Assert.Contains("predeterminado de Photino", profile.Note);
    }

    [Fact]
    public void A_data_folder_that_exists_but_rejects_writes_also_falls_back_for_the_profile()
    {
        var data = Path.Combine(_root, "datos-solo-lectura");
        Directory.CreateDirectory(Path.Combine(data, "cache", "WebView2"));
        var info = new DirectoryInfo(data);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.Write | System.Security.AccessControl.FileSystemRights.Delete,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Deny));
        info.SetAccessControl(acl);
        try
        {
            var profile = WebViewProfile.Choose(PathsWithData(data), Path.Combine(_root, "temp"));
            Assert.Equal(Path.Combine(_root, "temp", "Oak-Proyektor", "WebView2"), profile.Directory);
        }
        finally
        {
            acl = info.GetAccessControl();
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
                if (rule.AccessControlType == System.Security.AccessControl.AccessControlType.Deny) acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }
    }

    [Fact]
    public void The_probe_really_writes_and_cleans_up_after_itself()
    {
        var dir = Path.Combine(_root, "probe");
        WritableDirectory.Ensure(dir);
        Assert.Empty(Directory.GetFiles(dir));          // no deja el archivo de prueba
        Assert.Throws<ArgumentException>(() => WritableDirectory.Ensure(""));
    }
}