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
    public void Looking_for_a_window_nobody_has_returns_false_immediately()
    {
        Assert.False(ExistingInstance.TryBringToFront($"Ventana-que-no-existe-{Guid.NewGuid():N}", null));
    }

    // ───────── SingleInstanceCoordinator: qué hace una instancia recién lanzada ─────────

    private sealed class Script
    {
        public int Acquires, Focuses, Sleeps;
        public Func<int, SingleInstanceGuard?> TryAcquire = _ => null;   // recibe el nº de intento (1, 2, ...)
        public Func<int, bool> HasWindow = _ => false;                    // recibe el nº de comprobación (1, 2, ...)

        public InstanceResult Run(int attempts = 32) => SingleInstanceCoordinator.Acquire(
            () => TryAcquire(++Acquires), () => HasWindow(++Focuses), attempts, _ => Sleeps++, pollMs: 250);
    }

    [Fact]
    public void With_no_other_instance_it_starts_at_once_without_waiting_or_looking_for_windows()
    {
        var name = UniqueName();
        var s = new Script { TryAcquire = _ => SingleInstanceGuard.TryAcquire(name) };

        var r = s.Run();
        using var guard = r.Guard;

        Assert.Equal(InstanceOutcome.Primary, r.Outcome);
        Assert.NotNull(guard);
        Assert.Equal((1, 0, 0), (s.Acquires, s.Focuses, s.Sleeps));      // el caso normal no espera nada
    }

    [Fact]
    public void With_a_healthy_other_instance_it_focuses_it_confirms_once_and_does_not_start()
    {
        var s = new Script { HasWindow = _ => true };

        var r = s.Run();

        Assert.Equal(InstanceOutcome.ExistingFocused, r.Outcome);
        Assert.Null(r.Guard);
        Assert.Equal(2, s.Focuses);                                        // enfoca y confirma
        Assert.Equal(1, s.Sleeps);                                         // una sola pausa corta, no ocho segundos
    }

    [Fact]
    public void If_the_first_instance_is_closing_no_window_then_the_lock_is_released_it_starts_normally_instead_of_exiting_silently()
    {
        var name = UniqueName();
        var dying = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(dying);
        var s = new Script
        {
            HasWindow = _ => false,                                         // la ventana ya se cerró
            TryAcquire = attempt =>
            {
                if (attempt == 5) dying.Dispose();                          // al 5º intento el proceso termina y el sistema libera el candado
                return SingleInstanceGuard.TryAcquire(name);
            },
        };

        var r = s.Run();
        using var guard = r.Guard;

        Assert.Equal(InstanceOutcome.Primary, r.Outcome);
        Assert.NotNull(guard);
        Assert.Equal(5, s.Acquires);
        Assert.Equal(4, s.Sleeps);                                          // esperó entre intentos
    }

    [Fact]
    public void A_window_seen_once_that_then_vanishes_is_a_dying_instance_not_a_healthy_one()
    {
        var name = UniqueName();
        var dying = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(dying);
        var s = new Script
        {
            HasWindow = n => n == 1,                                      // la 1ª comprobación la ve; la confirmación ya no
            TryAcquire = attempt =>
            {
                if (attempt == 3) dying.Dispose();
                return SingleInstanceGuard.TryAcquire(name);
            },
        };

        var r = s.Run();
        using var guard = r.Guard;

        Assert.Equal(InstanceOutcome.Primary, r.Outcome);                   // no se queda «enfocando» una ventana que desaparece
    }

    [Fact]
    public void If_the_other_instance_is_still_starting_its_window_appears_and_it_is_focused()
    {
        var name = UniqueName();
        using var other = SingleInstanceGuard.TryAcquire(name);
        var s = new Script
        {
            TryAcquire = _ => SingleInstanceGuard.TryAcquire(name),
            HasWindow = n => n >= 4,                                        // aún sin ventana en las primeras comprobaciones
        };

        var r = s.Run();

        Assert.Equal(InstanceOutcome.ExistingFocused, r.Outcome);
    }

    [Fact]
    public void With_another_process_holding_the_lock_and_never_a_window_it_gives_up_after_the_attempts_and_says_so()
    {
        var name = UniqueName();
        using var stuck = SingleInstanceGuard.TryAcquire(name);
        var s = new Script { TryAcquire = _ => SingleInstanceGuard.TryAcquire(name), HasWindow = _ => false };

        var r = s.Run(attempts: 6);

        Assert.Equal(InstanceOutcome.ExistingUnresponsive, r.Outcome);      // el llamador muestra un aviso: NO sale en silencio
        Assert.Null(r.Guard);
        Assert.Equal((6, 6, 5), (s.Acquires, s.Focuses, s.Sleeps));
    }

    [Fact]
    public void The_default_wait_is_longer_than_the_shutdown_limit_of_the_first_instance()
    {
        // El apagado de la primera instancia tiene un límite de 3 s (CLAUDE.md): esperar menos que eso dejaría a la segunda salir antes de poder arrancar.
        Assert.True(SingleInstanceCoordinator.DefaultAttempts * SingleInstanceCoordinator.DefaultPollMs > 3000 * 2);
    }
}