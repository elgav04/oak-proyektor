namespace OakProyektor.Core.Output;

/// <summary>
/// Snapshot completo e idempotente de lo que debe mostrar la ventana de salida, en tres capas más los controles maestros:
/// <list type="number">
/// <item><see cref="Background"/>: ninguno | color | imagen | video en bucle.</item>
/// <item><see cref="Content"/>: un slide con bloques de texto y su tema.</item>
/// <item><see cref="Overlays"/>: contenedor de superposiciones (sin implementar).</item>
/// </list>
/// <para><b>Controles maestros</b> (<see cref="Master"/>), lo que cada uno hace en la salida (los snapshots llevan siempre TODAS las capas; qué se ve lo decide el modo):
/// <c>Live</c> todas las capas; <c>ClearText</c> solo el fondo (el slide se conserva en el estado y reaparece al volver a Live);
/// <c>Black</c> negro total (el video de fondo sigue reproduciéndose por debajo); <c>Logo</c> la pantalla de logo;
/// <c>Freeze</c>: ver abajo. Cambiar de modo es instantáneo y nunca espera a una transición.</para>
/// <para><b>Freeze</b>: la salida DEJA DE APLICAR snapshots nuevos de contenido y capas (texto, slide, superposiciones, y también el CAMBIO de fondo)
/// y conserva lo que tenía pintado; los snapshots que llegan mientras tanto se guardan y, al salir de Freeze (cualquier otro modo maestro), se aplica el ÚLTIMO.
/// El video de fondo que ya estaba en pantalla SIGUE reproduciéndose (congelar un fotograma parecería un fallo). La emergencia y la política
/// de monitores no dependen de Freeze. Lo implementa la salida (<c>frontend/src/output/freeze.ts</c>) a partir de <see cref="Master"/>; aquí
/// solo viaja el modo y el estado sigue acumulando los cambios.</para>
/// <see cref="Revision"/> solo cuenta cambios de los controles maestros; el orden de los snapshots lo da la secuencia global (<c>seq</c>) del protocolo.
/// <see cref="SchemaVersion"/> versiona la forma del modelo (sube si cambia el significado de un campo; la forma la fija además el test de protocolo).
/// </summary>
public sealed record OutputState(int SchemaVersion, long Revision, MasterMode Master, BackgroundLayer Background, ContentLayer Content, OverlayLayer Overlays)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Estado inicial: salida en negro, sin contenido.</summary>
    public static OutputState Initial { get; } = new(CurrentSchemaVersion, 0, MasterMode.Black, BackgroundLayer.Empty, ContentLayer.Empty, OverlayLayer.Empty);

    /// <summary>Aplica un control maestro. Solo cambia el modo: las capas se conservan. Repetir el mismo modo no cambia nada (devuelve la misma instancia).</summary>
    public OutputState WithMaster(MasterMode master)
    {
        if (!Enum.IsDefined(master)) throw new ArgumentOutOfRangeException(nameof(master), master, "Modo maestro desconocido");
        return master == Master ? this : this with { Revision = Revision + 1, Master = master };
    }

    /// <summary>Cambia el fondo. Un fondo idéntico al actual no cambia nada. NO toca el contenido: cambiar de fondo no reinicia el texto y viceversa.</summary>
    public OutputState WithBackground(BackgroundLayer background) =>
        background == Background ? this : this with { Background = background };

    /// <summary>Muestra un slide (reemplaza al anterior con crossfade). No toca el fondo ni el modo maestro.</summary>
    public OutputState WithSlide(Slide slide, int transitionMs) => this with { Content = new ContentLayer(slide, transitionMs) };

    /// <summary>Quita el slide (queda solo el fondo). Es distinto de <c>ClearText</c>: esto borra el contenido del estado, aquel solo lo oculta. Sin slide no cambia nada.</summary>
    public OutputState WithoutSlide(int transitionMs) =>
        Content.Slide is null ? this : this with { Content = new ContentLayer(null, transitionMs) };
}
