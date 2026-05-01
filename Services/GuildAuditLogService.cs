using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class GuildAuditLogService
{
    private readonly Db _db;
    private readonly DiscordOptions _options;
    private readonly ILogger<GuildAuditLogService> _logger;

    public GuildAuditLogService(
        Db db,
        IOptions<DiscordOptions> options,
        ILogger<GuildAuditLogService> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public Task SendSystemAsync(SocketGuild guild, string title, string description, Color? color = null)
        => SendAsync(guild, title, description, color ?? Color.LightGrey, actor: null);

    public async Task SendAsync(
        SocketGuild guild,
        string title,
        string description,
        Color color,
        IUser? actor = null)
    {
        var channel = await ResolveLogChannelAsync(guild);
        if (channel is null)
        {
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color)
            .WithCurrentTimestamp();

        if (!string.IsNullOrWhiteSpace(description))
        {
            embed.WithDescription(description);
        }

        if (actor is not null)
        {
            var avatar = actor.GetAvatarUrl() ?? actor.GetDefaultAvatarUrl();
            var authorName = actor.GlobalName ?? actor.Username;
            embed.WithAuthor(authorName, avatar)
                .WithThumbnailUrl(avatar);
        }

        try
        {
            await channel.SendMessageAsync(embed: embed.Build(), flags: MessageFlags.SuppressNotification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send guild audit log. GuildId={GuildId}, Title={Title}", guild.Id, title);
        }

        try
        {
            await _db.LogAuditAsync(
                guild.Id,
                InferCategory(title),
                title,
                description,
                (int)color.RawValue,
                actor?.Id,
                actor?.GlobalName ?? actor?.Username);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist audit log to DB. GuildId={GuildId}, Title={Title}", guild.Id, title);
        }
    }

    private static string InferCategory(string title)
    {
        var t = title.ToLowerInvariant();
        if (t.Contains("コマンド")) return "command";
        if (t.Contains("権限拒否")) return "permission";
        if (t.Contains("失敗")) return "error";
        if (t.Contains("勧誘")) return "recruit";
        if (t.Contains("抽選")) return "lottery";
        if (t.Contains("問い合わせ")) return "inquiry";
        if (t.Contains("ロール")) return "role";
        if (t.Contains("登録")) return "registration";
        if (t.Contains("削除")) return "delete";
        if (t.Contains("編集")) return "edit";
        return "general";
    }

    private async Task<IMessageChannel?> ResolveLogChannelAsync(SocketGuild guild)
    {
        var settings = await _db.GetGuildSettingsAsync(guild.Id);
        if (settings is not null && settings.LogChannelId != 0)
        {
            var byId = guild.GetTextChannel(ToUlong(settings.LogChannelId));
            if (byId is not null)
            {
                return byId;
            }
        }

        var name = string.IsNullOrWhiteSpace(_options.LogChannelName) ? "log" : _options.LogChannelName;
        return guild.TextChannels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
}
