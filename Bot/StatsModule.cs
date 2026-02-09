using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

[Group("stats", "統計")]
public sealed class StatsModule : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly TimeSpan JapanTimeOffset = TimeSpan.FromHours(9);
    private readonly StatsEmbedService _stats;
    private readonly VoiceStatsService _voiceStats;
    private readonly Db _db;
    private readonly DiscordOptions _options;

    public StatsModule(
        StatsEmbedService stats,
        VoiceStatsService voiceStats,
        Db db,
        IOptions<DiscordOptions> options)
    {
        _stats = stats;
        _voiceStats = voiceStats;
        _db = db;
        _options = options.Value;
    }

    [RequireBotAdmin]
    [SlashCommand("population", "このチャンネルにDC/サーバー人口分布を設置・更新します（以後リアルタイム更新）")]
    public async Task PopulationAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("このチャンネルには投稿できません。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var result = await _stats.SetupAndRefreshAsync(Context.Guild, channel);
        var fallbackText = result.UsedFallback ? "（フォールバックあり）" : string.Empty;
        await FollowupAsync(
            $"統計EMBEDを更新しました。対象登録数: {result.RegisteredCount} / 集計元: {result.Source} {fallbackText}\n" +
            "このサーバーのメンバー登録更新・参加/退出時に自動更新されます。",
            ephemeral: true);
    }

    [RequireBotAdmin]
    [SlashCommand("ranking", "週間アクティビティランキングを表示します")]
    public async Task RankingAsync([Summary("days", "集計日数(1-30)")] int days = 7)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var rangeDays = Math.Clamp(days, 1, 30);
        await DeferAsync(ephemeral: false);
        var embed = await _voiceStats.BuildWeeklyRankingEmbedAsync(Context.Guild, DateTimeOffset.UtcNow, rangeDays, topCount: 10);
        await FollowupAsync(embed: embed);
    }

    [RequireBotAdmin]
    [SlashCommand("today", "今日ランキングを作成/更新し、自動更新対象として登録します")]
    public async Task TodayRankingSetupAsync([Summary("top", "表示件数(1-20)")] int top = 10)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("このチャンネルには投稿できません。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var nowUtc = DateTimeOffset.UtcNow;
        var nowJst = nowUtc.ToOffset(JapanTimeOffset);
        var fromUtc = new DateTimeOffset(nowJst.Year, nowJst.Month, nowJst.Day, 0, 0, 0, JapanTimeOffset).ToUniversalTime();
        var topCount = Math.Clamp(top, 1, 20);
        var embed = await _voiceStats.BuildActivityRankingEmbedAsync(Context.Guild, fromUtc, nowUtc, topCount, "今日のアクティビティランキング");

        var panel = await _db.GetGuildDailyActivityPanelAsync(Context.Guild.Id);
        ulong? existingMessageId = null;
        if (panel is not null && panel.ChannelId == ToDbId(channel.Id) && panel.MessageId != 0)
        {
            existingMessageId = ToUlong(panel.MessageId);
        }

        var message = await EnsureTodayRankingMessageAsync(channel, existingMessageId, embed);
        await _db.UpsertGuildDailyActivityPanelAsync(Context.Guild.Id, channel.Id, message.Id);

        var intervalMinutes = Math.Clamp(_options.DailyActivityRankingUpdateIntervalMinutes, 5, 180);
        await FollowupAsync(
            $"今日ランキングの自動更新メッセージを設定しました。\n対象: <#{channel.Id}> / message: {message.Id}\n更新間隔: {intervalMinutes}分",
            ephemeral: true);
    }

    private async Task<IUserMessage> EnsureTodayRankingMessageAsync(IMessageChannel channel, ulong? existingMessageId, Embed embed)
    {
        IUserMessage? existing = null;
        if (existingMessageId.HasValue)
        {
            try
            {
                existing = await channel.GetMessageAsync(existingMessageId.Value) as IUserMessage;
            }
            catch
            {
            }
        }

        if (existing is null)
        {
            try
            {
                var recent = await channel.GetMessagesAsync(50).FlattenAsync();
                existing = recent
                    .OfType<IUserMessage>()
                    .FirstOrDefault(message =>
                        message.Author.Id == Context.Client.CurrentUser.Id &&
                        string.Equals(message.Content, ActivityRankingPanel.DailyMessageHeader, StringComparison.Ordinal));
            }
            catch
            {
            }
        }

        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = ActivityRankingPanel.DailyMessageHeader;
                props.Embed = embed;
            });
            return existing;
        }

        return await channel.SendMessageAsync(
            text: ActivityRankingPanel.DailyMessageHeader,
            embed: embed,
            flags: MessageFlags.SuppressNotification);
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
    private static long ToDbId(ulong value) => unchecked((long)value);
}
