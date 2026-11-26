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
        [4] = "dc653ddd84c0e6b3ff6c507e2bdb8e525efa0f190442ca1e41afc150d9547d7a",
        [5] = "1f1887df03c356b2b2912c53c94877907753be396c9d791aedab5baff89722e5",
        [6] = "ea0c24e13859e6cb8d2f808a328cc25f20079cb9e93e0440d461b609461ffbaa",
        [7] = "31ae79ccb55ebc1f395aeaf2475e4e98315bd6e1c7976a3029f1c831ce125f27",
        [8] = "6fb40321d7fdf131b78448e9fc093c6e26b3f5d17809ed73994eecbb269cbb34",
        [9] = "dc9f1615bee70d2c4bc357b0a5a17e2f8cb1fb1743b61977deb3a868405d454f",
        [10] = "c516442ef5e0108fb88dfe51d0618e7d1a30c627b848aee410b48ac490357aa6",
        [11] = "f9550cf386795cd4ae4c0159230ea333e8c74248c94333d48525078bacf92504",
        [12] = "23deb7a72105bd0bfa76b50286560631a0e62aeb4718b75aeeedb854f61b0125",
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