using OakProyektor.App;

namespace OakProyektor.Tests.Startup;

/// <summary>M14: el logger se crea con respaldo y un fallo de log nunca impide arrancar.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]   // usa ACL de Windows para simular una carpeta de solo lectura; la aplicación es solo para Windows
public sealed class LoggerSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oak-logger-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    /// <summary>Una ruta que NO se puede crear: su carpeta padre es un archivo.</summary>
    private string Blocked(string name)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, name);
        File.WriteAllText(file, "soy un archivo, no una carpeta");
        return Path.Combine(file, "logs");
    }

    private static string ReadAllLogs(string dir) =>
        string.Join("\n", Directory.GetFiles(dir, "oak-proyektor-*.log").Select(f => { using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return new StreamReader(s).ReadToEnd(); }));

    /// <summary>Una carpeta que EXISTE pero rechaza la escritura (permisos), como Program Files. Es el caso que Serilog oculta: no lanza, pierde los mensajes.</summary>
    private string ExistingButReadOnly(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var acl = new DirectoryInfo(dir).GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.Write | System.Security.AccessControl.FileSystemRights.Delete,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Deny));
        new DirectoryInfo(dir).SetAccessControl(acl);
        return dir;
    }

    private static void Unlock(string dir)
    {
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
            if (rule.AccessControlType == System.Security.AccessControl.AccessControlType.Deny) acl.RemoveAccessRule(rule);
        info.SetAccessControl(acl);
    }

    [Fact]
    public void A_folder_that_exists_but_is_not_writable_is_rejected_and_the_fallback_is_used()
    {
        var readOnly = ExistingButReadOnly("solo-lectura");
        var backup = Path.Combine(_root, "respaldo");
        try
        {
            var setup = LoggerSetup.Create([readOnly, backup]);
            setup.Log.Information("llegó al respaldo");
            setup.Dispose();

            Assert.Equal(backup, setup.Directory);                       // sin la comprobación real habría elegido readOnly y perdido todo en silencio
            Assert.Single(setup.Failures);
            Assert.Contains("llegó al respaldo", ReadAllLogs(backup));
            Assert.Empty(Directory.GetFiles(readOnly));
        }
        finally { Unlock(readOnly); }
    }

    [Fact]
    public void Uses_the_first_folder_when_it_is_writable()
    {
        var primary = Path.Combine(_root, "primaria");

        var setup = LoggerSetup.Create([primary, Path.Combine(_root, "respaldo")]);
        setup.Log.Information("hola desde la prueba");
        setup.Dispose();

        Assert.Equal(primary, setup.Directory);
        Assert.Empty(setup.Failures);
        Assert.Contains("hola desde la prueba", ReadAllLogs(primary));
        Assert.False(Directory.Exists(Path.Combine(_root, "respaldo")));
    }

    [Fact]
    public void Falls_back_to_the_next_folder_and_says_so_in_the_log()
    {
        var blocked = Blocked("bloqueada");
        var backup = Path.Combine(_root, "respaldo");

        var setup = LoggerSetup.Create([blocked, backup]);
        setup.Log.Information("sigue funcionando");
        setup.Dispose();

        Assert.Equal(backup, setup.Directory);
        var failure = Assert.Single(setup.Failures);
        Assert.Contains("bloqueada", failure);
        var text = ReadAllLogs(backup);
        Assert.Contains("sigue funcionando", text);
        Assert.Contains("No se pudo escribir el log", text);     // el respaldo deja constancia de por qué
    }

    [Fact]
    public void If_no_folder_works_it_returns_a_silent_logger_instead_of_throwing()
    {
        var setup = LoggerSetup.Create([Blocked("a"), Blocked("b")]);

        Assert.Null(setup.Directory);
        Assert.Equal(2, setup.Failures.Count);
        setup.Log.Information("esto no debe lanzar");
        setup.Log.Fatal(new InvalidOperationException("ni esto"), "tampoco");
        setup.Dispose();
    }

    [Fact]
    public void Garbage_paths_never_throw()
    {
        var setup = LoggerSetup.Create(["", "   ", "\0\0", new string('x', 4000)]);
        Assert.Null(setup.Directory);
        setup.Log.Information("ok");
    }

    [Fact]
    public void An_empty_candidate_list_gives_a_silent_logger()
    {
        var setup = LoggerSetup.Create([]);
        Assert.Null(setup.Directory);
        setup.Log.Information("ok");
    }
}