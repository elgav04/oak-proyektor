using OakProyektor.App;

namespace OakProyektor.Tests.Startup;

/// <summary>I11: instancia única con un Mutex con nombre.</summary>
public class SingleInstanceTests
{
    private static string UniqueName() => $@"Local\Oak-Proyektor-test-{Guid.NewGuid():N}";

    [Fact]
    public void The_first_instance_gets_the_guard_and_a_second_one_does_not()
    {
        var name = UniqueName();

        using var first = SingleInstanceGuard.TryAcquire(name);
        using var second = SingleInstanceGuard.TryAcquire(name);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void When_the_first_instance_ends_the_next_one_can_start()
    {
        var name = UniqueName();
        var first = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(first);

        first.Dispose();   // (el sistema hace lo mismo si el proceso muere)

        using var next = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task It_does_not_depend_on_the_thread_that_created_it()
    {
        // La aplicación crea el guardián en el hilo STA y lo libera en otro tras un await: un Mutex "tomado" lanzaría al soltarlo desde otro hilo.
        var name = UniqueName();
        var guard = await Task.Run(() => SingleInstanceGuard.TryAcquire(name));
        Assert.NotNull(guard);
        Assert.Null(SingleInstanceGuard.TryAcquire(name));

        await Task.Run(guard.Dispose);

        using var again = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void The_name_is_per_user_session_and_depends_on_the_data_folder()
    {
        var a = SingleInstanceGuard.NameFor(@"C:\Users\x\AppData\Local\Oak-Proyektor");
        var b = SingleInstanceGuard.NameFor(@"C:\Users\x\AppData\Local\Oak-Proyektor\");           // barra final
        var c = SingleInstanceGuard.NameFor(@"c:\users\X\appdata\local\oak-proyektor");            // otra capitalización
        var other = SingleInstanceGuard.NameFor(@"C:\repo\data");                                  // instalada vs desarrollo: pueden convivir

        Assert.StartsWith(@"Local\", a);
        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.NotEqual(a, other);
        Assert.Matches(@"^Local\\Oak-Proyektor-[0-9a-f]{16}$", a);
    }

    [Fact]
    public async Task Looking_for_a_window_nobody_has_returns_false_quickly()
    {
        var found = await ExistingInstance.TryBringToFrontAsync($"Ventana-que-no-existe-{Guid.NewGuid():N}", null, attempts: 2, delayMs: 5);
        Assert.False(found);
    }
}