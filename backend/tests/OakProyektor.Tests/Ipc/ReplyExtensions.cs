using System.Text.Json;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.Ipc;

internal static class ReplyExtensions
{
    /// <summary>Lee <c>payload.result</c> de un reply v2 <c>{ ok, result, error }</c>.</summary>
    public static T ReplyResult<T>(this IpcEnvelope reply) where T : class =>
        reply.Payload.GetProperty("result").Deserialize<T>(IpcSerializer.Options)!;

    public static IpcError ReplyError(this IpcEnvelope reply) =>
        reply.Payload.GetProperty("error").Deserialize<IpcError>(IpcSerializer.Options)!;
}
