using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

[Group("voice", "VC統計")]
public sealed class VoiceStatsModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly VcHourlyChartService _chartService;
    private readonly VoiceStatsService _voiceStats;

    public VoiceStatsModule(VcHourlyChartService chartService, VoiceStatsService voiceStats)
    {
        _chartService = chartService;
        _voiceStats = voiceStats;
    }

    [SlashCommand("hourly", "過去7日間のVC平均参加者数を時間帯別にグラフで表示します")]
    public async Task HourlyAsync(
        [Summary("days", "集計期間(日数)")] int days = 7)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync();

        var clampedDays = Math.Clamp(days, 1, 30);
        var chartPng = await _chartService.BuildHourlyAverageChartAsync(Context.Guild.Id, clampedDays);

        if (chartPng is null || chartPng.Length == 0)
        {
            await FollowupAsync("VC参加データがまだありません。", ephemeral: true);
            return;
        }

        var stream = new MemoryStream(chartPng);
        var embed = new EmbedBuilder()
            .WithTitle("📊 VC平均参加者数")
            .WithDescription($"過去{clampedDays}日間の時間帯別平均参加者数 (JST)")
            .WithColor(new Color(59, 130, 246))
            .WithCurrentTimestamp()
            .WithImageUrl("attachment://vc_hourly_chart.png")
            .Build();

        await Context.Interaction.FollowupWithFileAsync(stream, "vc_hourly_chart.png", embed: embed);
    }

}
