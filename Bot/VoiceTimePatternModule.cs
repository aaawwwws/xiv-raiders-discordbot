using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;

namespace RaidDcBot.Bot;

[Group("voicetime", "VC時間帯パターンテスト")]
public sealed class VoiceTimePatternModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly Db _db;
    private readonly DiscordOptions _options;

    public VoiceTimePatternModule(
        Db db,
        IOptions<DiscordOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    [RequireBotAdmin]
    [SlashCommand("test", "指定ユーザーの現在時刻における時間帯パターンを表示します（VC参加時と同じ分析）")]
    public async Task TestAsync(IUser user)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: false);

        var jstNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Tokyo Standard Time");
        var hint = await BuildTimePatternHintAsync(Context.Guild.Id, user.Id, jstNow.Hour, _options.VoiceJoinTimePatternDays);

        if (string.IsNullOrWhiteSpace(hint))
        {
            var embedNoData = new EmbedBuilder()
                .WithTitle("VC時間帯パターンテスト")
                .WithColor(Color.Blue)
                .WithCurrentTimestamp()
                .AddField("対象ユーザー", MentionUtils.MentionUser(user.Id), true)
                .AddField("現在時刻(JST)", $"{jstNow:yyyy-MM-dd HH:mm}", true)
                .AddField("🕐 時間帯の傾向", "VC活動データがまだありません", false)
                .Build();
            await FollowupAsync(embed: embedNoData);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("VC時間帯パターンテスト")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .AddField("対象ユーザー", MentionUtils.MentionUser(user.Id), true)
            .AddField("現在時刻(JST)", $"{jstNow:yyyy-MM-dd HH:mm}", true)
            .AddField("🕐 時間帯の傾向", hint, false)
            .Build();

        await FollowupAsync(embed: embed);
    }

    private async Task<string?> BuildTimePatternHintAsync(ulong guildId, ulong userId, int currentHourJst, int lookbackDays)
    {
        if (lookbackDays <= 0)
        {
            return null;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var fromUtc = nowUtc.AddDays(-lookbackDays);
        var activity = await _db.ListVoiceHourlyActivityByUserAsync(guildId, userId, fromUtc, nowUtc);
        if (activity.Count == 0)
        {
            return null;
        }

        var sorted = activity
            .Where(a => a.TotalSeconds > 0)
            .OrderByDescending(a => a.TotalSeconds)
            .ToList();
        if (sorted.Count == 0)
        {
            return null;
        }

        var current = sorted.FirstOrDefault(a => a.Hour == currentHourJst);
        var textHint = BuildTextHint(sorted, currentHourJst, current);
        return textHint;
    }

    private static string BuildTextHint(List<VoiceHourlyActivity> sorted, int currentHourJst, VoiceHourlyActivity? current)
    {
        var hourLabel = $"{currentHourJst}時台";
        if (current == null)
        {
            return $"{hourLabel}はまだVCに来たことがないようです 🆕";
        }

        var maxSeconds = sorted[0].TotalSeconds;
        var totalSeconds = sorted.Sum(a => a.TotalSeconds);
        var avgSeconds = totalSeconds / sorted.Count;

        var peakHours = sorted.Take(3).Select(a => a.Hour).OrderBy(h => h).ToList();
        var peakRange = string.Join("時・", peakHours) + "時";

        var peakLine = sorted.Count switch
        {
            1 => $"よくいる時間帯: {sorted[0].Hour}時のみ",
            _ => $"よくいる時間帯: {peakRange}"
        };

        if (current == null)
        {
            return $"{peakLine}\n{hourLabel}はまだVCに来たことがないようです 🆕";
        }

        var rank = sorted.FindIndex(a => a.Hour == currentHourJst) + 1;

        string currentLine;
        if (current.TotalSeconds == maxSeconds)
        {
            var pct = totalSeconds > 0 ? (int)(current.TotalSeconds * 100 / totalSeconds) : 100;
            currentLine = $"{hourLabel}が一番よくVCにいる時間帯です！ 🎉（全VC時間の{Math.Clamp(pct, 0, 100)}%）";
        }
        else if (rank <= 3)
        {
            currentLine = $"よくVCにいる時間帯です！ ✨（{rank}番目）";
        }
        else if (current.TotalSeconds >= avgSeconds)
        {
            currentLine = $"{hourLabel}、わりとよくいますね 👀";
        }
        else
        {
            currentLine = $"{hourLabel}もたまにVCに来てますね 👣";
        }

        return $"{peakLine}\n{currentLine}";
    }

}
