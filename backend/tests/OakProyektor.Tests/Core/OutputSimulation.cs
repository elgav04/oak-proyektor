using OakProyektor.Core.Display;
using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Core;

/// <summary>
/// Simulador de la salida para los tests: hace lo mismo que <c>OutputController.EvaluateLocked</c> (pasar el estado a <see cref="OutputSafetyEngine"/> cada
/// 500 ms y aplicar la decisión) pero sin Photino ni Win32, con listas de monitores de 1, 2, 3 o 4 pantallas. Las transiciones se registran en <see cref="Log"/>.
/// </summary>
internal sealed class OutputSimulation
{
    public static readonly string[] Ids = ["A", "B", "C", "D"];
    public static readonly ScreenRect Minimized = new(-32000, -32000, 160, 28);

    private readonly OutputSafetyEngine _engine = new();
    private DateTimeOffset _now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly Dictionary<string, ScreenRect> _rects = [];

    public List<DisplayInfo> Displays { get; private set; } = [];
    public string? PanelId { get; set; }
    public bool Duplicate { get; set; }
    public string TargetId { get; private set; }
    public bool Paused { get; private set; }
    public OutputPauseReason? Reason { get; private set; }
    public ScreenRect Applied { get; private set; }
    public string? Preferred { get; set; }
    public DisplayLostPolicy Policy { get; set; }
    public List<string> Recent { get; } = [];
    public bool AutoRestore { get; set; } = true;
    public bool Topmost { get; private set; } = true;
    public List<string> Log { get; } = [];

    public int Transitions => Log.Count(l => l.StartsWith("pause") || l.StartsWith("resume") || l.StartsWith("move"));

    /// <summary>Monitores A, B, C, D en fila (1920×1080 cada uno; A es el principal, en 0,0). Solo se incluyen los primeros <paramref name="count"/>.</summary>
    public OutputSimulation(int count, string target, string? panel, DisplayLostPolicy policy = DisplayLostPolicy.WarnAndWait, bool preferTarget = true)
    {
        for (var i = 0; i < 4; i++) _rects[Ids[i]] = new ScreenRect(i * 1920, 0, 1920, 1080);
        PanelId = panel;
        Policy = policy;
        TargetId = target;
        Preferred = preferTarget ? target : null;
        Recent.Add(target);
        SetConnected(Ids.Take(count).ToArray());
        Applied = _rects[target];
    }

    public ScreenRect RectOf(string id) => _rects[id];

    /// <summary>Qué monitores están conectados ahora (en el orden dado, para probar que el orden no importa).</summary>
    public void SetConnected(params string[] ids)
    {
        Displays = [.. ids.Select(id => new DisplayInfo(id, _rects[id].X, _rects[id].Y, _rects[id].Width, _rects[id].Height, 1, _rects[id] is { X: 0, Y: 0 }, id == PanelId))];
    }

    /// <summary>Cambia la resolución de un monitor (mismo ID): solo cambia su rectángulo.</summary>
    public void SetRect(string id, ScreenRect rect)
    {
        _rects[id] = rect;
        var connected = Displays.Select(d => d.Id).ToArray();
        SetConnected(connected);
    }

    public void MovePanelTo(string? id)
    {
        PanelId = id;
        SetConnected(Displays.Select(d => d.Id).ToArray());
    }

    public void StartPaused(OutputPauseReason reason) { Paused = true; Reason = reason; Topmost = false; }

    /// <summary>El operador pulsa «Reanudar salida» (como <c>OpenLocked</c>: empieza de cero el antirrebote y el conteo, y reanuda en el monitor asignado).</summary>
    public void OperatorResume()
    {
        _engine.Reset();
        Paused = false; Reason = null; Topmost = true;
        Applied = ScreenRect.Of(Displays.Single(x => x.Id == TargetId));
        Log.Add("operatorResume");
    }

    public ScreenRect WindowRect => Paused ? Minimized : Applied;

    /// <summary>Un sondeo (500 ms). Devuelve la decisión final aplicada.</summary>
    public OutputSafetyDecision Tick()
    {
        var input = new OutputSafetyInput(WindowRect, !Paused && Topmost, Displays, TargetId, Paused, Reason, AutoRestore, Applied, Preferred, Policy, Recent, Duplicate);
        var d = _engine.Step(input, _now);
        _now += TimeSpan.FromMilliseconds(500);

        switch (d.Kind)
        {
            case SafetyActionKind.Pause:
                var reason = d.Reason!.Value;
                if (!Paused || Rank(reason) > Rank(Reason!.Value) || (reason == OutputPauseReason.DuplicateMode && OutputPauseState.IsAutomatic(Reason.Value) && Reason != reason))
                {
                    Paused = true; Reason = reason; Topmost = false;
                    Log.Add($"pause:{reason}");
                }
                break;
            case SafetyActionKind.Resume:
                Paused = false; Reason = null; Applied = ScreenRect.Of(Displays.Single(x => x.Id == TargetId)); Topmost = true;
                Log.Add("resume");
                break;
            case SafetyActionKind.Move:
                TargetId = d.DisplayId!; Applied = d.Area!.Value; Paused = false; Reason = null; Topmost = true;
                Recent.Remove(TargetId); Recent.Insert(0, TargetId);
                Log.Add($"move:{d.DisplayId}");
                break;
            case SafetyActionKind.Refit:
                Applied = d.Area!.Value;
                Log.Add("refit");
                break;
            case SafetyActionKind.SetTopmost:
                Topmost = d.Topmost;
                break;
        }
        return d;
    }

    public void Run(int ticks) { for (var i = 0; i < ticks; i++) Tick(); }

    private static int Rank(OutputPauseReason r) => r switch { OutputPauseReason.Emergency => 3, OutputPauseReason.Manual or OutputPauseReason.Unstable => 2, _ => 1 };
}
