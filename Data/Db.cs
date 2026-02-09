using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using RaidDcBot.Models;

namespace RaidDcBot.Data;

public sealed class Db
{
    private readonly string _connectionString;

    public Db(IOptions<DatabaseOptions> options)
    {
        _connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("Database:ConnectionString is required.");
        }
    }

    private NpgsqlConnection CreateConnection()
    {
        return new NpgsqlConnection(_connectionString);
    }

    public async Task EnsureRecruitRequiredJobColumnAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS recruit_slots
ADD COLUMN IF NOT EXISTS required_job TEXT NULL;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureDiscordUserProfileCharacterColumnsAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS discord_users
ADD COLUMN IF NOT EXISTS ffxiv_name TEXT NULL,
ADD COLUMN IF NOT EXISTS ffxiv_world TEXT NULL,
ADD COLUMN IF NOT EXISTS ffxiv_data_center TEXT NULL;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureDiscordUserVoiceNotificationColumnAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS discord_users
ADD COLUMN IF NOT EXISTS voice_notification_enabled BOOLEAN NOT NULL DEFAULT TRUE;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureGuildLodestonePanelTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS guild_lodestone_panels (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL UNIQUE,
    channel_id BIGINT NOT NULL,
    message_id BIGINT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureGuildAdminControlPanelTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS guild_admin_control_panels (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL UNIQUE,
    channel_id BIGINT NOT NULL,
    message_id BIGINT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureGuildMemberContentRolesTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS guild_member_content_roles (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    role_key TEXT NOT NULL,
    label TEXT NOT NULL,
    role_name TEXT NOT NULL,
    button_style SMALLINT NOT NULL DEFAULT 2,
    button_row SMALLINT NOT NULL DEFAULT 0,
    sort_order INTEGER NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_guild_member_content_roles UNIQUE (guild_id, role_key)
);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureGuildStatsPanelTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS guild_stats_panels (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL UNIQUE,
    channel_id BIGINT NOT NULL,
    data_center_message_id BIGINT NOT NULL,
    world_message_id BIGINT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureGuildDailyActivityPanelTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS guild_daily_activity_panels (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL UNIQUE,
    channel_id BIGINT NOT NULL,
    message_id BIGINT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureGuildWeeklyActivityPanelTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS guild_weekly_activity_panels (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL UNIQUE,
    channel_id BIGINT NOT NULL,
    message_id BIGINT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureGuildSettingsColumnsAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS public.guild_settings
ADD COLUMN IF NOT EXISTS provisional_role_id BIGINT NOT NULL DEFAULT 0,
ADD COLUMN IF NOT EXISTS registration_channel_id BIGINT NOT NULL DEFAULT 0,
ADD COLUMN IF NOT EXISTS registration_review_channel_id BIGINT NOT NULL DEFAULT 0,
ADD COLUMN IF NOT EXISTS registration_approval_required BOOLEAN NOT NULL DEFAULT TRUE,
ADD COLUMN IF NOT EXISTS inactive_auto_kick_enabled BOOLEAN NOT NULL DEFAULT TRUE,
ADD COLUMN IF NOT EXISTS log_channel_id BIGINT NOT NULL DEFAULT 0,
ADD COLUMN IF NOT EXISTS control_channel_id BIGINT NOT NULL DEFAULT 0;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureRecruitTemplatesTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS public.recruit_templates (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    template_key TEXT NOT NULL,
    title TEXT NOT NULL,
    data_center TEXT NULL,
    note TEXT NULL,
    positions_text TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_recruit_templates UNIQUE (guild_id, template_key)
);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureMessageLogPayloadColumnsAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS public.message_logs
ADD COLUMN IF NOT EXISTS author_display_name TEXT NULL,
ADD COLUMN IF NOT EXISTS attachments_json JSONB NULL,
ADD COLUMN IF NOT EXISTS embeds_json JSONB NULL;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureUserReactionLogsTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS public.user_reaction_logs (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    channel_id BIGINT NOT NULL,
    message_id BIGINT NOT NULL,
    user_id BIGINT NOT NULL,
    emoji_key TEXT NOT NULL,
    reacted_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_user_reaction_logs UNIQUE (guild_id, user_id, message_id, emoji_key)
);

CREATE INDEX IF NOT EXISTS idx_user_reaction_logs_guild_reacted_at
ON public.user_reaction_logs (guild_id, reacted_at DESC);

CREATE INDEX IF NOT EXISTS idx_user_reaction_logs_user
ON public.user_reaction_logs (user_id);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureDiscordUserAsync(ulong userId)
    {
        const string sql = "INSERT INTO discord_users (discord_user_id) VALUES (@UserId) ON CONFLICT DO NOTHING;";
        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new { UserId = ToDbId(userId) });
    }

    public async Task UpsertDiscordUserProfileAsync(ulong userId, string displayName, string discordName)
    {
        const string sql = @"
INSERT INTO discord_users (discord_user_id, display_name, discord_name)
VALUES (@UserId, @DisplayName, @DiscordName)
ON CONFLICT (discord_user_id) DO UPDATE
SET display_name = EXCLUDED.display_name,
    discord_name = EXCLUDED.discord_name,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            UserId = ToDbId(userId),
            DisplayName = displayName,
            DiscordName = discordName
        });
    }

    public async Task UpdateUserProfileAsync(ulong userId, string? xHandle, string? blueskyHandle, string? bio)
    {
        const string sql = @"
UPDATE discord_users
SET x_handle = @XHandle,
    bluesky_handle = @BlueskyHandle,
    bio = @Bio,
    updated_at = NOW()
WHERE discord_user_id = @UserId;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            UserId = ToDbId(userId),
            XHandle = NullIfBlank(xHandle),
            BlueskyHandle = NullIfBlank(blueskyHandle),
            Bio = NullIfBlank(bio)
        });
    }

    public async Task UpdateUserRegistrationProfileAsync(ulong userId, string ffxivName, string ffxivWorld, string ffxivDataCenter, string? xHandle, string? blueskyHandle, string bio)
    {
        const string sql = @"
UPDATE discord_users
SET ffxiv_name = @FfxivName,
    ffxiv_world = @FfxivWorld,
    ffxiv_data_center = @FfxivDataCenter,
    x_handle = @XHandle,
    bluesky_handle = @BlueskyHandle,
    bio = @Bio,
    updated_at = NOW()
WHERE discord_user_id = @UserId;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            UserId = ToDbId(userId),
            FfxivName = NullIfBlank(ffxivName),
            FfxivWorld = NullIfBlank(ffxivWorld),
            FfxivDataCenter = NullIfBlank(ffxivDataCenter),
            XHandle = NullIfBlank(xHandle),
            BlueskyHandle = NullIfBlank(blueskyHandle),
            Bio = NullIfBlank(bio)
        });
    }

    public async Task<bool> GetVoiceNotificationEnabledAsync(ulong userId)
    {
        const string sql = @"
SELECT voice_notification_enabled
FROM discord_users
WHERE discord_user_id = @UserId
LIMIT 1;";

        await using var conn = CreateConnection();
        var value = await conn.ExecuteScalarAsync<bool?>(sql, new { UserId = ToDbId(userId) });
        return value ?? true;
    }

    public async Task<bool> ToggleVoiceNotificationEnabledAsync(ulong userId)
    {
        const string sql = @"
INSERT INTO discord_users (discord_user_id, voice_notification_enabled)
VALUES (@UserId, FALSE)
ON CONFLICT (discord_user_id) DO UPDATE
SET voice_notification_enabled = NOT discord_users.voice_notification_enabled,
    updated_at = NOW()
RETURNING voice_notification_enabled;";

        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(sql, new { UserId = ToDbId(userId) });
    }

    public async Task<DiscordUserProfile?> GetDiscordUserProfileAsync(ulong userId)
    {
        const string sql = @"
SELECT discord_user_id AS DiscordUserId,
       display_name AS DisplayName,
       discord_name AS DiscordName,
       ffxiv_name AS FfxivName,
       ffxiv_world AS FfxivWorld,
       ffxiv_data_center AS FfxivDataCenter,
       x_handle AS XHandle,
       bluesky_handle AS BlueskyHandle,
       bio AS Bio,
       updated_at AS UpdatedAt
FROM discord_users
WHERE discord_user_id = @UserId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<DiscordUserProfile>(sql, new { UserId = ToDbId(userId) });
    }

    public async Task<IReadOnlyList<DiscordUserProfile>> ListDiscordUserProfilesAsync(IReadOnlyCollection<ulong> userIds)
    {
        if (userIds.Count == 0)
        {
            return Array.Empty<DiscordUserProfile>();
        }

        const string sql = @"
SELECT discord_user_id AS DiscordUserId,
       display_name AS DisplayName,
       discord_name AS DiscordName,
       ffxiv_name AS FfxivName,
       ffxiv_world AS FfxivWorld,
       ffxiv_data_center AS FfxivDataCenter,
       x_handle AS XHandle,
       bluesky_handle AS BlueskyHandle,
       bio AS Bio,
       updated_at AS UpdatedAt
FROM discord_users
WHERE discord_user_id = ANY(@UserIds);";

        var ids = userIds.Select(ToDbId).ToArray();
        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<DiscordUserProfile>(sql, new { UserIds = ids });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<DiscordUserProfile>> ListRegisteredDiscordUserProfilesAsync()
    {
        const string sql = @"
SELECT discord_user_id AS DiscordUserId,
       display_name AS DisplayName,
       discord_name AS DiscordName,
       ffxiv_name AS FfxivName,
       ffxiv_world AS FfxivWorld,
       ffxiv_data_center AS FfxivDataCenter,
       x_handle AS XHandle,
       bluesky_handle AS BlueskyHandle,
       bio AS Bio,
       updated_at AS UpdatedAt
FROM discord_users
WHERE COALESCE(TRIM(ffxiv_name), '') <> ''
  AND COALESCE(TRIM(ffxiv_world), '') <> ''
  AND COALESCE(TRIM(ffxiv_data_center), '') <> '';";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<DiscordUserProfile>(sql);
        return rows.ToList();
    }

    public async Task InvalidateActiveTokensAsync(ulong userId)
    {
        const string sql = @"
UPDATE link_tokens
SET used_at = NOW()
WHERE discord_user_id = @DiscordUserId
  AND used_at IS NULL;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new { DiscordUserId = ToDbId(userId) });
    }

    public async Task<LinkToken> CreateLinkTokenAsync(ulong userId, string token, DateTimeOffset expiresAt)
    {
        const string sql = @"
INSERT INTO link_tokens (token_id, discord_user_id, lodestone_id, lodestone_url, label, token, expires_at)
VALUES (@TokenId, @DiscordUserId, NULL, NULL, 'main', @Token, @ExpiresAt)
RETURNING token_id AS TokenId,
          discord_user_id AS DiscordUserId,
          lodestone_id AS LodestoneId,
          lodestone_url AS LodestoneUrl,
          label AS Label,
          token AS Token,
          expires_at AS ExpiresAt,
          created_at AS CreatedAt,
          used_at AS UsedAt;";

        var parameters = new
        {
            TokenId = Guid.NewGuid(),
            DiscordUserId = ToDbId(userId),
            Token = token,
            ExpiresAt = expiresAt
        };

        await using var conn = CreateConnection();
        return await conn.QuerySingleAsync<LinkToken>(sql, parameters);
    }

    public async Task<LinkToken?> GetActiveTokenAsync(ulong userId)
    {
        const string sql = @"
SELECT token_id AS TokenId,
       discord_user_id AS DiscordUserId,
       lodestone_id AS LodestoneId,
       lodestone_url AS LodestoneUrl,
       label AS Label,
       token AS Token,
       expires_at AS ExpiresAt,
       created_at AS CreatedAt,
       used_at AS UsedAt
FROM link_tokens
WHERE discord_user_id = @DiscordUserId
  AND used_at IS NULL
  AND expires_at > NOW()
ORDER BY created_at DESC
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<LinkToken>(sql, new
        {
            DiscordUserId = ToDbId(userId)
        });
    }

    public async Task MarkTokenUsedAsync(Guid tokenId)
    {
        const string sql = "UPDATE link_tokens SET used_at = NOW() WHERE token_id = @TokenId;";
        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new { TokenId = tokenId });
    }

    public async Task UpsertCharacterAsync(LodestoneCharacter character)
    {
        const string sql = @"
INSERT INTO lodestone_characters (lodestone_id, name, world, data_center, portrait_url, lodestone_url)
VALUES (@LodestoneId, @Name, @World, @DataCenter, @PortraitUrl, @LodestoneUrl)
ON CONFLICT (lodestone_id) DO UPDATE
SET name = EXCLUDED.name,
    world = EXCLUDED.world,
    data_center = EXCLUDED.data_center,
    portrait_url = EXCLUDED.portrait_url,
    self_introduction = NULL,
    lodestone_url = EXCLUDED.lodestone_url,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, character);
    }

    public async Task UpsertMessageAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        ulong authorId,
        string authorName,
        string? authorDisplayName,
        string content,
        int attachmentCount,
        int embedCount,
        string? attachmentsJson,
        string? embedsJson,
        DateTimeOffset createdAt)
    {
        const string sql = @"
INSERT INTO message_logs (message_id, guild_id, channel_id, author_id, author_name, author_display_name, content, attachment_count, embed_count, attachments_json, embeds_json, created_at)
VALUES (@MessageId, @GuildId, @ChannelId, @AuthorId, @AuthorName, @AuthorDisplayName, @Content, @AttachmentCount, @EmbedCount, CAST(@AttachmentsJson AS JSONB), CAST(@EmbedsJson AS JSONB), @CreatedAt)
ON CONFLICT (message_id) DO UPDATE
SET author_id = EXCLUDED.author_id,
    author_name = EXCLUDED.author_name,
    author_display_name = EXCLUDED.author_display_name,
    content = EXCLUDED.content,
    attachment_count = EXCLUDED.attachment_count,
    embed_count = EXCLUDED.embed_count,
    attachments_json = EXCLUDED.attachments_json,
    embeds_json = EXCLUDED.embeds_json;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            MessageId = ToDbId(messageId),
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            AuthorId = ToDbId(authorId),
            AuthorName = authorName,
            AuthorDisplayName = NullIfBlank(authorDisplayName),
            Content = content,
            AttachmentCount = attachmentCount,
            EmbedCount = embedCount,
            AttachmentsJson = attachmentsJson,
            EmbedsJson = embedsJson,
            CreatedAt = createdAt
        });
    }

    public async Task UpsertReactionAsync(ulong guildId, ulong channelId, ulong messageId, ulong userId, string emojiKey, DateTimeOffset reactedAt)
    {
        const string sql = @"
INSERT INTO public.user_reaction_logs (guild_id, channel_id, message_id, user_id, emoji_key, reacted_at)
VALUES (@GuildId, @ChannelId, @MessageId, @UserId, @EmojiKey, @ReactedAt)
ON CONFLICT (guild_id, user_id, message_id, emoji_key) DO UPDATE
SET reacted_at = EXCLUDED.reacted_at,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            MessageId = ToDbId(messageId),
            UserId = ToDbId(userId),
            EmojiKey = string.IsNullOrWhiteSpace(emojiKey) ? "unknown" : emojiKey,
            ReactedAt = reactedAt
        });
    }

    public async Task UpdateMessageAsync(ulong messageId, string content, int attachmentCount, int embedCount, string? attachmentsJson, string? embedsJson)
    {
        const string sql = @"
UPDATE message_logs
SET content = @Content,
    attachment_count = @AttachmentCount,
    embed_count = @EmbedCount,
    attachments_json = CAST(@AttachmentsJson AS JSONB),
    embeds_json = CAST(@EmbedsJson AS JSONB),
    edited_at = NOW()
WHERE message_id = @MessageId;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            MessageId = ToDbId(messageId),
            Content = content,
            AttachmentCount = attachmentCount,
            EmbedCount = embedCount,
            AttachmentsJson = attachmentsJson,
            EmbedsJson = embedsJson
        });
    }

    public async Task MarkMessageDeletedAsync(ulong messageId)
    {
        const string sql = @"
UPDATE message_logs
SET deleted_at = NOW()
WHERE message_id = @MessageId;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new { MessageId = ToDbId(messageId) });
    }

    public async Task<MessageLog?> GetMessageAsync(ulong messageId)
    {
        const string sql = @"
SELECT message_id AS MessageId,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       author_id AS AuthorId,
       author_name AS AuthorName,
       author_display_name AS AuthorDisplayName,
       content AS Content,
       attachment_count AS AttachmentCount,
       embed_count AS EmbedCount,
       attachments_json::text AS AttachmentsJson,
       embeds_json::text AS EmbedsJson,
       created_at AS CreatedAt,
       edited_at AS EditedAt,
       deleted_at AS DeletedAt
FROM message_logs
WHERE message_id = @MessageId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<MessageLog>(sql, new { MessageId = ToDbId(messageId) });
    }

    public async Task<int> CleanupMessageLogsAsync(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        const string sql = @"
DELETE FROM message_logs
WHERE created_at < NOW() - (@Days * INTERVAL '1 day');";

        await using var conn = CreateConnection();
        return await conn.ExecuteAsync(sql, new { Days = retentionDays });
    }

    public async Task<VoiceSession> StartVoiceSessionAsync(ulong guildId, ulong channelId, ulong userId, string userName, DateTimeOffset joinedAt)
    {
        const string sql = @"
INSERT INTO voice_sessions (guild_id, channel_id, user_id, user_name, joined_at)
VALUES (@GuildId, @ChannelId, @UserId, @UserName, @JoinedAt)
RETURNING id AS Id,
          guild_id AS GuildId,
          channel_id AS ChannelId,
          user_id AS UserId,
          user_name AS UserName,
          joined_at AS JoinedAt,
          left_at AS LeftAt,
          duration_seconds AS DurationSeconds;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleAsync<VoiceSession>(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            UserId = ToDbId(userId),
            UserName = userName,
            JoinedAt = joinedAt
        });
    }

    public async Task<VoiceSession?> EndVoiceSessionAsync(ulong guildId, ulong channelId, ulong userId, DateTimeOffset leftAt)
    {
        const string sql = @"
WITH target AS (
    SELECT id
    FROM voice_sessions
    WHERE guild_id = @GuildId
      AND channel_id = @ChannelId
      AND user_id = @UserId
      AND left_at IS NULL
    ORDER BY joined_at DESC
    LIMIT 1
)
UPDATE voice_sessions v
SET left_at = @LeftAt,
    duration_seconds = GREATEST(0, EXTRACT(EPOCH FROM (@LeftAt - v.joined_at))::int)
FROM target
WHERE v.id = target.id
RETURNING v.id AS Id,
          v.guild_id AS GuildId,
          v.channel_id AS ChannelId,
          v.user_id AS UserId,
          v.user_name AS UserName,
          v.joined_at AS JoinedAt,
          v.left_at AS LeftAt,
          v.duration_seconds AS DurationSeconds;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<VoiceSession>(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            UserId = ToDbId(userId),
            LeftAt = leftAt
        });
    }

    public async Task<VoiceSession?> GetOpenVoiceSessionAsync(ulong guildId, ulong userId)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       user_id AS UserId,
       user_name AS UserName,
       joined_at AS JoinedAt,
       left_at AS LeftAt,
       duration_seconds AS DurationSeconds
FROM voice_sessions
WHERE guild_id = @GuildId
  AND user_id = @UserId
  AND left_at IS NULL
ORDER BY joined_at DESC
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<VoiceSession>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId)
        });
    }

    public async Task<IReadOnlyList<VoiceSession>> ListOpenVoiceSessionsAsync(ulong guildId, ulong channelId)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       user_id AS UserId,
       user_name AS UserName,
       joined_at AS JoinedAt,
       left_at AS LeftAt,
       duration_seconds AS DurationSeconds
FROM voice_sessions
WHERE guild_id = @GuildId
  AND channel_id = @ChannelId
  AND left_at IS NULL
ORDER BY joined_at ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<VoiceSession>(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId)
        });
        return rows.ToList();
    }

    public async Task<int> CleanupVoiceSessionsAsync(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        const string sql = @"
DELETE FROM voice_sessions
WHERE left_at IS NOT NULL
  AND left_at < NOW() - (@Days * INTERVAL '1 day');";

        await using var conn = CreateConnection();
        return await conn.ExecuteAsync(sql, new { Days = retentionDays });
    }

    public async Task<int> CleanupReactionLogsAsync(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        const string sql = @"
DELETE FROM public.user_reaction_logs
WHERE reacted_at < NOW() - (@Days * INTERVAL '1 day');";

        await using var conn = CreateConnection();
        return await conn.ExecuteAsync(sql, new { Days = retentionDays });
    }

    public async Task<IReadOnlyList<VoiceDurationSummary>> ListVoiceDurationSummaryAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive,
        int topCount)
    {
        const string sql = @"
SELECT user_id AS UserId,
       SUM(
           GREATEST(
               0,
               EXTRACT(EPOCH FROM (LEAST(left_at, @ToUtcExclusive) - GREATEST(joined_at, @FromUtcInclusive)))
           )
       )::BIGINT AS DurationSeconds
FROM voice_sessions
WHERE guild_id = @GuildId
  AND left_at IS NOT NULL
  AND left_at > @FromUtcInclusive
  AND joined_at < @ToUtcExclusive
GROUP BY user_id
HAVING SUM(
           GREATEST(
               0,
               EXTRACT(EPOCH FROM (LEAST(left_at, @ToUtcExclusive) - GREATEST(joined_at, @FromUtcInclusive)))
           )
       ) > 0
ORDER BY DurationSeconds DESC, user_id ASC
LIMIT @TopCount;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<VoiceDurationSummary>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive,
            TopCount = Math.Clamp(topCount, 1, 50)
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyCollection<ulong>> ListActiveUserIdsSinceAsync(ulong guildId, DateTimeOffset sinceUtc)
    {
        const string sql = @"
SELECT DISTINCT x.user_id
FROM (
    SELECT author_id AS user_id
    FROM message_logs
    WHERE guild_id = @GuildId
      AND deleted_at IS NULL
      AND created_at >= @SinceUtc
    UNION
    SELECT user_id AS user_id
    FROM voice_sessions
    WHERE guild_id = @GuildId
      AND (
          left_at IS NULL
          OR left_at >= @SinceUtc
          OR joined_at >= @SinceUtc
      )
    UNION
    SELECT user_id AS user_id
    FROM public.user_reaction_logs
    WHERE guild_id = @GuildId
      AND reacted_at >= @SinceUtc
) x;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<long>(sql, new
        {
            GuildId = ToDbId(guildId),
            SinceUtc = sinceUtc
        });

        return rows
            .Select(ToUlong)
            .ToHashSet();
    }

    public async Task<IReadOnlyList<MessageCountByChannel>> ListMessageCountsByChannelAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive)
    {
        const string sql = @"
SELECT channel_id AS ChannelId,
       author_id AS UserId,
       COUNT(1)::INT AS MessageCount
FROM message_logs
WHERE guild_id = @GuildId
  AND deleted_at IS NULL
  AND created_at >= @FromUtcInclusive
  AND created_at < @ToUtcExclusive
GROUP BY channel_id, author_id;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<MessageCountByChannel>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<MessageLog>> ListMessageLogsForExportAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive,
        int maxRows = 200000)
    {
        const string sql = @"
SELECT message_id AS MessageId,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       author_id AS AuthorId,
       author_name AS AuthorName,
       author_display_name AS AuthorDisplayName,
       content AS Content,
       attachment_count AS AttachmentCount,
       embed_count AS EmbedCount,
       attachments_json::text AS AttachmentsJson,
       embeds_json::text AS EmbedsJson,
       created_at AS CreatedAt,
       edited_at AS EditedAt,
       deleted_at AS DeletedAt
FROM message_logs
WHERE guild_id = @GuildId
  AND created_at >= @FromUtcInclusive
  AND created_at < @ToUtcExclusive
ORDER BY created_at ASC, message_id ASC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<MessageLog>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive,
            MaxRows = Math.Clamp(maxRows, 1, 500000)
        });

        return rows.ToList();
    }

    public async Task ReplaceMainCharacterAsync(ulong userId, long lodestoneId)
    {
        const string sql = @"
DELETE FROM discord_user_characters
WHERE discord_user_id = @DiscordUserId;

INSERT INTO discord_user_characters (discord_user_id, lodestone_id, label, is_main)
VALUES (@DiscordUserId, @LodestoneId, 'main', TRUE);";

        const string cleanup = @"
DELETE FROM lodestone_characters c
WHERE NOT EXISTS (
    SELECT 1
    FROM discord_user_characters duc
    WHERE duc.lodestone_id = c.lodestone_id
);";

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await conn.ExecuteAsync(sql, new
        {
            DiscordUserId = ToDbId(userId),
            LodestoneId = lodestoneId
        }, transaction: tx);

        await conn.ExecuteAsync(cleanup, transaction: tx);

        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<LinkedCharacter>> ListLinkedCharactersAsync(ulong userId)
    {
        const string sql = @"
SELECT c.lodestone_id AS LodestoneId,
       c.name AS Name,
       c.world AS World,
       c.data_center AS DataCenter,
       c.portrait_url AS PortraitUrl,
       c.self_introduction AS SelfIntroduction,
       duc.label AS Label,
       duc.is_main AS IsMain,
       c.lodestone_url AS LodestoneUrl,
       duc.linked_at AS LinkedAt,
       c.updated_at AS UpdatedAt
FROM discord_user_characters duc
JOIN lodestone_characters c ON c.lodestone_id = duc.lodestone_id
WHERE duc.discord_user_id = @DiscordUserId
ORDER BY duc.is_main DESC, duc.linked_at ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<LinkedCharacter>(sql, new { DiscordUserId = ToDbId(userId) });
        return rows.ToList();
    }

    public async Task<LinkedCharacter?> GetMainCharacterAsync(ulong userId)
    {
        const string sql = @"
SELECT c.lodestone_id AS LodestoneId,
       c.name AS Name,
       c.world AS World,
       c.data_center AS DataCenter,
       c.portrait_url AS PortraitUrl,
       c.self_introduction AS SelfIntroduction,
       duc.label AS Label,
       duc.is_main AS IsMain,
       c.lodestone_url AS LodestoneUrl,
       duc.linked_at AS LinkedAt,
       c.updated_at AS UpdatedAt
FROM discord_user_characters duc
JOIN lodestone_characters c ON c.lodestone_id = duc.lodestone_id
WHERE duc.discord_user_id = @DiscordUserId
ORDER BY duc.is_main DESC, duc.linked_at DESC
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<LinkedCharacter>(sql, new { DiscordUserId = ToDbId(userId) });
    }

    public async Task UpdateCharacterPortraitAsync(long lodestoneId, string portraitUrl)
    {
        const string sql = @"
UPDATE lodestone_characters
SET portrait_url = @PortraitUrl,
    updated_at = NOW()
WHERE lodestone_id = @LodestoneId;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            LodestoneId = lodestoneId,
            PortraitUrl = portraitUrl
        });
    }

    public async Task<RecruitPost> CreateRecruitPostAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        ulong creatorUserId,
        string title,
        string? dataCenter,
        string? scheduleText,
        DateTimeOffset? scheduleAt,
        string? note,
        IReadOnlyList<string> positions,
        IReadOnlyDictionary<string, string>? requiredJobs = null)
    {
        const string insertPostSql = @"
INSERT INTO recruit_posts (guild_id, channel_id, message_id, creator_user_id, title, data_center, schedule_text, schedule_at, note)
VALUES (@GuildId, @ChannelId, @MessageId, @CreatorUserId, @Title, @DataCenter, @ScheduleText, @ScheduleAt, @Note)
RETURNING id AS Id,
          guild_id AS GuildId,
          channel_id AS ChannelId,
          message_id AS MessageId,
          thread_channel_id AS ThreadChannelId,
          thread_message_id AS ThreadMessageId,
          status AS Status,
          closed_at AS ClosedAt,
          creator_user_id AS CreatorUserId,
          title AS Title,
          data_center AS DataCenter,
          schedule_text AS ScheduleText,
          schedule_at AS ScheduleAt,
          reminder_sent_at AS ReminderSentAt,
          note AS Note,
          created_at AS CreatedAt,
          updated_at AS UpdatedAt;";

        const string insertSlotSql = @"
INSERT INTO recruit_slots (post_id, position, required_job)
VALUES (@PostId, @Position, @RequiredJob);";

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var post = await conn.QuerySingleAsync<RecruitPost>(insertPostSql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            MessageId = ToDbId(messageId),
            CreatorUserId = ToDbId(creatorUserId),
            Title = title,
            DataCenter = NullIfBlank(dataCenter),
            ScheduleText = NullIfBlank(scheduleText),
            ScheduleAt = scheduleAt,
            Note = NullIfBlank(note)
        }, transaction: tx);

        foreach (var position in positions)
        {
            string? requiredJob = null;
            if (requiredJobs is not null && requiredJobs.TryGetValue(position, out var selected) && !string.IsNullOrWhiteSpace(selected))
            {
                requiredJob = selected;
            }

            await conn.ExecuteAsync(insertSlotSql, new
            {
                PostId = post.Id,
                Position = position,
                RequiredJob = NullIfBlank(requiredJob)
            }, transaction: tx);
        }

        await tx.CommitAsync();
        return post;
    }

    public async Task UpdateRecruitThreadAsync(int postId, ulong? threadChannelId, ulong? threadMessageId)
    {
        const string sql = @"
UPDATE recruit_posts
SET thread_channel_id = @ThreadChannelId,
    thread_message_id = @ThreadMessageId,
    updated_at = NOW()
WHERE id = @PostId;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            PostId = postId,
            ThreadChannelId = threadChannelId.HasValue ? ToDbId(threadChannelId.Value) : (long?)null,
            ThreadMessageId = threadMessageId.HasValue ? ToDbId(threadMessageId.Value) : (long?)null
        });
    }

    public async Task UpsertRecruitTemplateAsync(
        ulong guildId,
        string templateKey,
        string title,
        string? dataCenter,
        string? note,
        string? positionsText)
    {
        const string sql = @"
INSERT INTO recruit_templates (guild_id, template_key, title, data_center, note, positions_text)
VALUES (@GuildId, @TemplateKey, @Title, @DataCenter, @Note, @PositionsText)
ON CONFLICT (guild_id, template_key) DO UPDATE
SET title = EXCLUDED.title,
    data_center = EXCLUDED.data_center,
    note = EXCLUDED.note,
    positions_text = EXCLUDED.positions_text,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            TemplateKey = templateKey.Trim().ToLowerInvariant(),
            Title = title.Trim(),
            DataCenter = NullIfBlank(dataCenter),
            Note = NullIfBlank(note),
            PositionsText = NullIfBlank(positionsText)
        });
    }

    public async Task<bool> DeleteRecruitTemplateAsync(ulong guildId, string templateKey)
    {
        const string sql = @"
DELETE FROM recruit_templates
WHERE guild_id = @GuildId
  AND template_key = @TemplateKey;";

        await using var conn = CreateConnection();
        var rows = await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            TemplateKey = templateKey.Trim().ToLowerInvariant()
        });
        return rows > 0;
    }

    public async Task<RecruitTemplate?> GetRecruitTemplateAsync(ulong guildId, string templateKey)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       template_key AS TemplateKey,
       title AS Title,
       data_center AS DataCenter,
       note AS Note,
       positions_text AS PositionsText,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
FROM recruit_templates
WHERE guild_id = @GuildId
  AND template_key = @TemplateKey
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<RecruitTemplate>(sql, new
        {
            GuildId = ToDbId(guildId),
            TemplateKey = templateKey.Trim().ToLowerInvariant()
        });
    }

    public async Task<IReadOnlyList<RecruitTemplate>> ListRecruitTemplatesAsync(ulong guildId)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       template_key AS TemplateKey,
       title AS Title,
       data_center AS DataCenter,
       note AS Note,
       positions_text AS PositionsText,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
FROM recruit_templates
WHERE guild_id = @GuildId
ORDER BY template_key ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<RecruitTemplate>(sql, new { GuildId = ToDbId(guildId) });
        return rows.ToList();
    }

    public async Task<RecruitPost?> GetRecruitPostByMessageIdAsync(ulong messageId)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       thread_channel_id AS ThreadChannelId,
       thread_message_id AS ThreadMessageId,
       status AS Status,
       closed_at AS ClosedAt,
       creator_user_id AS CreatorUserId,
       title AS Title,
       data_center AS DataCenter,
       schedule_text AS ScheduleText,
       schedule_at AS ScheduleAt,
       reminder_sent_at AS ReminderSentAt,
       note AS Note,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
FROM recruit_posts
WHERE message_id = @MessageId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<RecruitPost>(sql, new { MessageId = ToDbId(messageId) });
    }

    public async Task<IReadOnlyList<RecruitPostSummary>> ListRecruitPostSummariesAsync(ulong guildId)
    {
        const string sql = @"
SELECT p.id AS Id,
       p.channel_id AS ChannelId,
       p.message_id AS MessageId,
       p.creator_user_id AS CreatorUserId,
       p.title AS Title,
       p.data_center AS DataCenter,
       p.schedule_text AS ScheduleText,
       p.schedule_at AS ScheduleAt,
       p.status AS Status,
       COALESCE(SUM(CASE WHEN s.user_id IS NOT NULL OR s.is_closed = TRUE THEN 1 ELSE 0 END), 0) AS FilledCount,
       COUNT(s.id) AS SlotCount,
       p.created_at AS CreatedAt,
       p.updated_at AS UpdatedAt
FROM recruit_posts p
LEFT JOIN recruit_slots s ON s.post_id = p.id
WHERE p.guild_id = @GuildId
GROUP BY p.id
ORDER BY p.created_at DESC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<RecruitPostSummary>(sql, new { GuildId = ToDbId(guildId) });
        return rows.ToList();
    }

    public async Task<int> CountActiveRecruitPostsByCreatorAsync(ulong guildId, ulong creatorUserId)
    {
        const string sql = @"
SELECT COUNT(1)
FROM recruit_posts
WHERE guild_id = @GuildId
  AND creator_user_id = @CreatorUserId
  AND status IN (0, 1);";

        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(sql, new
        {
            GuildId = ToDbId(guildId),
            CreatorUserId = ToDbId(creatorUserId)
        });
    }

    public async Task<IReadOnlyList<RecruitPostSummary>> ListActiveRecruitPostsByCreatorAsync(ulong guildId, ulong creatorUserId)
    {
        const string sql = @"
SELECT p.id AS Id,
       p.channel_id AS ChannelId,
       p.message_id AS MessageId,
       p.creator_user_id AS CreatorUserId,
       p.title AS Title,
       p.data_center AS DataCenter,
       p.schedule_text AS ScheduleText,
       p.schedule_at AS ScheduleAt,
       p.status AS Status,
       COALESCE(SUM(CASE WHEN s.user_id IS NOT NULL OR s.is_closed = TRUE THEN 1 ELSE 0 END), 0) AS FilledCount,
       COUNT(s.id) AS SlotCount,
       p.created_at AS CreatedAt,
       p.updated_at AS UpdatedAt
FROM recruit_posts p
LEFT JOIN recruit_slots s ON s.post_id = p.id
WHERE p.guild_id = @GuildId
  AND p.creator_user_id = @CreatorUserId
  AND p.status IN (0, 1)
GROUP BY p.id
ORDER BY p.created_at DESC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<RecruitPostSummary>(sql, new
        {
            GuildId = ToDbId(guildId),
            CreatorUserId = ToDbId(creatorUserId)
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<RecruitSlot>> ListRecruitSlotsAsync(int postId)
    {
        const string sql = @"
SELECT id AS Id,
       post_id AS PostId,
       position AS Position,
       required_job AS RequiredJob,
       job AS Job,
       user_id AS UserId,
       user_name AS UserName,
       comment AS Comment,
       is_closed AS IsClosed,
       thread_message_id AS ThreadMessageId,
       joined_at AS JoinedAt,
       updated_at AS UpdatedAt
FROM recruit_slots
WHERE post_id = @PostId
ORDER BY id ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<RecruitSlot>(sql, new { PostId = postId });
        return rows.ToList();
    }

    public async Task<RecruitSlot?> GetRecruitSlotByUserAsync(int postId, ulong userId)
    {
        const string sql = @"
SELECT id AS Id,
       post_id AS PostId,
       position AS Position,
       required_job AS RequiredJob,
       job AS Job,
       user_id AS UserId,
       user_name AS UserName,
       comment AS Comment,
       is_closed AS IsClosed,
       thread_message_id AS ThreadMessageId,
       joined_at AS JoinedAt,
       updated_at AS UpdatedAt
FROM recruit_slots
WHERE post_id = @PostId
  AND user_id = @UserId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<RecruitSlot>(sql, new
        {
            PostId = postId,
            UserId = ToDbId(userId)
        });
    }

    public async Task<RecruitAssignStatus> AssignRecruitSlotAsync(int postId, string position, ulong userId, string userName, string job, string comment)
    {
        const string selectPostSql = @"
SELECT status
FROM recruit_posts
WHERE id = @PostId
FOR UPDATE;";

        const string selectSlotsSql = @"
SELECT id AS Id,
       post_id AS PostId,
       position AS Position,
       required_job AS RequiredJob,
       job AS Job,
       user_id AS UserId,
       user_name AS UserName,
       comment AS Comment,
       is_closed AS IsClosed,
       thread_message_id AS ThreadMessageId,
       joined_at AS JoinedAt,
       updated_at AS UpdatedAt
FROM recruit_slots
WHERE post_id = @PostId
FOR UPDATE;";

        const string clearSlotSql = @"
UPDATE recruit_slots
SET user_id = NULL,
    user_name = NULL,
    job = NULL,
    comment = NULL,
    is_closed = FALSE,
    thread_message_id = NULL,
    joined_at = NULL,
    updated_at = NOW()
WHERE id = @Id;";

        const string updateSlotSql = @"
UPDATE recruit_slots
SET user_id = @UserId,
    user_name = @UserName,
    job = @Job,
    comment = @Comment,
    is_closed = FALSE,
    joined_at = NOW(),
    updated_at = NOW()
WHERE id = @Id;";

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var statusValue = await conn.ExecuteScalarAsync<short?>(selectPostSql, new { PostId = postId }, transaction: tx);
        if (!statusValue.HasValue)
        {
            await tx.RollbackAsync();
            return RecruitAssignStatus.PostNotFound;
        }

        var slots = (await conn.QueryAsync<RecruitSlot>(selectSlotsSql, new { PostId = postId }, transaction: tx)).ToList();
        if (slots.Count == 0)
        {
            await tx.RollbackAsync();
            return RecruitAssignStatus.PostNotFound;
        }

        var status = (RecruitPostStatus)statusValue.Value;
        var userDbId = ToDbId(userId);
        if (status != RecruitPostStatus.Open)
        {
            var hasSlot = slots.Any(slot => slot.UserId.HasValue && slot.UserId.Value == userDbId);
            if (!(status == RecruitPostStatus.Full && hasSlot))
            {
                await tx.RollbackAsync();
                return RecruitAssignStatus.PostClosed;
            }
        }

        var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, position, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await tx.RollbackAsync();
            return RecruitAssignStatus.PositionNotFound;
        }

        if (target.IsClosed && (!target.UserId.HasValue || target.UserId.Value != userDbId))
        {
            await tx.RollbackAsync();
            return RecruitAssignStatus.PositionClosed;
        }

        if (target.UserId.HasValue && target.UserId.Value != userDbId)
        {
            await tx.RollbackAsync();
            return RecruitAssignStatus.PositionTaken;
        }

        if (!string.IsNullOrWhiteSpace(target.RequiredJob) &&
            !string.Equals(target.RequiredJob, job, StringComparison.OrdinalIgnoreCase))
        {
            await tx.RollbackAsync();
            return RecruitAssignStatus.JobRestricted;
        }

        var jobTaken = slots.Any(slot =>
            !string.IsNullOrWhiteSpace(slot.Job) &&
            string.Equals(slot.Job, job, StringComparison.OrdinalIgnoreCase) &&
            slot.UserId.HasValue &&
            slot.UserId.Value != userDbId);

        if (jobTaken)
        {
            await tx.RollbackAsync();
            return RecruitAssignStatus.JobTaken;
        }

        var previous = slots.FirstOrDefault(slot =>
            slot.UserId.HasValue &&
            slot.UserId.Value == userDbId &&
            !string.Equals(slot.Position, position, StringComparison.OrdinalIgnoreCase));

        if (previous is not null)
        {
            await conn.ExecuteAsync(clearSlotSql, new { Id = previous.Id }, transaction: tx);
        }

        await conn.ExecuteAsync(updateSlotSql, new
        {
            Id = target.Id,
            UserId = userDbId,
            UserName = userName,
            Job = job,
            Comment = NullIfBlank(comment)
        }, transaction: tx);

        await conn.ExecuteAsync("UPDATE recruit_posts SET updated_at = NOW() WHERE id = @PostId;", new { PostId = postId }, transaction: tx);

        await tx.CommitAsync();
        return RecruitAssignStatus.Success;
    }

    public async Task<bool> ClearRecruitSlotAsync(int postId, ulong userId)
    {
        const string clearSql = @"
UPDATE recruit_slots
SET user_id = NULL,
    user_name = NULL,
    job = NULL,
    comment = NULL,
    is_closed = FALSE,
    thread_message_id = NULL,
    joined_at = NULL,
    updated_at = NOW()
WHERE post_id = @PostId
  AND user_id = @UserId;";

        await using var conn = CreateConnection();
        var rows = await conn.ExecuteAsync(clearSql, new
        {
            PostId = postId,
            UserId = ToDbId(userId)
        });

        if (rows > 0)
        {
            await conn.ExecuteAsync("UPDATE recruit_posts SET updated_at = NOW() WHERE id = @PostId;", new { PostId = postId });
        }

        return rows > 0;
    }

    public async Task<bool> UpdateRecruitSlotCommentAsync(int postId, ulong userId, string comment)
    {
        const string sql = @"
UPDATE recruit_slots
SET comment = @Comment,
    updated_at = NOW()
WHERE post_id = @PostId
  AND user_id = @UserId;";

        await using var conn = CreateConnection();
        var rows = await conn.ExecuteAsync(sql, new
        {
            PostId = postId,
            UserId = ToDbId(userId),
            Comment = NullIfBlank(comment)
        });

        if (rows > 0)
        {
            await conn.ExecuteAsync("UPDATE recruit_posts SET updated_at = NOW() WHERE id = @PostId;", new { PostId = postId });
        }

        return rows > 0;
    }

    public async Task<bool> UpdateRecruitSlotThreadMessageAsync(int postId, ulong userId, ulong? threadMessageId)
    {
        const string sql = @"
UPDATE recruit_slots
SET thread_message_id = @ThreadMessageId,
    updated_at = NOW()
WHERE post_id = @PostId
  AND user_id = @UserId;";

        await using var conn = CreateConnection();
        var rows = await conn.ExecuteAsync(sql, new
        {
            PostId = postId,
            UserId = ToDbId(userId),
            ThreadMessageId = threadMessageId.HasValue ? ToDbId(threadMessageId.Value) : (long?)null
        });

        return rows > 0;
    }

    public async Task<RecruitIngameFillStatus> MarkRecruitSlotIngameFilledAsync(int postId, string position, string job)
    {
        const string selectPostSql = @"
SELECT status
FROM recruit_posts
WHERE id = @PostId
FOR UPDATE;";

        const string selectSlotsSql = @"
SELECT id AS Id,
       post_id AS PostId,
       position AS Position,
       required_job AS RequiredJob,
       job AS Job,
       user_id AS UserId,
       is_closed AS IsClosed
FROM recruit_slots
WHERE post_id = @PostId
FOR UPDATE;";

        const string closeSlotSql = @"
UPDATE recruit_slots
SET is_closed = TRUE,
    job = @Job,
    comment = NULL,
    updated_at = NOW()
WHERE id = @Id;";

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var statusValue = await conn.ExecuteScalarAsync<short?>(selectPostSql, new { PostId = postId }, transaction: tx);
        if (!statusValue.HasValue)
        {
            await tx.RollbackAsync();
            return RecruitIngameFillStatus.PostNotFound;
        }

        var status = (RecruitPostStatus)statusValue.Value;
        if (status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await tx.RollbackAsync();
            return RecruitIngameFillStatus.PostClosed;
        }

        var slots = (await conn.QueryAsync<RecruitSlot>(selectSlotsSql, new { PostId = postId }, transaction: tx)).ToList();
        var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, position, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await tx.RollbackAsync();
            return RecruitIngameFillStatus.PositionNotFound;
        }

        if (target.UserId.HasValue)
        {
            await tx.RollbackAsync();
            return RecruitIngameFillStatus.PositionOccupied;
        }

        if (target.IsClosed)
        {
            await tx.RollbackAsync();
            return RecruitIngameFillStatus.PositionAlreadyClosed;
        }

        if (!string.IsNullOrWhiteSpace(target.RequiredJob) &&
            !string.Equals(target.RequiredJob, job, StringComparison.OrdinalIgnoreCase))
        {
            await tx.RollbackAsync();
            return RecruitIngameFillStatus.JobRestricted;
        }

        var jobTaken = slots.Any(slot =>
            !string.IsNullOrWhiteSpace(slot.Job) &&
            string.Equals(slot.Job, job, StringComparison.OrdinalIgnoreCase) &&
            slot.Id != target.Id &&
            (slot.UserId.HasValue || slot.IsClosed));

        if (jobTaken)
        {
            await tx.RollbackAsync();
            return RecruitIngameFillStatus.JobTaken;
        }

        await conn.ExecuteAsync(closeSlotSql, new { Id = target.Id, Job = job }, transaction: tx);
        await conn.ExecuteAsync("UPDATE recruit_posts SET updated_at = NOW() WHERE id = @PostId;", new { PostId = postId }, transaction: tx);
        await tx.CommitAsync();

        return RecruitIngameFillStatus.Success;
    }

    public async Task<RecruitIngameUnfillStatus> UnmarkRecruitSlotIngameFilledAsync(int postId, string position)
    {
        const string selectPostSql = @"
SELECT status
FROM recruit_posts
WHERE id = @PostId
FOR UPDATE;";

        const string selectSlotsSql = @"
SELECT id AS Id,
       post_id AS PostId,
       position AS Position,
       required_job AS RequiredJob,
       user_id AS UserId,
       is_closed AS IsClosed
FROM recruit_slots
WHERE post_id = @PostId
FOR UPDATE;";

        const string openSlotSql = @"
UPDATE recruit_slots
SET is_closed = FALSE,
    job = NULL,
    comment = NULL,
    updated_at = NOW()
WHERE id = @Id;";

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var statusValue = await conn.ExecuteScalarAsync<short?>(selectPostSql, new { PostId = postId }, transaction: tx);
        if (!statusValue.HasValue)
        {
            await tx.RollbackAsync();
            return RecruitIngameUnfillStatus.PostNotFound;
        }

        var status = (RecruitPostStatus)statusValue.Value;
        if (status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await tx.RollbackAsync();
            return RecruitIngameUnfillStatus.PostClosed;
        }

        var slots = (await conn.QueryAsync<RecruitSlot>(selectSlotsSql, new { PostId = postId }, transaction: tx)).ToList();
        var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, position, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await tx.RollbackAsync();
            return RecruitIngameUnfillStatus.PositionNotFound;
        }

        if (target.UserId.HasValue)
        {
            await tx.RollbackAsync();
            return RecruitIngameUnfillStatus.PositionOccupied;
        }

        if (!target.IsClosed)
        {
            await tx.RollbackAsync();
            return RecruitIngameUnfillStatus.PositionNotClosed;
        }

        await conn.ExecuteAsync(openSlotSql, new { Id = target.Id }, transaction: tx);
        await conn.ExecuteAsync("UPDATE recruit_posts SET updated_at = NOW() WHERE id = @PostId;", new { PostId = postId }, transaction: tx);
        await tx.CommitAsync();

        return RecruitIngameUnfillStatus.Success;
    }

    public async Task UpdateRecruitPostStatusAsync(int postId, RecruitPostStatus status)
    {
        const string sql = @"
UPDATE recruit_posts
SET status = @Status,
    closed_at = CASE WHEN @Status IN (2, 3) THEN NOW() ELSE NULL END,
    updated_at = NOW()
WHERE id = @PostId;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            PostId = postId,
            Status = (short)status
        });
    }

    public async Task<bool> UpdateRecruitPostDetailsAsync(int postId, string title, string? dataCenter, string? scheduleText, DateTimeOffset? scheduleAt, string? note)
    {
        const string sql = @"
UPDATE recruit_posts
SET title = @Title,
    data_center = @DataCenter,
    schedule_text = @ScheduleText,
    schedule_at = @ScheduleAt,
    note = @Note,
    reminder_sent_at = NULL,
    updated_at = NOW()
WHERE id = @PostId;";

        await using var conn = CreateConnection();
        var rows = await conn.ExecuteAsync(sql, new
        {
            PostId = postId,
            Title = title,
            DataCenter = NullIfBlank(dataCenter),
            ScheduleText = NullIfBlank(scheduleText),
            ScheduleAt = scheduleAt,
            Note = NullIfBlank(note)
        });

        return rows > 0;
    }

    public async Task<IReadOnlyList<RecruitPost>> ListDueRecruitPostsForReminderAsync(DateTimeOffset fromUtcExclusive, DateTimeOffset toUtcInclusive)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       thread_channel_id AS ThreadChannelId,
       thread_message_id AS ThreadMessageId,
       status AS Status,
       closed_at AS ClosedAt,
       creator_user_id AS CreatorUserId,
       title AS Title,
       data_center AS DataCenter,
       schedule_text AS ScheduleText,
       schedule_at AS ScheduleAt,
       reminder_sent_at AS ReminderSentAt,
       note AS Note,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
FROM recruit_posts
WHERE status IN (0, 1)
  AND schedule_at IS NOT NULL
  AND schedule_at > @FromUtcExclusive
  AND schedule_at <= @ToUtcInclusive
  AND reminder_sent_at IS NULL
ORDER BY schedule_at ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<RecruitPost>(sql, new
        {
            FromUtcExclusive = fromUtcExclusive,
            ToUtcInclusive = toUtcInclusive
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<RecruitPost>> ListDueRecruitPostsForAutoCloseAsync(DateTimeOffset nowUtc)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       thread_channel_id AS ThreadChannelId,
       thread_message_id AS ThreadMessageId,
       status AS Status,
       closed_at AS ClosedAt,
       creator_user_id AS CreatorUserId,
       title AS Title,
       data_center AS DataCenter,
       schedule_text AS ScheduleText,
       schedule_at AS ScheduleAt,
       reminder_sent_at AS ReminderSentAt,
       note AS Note,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
FROM recruit_posts
WHERE status IN (0, 1)
  AND schedule_at IS NOT NULL
  AND schedule_at <= @NowUtc
ORDER BY schedule_at ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<RecruitPost>(sql, new { NowUtc = nowUtc });
        return rows.ToList();
    }

    public async Task<bool> MarkRecruitReminderSentAsync(int postId)
    {
        const string sql = @"
UPDATE recruit_posts
SET reminder_sent_at = NOW(),
    updated_at = NOW()
WHERE id = @PostId
  AND reminder_sent_at IS NULL;";

        await using var conn = CreateConnection();
        var rows = await conn.ExecuteAsync(sql, new { PostId = postId });
        return rows > 0;
    }

    public async Task<RecruitPositionUpdateResult> UpdateRecruitPositionsAsync(int postId, IReadOnlyList<string> positions)
    {
        const string selectSlotsSql = @"
SELECT id AS Id,
       position AS Position,
       required_job AS RequiredJob,
       user_id AS UserId,
       is_closed AS IsClosed
FROM recruit_slots
WHERE post_id = @PostId
FOR UPDATE;";

        const string deleteSlotsSql = @"
DELETE FROM recruit_slots
WHERE id = ANY(@Ids);";

        const string insertSlotSql = @"
INSERT INTO recruit_slots (post_id, position)
VALUES (@PostId, @Position);";

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var slots = (await conn.QueryAsync<RecruitSlot>(selectSlotsSql, new { PostId = postId }, transaction: tx)).ToList();
        var positionSet = new HashSet<string>(positions, StringComparer.OrdinalIgnoreCase);

        var blocked = slots
            .Where(slot => !positionSet.Contains(slot.Position) && slot.UserId.HasValue)
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (blocked.Count > 0)
        {
            await tx.RollbackAsync();
            return new RecruitPositionUpdateResult(false, blocked);
        }

        var removeIds = slots
            .Where(slot => !positionSet.Contains(slot.Position))
            .Select(slot => slot.Id)
            .ToArray();

        if (removeIds.Length > 0)
        {
            await conn.ExecuteAsync(deleteSlotsSql, new { Ids = removeIds }, transaction: tx);
        }

        var existing = new HashSet<string>(slots.Select(slot => slot.Position), StringComparer.OrdinalIgnoreCase);
        foreach (var position in positions)
        {
            if (existing.Contains(position))
            {
                continue;
            }

            await conn.ExecuteAsync(insertSlotSql, new { PostId = postId, Position = position }, transaction: tx);
        }

        await conn.ExecuteAsync("UPDATE recruit_posts SET updated_at = NOW() WHERE id = @PostId;", new { PostId = postId }, transaction: tx);
        await tx.CommitAsync();

        return new RecruitPositionUpdateResult(true, Array.Empty<string>());
    }

    public async Task<RecruitRequiredJobUpdateStatus> UpdateRecruitSlotRequiredJobAsync(int postId, string position, string? requiredJob)
    {
        const string selectPostSql = @"
SELECT status
FROM recruit_posts
WHERE id = @PostId
FOR UPDATE;";

        const string selectSlotSql = @"
SELECT id AS Id,
       position AS Position,
       job AS Job,
       user_id AS UserId
FROM recruit_slots
WHERE post_id = @PostId
  AND position = @Position
FOR UPDATE;";

        const string updateSql = @"
UPDATE recruit_slots
SET required_job = @RequiredJob,
    updated_at = NOW()
WHERE id = @Id;";

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var statusValue = await conn.ExecuteScalarAsync<short?>(selectPostSql, new { PostId = postId }, transaction: tx);
        if (!statusValue.HasValue)
        {
            await tx.RollbackAsync();
            return RecruitRequiredJobUpdateStatus.PostNotFound;
        }

        var status = (RecruitPostStatus)statusValue.Value;
        if (status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await tx.RollbackAsync();
            return RecruitRequiredJobUpdateStatus.PostClosed;
        }

        var slot = await conn.QuerySingleOrDefaultAsync<RecruitSlot>(selectSlotSql, new
        {
            PostId = postId,
            Position = position
        }, transaction: tx);

        if (slot is null)
        {
            await tx.RollbackAsync();
            return RecruitRequiredJobUpdateStatus.PositionNotFound;
        }

        if (!string.IsNullOrWhiteSpace(requiredJob) &&
            slot.UserId.HasValue &&
            !string.Equals(slot.Job, requiredJob, StringComparison.OrdinalIgnoreCase))
        {
            await tx.RollbackAsync();
            return RecruitRequiredJobUpdateStatus.PositionOccupiedByOtherJob;
        }

        await conn.ExecuteAsync(updateSql, new
        {
            Id = slot.Id,
            RequiredJob = NullIfBlank(requiredJob)
        }, transaction: tx);
        await conn.ExecuteAsync("UPDATE recruit_posts SET updated_at = NOW() WHERE id = @PostId;", new { PostId = postId }, transaction: tx);

        await tx.CommitAsync();
        return RecruitRequiredJobUpdateStatus.Success;
    }

    public async Task UpsertTempVoiceChannelAsync(ulong guildId, ulong channelId, ulong ownerUserId)
    {
        const string sql = @"
INSERT INTO temp_voice_channels (guild_id, channel_id, owner_user_id)
VALUES (@GuildId, @ChannelId, @OwnerUserId)
ON CONFLICT (channel_id) DO UPDATE
SET owner_user_id = EXCLUDED.owner_user_id,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            OwnerUserId = ToDbId(ownerUserId)
        });
    }

    public async Task<long?> GetTempVoiceOwnerAsync(ulong channelId)
    {
        const string sql = @"
SELECT owner_user_id
FROM temp_voice_channels
WHERE channel_id = @ChannelId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<long?>(sql, new { ChannelId = ToDbId(channelId) });
    }

    public async Task RemoveTempVoiceChannelAsync(ulong channelId)
    {
        const string sql = "DELETE FROM temp_voice_channels WHERE channel_id = @ChannelId;";
        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new { ChannelId = ToDbId(channelId) });
    }

    public async Task<bool> IsLodestoneLinkedByAnotherUserAsync(ulong userId, long lodestoneId)
    {
        const string sql = @"
SELECT 1
FROM discord_user_characters
WHERE lodestone_id = @LodestoneId
  AND discord_user_id <> @DiscordUserId
LIMIT 1;";

        await using var conn = CreateConnection();
        var result = await conn.ExecuteScalarAsync<int?>(sql, new
        {
            DiscordUserId = ToDbId(userId),
            LodestoneId = lodestoneId
        });
        return result.HasValue;
    }

    public async Task<GuildPanel?> GetGuildPanelAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       updated_at AS UpdatedAt
FROM guild_panels
WHERE guild_id = @GuildId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GuildPanel>(sql, new { GuildId = ToDbId(guildId) });
    }

    public async Task<IReadOnlyList<GuildPanel>> ListGuildPanelsAsync()
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       updated_at AS UpdatedAt
FROM guild_panels;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<GuildPanel>(sql);
        return rows.ToList();
    }

    public async Task<GuildPanel?> GetGuildLodestonePanelAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       updated_at AS UpdatedAt
FROM guild_lodestone_panels
WHERE guild_id = @GuildId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GuildPanel>(sql, new { GuildId = ToDbId(guildId) });
    }

    public async Task<GuildSettings?> GetGuildSettingsAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       admin_role_id AS AdminRoleId,
       member_role_id AS MemberRoleId,
       provisional_role_id AS ProvisionalRoleId,
       registration_channel_id AS RegistrationChannelId,
       registration_review_channel_id AS RegistrationReviewChannelId,
       registration_approval_required AS RegistrationApprovalRequired,
       inactive_auto_kick_enabled AS InactiveAutoKickEnabled,
       log_channel_id AS LogChannelId,
       control_channel_id AS ControlChannelId,
       updated_at AS UpdatedAt
FROM guild_settings
WHERE guild_id = @GuildId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GuildSettings>(sql, new { GuildId = ToDbId(guildId) });
    }

    public async Task UpsertGuildSettingsAsync(
        ulong guildId,
        ulong adminRoleId,
        ulong memberRoleId,
        ulong provisionalRoleId,
        ulong registrationChannelId,
        ulong registrationReviewChannelId,
        bool registrationApprovalRequired,
        bool inactiveAutoKickEnabled,
        ulong logChannelId,
        ulong controlChannelId)
    {
        const string sql = @"
INSERT INTO guild_settings (guild_id, admin_role_id, member_role_id, provisional_role_id, registration_channel_id, registration_review_channel_id, registration_approval_required, inactive_auto_kick_enabled, log_channel_id, control_channel_id)
VALUES (@GuildId, @AdminRoleId, @MemberRoleId, @ProvisionalRoleId, @RegistrationChannelId, @RegistrationReviewChannelId, @RegistrationApprovalRequired, @InactiveAutoKickEnabled, @LogChannelId, @ControlChannelId)
ON CONFLICT (guild_id) DO UPDATE
SET admin_role_id = EXCLUDED.admin_role_id,
    member_role_id = EXCLUDED.member_role_id,
    provisional_role_id = EXCLUDED.provisional_role_id,
    registration_channel_id = EXCLUDED.registration_channel_id,
    registration_review_channel_id = EXCLUDED.registration_review_channel_id,
    registration_approval_required = EXCLUDED.registration_approval_required,
    inactive_auto_kick_enabled = EXCLUDED.inactive_auto_kick_enabled,
    log_channel_id = EXCLUDED.log_channel_id,
    control_channel_id = EXCLUDED.control_channel_id,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            AdminRoleId = ToDbId(adminRoleId),
            MemberRoleId = ToDbId(memberRoleId),
            ProvisionalRoleId = ToDbId(provisionalRoleId),
            RegistrationChannelId = ToDbId(registrationChannelId),
            RegistrationReviewChannelId = ToDbId(registrationReviewChannelId),
            RegistrationApprovalRequired = registrationApprovalRequired,
            InactiveAutoKickEnabled = inactiveAutoKickEnabled,
            LogChannelId = ToDbId(logChannelId),
            ControlChannelId = ToDbId(controlChannelId)
        });
    }

    public async Task<bool> UpdateGuildRegistrationApprovalRequiredAsync(ulong guildId, bool required)
    {
        const string sql = @"
UPDATE guild_settings
SET registration_approval_required = @Required,
    updated_at = NOW()
WHERE guild_id = @GuildId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new { GuildId = ToDbId(guildId), Required = required });
        return affected > 0;
    }

    public async Task<bool> UpdateGuildInactiveAutoKickEnabledAsync(ulong guildId, bool enabled)
    {
        const string sql = @"
UPDATE guild_settings
SET inactive_auto_kick_enabled = @Enabled,
    updated_at = NOW()
WHERE guild_id = @GuildId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new { GuildId = ToDbId(guildId), Enabled = enabled });
        return affected > 0;
    }

    public async Task<GuildPanel?> GetGuildControlPanelAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       updated_at AS UpdatedAt
FROM guild_control_panels
WHERE guild_id = @GuildId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GuildPanel>(sql, new { GuildId = ToDbId(guildId) });
    }

    public async Task<GuildPanel?> GetGuildAdminControlPanelAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       updated_at AS UpdatedAt
FROM guild_admin_control_panels
WHERE guild_id = @GuildId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GuildPanel>(sql, new { GuildId = ToDbId(guildId) });
    }

    public async Task<GuildPanel?> GetGuildRecruitPanelAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       updated_at AS UpdatedAt
FROM guild_recruit_panels
WHERE guild_id = @GuildId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GuildPanel>(sql, new { GuildId = ToDbId(guildId) });
    }

    public async Task<GuildPanel?> GetGuildRecruitListPanelAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       updated_at AS UpdatedAt
FROM guild_recruit_list_panels
WHERE guild_id = @GuildId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GuildPanel>(sql, new { GuildId = ToDbId(guildId) });
    }

    public async Task<GuildPanel?> GetGuildDailyActivityPanelAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       updated_at AS UpdatedAt
FROM guild_daily_activity_panels
WHERE guild_id = @GuildId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GuildPanel>(sql, new { GuildId = ToDbId(guildId) });
    }

    public async Task<GuildPanel?> GetGuildWeeklyActivityPanelAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       updated_at AS UpdatedAt
FROM guild_weekly_activity_panels
WHERE guild_id = @GuildId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GuildPanel>(sql, new { GuildId = ToDbId(guildId) });
    }

    public async Task UpsertGuildControlPanelAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        const string sql = @"
INSERT INTO guild_control_panels (guild_id, channel_id, message_id)
VALUES (@GuildId, @ChannelId, @MessageId)
ON CONFLICT (guild_id) DO UPDATE
SET channel_id = EXCLUDED.channel_id,
    message_id = EXCLUDED.message_id,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            MessageId = ToDbId(messageId)
        });
    }

    public async Task UpsertGuildAdminControlPanelAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        const string sql = @"
INSERT INTO guild_admin_control_panels (guild_id, channel_id, message_id)
VALUES (@GuildId, @ChannelId, @MessageId)
ON CONFLICT (guild_id) DO UPDATE
SET channel_id = EXCLUDED.channel_id,
    message_id = EXCLUDED.message_id,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            MessageId = ToDbId(messageId)
        });
    }

    public async Task UpsertGuildRecruitPanelAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        const string sql = @"
INSERT INTO guild_recruit_panels (guild_id, channel_id, message_id)
VALUES (@GuildId, @ChannelId, @MessageId)
ON CONFLICT (guild_id) DO UPDATE
SET channel_id = EXCLUDED.channel_id,
    message_id = EXCLUDED.message_id,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            MessageId = ToDbId(messageId)
        });
    }

    public async Task UpsertGuildRecruitListPanelAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        const string sql = @"
INSERT INTO guild_recruit_list_panels (guild_id, channel_id, message_id)
VALUES (@GuildId, @ChannelId, @MessageId)
ON CONFLICT (guild_id) DO UPDATE
SET channel_id = EXCLUDED.channel_id,
    message_id = EXCLUDED.message_id,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            MessageId = ToDbId(messageId)
        });
    }

    public async Task UpsertGuildDailyActivityPanelAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        const string sql = @"
INSERT INTO guild_daily_activity_panels (guild_id, channel_id, message_id)
VALUES (@GuildId, @ChannelId, @MessageId)
ON CONFLICT (guild_id) DO UPDATE
SET channel_id = EXCLUDED.channel_id,
    message_id = EXCLUDED.message_id,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            MessageId = ToDbId(messageId)
        });
    }

    public async Task UpsertGuildWeeklyActivityPanelAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        const string sql = @"
INSERT INTO guild_weekly_activity_panels (guild_id, channel_id, message_id)
VALUES (@GuildId, @ChannelId, @MessageId)
ON CONFLICT (guild_id) DO UPDATE
SET channel_id = EXCLUDED.channel_id,
    message_id = EXCLUDED.message_id,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            MessageId = ToDbId(messageId)
        });
    }

    public async Task UpsertGuildPanelAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        const string sql = @"
INSERT INTO guild_panels (guild_id, channel_id, message_id)
VALUES (@GuildId, @ChannelId, @MessageId)
ON CONFLICT (guild_id) DO UPDATE
SET channel_id = EXCLUDED.channel_id,
    message_id = EXCLUDED.message_id,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            MessageId = ToDbId(messageId)
        });
    }

    public async Task UpsertGuildLodestonePanelAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        const string sql = @"
INSERT INTO guild_lodestone_panels (guild_id, channel_id, message_id)
VALUES (@GuildId, @ChannelId, @MessageId)
ON CONFLICT (guild_id) DO UPDATE
SET channel_id = EXCLUDED.channel_id,
    message_id = EXCLUDED.message_id,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            MessageId = ToDbId(messageId)
        });
    }

    public async Task<GuildStatsPanel?> GetGuildStatsPanelAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       channel_id AS ChannelId,
       data_center_message_id AS DataCenterMessageId,
       world_message_id AS WorldMessageId,
       updated_at AS UpdatedAt
FROM guild_stats_panels
WHERE guild_id = @GuildId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GuildStatsPanel>(sql, new { GuildId = ToDbId(guildId) });
    }

    public async Task UpsertGuildStatsPanelAsync(ulong guildId, ulong channelId, ulong dataCenterMessageId, ulong worldMessageId)
    {
        const string sql = @"
INSERT INTO guild_stats_panels (guild_id, channel_id, data_center_message_id, world_message_id)
VALUES (@GuildId, @ChannelId, @DataCenterMessageId, @WorldMessageId)
ON CONFLICT (guild_id) DO UPDATE
SET channel_id = EXCLUDED.channel_id,
    data_center_message_id = EXCLUDED.data_center_message_id,
    world_message_id = EXCLUDED.world_message_id,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            DataCenterMessageId = ToDbId(dataCenterMessageId),
            WorldMessageId = ToDbId(worldMessageId)
        });
    }

    public async Task<IReadOnlyList<MemberContentRoleSetting>> ListMemberContentRoleSettingsAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       role_key AS RoleKey,
       label AS Label,
       role_name AS RoleName,
       button_style AS ButtonStyle,
       button_row AS ButtonRow,
       sort_order AS SortOrder,
       is_active AS IsActive,
       updated_at AS UpdatedAt
FROM guild_member_content_roles
WHERE guild_id = @GuildId
  AND is_active = TRUE
ORDER BY button_row ASC, sort_order ASC, role_key ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<MemberContentRoleSetting>(sql, new { GuildId = ToDbId(guildId) });
        return rows.ToList();
    }

    public async Task UpsertMemberContentRoleSettingAsync(
        ulong guildId,
        string roleKey,
        string label,
        string roleName,
        int buttonStyle,
        int buttonRow)
    {
        const string sql = @"
WITH next_sort AS (
    SELECT COALESCE(MAX(sort_order), 0) + 1 AS next_value
    FROM guild_member_content_roles
    WHERE guild_id = @GuildId
)
INSERT INTO guild_member_content_roles (guild_id, role_key, label, role_name, button_style, button_row, sort_order, is_active)
VALUES (@GuildId, @RoleKey, @Label, @RoleName, @ButtonStyle, @ButtonRow, (SELECT next_value FROM next_sort), TRUE)
ON CONFLICT (guild_id, role_key) DO UPDATE
SET label = EXCLUDED.label,
    role_name = EXCLUDED.role_name,
    button_style = EXCLUDED.button_style,
    button_row = EXCLUDED.button_row,
    is_active = TRUE,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            RoleKey = roleKey.Trim(),
            Label = label.Trim(),
            RoleName = roleName.Trim(),
            ButtonStyle = buttonStyle,
            ButtonRow = buttonRow
        });
    }

    public async Task DisableMemberContentRoleSettingAsync(ulong guildId, string roleKey)
    {
        const string sql = @"
UPDATE guild_member_content_roles
SET is_active = FALSE,
    updated_at = NOW()
WHERE guild_id = @GuildId
  AND role_key = @RoleKey;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            RoleKey = roleKey.Trim()
        });
    }

    private static ulong ToUlong(long id) => unchecked((ulong)id);
    private static long ToDbId(ulong id) => unchecked((long)id);

    private static string? NullIfBlank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

public sealed record RecruitPositionUpdateResult(bool Success, IReadOnlyList<string> BlockedPositions);
