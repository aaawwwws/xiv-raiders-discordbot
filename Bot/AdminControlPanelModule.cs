using System.Text;
using Discord;
using Discord.Interactions;
using RaidDcBot.Data;
using RaidDcBot.Models;

namespace RaidDcBot.Bot;

[Group("adminpanel", "ADMINコントロールパネル")]
public sealed class AdminControlPanelModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly Db _db;

    public AdminControlPanelModule(Db db)
    {
        _db = db;
    }

    [RequireBotAdmin]
    [SlashCommand("setup", "このチャンネルにADMINコントロールパネルを設置します")]
    public async Task SetupAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("このチャンネルにはパネルを設置できません。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var existing = await _db.GetGuildAdminControlPanelAsync(Context.Guild.Id);
        ulong? existingMessageId = existing is null || existing.MessageId == 0 ? null : ToUlong(existing.MessageId);
        var message = await EnsureAdminControlPanelMessageAsync(channel, existingMessageId);
        await _db.UpsertGuildAdminControlPanelAsync(Context.Guild.Id, channel.Id, message.Id);
        await FollowupAsync($"このチャンネルにADMINコントロールパネルを設置しました。(message: {message.Id})", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.ExportMessageCsvCustomId, ignoreGroupNames: true)]
    public async Task ExportMessageCsvAllAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ExportMessageCsvAsync();
    }

    private async Task ExportMessageCsvAsync()
    {
        if (Context.Guild is null)
        {
            return;
        }

        var toUtcExclusive = DateTimeOffset.UtcNow;
        var fromUtcInclusive = DateTimeOffset.UnixEpoch;
        var rows = await _db.ListMessageLogsForExportAsync(Context.Guild.Id, fromUtcInclusive, toUtcExclusive);

        if (rows.Count == 0)
        {
            await FollowupAsync("メッセージログはありません。", ephemeral: true);
            return;
        }

        var csv = BuildMessageLogCsv(rows);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv);
        await using var stream = new MemoryStream(bytes, writable: false);
        var fileName = $"message_logs_{Context.Guild.Id}_all_to_{toUtcExclusive:yyyyMMddHHmm}.csv";
        await FollowupWithFileAsync(stream, fileName, text: $"CSVを出力しました。期間: 全期間 / 件数: {rows.Count}", ephemeral: true);
    }

    private async Task<IUserMessage> EnsureAdminControlPanelMessageAsync(IMessageChannel channel, ulong? existingMessageId)
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

        var components = AdminControlPanel.BuildComponents();
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = AdminControlPanel.PanelMessage;
                props.Components = components;
            });
            return existing;
        }

        return await channel.SendMessageAsync(AdminControlPanel.PanelMessage, components: components, flags: MessageFlags.SuppressNotification);
    }

    private static string BuildMessageLogCsv(IReadOnlyList<MessageLog> rows)
    {
        var sb = new StringBuilder(capacity: Math.Max(1024, rows.Count * 128));
        sb.AppendLine("created_at_utc,message_id,channel_id,author_id,author_name,author_display_name,content,attachment_count,embed_count,attachments_json,embeds_json,edited_at_utc,deleted_at_utc");

        foreach (var row in rows)
        {
            var displayName = string.IsNullOrWhiteSpace(row.AuthorDisplayName) ? row.AuthorName : row.AuthorDisplayName;
            sb.Append(ToCsvCell(row.CreatedAt.ToUniversalTime().ToString("O"))).Append(',')
              .Append(ToCsvCell(row.MessageId.ToString())).Append(',')
              .Append(ToCsvCell(row.ChannelId.ToString())).Append(',')
              .Append(ToCsvCell(row.AuthorId.ToString())).Append(',')
              .Append(ToCsvCell(row.AuthorName)).Append(',')
              .Append(ToCsvCell(displayName)).Append(',')
              .Append(ToCsvCell(row.Content)).Append(',')
              .Append(ToCsvCell(row.AttachmentCount.ToString())).Append(',')
              .Append(ToCsvCell(row.EmbedCount.ToString())).Append(',')
              .Append(ToCsvCell(row.AttachmentsJson)).Append(',')
              .Append(ToCsvCell(row.EmbedsJson)).Append(',')
              .Append(ToCsvCell(row.EditedAt?.ToUniversalTime().ToString("O"))).Append(',')
              .Append(ToCsvCell(row.DeletedAt?.ToUniversalTime().ToString("O")))
              .AppendLine();
        }

        return sb.ToString();
    }

    private static string ToCsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var needsQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuote)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
}
