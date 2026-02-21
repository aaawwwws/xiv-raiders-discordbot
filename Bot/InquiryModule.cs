using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

[Group("inquiry", "問い合わせ")]
public sealed class InquiryModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string InquiryCategoryName = "お問い合わせ";
    private const string InquiryOpenButtonId = "inquiry_open";
    private const string InquiryModalId = "inquiry_modal";
    private const string InquiryTicketCloseButtonId = "inquiry_ticket_close";
    private const string InquiryTicketReopenButtonId = "inquiry_ticket_reopen";
    private const string InquiryTicketDeleteButtonId = "inquiry_ticket_delete";

    private readonly Db _db;
    private readonly DiscordOptions _options;
    private readonly GuildAuditLogService _audit;
    private readonly ILogger<InquiryModule> _logger;

    public InquiryModule(
        Db db,
        IOptions<DiscordOptions> options,
        GuildAuditLogService audit,
        ILogger<InquiryModule> logger)
    {
        _db = db;
        _options = options.Value;
        _audit = audit;
        _logger = logger;
    }

    [RequireBotAdmin]
    [SlashCommand("setup", "このチャンネルに問い合わせパネルを設置します")]
    public async Task SetupAsync()
    {
        var channel = await ResolveSetupChannelAsync();
        if (channel is null)
        {
            return;
        }

        await DeferAsync(ephemeral: true);

        await EnsurePanelMessageAsync(channel);
        await FollowupAsync("このチャンネルに問い合わせパネルを設置しました。", ephemeral: true);
    }

    [ComponentInteraction(InquiryOpenButtonId, ignoreGroupNames: true)]
    public async Task OpenInquiryFromPanelAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<InquiryModal>(InquiryModalId);
    }

    [ModalInteraction(InquiryModalId, ignoreGroupNames: true)]
    public async Task HandleInquiryModalAsync(InquiryModal modal)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        try
        {
            if (Context.User is not SocketGuildUser requester)
            {
                await FollowupAsync("問い合わせ送信者情報を取得できませんでした。", ephemeral: true);
                return;
            }

            var existingOpen = await _db.GetOpenInquiryTicketByRequesterAsync(Context.Guild.Id, requester.Id);
            if (existingOpen is not null)
            {
                var existingChannel = Context.Guild.GetTextChannel(ToUlong(existingOpen.ChannelId));
                if (existingChannel is not null)
                {
                    await FollowupAsync($"未クローズのお問い合わせがあります。<#{existingChannel.Id}> を利用してください。", ephemeral: true);
                    return;
                }

                await _db.UpdateInquiryTicketStatusAsync(existingOpen.Id, InquiryTicketStatus.Closed, DateTimeOffset.UtcNow);
            }

            var category = await ResolveOrCreateInquiryCategoryAsync(Context.Guild);
            if (category is null)
            {
                await FollowupAsync("お問い合わせカテゴリーの作成に失敗しました。管理者に連絡してください。", ephemeral: true);
                return;
            }

            var subject = NormalizeSubject(modal.Subject);
            var detail = NormalizeDetail(modal.Detail);
            var adminRole = ResolveAdminRole(Context.Guild);
            var adminMention = adminRole?.Mention;
            var inquiryChannel = await CreateInquiryChannelAsync(Context.Guild, category, requester, adminRole);
            var nowUtc = DateTimeOffset.UtcNow;
            var ticket = await _db.CreateInquiryTicketAsync(
                Context.Guild.Id,
                inquiryChannel.Id,
                requester.Id,
                requester.ToString(),
                subject,
                InquiryTicketStatus.Open,
                nowUtc);

            var embed = new EmbedBuilder()
                .WithTitle("新規問い合わせ")
                .WithColor(Color.Orange)
                .WithCurrentTimestamp()
                .AddField("件名", subject, false)
                .AddField("投稿者", $"{Context.User.Mention} ({Context.User})", false)
                .AddField("内容", detail, false);

            await inquiryChannel.SendMessageAsync(
                text: string.IsNullOrWhiteSpace(adminMention) ? null : adminMention,
                embed: embed.Build());
            await NotifyInquiryToLogAsync(Context.Guild, requester, inquiryChannel, subject, ticket.Id);
            await TryRefreshInquiryManagementViewsAsync(Context.Guild, ticket);

            await FollowupAsync($"問い合わせチャンネルを作成しました。<#{inquiryChannel.Id}>", ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to submit inquiry. GuildId={GuildId}, UserId={UserId}", Context.Guild.Id, Context.User.Id);
            await FollowupAsync("問い合わせの送信に失敗しました。時間をおいて再試行してください。", ephemeral: true);
        }
    }

    [RequireBotAdmin]
    [ComponentInteraction(InquiryTicketCloseButtonId, ignoreGroupNames: true)]
    public async Task CloseInquiryTicketAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser actor)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        if (await ResolveTicketContextAsync() is not { } context)
        {
            await FollowupAsync("このチャンネルは問い合わせチケットではありません。", ephemeral: true);
            return;
        }

        if (InquiryTicketStatus.IsClosed(context.Ticket.Status))
        {
            await FollowupAsync("このチケットはすでにクローズ済みです。", ephemeral: true);
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var updated = await _db.UpdateInquiryTicketStatusAsync(context.Ticket.Id, InquiryTicketStatus.Closed, nowUtc);
        if (!updated)
        {
            await FollowupAsync("クローズに失敗しました。時間を置いて再試行してください。", ephemeral: true);
            return;
        }

        var updatedTicket = await _db.GetInquiryTicketByIdAsync(context.Ticket.Id);
        if (updatedTicket is null)
        {
            await FollowupAsync("クローズ後のチケット情報取得に失敗しました。", ephemeral: true);
            return;
        }

        await ApplyTicketPermissionStateAsync(context.Channel, context.Guild, updatedTicket, closed: true);
        var transcript = await BuildTranscriptAsync(context.Channel, maxMessages: 300);
        await _db.SaveInquiryTranscriptAsync(updatedTicket.Id, transcript, nowUtc);
        await RefreshTicketControlMessageAsync(updatedTicket, context.Guild);
        await TryRefreshInquiryManagementViewsAsync(context.Guild, updatedTicket);

        await _audit.SendAsync(
            context.Guild,
            "お問い合わせクローズ",
            $"チケット: #{updatedTicket.Id}\n投稿者: {MentionUser(ToUlong(updatedTicket.RequesterUserId))}\nチャンネル: <#{context.Channel.Id}>",
            Color.DarkGrey,
            actor);

        await FollowupAsync("お問い合わせをクローズしました。", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction("inquiry_ticket_assign_me", ignoreGroupNames: true)]
    public Task LegacyAssignInquiryTicketAsync()
        => RespondAsync("この操作は廃止されました。クローズのみ利用できます。", ephemeral: true);

    [RequireBotAdmin]
    [ComponentInteraction("inquiry_ticket_unassign", ignoreGroupNames: true)]
    public Task LegacyUnassignInquiryTicketAsync()
        => RespondAsync("この操作は廃止されました。クローズのみ利用できます。", ephemeral: true);

    [RequireBotAdmin]
    [ComponentInteraction("inquiry_ticket_reopen", ignoreGroupNames: true)]
    public async Task ReopenInquiryTicketAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser actor)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        if (await ResolveTicketContextAsync() is not { } context)
        {
            await FollowupAsync("このチャンネルは問い合わせチケットではありません。", ephemeral: true);
            return;
        }

        if (!InquiryTicketStatus.IsClosed(context.Ticket.Status))
        {
            await FollowupAsync("このチケットはすでにオープンです。", ephemeral: true);
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var updated = await _db.UpdateInquiryTicketStatusAsync(context.Ticket.Id, InquiryTicketStatus.Open, nowUtc);
        if (!updated)
        {
            await FollowupAsync("再オープンに失敗しました。時間を置いて再試行してください。", ephemeral: true);
            return;
        }

        var updatedTicket = await _db.GetInquiryTicketByIdAsync(context.Ticket.Id);
        if (updatedTicket is null)
        {
            await FollowupAsync("再オープン後のチケット情報取得に失敗しました。", ephemeral: true);
            return;
        }

        await ApplyTicketPermissionStateAsync(context.Channel, context.Guild, updatedTicket, closed: false);
        await RefreshTicketControlMessageAsync(updatedTicket, context.Guild);
        await TryRefreshInquiryManagementViewsAsync(context.Guild, updatedTicket);

        await _audit.SendAsync(
            context.Guild,
            "お問い合わせ再オープン",
            $"チケット: #{updatedTicket.Id}\n投稿者: {MentionUser(ToUlong(updatedTicket.RequesterUserId))}\nチャンネル: <#{context.Channel.Id}>",
            Color.Orange,
            actor);

        await FollowupAsync("お問い合わせを再オープンしました。", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(InquiryTicketDeleteButtonId, ignoreGroupNames: true)]
    public async Task DeleteInquiryTicketChannelAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser actor)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        if (await ResolveTicketContextAsync() is not { } context)
        {
            await FollowupAsync("このチャンネルは問い合わせチケットではありません。", ephemeral: true);
            return;
        }

        if (!InquiryTicketStatus.IsClosed(context.Ticket.Status))
        {
            await FollowupAsync("チャンネル削除はクローズ済みチケットのみ実行できます。", ephemeral: true);
            return;
        }

        try
        {
            await _audit.SendAsync(
                context.Guild,
                "お問い合わせチャンネル削除",
                $"チケット: #{context.Ticket.Id}\n投稿者: {MentionUser(ToUlong(context.Ticket.RequesterUserId))}\nチャンネル: <#{context.Channel.Id}>",
                Color.DarkGrey,
                actor);

            await context.Channel.DeleteAsync(new RequestOptions
            {
                AuditLogReason = $"Inquiry ticket channel deleted. TicketId={context.Ticket.Id}"
            });

            await FollowupAsync("お問い合わせチャンネルを削除しました。", ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete inquiry ticket channel. TicketId={TicketId}, ChannelId={ChannelId}", context.Ticket.Id, context.Channel.Id);
            await FollowupAsync("お問い合わせチャンネルの削除に失敗しました。時間を置いて再試行してください。", ephemeral: true);
        }
    }

    [RequireBotAdmin]
    [ComponentInteraction("inquiry_ticket_refresh", ignoreGroupNames: true)]
    public Task LegacyRefreshInquiryTicketAsync()
        => RespondAsync("この操作は廃止されました。クローズのみ利用できます。", ephemeral: true);

    [RequireBotAdmin]
    [ComponentInteraction("inquiry_ticket_status_select", ignoreGroupNames: true)]
    public Task LegacyUpdateInquiryStatusAsync(string[] _)
        => RespondAsync("この操作は廃止されました。クローズのみ利用できます。", ephemeral: true);

    private async Task<(SocketGuild Guild, SocketTextChannel Channel, InquiryTicket Ticket)?> ResolveTicketContextAsync()
    {
        if (Context.Guild is null || Context.Channel is not SocketTextChannel channel)
        {
            return null;
        }

        var ticket = await _db.GetInquiryTicketByChannelAsync(channel.Id);
        if (ticket is not null)
        {
            return (Context.Guild, channel, ticket);
        }

        if (Context.Interaction is SocketMessageComponent component &&
            TryParseTicketIdFromMessage(component.Message, out var ticketId))
        {
            ticket = await _db.GetInquiryTicketByIdAsync(ticketId);
            if (ticket is not null && ticket.GuildId == ToDbId(Context.Guild.Id))
            {
                var ticketChannel = Context.Guild.GetTextChannel(ToUlong(ticket.ChannelId));
                if (ticketChannel is not null)
                {
                    return (Context.Guild, ticketChannel, ticket);
                }
            }
        }

        return null;
    }

    private static bool TryParseTicketIdFromMessage(IMessage message, out int ticketId)
    {
        ticketId = 0;
        if (message.Embeds.Count == 0)
        {
            return false;
        }

        var title = message.Embeds.FirstOrDefault()?.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        const string prefix = "お問い合わせ管理 #";
        if (!title.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var raw = title.Substring(prefix.Length);
        return int.TryParse(raw, out ticketId) && ticketId > 0;
    }

    private async Task<IMessageChannel?> ResolveSetupChannelAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return null;
        }

        if (Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("このチャンネルにはパネルを設置できません。", ephemeral: true);
            return null;
        }

        return channel;
    }

    private async Task<IUserMessage> EnsurePanelMessageAsync(IMessageChannel channel)
    {
        var recent = await channel.GetMessagesAsync(50).FlattenAsync();
        var existing = recent
            .OfType<IUserMessage>()
            .FirstOrDefault(message =>
                message.Author.Id == Context.Client.CurrentUser.Id &&
                HasInquiryButton(message));

        var components = BuildInquiryPanelComponents();
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = string.Empty;
                props.Components = components;
            });
            return existing;
        }

        return await channel.SendMessageAsync(
            components: components,
            flags: MessageFlags.SuppressNotification);
    }

    private static MessageComponent BuildInquiryPanelComponents()
    {
        return new ComponentBuilder()
            .WithButton("お問い合わせ", InquiryOpenButtonId, ButtonStyle.Primary)
            .Build();
    }

    private static bool HasInquiryButton(IUserMessage message)
    {
        return message.Components
            .OfType<ActionRowComponent>()
            .SelectMany(row => row.Components)
            .OfType<ButtonComponent>()
            .Any(button => string.Equals(button.CustomId, InquiryOpenButtonId, StringComparison.Ordinal));
    }

    private async Task EnsureTicketControlMessageAsync(ITextChannel channel, SocketGuild guild, InquiryTicket ticket)
    {
        var recent = await channel.GetMessagesAsync(50).FlattenAsync();
        var existing = recent
            .OfType<IUserMessage>()
            .FirstOrDefault(message =>
                message.Author.Id == Context.Client.CurrentUser.Id &&
                HasTicketControl(message));

        var embed = BuildTicketControlEmbed(guild, ticket);
        var components = BuildTicketControlComponents(ticket);
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = embed;
                props.Components = components;
            });
            return;
        }

        await channel.SendMessageAsync(embed: embed, components: components, flags: MessageFlags.SuppressNotification);
    }

    private static bool HasTicketControl(IUserMessage message)
    {
        return message.Components
            .OfType<ActionRowComponent>()
            .SelectMany(row => row.Components)
            .OfType<ButtonComponent>()
            .Any(button =>
                string.Equals(button.CustomId, InquiryTicketCloseButtonId, StringComparison.Ordinal) ||
                string.Equals(button.CustomId, InquiryTicketReopenButtonId, StringComparison.Ordinal) ||
                string.Equals(button.CustomId, InquiryTicketDeleteButtonId, StringComparison.Ordinal));
    }

    private async Task RefreshTicketControlMessageAsync(InquiryTicket ticket, SocketGuild guild)
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            return;
        }

        if (component.Message is IUserMessage sourceMessage &&
            sourceMessage.Author.Id == Context.Client.CurrentUser.Id &&
            HasTicketControl(sourceMessage))
        {
            await sourceMessage.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = BuildTicketControlEmbed(guild, ticket);
                props.Components = BuildTicketControlComponents(ticket);
            });
        }
    }

    private static Embed BuildTicketControlEmbed(SocketGuild guild, InquiryTicket ticket)
    {
        var requester = MentionUser(ToUlong(ticket.RequesterUserId));
        var statusLabel = InquiryTicketStatus.IsClosed(ticket.Status) ? "クローズ" : "オープン";

        var embed = new EmbedBuilder()
            .WithTitle($"お問い合わせ管理 #{ticket.Id}")
            .WithColor(InquiryTicketStatus.IsClosed(ticket.Status) ? Color.DarkGrey : Color.Orange)
            .WithCurrentTimestamp()
            .AddField("状態", statusLabel, true)
            .AddField("投稿者", requester, false)
            .AddField("件名", string.IsNullOrWhiteSpace(ticket.Subject) ? "未指定" : ticket.Subject, false)
            .AddField("最終更新", FormatTimestamp(ticket.LastActivityAt), true)
            .AddField("作成", FormatTimestamp(ticket.CreatedAt), true);

        if (ticket.ClosedAt.HasValue)
        {
            embed.AddField("クローズ", FormatTimestamp(ticket.ClosedAt.Value), true);
        }

        embed.WithFooter($"Guild: {guild.Name} / Channel: {ticket.ChannelId}");
        return embed.Build();
    }

    private static MessageComponent BuildTicketControlComponents(InquiryTicket ticket)
    {
        var isClosed = InquiryTicketStatus.IsClosed(ticket.Status);
        var builder = new ComponentBuilder();
        if (isClosed)
        {
            builder
                .WithButton("再オープン", InquiryTicketReopenButtonId, ButtonStyle.Success, row: 0)
                .WithButton("削除", InquiryTicketDeleteButtonId, ButtonStyle.Danger, row: 0);
        }
        else
        {
            builder.WithButton("クローズ", InquiryTicketCloseButtonId, ButtonStyle.Danger, row: 0);
        }

        return builder.Build();
    }

    private static string NormalizeSubject(string? raw)
    {
        var value = raw?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(value) ? "未指定" : value;
    }

    private static string NormalizeDetail(string? raw)
    {
        var value = raw?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(value) ? "（空）" : value;
    }

    private static string BuildInquiryChannelName(SocketGuild guild, ulong userId)
    {
        var baseName = $"inquiry-{userId}";
        var names = guild.TextChannels
            .Select(channel => channel.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
        {
            return baseName;
        }

        for (var suffix = 1; suffix <= 999; suffix++)
        {
            var candidate = $"{baseName}-{suffix}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private static OverwritePermissions BuildInquiryParticipantPermissions()
    {
        return new OverwritePermissions(
            viewChannel: PermValue.Allow,
            readMessageHistory: PermValue.Allow,
            sendMessages: PermValue.Allow,
            attachFiles: PermValue.Allow,
            embedLinks: PermValue.Allow,
            addReactions: PermValue.Allow,
            useApplicationCommands: PermValue.Allow);
    }

    private static OverwritePermissions BuildInquiryReadOnlyPermissions()
    {
        return new OverwritePermissions(
            viewChannel: PermValue.Allow,
            readMessageHistory: PermValue.Allow,
            sendMessages: PermValue.Deny,
            attachFiles: PermValue.Deny,
            addReactions: PermValue.Deny,
            useApplicationCommands: PermValue.Allow);
    }

    private IRole? ResolveAdminRole(SocketGuild guild)
    {
        var roleName = string.IsNullOrWhiteSpace(_options.AdminRoleName) ? "RaidDC Admin" : _options.AdminRoleName;
        return guild.Roles.FirstOrDefault(r => string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));
    }

    private static Task ApplyInquiryPermissionsAsync(IGuildChannel channel, SocketGuild guild, SocketGuildUser requester, IRole? adminRole)
    {
        var denyEveryone = new OverwritePermissions(viewChannel: PermValue.Deny);
        var allowParticipant = BuildInquiryParticipantPermissions();
        var tasks = new List<Task>
        {
            channel.AddPermissionOverwriteAsync(guild.EveryoneRole, denyEveryone),
            channel.AddPermissionOverwriteAsync(requester, allowParticipant)
        };

        if (adminRole is not null)
        {
            tasks.Add(channel.AddPermissionOverwriteAsync(adminRole, allowParticipant));
        }

        return Task.WhenAll(tasks);
    }

    private async Task ApplyTicketPermissionStateAsync(SocketTextChannel channel, SocketGuild guild, InquiryTicket ticket, bool closed)
    {
        var adminRole = ResolveAdminRole(guild);
        var requester = guild.GetUser(ToUlong(ticket.RequesterUserId));
        var denyEveryone = new OverwritePermissions(viewChannel: PermValue.Deny);

        await channel.AddPermissionOverwriteAsync(guild.EveryoneRole, denyEveryone);
        if (requester is not null)
        {
            var permissions = closed ? BuildInquiryReadOnlyPermissions() : BuildInquiryParticipantPermissions();
            await channel.AddPermissionOverwriteAsync(requester, permissions);
        }

        if (adminRole is not null)
        {
            await channel.AddPermissionOverwriteAsync(adminRole, BuildInquiryParticipantPermissions());
        }
    }

    private async Task<ICategoryChannel?> ResolveOrCreateInquiryCategoryAsync(SocketGuild guild)
    {
        var existing = guild.CategoryChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, InquiryCategoryName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        return await guild.CreateCategoryChannelAsync(InquiryCategoryName);
    }

    private async Task<ITextChannel> CreateInquiryChannelAsync(
        SocketGuild guild,
        ICategoryChannel category,
        SocketGuildUser requester,
        IRole? adminRole)
    {
        var channelName = BuildInquiryChannelName(guild, requester.Id);
        var created = await guild.CreateTextChannelAsync(channelName, props =>
        {
            props.CategoryId = category.Id;
            props.Topic = $"問い合わせ: {requester} ({requester.Id})";
        });

        await ApplyInquiryPermissionsAsync(created, guild, requester, adminRole);
        return created;
    }

    private async Task TryRefreshInquiryManagementPanelAsync(SocketGuild guild)
    {
        try
        {
            var channel = await ResolveOrCreateInquiryManagementChannelAsync(guild);
            if (channel is null)
            {
                return;
            }

            var tickets = await _db.ListInquiryTicketsAsync(guild.Id, includeClosed: false, maxRows: 25);
            var embed = InquiryManagementPanel.BuildSummaryEmbed(tickets);
            var components = InquiryManagementPanel.BuildComponents(tickets);
            var recent = await channel.GetMessagesAsync(50).FlattenAsync();
            var existing = recent
                .OfType<IUserMessage>()
                .FirstOrDefault(message =>
                    message.Author.Id == Context.Client.CurrentUser.Id &&
                    InquiryManagementPanel.LooksLikeDashboardMessage(message));

            if (existing is not null)
            {
                await existing.ModifyAsync(props =>
                {
                    props.Content = null;
                    props.Embed = embed;
                    props.Components = components;
                });
                return;
            }

            await channel.SendMessageAsync(
                embed: embed,
                components: components,
                flags: MessageFlags.SuppressNotification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh inquiry management panel. GuildId={GuildId}", guild.Id);
        }
    }

    private async Task TryRefreshInquiryManagementViewsAsync(SocketGuild guild, InquiryTicket ticket)
    {
        await TryRefreshInquiryManagementPanelAsync(guild);
        await TryUpsertInquiryManagementDetailMessageAsync(guild, ticket);
    }

    private async Task TryUpsertInquiryManagementDetailMessageAsync(SocketGuild guild, InquiryTicket ticket)
    {
        try
        {
            var channel = await ResolveOrCreateInquiryManagementChannelAsync(guild);
            if (channel is null)
            {
                return;
            }

            var embed = BuildTicketControlEmbed(guild, ticket);
            var recent = await channel.GetMessagesAsync(100).FlattenAsync();
            var existing = recent
                .OfType<IUserMessage>()
                .FirstOrDefault(message =>
                    message.Author.Id == Context.Client.CurrentUser.Id &&
                    LooksLikeInquiryManagementDetailMessage(message, ticket.Id));

            if (existing is not null)
            {
                await existing.ModifyAsync(props =>
                {
                    props.Content = null;
                    props.Embed = embed;
                    props.Components = BuildTicketControlComponents(ticket);
                });
                return;
            }

            await channel.SendMessageAsync(
                embed: embed,
                components: BuildTicketControlComponents(ticket),
                flags: MessageFlags.SuppressNotification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to upsert inquiry management detail message. GuildId={GuildId}, TicketId={TicketId}", guild.Id, ticket.Id);
        }
    }

    private static bool LooksLikeInquiryManagementDetailMessage(IUserMessage message, int ticketId)
    {
        if (message.Embeds.Count == 0)
        {
            return false;
        }

        var expectedTitle = $"お問い合わせ管理 #{ticketId}";
        return message.Embeds.Any(embed =>
            string.Equals(embed.Title, expectedTitle, StringComparison.Ordinal));
    }

    private async Task<ITextChannel?> ResolveOrCreateInquiryManagementChannelAsync(SocketGuild guild)
    {
        var existing = guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, InquiryManagementPanel.ChannelName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            await ApplyInquiryManagementChannelPermissionsAsync(guild, existing);
            return existing;
        }

        var category = guild.CategoryChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, InquiryCategoryName, StringComparison.OrdinalIgnoreCase));
        var created = await guild.CreateTextChannelAsync(InquiryManagementPanel.ChannelName, props =>
        {
            props.Topic = "お問い合わせ管理パネル";
            if (category is not null)
            {
                props.CategoryId = category.Id;
            }
        });

        await ApplyInquiryManagementChannelPermissionsAsync(guild, created);
        return created;
    }

    private async Task ApplyInquiryManagementChannelPermissionsAsync(SocketGuild guild, ITextChannel channel)
    {
        var adminRole = ResolveAdminRole(guild);
        var denyEveryone = new OverwritePermissions(viewChannel: PermValue.Deny);
        await channel.AddPermissionOverwriteAsync(guild.EveryoneRole, denyEveryone);

        if (adminRole is not null)
        {
            var allowAdmin = new OverwritePermissions(
                viewChannel: PermValue.Allow,
                readMessageHistory: PermValue.Allow,
                sendMessages: PermValue.Allow,
                manageMessages: PermValue.Allow,
                useApplicationCommands: PermValue.Allow);
            await channel.AddPermissionOverwriteAsync(adminRole, allowAdmin);
        }
    }

    private async Task NotifyInquiryToLogAsync(SocketGuild guild, SocketGuildUser requester, ITextChannel inquiryChannel, string subject, int ticketId)
    {
        var normalizedSubject = string.IsNullOrWhiteSpace(subject) ? "未指定" : subject;
        var description = $"お問い合わせが来ました。\nチケット: #{ticketId}\n投稿者: {requester.Mention} ({requester})\n件名: {normalizedSubject}\n対応チャンネル: <#{inquiryChannel.Id}>";
        await _audit.SendAsync(guild, "お問い合わせ通知", description, Color.Orange, requester);
    }

    private static async Task<string> BuildTranscriptAsync(ITextChannel channel, int maxMessages)
    {
        var messages = await channel.GetMessagesAsync(Math.Clamp(maxMessages, 50, 500)).FlattenAsync();
        var ordered = messages
            .OrderBy(message => message.Timestamp)
            .ThenBy(message => message.Id)
            .ToList();

        var sb = new StringBuilder(capacity: Math.Max(4096, ordered.Count * 80));
        foreach (var message in ordered)
        {
            var time = message.Timestamp.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy/MM/dd HH:mm:ss");
            var author = message.Author?.ToString() ?? "(unknown)";
            var content = string.IsNullOrWhiteSpace(message.Content)
                ? "(no content)"
                : message.Content.Replace('\r', ' ').Replace('\n', ' ');
            sb.Append('[').Append(time).Append("] ").Append(author).Append(": ").AppendLine(content);

            if (message.Attachments.Count > 0)
            {
                foreach (var attachment in message.Attachments)
                {
                    sb.Append("  - attachment: ").Append(attachment.Url).AppendLine();
                }
            }
        }

        if (sb.Length == 0)
        {
            return "(empty transcript)";
        }

        const int maxTextLength = 180_000;
        if (sb.Length > maxTextLength)
        {
            return sb.ToString(0, maxTextLength) + "\n...(truncated)";
        }

        return sb.ToString();
    }

    private static string FormatTimestamp(DateTimeOffset value) => $"<t:{value.ToUnixTimeSeconds()}:F>";
    private static string MentionUser(ulong userId) => $"<@{userId}>";
    private static ulong ToUlong(long value) => unchecked((ulong)value);
    private static long ToDbId(ulong value) => unchecked((long)value);
}

public sealed class InquiryModal : IModal
{
    public string Title => "問い合わせ";

    [InputLabel("件名")]
    [ModalTextInput("inquiry_subject", maxLength: 80)]
    public string? Subject { get; set; }

    [InputLabel("内容")]
    [ModalTextInput("inquiry_detail", TextInputStyle.Paragraph, maxLength: 1000)]
    public string? Detail { get; set; }
}
