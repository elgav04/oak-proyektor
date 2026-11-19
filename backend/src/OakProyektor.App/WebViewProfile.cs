namespace OakProyektor.App;

/// <summary>
/// Los perfiles de WebView2 EFECTIVOS: uno para el panel y OTRO para la salida (carpetas distintas = entornos distintos = procesos de navegador distintos).
/// Medido: con un perfil compartido, un panel recargado (o recuperado) acaba en el MISMO renderizador que la salida y un bucle infinito en el panel
/// congela también la proyección; con perfiles separados esto no puede pasar, y el árbol de procesos del panel se puede identificar sin ambigüedad (su carpeta).
/// Es una caché regenerable y la interfaz es prioritaria: si la carpeta de datos no se puede escribir, el perfil cae a una carpeta
/// temporal y, si tampoco, al predeterminado de Photino. Sin este respaldo, una carpeta de datos imposible dejaba el panel en NEGRO
/// (WebView2 no avisa: no puede crear su perfil y no dibuja nada), así que el operador ni siquiera veía el aviso de la base de datos.
/// </summary>
/// <param name="Directory">Carpeta del PANEL, o null para el predeterminado de Photino (<c>%LOCALAPPDATA%\Photino</c>).</param>
/// <param name="Note">Por qué no se usó la carpeta preferida (para el log); null si se usó.</param>
/// <param name="OutputDirectory">Carpeta de la SALIDA (hermana de la del panel con sufijo <c>-output</c>), o null si no se pudo escribir (entonces usa el predeterminado de Photino).</param>
internal sealed record WebViewProfile(string? Directory, string? Note, string? OutputDirectory = null)
{
    public static WebViewProfile Choose(ApplicationPaths paths, string temporaryRoot)
    {
        var failures = new List<string>();
        var temporary = Path.Combine(temporaryRoot, ApplicationPaths.ProductFolderName, "WebView2");
        var chosen = WritableDirectory.FirstWritable([paths.WebViewDataDirectory, temporary], failures);

        // La salida tiene su propia carpeta junto a la del panel. Si no se puede escribir, usa el predeterminado de Photino (que tampoco es el del panel).
        var output = chosen is null ? null : WritableDirectory.FirstWritable([chosen + OutputSuffix], []);

        if (chosen == paths.WebViewDataDirectory) return new WebViewProfile(chosen, null, output);
        var why = string.Join(" | ", failures);
        return chosen is null
            ? new WebViewProfile(null, $"No se pudo escribir el perfil de WebView2 en ninguna carpeta ({why}); se usa el predeterminado de Photino.")
            : new WebViewProfile(chosen, $"No se pudo escribir el perfil de WebView2 en la carpeta de datos ({why}); se usa {chosen}.", output);
    }

    public const string OutputSuffix = "-output";

    /// <summary>Aplica el perfil del PANEL a su ventana (si hay carpeta).</summary>
    public Photino.NET.PhotinoWindow ApplyTo(Photino.NET.PhotinoWindow window) =>
        Directory is null ? window : window.SetTemporaryFilesPath(Directory);

    /// <summary>Aplica el perfil propio de la SALIDA: un entorno distinto al del panel, así que un fallo del renderizador del panel no puede alcanzarla.</summary>
    public Photino.NET.PhotinoWindow ApplyToOutput(Photino.NET.PhotinoWindow window) =>
        OutputDirectory is null ? window : window.SetTemporaryFilesPath(OutputDirectory);
}