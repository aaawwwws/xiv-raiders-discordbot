using System.Collections.Concurrent;

namespace RaidDcBot.Bot;

public sealed class VoiceTransferTracker
{
    private readonly ConcurrentDictionary<ulong, ulong> _messageIds = new();

    public bool TryGet(ulong channelId, out ulong messageId)
        => _messageIds.TryGetValue(channelId, out messageId);

    public void Set(ulong channelId, ulong messageId)
        => _messageIds[channelId] = messageId;

    public bool TryRemove(ulong channelId, out ulong messageId)
        => _messageIds.TryRemove(channelId, out messageId);
}
