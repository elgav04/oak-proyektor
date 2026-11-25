using System.Globalization;
using System.Reflection;

namespace OakProyektor.Data.Migrations;

/// <summary>Una migración numerada: el script SQL embebido <c>NNNN_Nombre.sql</c>.</summary>
/// <summary>Un paso de CÓDIGO de una migración: corre dentro de su misma transacción, justo después del SQL (para reglas que viven en C#). Devuelve un resumen SOLO con conteos (se registra).</summary>
public delegate Task<string?> MigrationStep(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, CancellationToken cancellationToken);

public sealed record Migration(int Version, string Name, string Sql, MigrationStep? After = null)
{
    private const string ResourceMarker = ".Migrations.";

    /// <summary>Pasos de código por versión. Una vez publicada, la migración (SQL y paso) no se toca: para corregirla se añade otra.</summary>
    private static readonly IReadOnlyDictionary<int, MigrationStep> CodeSteps = new Dictionary<int, MigrationStep> { [10] = Songs.SongSectionRepair.RunAsync };

    /// <summary>Carga y valida (versiones consecutivas desde 1) las migraciones embebidas del ensamblado.</summary>
    public static IReadOnlyList<Migration> LoadEmbedded(Assembly assembly)
    {
        var list = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var at = resource.IndexOf(ResourceMarker, StringComparison.Ordinal);
            if (at < 0 || !resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) continue;

            var file = resource[(at + ResourceMarker.Length)..^4];          // "0001_Initial"
            var underscore = file.IndexOf('_');
            var number = underscore < 0 ? file : file[..underscore];
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
                throw new InvalidOperationException($"Nombre de migración inválido: {resource}");

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"No se pudo leer la migración {resource}");
            using var reader = new StreamReader(stream);
            list.Add(new Migration(version, underscore < 0 ? file : file[(underscore + 1)..], reader.ReadToEnd(), CodeSteps.GetValueOrDefault(version)));
        }

        list.Sort(static (a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Version != i + 1)
                throw new InvalidOperationException($"Las migraciones deben ser consecutivas desde 1; falta la {i + 1}.");
        }
        return list;
    }
}
