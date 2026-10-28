using System.Collections.Concurrent;

namespace Kautuka.Gateway.Domain.Assets;

/// <summary>
/// Holds an image from a mail attachment until the next model call.
/// A tool result is text, so the picture is added as image content on that call.
/// </summary>
internal sealed class MailImageHandoff
{
    private readonly ConcurrentDictionary<string, Pending> pending = new(StringComparer.Ordinal);

    public void Hold(string toolCallId, string name, string mediaType, byte[] bytes)
    {
        pending[toolCallId] = new Pending(name, mediaType, bytes);
    }

    public bool TryTake(string toolCallId, out Pending image)
        => pending.TryRemove(toolCallId, out image!);

    internal readonly record struct Pending(string Name, string MediaType, byte[] Bytes);
}
