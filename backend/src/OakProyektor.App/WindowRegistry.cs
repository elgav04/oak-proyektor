using Photino.NET;

namespace OakProyektor.App;

/// <summary>
/// Ventanas vivas y si su página ya está lista, accesibles desde cualquier hilo.
/// Una ventana está "lista" cuando su página envió su primer mensaje. Enviar con SendWebMessage antes de eso
/// (el WebView2 se inicializa de forma asíncrona) provocó una violación de acceso en el código nativo.
/// </summary>
internal sealed class WindowRegistry(PhotinoWindow control)
{
    private readonly object _gate = new();
    private PhotinoWindow? _output;
    private bool _controlReady;
    private bool _outputReady;

    public PhotinoWindow Control { get; } = control;

    public PhotinoWindow? Output
    {
        get { lock (_gate) return _output; }
    }

    /// <summary>Registra la ventana de salida (o null al cerrarla). Siempre arranca como "no lista".</summary>
    public void SetOutput(PhotinoWindow? window)
    {
        lock (_gate) { _output = window; _outputReady = false; }
    }

    /// <summary>La página de control va a recargarse: hasta que vuelva a hablar no se le envía nada (igual que al arrancar).</summary>
    public void ResetControlReady()
    {
        lock (_gate) _controlReady = false;
    }

    public void MarkControlReady()
    {
        lock (_gate) _controlReady = true;
    }

    /// <summary>La página de la salida habló: pasa a lista, solo si sigue siendo la misma ventana.</summary>
    public void MarkOutputReady(PhotinoWindow window)
    {
        lock (_gate) { if (ReferenceEquals(_output, window)) _outputReady = true; }
    }

    /// <summary>La ventana, solo si ya se le puede enviar; si no, null (la página pide su estado al cargar, no se pierde nada).</summary>
    public PhotinoWindow? GetIfReady(WindowKind kind)
    {
        lock (_gate)
        {
            return kind == WindowKind.Control ? (_controlReady ? Control : null) : (_outputReady ? _output : null);
        }
    }
}
