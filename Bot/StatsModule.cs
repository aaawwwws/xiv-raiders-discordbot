using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<StatsModule> _logger;

    public StatsModule(
        StatsEmbedService stats,
        VoiceStatsService voiceStats,
        Db db,
        IOptions<DiscordOptions> options,
        ILogger<StatsModule> logger)
    {
        _stats = stats;
        _voiceStats = voiceStats;
        _db = db;
        _options = options.Value;
        _logger = logger;
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

    [SlashCommand("story", "ストーリー進行度の分布を円グラフで設置・更新します（以後リアルタイム更新）")]
    public async Task StoryStatsAsync()
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

        try
        {
            var embed = await _stats.BuildStoryProgressionEmbedAsync(Context.Guild.Id);
            var chartPng = await _stats.BuildStoryProgressionChartAsync(Context.Guild.Id);

            var existing = await _db.GetGuildStoryStatsPanelAsync(Context.Guild.Id);
            ulong? existingMessageId = existing is not null && existing.ChannelId == ToDbId(channel.Id)
                ? ToUlong(existing.MessageId)
                : null;

            IUserMessage message;
            if (chartPng is null || chartPng.Length == 0)
            {
                message = await EnsureMessageAsync(channel, existingMessageId, embed, null);
            }
            else
            {
                var stream = new MemoryStream(chartPng);
                message = await EnsureMessageAsync(channel, existingMessageId, embed, stream);
            }

            await _db.UpsertGuildStoryStatsPanelAsync(Context.Guild.Id, channel.Id, message.Id);
            await FollowupAsync(
                $"ストーリー進行度統計を設定しました。\n対象: <#{channel.Id}>\nメンバー登録更新時に自動更新されます。",
                ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate story progression chart. GuildId={GuildId}", Context.Guild.Id);
            await FollowupAsync("統計画像の生成に失敗しました。", ephemeral: true);
        }
    }

    private async Task<IUserMessage> EnsureMessageAsync(
        IMessageChannel channel,
        ulong? existingMessageId,
        Embed embed,
        MemoryStream? chartStream)
    {
        IUserMessage? existing = null;
        if (existingMessageId.HasValue)
        {
            try
            {
                existing = await channel.GetMessageAsync(existingMessageId.Value) as IUserMessage;
            }
            catch { }
        }

        if (existing is not null)
        {
            if (chartStream is not null)
            {
                await existing.ModifyAsync(props =>
                {
                    props.Content = null;
                    props.Embed = embed;
                    props.Attachments = new Optional<IEnumerable<FileAttachment>>(new[]
                    {
                        new FileAttachment(chartStream, "story_progression_chart.png")
                    });
                });
            }
            else
            {
                await existing.ModifyAsync(props =>
                {
                    props.Content = null;
                    props.Embed = embed;
                });
            }
            return existing;
        }

        if (chartStream is not null)
        {
            return await channel.SendFileAsync(
                chartStream,
                "story_progression_chart.png",
                embed: embed,
                flags: MessageFlags.SuppressNotification);
        }

        return await channel.SendMessageAsync(
            embed: embed,
            flags: MessageFlags.SuppressNotification);
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
    private static long ToDbId(ulong value) => unchecked((long)value);
}
