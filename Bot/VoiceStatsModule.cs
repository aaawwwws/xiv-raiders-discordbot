using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

[Group("voice", "VC統計")]
public sealed class VoiceStatsModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly VoiceStatsService _voiceStats;

    public VoiceStatsModule(VoiceStatsService voiceStats)
    {
        _voiceStats = voiceStats;
    }

    [RequireBotAdmin]
    [SlashCommand("weekly_rank", "直近N日間の週間アクティビティランキングを表示します")]
    public async Task WeeklyRankAsync([Summary("days", "集計日数(1-30)")] int days = 7)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var rangeDays = Math.Clamp(days, 1, 30);
        await DeferAsync(ephemeral: false);
        var embed = await _voiceStats.BuildWeeklyRankingEmbedAsync(Context.Guild, DateTimeOffset.UtcNow, rangeDays, topCount: 10);
        await FollowupAsync(embed: embed);
    }
}
