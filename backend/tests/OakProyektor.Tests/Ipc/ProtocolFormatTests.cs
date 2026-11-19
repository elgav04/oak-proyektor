using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OakProyektor.Core.Database;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.Ipc;

/// <summary>
/// Contrato del protocolo, parte 2 (la parte 1 — versión, catálogo y nombres de campo — está en <see cref="ProtocolContractTests"/>):
/// <list type="number">
/// <item><b>Tipos y nulabilidad</b> de cada campo, leídos por reflexión de los records de C# (con sus anotaciones de nulabilidad) y comparados con
/// las interfaces de <c>protocol.ts</c>. Regla por dirección: lo que C# ENVÍA (eventos/replies) debe coincidir exactamente (si C# puede mandar
/// <c>null</c>, TS debe admitirlo; si TS lo admite pero C# nunca lo manda, también es una deriva); lo que el panel ENVÍA (comandos) no puede
/// admitir <c>null</c> donde C# no lo admite (sí puede ser más estricto, p. ej. C# tolera <c>reason</c> ausente y TS siempre lo manda).</item>
/// <item><b>Huella del formato fijada por versión</b>: si cambia el formato de cualquier mensaje y no se sube <c>ProtocolVersion</c>, el test falla.
/// Igual que <c>MigrationHashTests</c>: al subir la versión se AÑADE la huella nueva; la de una versión ya publicada no se toca.</item>
/// <item><b>Cobertura</b>: todo record de <c>OakProyektor.Ipc.Messages</c> debe estar comparado; un payload nuevo sin par falla.</item>
/// </list>
/// LÍMITE: el formato es la FORMA de los datos. Un cambio de significado con la misma forma (p. ej. «revision» pasa a contar otra cosa) no se
/// detecta: ese caso se resuelve a mano subiendo la versión.
/// </summary>
public partial class ProtocolFormatTests
{
    private enum Direction { FromBackend, ToBackend }

    private sealed record Pair(Type Cs, string Ts, Direction Dir);
    private sealed record Field(string Name, string Core, bool Nullable);

    /// <summary>Un payload por interfaz de protocol.ts. Los comandos van de la UI a C#; el resto, de C# a la UI.</summary>
    private static readonly Pair[] Pairs =
    [
        new(typeof(OutputOpenPayload), "OutputOpenPayload", Direction.ToBackend),
        new(typeof(OutputSetMasterPayload), "OutputSetMasterPayload", Direction.ToBackend),
        new(typeof(OutputRecoverPayload), "OutputRecoverPayload", Direction.ToBackend),
        new(typeof(SetAutoRestorePayload), "SetAutoRestorePayload", Direction.ToBackend),
        new(typeof(SetDisplayLostPolicyPayload), "SetDisplayLostPolicyPayload", Direction.ToBackend),
        new(typeof(DisplayInfo), "DisplayInfo", Direction.FromBackend),
        new(typeof(OutputState), "OutputState", Direction.FromBackend),
        new(typeof(DisplayListPayload), "DisplayListPayload", Direction.FromBackend),
        new(typeof(OutputWindowStatus), "OutputWindowStatus", Direction.FromBackend),
        new(typeof(OutputStateChangedPayload), "OutputStateChangedPayload", Direction.FromBackend),
        new(typeof(DatabaseStatusPayload), "DatabaseStatusPayload", Direction.FromBackend),
        new(typeof(CommandFailedPayload), "CommandFailedPayload", Direction.FromBackend),
        new(typeof(IpcError), "IpcErrorPayload", Direction.FromBackend),
        new(typeof(IpcReply), "IpcReplyFields", Direction.FromBackend),
    ];

    /// <summary>Alias de tipo de protocol.ts → enum de C# que representa. <c>RecoverReason</c> es un SUBCONJUNTO de <c>OutputPauseReason</c> a propósito.</summary>
    private static readonly Dictionary<string, Type> TsEnumAliases = new()
    {
        ["MasterMode"] = typeof(MasterMode),
        ["DatabaseState"] = typeof(DatabaseState),
        ["OutputPauseReason"] = typeof(OutputPauseReason),
        ["RecoverReason"] = typeof(OutputPauseReason),
        ["DisplayLostPolicy"] = typeof(DisplayLostPolicy),
    };

    private static readonly Type[] WireEnums = [typeof(MasterMode), typeof(DatabaseState), typeof(OutputPauseReason), typeof(DisplayLostPolicy)];

    /// <summary>Nombre en protocol.ts de los tipos de C# cuyo nombre difiere.</summary>
    private static string TsNameOf(Type t) => t == typeof(IpcError) ? "IpcErrorPayload" : t.Name;

    /// <summary>
    /// Huella fijada del formato, por versión del protocolo. Para subir la versión: sube <c>IpcCatalog.ProtocolVersion</c> y <c>PROTOCOL_VERSION</c> y AÑADE aquí
    /// la huella nueva (el mensaje de fallo la calcula). La de una versión ya publicada NO se cambia: si cambia el formato, se crea una versión nueva.
    /// </summary>
    private static readonly Dictionary<int, string> PinnedFormat = new()
    {
        [3] = "efee82b9a3b116fe10d0d29f9bc49cc8e4b5c70be8636fbe26a5986846b94d0b",
        [4] = "2acd299b86c38d491db90fe708f4ab670a8a583ba376229ceda88a8440ae8318",
    };

    // ───────────────────────────── lectura de C# ─────────────────────────────

    private static string JsonName(string parameterName) => JsonNamingPolicy.CamelCase.ConvertName(parameterName);

    private static (string Core, bool Nullable) DescribeCs(Type type, NullabilityInfo info)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null) return (DescribeCs(underlying, info).Core, true);

        var nullable = info.ReadState == NullabilityState.Nullable;
        if (type == typeof(string)) return ("string", nullable);
        if (type == typeof(bool)) return ("boolean", nullable);
        if (type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return ("number", nullable);
        if (type == typeof(object)) return ("unknown", nullable);
        if (type.IsEnum) return ($"enum:{type.Name}", nullable);
        if (type.IsGenericType && type.GetGenericTypeDefinition() is var def
            && (def == typeof(IReadOnlyList<>) || def == typeof(List<>) || def == typeof(IEnumerable<>) || def == typeof(IReadOnlyCollection<>)))
        {
            var (inner, innerNullable) = DescribeCs(type.GetGenericArguments()[0], info.GenericTypeArguments[0]);
            return ($"{inner}{(innerNullable ? "?" : "")}[]", nullable);
        }
        return ($"ref:{TsNameOf(type)}", nullable);
    }

    private static List<Field> CsFields(Type type)
    {
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var context = new NullabilityInfoContext();
        return [.. ctor.GetParameters().Select(p =>
        {
            var (core, nullable) = DescribeCs(p.ParameterType, context.Create(p));
            return new Field(JsonName(p.Name!), core, nullable);
        })];
    }

    // ───────────────────────────── lectura de protocol.ts ─────────────────────────────

    [GeneratedRegex(@"export interface (\w+) \{([^}]*)\}")]
    private static partial Regex InterfaceRegex();

    [GeneratedRegex(@"^\s*(\w+)(\??)\s*:\s*([^;]+);", RegexOptions.Multiline)]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"export type (\w+) = ((?:""[^""]+""\s*\|?\s*)+);")]
    private static partial Regex LiteralUnionRegex();

    private static string NormalizeTsAtom(string atom)
    {
        if (atom.EndsWith("[]", StringComparison.Ordinal)) return NormalizeTsAtom(atom[..^2]) + "[]";
        return atom switch
        {
            "string" => "string",
            "number" => "number",
            "boolean" => "boolean",
            "unknown" => "unknown",
            _ when TsEnumAliases.TryGetValue(atom, out var e) => $"enum:{e.Name}",
            _ => $"ref:{atom}",
        };
    }

    private static (string Core, bool Nullable) NormalizeTs(string expression, bool optional)
    {
        var parts = expression.Split('|').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        var nullable = parts.Remove("null") | optional;
        return (string.Join("|", parts.Select(NormalizeTsAtom).Distinct().Order()), nullable);
    }

    private static List<Field>? TsFields(string tsSource, string interfaceName)
    {
        var match = InterfaceRegex().Matches(tsSource).FirstOrDefault(m => m.Groups[1].Value == interfaceName);
        if (match is null) return null;
        return [.. FieldRegex().Matches(match.Groups[2].Value).Select(m =>
        {
            var (core, nullable) = NormalizeTs(m.Groups[3].Value, optional: m.Groups[2].Value == "?");
            return new Field(m.Groups[1].Value, core, nullable);
        })];
    }

    // ───────────────────────────── comparación ─────────────────────────────

    /// <summary>Todas las diferencias entre los records de C# y el protocol.ts dado (vacío = coinciden).</summary>
    internal static List<string> FindMismatches(string tsSource)
    {
        var problems = new List<string>();
        foreach (var pair in Pairs)
        {
            var ts = TsFields(tsSource, pair.Ts);
            if (ts is null) { problems.Add($"{pair.Ts}: protocol.ts no declara la interfaz"); continue; }
            var cs = CsFields(pair.Cs);

            var onlyCs = cs.Select(f => f.Name).Except(ts.Select(f => f.Name)).ToList();
            var onlyTs = ts.Select(f => f.Name).Except(cs.Select(f => f.Name)).ToList();
            if (onlyCs.Count > 0) problems.Add($"{pair.Ts}: campos solo en C#: {string.Join(", ", onlyCs)}");
            if (onlyTs.Count > 0) problems.Add($"{pair.Ts}: campos solo en TypeScript: {string.Join(", ", onlyTs)}");

            foreach (var c in cs)
            {
                if (ts.FirstOrDefault(f => f.Name == c.Name) is not { } t) continue;
                if (c.Core != t.Core) problems.Add($"{pair.Ts}.{c.Name}: tipo distinto (C# {c.Core}, TypeScript {t.Core})");
                if (c.Core == "unknown") continue;   // «cualquier cosa» ya incluye null

                if (pair.Dir == Direction.FromBackend && c.Nullable != t.Nullable)
                    problems.Add(c.Nullable
                        ? $"{pair.Ts}.{c.Name}: C# puede enviar null y TypeScript no lo admite"
                        : $"{pair.Ts}.{c.Name}: TypeScript admite null pero C# nunca lo envía (deriva)");
                if (pair.Dir == Direction.ToBackend && t.Nullable && !c.Nullable)
                    problems.Add($"{pair.Ts}.{c.Name}: TypeScript puede enviar null y C# no lo admite");
            }
        }

        // Alias que son una unión de literales (p. ej. RecoverReason): cada literal debe existir en el enum de C# al que corresponde.
        foreach (Match m in LiteralUnionRegex().Matches(tsSource))
        {
            if (!TsEnumAliases.TryGetValue(m.Groups[1].Value, out var enumType)) continue;
            var known = Enum.GetNames(enumType).Select(JsonName).ToHashSet();
            foreach (var literal in Regex.Matches(m.Groups[2].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).Where(l => !known.Contains(l)))
                problems.Add($"{m.Groups[1].Value}: «{literal}» no existe en {enumType.Name} de C#");
        }
        return problems;
    }

    // ───────────────────────────── huella del formato ─────────────────────────────

    /// <summary>Descripción canónica y legible del formato completo (también sirve para ver QUÉ cambió cuando falla la huella).</summary>
    private static string DescribeFormat(IEnumerable<Pair>? pairs = null)
    {
        var sb = new StringBuilder();
        var envelope = IpcEnvelope.Create(IpcKind.Command, IpcTypes.OutputClose, new EmptyPayload(), id: "x");
        using (var doc = JsonDocument.Parse(IpcSerializer.Serialize(envelope)))
            sb.AppendLine("envelope: " + string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name)));

        foreach (var line in IpcCatalog.Messages.Select(m => $"message {m.Kind} {m.Type}").Order(StringComparer.Ordinal)) sb.AppendLine(line);
        foreach (var pair in (pairs ?? Pairs).OrderBy(p => p.Cs.Name, StringComparer.Ordinal))
            sb.AppendLine($"payload {pair.Cs.Name} ({pair.Dir}): " +
                          string.Join(", ", CsFields(pair.Cs).OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => $"{f.Name}: {f.Core}{(f.Nullable ? " | null" : "")}")));
        foreach (var e in WireEnums.OrderBy(e => e.Name, StringComparer.Ordinal))
            sb.AppendLine($"enum {e.Name}: " + string.Join(", ", Enum.GetNames(e).Select(JsonName).Order(StringComparer.Ordinal)));
        return sb.ToString();
    }

    private static string Fingerprint(IEnumerable<Pair>? pairs = null) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DescribeFormat(pairs)))).ToLowerInvariant();

    // ───────────────────────────── pruebas ─────────────────────────────

    private static string TsSource => File.ReadAllText(FindProtocolFile());

    private static string FindProtocolFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "frontend", "src", "ipc", "protocol.ts");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("No se encontró frontend/src/ipc/protocol.ts subiendo desde " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Field_types_and_nullability_match_between_csharp_and_typescript()
    {
        var problems = FindMismatches(TsSource);
        Assert.True(problems.Count == 0, "C# y protocol.ts divergen:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_message_record_is_compared_so_a_new_payload_cannot_be_forgotten()
    {
        var compared = Pairs.Select(p => p.Cs).ToHashSet();
        var forgotten = typeof(OutputOpenPayload).Assembly.GetTypes()
            .Where(t => t.Namespace == "OakProyektor.Ipc.Messages" && t.IsPublic && !t.IsAbstract && t != typeof(EmptyPayload)
                        && t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null)
            .Where(t => !compared.Contains(t))
            .Select(t => t.Name)
            .ToList();
        Assert.True(forgotten.Count == 0, "Payloads sin comparar con protocol.ts (añádelos a Pairs): " + string.Join(", ", forgotten));
    }

    [Fact]
    public void The_wire_format_matches_the_one_pinned_for_this_protocol_version()
    {
        var version = IpcCatalog.ProtocolVersion;
        var current = Fingerprint();

        if (!PinnedFormat.TryGetValue(version, out var pinned))
            Assert.Fail($"La versión {version} del protocolo no tiene huella de formato fijada. Añade en PinnedFormat: [{version}] = \"{current}\".{Environment.NewLine}Formato actual:{Environment.NewLine}{DescribeFormat()}");

        Assert.True(pinned == current,
            $"El FORMATO de los mensajes cambió sin subir la versión del protocolo (v{version}). Sube IpcCatalog.ProtocolVersion y PROTOCOL_VERSION (CLAUDE.md: " +
            $"«Cambia v cuando cambie el formato de cualquier mensaje») y añade la huella nueva; NO cambies la de una versión ya publicada." +
            $"{Environment.NewLine}Huella fijada: {pinned}{Environment.NewLine}Huella actual:  {current}{Environment.NewLine}Formato actual:{Environment.NewLine}{DescribeFormat()}");
    }

    // ───────────────────────────── el propio comprobador detecta deriva (no es vacío) ─────────────────────────────

    [Fact]
    public void The_baseline_has_no_mismatches_so_the_mutations_below_prove_something()
    {
        Assert.Empty(FindMismatches(TsSource));
    }

    [Theory]
    // tipo distinto
    [InlineData("  width: number;", "  width: string;", "DisplayInfo.width: tipo distinto")]
    [InlineData("  seq: number;\n  displays:", "  seq: string;\n  displays:", "DisplayListPayload.seq: tipo distinto")]
    [InlineData("  displays: DisplayInfo[];", "  displays: DisplayInfo;", "DisplayListPayload.displays: tipo distinto")]
    [InlineData("  master: MasterMode;\n  backgroundColor", "  master: string;\n  backgroundColor", "OutputState.master: tipo distinto")]
    // C# puede enviar null y TypeScript no lo admite
    [InlineData("  displayId: string | null;\n  isWindowed", "  displayId: string;\n  isWindowed", "OutputWindowStatus.displayId: C# puede enviar null y TypeScript no lo admite")]
    [InlineData("  pauseReason: OutputPauseReason | null;", "  pauseReason: OutputPauseReason;", "OutputWindowStatus.pauseReason: C# puede enviar null")]
    [InlineData("  text: string | null;", "  text: string;", "OutputState.text: C# puede enviar null")]
    // TypeScript admite null pero C# nunca lo envía
    [InlineData("  state: DatabaseState;", "  state: DatabaseState | null;", "DatabaseStatusPayload.state: TypeScript admite null pero C# nunca lo envía")]
    [InlineData("  id: string;\n  x: number;", "  id: string | null;\n  x: number;", "DisplayInfo.id: TypeScript admite null")]
    // comando: la UI no puede enviar null donde C# no lo admite
    [InlineData("  master: MasterMode;\n}", "  master: MasterMode | null;\n}", "OutputSetMasterPayload.master: TypeScript puede enviar null y C# no lo admite")]
    [InlineData("  enabled: boolean;", "  enabled: boolean | null;", "SetAutoRestorePayload.enabled: TypeScript puede enviar null")]
    // campos de más o de menos
    [InlineData("  hostsPanel: boolean;", "  hostsPanel: boolean;\n  extra: string;", "DisplayInfo: campos solo en TypeScript: extra")]
    [InlineData("  isPrimary: boolean;\n  hostsPanel", "  hostsPanel", "DisplayInfo: campos solo en C#: isPrimary")]
    // enum: literal que C# no conoce
    [InlineData("export type RecoverReason = \"manual\" | \"emergency\";", "export type RecoverReason = \"manual\" | \"panic\";", "«panic» no existe en OutputPauseReason")]
    // interfaz ausente
    [InlineData("export interface IpcErrorPayload {", "export interface IpcErrorPayloadX {", "IpcErrorPayload: protocol.ts no declara la interfaz")]
    public void A_drift_in_protocol_ts_is_detected(string find, string replace, string expectedFragment)
    {
        var source = TsSource;
        Assert.Contains(find, source.Replace("\r\n", "\n"));   // la mutación debe aplicarse de verdad
        var mutated = source.Replace("\r\n", "\n").Replace(find, replace);

        var problems = FindMismatches(mutated);

        Assert.Contains(problems, p => p.Contains(expectedFragment));
    }

    [Fact]
    public void A_command_may_be_stricter_in_typescript_than_in_csharp_which_is_the_case_of_recover_reason()
    {
        // C# tolera reason nulo/ausente (lo trata como manual); TypeScript siempre lo manda: no es una divergencia.
        var cs = CsFields(typeof(OutputRecoverPayload)).Single();
        Assert.True(cs.Nullable);
        Assert.DoesNotContain(FindMismatches(TsSource), p => p.Contains("OutputRecoverPayload"));
    }

    [Fact]
    public void The_fingerprint_is_deterministic_and_changes_when_the_format_description_changes()
    {
        Assert.Equal(Fingerprint(), Fingerprint());
        // Quitar un payload del formato cambia la huella: la huella depende del contenido, no es una constante.
        Assert.NotEqual(Fingerprint(), Fingerprint(Pairs.Skip(1)));
        Assert.Contains("payload OutputOpenPayload (ToBackend): displayId: string | null", DescribeFormat());
        Assert.Contains("message command output.recover", DescribeFormat());
        Assert.Contains("envelope: v, id, kind, type, payload", DescribeFormat());
    }
}
