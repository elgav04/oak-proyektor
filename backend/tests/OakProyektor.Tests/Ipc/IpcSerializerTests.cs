using System.Text.Json;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.Ipc;

public class IpcSerializerTests
{
    private static IpcEnvelope Parse(string json)
    {
        Assert.True(IpcSerializer.TryParse(json, out var envelope, out var error), error);
        return envelope!;
    }

    [Fact]
    public void Envelope_round_trips_with_camel_case_names()
    {
        var original = IpcEnvelope.Create(IpcKind.Command, IpcTypes.OutputSetMaster, new OutputSetMasterPayload(MasterMode.ClearText), "abc-123");

        var json = IpcSerializer.Serialize(original);
        var parsed = Parse(json);

        Assert.Equal((1, "abc-123", "command", "output.setMaster"), (parsed.V, parsed.Id, parsed.Kind, parsed.Type));
        Assert.Equal(MasterMode.ClearText, parsed.GetPayload<OutputSetMasterPayload>().Master);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["v", "id", "kind", "type", "payload"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("clearText", doc.RootElement.GetProperty("payload").GetProperty("master").GetString());
    }

    [Theory]
    [InlineData(MasterMode.Live, "live")]
    [InlineData(MasterMode.ClearText, "clearText")]
    [InlineData(MasterMode.Black, "black")]
    [InlineData(MasterMode.Logo, "logo")]
    [InlineData(MasterMode.Freeze, "freeze")]
    public void Master_modes_use_the_documented_names(MasterMode mode, string name)
    {
        var json = JsonSerializer.Serialize(new OutputSetMasterPayload(mode), IpcSerializer.Options);
        Assert.Equal($"{{\"master\":\"{name}\"}}", json);
    }

    [Fact]
    public void Numeric_or_unknown_enum_values_are_rejected()
    {
        var env = Parse("""{"v":1,"id":"1","kind":"command","type":"output.setMaster","payload":{"master":2}}""");
        Assert.Throws<JsonException>(() => env.GetPayload<OutputSetMasterPayload>());

        var env2 = Parse("""{"v":1,"id":"1","kind":"command","type":"output.setMaster","payload":{"master":"bogus"}}""");
        Assert.Throws<JsonException>(() => env2.GetPayload<OutputSetMasterPayload>());
    }

    [Fact]
    public void Nulls_are_written_explicitly()
    {
        Assert.Equal("""{"displayIndex":null}""", JsonSerializer.Serialize(new OutputOpenPayload(null), IpcSerializer.Options));
        Assert.Equal("{}", JsonSerializer.Serialize(new EmptyPayload(), IpcSerializer.Options));
    }

    [Fact]
    public void Display_list_and_output_state_payloads_round_trip()
    {
        var displays = new DisplayListPayload([new DisplayInfo(0, 0, 0, 1920, 1080, 1.25, true, true), new DisplayInfo(1, 1920, 0, 1920, 1080, 1, false, false)]);
        var back = Parse(IpcSerializer.Serialize(IpcEnvelope.Create(IpcKind.Event, IpcTypes.DisplayChanged, displays))).GetPayload<DisplayListPayload>();
        Assert.Equal(displays.Displays, back.Displays);

        var state = new OutputStateChangedPayload(OutputState.Initial.WithMaster(MasterMode.Live), new OutputWindowStatus(true, 1, false));
        var back2 = Parse(IpcSerializer.Serialize(IpcEnvelope.Create(IpcKind.Event, IpcTypes.OutputStateChanged, state))).GetPayload<OutputStateChangedPayload>();
        Assert.Equal(state, back2);
    }

    [Fact]
    public void Missing_payload_becomes_an_empty_object()
    {
        var env = Parse("""{"v":1,"id":"1","kind":"command","type":"output.close"}""");
        Assert.Equal(JsonValueKind.Object, env.Payload.ValueKind);
        Assert.Empty(env.Payload.EnumerateObject());
    }

    [Fact]
    public void Unknown_extra_fields_are_tolerated_for_forward_compatibility()
    {
        var env = Parse("""{"v":1,"id":"1","kind":"command","type":"output.open","payload":{"displayIndex":1,"future":true},"extra":5}""");
        Assert.Equal(1, env.GetPayload<OutputOpenPayload>().DisplayIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("""{"v":1,"kind":"command","type":"x"}""")]                       // sin id
    [InlineData("""{"v":1,"id":"1","type":"x"}""")]                               // sin kind
    [InlineData("""{"v":1,"id":"1","kind":"command"}""")]                         // sin type
    [InlineData("""{"v":2,"id":"1","kind":"command","type":"x","payload":{}}""")] // versión futura
    [InlineData("""{"id":"1","kind":"command","type":"x","payload":{}}""")]       // sin versión
    [InlineData("""{"v":"1","id":"1","kind":"command","type":"x"}""")]            // versión con tipo equivocado
    public void Invalid_messages_are_rejected_without_throwing(string? raw)
    {
        Assert.False(IpcSerializer.TryParse(raw, out var envelope, out var error));
        Assert.Null(envelope);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Oversized_messages_are_rejected()
    {
        var big = "{\"v\":1,\"id\":\"1\",\"kind\":\"command\",\"type\":\"x\",\"payload\":{\"a\":\"" + new string('x', IpcSerializer.MaxMessageChars) + "\"}}";
        Assert.False(IpcSerializer.TryParse(big, out _, out var error));
        Assert.Contains("demasiado grande", error);
    }
}


