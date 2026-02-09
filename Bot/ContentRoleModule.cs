using System.Text.RegularExpressions;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

[Group("content", "コンテンツロール")]
public sealed class ContentRoleModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly Db _db;
    private readonly GuildAuditLogService _audit;
    private readonly ILogger<ContentRoleModule> _logger;

    public ContentRoleModule(Db db, GuildAuditLogService audit, ILogger<ContentRoleModule> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    [RequireBotAdmin]
    [SlashCommand("setup", "このチャンネルにコンテンツロールパネルを設置します")]
    public async Task SetupAsync()
    {
        var channel = await ResolveSetupChannelAsync();
        if (channel is null)
        {
            return;
        }

        await DeferAsync(ephemeral: true);

        var message = await EnsureContentRolePanelMessageAsync(channel);
        await FollowupAsync($"このチャンネルにコンテンツロールパネルを設置しました。(message: {message.Id})", ephemeral: true);
    }

    [RequireBotAdmin]
    [SlashCommand("add", "コンテンツロールのボタンを追加/更新します")]
    public async Task AddAsync(
        [Summary("label", "ボタン表示名")] string label,
        [Summary("role_name", "付与するロール名")] string roleName,
        [Summary("style", "Primary / Secondary / Success / Danger")] string style = "Secondary",
        [Summary("row", "0-4")] int row = 1,
        [Summary("key", "任意: 管理キー (未指定なら自動)")] string key = "")
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var trimmedLabel = label?.Trim() ?? string.Empty;
        var trimmedRoleName = roleName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedLabel) || string.IsNullOrWhiteSpace(trimmedRoleName))
        {
            await RespondAsync("label と role_name は必須です。", ephemeral: true);
            return;
        }

        if (row < 0 || row > 4)
        {
            await RespondAsync("row は 0〜4 で指定してください。", ephemeral: true);
            return;
        }

        if (!TryParseStyle(style, out var parsedStyle))
        {
            await RespondAsync("style は Primary / Secondary / Success / Danger のいずれかです。", ephemeral: true);
            return;
        }

        var existing = await _db.ListMemberContentRoleSettingsAsync(Context.Guild.Id);
        var explicitKeyProvided = !string.IsNullOrWhiteSpace(key);
        var finalKey = BuildContentRoleKey(key, trimmedLabel);
        if (!explicitKeyProvided && existing.Any(x => string.Equals(x.RoleKey, finalKey, StringComparison.OrdinalIgnoreCase)))
        {
            finalKey = BuildUniqueContentRoleKey(finalKey, existing);
        }

        await _db.UpsertMemberContentRoleSettingAsync(
            Context.Guild.Id,
            finalKey,
            trimmedLabel,
            trimmedRoleName,
            (int)parsedStyle,
            row);

        await RespondAsync($"コンテンツロールを追加/更新しました。key: `{finalKey}`", ephemeral: true);
    }

    [RequireBotAdmin]
    [SlashCommand("remove", "追加したコンテンツロールを無効化します")]
    public async Task RemoveAsync([Summary("key", "削除する管理キー")] string key)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var finalKey = BuildContentRoleKey(key, key);
        if (string.IsNullOrWhiteSpace(finalKey))
        {
            await RespondAsync("key を指定してください。", ephemeral: true);
            return;
        }

        await _db.DisableMemberContentRoleSettingAsync(Context.Guild.Id, finalKey);
        await RespondAsync($"コンテンツロールを無効化しました。key: `{finalKey}`", ephemeral: true);
    }

    [RequireBotAdmin]
    [SlashCommand("list", "追加したコンテンツロールの一覧を表示します")]
    public async Task ListAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var settings = await _db.ListMemberContentRoleSettingsAsync(Context.Guild.Id);
        if (settings.Count == 0)
        {
            await RespondAsync("追加済みのコンテンツロールはありません。", ephemeral: true);
            return;
        }

        var lines = settings
            .Select(x => $"`{x.RoleKey}` | {x.Label} | {x.RoleName} | style={((ButtonStyle)x.ButtonStyle)} | row={x.ButtonRow}");
        await RespondAsync(string.Join('\n', lines), ephemeral: true);
    }

    [ComponentInteraction("content_role:*", ignoreGroupNames: true)]
    public Task ToggleContentRoleAsync(string key)
        => ToggleContentRoleInternalAsync(key);

    [ComponentInteraction("member_content_role:*", ignoreGroupNames: true)]
    public Task ToggleLegacyContentRoleAsync(string key)
        => ToggleContentRoleInternalAsync(key);

    private async Task ToggleContentRoleInternalAsync(string key)
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await ReplyEphemeralAsync("この操作はサーバー内でのみ使えます。");
            return;
        }

        await DeferAsync(ephemeral: true);

        var definitions = await LoadContentRoleDefinitionsAsync(Context.Guild.Id);
        if (!MemberContentRolePanel.TryResolveRole(definitions, key, out var definition))
        {
            await _audit.SendAsync(
                Context.Guild,
                "無効なコンテンツロール操作",
                $"👤 ユーザー: {guildUser.Mention} ({guildUser})\n🔑 key: {key}",
                Color.Orange,
                guildUser);
            await ReplyEphemeralAsync("対象コンテンツを判定できませんでした。");
            return;
        }

        IRole? role = Context.Guild.Roles.FirstOrDefault(r =>
            string.Equals(r.Name, definition.RoleName, StringComparison.OrdinalIgnoreCase));
        if (role is null)
        {
            try
            {
                role = await Context.Guild.CreateRoleAsync(
                    definition.RoleName,
                    GuildPermissions.None,
                    color: new Color(127, 140, 141),
                    isHoisted: false,
                    isMentionable: false);

                await _audit.SendSystemAsync(
                    Context.Guild,
                    "コンテンツロール自動作成",
                    $"👤 操作ユーザー: {guildUser.Mention} ({guildUser})\n🧩 ロール: {definition.RoleName}\n🔑 key: {definition.Key}",
                    Color.LightGrey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create content role {RoleName}.", definition.RoleName);
                await _audit.SendAsync(
                    Context.Guild,
                    "コンテンツロール作成失敗",
                    $"👤 操作ユーザー: {guildUser.Mention} ({guildUser})\n🧩 ロール: {definition.RoleName}\n🔑 key: {definition.Key}\n❌ Error: {ex.Message}",
                    Color.Red,
                    guildUser);
                await ReplyEphemeralAsync("ロール作成に失敗しました。Botの権限を確認してください。");
                return;
            }
        }

        try
        {
            if (!TryValidateRoleOperation(Context.Guild, guildUser, role, out var reason))
            {
                _logger.LogWarning(
                    "Skipped content role toggle due to permission/hierarchy. GuildId={GuildId}, UserId={UserId}, RoleId={RoleId}, Key={Key}, Reason={Reason}",
                    Context.Guild.Id,
                    guildUser.Id,
                    role.Id,
                    definition.Key,
                    reason);
                await _audit.SendAsync(
                    Context.Guild,
                    "コンテンツロール変更スキップ",
                    $"👤 ユーザー: {guildUser.Mention} ({guildUser})\n🧩 ロール: {role.Name}\n🏷️ ラベル: {definition.Label}\n🔑 key: {definition.Key}\n⚠️ 理由: {reason}",
                    Color.Orange,
                    guildUser);
                await ReplyEphemeralAsync($"ロール変更できません: {reason}");
                return;
            }

            var hasRole = guildUser.Roles.Any(r => r.Id == role.Id);
            if (hasRole)
            {
                await guildUser.RemoveRoleAsync(role);
                await _audit.SendAsync(
                    Context.Guild,
                    "コンテンツロール解除",
                    $"👤 ユーザー: {guildUser.Mention} ({guildUser})\n🧩 ロール: {role.Name}\n🏷️ ラベル: {definition.Label}\n🔑 key: {definition.Key}",
                    Color.Orange,
                    guildUser);
                await ReplyEphemeralAsync($"ロールを外しました: {definition.Label}");
                return;
            }

            await guildUser.AddRoleAsync(role);
            await _audit.SendAsync(
                Context.Guild,
                "コンテンツロール付与",
                $"👤 ユーザー: {guildUser.Mention} ({guildUser})\n🧩 ロール: {role.Name}\n🏷️ ラベル: {definition.Label}\n🔑 key: {definition.Key}",
                Color.Green,
                guildUser);
            await ReplyEphemeralAsync($"ロールを付与しました: {definition.Label}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to toggle content role {RoleName} for user {UserId}.", definition.RoleName, guildUser.Id);
            await _audit.SendAsync(
                Context.Guild,
                "コンテンツロール変更失敗",
                $"👤 ユーザー: {guildUser.Mention} ({guildUser})\n🧩 ロール: {definition.RoleName}\n🏷️ ラベル: {definition.Label}\n🔑 key: {definition.Key}\n❌ Error: {ex.Message}",
                Color.Red,
                guildUser);
            await ReplyEphemeralAsync("ロール変更に失敗しました。Botの権限を確認してください。");
        }
    }

    private Task ReplyEphemeralAsync(string text)
    {
        if (Context.Interaction.HasResponded)
        {
            return FollowupAsync(text, ephemeral: true);
        }

        return RespondAsync(text, ephemeral: true);
    }

    private static bool TryValidateRoleOperation(SocketGuild guild, SocketGuildUser targetUser, IRole role, out string reason)
    {
        var botUser = guild.CurrentUser;
        if (botUser is null)
        {
            reason = "Botユーザーを取得できませんでした。";
            return false;
        }

        if (!botUser.GuildPermissions.ManageRoles)
        {
            reason = "Botに「ロールの管理」権限がありません。";
            return false;
        }

        if (role.Position >= botUser.Hierarchy)
        {
            reason = $"Botのロール位置が `{role.Name}` 以下です。";
            return false;
        }

        if (targetUser.Id == guild.OwnerId)
        {
            reason = "サーバーオーナーのロールは変更できません。";
            return false;
        }

        if (targetUser.Id != botUser.Id && targetUser.Hierarchy >= botUser.Hierarchy)
        {
            reason = "対象ユーザーのロール階層がBot以上です。";
            return false;
        }

        reason = string.Empty;
        return true;
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

    private async Task<IUserMessage> EnsureContentRolePanelMessageAsync(IMessageChannel channel)
    {
        var recent = await channel.GetMessagesAsync(100).FlattenAsync();
        var existing = recent
            .OfType<IUserMessage>()
            .FirstOrDefault(message =>
                message.Author.Id == Context.Client.CurrentUser.Id &&
                MemberContentRolePanel.LooksLikePanelMessage(message.Content));

        if (Context.Guild is null)
        {
            throw new InvalidOperationException("Guild context is required.");
        }

        var definitions = await LoadContentRoleDefinitionsAsync(Context.Guild.Id);
        var components = MemberContentRolePanel.BuildComponents(definitions);
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = MemberContentRolePanel.PanelMessage;
                props.Components = components;
            });
            return existing;
        }

        return await channel.SendMessageAsync(MemberContentRolePanel.PanelMessage, components: components, flags: MessageFlags.SuppressNotification);
    }

    private async Task<IReadOnlyList<ContentRoleDefinition>> LoadContentRoleDefinitionsAsync(ulong guildId)
    {
        var defaults = MemberContentRolePanel.DefaultDefinitions.ToList();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < defaults.Count; i++)
        {
            indexByKey[defaults[i].Key] = i;
        }

        var custom = await _db.ListMemberContentRoleSettingsAsync(guildId);
        foreach (var row in custom)
        {
            var definition = new ContentRoleDefinition(
                row.RoleKey,
                row.Label,
                row.RoleName,
                NormalizeButtonStyle(row.ButtonStyle),
                Math.Clamp(row.ButtonRow, 0, 4));

            if (indexByKey.TryGetValue(definition.Key, out var existingIndex))
            {
                defaults[existingIndex] = definition;
                continue;
            }

            indexByKey[definition.Key] = defaults.Count;
            defaults.Add(definition);
        }

        return defaults
            .OrderBy(x => x.Row)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();
    }

    private static bool TryParseStyle(string raw, out ButtonStyle style)
    {
        style = ButtonStyle.Secondary;
        var trimmed = raw?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return true;
        }

        if (int.TryParse(trimmed, out var numeric))
        {
            style = NormalizeButtonStyle(numeric);
            return numeric is >= 1 and <= 4;
        }

        if (trimmed.Equals("primary", StringComparison.OrdinalIgnoreCase))
        {
            style = ButtonStyle.Primary;
            return true;
        }

        if (trimmed.Equals("secondary", StringComparison.OrdinalIgnoreCase))
        {
            style = ButtonStyle.Secondary;
            return true;
        }

        if (trimmed.Equals("success", StringComparison.OrdinalIgnoreCase))
        {
            style = ButtonStyle.Success;
            return true;
        }

        if (trimmed.Equals("danger", StringComparison.OrdinalIgnoreCase))
        {
            style = ButtonStyle.Danger;
            return true;
        }

        return false;
    }

    private static ButtonStyle NormalizeButtonStyle(int style)
    {
        return style switch
        {
            1 => ButtonStyle.Primary,
            2 => ButtonStyle.Secondary,
            3 => ButtonStyle.Success,
            4 => ButtonStyle.Danger,
            _ => ButtonStyle.Secondary
        };
    }

    private static string BuildContentRoleKey(string rawKey, string fallbackLabel)
    {
        var source = string.IsNullOrWhiteSpace(rawKey) ? fallbackLabel : rawKey;
        var normalized = source.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+", "_");
        normalized = Regex.Replace(normalized, @"[^a-z0-9_\-]", "_");
        normalized = Regex.Replace(normalized, @"_+", "_").Trim('_');
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized[..Math.Min(normalized.Length, 40)];
        }

        return $"custom_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private static string BuildUniqueContentRoleKey(string baseKey, IReadOnlyList<MemberContentRoleSetting> existing)
    {
        var index = 2;
        var candidate = baseKey;
        while (existing.Any(x => string.Equals(x.RoleKey, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseKey}_{index}";
            if (candidate.Length > 40)
            {
                candidate = candidate[..40];
            }

            index++;
        }

        return candidate;
    }
}
