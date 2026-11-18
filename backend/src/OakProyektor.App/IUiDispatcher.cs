using Photino.NET;

namespace OakProyektor.App;

/// <summary>Ejecuta código en el hilo de UI de Photino (único hilo seguro para tocar ventanas).</summary>
internal interface IUiDispatcher
{
    /// <summary>Ejecuta y espera. Si <paramref name="action"/> lanza, la excepción se relanza en el llamador (nunca cruza la capa nativa).</summary>
    void Invoke(Action action);
}

internal sealed class PhotinoUiDispatcher(PhotinoWindow main) : IUiDispatcher
{
    public void Invoke(Action action)
    {
        Exception? failure = null;
        main.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
