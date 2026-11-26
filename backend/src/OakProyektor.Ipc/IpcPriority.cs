namespace OakProyektor.Ipc;

/// <summary>Carril de entrada de un mensaje (ver <see cref="IpcPriority.LaneOf"/>).</summary>
public enum IpcLane
{
    /// <summary>
    /// Controles maestros, recuperación de emergencia y las alertas (<c>alert.show</c>, <c>alert.hide</c>: solo tocan memoria, y así «mostrar» y «quitar» comparten FIFO y no se cruzan): nunca esperan a nadie.
    /// </summary>
    Priority,
    /// <summary>Todo lo que el operador espera ver YA: proyectar/quitar contenido, consultas rápidas (estado, monitores, latido, versículos, ajustes).</summary>
    Live,
    /// <summary>
    /// Trabajo que puede tardar: validar y aplicar fondos (toca el disco), abrir/cerrar la ventana de salida, importar Biblias, búsquedas de texto y TODA escritura de canciones
    /// (guardar, restaurar, borrar: pueden esperar el bloqueo de escritura de SQLite mientras corre una importación) y de listas de culto (crear, editar, añadir, mover, quitar, duplicar, borrar).
    /// Las LECTURAS de listas (<c>playlist.list/get/getSettings</c>) y sus ajustes van en Live.
    /// </summary>
    Slow,
}

/// <summary>
/// Reparto de los mensajes entrantes en TRES carriles, cada uno con su propio consumidor (FIFO dentro de cada uno; entre carriles NO hay orden garantizado):
/// <list type="bullet">
/// <item><b>Priority</b> (<c>output.setMaster</c>, <c>output.recover</c>): los controles maestros deben responder al instante (hallazgo I5).</item>
/// <item><b>Live</b> (por defecto): proyectar un versículo (<c>output.showContent</c>), quitarlo (<c>output.clearContent</c>) y las consultas rápidas. Un handler de este
/// carril solo toca memoria o consultas cortas a la BD: nunca disco de medios ni ventanas. Así una proyección no espera detrás de un fondo en un USB lento (hallazgo H6).</item>
/// <item><b>Slow</b>: <c>output.setBackground</c> (validación de hasta 5 s con una unidad atascada), <c>output.retryBackground</c> (mismo orden que el anterior),
/// <c>output.open</c>/<c>output.close</c> (crean y cierran la ventana), <c>bible.import</c> y <c>bible.search</c> (texto libre sobre todo el FTS).</item>
/// </list>
/// Por qué este reparto y no más carriles: las únicas dependencias de orden reales son (a) «Mover/reabrir» y «Recuperar control» — ya resuelta con el número de llegada
/// (<c>IpcEnvelope.Order</c>) — y (b) fondos entre sí (van juntos en Slow, así que «aplicar A» y «reintentar» no se cruzan). El contenido y el fondo son capas independientes
/// del estado y cada cambio publica su snapshot con la secuencia global (<c>seq</c>) bajo un candado, así que ejecutarlos en paralelo no puede reordenar las réplicas.
/// Un tipo desconocido cae en Live (su error <c>unknown_message</c> sale al instante).
/// </summary>
public static class IpcPriority
{
    public static bool IsHigh(IpcEnvelope envelope) =>
        envelope.Kind == IpcKind.Command && envelope.Type is IpcTypes.OutputSetMaster or IpcTypes.OutputRecover or IpcTypes.AlertShow or IpcTypes.AlertHide;

    public static IpcLane LaneOf(IpcEnvelope envelope)
    {
        if (IsHigh(envelope)) return IpcLane.Priority;
        return envelope.Type switch
        {
            IpcTypes.OutputSetBackground or IpcTypes.OutputRetryBackground or IpcTypes.OutputOpen or IpcTypes.OutputClose
                or IpcTypes.BibleImport or IpcTypes.BibleSearch
                or IpcTypes.SongSearch or IpcTypes.SongSave or IpcTypes.SongRestoreRevision or IpcTypes.SongDelete
                or IpcTypes.PlaylistCreate or IpcTypes.PlaylistUpdate or IpcTypes.PlaylistAddItem or IpcTypes.PlaylistMoveItem or IpcTypes.PlaylistRemoveItem
                or IpcTypes.PlaylistUpdateItem or IpcTypes.PlaylistDuplicate or IpcTypes.PlaylistDelete
                or IpcTypes.AlertSave or IpcTypes.AlertDelete
                // Biblioteca de medios: TODO lo que toca disco o la BD (importar, renombrar, mover, borrar, carpetas, miniaturas, verificar, servir un fondo) va por el carril lento.
                or IpcTypes.MediaImport or IpcTypes.MediaRename or IpcTypes.MediaMove or IpcTypes.MediaDelete or IpcTypes.MediaCreateFolder or IpcTypes.MediaRenameFolder
                or IpcTypes.MediaDeleteFolder or IpcTypes.MediaReportProbe or IpcTypes.MediaSetThumbnail or IpcTypes.MediaVerifyLibrary or IpcTypes.MediaSetBackground => IpcLane.Slow,
            _ => IpcLane.Live,
        };
    }
}
