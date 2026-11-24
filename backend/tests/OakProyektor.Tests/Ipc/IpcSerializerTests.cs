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

        Assert.Equal((IpcCatalog.ProtocolVersion, "abc-123", "command", "output.setMaster"), (parsed.V, parsed.Id, parsed.Kind, parsed.Type));
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
        var env = Parse("""{"v":11,"id":"1","kind":"command","type":"output.setMaster","payload":{"master":2}}""");
        Assert.Throws<JsonException>(() => env.GetPayload<OutputSetMasterPayload>());

        var env2 = Parse("""{"v":11,"id":"1","kind":"command","type":"output.setMaster","payload":{"master":"bogus"}}""");
        Assert.Throws<JsonException>(() => env2.GetPayload<OutputSetMasterPayload>());
    }

    [Fact]
    public void Nulls_are_written_explicitly()
    {
        Assert.Equal("""{"displayId":null}""", JsonSerializer.Serialize(new OutputOpenPayload(null), IpcSerializer.Options));
        Assert.Equal("{}", JsonSerializer.Serialize(new EmptyPayload(), IpcSerializer.Options));
    }

    [Fact]
    public void Display_list_and_output_state_payloads_round_trip()
    {
        var displays = new DisplayListPayload(7, [new DisplayInfo(DisplayId.From(0, 0, 1920, 1080), 0, 0, 1920, 1080, 1.25, true, true), new DisplayInfo(DisplayId.From(1920, 0, 1920, 1080), 1920, 0, 1920, 1080, 1, false, false)]);
        var back = Parse(IpcSerializer.Serialize(IpcEnvelope.Create(IpcKind.Event, IpcTypes.DisplayChanged, displays))).GetPayload<DisplayListPayload>();
        Assert.Equal(displays.Displays, back.Displays);
        Assert.Equal(7, back.Seq);

        var state = new OutputStateChangedPayload(42, OutputState.Initial.WithMaster(MasterMode.Live).WithBackground(new BackgroundLayer(BackgroundKind.Video, "#112233", "file:///C:/a%20b.mp4", "a b.mp4", true, 600, BackgroundLayer.RemovableDriveWarning)).WithSlide(new Slide("s1", [new TextBlock("Hola\nmundo")], TextTheme.Default), 400), new OutputWindowStatus(true, "1920,0,1920x1080", false, true, OutputPauseReason.DisplayLost, true, "Ctrl+Alt+Shift+M", "1920,0,1920x1080", DisplayLostPolicy.WarnAndWait, true, true, [new HotkeyStatus("black", "Ctrl+Alt+Shift+B", true), new HotkeyStatus("logo", "Ctrl+Alt+Shift+L", false)]));
        var back2 = Parse(IpcSerializer.Serialize(IpcEnvelope.Create(IpcKind.Event, IpcTypes.OutputStateChanged, state))).GetPayload<OutputStateChangedPayload>();
        // Las listas se comparan por contenido (un record con una lista no es igual a otro con una lista distinta aunque tenga lo mismo).
        // Las listas (hotkeys, bloques, superposiciones) se comparan por referencia: se comprueba la ida y vuelta por su JSON.
        Assert.Equal(JsonSerializer.Serialize(state, IpcSerializer.Options), JsonSerializer.Serialize(back2, IpcSerializer.Options));
        Assert.Equal(state.Window.Hotkeys, back2.Window.Hotkeys);

        // En el cable los enums nuevos van en camelCase, como los demás (el panel los compara con esos textos).
        var json = IpcSerializer.Serialize(IpcEnvelope.Create(IpcKind.Event, IpcTypes.OutputStateChanged,
            state with { Window = state.Window with { PauseReason = OutputPauseReason.DuplicateMode, OnDisplayLost = DisplayLostPolicy.MoveToBest } }));
        Assert.Contains("\"pauseReason\":\"duplicateMode\"", json);
        Assert.Contains("\"onDisplayLost\":\"moveToBest\"", json);
        Assert.Contains("\"preferredDisplayId\":\"1920,0,1920x1080\"", json);
    }

    [Fact]
    public void Missing_payload_becomes_an_empty_object()
    {
        var env = Parse("""{"v":11,"id":"1","kind":"command","type":"output.close"}""");
        Assert.Equal(JsonValueKind.Object, env.Payload.ValueKind);
        Assert.Empty(env.Payload.EnumerateObject());
    }

    [Fact]
    public void Unknown_extra_fields_are_tolerated_for_forward_compatibility()
    {
        var env = Parse("""{"v":11,"id":"1","kind":"command","type":"output.open","payload":{"displayId":"1920,0,1920x1080","future":true},"extra":5}""");
        Assert.Equal("1920,0,1920x1080", env.GetPayload<OutputOpenPayload>().DisplayId);
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
    [InlineData("""{"v":11,"kind":"command","type":"x"}""")]                       // sin id
    [InlineData("""{"v":11,"id":"1","type":"x"}""")]                               // sin kind
    [InlineData("""{"v":11,"id":"1","kind":"command"}""")]                         // sin type
    [InlineData("""{"v":119,"id":"1","kind":"command","type":"x","payload":{}}""")] // versión futura
    [InlineData("""{"v":2,"id":"1","kind":"command","type":"x","payload":{}}""")] // versión anterior (v2)
    [InlineData("""{"v":1,"id":"1","kind":"command","type":"x","payload":{}}""")] // versión anterior (v1)
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
        var big = "{\"v\":2,\"id\":\"1\",\"kind\":\"command\",\"type\":\"x\",\"payload\":{\"a\":\"" + new string('x', IpcSerializer.MaxMessageChars) + "\"}}";
        Assert.False(IpcSerializer.TryParse(big, out _, out var error));
        Assert.Contains("demasiado grande", error);
    }
}




public class IpcSerializerOptionsTests
{
    [Fact]
    public void The_shared_options_are_read_only_from_the_start_so_no_code_can_alter_them()
    {
        Assert.True(IpcSerializer.Options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => IpcSerializer.Options.PropertyNamingPolicy = null);
        Assert.Throws<InvalidOperationException>(() => IpcSerializer.Options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        Assert.Throws<InvalidOperationException>(() => IpcSerializer.Options.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString);
    }

    [Fact]
    public void A_freshly_created_instance_is_already_read_only_before_anything_has_used_it()
    {
        // La instancia compartida se vuelve de solo lectura sola al primer uso, así que otro test pudo haberla "protegido" ya.
        // Aquí se crea una nueva por reflexión: tiene que ser de solo lectura DESDE EL PRINCIPIO.
        var create = typeof(IpcSerializer).GetMethod("CreateOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var fresh = (System.Text.Json.JsonSerializerOptions)create.Invoke(null, null)!;

        Assert.True(fresh.IsReadOnly);
    }

    [Fact]
    public void Being_read_only_does_not_change_the_wire_format()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new OutputSetMasterPayload(MasterMode.ClearText), IpcSerializer.Options);
        Assert.Equal("{\"master\":\"clearText\"}", json);
    }
}
