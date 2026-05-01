using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Options;
using System.Globalization;
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

        var refreshTask = _stats.SetupAndRefreshAsync(Context.Guild, channel);
        var completed = await Task.WhenAny(refreshTask, Task.Delay(TimeSpan.FromSeconds(30)));
        if (completed != refreshTask)
        {
            await FollowupAsync(
                "統計EMBED更新がタイムアウトしました。しばらくして再実行してください。",
                ephemeral: true);
            return;
        }

        var result = await refreshTask;
        if (!result.Updated)
        {
            await FollowupAsync(
                $"統計EMBEDの更新に失敗しました。理由: {result.Source}\n" +
                "Botのチャンネル参照権限/送信権限を確認してください。",
                ephemeral: true);
            return;
        }

        await FollowupAsync(
            $"統計EMBEDを更新しました。対象登録数: {result.RegisteredCount} / 集計元: {result.Source}\n" +
            "このサーバーのメンバー登録更新・参加/退出時に自動更新されます。",
            ephemeral: true);
    }

    [RequireBotAdmin]
    [SlashCommand("week", "週間ランキングの自動投稿先をこのチャンネルに設定します")]
    public async Task SetupWeeklyRankingAsync()
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

        var existing = await _db.GetGuildWeeklyActivityPanelAsync(Context.Guild.Id);
        var lastMessageId = existing is null ? 0UL : ToUlong(existing.MessageId);
        await _db.UpsertGuildWeeklyActivityPanelAsync(Context.Guild.Id, channel.Id, lastMessageId);

        var day = ToJstDayName(Math.Clamp(_options.VoiceWeeklyRankingPostDayOfWeekJst, 0, 6));
        var hour = Math.Clamp(_options.VoiceWeeklyRankingPostHourJst, 0, 23);
        var rangeDays = Math.Clamp(_options.VoiceWeeklyRankingRangeDays, 1, 30);
        await FollowupAsync(
            $"週間ランキングの送信先を設定しました。\n対象: <#{channel.Id}>\n投稿タイミング: 毎週{day} {hour:00}:00 (JST)\n集計期間: 直前{rangeDays}日（確定分のみ）",
            ephemeral: true);
    }

    [RequireBotAdmin]
    [SlashCommand("today", "今日ランキングを作成/更新し、自動更新対象として登録します")]
    public async Task TodayRankingSetupAsync([Summary("top", "表示件数(1-10)")] int top = 10)
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
        var topCount = Math.Clamp(top, 1, 10);
        var embed = await _voiceStats.BuildActivityRankingEmbedAsync(Context.Guild, fromUtc, nowUtc, topCount, ActivityRankingPanel.DailyMessageHeader);

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
                        LooksLikeDailyRankingMessage(message));
            }
            catch
            {
            }
        }

        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = embed;
            });
            return existing;
        }

        return await channel.SendMessageAsync(
            embed: embed,
            flags: MessageFlags.SuppressNotification);
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
    private static long ToDbId(ulong value) => unchecked((long)value);

    private static bool LooksLikeDailyRankingMessage(IUserMessage message)
    {
        var title = message.Embeds.FirstOrDefault()?.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (DateTime.TryParseExact(title, "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return true;
        }

        const string suffix = "のランキング";
        if (!title.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var datePart = title[..^suffix.Length];
        return DateTime.TryParseExact(datePart, "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static string ToJstDayName(int dayOfWeek)
    {
        return dayOfWeek switch
        {
            0 => "日曜日",
            1 => "月曜日",
            2 => "火曜日",
            3 => "水曜日",
            4 => "木曜日",
            5 => "金曜日",
            6 => "土曜日",
            _ => "不明"
        };
    }
}
