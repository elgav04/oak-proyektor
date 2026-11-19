using System.Text.Json;
using System.Text.RegularExpressions;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.Ipc;

/// <summary>Falla si <c>frontend/src/ipc/protocol.ts</c> y los tipos de C# divergen.</summary>
public partial class ProtocolContractTests
{
    private static readonly string TsSource = File.ReadAllText(FindProtocolFile());

    private static string FindProtocolFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "frontend", "src", "ipc", "protocol.ts");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("No se encontró frontend/src/ipc/protocol.ts subiendo desde " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"PROTOCOL_VERSION\s*=\s*(\d+)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex("""\{\s*type:\s*"([^"]+)",\s*kind:\s*"([^"]+)"\s*\}""")]
    private static partial Regex CatalogEntryRegex();

    [GeneratedRegex(@"MASTER_MODES\s*=\s*\[([^\]]*)\]")]
    private static partial Regex MasterModesRegex();

    [GeneratedRegex(@"PAUSE_REASONS\s*=\s*\[([^\]]*)\]")]
    private static partial Regex PauseReasonsRegex();

    [GeneratedRegex(@"export interface (\w+) \{([^}]*)\}")]
    private static partial Regex InterfaceRegex();

    [GeneratedRegex(@"^\s*(\w+)\??\s*:", RegexOptions.Multiline)]
    private static partial Regex FieldRegex();

    private static string[] TsFields(string interfaceName)
    {
        var match = InterfaceRegex().Matches(TsSource).FirstOrDefault(m => m.Groups[1].Value == interfaceName)
            ?? throw new InvalidOperationException($"protocol.ts no declara la interfaz {interfaceName}");
        return FieldRegex().Matches(match.Groups[2].Value).Select(m => m.Groups[1].Value).Order().ToArray();
    }

    private static string[] JsonKeys(object sample)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(sample, sample.GetType(), IpcSerializer.Options));
        return doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
    }

    [Fact]
    public void Protocol_version_matches()
    {
        Assert.Equal(IpcCatalog.ProtocolVersion, int.Parse(VersionRegex().Match(TsSource).Groups[1].Value));
    }

    [Fact]
    public void Message_catalog_matches_exactly()
    {
        var ts = CatalogEntryRegex().Matches(TsSource).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).Order().ToArray();
        var cs = IpcCatalog.Messages.Select(m => (m.Type, m.Kind)).Order().ToArray();

        Assert.NotEmpty(ts);
        Assert.Equal(cs, ts);
    }

    [Fact]
    public void Every_type_constant_in_csharp_is_in_the_catalog()
    {
        var constants = typeof(IpcTypes).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
        var cataloged = IpcCatalog.Messages.Select(m => m.Type).ToHashSet();
        Assert.Equal(constants, cataloged);
    }

    [Fact]
    public void Master_modes_match_in_name_and_order()
    {
        var ts = Regex.Matches(MasterModesRegex().Match(TsSource).Groups[1].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        var cs = Enum.GetNames<MasterMode>().Select(n => JsonNamingPolicy.CamelCase.ConvertName(n)).ToArray();
        Assert.Equal(cs, ts);
    }

    [Fact]
    public void Pause_reasons_match_in_name_and_order()
    {
        var ts = Regex.Matches(PauseReasonsRegex().Match(TsSource).Groups[1].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        var cs = Enum.GetNames<OutputPauseReason>().Select(n => JsonNamingPolicy.CamelCase.ConvertName(n)).ToArray();
        Assert.Equal(cs, ts);
    }

    [Fact]
    public void Payload_interfaces_have_the_same_fields_as_the_serialized_csharp_types()
    {
        var display = new DisplayInfo("0,0,1x1", 0, 0, 1, 1, 1, true, true);
        var state = OutputState.Initial;
        var window = new OutputWindowStatus(true, "0,0,1x1", false, false, null, true, null);

        Assert.Equal(JsonKeys(display), TsFields("DisplayInfo"));
        Assert.Equal(JsonKeys(state), TsFields("OutputState"));
        Assert.Equal(JsonKeys(new OutputOpenPayload(null)), TsFields("OutputOpenPayload"));
        Assert.Equal(JsonKeys(new OutputSetMasterPayload(MasterMode.Live)), TsFields("OutputSetMasterPayload"));
        Assert.Equal(JsonKeys(new DisplayListPayload(1, [display])), TsFields("DisplayListPayload"));
        Assert.Equal(JsonKeys(window), TsFields("OutputWindowStatus"));
        Assert.Equal(JsonKeys(new SetAutoRestorePayload(true)), TsFields("SetAutoRestorePayload"));
        Assert.Equal(JsonKeys(new OutputStateChangedPayload(1, state, window)), TsFields("OutputStateChangedPayload"));
        Assert.Equal(JsonKeys(new IpcError("c", "m")), TsFields("IpcErrorPayload"));
        Assert.Equal(JsonKeys(new IpcReply(true, null, null)), TsFields("IpcReplyFields"));
        Assert.Equal(JsonKeys(new CommandFailedPayload("id", "t", new IpcError("c", "m"))), TsFields("CommandFailedPayload"));
    }
}




