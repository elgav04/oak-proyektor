using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using OakProyektor.Data;

namespace OakProyektor.Tests.Data;

/// <summary>
/// M6: fija el contenido de cada migración. Una migración publicada NO se edita nunca: quien ya la aplicó no volvería a ejecutarla,
/// así que la edición dejaría dos esquemas distintos con el mismo número de versión sin que nada lo notara.
/// </summary>
public class MigrationHashTests
{
    /// <summary>
    /// Para añadir una migración NUEVA: agrega aquí su número y su hash (el mensaje de fallo lo calcula). Para corregir una
    /// migración ya publicada NO cambies este hash: crea una migración nueva que arregle lo anterior.
    /// </summary>
    private static readonly Dictionary<int, string> Pinned = new()
    {
        [1] = "31a1a440da862527b16a2de648d925ee272888829d30d3909ce5ca385e57d145",
        [2] = "011da5ddbf1c0a00f8c49144f2816432a4ca93171034c146e1cc68742e90eeb7",
        [3] = "f998f18ce6e2d2989c9f395c7ecf462ff0a32167bb910948cd55c9a404160885",
    };

    private static string Hash(string sql)
    {
        // Normaliza saltos de línea: git en Windows puede convertir LF en CRLF y el hash no debe depender de eso.
        var normalized = sql.Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    [Fact]
    public void Every_migration_matches_its_pinned_hash_and_none_is_unpinned()
    {
        var actual = OakProyektor.Data.Migrations.Migration.LoadEmbedded(typeof(DatabaseInitializer).Assembly)
            .ToDictionary(m => m.Version, m => Hash(m.Sql));

        var problems = new List<string>();
        foreach (var (version, hash) in actual)
        {
            if (!Pinned.TryGetValue(version, out var pinned))
                problems.Add($"La migración {version} no tiene hash fijado. Si es nueva, agrégalo: [{version}] = \"{hash}\".");
            else if (pinned != hash)
                problems.Add($"La migración {version} CAMBIÓ (esperado {pinned}, actual {hash}). Una migración publicada no se edita: crea una migración nueva.");
        }
        foreach (var version in Pinned.Keys.Except(actual.Keys))
            problems.Add($"La migración {version} tiene hash fijado pero ya no existe: no se borran migraciones publicadas.");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_hash_ignores_line_ending_differences()
    {
        Assert.Equal(Hash("a\nb\n"), Hash("a\r\nb\r\n"));
        Assert.NotEqual(Hash("a\nb\n"), Hash("a\nB\n"));
    }
}