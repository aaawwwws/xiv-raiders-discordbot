using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;
using System.Text;

namespace RaidDcBot.Bot;

[Group("newlife", "新生活関連コマンド")]
public sealed class NewLifeModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly LodestoneClient _lodestone;
    private readonly Db _db;
    private readonly ILogger<NewLifeModule> _logger;

    public NewLifeModule(
        LodestoneClient lodestone,
        Db db,
        ILogger<NewLifeModule> logger)
    {
        _lodestone = lodestone;
        _db = db;
        _logger = logger;
    }

    [SlashCommand("fc-embed", "FC情報をEmbedで表示します")]
    public async Task FcEmbedAsync([Summary(description: "LodestoneのFCページURL（省略可）")] string? url = null)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync();

        var targetUrl = url;
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            var settings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
            targetUrl = settings?.DefaultFcUrl;
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                await FollowupAsync("URLが指定されておらず、デフォルトのFC URLも設定されていません。\n`/newlife fc-embed url:https://jp.finalfantasyxiv.com/lodestone/freecompany/...` のように指定してください。", ephemeral: true);
                return;
            }
        }

        if (!_lodestone.TryNormalizeFcUrl(targetUrl, out var normalizedUrl, out var normalizeError))
        {
            await FollowupAsync(normalizeError, ephemeral: true);
            return;
        }

        string html;
        try
        {
            html = await _lodestone.FetchHtmlAsync(normalizedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch FC page.");
            await FollowupAsync($"FCページの取得に失敗しました: {ex.Message}", ephemeral: true);
            return;
        }

        if (!_lodestone.TryParseFc(html, normalizedUrl, out var fc))
        {
            await FollowupAsync("FCページの解析に失敗しました。URLを確認してください。", ephemeral: true);
            return;
        }

        var members = await _lodestone.FetchFcMembersAsync(normalizedUrl);
        var embeds = DiscordBotService.BuildFcEmbeds(fc, members);
        var message = await FollowupAsync(embeds: embeds);
        await _db.UpdateGuildFcEmbedAsync(Context.Guild.Id, Context.Channel.Id, message.Id);
    }

    [RequireBotAdmin]
    [SlashCommand("set-fc-url", "デフォルトのFC URLを設定します")]
    public async Task SetFcUrlAsync([Summary(description: "LodestoneのFCページURL")] string url)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        if (!_lodestone.TryNormalizeFcUrl(url, out var normalizedUrl, out var normalizeError))
        {
            await FollowupAsync(normalizeError, ephemeral: true);
            return;
        }

        await _db.UpdateGuildDefaultFcUrlAsync(Context.Guild.Id, normalizedUrl);
        await FollowupAsync($"デフォルトFC URLを設定しました: {normalizedUrl}", ephemeral: true);
    }
}
