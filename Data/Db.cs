using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using RaidDcBot.Models;
using RaidDcBot.Services;

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

    public async Task ApplyRecruitDuplicateJobDiffSqlAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS recruit_posts
ADD COLUMN IF NOT EXISTS disallow_duplicate_jobs BOOLEAN NOT NULL DEFAULT TRUE;

DROP INDEX IF EXISTS ux_recruit_slots_job;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureRecruitHostFlexSpecColumnAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS recruit_posts
ADD COLUMN IF NOT EXISTS host_flex_spec TEXT NULL;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureRecruitHostFlexJobsSpecColumnAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS recruit_posts
ADD COLUMN IF NOT EXISTS host_flex_jobs_spec TEXT NULL;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureRecruitSlotFlexSpecColumnAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS recruit_slots
ADD COLUMN IF NOT EXISTS flex_spec TEXT NULL;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureRecruitSlotFlexJobsSpecColumnAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS recruit_slots
ADD COLUMN IF NOT EXISTS flex_jobs_spec TEXT NULL;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureRecruitUnderfilledDecisionDeadlineColumnAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS recruit_posts
ADD COLUMN IF NOT EXISTS underfilled_decision_deadline_at TIMESTAMPTZ NULL;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task BackfillRecruitSlotFlexJobsSpecAsync()
    {
        const string sql = @"
UPDATE recruit_slots
SET flex_jobs_spec = UPPER(TRIM(job)),
    updated_at = NOW()
WHERE flex_spec IS NOT NULL
  AND flex_jobs_spec IS NULL
  AND job IS NOT NULL
  AND TRIM(job) <> '';";

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

    public async Task EnsureDiscordUserVoiceTextAutoDeleteColumnsAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS discord_users
ADD COLUMN IF NOT EXISTS vc_text_auto_delete_enabled BOOLEAN NOT NULL DEFAULT FALSE,
ADD COLUMN IF NOT EXISTS vc_text_auto_delete_hours INTEGER NOT NULL DEFAULT 6;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureDiscordUserAvatarColumnAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS discord_users
ADD COLUMN IF NOT EXISTS discord_avatar_url TEXT NULL;";

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

    public async Task EnsureLodestoneCharacterExtrasCacheColumnsAsync()
    {
        const string sql = @"
ALTER TABLE IF EXISTS lodestone_characters
ADD COLUMN IF NOT EXISTS extras_json JSONB NULL,
ADD COLUMN IF NOT EXISTS extras_synced_at TIMESTAMPTZ NULL;";

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

    public async Task EnsureActivityRankingAwardsTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS activity_ranking_awards (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    ranking_key TEXT NOT NULL,
    user_id BIGINT NOT NULL,
    best_rank SMALLINT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_activity_ranking_awards UNIQUE (guild_id, ranking_key, user_id)
);

ALTER TABLE IF EXISTS activity_ranking_awards
ADD COLUMN IF NOT EXISTS best_rank SMALLINT NULL;

CREATE INDEX IF NOT EXISTS idx_activity_ranking_awards_guild_user
ON activity_ranking_awards (guild_id, user_id);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureActivityRankingMetricAwardsTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS activity_ranking_metric_awards (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    ranking_key TEXT NOT NULL,
    metric_key TEXT NOT NULL,
    user_id BIGINT NOT NULL,
    rank SMALLINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_activity_ranking_metric_awards UNIQUE (guild_id, ranking_key, metric_key, user_id)
);

CREATE INDEX IF NOT EXISTS idx_activity_ranking_metric_awards_guild_user_metric
ON activity_ranking_metric_awards (guild_id, user_id, metric_key);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureGuildDailyReactionPostPanelTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS guild_daily_reaction_post_panels (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL UNIQUE,
    channel_id BIGINT NOT NULL,
    message_id BIGINT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureGuildWeeklyReactionPostPanelTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS guild_weekly_reaction_post_panels (
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
ADD COLUMN IF NOT EXISTS inactive_auto_kick_user_data_retention_days INTEGER NOT NULL DEFAULT 7,
ADD COLUMN IF NOT EXISTS log_channel_id BIGINT NOT NULL DEFAULT 0,
ADD COLUMN IF NOT EXISTS control_channel_id BIGINT NOT NULL DEFAULT 0,
ADD COLUMN IF NOT EXISTS runtime_cleanup_interval_minutes INTEGER NULL,
ADD COLUMN IF NOT EXISTS runtime_recruit_closed_retention_hours INTEGER NULL,
ADD COLUMN IF NOT EXISTS runtime_recruit_canceled_retention_hours INTEGER NULL,
ADD COLUMN IF NOT EXISTS runtime_message_log_retention_days INTEGER NULL,
ADD COLUMN IF NOT EXISTS runtime_voice_session_retention_days INTEGER NULL,
ADD COLUMN IF NOT EXISTS runtime_reaction_log_retention_days INTEGER NULL,
ADD COLUMN IF NOT EXISTS runtime_tuning_updated_at TIMESTAMPTZ NULL;";

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

    public async Task EnsureInactiveKickRetentionTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS public.inactive_kick_user_data_retentions (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    user_id BIGINT NOT NULL,
    delete_after TIMESTAMPTZ NOT NULL,
    warning_notified_at TIMESTAMPTZ NULL,
    final_warning_notified_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_inactive_kick_user_data_retentions UNIQUE (guild_id, user_id)
);

ALTER TABLE IF EXISTS public.inactive_kick_user_data_retentions
ADD COLUMN IF NOT EXISTS warning_notified_at TIMESTAMPTZ NULL;

ALTER TABLE IF EXISTS public.inactive_kick_user_data_retentions
ADD COLUMN IF NOT EXISTS final_warning_notified_at TIMESTAMPTZ NULL;

CREATE INDEX IF NOT EXISTS idx_inactive_kick_user_data_retentions_delete_after
ON public.inactive_kick_user_data_retentions (delete_after ASC);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureInquiryTicketsTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS public.inquiry_tickets (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    channel_id BIGINT NOT NULL UNIQUE,
    requester_user_id BIGINT NOT NULL,
    requester_name TEXT NOT NULL,
    subject TEXT NOT NULL,
    status TEXT NOT NULL,
    assigned_admin_user_id BIGINT NULL,
    assigned_admin_name TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_activity_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    closed_at TIMESTAMPTZ NULL,
    last_reminder_at TIMESTAMPTZ NULL,
    transcript_text TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_inquiry_tickets_guild_status
ON public.inquiry_tickets (guild_id, status, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_inquiry_tickets_last_activity
ON public.inquiry_tickets (last_activity_at ASC)
WHERE status <> 'CLOSED';

CREATE UNIQUE INDEX IF NOT EXISTS ux_inquiry_tickets_open_requester
ON public.inquiry_tickets (guild_id, requester_user_id)
WHERE status <> 'CLOSED';";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureAdminMemberNotesTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS public.admin_member_notes (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    target_user_id BIGINT NOT NULL,
    author_user_id BIGINT NOT NULL,
    author_name TEXT NOT NULL,
    note TEXT NOT NULL,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_admin_member_notes_target
ON public.admin_member_notes (guild_id, target_user_id, created_at DESC);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureAdminMemberModerationActionsTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS public.admin_member_moderation_actions (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    target_user_id BIGINT NOT NULL,
    actor_user_id BIGINT NOT NULL,
    actor_name TEXT NOT NULL,
    action_type TEXT NOT NULL,
    reason TEXT NULL,
    detail TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_admin_member_moderation_actions_target
ON public.admin_member_moderation_actions (guild_id, target_user_id, created_at DESC);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureFfLogsSeenReportsTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS public.fflogs_seen_reports (
    guild_id BIGINT NOT NULL,
    report_code TEXT NOT NULL,
    detected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    report_end_at TIMESTAMPTZ NULL,
    PRIMARY KEY (guild_id, report_code)
);

CREATE INDEX IF NOT EXISTS idx_fflogs_seen_reports_detected_at
ON public.fflogs_seen_reports (detected_at DESC);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureSsAlbumFavoritesTableAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS public.ss_album_favorites (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    viewer_user_id BIGINT NOT NULL,
    target_user_id BIGINT NOT NULL,
    message_id BIGINT NOT NULL,
    attachment_id BIGINT NOT NULL,
    channel_id BIGINT NOT NULL,
    image_url TEXT NOT NULL,
    file_name TEXT NULL,
    message_content TEXT NULL,
    local_path TEXT NULL,
    posted_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_ss_album_favorites UNIQUE (guild_id, viewer_user_id, message_id, attachment_id)
);

CREATE INDEX IF NOT EXISTS idx_ss_album_favorites_viewer
ON public.ss_album_favorites (guild_id, viewer_user_id, posted_at DESC);

CREATE INDEX IF NOT EXISTS idx_ss_album_favorites_target
ON public.ss_album_favorites (guild_id, viewer_user_id, target_user_id, posted_at DESC);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureLotteryTablesAsync()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS public.lotteries (
    id SERIAL PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    channel_id BIGINT NOT NULL,
    message_id BIGINT NULL UNIQUE,
    participant_role_id BIGINT NULL,
    participant_role_delete_at TIMESTAMPTZ NULL,
    title TEXT NOT NULL,
    description TEXT NULL,
    prize_list TEXT NULL,
    allow_duplicate_winners BOOLEAN NOT NULL DEFAULT FALSE,
    winner_count INTEGER NOT NULL,
    status TEXT NOT NULL,
    close_at TIMESTAMPTZ NOT NULL,
    created_by_user_id BIGINT NOT NULL,
    created_by_name TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    drawn_at TIMESTAMPTZ NULL,
    canceled_at TIMESTAMPTZ NULL
);

ALTER TABLE IF EXISTS public.lotteries
ADD COLUMN IF NOT EXISTS prize_list TEXT NULL;

ALTER TABLE IF EXISTS public.lotteries
ADD COLUMN IF NOT EXISTS allow_duplicate_winners BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE IF EXISTS public.lotteries
ADD COLUMN IF NOT EXISTS participant_role_id BIGINT NULL;

ALTER TABLE IF EXISTS public.lotteries
ADD COLUMN IF NOT EXISTS participant_role_delete_at TIMESTAMPTZ NULL;

CREATE INDEX IF NOT EXISTS idx_lotteries_guild_status
ON public.lotteries (guild_id, status, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_lotteries_close_at
ON public.lotteries (status, close_at ASC);

CREATE TABLE IF NOT EXISTS public.lottery_entries (
    id SERIAL PRIMARY KEY,
    lottery_id INTEGER NOT NULL REFERENCES public.lotteries(id) ON DELETE CASCADE,
    user_id BIGINT NOT NULL,
    user_name TEXT NOT NULL,
    joined_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_lottery_entries UNIQUE (lottery_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_lottery_entries_lottery
ON public.lottery_entries (lottery_id, joined_at ASC);

CREATE TABLE IF NOT EXISTS public.lottery_winners (
    id SERIAL PRIMARY KEY,
    lottery_id INTEGER NOT NULL REFERENCES public.lotteries(id) ON DELETE CASCADE,
    user_id BIGINT NOT NULL,
    user_name TEXT NOT NULL,
    winner_order INTEGER NOT NULL,
    drawn_by_user_id BIGINT NOT NULL,
    drawn_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE IF EXISTS public.lottery_winners
DROP CONSTRAINT IF EXISTS ux_lottery_winners;

CREATE INDEX IF NOT EXISTS idx_lottery_winners_lottery
ON public.lottery_winners (lottery_id, winner_order ASC);

CREATE TABLE IF NOT EXISTS public.lottery_claim_channels (
    id SERIAL PRIMARY KEY,
    lottery_id INTEGER NOT NULL REFERENCES public.lotteries(id) ON DELETE CASCADE,
    user_id BIGINT NOT NULL,
    channel_id BIGINT NOT NULL,
    control_message_id BIGINT NULL,
    is_closed BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    closed_at TIMESTAMPTZ NULL,
    CONSTRAINT ux_lottery_claim_channels UNIQUE (lottery_id, user_id)
);

ALTER TABLE IF EXISTS public.lottery_claim_channels
ADD COLUMN IF NOT EXISTS control_message_id BIGINT NULL;

ALTER TABLE IF EXISTS public.lottery_claim_channels
ADD COLUMN IF NOT EXISTS is_closed BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE IF EXISTS public.lottery_claim_channels
ADD COLUMN IF NOT EXISTS closed_at TIMESTAMPTZ NULL;

CREATE INDEX IF NOT EXISTS idx_lottery_claim_channels_channel
ON public.lottery_claim_channels (channel_id);";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task EnsureDiscordUserAsync(ulong userId)
    {
        const string sql = "INSERT INTO discord_users (discord_user_id) VALUES (@UserId) ON CONFLICT DO NOTHING;";
        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new { UserId = ToDbId(userId) });
    }

    public async Task UpsertDiscordUserProfileAsync(ulong userId, string displayName, string discordName, string? discordAvatarUrl = null)
    {
        const string sql = @"
INSERT INTO discord_users (discord_user_id, display_name, discord_name, discord_avatar_url)
VALUES (@UserId, @DisplayName, @DiscordName, @DiscordAvatarUrl)
ON CONFLICT (discord_user_id) DO UPDATE
SET display_name = EXCLUDED.display_name,
    discord_name = EXCLUDED.discord_name,
    discord_avatar_url = COALESCE(EXCLUDED.discord_avatar_url, discord_users.discord_avatar_url),
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            UserId = ToDbId(userId),
            DisplayName = displayName,
            DiscordName = discordName,
            DiscordAvatarUrl = NullIfBlank(discordAvatarUrl)
        });
    }

    public async Task<bool> HasMainCharacterAsync(ulong userId)
    {
        const string sql = @"
SELECT 1
FROM discord_user_characters
WHERE discord_user_id = @UserId
LIMIT 1;";

        await using var conn = CreateConnection();
        var exists = await conn.ExecuteScalarAsync<int?>(sql, new { UserId = ToDbId(userId) });
        return exists.HasValue;
    }

    public async Task<bool> DeleteDiscordUserDataAsync(ulong userId)
    {
        const string deleteSql = @"
DELETE FROM discord_users
WHERE discord_user_id = @UserId;";

        const string cleanupSql = @"
DELETE FROM lodestone_characters c
WHERE NOT EXISTS (
    SELECT 1
    FROM discord_user_characters duc
    WHERE duc.lodestone_id = c.lodestone_id
);";

        const string clearRetentionSql = @"
DELETE FROM inactive_kick_user_data_retentions
WHERE user_id = @UserId;";

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var affected = await conn.ExecuteAsync(deleteSql, new { UserId = ToDbId(userId) }, transaction: tx);
        await conn.ExecuteAsync(cleanupSql, transaction: tx);
        await conn.ExecuteAsync(clearRetentionSql, new { UserId = ToDbId(userId) }, transaction: tx);

        await tx.CommitAsync();
        return affected > 0;
    }

    public async Task UpsertInactiveKickUserDataRetentionAsync(ulong guildId, ulong userId, DateTimeOffset deleteAfter)
    {
        const string sql = @"
INSERT INTO inactive_kick_user_data_retentions (guild_id, user_id, delete_after)
VALUES (@GuildId, @UserId, @DeleteAfter)
ON CONFLICT (guild_id, user_id) DO UPDATE
SET delete_after = EXCLUDED.delete_after,
    warning_notified_at = NULL,
    final_warning_notified_at = NULL,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            DeleteAfter = deleteAfter
        });
    }

    public async Task<bool> HasInactiveKickUserDataRetentionAsync(ulong guildId, ulong userId)
    {
        const string sql = @"
SELECT 1
FROM inactive_kick_user_data_retentions
WHERE guild_id = @GuildId
  AND user_id = @UserId
LIMIT 1;";

        await using var conn = CreateConnection();
        var exists = await conn.ExecuteScalarAsync<int?>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId)
        });
        return exists.HasValue;
    }

    public async Task<bool> RemoveInactiveKickUserDataRetentionAsync(ulong guildId, ulong userId)
    {
        const string sql = @"
DELETE FROM inactive_kick_user_data_retentions
WHERE guild_id = @GuildId
  AND user_id = @UserId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId)
        });
        return affected > 0;
    }

    public async Task<IReadOnlyList<InactiveKickRetentionEntry>> ListDueInactiveKickUserDataRetentionsAsync(DateTimeOffset dueUtc, int maxRows = 200)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       user_id AS UserId,
       delete_after AS DeleteAfter,
       warning_notified_at AS WarningNotifiedAt,
       final_warning_notified_at AS FinalWarningNotifiedAt,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
FROM inactive_kick_user_data_retentions
WHERE delete_after <= @DueUtc
ORDER BY delete_after ASC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<InactiveKickRetentionEntry>(sql, new
        {
            DueUtc = dueUtc,
            MaxRows = Math.Clamp(maxRows, 1, 1000)
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<InactiveKickRetentionEntry>> ListDueInactiveKickUserDataRetentionWarningsAsync(DateTimeOffset nowUtc, int maxRows = 200)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       user_id AS UserId,
       delete_after AS DeleteAfter,
       warning_notified_at AS WarningNotifiedAt,
       final_warning_notified_at AS FinalWarningNotifiedAt,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
FROM inactive_kick_user_data_retentions
WHERE warning_notified_at IS NULL
  AND delete_after > @NowUtc + INTERVAL '1 hour'
  AND delete_after <= @NowUtc + INTERVAL '1 day'
ORDER BY delete_after ASC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<InactiveKickRetentionEntry>(sql, new
        {
            NowUtc = nowUtc,
            MaxRows = Math.Clamp(maxRows, 1, 1000)
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<InactiveKickRetentionEntry>> ListDueInactiveKickUserDataFinalWarningsAsync(DateTimeOffset nowUtc, int maxRows = 200)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       user_id AS UserId,
       delete_after AS DeleteAfter,
       warning_notified_at AS WarningNotifiedAt,
       final_warning_notified_at AS FinalWarningNotifiedAt,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
FROM inactive_kick_user_data_retentions
WHERE final_warning_notified_at IS NULL
  AND delete_after > @NowUtc
  AND delete_after <= @NowUtc + INTERVAL '1 hour'
ORDER BY delete_after ASC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<InactiveKickRetentionEntry>(sql, new
        {
            NowUtc = nowUtc,
            MaxRows = Math.Clamp(maxRows, 1, 1000)
        });
        return rows.ToList();
    }

    public async Task<bool> MarkInactiveKickUserDataRetentionWarningNotifiedAsync(ulong guildId, ulong userId, DateTimeOffset notifiedAtUtc)
    {
        const string sql = @"
UPDATE inactive_kick_user_data_retentions
SET warning_notified_at = @NotifiedAtUtc,
    updated_at = NOW()
WHERE guild_id = @GuildId
  AND user_id = @UserId
  AND warning_notified_at IS NULL;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            NotifiedAtUtc = notifiedAtUtc
        });
        return affected > 0;
    }

    public async Task<bool> MarkInactiveKickUserDataRetentionFinalWarningNotifiedAsync(ulong guildId, ulong userId, DateTimeOffset notifiedAtUtc)
    {
        const string sql = @"
UPDATE inactive_kick_user_data_retentions
SET final_warning_notified_at = @NotifiedAtUtc,
    updated_at = NOW()
WHERE guild_id = @GuildId
  AND user_id = @UserId
  AND final_warning_notified_at IS NULL;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            NotifiedAtUtc = notifiedAtUtc
        });
        return affected > 0;
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

    public async Task<bool> SetVoiceNotificationEnabledAsync(ulong userId, bool enabled)
    {
        const string sql = @"
INSERT INTO discord_users (discord_user_id, voice_notification_enabled)
VALUES (@UserId, @Enabled)
ON CONFLICT (discord_user_id) DO UPDATE
SET voice_notification_enabled = @Enabled,
    updated_at = NOW()
RETURNING voice_notification_enabled;";

        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(sql, new
        {
            UserId = ToDbId(userId),
            Enabled = enabled
        });
    }

    public async Task<VoiceTextAutoDeletePolicy> GetVoiceTextAutoDeletePolicyAsync(ulong userId)
    {
        const string sql = @"
SELECT vc_text_auto_delete_enabled AS Enabled,
       vc_text_auto_delete_hours AS RetentionHours
FROM discord_users
WHERE discord_user_id = @UserId
LIMIT 1;";

        await using var conn = CreateConnection();
        var policy = await conn.QuerySingleOrDefaultAsync<VoiceTextAutoDeletePolicy>(sql, new { UserId = ToDbId(userId) });
        if (policy is null)
        {
            return new VoiceTextAutoDeletePolicy
            {
                Enabled = false,
                RetentionHours = 6
            };
        }

        var retentionHours = Math.Clamp(policy.RetentionHours <= 0 ? 6 : policy.RetentionHours, 1, 168);
        return new VoiceTextAutoDeletePolicy
        {
            Enabled = policy.Enabled,
            RetentionHours = retentionHours
        };
    }

    public async Task<VoiceTextAutoDeletePolicy> SetVoiceTextAutoDeletePolicyAsync(ulong userId, bool enabled, int retentionHours)
    {
        const string sql = @"
INSERT INTO discord_users (discord_user_id, vc_text_auto_delete_enabled, vc_text_auto_delete_hours)
VALUES (@UserId, @Enabled, @RetentionHours)
ON CONFLICT (discord_user_id) DO UPDATE
SET vc_text_auto_delete_enabled = @Enabled,
    vc_text_auto_delete_hours = @RetentionHours,
    updated_at = NOW()
RETURNING vc_text_auto_delete_enabled AS Enabled,
          vc_text_auto_delete_hours AS RetentionHours;";

        var clampedRetentionHours = Math.Clamp(retentionHours <= 0 ? 6 : retentionHours, 1, 168);
        await using var conn = CreateConnection();
        var updated = await conn.QuerySingleAsync<VoiceTextAutoDeletePolicy>(sql, new
        {
            UserId = ToDbId(userId),
            Enabled = enabled,
            RetentionHours = clampedRetentionHours
        });

        return new VoiceTextAutoDeletePolicy
        {
            Enabled = updated.Enabled,
            RetentionHours = Math.Clamp(updated.RetentionHours <= 0 ? 6 : updated.RetentionHours, 1, 168)
        };
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

    public async Task RemoveReactionAsync(ulong guildId, ulong messageId, ulong userId, string emojiKey)
    {
        const string sql = @"
DELETE FROM public.user_reaction_logs
WHERE guild_id = @GuildId
  AND message_id = @MessageId
  AND user_id = @UserId
  AND emoji_key = @EmojiKey;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            MessageId = ToDbId(messageId),
            UserId = ToDbId(userId),
            EmojiKey = string.IsNullOrWhiteSpace(emojiKey) ? "unknown" : emojiKey
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

    public async Task<IReadOnlyList<VoiceSession>> ListOpenVoiceSessionsByGuildAsync(ulong guildId)
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
  AND left_at IS NULL
ORDER BY joined_at ASC, id ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<VoiceSession>(sql, new
        {
            GuildId = ToDbId(guildId)
        });
        return rows.ToList();
    }

    public async Task<VoiceSession?> EndVoiceSessionByIdAsync(long sessionId, DateTimeOffset leftAt)
    {
        const string sql = @"
UPDATE voice_sessions
SET left_at = @LeftAt,
    duration_seconds = GREATEST(0, EXTRACT(EPOCH FROM (@LeftAt - joined_at))::int)
WHERE id = @SessionId
  AND left_at IS NULL
RETURNING id AS Id,
          guild_id AS GuildId,
          channel_id AS ChannelId,
          user_id AS UserId,
          user_name AS UserName,
          joined_at AS JoinedAt,
          left_at AS LeftAt,
          duration_seconds AS DurationSeconds;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<VoiceSession>(sql, new
        {
            SessionId = sessionId,
            LeftAt = leftAt
        });
    }

    public async Task<int> AddAdminMemberNoteAsync(
        ulong guildId,
        ulong targetUserId,
        ulong authorUserId,
        string authorName,
        string note)
    {
        if (guildId == 0 || targetUserId == 0 || authorUserId == 0 || string.IsNullOrWhiteSpace(note))
        {
            return 0;
        }

        const string sql = @"
INSERT INTO public.admin_member_notes (
    guild_id,
    target_user_id,
    author_user_id,
    author_name,
    note,
    is_deleted,
    created_at,
    updated_at)
VALUES (
    @GuildId,
    @TargetUserId,
    @AuthorUserId,
    @AuthorName,
    @Note,
    FALSE,
    NOW(),
    NOW())
RETURNING id;";

        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(sql, new
        {
            GuildId = ToDbId(guildId),
            TargetUserId = ToDbId(targetUserId),
            AuthorUserId = ToDbId(authorUserId),
            AuthorName = NullIfBlank(authorName) ?? authorUserId.ToString(),
            Note = note.Trim()
        });
    }

    public async Task<IReadOnlyList<AdminMemberNote>> ListAdminMemberNotesAsync(
        ulong guildId,
        ulong targetUserId,
        int maxRows = 10)
    {
        if (guildId == 0 || targetUserId == 0)
        {
            return Array.Empty<AdminMemberNote>();
        }

        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       target_user_id AS TargetUserId,
       author_user_id AS AuthorUserId,
       author_name AS AuthorName,
       note AS Note,
       is_deleted AS IsDeleted,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
FROM public.admin_member_notes
WHERE guild_id = @GuildId
  AND target_user_id = @TargetUserId
  AND is_deleted = FALSE
ORDER BY created_at DESC, id DESC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<AdminMemberNote>(sql, new
        {
            GuildId = ToDbId(guildId),
            TargetUserId = ToDbId(targetUserId),
            MaxRows = Math.Clamp(maxRows, 1, 50)
        });
        return rows.ToList();
    }

    public async Task<int> AddAdminMemberModerationActionAsync(
        ulong guildId,
        ulong targetUserId,
        ulong actorUserId,
        string actorName,
        string actionType,
        string? reason = null,
        string? detail = null)
    {
        if (guildId == 0 || targetUserId == 0 || actorUserId == 0 || string.IsNullOrWhiteSpace(actionType))
        {
            return 0;
        }

        const string sql = @"
INSERT INTO public.admin_member_moderation_actions (
    guild_id,
    target_user_id,
    actor_user_id,
    actor_name,
    action_type,
    reason,
    detail,
    created_at)
VALUES (
    @GuildId,
    @TargetUserId,
    @ActorUserId,
    @ActorName,
    @ActionType,
    @Reason,
    @Detail,
    NOW())
RETURNING id;";

        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(sql, new
        {
            GuildId = ToDbId(guildId),
            TargetUserId = ToDbId(targetUserId),
            ActorUserId = ToDbId(actorUserId),
            ActorName = NullIfBlank(actorName) ?? actorUserId.ToString(),
            ActionType = actionType.Trim(),
            Reason = NullIfBlank(reason),
            Detail = NullIfBlank(detail)
        });
    }

    public async Task<IReadOnlyList<AdminMemberModerationAction>> ListAdminMemberModerationActionsAsync(
        ulong guildId,
        ulong targetUserId,
        int maxRows = 20)
    {
        if (guildId == 0 || targetUserId == 0)
        {
            return Array.Empty<AdminMemberModerationAction>();
        }

        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       target_user_id AS TargetUserId,
       actor_user_id AS ActorUserId,
       actor_name AS ActorName,
       action_type AS ActionType,
       reason AS Reason,
       detail AS Detail,
       created_at AS CreatedAt
FROM public.admin_member_moderation_actions
WHERE guild_id = @GuildId
  AND target_user_id = @TargetUserId
ORDER BY created_at DESC, id DESC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<AdminMemberModerationAction>(sql, new
        {
            GuildId = ToDbId(guildId),
            TargetUserId = ToDbId(targetUserId),
            MaxRows = Math.Clamp(maxRows, 1, 100)
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<AdminModerationTimelineEntry>> ListAdminModerationTimelineAsync(
        ulong guildId,
        int offsetRows,
        int maxRows = 20)
    {
        if (guildId == 0)
        {
            return Array.Empty<AdminModerationTimelineEntry>();
        }

        const string sql = @"
SELECT entry_id AS EntryId,
       target_user_id AS TargetUserId,
       actor_user_id AS ActorUserId,
       actor_name AS ActorName,
       action_type AS ActionType,
       reason AS Reason,
       detail AS Detail,
       created_at AS CreatedAt
FROM (
    SELECT id AS entry_id,
           target_user_id,
           actor_user_id,
           actor_name,
           action_type,
           reason,
           detail,
           created_at
    FROM public.admin_member_moderation_actions
    WHERE guild_id = @GuildId
    UNION ALL
    SELECT id AS entry_id,
           target_user_id,
           author_user_id AS actor_user_id,
           author_name AS actor_name,
           'admin_note' AS action_type,
           NULL::TEXT AS reason,
           note AS detail,
           created_at
    FROM public.admin_member_notes
    WHERE guild_id = @GuildId
      AND is_deleted = FALSE
) t
ORDER BY created_at DESC, entry_id DESC
LIMIT @MaxRows
OFFSET @OffsetRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<AdminModerationTimelineEntry>(sql, new
        {
            GuildId = ToDbId(guildId),
            OffsetRows = Math.Max(0, offsetRows),
            MaxRows = Math.Clamp(maxRows, 1, 100)
        });
        return rows.ToList();
    }

    public async Task<int> CountAdminModerationTimelineEntriesAsync(ulong guildId)
    {
        if (guildId == 0)
        {
            return 0;
        }

        const string sql = @"
SELECT (
    COALESCE((SELECT COUNT(1) FROM public.admin_member_moderation_actions WHERE guild_id = @GuildId), 0)
    +
    COALESCE((SELECT COUNT(1) FROM public.admin_member_notes WHERE guild_id = @GuildId AND is_deleted = FALSE), 0)
)::INT;";

        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(sql, new { GuildId = ToDbId(guildId) });
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

    public async Task<IReadOnlyList<RecruitPost>> ListRecruitPostsDueForArtifactCleanupAsync(
        DateTimeOffset closedCutoffUtc,
        DateTimeOffset canceledCutoffUtc)
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
       disallow_duplicate_jobs AS DisallowDuplicateJobs,
       host_flex_spec AS HostFlexSpec,
       host_flex_jobs_spec AS HostFlexJobsSpec,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
FROM recruit_posts
WHERE (
        status = @ClosedStatus
    AND COALESCE(schedule_at, closed_at, updated_at) <= @ClosedCutoffUtc
)
   OR (
        status = @CanceledStatus
    AND COALESCE(closed_at, updated_at) <= @CanceledCutoffUtc
)
ORDER BY COALESCE(schedule_at, closed_at, updated_at) ASC
LIMIT 200;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<RecruitPost>(sql, new
        {
            ClosedStatus = (int)RecruitPostStatus.Closed,
            CanceledStatus = (int)RecruitPostStatus.Canceled,
            ClosedCutoffUtc = closedCutoffUtc,
            CanceledCutoffUtc = canceledCutoffUtc
        });
        return rows.ToList();
    }

    public async Task<bool> DeleteRecruitPostAsync(int postId)
    {
        const string sql = @"
DELETE FROM recruit_posts
WHERE id = @PostId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new { PostId = postId });
        return affected > 0;
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
               EXTRACT(EPOCH FROM (LEAST(COALESCE(left_at, @ToUtcExclusive), @ToUtcExclusive) - GREATEST(joined_at, @FromUtcInclusive)))
           )
       )::BIGINT AS DurationSeconds
FROM voice_sessions
WHERE guild_id = @GuildId
  AND COALESCE(left_at, @ToUtcExclusive) > @FromUtcInclusive
  AND joined_at < @ToUtcExclusive
GROUP BY user_id
HAVING SUM(
           GREATEST(
               0,
               EXTRACT(EPOCH FROM (LEAST(COALESCE(left_at, @ToUtcExclusive), @ToUtcExclusive) - GREATEST(joined_at, @FromUtcInclusive)))
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

    public async Task<IReadOnlyList<VoiceChannelDurationSummary>> ListVoiceChannelDurationSummaryAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive,
        int topCount)
    {
        const string sql = @"
SELECT channel_id AS ChannelId,
       SUM(
           GREATEST(
               0,
               EXTRACT(EPOCH FROM (LEAST(COALESCE(left_at, @ToUtcExclusive), @ToUtcExclusive) - GREATEST(joined_at, @FromUtcInclusive)))
           )
       )::BIGINT AS DurationSeconds
FROM voice_sessions
WHERE guild_id = @GuildId
  AND COALESCE(left_at, @ToUtcExclusive) > @FromUtcInclusive
  AND joined_at < @ToUtcExclusive
GROUP BY channel_id
HAVING SUM(
           GREATEST(
               0,
               EXTRACT(EPOCH FROM (LEAST(COALESCE(left_at, @ToUtcExclusive), @ToUtcExclusive) - GREATEST(joined_at, @FromUtcInclusive)))
           )
       ) > 0
ORDER BY DurationSeconds DESC, channel_id ASC
LIMIT @TopCount;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<VoiceChannelDurationSummary>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive,
            TopCount = Math.Clamp(topCount, 1, 50)
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<UserActivityTimeline>> ListUserActivityTimelinesAsync(
        ulong guildId,
        IReadOnlyCollection<ulong> userIds)
    {
        if (userIds.Count == 0)
        {
            return Array.Empty<UserActivityTimeline>();
        }

        var normalizedUserIds = userIds
            .Select(ToDbId)
            .Distinct()
            .ToArray();
        if (normalizedUserIds.Length == 0)
        {
            return Array.Empty<UserActivityTimeline>();
        }

        const string sql = @"
WITH target_users AS (
    SELECT UNNEST(@UserIds::BIGINT[]) AS user_id
),
message_edges AS (
    SELECT author_id AS user_id,
           MIN(created_at) AS first_message_at,
           MAX(created_at) AS last_message_at
    FROM message_logs
    WHERE guild_id = @GuildId
      AND deleted_at IS NULL
      AND author_id = ANY(@UserIds)
    GROUP BY author_id
),
voice_edges AS (
    SELECT user_id,
           MIN(joined_at) AS first_voice_at,
           MAX(joined_at) AS last_voice_joined_at,
           MAX(COALESCE(left_at, joined_at)) AS last_voice_at
    FROM voice_sessions
    WHERE guild_id = @GuildId
      AND user_id = ANY(@UserIds)
    GROUP BY user_id
),
reaction_edges AS (
    SELECT user_id,
           MIN(reacted_at) AS first_reaction_at,
           MAX(reacted_at) AS last_reaction_at
    FROM public.user_reaction_logs
    WHERE guild_id = @GuildId
      AND user_id = ANY(@UserIds)
    GROUP BY user_id
)
SELECT t.user_id AS UserId,
       m.first_message_at AS FirstMessageAt,
       v.first_voice_at AS FirstVoiceAt,
       r.first_reaction_at AS FirstReactionAt,
       m.last_message_at AS LastMessageAt,
       v.last_voice_at AS LastVoiceAt,
       v.last_voice_joined_at AS LastVoiceJoinedAt,
       r.last_reaction_at AS LastReactionAt,
       CASE
           WHEN m.last_message_at IS NULL
            AND v.last_voice_at IS NULL
            AND r.last_reaction_at IS NULL
               THEN NULL
           ELSE GREATEST(
                COALESCE(m.last_message_at, 'epoch'::timestamptz),
                COALESCE(v.last_voice_at, 'epoch'::timestamptz),
                COALESCE(r.last_reaction_at, 'epoch'::timestamptz))
       END AS LastActivityAt
FROM target_users t
LEFT JOIN message_edges m ON m.user_id = t.user_id
LEFT JOIN voice_edges v ON v.user_id = t.user_id
LEFT JOIN reaction_edges r ON r.user_id = t.user_id
ORDER BY t.user_id ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<UserActivityTimeline>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserIds = normalizedUserIds
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<VoiceHourlyActivity>> ListVoiceHourlyActivityAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive)
    {
        if (toUtcExclusive <= fromUtcInclusive)
        {
            return Array.Empty<VoiceHourlyActivity>();
        }

        const string sql = @"
WITH bounded AS (
    SELECT id,
           user_id,
           GREATEST(joined_at, @FromUtcInclusive) AS start_utc,
           LEAST(COALESCE(left_at, @ToUtcExclusive), @ToUtcExclusive) AS end_utc
    FROM voice_sessions
    WHERE guild_id = @GuildId
      AND COALESCE(left_at, @ToUtcExclusive) > @FromUtcInclusive
      AND joined_at < @ToUtcExclusive
),
expanded AS (
    SELECT b.id,
           b.user_id,
           gs.bucket_start,
           (b.start_utc AT TIME ZONE 'Asia/Tokyo') AS start_jst,
           (b.end_utc AT TIME ZONE 'Asia/Tokyo') AS end_jst
    FROM bounded b
    CROSS JOIN LATERAL generate_series(
        date_trunc('hour', b.start_utc AT TIME ZONE 'Asia/Tokyo'),
        date_trunc('hour', (b.end_utc AT TIME ZONE 'Asia/Tokyo') - INTERVAL '1 second'),
        INTERVAL '1 hour'
    ) AS gs(bucket_start)
)
SELECT EXTRACT(HOUR FROM bucket_start)::INT AS Hour,
       COALESCE(SUM(
           GREATEST(
               0,
               EXTRACT(EPOCH FROM (
                   LEAST(end_jst, bucket_start + INTERVAL '1 hour')
                   - GREATEST(start_jst, bucket_start)
               ))
           )
       ), 0)::BIGINT AS TotalSeconds,
       COUNT(DISTINCT id)::INT AS SessionCount,
       COUNT(DISTINCT user_id)::INT AS ActiveUsers
FROM expanded
GROUP BY Hour
ORDER BY Hour ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<VoiceHourlyActivity>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<VoiceHourlyActivity>> ListVoiceHourlyActivityByUserAsync(
        ulong guildId,
        ulong userId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive)
    {
        if (guildId == 0 || userId == 0 || toUtcExclusive <= fromUtcInclusive)
        {
            return Array.Empty<VoiceHourlyActivity>();
        }

        const string sql = @"
WITH bounded AS (
    SELECT id,
           GREATEST(joined_at, @FromUtcInclusive) AS start_utc,
           LEAST(COALESCE(left_at, @ToUtcExclusive), @ToUtcExclusive) AS end_utc
    FROM voice_sessions
    WHERE guild_id = @GuildId
      AND user_id = @UserId
      AND COALESCE(left_at, @ToUtcExclusive) > @FromUtcInclusive
      AND joined_at < @ToUtcExclusive
),
expanded AS (
    SELECT b.id,
           gs.bucket_start,
           (b.start_utc AT TIME ZONE 'Asia/Tokyo') AS start_jst,
           (b.end_utc AT TIME ZONE 'Asia/Tokyo') AS end_jst
    FROM bounded b
    CROSS JOIN LATERAL generate_series(
        date_trunc('hour', b.start_utc AT TIME ZONE 'Asia/Tokyo'),
        date_trunc('hour', (b.end_utc AT TIME ZONE 'Asia/Tokyo') - INTERVAL '1 second'),
        INTERVAL '1 hour'
    ) AS gs(bucket_start)
)
SELECT EXTRACT(HOUR FROM bucket_start)::INT AS Hour,
       COALESCE(SUM(
           GREATEST(
               0,
               EXTRACT(EPOCH FROM (
                   LEAST(end_jst, bucket_start + INTERVAL '1 hour')
                   - GREATEST(start_jst, bucket_start)
               ))
           )
       ), 0)::BIGINT AS TotalSeconds,
       COUNT(DISTINCT id)::INT AS SessionCount,
       CASE WHEN COUNT(DISTINCT id) > 0 THEN 1 ELSE 0 END::INT AS ActiveUsers
FROM expanded
GROUP BY Hour
ORDER BY Hour ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<VoiceHourlyActivity>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<VoiceWeekdayHourlyActivity>> ListVoiceWeekdayHourlyActivityAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive)
    {
        if (toUtcExclusive <= fromUtcInclusive)
        {
            return Array.Empty<VoiceWeekdayHourlyActivity>();
        }

        const string sql = @"
WITH bounded AS (
    SELECT id,
           user_id,
           GREATEST(joined_at, @FromUtcInclusive) AS start_utc,
           LEAST(COALESCE(left_at, @ToUtcExclusive), @ToUtcExclusive) AS end_utc
    FROM voice_sessions
    WHERE guild_id = @GuildId
      AND COALESCE(left_at, @ToUtcExclusive) > @FromUtcInclusive
      AND joined_at < @ToUtcExclusive
),
expanded AS (
    SELECT b.id,
           b.user_id,
           gs.bucket_start,
           (b.start_utc AT TIME ZONE 'Asia/Tokyo') AS start_jst,
           (b.end_utc AT TIME ZONE 'Asia/Tokyo') AS end_jst
    FROM bounded b
    CROSS JOIN LATERAL generate_series(
        date_trunc('hour', b.start_utc AT TIME ZONE 'Asia/Tokyo'),
        date_trunc('hour', (b.end_utc AT TIME ZONE 'Asia/Tokyo') - INTERVAL '1 second'),
        INTERVAL '1 hour'
    ) AS gs(bucket_start)
)
SELECT ((EXTRACT(ISODOW FROM bucket_start)::INT + 6) % 7) AS Weekday,
       EXTRACT(HOUR FROM bucket_start)::INT AS Hour,
       COALESCE(SUM(
           GREATEST(
               0,
               EXTRACT(EPOCH FROM (
                   LEAST(end_jst, bucket_start + INTERVAL '1 hour')
                   - GREATEST(start_jst, bucket_start)
               ))
           )
       ), 0)::BIGINT AS TotalSeconds,
       COUNT(DISTINCT id)::INT AS SessionCount,
       COUNT(DISTINCT user_id)::INT AS ActiveUsers
FROM expanded
GROUP BY Weekday, Hour
ORDER BY Weekday ASC, Hour ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<VoiceWeekdayHourlyActivity>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<VoiceUserHourSummary>> ListVoiceTopUsersByHourAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive,
        int hourJst,
        int topCount)
    {
        if (toUtcExclusive <= fromUtcInclusive)
        {
            return Array.Empty<VoiceUserHourSummary>();
        }

        var normalizedHour = Math.Clamp(hourJst, 0, 23);
        var normalizedTopCount = Math.Clamp(topCount, 1, 20);

        const string sql = @"
WITH bounded AS (
    SELECT id,
           user_id,
           GREATEST(joined_at, @FromUtcInclusive) AS start_utc,
           LEAST(COALESCE(left_at, @ToUtcExclusive), @ToUtcExclusive) AS end_utc
    FROM voice_sessions
    WHERE guild_id = @GuildId
      AND COALESCE(left_at, @ToUtcExclusive) > @FromUtcInclusive
      AND joined_at < @ToUtcExclusive
),
expanded AS (
    SELECT b.user_id,
           gs.bucket_start,
           (b.start_utc AT TIME ZONE 'Asia/Tokyo') AS start_jst,
           (b.end_utc AT TIME ZONE 'Asia/Tokyo') AS end_jst
    FROM bounded b
    CROSS JOIN LATERAL generate_series(
        date_trunc('hour', b.start_utc AT TIME ZONE 'Asia/Tokyo'),
        date_trunc('hour', (b.end_utc AT TIME ZONE 'Asia/Tokyo') - INTERVAL '1 second'),
        INTERVAL '1 hour'
    ) AS gs(bucket_start)
)
SELECT user_id AS UserId,
       COALESCE(SUM(
           GREATEST(
               0,
               EXTRACT(EPOCH FROM (
                   LEAST(end_jst, bucket_start + INTERVAL '1 hour')
                   - GREATEST(start_jst, bucket_start)
               ))
           )
       ), 0)::BIGINT AS DurationSeconds
FROM expanded
WHERE EXTRACT(HOUR FROM bucket_start)::INT = @HourJst
GROUP BY user_id
HAVING COALESCE(SUM(
           GREATEST(
               0,
               EXTRACT(EPOCH FROM (
                   LEAST(end_jst, bucket_start + INTERVAL '1 hour')
                   - GREATEST(start_jst, bucket_start)
               ))
           )
       ), 0) > 0
ORDER BY DurationSeconds DESC, user_id ASC
LIMIT @TopCount;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<VoiceUserHourSummary>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive,
            HourJst = normalizedHour,
            TopCount = normalizedTopCount
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

    public async Task<IReadOnlyList<MessageWeekdayHourlyActivity>> ListMessageWeekdayHourlyActivityAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive)
    {
        if (toUtcExclusive <= fromUtcInclusive)
        {
            return Array.Empty<MessageWeekdayHourlyActivity>();
        }

        const string sql = @"
SELECT ((EXTRACT(ISODOW FROM created_at AT TIME ZONE 'Asia/Tokyo')::INT + 6) % 7) AS Weekday,
       EXTRACT(HOUR FROM created_at AT TIME ZONE 'Asia/Tokyo')::INT AS Hour,
       COUNT(1)::BIGINT AS MessageCount,
       COUNT(DISTINCT author_id)::INT AS ActiveUsers
FROM message_logs
WHERE guild_id = @GuildId
  AND deleted_at IS NULL
  AND created_at >= @FromUtcInclusive
  AND created_at < @ToUtcExclusive
GROUP BY Weekday, Hour
ORDER BY Weekday ASC, Hour ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<MessageWeekdayHourlyActivity>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<MessageHourlyActivity>> ListMessageHourlyActivityByUserAsync(
        ulong guildId,
        ulong userId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive,
        bool includeDeleted = false)
    {
        if (guildId == 0 || userId == 0 || toUtcExclusive <= fromUtcInclusive)
        {
            return Array.Empty<MessageHourlyActivity>();
        }

        const string sql = @"
SELECT EXTRACT(HOUR FROM created_at AT TIME ZONE 'Asia/Tokyo')::INT AS Hour,
       COUNT(1)::BIGINT AS MessageCount
FROM message_logs
WHERE guild_id = @GuildId
  AND author_id = @UserId
  AND created_at >= @FromUtcInclusive
  AND created_at < @ToUtcExclusive
  AND (@IncludeDeleted OR deleted_at IS NULL)
GROUP BY Hour
ORDER BY Hour ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<MessageHourlyActivity>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive,
            IncludeDeleted = includeDeleted
        });
        return rows.ToList();
    }

    public async Task RecordActivityRankingAwardsAsync(
        ulong guildId,
        string rankingKey,
        IReadOnlyCollection<ulong> userIds,
        IReadOnlyDictionary<ulong, int>? bestRanksByUser = null)
    {
        if (string.IsNullOrWhiteSpace(rankingKey) || userIds.Count == 0)
        {
            return;
        }

        var normalized = userIds
            .Where(id => id != 0)
            .Select(ToDbId)
            .Distinct()
            .ToArray();
        if (normalized.Length == 0)
        {
            return;
        }

        var normalizedRankingKey = rankingKey.Trim();
        var guildDbId = ToDbId(guildId);
        var rows = normalized.Select(dbUserId =>
        {
            var userId = ToUlong(dbUserId);
            int? bestRank = null;
            if (bestRanksByUser is not null &&
                bestRanksByUser.TryGetValue(userId, out var rank) &&
                rank > 0)
            {
                bestRank = Math.Clamp(rank, 1, 99);
            }

            return new
            {
                GuildId = guildDbId,
                RankingKey = normalizedRankingKey,
                UserId = dbUserId,
                BestRank = bestRank
            };
        }).ToArray();

        const string sql = @"
INSERT INTO activity_ranking_awards (guild_id, ranking_key, user_id, best_rank)
VALUES (@GuildId, @RankingKey, @UserId, @BestRank)
ON CONFLICT (guild_id, ranking_key, user_id) DO UPDATE
SET best_rank = CASE
    WHEN activity_ranking_awards.best_rank IS NULL THEN EXCLUDED.best_rank
    WHEN EXCLUDED.best_rank IS NULL THEN activity_ranking_awards.best_rank
    ELSE LEAST(activity_ranking_awards.best_rank, EXCLUDED.best_rank)
END;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, rows);
    }

    public async Task<int> CountActivityRankingAwardsByUserAsync(ulong guildId, ulong userId)
    {
        if (guildId == 0 || userId == 0)
        {
            return 0;
        }

        const string sql = @"
SELECT COUNT(1)::INT
FROM activity_ranking_awards
WHERE guild_id = @GuildId
  AND user_id = @UserId;";

        await using var conn = CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId)
        });
        return Math.Max(0, count);
    }

    public async Task<ActivityRankingPodiumCount> GetActivityRankingPodiumCountsByUserAsync(ulong guildId, ulong userId)
    {
        if (guildId == 0 || userId == 0)
        {
            return new ActivityRankingPodiumCount();
        }

        const string sql = @"
SELECT COALESCE(SUM(CASE WHEN best_rank = 1 THEN 1 ELSE 0 END), 0)::INT AS FirstCount,
       COALESCE(SUM(CASE WHEN best_rank = 2 THEN 1 ELSE 0 END), 0)::INT AS SecondCount,
       COALESCE(SUM(CASE WHEN best_rank = 3 THEN 1 ELSE 0 END), 0)::INT AS ThirdCount
FROM activity_ranking_awards
WHERE guild_id = @GuildId
  AND user_id = @UserId;";

        await using var conn = CreateConnection();
        var row = await conn.QuerySingleAsync<ActivityRankingPodiumCount>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId)
        });
        return row ?? new ActivityRankingPodiumCount();
    }

    public async Task RecordActivityRankingMetricAwardsAsync(
        ulong guildId,
        string rankingKey,
        string metricKey,
        IReadOnlyList<ulong> rankedUserIds)
    {
        if (guildId == 0 ||
            string.IsNullOrWhiteSpace(rankingKey) ||
            string.IsNullOrWhiteSpace(metricKey) ||
            rankedUserIds.Count == 0)
        {
            return;
        }

        var normalizedUsers = rankedUserIds
            .Where(id => id != 0)
            .Distinct()
            .Take(50)
            .Select(ToDbId)
            .ToArray();
        if (normalizedUsers.Length == 0)
        {
            return;
        }

        const string sql = @"
WITH ranked AS (
    SELECT uid AS user_id,
           ord::INT AS rank
    FROM UNNEST(@UserIds::BIGINT[]) WITH ORDINALITY AS x(uid, ord)
)
INSERT INTO activity_ranking_metric_awards (guild_id, ranking_key, metric_key, user_id, rank)
SELECT @GuildId, @RankingKey, @MetricKey, user_id, rank
FROM ranked
ON CONFLICT (guild_id, ranking_key, metric_key, user_id) DO UPDATE
SET rank = LEAST(activity_ranking_metric_awards.rank, EXCLUDED.rank),
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            RankingKey = rankingKey.Trim(),
            MetricKey = metricKey.Trim(),
            UserIds = normalizedUsers
        });
    }

    public async Task<ActivityRankingPodiumCount> GetActivityRankingMetricPodiumCountsByUserAsync(
        ulong guildId,
        ulong userId,
        string metricKey)
    {
        if (guildId == 0 || userId == 0 || string.IsNullOrWhiteSpace(metricKey))
        {
            return new ActivityRankingPodiumCount();
        }

        const string sql = @"
SELECT COALESCE(SUM(CASE WHEN rank = 1 THEN 1 ELSE 0 END), 0)::INT AS FirstCount,
       COALESCE(SUM(CASE WHEN rank = 2 THEN 1 ELSE 0 END), 0)::INT AS SecondCount,
       COALESCE(SUM(CASE WHEN rank = 3 THEN 1 ELSE 0 END), 0)::INT AS ThirdCount
FROM activity_ranking_metric_awards
WHERE guild_id = @GuildId
  AND user_id = @UserId
  AND metric_key = @MetricKey;";

        await using var conn = CreateConnection();
        var row = await conn.QuerySingleAsync<ActivityRankingPodiumCount>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            MetricKey = metricKey.Trim()
        });
        return row ?? new ActivityRankingPodiumCount();
    }

    public async Task<IReadOnlyList<ReactionCountByChannel>> ListReactionCountsByChannelAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive)
    {
        const string sql = @"
SELECT r.channel_id AS ChannelId,
       r.user_id AS UserId,
       COUNT(1)::INT AS ReactionCount
FROM public.user_reaction_logs r
JOIN public.message_logs m
  ON m.guild_id = r.guild_id
 AND m.message_id = r.message_id
WHERE r.guild_id = @GuildId
  AND r.reacted_at >= @FromUtcInclusive
  AND r.reacted_at < @ToUtcExclusive
  AND m.author_id <> 0
  AND m.author_id <> r.user_id
GROUP BY r.channel_id, r.user_id;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<ReactionCountByChannel>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ReactionCountByChannel>> ListReactionReceivedCountsByChannelAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive)
    {
        const string sql = @"
SELECT r.channel_id AS ChannelId,
       m.author_id AS UserId,
       COUNT(1)::INT AS ReactionCount
FROM public.user_reaction_logs r
JOIN public.message_logs m
  ON m.guild_id = r.guild_id
 AND m.message_id = r.message_id
WHERE r.guild_id = @GuildId
  AND r.reacted_at >= @FromUtcInclusive
  AND r.reacted_at < @ToUtcExclusive
  AND m.author_id <> r.user_id
  AND m.author_id <> 0
GROUP BY r.channel_id, m.author_id;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<ReactionCountByChannel>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ReactionReceivedTopPost>> ListTopReactionReceivedPostsByUserAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive)
    {
        const string sql = @"
WITH per_post AS (
    SELECT m.author_id AS UserId,
           MIN(r.channel_id) AS ChannelId,
           r.message_id AS MessageId,
           COUNT(1)::INT AS ReactionCount,
           MAX(r.reacted_at) AS LastReactedAt
    FROM public.user_reaction_logs r
    JOIN public.message_logs m
      ON m.guild_id = r.guild_id
     AND m.message_id = r.message_id
    WHERE r.guild_id = @GuildId
      AND r.reacted_at >= @FromUtcInclusive
      AND r.reacted_at < @ToUtcExclusive
      AND m.deleted_at IS NULL
      AND m.author_id <> 0
      AND m.author_id <> r.user_id
    GROUP BY m.author_id, r.message_id
),
ranked AS (
    SELECT UserId,
           ChannelId,
           MessageId,
           ReactionCount,
           ROW_NUMBER() OVER (
               PARTITION BY UserId
               ORDER BY ReactionCount DESC, LastReactedAt DESC, MessageId DESC
           ) AS rn
    FROM per_post
)
SELECT UserId,
       ChannelId,
       MessageId,
       ReactionCount
FROM ranked
WHERE rn = 1;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<ReactionReceivedTopPost>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ReactionPostSummary>> ListTopReactedPostsAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive,
        int topCount = 10,
        bool includeDeleted = false)
    {
        const string sql = @"
SELECT r.message_id AS MessageId,
       MIN(r.channel_id) AS ChannelId,
       COUNT(1)::INT AS ReactionCount,
       COUNT(DISTINCT r.user_id)::INT AS ReactorCount,
       MAX(r.reacted_at) AS LastReactedAt,
       m.author_id AS AuthorId,
       m.author_name AS AuthorName,
       m.author_display_name AS AuthorDisplayName,
       m.content AS Content,
       m.deleted_at AS DeletedAt
FROM public.user_reaction_logs r
LEFT JOIN public.message_logs m
  ON m.guild_id = r.guild_id
 AND m.message_id = r.message_id
WHERE r.guild_id = @GuildId
  AND r.reacted_at >= @FromUtcInclusive
  AND r.reacted_at < @ToUtcExclusive
  AND (@IncludeDeleted OR m.deleted_at IS NULL OR m.message_id IS NULL)
GROUP BY r.message_id, m.author_id, m.author_name, m.author_display_name, m.content, m.deleted_at
ORDER BY ReactionCount DESC, ReactorCount DESC, LastReactedAt DESC, r.message_id DESC
LIMIT @TopCount;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<ReactionPostSummary>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive,
            IncludeDeleted = includeDeleted,
            TopCount = Math.Clamp(topCount, 1, 50)
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

    public async Task<IReadOnlyList<MessageLog>> ListRecentMessageLogsAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive,
        ulong? userId = null,
        int maxRows = 100,
        bool includeDeleted = false)
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
  AND (@UserId IS NULL OR author_id = @UserId)
  AND (@IncludeDeleted OR deleted_at IS NULL)
ORDER BY created_at DESC, message_id DESC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        long? userDbId = userId.HasValue ? ToDbId(userId.Value) : null;
        var rows = await conn.QueryAsync<MessageLog>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive,
            UserId = userDbId,
            IncludeDeleted = includeDeleted,
            MaxRows = Math.Clamp(maxRows, 1, 200)
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<MessageLog>> ListRecentMessageLogsByChannelAsync(
        ulong guildId,
        ulong channelId,
        ulong? userId = null,
        int maxRows = 1000,
        bool includeDeleted = false)
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
  AND channel_id = @ChannelId
  AND (@UserId IS NULL OR author_id = @UserId)
  AND (@IncludeDeleted OR deleted_at IS NULL)
ORDER BY created_at DESC, message_id DESC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        long? userDbId = userId.HasValue ? ToDbId(userId.Value) : null;
        var rows = await conn.QueryAsync<MessageLog>(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            UserId = userDbId,
            IncludeDeleted = includeDeleted,
            MaxRows = Math.Clamp(maxRows, 1, 5000)
        });
        return rows.ToList();
    }

    public async Task<bool> ExistsSsAlbumFavoriteAsync(
        ulong guildId,
        ulong viewerUserId,
        ulong messageId,
        ulong attachmentId)
    {
        if (guildId == 0 || viewerUserId == 0 || messageId == 0 || attachmentId == 0)
        {
            return false;
        }

        const string sql = @"
SELECT EXISTS (
    SELECT 1
    FROM public.ss_album_favorites
    WHERE guild_id = @GuildId
      AND viewer_user_id = @ViewerUserId
      AND message_id = @MessageId
      AND attachment_id = @AttachmentId
);";

        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(sql, new
        {
            GuildId = ToDbId(guildId),
            ViewerUserId = ToDbId(viewerUserId),
            MessageId = ToDbId(messageId),
            AttachmentId = ToDbId(attachmentId)
        });
    }

    public async Task UpsertSsAlbumFavoriteAsync(
        ulong guildId,
        ulong viewerUserId,
        ulong targetUserId,
        ulong messageId,
        ulong attachmentId,
        ulong channelId,
        string imageUrl,
        string? fileName,
        string? messageContent,
        string? localPath,
        DateTimeOffset postedAt)
    {
        if (guildId == 0 || viewerUserId == 0 || targetUserId == 0 || messageId == 0 || attachmentId == 0 || channelId == 0 ||
            string.IsNullOrWhiteSpace(imageUrl))
        {
            return;
        }

        const string sql = @"
INSERT INTO public.ss_album_favorites (
    guild_id,
    viewer_user_id,
    target_user_id,
    message_id,
    attachment_id,
    channel_id,
    image_url,
    file_name,
    message_content,
    local_path,
    posted_at)
VALUES (
    @GuildId,
    @ViewerUserId,
    @TargetUserId,
    @MessageId,
    @AttachmentId,
    @ChannelId,
    @ImageUrl,
    @FileName,
    @MessageContent,
    @LocalPath,
    @PostedAt)
ON CONFLICT (guild_id, viewer_user_id, message_id, attachment_id) DO UPDATE
SET target_user_id = EXCLUDED.target_user_id,
    channel_id = EXCLUDED.channel_id,
    image_url = EXCLUDED.image_url,
    file_name = EXCLUDED.file_name,
    message_content = EXCLUDED.message_content,
    local_path = EXCLUDED.local_path,
    posted_at = EXCLUDED.posted_at,
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ViewerUserId = ToDbId(viewerUserId),
            TargetUserId = ToDbId(targetUserId),
            MessageId = ToDbId(messageId),
            AttachmentId = ToDbId(attachmentId),
            ChannelId = ToDbId(channelId),
            ImageUrl = imageUrl,
            FileName = NullIfBlank(fileName),
            MessageContent = NullIfBlank(messageContent),
            LocalPath = NullIfBlank(localPath),
            PostedAt = postedAt
        });
    }

    public async Task<bool> DeleteSsAlbumFavoriteAsync(
        ulong guildId,
        ulong viewerUserId,
        ulong messageId,
        ulong attachmentId)
    {
        if (guildId == 0 || viewerUserId == 0 || messageId == 0 || attachmentId == 0)
        {
            return false;
        }

        const string sql = @"
DELETE FROM public.ss_album_favorites
WHERE guild_id = @GuildId
  AND viewer_user_id = @ViewerUserId
  AND message_id = @MessageId
  AND attachment_id = @AttachmentId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ViewerUserId = ToDbId(viewerUserId),
            MessageId = ToDbId(messageId),
            AttachmentId = ToDbId(attachmentId)
        });
        return affected > 0;
    }

    public async Task<IReadOnlyList<SsAlbumFavorite>> ListSsAlbumFavoritesAsync(
        ulong guildId,
        ulong viewerUserId,
        ulong? targetUserId = null,
        int maxRows = 2000)
    {
        if (guildId == 0 || viewerUserId == 0)
        {
            return Array.Empty<SsAlbumFavorite>();
        }

        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       viewer_user_id AS ViewerUserId,
       target_user_id AS TargetUserId,
       message_id AS MessageId,
       attachment_id AS AttachmentId,
       channel_id AS ChannelId,
       image_url AS ImageUrl,
       file_name AS FileName,
       message_content AS MessageContent,
       local_path AS LocalPath,
       posted_at AS PostedAt,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
FROM public.ss_album_favorites
WHERE guild_id = @GuildId
  AND viewer_user_id = @ViewerUserId
  AND (@TargetUserId IS NULL OR target_user_id = @TargetUserId)
ORDER BY posted_at DESC, id DESC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        long? targetUserDbId = targetUserId.HasValue ? ToDbId(targetUserId.Value) : null;
        var rows = await conn.QueryAsync<SsAlbumFavorite>(sql, new
        {
            GuildId = ToDbId(guildId),
            ViewerUserId = ToDbId(viewerUserId),
            TargetUserId = targetUserDbId,
            MaxRows = Math.Clamp(maxRows, 1, 5000)
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<SsAlbumFavoriteKey>> ListSsAlbumFavoriteKeysAsync(
        ulong guildId,
        ulong viewerUserId,
        ulong? targetUserId = null,
        int maxRows = 5000)
    {
        if (guildId == 0 || viewerUserId == 0)
        {
            return Array.Empty<SsAlbumFavoriteKey>();
        }

        const string sql = @"
SELECT message_id AS MessageId,
       attachment_id AS AttachmentId
FROM public.ss_album_favorites
WHERE guild_id = @GuildId
  AND viewer_user_id = @ViewerUserId
  AND (@TargetUserId IS NULL OR target_user_id = @TargetUserId)
ORDER BY posted_at DESC, id DESC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        long? targetUserDbId = targetUserId.HasValue ? ToDbId(targetUserId.Value) : null;
        var rows = await conn.QueryAsync<SsAlbumFavoriteKey>(sql, new
        {
            GuildId = ToDbId(guildId),
            ViewerUserId = ToDbId(viewerUserId),
            TargetUserId = targetUserDbId,
            MaxRows = Math.Clamp(maxRows, 1, 10000)
        });
        return rows.ToList();
    }

    public async Task<int> CountMessagesByUserAsync(
        ulong guildId,
        ulong userId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive,
        bool includeDeleted = false)
    {
        if (guildId == 0 || userId == 0 || toUtcExclusive <= fromUtcInclusive)
        {
            return 0;
        }

        const string sql = @"
SELECT COUNT(1)::INT
FROM message_logs
WHERE guild_id = @GuildId
  AND author_id = @UserId
  AND created_at >= @FromUtcInclusive
  AND created_at < @ToUtcExclusive
  AND (@IncludeDeleted OR deleted_at IS NULL);";

        await using var conn = CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive,
            IncludeDeleted = includeDeleted
        });
        return Math.Max(0, count);
    }

    public async Task<int> CountReactionsByUserAsync(
        ulong guildId,
        ulong userId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive)
    {
        if (guildId == 0 || userId == 0 || toUtcExclusive <= fromUtcInclusive)
        {
            return 0;
        }

        const string sql = @"
SELECT COUNT(1)::INT
FROM public.user_reaction_logs
WHERE guild_id = @GuildId
  AND user_id = @UserId
  AND reacted_at >= @FromUtcInclusive
  AND reacted_at < @ToUtcExclusive;";

        await using var conn = CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive
        });
        return Math.Max(0, count);
    }

    public async Task<IReadOnlyList<VoiceSession>> ListRecentVoiceSessionsAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive,
        ulong? userId = null,
        int maxRows = 100)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       user_id AS UserId,
       user_name AS UserName,
       joined_at AS JoinedAt,
       left_at AS LeftAt,
       GREATEST(
           0,
           EXTRACT(
               EPOCH FROM (
                   LEAST(COALESCE(left_at, @ToUtcExclusive), @ToUtcExclusive)
                   - GREATEST(joined_at, @FromUtcInclusive)
               )
           )::INT
       ) AS DurationSeconds
FROM voice_sessions
WHERE guild_id = @GuildId
  AND (@UserId IS NULL OR user_id = @UserId)
  AND COALESCE(left_at, @ToUtcExclusive) > @FromUtcInclusive
  AND joined_at < @ToUtcExclusive
ORDER BY joined_at DESC, id DESC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        long? userDbId = userId.HasValue ? ToDbId(userId.Value) : null;
        var rows = await conn.QueryAsync<VoiceSession>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive,
            UserId = userDbId,
            MaxRows = Math.Clamp(maxRows, 1, 200)
        });
        return rows.ToList();
    }

    public async Task<(int SessionCount, long TotalDurationSeconds)> SummarizeVoiceByUserAsync(
        ulong guildId,
        ulong userId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive)
    {
        if (guildId == 0 || userId == 0 || toUtcExclusive <= fromUtcInclusive)
        {
            return (0, 0L);
        }

        const string sql = @"
SELECT COUNT(1)::INT AS SessionCount,
       COALESCE(SUM(
           GREATEST(
               0,
               EXTRACT(
                   EPOCH FROM (
                       LEAST(COALESCE(left_at, @ToUtcExclusive), @ToUtcExclusive)
                       - GREATEST(joined_at, @FromUtcInclusive)
                   )
               )
           )
       ), 0)::BIGINT AS TotalDurationSeconds
FROM voice_sessions
WHERE guild_id = @GuildId
  AND user_id = @UserId
  AND COALESCE(left_at, @ToUtcExclusive) > @FromUtcInclusive
  AND joined_at < @ToUtcExclusive;";

        await using var conn = CreateConnection();
        var row = await conn.QuerySingleAsync<VoiceUserSummaryAggregate>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive
        });
        return (Math.Max(0, row.SessionCount), Math.Max(0L, row.TotalDurationSeconds));
    }

    public async Task<long> SummarizeVoiceDurationByGuildAsync(
        ulong guildId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive)
    {
        if (guildId == 0 || toUtcExclusive <= fromUtcInclusive)
        {
            return 0L;
        }

        const string sql = @"
WITH clipped AS (
    SELECT GREATEST(joined_at, @FromUtcInclusive) AS start_at,
           LEAST(COALESCE(left_at, @ToUtcExclusive), @ToUtcExclusive) AS end_at
    FROM voice_sessions
    WHERE guild_id = @GuildId
      AND COALESCE(left_at, @ToUtcExclusive) > @FromUtcInclusive
      AND joined_at < @ToUtcExclusive
),
events AS (
    SELECT start_at AS ts, 1 AS delta
    FROM clipped
    WHERE end_at > start_at
    UNION ALL
    SELECT end_at AS ts, -1 AS delta
    FROM clipped
    WHERE end_at > start_at
),
merged AS (
    SELECT ts,
           SUM(delta) AS delta
    FROM events
    GROUP BY ts
),
timeline AS (
    SELECT ts,
           LEAD(ts) OVER (ORDER BY ts) AS next_ts,
           SUM(delta) OVER (ORDER BY ts ROWS UNBOUNDED PRECEDING) AS active_count
    FROM merged
)
SELECT COALESCE(
           SUM(
               EXTRACT(EPOCH FROM (next_ts - ts))
           ) FILTER (WHERE active_count > 0 AND next_ts IS NOT NULL AND next_ts > ts),
           0
       )::BIGINT
FROM timeline;";

        await using var conn = CreateConnection();
        var total = await conn.ExecuteScalarAsync<long>(sql, new
        {
            GuildId = ToDbId(guildId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive
        });
        return Math.Max(0L, total);
    }

    public async Task<IReadOnlyList<MessageLog>> ListMessageLogsForUserExportAsync(
        ulong guildId,
        ulong userId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive,
        int maxRows = 5000,
        bool includeDeleted = true)
    {
        if (guildId == 0 || userId == 0)
        {
            return Array.Empty<MessageLog>();
        }

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
  AND author_id = @UserId
  AND created_at >= @FromUtcInclusive
  AND created_at < @ToUtcExclusive
  AND (@IncludeDeleted OR deleted_at IS NULL)
ORDER BY created_at DESC, message_id DESC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<MessageLog>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive,
            IncludeDeleted = includeDeleted,
            MaxRows = Math.Clamp(maxRows, 1, 50000)
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<VoiceSession>> ListVoiceSessionsForUserExportAsync(
        ulong guildId,
        ulong userId,
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive,
        int maxRows = 5000)
    {
        if (guildId == 0 || userId == 0)
        {
            return Array.Empty<VoiceSession>();
        }

        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       user_id AS UserId,
       user_name AS UserName,
       joined_at AS JoinedAt,
       left_at AS LeftAt,
       GREATEST(
           0,
           EXTRACT(
               EPOCH FROM (
                   LEAST(COALESCE(left_at, @ToUtcExclusive), @ToUtcExclusive)
                   - GREATEST(joined_at, @FromUtcInclusive)
               )
           )::INT
       ) AS DurationSeconds
FROM voice_sessions
WHERE guild_id = @GuildId
  AND user_id = @UserId
  AND COALESCE(left_at, @ToUtcExclusive) > @FromUtcInclusive
  AND joined_at < @ToUtcExclusive
ORDER BY joined_at DESC, id DESC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<VoiceSession>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            FromUtcInclusive = fromUtcInclusive,
            ToUtcExclusive = toUtcExclusive,
            MaxRows = Math.Clamp(maxRows, 1, 50000)
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

    public async Task<IReadOnlyList<GuildMemberMainCharacter>> ListMainLinkedCharactersForUsersAsync(IReadOnlyCollection<ulong> userIds)
    {
        if (userIds.Count == 0)
        {
            return Array.Empty<GuildMemberMainCharacter>();
        }

        const string sql = @"
SELECT duc.discord_user_id AS DiscordUserId,
       c.lodestone_id AS LodestoneId,
       c.name AS Name,
       c.world AS World,
       c.data_center AS DataCenter,
       c.lodestone_url AS LodestoneUrl
FROM discord_user_characters duc
JOIN lodestone_characters c ON c.lodestone_id = duc.lodestone_id
WHERE duc.discord_user_id = ANY(@DiscordUserIds)
  AND duc.is_main = TRUE;";

        var ids = userIds.Select(ToDbId).ToArray();
        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<GuildMemberMainCharacter>(sql, new { DiscordUserIds = ids });
        return rows.ToList();
    }

    public async Task<bool> TryInsertFfLogsSeenReportAsync(ulong guildId, string reportCode, DateTimeOffset detectedAt, DateTimeOffset? reportEndAt = null)
    {
        if (string.IsNullOrWhiteSpace(reportCode))
        {
            return false;
        }

        const string sql = @"
INSERT INTO public.fflogs_seen_reports (guild_id, report_code, detected_at, report_end_at)
VALUES (@GuildId, @ReportCode, @DetectedAt, @ReportEndAt)
ON CONFLICT (guild_id, report_code) DO NOTHING;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            ReportCode = reportCode.Trim(),
            DetectedAt = detectedAt,
            ReportEndAt = reportEndAt
        });
        return affected > 0;
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

    public async Task<LodestoneCharacterExtrasCache?> GetCharacterExtrasCacheAsync(long lodestoneId)
    {
        const string sql = @"
SELECT lodestone_id AS LodestoneId,
       CAST(extras_json AS TEXT) AS ExtrasJson,
       extras_synced_at AS ExtrasSyncedAt
FROM lodestone_characters
WHERE lodestone_id = @LodestoneId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<LodestoneCharacterExtrasCache>(sql, new { LodestoneId = lodestoneId });
    }

    public async Task UpdateCharacterExtrasCacheAsync(long lodestoneId, string extrasJson, DateTimeOffset syncedAt)
    {
        const string sql = @"
UPDATE lodestone_characters
SET extras_json = CAST(@ExtrasJson AS JSONB),
    extras_synced_at = @ExtrasSyncedAt,
    updated_at = NOW()
WHERE lodestone_id = @LodestoneId;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            LodestoneId = lodestoneId,
            ExtrasJson = extrasJson,
            ExtrasSyncedAt = syncedAt
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
        bool disallowDuplicateJobs,
        string? hostFlexSpec,
        string? hostFlexJobsSpec,
        IReadOnlyList<string> positions,
        IReadOnlyDictionary<string, string>? requiredJobs = null,
        IReadOnlyDictionary<string, string>? preFilledSlots = null,
        RecruitPostStatus status = RecruitPostStatus.Open)
    {
        const string insertPostSql = @"
INSERT INTO recruit_posts (guild_id, channel_id, message_id, creator_user_id, title, data_center, schedule_text, schedule_at, note, disallow_duplicate_jobs, host_flex_spec, host_flex_jobs_spec, status)
VALUES (@GuildId, @ChannelId, @MessageId, @CreatorUserId, @Title, @DataCenter, @ScheduleText, @ScheduleAt, @Note, @DisallowDuplicateJobs, @HostFlexSpec, @HostFlexJobsSpec, @Status)
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
          underfilled_decision_deadline_at AS UnderfilledDecisionDeadlineAt,
          note AS Note,
          disallow_duplicate_jobs AS DisallowDuplicateJobs,
          host_flex_spec AS HostFlexSpec,
          host_flex_jobs_spec AS HostFlexJobsSpec,
          created_at AS CreatedAt,
          updated_at AS UpdatedAt;";

        const string insertSlotSql = @"
INSERT INTO recruit_slots (post_id, position, required_job, job, is_closed)
VALUES (@PostId, @Position, @RequiredJob, @Job, @IsClosed);";

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
            Note = NullIfBlank(note),
            DisallowDuplicateJobs = disallowDuplicateJobs,
            HostFlexSpec = NormalizeFlexSpec(hostFlexSpec),
            HostFlexJobsSpec = NormalizeFlexJobsSpec(hostFlexJobsSpec),
            Status = (short)status
        }, transaction: tx);

        foreach (var position in positions)
        {
            string? requiredJob = null;
            if (requiredJobs is not null && requiredJobs.TryGetValue(position, out var selected) && !string.IsNullOrWhiteSpace(selected))
            {
                requiredJob = selected;
            }

            string? preFilledJob = null;
            var isClosed = false;
            if (preFilledSlots is not null &&
                preFilledSlots.TryGetValue(position, out var selectedPrefilled) &&
                !string.IsNullOrWhiteSpace(selectedPrefilled))
            {
                preFilledJob = selectedPrefilled;
                isClosed = true;
            }

            await conn.ExecuteAsync(insertSlotSql, new
            {
                PostId = post.Id,
                Position = position,
                RequiredJob = NullIfBlank(requiredJob),
                Job = NullIfBlank(preFilledJob),
                IsClosed = isClosed
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
       underfilled_decision_deadline_at AS UnderfilledDecisionDeadlineAt,
       note AS Note,
       disallow_duplicate_jobs AS DisallowDuplicateJobs,
       host_flex_spec AS HostFlexSpec,
       host_flex_jobs_spec AS HostFlexJobsSpec,
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

    public async Task<IReadOnlyList<RecruitPostSummary>> ListActiveRecruitPostsByParticipantAsync(ulong guildId, ulong userId, int maxRows = 10)
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
  AND p.status IN (0, 1)
  AND EXISTS (
      SELECT 1
      FROM recruit_slots rs
      WHERE rs.post_id = p.id
        AND rs.user_id = @UserId
  )
GROUP BY p.id
ORDER BY p.schedule_at ASC NULLS LAST, p.created_at DESC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<RecruitPostSummary>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            MaxRows = Math.Clamp(maxRows, 1, 50)
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
       flex_spec AS FlexSpec,
       flex_jobs_spec AS FlexJobsSpec,
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
       flex_spec AS FlexSpec,
       flex_jobs_spec AS FlexJobsSpec,
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

    public async Task<RecruitAssignStatus> AssignRecruitSlotAsync(
        int postId,
        string position,
        ulong userId,
        string userName,
        string job,
        string comment,
        bool disallowDuplicateJobs = true,
        string? flexSpec = null,
        string? flexJobsSpec = null)
    {
        const string selectPostSql = @"
SELECT status AS Status,
       creator_user_id AS CreatorUserId,
       host_flex_spec AS HostFlexSpec,
       host_flex_jobs_spec AS HostFlexJobsSpec
FROM recruit_posts
WHERE id = @PostId
FOR UPDATE;";

        const string selectSlotsSql = @"
SELECT id AS Id,
       post_id AS PostId,
       position AS Position,
       required_job AS RequiredJob,
       flex_spec AS FlexSpec,
       flex_jobs_spec AS FlexJobsSpec,
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
    flex_spec = NULL,
    flex_jobs_spec = NULL,
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
    flex_spec = @FlexSpec,
    flex_jobs_spec = @FlexJobsSpec,
    job = @Job,
    comment = @Comment,
    is_closed = FALSE,
    joined_at = NOW(),
    updated_at = NOW()
WHERE id = @Id;";

        const string closeFlexSlotSql = @"
UPDATE recruit_slots
SET user_id = NULL,
    user_name = NULL,
    flex_spec = @FlexSpec,
    flex_jobs_spec = @FlexJobsSpec,
    job = @Job,
    comment = NULL,
    is_closed = TRUE,
    thread_message_id = NULL,
    joined_at = NOW(),
    updated_at = NOW()
WHERE id = @Id;";

        const string clearHostFlexSql = @"
UPDATE recruit_posts
SET host_flex_spec = NULL,
    host_flex_jobs_spec = NULL,
    updated_at = NOW()
WHERE id = @PostId;";

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var postInfo = await conn.QuerySingleOrDefaultAsync<RecruitPostLockInfo>(selectPostSql, new { PostId = postId }, transaction: tx);
        if (postInfo is null)
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

        var status = (RecruitPostStatus)postInfo.Status;
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

        if (target.IsClosed && target.UserId.HasValue && target.UserId.Value != userDbId)
        {
            await tx.RollbackAsync();
            return RecruitAssignStatus.PositionClosed;
        }

        if (!string.IsNullOrWhiteSpace(target.RequiredJob) &&
            !string.Equals(target.RequiredJob, job, StringComparison.OrdinalIgnoreCase))
        {
            await tx.RollbackAsync();
            return RecruitAssignStatus.JobRestricted;
        }

        var previousSlots = slots
            .Where(slot =>
                slot.UserId.HasValue &&
                slot.UserId.Value == userDbId &&
                !string.Equals(slot.Position, position, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var normalizedFlexSpec = NormalizeFlexSpec(flexSpec);
        var normalizedFlexJobsSpec = NormalizeFlexJobsSpec(flexJobsSpec);
        if (string.IsNullOrWhiteSpace(normalizedFlexSpec))
        {
            normalizedFlexJobsSpec = null;
        }
        var normalizedComment = NullIfBlank(comment);
        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!IsJobAllowedForPosition(target.Position, job, recruitPositions))
        {
            await tx.RollbackAsync();
            return RecruitAssignStatus.JobRestricted;
        }

        if (target.IsClosed && !target.UserId.HasValue)
        {
            var reopened = await TryReopenClosedFlexTargetForJoinAsync(
                conn,
                tx,
                slots,
                postInfo,
                target,
                recruitPositions,
                disallowDuplicateJobs,
                userDbId,
                clearSlotSql,
                updateSlotSql,
                closeFlexSlotSql);
            if (!reopened)
            {
                await tx.RollbackAsync();
                return RecruitAssignStatus.PositionClosed;
            }
        }

        // First, attempt a full-party rebalance for every join.
        // This avoids position-specific heuristics and handles generic FLEX patterns.
        var rebalancedFirst = await TryRebalanceFlexibleAssignmentsAsync(
            conn,
            tx,
            slots,
            postInfo,
            recruitPositions,
            userDbId,
            userName,
            target.Position,
            job,
            normalizedComment,
            normalizedFlexSpec,
            normalizedFlexJobsSpec,
            disallowDuplicateJobs,
            clearSlotSql,
            updateSlotSql);
        if (rebalancedFirst)
        {
            await NormalizeParticipantFlexSlotsAsync(conn, tx, slots, recruitPositions, postInfo.CreatorUserId, disallowDuplicateJobs);

            if (ShouldFixHostFlexToCurrentPosition(postInfo, slots, recruitPositions, disallowDuplicateJobs))
            {
                await conn.ExecuteAsync(clearHostFlexSql, new { PostId = postId }, transaction: tx);
                postInfo.HostFlexSpec = null;
                postInfo.HostFlexJobsSpec = null;
            }

            await conn.ExecuteAsync("UPDATE recruit_posts SET updated_at = NOW() WHERE id = @PostId;", new { PostId = postId }, transaction: tx);
            await tx.CommitAsync();
            return RecruitAssignStatus.Success;
        }

        if (disallowDuplicateJobs)
        {
            if (HasReservedClosedJobConflict(slots, job, target.Position))
            {
                await tx.RollbackAsync();
                return RecruitAssignStatus.JobTaken;
            }

            var duplicateSlots = slots
                .Where(slot =>
                    !string.IsNullOrWhiteSpace(slot.Job) &&
                    string.Equals(slot.Job, job, StringComparison.OrdinalIgnoreCase) &&
                    slot.UserId.HasValue &&
                    slot.UserId.Value != userDbId)
                .ToArray();

            if (duplicateSlots.Length > 0)
            {
                var resolvedByMovingFlexibleHost = false;
                if (duplicateSlots.All(slot => slot.UserId!.Value == postInfo.CreatorUserId))
                {
                    var hostCurrentSlot = duplicateSlots[0];
                    var hostFlexPositions = ParseFlexPositions(postInfo.HostFlexSpec);
                    var hostFlexJobs = ParseFlexJobsSpec(postInfo.HostFlexJobsSpec);
                    if (hostFlexJobs.Count == 0 && !string.IsNullOrWhiteSpace(hostCurrentSlot.Job))
                    {
                        hostFlexJobs = new[] { hostCurrentSlot.Job.Trim().ToUpperInvariant() };
                    }

                    if (hostFlexPositions.Count >= 2 &&
                        hostFlexPositions.Contains(hostCurrentSlot.Position, StringComparer.OrdinalIgnoreCase))
                    {
                        RecruitSlot? hostDestinationSlot = null;
                        string? hostDestinationJob = null;
                        foreach (var candidatePosition in hostFlexPositions)
                        {
                            if (string.Equals(candidatePosition, target.Position, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var candidateSlot = slots.FirstOrDefault(slot =>
                                string.Equals(slot.Position, candidatePosition, StringComparison.OrdinalIgnoreCase));
                            if (candidateSlot is null || candidateSlot.IsClosed)
                            {
                                continue;
                            }

                            if (candidateSlot.UserId.HasValue &&
                                candidateSlot.UserId.Value != postInfo.CreatorUserId)
                            {
                                continue;
                            }

                            foreach (var candidateJob in hostFlexJobs)
                            {
                                if (string.Equals(candidateJob, job, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                if (!IsJobAllowedForPosition(candidatePosition, candidateJob, recruitPositions))
                                {
                                    continue;
                                }

                                if (!string.IsNullOrWhiteSpace(candidateSlot.RequiredJob) &&
                                    !string.Equals(candidateSlot.RequiredJob, candidateJob, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                var duplicateWithOthers = slots.Any(slot =>
                                    !string.IsNullOrWhiteSpace(slot.Job) &&
                                    string.Equals(slot.Job, candidateJob, StringComparison.OrdinalIgnoreCase) &&
                                    slot.UserId.HasValue &&
                                    slot.UserId.Value != postInfo.CreatorUserId &&
                                    slot.UserId.Value != userDbId &&
                                    !string.Equals(slot.Position, hostCurrentSlot.Position, StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(slot.Position, candidatePosition, StringComparison.OrdinalIgnoreCase));
                                if (duplicateWithOthers)
                                {
                                    continue;
                                }

                                hostDestinationSlot = candidateSlot;
                                hostDestinationJob = candidateJob;
                                break;
                            }

                            if (hostDestinationSlot is not null && !string.IsNullOrWhiteSpace(hostDestinationJob))
                            {
                                break;
                            }
                        }

                        if (hostDestinationSlot is not null && !string.IsNullOrWhiteSpace(hostDestinationJob))
                        {
                            var hostName = string.IsNullOrWhiteSpace(hostCurrentSlot.UserName)
                                ? "主催"
                                : hostCurrentSlot.UserName!;
                            var hostComment = NullIfBlank(hostCurrentSlot.Comment);

                            var extraHostSlots = slots
                                .Where(slot =>
                                    slot.UserId.HasValue &&
                                    slot.UserId.Value == postInfo.CreatorUserId &&
                                    !string.Equals(slot.Position, hostCurrentSlot.Position, StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(slot.Position, hostDestinationSlot.Position, StringComparison.OrdinalIgnoreCase))
                                .ToArray();
                            foreach (var extra in extraHostSlots)
                            {
                                await conn.ExecuteAsync(clearSlotSql, new { Id = extra.Id }, transaction: tx);
                                extra.UserId = null;
                                extra.UserName = null;
                                extra.FlexSpec = null;
                                extra.FlexJobsSpec = null;
                                extra.Job = null;
                                extra.Comment = null;
                                extra.IsClosed = false;
                            }

                            if (!string.Equals(hostDestinationSlot.Position, hostCurrentSlot.Position, StringComparison.OrdinalIgnoreCase))
                            {
                                await conn.ExecuteAsync(clearSlotSql, new { Id = hostCurrentSlot.Id }, transaction: tx);
                                hostCurrentSlot.UserId = null;
                                hostCurrentSlot.UserName = null;
                                hostCurrentSlot.FlexSpec = null;
                                hostCurrentSlot.FlexJobsSpec = null;
                                hostCurrentSlot.Job = null;
                                hostCurrentSlot.Comment = null;
                                hostCurrentSlot.IsClosed = false;
                            }

                            await conn.ExecuteAsync(updateSlotSql, new
                            {
                                Id = hostDestinationSlot.Id,
                                UserId = postInfo.CreatorUserId,
                                UserName = hostName,
                                FlexSpec = (string?)null,
                                FlexJobsSpec = (string?)null,
                                Job = hostDestinationJob,
                                Comment = hostComment
                            }, transaction: tx);
                            hostDestinationSlot.UserId = postInfo.CreatorUserId;
                            hostDestinationSlot.UserName = hostName;
                            hostDestinationSlot.FlexSpec = null;
                            hostDestinationSlot.FlexJobsSpec = null;
                            hostDestinationSlot.Job = hostDestinationJob;
                            hostDestinationSlot.Comment = hostComment;
                            hostDestinationSlot.IsClosed = false;

                            resolvedByMovingFlexibleHost = true;
                        }
                    }
                }

                if (!resolvedByMovingFlexibleHost)
                {
                    var rebalanced = await TryRebalanceFlexibleAssignmentsAsync(
                        conn,
                        tx,
                        slots,
                        postInfo,
                        recruitPositions,
                        userDbId,
                        userName,
                        target.Position,
                        job,
                        normalizedComment,
                        normalizedFlexSpec,
                        normalizedFlexJobsSpec,
                        disallowDuplicateJobs,
                        clearSlotSql,
                        updateSlotSql);
                    if (rebalanced)
                    {
                        await NormalizeParticipantFlexSlotsAsync(conn, tx, slots, recruitPositions, postInfo.CreatorUserId, disallowDuplicateJobs);

                        if (ShouldFixHostFlexToCurrentPosition(postInfo, slots, recruitPositions, disallowDuplicateJobs))
                        {
                            await conn.ExecuteAsync(clearHostFlexSql, new { PostId = postId }, transaction: tx);
                            postInfo.HostFlexSpec = null;
                            postInfo.HostFlexJobsSpec = null;
                        }

                        await conn.ExecuteAsync("UPDATE recruit_posts SET updated_at = NOW() WHERE id = @PostId;", new { PostId = postId }, transaction: tx);
                        await tx.CommitAsync();
                        return RecruitAssignStatus.Success;
                    }

                    await tx.RollbackAsync();
                    return RecruitAssignStatus.JobTaken;
                }
            }
        }

        if (target.UserId.HasValue && target.UserId.Value != userDbId)
        {
            var occupyingUserId = target.UserId.Value;
            IReadOnlyList<string> occupyingFlexPositions = Array.Empty<string>();
            if (occupyingUserId == postInfo.CreatorUserId)
            {
                var hostFlexPositions = ParseFlexPositions(postInfo.HostFlexSpec);
                var occupiedByFlexibleHost = userDbId != postInfo.CreatorUserId &&
                                             hostFlexPositions.Contains(target.Position, StringComparer.OrdinalIgnoreCase);
                if (occupiedByFlexibleHost)
                {
                    occupyingFlexPositions = hostFlexPositions;
                }
            }
            else
            {
                var slotFlexPositions = ParseFlexPositions(target.FlexSpec);
                if (slotFlexPositions.Contains(target.Position, StringComparer.OrdinalIgnoreCase))
                {
                    occupyingFlexPositions = slotFlexPositions;
                }
            }

            if (occupyingFlexPositions.Count >= 2)
            {
                var occupyingJob = target.Job?.Trim().ToUpperInvariant();
                IReadOnlyList<string> occupyingCandidateJobs;
                if (occupyingUserId == postInfo.CreatorUserId)
                {
                    var hostFlexJobs = ParseFlexJobsSpec(postInfo.HostFlexJobsSpec);
                    occupyingCandidateJobs = hostFlexJobs.Count > 0
                        ? hostFlexJobs
                        : string.IsNullOrWhiteSpace(occupyingJob)
                            ? Array.Empty<string>()
                            : new[] { occupyingJob };
                }
                else
                {
                    var participantFlexJobs = ParseFlexJobsSpec(target.FlexJobsSpec);
                    if (participantFlexJobs.Count > 0)
                    {
                        occupyingCandidateJobs = participantFlexJobs;
                    }
                    else
                    {
                        occupyingCandidateJobs = string.IsNullOrWhiteSpace(occupyingJob)
                            ? Array.Empty<string>()
                            : new[] { occupyingJob };
                    }
                }

                var occupyingUserName = string.IsNullOrWhiteSpace(target.UserName)
                    ? occupyingUserId == postInfo.CreatorUserId ? "主催" : "参加者"
                    : target.UserName!;
                RecruitSlot? alternate = null;
                string? alternateJob = null;
                foreach (var candidateAlternate in slots.Where(slot =>
                             !string.Equals(slot.Position, target.Position, StringComparison.OrdinalIgnoreCase) &&
                             occupyingFlexPositions.Contains(slot.Position, StringComparer.OrdinalIgnoreCase) &&
                             !slot.IsClosed &&
                             (!slot.UserId.HasValue ||
                              slot.UserId.Value == occupyingUserId ||
                              slot.UserId.Value == userDbId)))
                {
                    foreach (var candidateJob in occupyingCandidateJobs)
                    {
                        if (string.IsNullOrWhiteSpace(candidateJob))
                        {
                            continue;
                        }

                        if (!IsJobAllowedForPosition(candidateAlternate.Position, candidateJob, recruitPositions))
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(candidateAlternate.RequiredJob) &&
                            !string.Equals(candidateAlternate.RequiredJob, candidateJob, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (disallowDuplicateJobs)
                        {
                            var duplicateWithOccupiedUsers = slots.Any(slot =>
                                !string.IsNullOrWhiteSpace(slot.Job) &&
                                string.Equals(slot.Job, candidateJob, StringComparison.OrdinalIgnoreCase) &&
                                slot.UserId.HasValue &&
                                slot.UserId.Value != occupyingUserId &&
                                slot.UserId.Value != userDbId &&
                                !string.Equals(slot.Position, target.Position, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(slot.Position, candidateAlternate.Position, StringComparison.OrdinalIgnoreCase));
                            var duplicateWithJoiningUser = userDbId != occupyingUserId &&
                                                           string.Equals(candidateJob, job, StringComparison.OrdinalIgnoreCase);
                            if (duplicateWithOccupiedUsers || duplicateWithJoiningUser)
                            {
                                continue;
                            }
                        }

                        alternate = candidateAlternate;
                        alternateJob = candidateJob;
                        break;
                    }

                    if (alternate is not null && !string.IsNullOrWhiteSpace(alternateJob))
                    {
                        break;
                    }
                }

                if (alternate is not null && !string.IsNullOrWhiteSpace(alternateJob))
                {
                    var previousTargetFlexSpec = target.FlexSpec;
                    var previousTargetFlexJobsSpec = target.FlexJobsSpec;
                    var previousTargetComment = NullIfBlank(target.Comment);
                    foreach (var previous in previousSlots)
                    {
                        await conn.ExecuteAsync(clearSlotSql, new { Id = previous.Id }, transaction: tx);
                        previous.UserId = null;
                        previous.UserName = null;
                        previous.FlexSpec = null;
                        previous.FlexJobsSpec = null;
                        previous.Job = null;
                        previous.Comment = null;
                        previous.IsClosed = false;
                    }

                    var occupyingDuplicateSlots = slots
                        .Where(slot =>
                            slot.UserId.HasValue &&
                            slot.UserId.Value == occupyingUserId &&
                            !string.Equals(slot.Position, target.Position, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(slot.Position, alternate.Position, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    foreach (var duplicate in occupyingDuplicateSlots)
                    {
                        await conn.ExecuteAsync(clearSlotSql, new { Id = duplicate.Id }, transaction: tx);
                        duplicate.UserId = null;
                        duplicate.UserName = null;
                        duplicate.FlexSpec = null;
                        duplicate.FlexJobsSpec = null;
                        duplicate.Job = null;
                        duplicate.Comment = null;
                        duplicate.IsClosed = false;
                    }

                    await conn.ExecuteAsync(updateSlotSql, new
                    {
                        Id = target.Id,
                        UserId = userDbId,
                        UserName = userName,
                        FlexSpec = normalizedFlexSpec,
                        FlexJobsSpec = normalizedFlexJobsSpec,
                        Job = job,
                        Comment = normalizedComment
                    }, transaction: tx);
                    target.UserId = userDbId;
                    target.UserName = userName;
                    target.FlexSpec = normalizedFlexSpec;
                    target.FlexJobsSpec = normalizedFlexJobsSpec;
                    target.Job = job;
                    target.Comment = normalizedComment;
                    target.IsClosed = false;

                    var occupyingFlexSpec = occupyingUserId == postInfo.CreatorUserId
                        ? null
                        : NormalizeFlexSpec(previousTargetFlexSpec);
                    var occupyingFlexJobsSpec = occupyingUserId == postInfo.CreatorUserId
                        ? null
                        : NormalizeFlexJobsSpec(previousTargetFlexJobsSpec);
                    await conn.ExecuteAsync(updateSlotSql, new
                    {
                        Id = alternate.Id,
                        UserId = occupyingUserId,
                        UserName = occupyingUserName,
                        FlexSpec = occupyingFlexSpec,
                        FlexJobsSpec = occupyingFlexJobsSpec,
                        Job = alternateJob,
                        Comment = previousTargetComment
                    }, transaction: tx);
                    alternate.UserId = occupyingUserId;
                    alternate.UserName = occupyingUserName;
                    alternate.FlexSpec = occupyingFlexSpec;
                    alternate.FlexJobsSpec = occupyingFlexJobsSpec;
                    alternate.Job = alternateJob;
                    alternate.Comment = previousTargetComment;
                    alternate.IsClosed = false;

                    if (ShouldFixHostFlexToCurrentPosition(postInfo, slots, recruitPositions, disallowDuplicateJobs))
                    {
                        await conn.ExecuteAsync(clearHostFlexSql, new { PostId = postId }, transaction: tx);
                        postInfo.HostFlexSpec = null;
                        postInfo.HostFlexJobsSpec = null;
                    }

                    await conn.ExecuteAsync("UPDATE recruit_posts SET updated_at = NOW() WHERE id = @PostId;", new { PostId = postId }, transaction: tx);
                    await tx.CommitAsync();
                    return RecruitAssignStatus.Success;
                }
            }

            var rebalancedWhenTaken = await TryRebalanceFlexibleAssignmentsAsync(
                conn,
                tx,
                slots,
                postInfo,
                recruitPositions,
                userDbId,
                userName,
                target.Position,
                job,
                normalizedComment,
                normalizedFlexSpec,
                normalizedFlexJobsSpec,
                disallowDuplicateJobs,
                clearSlotSql,
                updateSlotSql);
            if (rebalancedWhenTaken)
            {
                await NormalizeParticipantFlexSlotsAsync(conn, tx, slots, recruitPositions, postInfo.CreatorUserId, disallowDuplicateJobs);

                if (ShouldFixHostFlexToCurrentPosition(postInfo, slots, recruitPositions, disallowDuplicateJobs))
                {
                    await conn.ExecuteAsync(clearHostFlexSql, new { PostId = postId }, transaction: tx);
                    postInfo.HostFlexSpec = null;
                    postInfo.HostFlexJobsSpec = null;
                }

                await conn.ExecuteAsync("UPDATE recruit_posts SET updated_at = NOW() WHERE id = @PostId;", new { PostId = postId }, transaction: tx);
                await tx.CommitAsync();
                return RecruitAssignStatus.Success;
            }

            await tx.RollbackAsync();
            return RecruitAssignStatus.PositionTaken;
        }

        foreach (var previous in previousSlots)
        {
            await conn.ExecuteAsync(clearSlotSql, new { Id = previous.Id }, transaction: tx);
            previous.UserId = null;
            previous.UserName = null;
            previous.FlexSpec = null;
            previous.FlexJobsSpec = null;
            previous.Job = null;
            previous.Comment = null;
            previous.IsClosed = false;
        }

        await conn.ExecuteAsync(updateSlotSql, new
        {
            Id = target.Id,
            UserId = userDbId,
            UserName = userName,
            FlexSpec = normalizedFlexSpec,
            FlexJobsSpec = normalizedFlexJobsSpec,
            Job = job,
            Comment = normalizedComment
        }, transaction: tx);
        target.UserId = userDbId;
        target.UserName = userName;
        target.FlexSpec = normalizedFlexSpec;
        target.FlexJobsSpec = normalizedFlexJobsSpec;
        target.Job = job;
        target.Comment = normalizedComment;
        target.IsClosed = false;

        await NormalizeParticipantFlexSlotsAsync(conn, tx, slots, recruitPositions, postInfo.CreatorUserId, disallowDuplicateJobs);

        if (ShouldFixHostFlexToCurrentPosition(postInfo, slots, recruitPositions, disallowDuplicateJobs))
        {
            await conn.ExecuteAsync(clearHostFlexSql, new { PostId = postId }, transaction: tx);
            postInfo.HostFlexSpec = null;
            postInfo.HostFlexJobsSpec = null;
        }

        await conn.ExecuteAsync("UPDATE recruit_posts SET updated_at = NOW() WHERE id = @PostId;", new { PostId = postId }, transaction: tx);

        await tx.CommitAsync();
        return RecruitAssignStatus.Success;
    }

    public async Task<bool> MarkRecruitSlotClosedFlexAsync(
        int postId,
        string position,
        string job,
        string flexSpec,
        string? flexJobsSpec = null)
    {
        const string sql = @"
UPDATE recruit_slots
SET user_id = NULL,
    user_name = NULL,
    flex_spec = @FlexSpec,
    flex_jobs_spec = @FlexJobsSpec,
    job = @Job,
    comment = NULL,
    is_closed = TRUE,
    thread_message_id = NULL,
    joined_at = NOW(),
    updated_at = NOW()
WHERE post_id = @PostId
  AND position = @Position;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            PostId = postId,
            Position = position,
            FlexSpec = NormalizeFlexSpec(flexSpec),
            FlexJobsSpec = NormalizeFlexJobsSpec(flexJobsSpec),
            Job = NullIfBlank(job)
        });
        return affected > 0;
    }

    public async Task<bool> ClearRecruitSlotAsync(int postId, ulong userId)
    {
        const string clearSql = @"
UPDATE recruit_slots
SET user_id = NULL,
    user_name = NULL,
    flex_spec = NULL,
    flex_jobs_spec = NULL,
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

    public async Task<RecruitIngameFillStatus> MarkRecruitSlotIngameFilledAsync(int postId, string position, string job, bool disallowDuplicateJobs = true)
    {
        const string selectPostSql = @"
SELECT status AS Status,
       creator_user_id AS CreatorUserId,
       host_flex_spec AS HostFlexSpec,
       host_flex_jobs_spec AS HostFlexJobsSpec
FROM recruit_posts
WHERE id = @PostId
FOR UPDATE;";

        const string selectSlotsSql = @"
SELECT id AS Id,
       post_id AS PostId,
       position AS Position,
       required_job AS RequiredJob,
       flex_spec AS FlexSpec,
       flex_jobs_spec AS FlexJobsSpec,
       job AS Job,
       user_id AS UserId,
       user_name AS UserName,
       comment AS Comment,
       thread_message_id AS ThreadMessageId,
       joined_at AS JoinedAt,
       updated_at AS UpdatedAt,
       is_closed AS IsClosed
FROM recruit_slots
WHERE post_id = @PostId
FOR UPDATE;";

        const string clearSlotSql = @"
UPDATE recruit_slots
SET user_id = NULL,
    user_name = NULL,
    flex_spec = NULL,
    flex_jobs_spec = NULL,
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
    flex_spec = @FlexSpec,
    flex_jobs_spec = @FlexJobsSpec,
    job = @Job,
    comment = @Comment,
    is_closed = FALSE,
    joined_at = NOW(),
    updated_at = NOW()
WHERE id = @Id;";

        const string closeFlexSlotSql = @"
UPDATE recruit_slots
SET user_id = NULL,
    user_name = NULL,
    flex_spec = @FlexSpec,
    flex_jobs_spec = @FlexJobsSpec,
    job = @Job,
    comment = NULL,
    is_closed = TRUE,
    thread_message_id = NULL,
    joined_at = NOW(),
    updated_at = NOW()
WHERE id = @Id;";

        const string closeSlotSql = @"
UPDATE recruit_slots
SET is_closed = TRUE,
    flex_spec = NULL,
    flex_jobs_spec = NULL,
    job = @Job,
    comment = NULL,
    updated_at = NOW()
WHERE id = @Id;";

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var postInfo = await conn.QuerySingleOrDefaultAsync<RecruitPostLockInfo>(selectPostSql, new { PostId = postId }, transaction: tx);
        if (postInfo is null)
        {
            await tx.RollbackAsync();
            return RecruitIngameFillStatus.PostNotFound;
        }

        var status = (RecruitPostStatus)postInfo.Status;
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

        if (!string.IsNullOrWhiteSpace(target.RequiredJob) &&
            !string.Equals(target.RequiredJob, job, StringComparison.OrdinalIgnoreCase))
        {
            await tx.RollbackAsync();
            return RecruitIngameFillStatus.JobRestricted;
        }

        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (target.IsClosed)
        {
            var reopened = await TryReopenClosedFlexTargetForJoinAsync(
                conn,
                tx,
                slots,
                postInfo,
                target,
                recruitPositions,
                disallowDuplicateJobs,
                long.MinValue,
                clearSlotSql,
                updateSlotSql,
                closeFlexSlotSql);
            if (!reopened)
            {
                await tx.RollbackAsync();
                return RecruitIngameFillStatus.PositionAlreadyClosed;
            }
        }

        if (disallowDuplicateJobs)
        {
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
    flex_spec = NULL,
    flex_jobs_spec = NULL,
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
    underfilled_decision_deadline_at = NULL,
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
    underfilled_decision_deadline_at = NULL,
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

    public async Task<bool> UpdateRecruitPostCreatorAsync(int postId, ulong creatorUserId, string? hostFlexSpec = null, string? hostFlexJobsSpec = null)
    {
        const string sql = @"
UPDATE recruit_posts
SET creator_user_id = @CreatorUserId,
    host_flex_spec = @HostFlexSpec,
    host_flex_jobs_spec = @HostFlexJobsSpec,
    updated_at = NOW()
WHERE id = @PostId;";

        var normalizedHostFlexSpec = NormalizeFlexSpec(hostFlexSpec);
        var normalizedHostFlexJobsSpec = string.IsNullOrWhiteSpace(normalizedHostFlexSpec)
            ? null
            : NormalizeFlexJobsSpec(hostFlexJobsSpec);

        await using var conn = CreateConnection();
        var rows = await conn.ExecuteAsync(sql, new
        {
            PostId = postId,
            CreatorUserId = ToDbId(creatorUserId),
            HostFlexSpec = normalizedHostFlexSpec,
            HostFlexJobsSpec = normalizedHostFlexJobsSpec
        });

        return rows > 0;
    }

    public async Task<bool> UpdateRecruitPostScheduleAsync(int postId, string? scheduleText, DateTimeOffset? scheduleAt)
    {
        const string sql = @"
UPDATE recruit_posts
SET schedule_text = @ScheduleText,
    schedule_at = @ScheduleAt,
    reminder_sent_at = NULL,
    underfilled_decision_deadline_at = NULL,
    updated_at = NOW()
WHERE id = @PostId;";

        await using var conn = CreateConnection();
        var rows = await conn.ExecuteAsync(sql, new
        {
            PostId = postId,
            ScheduleText = NullIfBlank(scheduleText),
            ScheduleAt = scheduleAt
        });

        return rows > 0;
    }

    public async Task<bool> SetRecruitUnderfilledDecisionDeadlineAsync(int postId, DateTimeOffset? deadlineAt)
    {
        const string sql = @"
UPDATE recruit_posts
SET underfilled_decision_deadline_at = @DeadlineAt,
    updated_at = NOW()
WHERE id = @PostId;";

        await using var conn = CreateConnection();
        var rows = await conn.ExecuteAsync(sql, new
        {
            PostId = postId,
            DeadlineAt = deadlineAt
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
       underfilled_decision_deadline_at AS UnderfilledDecisionDeadlineAt,
       note AS Note,
       disallow_duplicate_jobs AS DisallowDuplicateJobs,
       host_flex_spec AS HostFlexSpec,
       host_flex_jobs_spec AS HostFlexJobsSpec,
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
       underfilled_decision_deadline_at AS UnderfilledDecisionDeadlineAt,
       note AS Note,
       disallow_duplicate_jobs AS DisallowDuplicateJobs,
       host_flex_spec AS HostFlexSpec,
       host_flex_jobs_spec AS HostFlexJobsSpec,
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
       inactive_auto_kick_user_data_retention_days AS InactiveAutoKickUserDataRetentionDays,
       log_channel_id AS LogChannelId,
       control_channel_id AS ControlChannelId,
       runtime_cleanup_interval_minutes AS RuntimeCleanupIntervalMinutes,
       runtime_recruit_closed_retention_hours AS RuntimeRecruitClosedRetentionHours,
       runtime_recruit_canceled_retention_hours AS RuntimeRecruitCanceledRetentionHours,
       runtime_message_log_retention_days AS RuntimeMessageLogRetentionDays,
       runtime_voice_session_retention_days AS RuntimeVoiceSessionRetentionDays,
       runtime_reaction_log_retention_days AS RuntimeReactionLogRetentionDays,
       runtime_tuning_updated_at AS RuntimeTuningUpdatedAt,
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

    public async Task<int> UpsertGuildInactiveKickUserDataRetentionDaysAsync(ulong guildId, int retentionDays)
    {
        const string sql = @"
INSERT INTO guild_settings (
    guild_id,
    admin_role_id,
    member_role_id,
    provisional_role_id,
    registration_channel_id,
    registration_review_channel_id,
    registration_approval_required,
    inactive_auto_kick_enabled,
    inactive_auto_kick_user_data_retention_days,
    log_channel_id,
    control_channel_id)
VALUES (
    @GuildId,
    0,
    0,
    0,
    0,
    0,
    TRUE,
    TRUE,
    @RetentionDays,
    0,
    0)
ON CONFLICT (guild_id) DO UPDATE
SET inactive_auto_kick_user_data_retention_days = @RetentionDays,
    updated_at = NOW()
RETURNING inactive_auto_kick_user_data_retention_days;";

        var normalized = Math.Clamp(retentionDays, 1, 30);
        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(sql, new
        {
            GuildId = ToDbId(guildId),
            RetentionDays = normalized
        });
    }

    public async Task UpsertGuildRuntimeTuningAsync(
        ulong guildId,
        int cleanupIntervalMinutes,
        int recruitClosedRetentionHours,
        int recruitCanceledRetentionHours,
        int messageLogRetentionDays,
        int voiceSessionRetentionDays,
        int reactionLogRetentionDays)
    {
        const string sql = @"
INSERT INTO guild_settings (
    guild_id,
    admin_role_id,
    member_role_id,
    provisional_role_id,
    registration_channel_id,
    registration_review_channel_id,
    registration_approval_required,
    inactive_auto_kick_enabled,
    log_channel_id,
    control_channel_id,
    runtime_cleanup_interval_minutes,
    runtime_recruit_closed_retention_hours,
    runtime_recruit_canceled_retention_hours,
    runtime_message_log_retention_days,
    runtime_voice_session_retention_days,
    runtime_reaction_log_retention_days,
    runtime_tuning_updated_at)
VALUES (
    @GuildId,
    0,
    0,
    0,
    0,
    0,
    TRUE,
    TRUE,
    0,
    0,
    @CleanupIntervalMinutes,
    @RecruitClosedRetentionHours,
    @RecruitCanceledRetentionHours,
    @MessageLogRetentionDays,
    @VoiceSessionRetentionDays,
    @ReactionLogRetentionDays,
    NOW())
ON CONFLICT (guild_id) DO UPDATE
SET runtime_cleanup_interval_minutes = EXCLUDED.runtime_cleanup_interval_minutes,
    runtime_recruit_closed_retention_hours = EXCLUDED.runtime_recruit_closed_retention_hours,
    runtime_recruit_canceled_retention_hours = EXCLUDED.runtime_recruit_canceled_retention_hours,
    runtime_message_log_retention_days = EXCLUDED.runtime_message_log_retention_days,
    runtime_voice_session_retention_days = EXCLUDED.runtime_voice_session_retention_days,
    runtime_reaction_log_retention_days = EXCLUDED.runtime_reaction_log_retention_days,
    runtime_tuning_updated_at = NOW(),
    updated_at = NOW();";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            GuildId = ToDbId(guildId),
            CleanupIntervalMinutes = cleanupIntervalMinutes,
            RecruitClosedRetentionHours = recruitClosedRetentionHours,
            RecruitCanceledRetentionHours = recruitCanceledRetentionHours,
            MessageLogRetentionDays = messageLogRetentionDays,
            VoiceSessionRetentionDays = voiceSessionRetentionDays,
            ReactionLogRetentionDays = reactionLogRetentionDays
        });
    }

    public async Task<GuildSettings?> GetLatestGuildRuntimeTuningAsync()
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
       inactive_auto_kick_user_data_retention_days AS InactiveAutoKickUserDataRetentionDays,
       log_channel_id AS LogChannelId,
       control_channel_id AS ControlChannelId,
       runtime_cleanup_interval_minutes AS RuntimeCleanupIntervalMinutes,
       runtime_recruit_closed_retention_hours AS RuntimeRecruitClosedRetentionHours,
       runtime_recruit_canceled_retention_hours AS RuntimeRecruitCanceledRetentionHours,
       runtime_message_log_retention_days AS RuntimeMessageLogRetentionDays,
       runtime_voice_session_retention_days AS RuntimeVoiceSessionRetentionDays,
       runtime_reaction_log_retention_days AS RuntimeReactionLogRetentionDays,
       runtime_tuning_updated_at AS RuntimeTuningUpdatedAt,
       updated_at AS UpdatedAt
FROM guild_settings
WHERE runtime_tuning_updated_at IS NOT NULL
ORDER BY runtime_tuning_updated_at DESC
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GuildSettings>(sql);
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

    public async Task<GuildPanel?> GetGuildDailyReactionPostPanelAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       updated_at AS UpdatedAt
FROM guild_daily_reaction_post_panels
WHERE guild_id = @GuildId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GuildPanel>(sql, new { GuildId = ToDbId(guildId) });
    }

    public async Task<GuildPanel?> GetGuildWeeklyReactionPostPanelAsync(ulong guildId)
    {
        const string sql = @"
SELECT guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       updated_at AS UpdatedAt
FROM guild_weekly_reaction_post_panels
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

    public async Task UpsertGuildDailyReactionPostPanelAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        const string sql = @"
INSERT INTO guild_daily_reaction_post_panels (guild_id, channel_id, message_id)
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

    public async Task UpsertGuildWeeklyReactionPostPanelAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        const string sql = @"
INSERT INTO guild_weekly_reaction_post_panels (guild_id, channel_id, message_id)
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

    public async Task<InquiryTicket> CreateInquiryTicketAsync(
        ulong guildId,
        ulong channelId,
        ulong requesterUserId,
        string requesterName,
        string subject,
        string status,
        DateTimeOffset createdAt)
    {
        const string sql = @"
INSERT INTO public.inquiry_tickets (
    guild_id,
    channel_id,
    requester_user_id,
    requester_name,
    subject,
    status,
    created_at,
    updated_at,
    last_activity_at,
    closed_at
)
VALUES (
    @GuildId,
    @ChannelId,
    @RequesterUserId,
    @RequesterName,
    @Subject,
    @Status,
    @CreatedAt,
    @CreatedAt,
    @CreatedAt,
    CASE WHEN @Status = 'CLOSED' THEN @CreatedAt ELSE NULL END
)
RETURNING id AS Id,
          guild_id AS GuildId,
          channel_id AS ChannelId,
          requester_user_id AS RequesterUserId,
          requester_name AS RequesterName,
          subject AS Subject,
          status AS Status,
          assigned_admin_user_id AS AssignedAdminUserId,
          assigned_admin_name AS AssignedAdminName,
          created_at AS CreatedAt,
          updated_at AS UpdatedAt,
          last_activity_at AS LastActivityAt,
          closed_at AS ClosedAt,
          last_reminder_at AS LastReminderAt,
          transcript_text AS TranscriptText;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleAsync<InquiryTicket>(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            RequesterUserId = ToDbId(requesterUserId),
            RequesterName = string.IsNullOrWhiteSpace(requesterName) ? requesterUserId.ToString() : requesterName.Trim(),
            Subject = string.IsNullOrWhiteSpace(subject) ? "未指定" : subject.Trim(),
            Status = InquiryTicketStatus.Normalize(status),
            CreatedAt = createdAt
        });
    }

    public async Task<InquiryTicket?> GetInquiryTicketByChannelAsync(ulong channelId)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       requester_user_id AS RequesterUserId,
       requester_name AS RequesterName,
       subject AS Subject,
       status AS Status,
       assigned_admin_user_id AS AssignedAdminUserId,
       assigned_admin_name AS AssignedAdminName,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       last_activity_at AS LastActivityAt,
       closed_at AS ClosedAt,
       last_reminder_at AS LastReminderAt,
       transcript_text AS TranscriptText
FROM public.inquiry_tickets
WHERE channel_id = @ChannelId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<InquiryTicket>(sql, new { ChannelId = ToDbId(channelId) });
    }

    public async Task<InquiryTicket?> GetInquiryTicketByIdAsync(int ticketId)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       requester_user_id AS RequesterUserId,
       requester_name AS RequesterName,
       subject AS Subject,
       status AS Status,
       assigned_admin_user_id AS AssignedAdminUserId,
       assigned_admin_name AS AssignedAdminName,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       last_activity_at AS LastActivityAt,
       closed_at AS ClosedAt,
       last_reminder_at AS LastReminderAt,
       transcript_text AS TranscriptText
FROM public.inquiry_tickets
WHERE id = @TicketId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<InquiryTicket>(sql, new { TicketId = ticketId });
    }

    public async Task<InquiryTicket?> GetOpenInquiryTicketByRequesterAsync(ulong guildId, ulong requesterUserId)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       requester_user_id AS RequesterUserId,
       requester_name AS RequesterName,
       subject AS Subject,
       status AS Status,
       assigned_admin_user_id AS AssignedAdminUserId,
       assigned_admin_name AS AssignedAdminName,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       last_activity_at AS LastActivityAt,
       closed_at AS ClosedAt,
       last_reminder_at AS LastReminderAt,
       transcript_text AS TranscriptText
FROM public.inquiry_tickets
WHERE guild_id = @GuildId
  AND requester_user_id = @RequesterUserId
  AND status <> 'CLOSED'
ORDER BY created_at DESC
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<InquiryTicket>(sql, new
        {
            GuildId = ToDbId(guildId),
            RequesterUserId = ToDbId(requesterUserId)
        });
    }

    public async Task<IReadOnlyList<InquiryTicket>> ListInquiryTicketsAsync(ulong guildId, bool includeClosed, int maxRows = 50)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       requester_user_id AS RequesterUserId,
       requester_name AS RequesterName,
       subject AS Subject,
       status AS Status,
       assigned_admin_user_id AS AssignedAdminUserId,
       assigned_admin_name AS AssignedAdminName,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       last_activity_at AS LastActivityAt,
       closed_at AS ClosedAt,
       last_reminder_at AS LastReminderAt,
       transcript_text AS TranscriptText
FROM public.inquiry_tickets
WHERE guild_id = @GuildId
  AND (@IncludeClosed OR status <> 'CLOSED')
ORDER BY updated_at DESC, id DESC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<InquiryTicket>(sql, new
        {
            GuildId = ToDbId(guildId),
            IncludeClosed = includeClosed,
            MaxRows = Math.Clamp(maxRows, 1, 200)
        });
        return rows.ToList();
    }

    public async Task<bool> UpdateInquiryTicketStatusAsync(int ticketId, string status, DateTimeOffset nowUtc)
    {
        const string sql = @"
UPDATE public.inquiry_tickets
SET status = @Status,
    updated_at = @NowUtc,
    last_activity_at = @NowUtc,
    last_reminder_at = NULL,
    closed_at = CASE
        WHEN @Status = 'CLOSED' THEN COALESCE(closed_at, @NowUtc)
        ELSE NULL
    END
WHERE id = @TicketId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            TicketId = ticketId,
            Status = InquiryTicketStatus.Normalize(status),
            NowUtc = nowUtc
        });
        return affected > 0;
    }

    public async Task<bool> UpdateInquiryTicketAssignmentAsync(
        int ticketId,
        ulong? assignedAdminUserId,
        string? assignedAdminName,
        DateTimeOffset nowUtc)
    {
        const string sql = @"
UPDATE public.inquiry_tickets
SET assigned_admin_user_id = @AssignedAdminUserId,
    assigned_admin_name = @AssignedAdminName,
    updated_at = @NowUtc,
    last_activity_at = @NowUtc,
    last_reminder_at = NULL
WHERE id = @TicketId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            TicketId = ticketId,
            AssignedAdminUserId = assignedAdminUserId.HasValue ? (long?)ToDbId(assignedAdminUserId.Value) : null,
            AssignedAdminName = NullIfBlank(assignedAdminName),
            NowUtc = nowUtc
        });
        return affected > 0;
    }

    public async Task<bool> TouchInquiryTicketActivityAsync(ulong channelId, DateTimeOffset activityAt)
    {
        const string sql = @"
UPDATE public.inquiry_tickets
SET last_activity_at = GREATEST(last_activity_at, @ActivityAt),
    updated_at = NOW(),
    last_reminder_at = NULL
WHERE channel_id = @ChannelId
  AND status <> 'CLOSED';";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            ChannelId = ToDbId(channelId),
            ActivityAt = activityAt
        });
        return affected > 0;
    }

    public async Task<IReadOnlyList<InquiryTicket>> ListDueInquiryReminderTicketsAsync(DateTimeOffset cutoffUtc, int maxRows = 100)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       requester_user_id AS RequesterUserId,
       requester_name AS RequesterName,
       subject AS Subject,
       status AS Status,
       assigned_admin_user_id AS AssignedAdminUserId,
       assigned_admin_name AS AssignedAdminName,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       last_activity_at AS LastActivityAt,
       closed_at AS ClosedAt,
       last_reminder_at AS LastReminderAt,
       transcript_text AS TranscriptText
FROM public.inquiry_tickets
WHERE status <> 'CLOSED'
  AND last_activity_at <= @CutoffUtc
  AND (last_reminder_at IS NULL OR last_reminder_at < last_activity_at)
ORDER BY last_activity_at ASC, id ASC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<InquiryTicket>(sql, new
        {
            CutoffUtc = cutoffUtc,
            MaxRows = Math.Clamp(maxRows, 1, 300)
        });
        return rows.ToList();
    }

    public async Task<bool> MarkInquiryReminderSentAsync(int ticketId, DateTimeOffset reminderAt)
    {
        const string sql = @"
UPDATE public.inquiry_tickets
SET last_reminder_at = @ReminderAt,
    updated_at = NOW()
WHERE id = @TicketId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            TicketId = ticketId,
            ReminderAt = reminderAt
        });
        return affected > 0;
    }

    public async Task<bool> SaveInquiryTranscriptAsync(int ticketId, string transcriptText, DateTimeOffset nowUtc)
    {
        const string sql = @"
UPDATE public.inquiry_tickets
SET transcript_text = @TranscriptText,
    updated_at = @NowUtc
WHERE id = @TicketId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            TicketId = ticketId,
            TranscriptText = NullIfBlank(transcriptText),
            NowUtc = nowUtc
        });
        return affected > 0;
    }

    public async Task<Lottery> CreateLotteryAsync(
        ulong guildId,
        ulong channelId,
        string title,
        string? description,
        string? prizeList,
        bool allowDuplicateWinners,
        int winnerCount,
        DateTimeOffset closeAt,
        ulong createdByUserId,
        string createdByName,
        DateTimeOffset createdAt)
    {
        const string sql = @"
INSERT INTO public.lotteries (
    guild_id,
    channel_id,
    title,
    description,
    prize_list,
    allow_duplicate_winners,
    winner_count,
    status,
    close_at,
    created_by_user_id,
    created_by_name,
    created_at,
    updated_at
)
VALUES (
    @GuildId,
    @ChannelId,
    @Title,
    @Description,
    @PrizeList,
    @AllowDuplicateWinners,
    @WinnerCount,
    @Status,
    @CloseAt,
    @CreatedByUserId,
    @CreatedByName,
    @CreatedAt,
    @CreatedAt
)
RETURNING id AS Id,
          guild_id AS GuildId,
          channel_id AS ChannelId,
          message_id AS MessageId,
          participant_role_id AS ParticipantRoleId,
          participant_role_delete_at AS ParticipantRoleDeleteAt,
          title AS Title,
          description AS Description,
          prize_list AS PrizeList,
          allow_duplicate_winners AS AllowDuplicateWinners,
          winner_count AS WinnerCount,
          status AS Status,
          close_at AS CloseAt,
          created_by_user_id AS CreatedByUserId,
          created_by_name AS CreatedByName,
          created_at AS CreatedAt,
          updated_at AS UpdatedAt,
          drawn_at AS DrawnAt,
          canceled_at AS CanceledAt;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleAsync<Lottery>(sql, new
        {
            GuildId = ToDbId(guildId),
            ChannelId = ToDbId(channelId),
            Title = string.IsNullOrWhiteSpace(title) ? "抽選" : title.Trim(),
            Description = NullIfBlank(description),
            PrizeList = NullIfBlank(prizeList),
            AllowDuplicateWinners = allowDuplicateWinners,
            WinnerCount = Math.Clamp(winnerCount, 1, 99),
            Status = LotteryStatus.Open,
            CloseAt = closeAt,
            CreatedByUserId = ToDbId(createdByUserId),
            CreatedByName = string.IsNullOrWhiteSpace(createdByName) ? createdByUserId.ToString() : createdByName.Trim(),
            CreatedAt = createdAt
        });
    }

    public async Task<bool> UpdateLotteryMessageIdAsync(int lotteryId, ulong messageId)
    {
        const string sql = @"
UPDATE public.lotteries
SET message_id = @MessageId,
    updated_at = NOW()
WHERE id = @LotteryId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            LotteryId = lotteryId,
            MessageId = ToDbId(messageId)
        });
        return affected > 0;
    }

    public async Task<bool> UpdateLotteryParticipantRoleIdAsync(int lotteryId, ulong? participantRoleId)
    {
        const string sql = @"
UPDATE public.lotteries
SET participant_role_id = @ParticipantRoleId,
    participant_role_delete_at = NULL,
    updated_at = NOW()
WHERE id = @LotteryId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            LotteryId = lotteryId,
            ParticipantRoleId = participantRoleId.HasValue ? (long?)ToDbId(participantRoleId.Value) : null
        });
        return affected > 0;
    }

    public async Task<bool> UpdateLotteryParticipantRoleDeleteAtAsync(int lotteryId, DateTimeOffset? deleteAtUtc)
    {
        const string sql = @"
UPDATE public.lotteries
SET participant_role_delete_at = @DeleteAtUtc,
    updated_at = NOW()
WHERE id = @LotteryId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            LotteryId = lotteryId,
            DeleteAtUtc = deleteAtUtc
        });
        return affected > 0;
    }

    public async Task<Lottery?> GetLotteryByIdAsync(int lotteryId)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       participant_role_id AS ParticipantRoleId,
       participant_role_delete_at AS ParticipantRoleDeleteAt,
       title AS Title,
       description AS Description,
       prize_list AS PrizeList,
       allow_duplicate_winners AS AllowDuplicateWinners,
       winner_count AS WinnerCount,
       status AS Status,
       close_at AS CloseAt,
       created_by_user_id AS CreatedByUserId,
       created_by_name AS CreatedByName,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       drawn_at AS DrawnAt,
       canceled_at AS CanceledAt
FROM public.lotteries
WHERE id = @LotteryId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<Lottery>(sql, new { LotteryId = lotteryId });
    }

    public async Task<Lottery?> GetLotteryByMessageIdAsync(ulong messageId)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       participant_role_id AS ParticipantRoleId,
       participant_role_delete_at AS ParticipantRoleDeleteAt,
       title AS Title,
       description AS Description,
       prize_list AS PrizeList,
       allow_duplicate_winners AS AllowDuplicateWinners,
       winner_count AS WinnerCount,
       status AS Status,
       close_at AS CloseAt,
       created_by_user_id AS CreatedByUserId,
       created_by_name AS CreatedByName,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       drawn_at AS DrawnAt,
       canceled_at AS CanceledAt
FROM public.lotteries
WHERE message_id = @MessageId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<Lottery>(sql, new
        {
            MessageId = ToDbId(messageId)
        });
    }

    public async Task<IReadOnlyList<Lottery>> ListRecentLotteriesAsync(ulong guildId, int maxRows = 10)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       participant_role_id AS ParticipantRoleId,
       participant_role_delete_at AS ParticipantRoleDeleteAt,
       title AS Title,
       description AS Description,
       prize_list AS PrizeList,
       allow_duplicate_winners AS AllowDuplicateWinners,
       winner_count AS WinnerCount,
       status AS Status,
       close_at AS CloseAt,
       created_by_user_id AS CreatedByUserId,
       created_by_name AS CreatedByName,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       drawn_at AS DrawnAt,
       canceled_at AS CanceledAt
FROM public.lotteries
WHERE guild_id = @GuildId
ORDER BY updated_at DESC, id DESC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<Lottery>(sql, new
        {
            GuildId = ToDbId(guildId),
            MaxRows = Math.Clamp(maxRows, 1, 50)
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Lottery>> ListActiveLotteriesByEntryUserAsync(ulong guildId, ulong userId, int maxRows = 10)
    {
        const string sql = @"
SELECT l.id AS Id,
       l.guild_id AS GuildId,
       l.channel_id AS ChannelId,
       l.message_id AS MessageId,
       l.participant_role_id AS ParticipantRoleId,
       l.participant_role_delete_at AS ParticipantRoleDeleteAt,
       l.title AS Title,
       l.description AS Description,
       l.prize_list AS PrizeList,
       l.allow_duplicate_winners AS AllowDuplicateWinners,
       l.winner_count AS WinnerCount,
       l.status AS Status,
       l.close_at AS CloseAt,
       l.created_by_user_id AS CreatedByUserId,
       l.created_by_name AS CreatedByName,
       l.created_at AS CreatedAt,
       l.updated_at AS UpdatedAt,
       l.drawn_at AS DrawnAt,
       l.canceled_at AS CanceledAt
FROM public.lotteries l
WHERE l.guild_id = @GuildId
  AND l.status IN (@OpenStatus, @ClosedStatus)
  AND EXISTS (
      SELECT 1
      FROM public.lottery_entries e
      WHERE e.lottery_id = l.id
        AND e.user_id = @UserId
  )
ORDER BY l.close_at ASC, l.id ASC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<Lottery>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId),
            OpenStatus = LotteryStatus.Open,
            ClosedStatus = LotteryStatus.Closed,
            MaxRows = Math.Clamp(maxRows, 1, 50)
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Lottery>> ListDueLotteriesForAutoCloseAsync(DateTimeOffset nowUtc, int maxRows = 200)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       participant_role_id AS ParticipantRoleId,
       participant_role_delete_at AS ParticipantRoleDeleteAt,
       title AS Title,
       description AS Description,
       prize_list AS PrizeList,
       allow_duplicate_winners AS AllowDuplicateWinners,
       winner_count AS WinnerCount,
       status AS Status,
       close_at AS CloseAt,
       created_by_user_id AS CreatedByUserId,
       created_by_name AS CreatedByName,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       drawn_at AS DrawnAt,
       canceled_at AS CanceledAt
FROM public.lotteries
WHERE close_at <= @NowUtc
  AND (
      status = @OpenStatus
      OR (
          status = @ClosedStatus
          AND drawn_at IS NULL
          AND EXISTS (
              SELECT 1
              FROM public.lottery_entries e
              WHERE e.lottery_id = public.lotteries.id
          )
      )
  )
ORDER BY close_at ASC, id ASC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<Lottery>(sql, new
        {
            OpenStatus = LotteryStatus.Open,
            ClosedStatus = LotteryStatus.Closed,
            NowUtc = nowUtc,
            MaxRows = Math.Clamp(maxRows, 1, 1000)
        });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Lottery>> ListDueLotteryParticipantRoleDeletesAsync(DateTimeOffset nowUtc, int maxRows = 200)
    {
        const string sql = @"
SELECT id AS Id,
       guild_id AS GuildId,
       channel_id AS ChannelId,
       message_id AS MessageId,
       participant_role_id AS ParticipantRoleId,
       participant_role_delete_at AS ParticipantRoleDeleteAt,
       title AS Title,
       description AS Description,
       prize_list AS PrizeList,
       allow_duplicate_winners AS AllowDuplicateWinners,
       winner_count AS WinnerCount,
       status AS Status,
       close_at AS CloseAt,
       created_by_user_id AS CreatedByUserId,
       created_by_name AS CreatedByName,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       drawn_at AS DrawnAt,
       canceled_at AS CanceledAt
FROM public.lotteries
WHERE participant_role_id IS NOT NULL
  AND participant_role_delete_at IS NOT NULL
  AND participant_role_delete_at <= @NowUtc
ORDER BY participant_role_delete_at ASC, id ASC
LIMIT @MaxRows;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<Lottery>(sql, new
        {
            NowUtc = nowUtc,
            MaxRows = Math.Clamp(maxRows, 1, 1000)
        });
        return rows.ToList();
    }

    public async Task<LotteryClaimChannel?> GetLotteryClaimChannelAsync(int lotteryId, ulong userId)
    {
        const string sql = @"
SELECT id AS Id,
       lottery_id AS LotteryId,
       user_id AS UserId,
       channel_id AS ChannelId,
       control_message_id AS ControlMessageId,
       is_closed AS IsClosed,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       closed_at AS ClosedAt
FROM public.lottery_claim_channels
WHERE lottery_id = @LotteryId
  AND user_id = @UserId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<LotteryClaimChannel>(sql, new
        {
            LotteryId = lotteryId,
            UserId = ToDbId(userId)
        });
    }

    public async Task<LotteryClaimChannel?> GetLotteryClaimChannelByChannelIdAsync(ulong channelId)
    {
        const string sql = @"
SELECT id AS Id,
       lottery_id AS LotteryId,
       user_id AS UserId,
       channel_id AS ChannelId,
       control_message_id AS ControlMessageId,
       is_closed AS IsClosed,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       closed_at AS ClosedAt
FROM public.lottery_claim_channels
WHERE channel_id = @ChannelId
LIMIT 1;";

        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<LotteryClaimChannel>(sql, new
        {
            ChannelId = ToDbId(channelId)
        });
    }

    public async Task UpsertLotteryClaimChannelIdAsync(int lotteryId, ulong userId, ulong channelId, DateTimeOffset nowUtc)
    {
        const string sql = @"
INSERT INTO public.lottery_claim_channels (
    lottery_id,
    user_id,
    channel_id,
    control_message_id,
    is_closed,
    created_at,
    updated_at,
    closed_at
)
VALUES (
    @LotteryId,
    @UserId,
    @ChannelId,
    NULL,
    FALSE,
    @NowUtc,
    @NowUtc,
    NULL
)
ON CONFLICT (lottery_id, user_id) DO UPDATE
SET channel_id = EXCLUDED.channel_id,
    control_message_id = NULL,
    is_closed = FALSE,
    closed_at = NULL,
    updated_at = EXCLUDED.updated_at;";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            LotteryId = lotteryId,
            UserId = ToDbId(userId),
            ChannelId = ToDbId(channelId),
            NowUtc = nowUtc
        });
    }

    public async Task<bool> UpdateLotteryClaimChannelControlMessageIdAsync(int claimChannelId, ulong? messageId)
    {
        const string sql = @"
UPDATE public.lottery_claim_channels
SET control_message_id = @ControlMessageId,
    updated_at = NOW()
WHERE id = @ClaimChannelId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            ClaimChannelId = claimChannelId,
            ControlMessageId = messageId.HasValue ? (long?)ToDbId(messageId.Value) : null
        });
        return affected > 0;
    }

    public async Task<bool> UpdateLotteryClaimChannelClosedStateAsync(int claimChannelId, bool isClosed, DateTimeOffset nowUtc)
    {
        const string sql = @"
UPDATE public.lottery_claim_channels
SET is_closed = @IsClosed,
    closed_at = CASE WHEN @IsClosed THEN @NowUtc ELSE NULL END,
    updated_at = @NowUtc
WHERE id = @ClaimChannelId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            ClaimChannelId = claimChannelId,
            IsClosed = isClosed,
            NowUtc = nowUtc
        });
        return affected > 0;
    }

    public async Task<bool> DeleteLotteryClaimChannelAsync(int claimChannelId)
    {
        const string sql = @"
DELETE FROM public.lottery_claim_channels
WHERE id = @ClaimChannelId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new { ClaimChannelId = claimChannelId });
        return affected > 0;
    }

    public async Task<bool> AddLotteryEntryAsync(int lotteryId, ulong userId, string userName, DateTimeOffset joinedAt)
    {
        const string sql = @"
INSERT INTO public.lottery_entries (
    lottery_id,
    user_id,
    user_name,
    joined_at,
    updated_at
)
VALUES (
    @LotteryId,
    @UserId,
    @UserName,
    @JoinedAt,
    @JoinedAt
)
ON CONFLICT (lottery_id, user_id) DO NOTHING;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            LotteryId = lotteryId,
            UserId = ToDbId(userId),
            UserName = string.IsNullOrWhiteSpace(userName) ? userId.ToString() : userName.Trim(),
            JoinedAt = joinedAt
        });
        return affected > 0;
    }

    public async Task<bool> RemoveLotteryEntryAsync(int lotteryId, ulong userId)
    {
        const string sql = @"
DELETE FROM public.lottery_entries
WHERE lottery_id = @LotteryId
  AND user_id = @UserId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            LotteryId = lotteryId,
            UserId = ToDbId(userId)
        });
        return affected > 0;
    }

    public async Task<IReadOnlyList<LotteryEntry>> ListLotteryEntriesAsync(int lotteryId)
    {
        const string sql = @"
SELECT id AS Id,
       lottery_id AS LotteryId,
       user_id AS UserId,
       user_name AS UserName,
       joined_at AS JoinedAt,
       updated_at AS UpdatedAt
FROM public.lottery_entries
WHERE lottery_id = @LotteryId
ORDER BY joined_at ASC, id ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<LotteryEntry>(sql, new { LotteryId = lotteryId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<LotteryWinner>> ListLotteryWinnersAsync(int lotteryId)
    {
        const string sql = @"
SELECT id AS Id,
       lottery_id AS LotteryId,
       user_id AS UserId,
       user_name AS UserName,
       winner_order AS WinnerOrder,
       drawn_by_user_id AS DrawnByUserId,
       drawn_at AS DrawnAt
FROM public.lottery_winners
WHERE lottery_id = @LotteryId
ORDER BY winner_order ASC, id ASC;";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<LotteryWinner>(sql, new { LotteryId = lotteryId });
        return rows.ToList();
    }

    public async Task<int> CountLotteryWinsForGuildUserAsync(ulong guildId, ulong userId)
    {
        const string sql = @"
SELECT COUNT(1)
FROM public.lottery_winners w
INNER JOIN public.lotteries l
    ON l.id = w.lottery_id
WHERE l.guild_id = @GuildId
  AND w.user_id = @UserId;";

        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(sql, new
        {
            GuildId = ToDbId(guildId),
            UserId = ToDbId(userId)
        });
    }

    public async Task ReplaceLotteryWinnersAsync(
        int lotteryId,
        IReadOnlyList<LotteryEntry> winners,
        ulong drawnByUserId,
        DateTimeOffset drawnAt)
    {
        const string clearSql = @"
DELETE FROM public.lottery_winners
WHERE lottery_id = @LotteryId;";

        const string insertSql = @"
INSERT INTO public.lottery_winners (
    lottery_id,
    user_id,
    user_name,
    winner_order,
    drawn_by_user_id,
    drawn_at
)
VALUES (
    @LotteryId,
    @UserId,
    @UserName,
    @WinnerOrder,
    @DrawnByUserId,
    @DrawnAt
);";

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(clearSql, new { LotteryId = lotteryId }, tx);

        var order = 1;
        foreach (var winner in winners)
        {
            await conn.ExecuteAsync(insertSql, new
            {
                LotteryId = lotteryId,
                UserId = winner.UserId,
                UserName = winner.UserName,
                WinnerOrder = order++,
                DrawnByUserId = ToDbId(drawnByUserId),
                DrawnAt = drawnAt
            }, tx);
        }

        await tx.CommitAsync();
    }

    public async Task<bool> UpdateLotteryStatusAsync(int lotteryId, string status, DateTimeOffset nowUtc)
    {
        const string sql = @"
UPDATE public.lotteries
SET status = @Status,
    updated_at = @NowUtc,
    drawn_at = CASE
        WHEN @Status = 'DRAWN' THEN @NowUtc
        ELSE drawn_at
    END,
    canceled_at = CASE
        WHEN @Status = 'CANCELED' THEN @NowUtc
        ELSE canceled_at
    END
WHERE id = @LotteryId;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            LotteryId = lotteryId,
            Status = LotteryStatus.Normalize(status),
            NowUtc = nowUtc
        });
        return affected > 0;
    }

    public async Task<bool> TryCloseLotteryIfExpiredAsync(int lotteryId, DateTimeOffset nowUtc)
    {
        const string sql = @"
UPDATE public.lotteries
SET status = @ClosedStatus,
    updated_at = @NowUtc
WHERE id = @LotteryId
  AND status = @OpenStatus
  AND close_at <= @NowUtc;";

        await using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(sql, new
        {
            LotteryId = lotteryId,
            ClosedStatus = LotteryStatus.Closed,
            OpenStatus = LotteryStatus.Open,
            NowUtc = nowUtc
        });
        return affected > 0;
    }

    private static async Task<bool> TryReopenClosedFlexTargetForJoinAsync(
        NpgsqlConnection conn,
        System.Data.Common.DbTransaction tx,
        IReadOnlyList<RecruitSlot> slots,
        RecruitPostLockInfo postInfo,
        RecruitSlot closedTarget,
        IReadOnlyCollection<string> recruitPositions,
        bool disallowDuplicateJobs,
        long joiningUserId,
        string clearSlotSql,
        string updateSlotSql,
        string closeFlexSlotSql,
        ISet<string>? visitingClosedPositions = null)
    {
        if (!closedTarget.IsClosed || closedTarget.UserId.HasValue || string.IsNullOrWhiteSpace(closedTarget.FlexSpec))
        {
            return false;
        }

        var normalizedClosedPosition = closedTarget.Position?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedClosedPosition))
        {
            return false;
        }

        visitingClosedPositions ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!visitingClosedPositions.Add(normalizedClosedPosition))
        {
            return false;
        }

        try
        {

            var flexPositions = ParseFlexPositions(closedTarget.FlexSpec)
                .Select(position => position.Trim().ToUpperInvariant())
                .Where(position => !string.IsNullOrWhiteSpace(position))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (flexPositions.Length < 2 ||
                !flexPositions.Contains(closedTarget.Position, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var flexJobs = ParseFlexJobsSpec(closedTarget.FlexJobsSpec);
            var currentJob = closedTarget.Job?.Trim().ToUpperInvariant();
            if (flexJobs.Count == 0 && !string.IsNullOrWhiteSpace(currentJob))
            {
                flexJobs = new[] { currentJob };
            }
            else if (!string.IsNullOrWhiteSpace(currentJob) &&
                     !flexJobs.Contains(currentJob, StringComparer.OrdinalIgnoreCase))
            {
                flexJobs = flexJobs
                    .Append(currentJob)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            if (flexJobs.Count == 0)
            {
                return false;
            }

            HashSet<string> BuildBlockedJobs()
                => disallowDuplicateJobs
                    ? slots
                        .Where(slot =>
                            !string.IsNullOrWhiteSpace(slot.Job) &&
                            !string.Equals(slot.Position, closedTarget.Position, StringComparison.OrdinalIgnoreCase) &&
                            ((slot.UserId.HasValue && slot.UserId.Value != joiningUserId) ||
                             (slot.IsClosed && !slot.UserId.HasValue && ShouldReserveClosedSlotJobForDuplicateCheck(slot))))
                        .Select(slot => slot.Job!.Trim().ToUpperInvariant())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidatePosition in flexPositions)
            {
                if (string.Equals(candidatePosition, closedTarget.Position, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidateSlot = slots.FirstOrDefault(slot =>
                    string.Equals(slot.Position, candidatePosition, StringComparison.OrdinalIgnoreCase));
                if (candidateSlot is null)
                {
                    continue;
                }

                if (candidateSlot.IsClosed)
                {
                    var reopenedCandidate = await TryReopenClosedFlexTargetForJoinAsync(
                        conn,
                        tx,
                        slots,
                        postInfo,
                        candidateSlot,
                        recruitPositions,
                        disallowDuplicateJobs,
                        joiningUserId,
                        clearSlotSql,
                        updateSlotSql,
                        closeFlexSlotSql,
                        visitingClosedPositions);
                    if (!reopenedCandidate)
                    {
                        continue;
                    }
                }

                if (candidateSlot.UserId.HasValue)
                {
                    var relocated = await TryRelocateFlexibleOccupantForClosedFlexAsync(
                        conn,
                        tx,
                        slots,
                        postInfo,
                        candidateSlot,
                        recruitPositions,
                        disallowDuplicateJobs,
                        clearSlotSql,
                        updateSlotSql);
                    if (!relocated)
                    {
                        continue;
                    }
                }

                if (candidateSlot.IsClosed || candidateSlot.UserId.HasValue)
                {
                    continue;
                }

                var blockedJobs = BuildBlockedJobs();
                var candidateJob = flexJobs.FirstOrDefault(job =>
                    IsJobAllowedForPosition(candidatePosition, job, recruitPositions) &&
                    (string.IsNullOrWhiteSpace(candidateSlot.RequiredJob) ||
                     string.Equals(candidateSlot.RequiredJob, job, StringComparison.OrdinalIgnoreCase)) &&
                    (!disallowDuplicateJobs || !blockedJobs.Contains(job)));
                if (string.IsNullOrWhiteSpace(candidateJob))
                {
                    continue;
                }

                var normalizedFlexSpec = NormalizeFlexSpec(closedTarget.FlexSpec);
                var normalizedFlexJobsSpec = NormalizeFlexJobsSpec(closedTarget.FlexJobsSpec);
                await conn.ExecuteAsync(clearSlotSql, new { Id = closedTarget.Id }, transaction: tx);
                closedTarget.UserId = null;
                closedTarget.UserName = null;
                closedTarget.FlexSpec = null;
                closedTarget.FlexJobsSpec = null;
                closedTarget.Job = null;
                closedTarget.Comment = null;
                closedTarget.IsClosed = false;

                await conn.ExecuteAsync(closeFlexSlotSql, new
                {
                    Id = candidateSlot.Id,
                    FlexSpec = normalizedFlexSpec,
                    FlexJobsSpec = normalizedFlexJobsSpec,
                    Job = candidateJob
                }, transaction: tx);
                candidateSlot.UserId = null;
                candidateSlot.UserName = null;
                candidateSlot.FlexSpec = normalizedFlexSpec;
                candidateSlot.FlexJobsSpec = normalizedFlexJobsSpec;
                candidateSlot.Job = candidateJob;
                candidateSlot.Comment = null;
                candidateSlot.IsClosed = true;
                return true;
            }

            return false;
        }
        finally
        {
            visitingClosedPositions.Remove(normalizedClosedPosition);
        }
    }

    private static async Task<bool> TryRelocateFlexibleOccupantForClosedFlexAsync(
        NpgsqlConnection conn,
        System.Data.Common.DbTransaction tx,
        IReadOnlyList<RecruitSlot> slots,
        RecruitPostLockInfo postInfo,
        RecruitSlot occupiedSlot,
        IReadOnlyCollection<string> recruitPositions,
        bool disallowDuplicateJobs,
        string clearSlotSql,
        string updateSlotSql)
    {
        if (!occupiedSlot.UserId.HasValue)
        {
            return true;
        }

        var occupyingUserId = occupiedSlot.UserId.Value;
        var occupyingUserName = string.IsNullOrWhiteSpace(occupiedSlot.UserName)
            ? occupyingUserId == postInfo.CreatorUserId ? "主催" : "参加者"
            : occupiedSlot.UserName!;
        var occupyingComment = NullIfBlank(occupiedSlot.Comment);

        IReadOnlyList<string> occupyingFlexPositions;
        IReadOnlyList<string> occupyingFlexJobs;
        if (occupyingUserId == postInfo.CreatorUserId)
        {
            occupyingFlexPositions = ParseFlexPositions(postInfo.HostFlexSpec);
            occupyingFlexJobs = ParseFlexJobsSpec(postInfo.HostFlexJobsSpec);
        }
        else
        {
            occupyingFlexPositions = ParseFlexPositions(occupiedSlot.FlexSpec);
            occupyingFlexJobs = ParseFlexJobsSpec(occupiedSlot.FlexJobsSpec);
        }

        if (occupyingFlexPositions.Count < 2 ||
            !occupyingFlexPositions.Contains(occupiedSlot.Position, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var currentOccupyingJob = occupiedSlot.Job?.Trim().ToUpperInvariant();
        if (occupyingFlexJobs.Count == 0 && !string.IsNullOrWhiteSpace(currentOccupyingJob))
        {
            occupyingFlexJobs = new[] { currentOccupyingJob };
        }
        else if (!string.IsNullOrWhiteSpace(currentOccupyingJob) &&
                 !occupyingFlexJobs.Contains(currentOccupyingJob, StringComparer.OrdinalIgnoreCase))
        {
            occupyingFlexJobs = occupyingFlexJobs
                .Append(currentOccupyingJob)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (occupyingFlexJobs.Count == 0)
        {
            return false;
        }

        var orderedOccupyingJobs = occupyingFlexJobs
            .OrderBy(job => string.Equals(job, currentOccupyingJob, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(job => job, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var alternate in slots.Where(slot =>
                     !string.Equals(slot.Position, occupiedSlot.Position, StringComparison.OrdinalIgnoreCase) &&
                     occupyingFlexPositions.Contains(slot.Position, StringComparer.OrdinalIgnoreCase) &&
                     !slot.IsClosed &&
                     (!slot.UserId.HasValue || slot.UserId.Value == occupyingUserId)))
        {
            foreach (var candidateJob in orderedOccupyingJobs)
            {
                if (!IsJobAllowedForPosition(alternate.Position, candidateJob, recruitPositions))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(alternate.RequiredJob) &&
                    !string.Equals(alternate.RequiredJob, candidateJob, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (disallowDuplicateJobs)
                {
                    var duplicateWithOthers = slots.Any(slot =>
                        !string.IsNullOrWhiteSpace(slot.Job) &&
                        string.Equals(slot.Job, candidateJob, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(slot.Position, occupiedSlot.Position, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(slot.Position, alternate.Position, StringComparison.OrdinalIgnoreCase) &&
                        ((slot.UserId.HasValue && slot.UserId.Value != occupyingUserId) ||
                         (slot.IsClosed && !slot.UserId.HasValue && ShouldReserveClosedSlotJobForDuplicateCheck(slot))));
                    if (duplicateWithOthers)
                    {
                        continue;
                    }
                }

                var storedFlexSpec = occupyingUserId == postInfo.CreatorUserId ? null : NormalizeFlexSpec(occupiedSlot.FlexSpec);
                var storedFlexJobsSpec = occupyingUserId == postInfo.CreatorUserId ? null : NormalizeFlexJobsSpec(occupiedSlot.FlexJobsSpec);
                if (string.IsNullOrWhiteSpace(storedFlexSpec))
                {
                    storedFlexJobsSpec = null;
                }

                await conn.ExecuteAsync(clearSlotSql, new { Id = occupiedSlot.Id }, transaction: tx);
                occupiedSlot.UserId = null;
                occupiedSlot.UserName = null;
                occupiedSlot.FlexSpec = null;
                occupiedSlot.FlexJobsSpec = null;
                occupiedSlot.Job = null;
                occupiedSlot.Comment = null;
                occupiedSlot.IsClosed = false;

                await conn.ExecuteAsync(updateSlotSql, new
                {
                    Id = alternate.Id,
                    UserId = occupyingUserId,
                    UserName = occupyingUserName,
                    FlexSpec = storedFlexSpec,
                    FlexJobsSpec = storedFlexJobsSpec,
                    Job = candidateJob,
                    Comment = occupyingComment
                }, transaction: tx);
                alternate.UserId = occupyingUserId;
                alternate.UserName = occupyingUserName;
                alternate.FlexSpec = storedFlexSpec;
                alternate.FlexJobsSpec = storedFlexJobsSpec;
                alternate.Job = candidateJob;
                alternate.Comment = occupyingComment;
                alternate.IsClosed = false;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ParseFlexPositions(string? flexSpec)
    {
        if (string.IsNullOrWhiteSpace(flexSpec))
        {
            return Array.Empty<string>();
        }

        var normalized = flexSpec.Trim();
        if (normalized.StartsWith("FLEX:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[5..].Trim();
        }
        else if (normalized.StartsWith("FLEX(", StringComparison.OrdinalIgnoreCase) &&
                 normalized.EndsWith(")", StringComparison.Ordinal))
        {
            normalized = normalized[5..^1].Trim();
        }
        else if (string.Equals(normalized, "FLEX", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "ANY";
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Array.Empty<string>();
        }

        return normalized
            .Split(new[] { ',', '/', '|', '，', '／', '｜', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(position => position.Trim().ToUpperInvariant())
            .Where(position => !string.IsNullOrWhiteSpace(position))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizeFlexSpec(string? flexSpec)
    {
        var positions = ParseFlexPositions(flexSpec);
        if (positions.Count == 0)
        {
            return null;
        }

        return string.Join(',', positions);
    }

    private static IReadOnlyList<string> ParseFlexJobsSpec(string? jobsSpec)
    {
        if (string.IsNullOrWhiteSpace(jobsSpec))
        {
            return Array.Empty<string>();
        }

        return jobsSpec
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(job => job.Trim().ToUpperInvariant())
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizeFlexJobsSpec(string? jobsSpec)
    {
        var jobs = ParseFlexJobsSpec(jobsSpec);
        if (jobs.Count == 0)
        {
            return null;
        }

        return string.Join(',', jobs.OrderBy(job => job, StringComparer.OrdinalIgnoreCase));
    }

    private static bool ShouldReserveFlexibleJobForDuplicateCheck(string? flexSpec, IReadOnlyList<string> candidateJobs)
    {
        if (candidateJobs.Count <= 1)
        {
            return true;
        }

        var positions = ParseFlexPositions(flexSpec);
        return positions.Count < 2;
    }

    private static bool ShouldReserveClosedSlotJobForDuplicateCheck(RecruitSlot slot)
    {
        if (string.IsNullOrWhiteSpace(slot.Job))
        {
            return false;
        }

        var candidateJobs = ParseFlexJobsSpec(slot.FlexJobsSpec);
        if (candidateJobs.Count == 0)
        {
            candidateJobs = new[] { slot.Job.Trim().ToUpperInvariant() };
        }

        return ShouldReserveFlexibleJobForDuplicateCheck(slot.FlexSpec, candidateJobs);
    }

    private static bool HasReservedClosedJobConflict(
        IReadOnlyCollection<RecruitSlot> slots,
        string job,
        string targetPosition)
    {
        if (string.IsNullOrWhiteSpace(job))
        {
            return false;
        }

        var normalizedJob = job.Trim().ToUpperInvariant();
        return slots.Any(slot =>
            slot.IsClosed &&
            !slot.UserId.HasValue &&
            !string.IsNullOrWhiteSpace(slot.Job) &&
            string.Equals(slot.Job.Trim(), normalizedJob, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(slot.Position, targetPosition, StringComparison.OrdinalIgnoreCase) &&
            ShouldReserveClosedSlotJobForDuplicateCheck(slot));
    }

    private static bool IsJobAllowedForPosition(string position, string job, IReadOnlyCollection<string> recruitPositions)
    {
        if (!RecruitmentRules.TryGetJobsForRecruit(position, recruitPositions, out var jobs))
        {
            return false;
        }

        return jobs.Any(option => string.Equals(option.Code, job, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldFixHostFlexToCurrentPosition(
        RecruitPostLockInfo postInfo,
        IReadOnlyCollection<RecruitSlot> slots,
        IReadOnlyCollection<string> recruitPositions,
        bool disallowDuplicateJobs)
    {
        var hostFlexPositions = ParseFlexPositions(postInfo.HostFlexSpec);
        if (hostFlexPositions.Count < 2)
        {
            return true;
        }

        var hostSlot = slots.FirstOrDefault(slot =>
            slot.UserId.HasValue &&
            slot.UserId.Value == postInfo.CreatorUserId &&
            hostFlexPositions.Contains(slot.Position, StringComparer.OrdinalIgnoreCase));
        if (hostSlot is null)
        {
            return false;
        }

        if (!hostFlexPositions.Contains(hostSlot.Position, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var hostJobs = ParseFlexJobsSpec(postInfo.HostFlexJobsSpec);
        if (hostJobs.Count == 0 && !string.IsNullOrWhiteSpace(hostSlot.Job))
        {
            hostJobs = new[] { hostSlot.Job.Trim().ToUpperInvariant() };
        }
        if (hostJobs.Count == 0)
        {
            return true;
        }

        // 複数ジョブFLEXは、他メンバーの一時的なジョブ重複状態でも将来的に再配置可能なため維持する。
        if (hostJobs.Count >= 2)
        {
            return false;
        }

        // 実運用で割り当て可能な候補が1枠以下なら固定化する。
        var assignableCount = CountAssignableFlexPositions(
            slots,
            recruitPositions,
            hostFlexPositions,
            hostJobs,
            postInfo.CreatorUserId,
            disallowDuplicateJobs);
        return assignableCount < 2;
    }

    private static async Task NormalizeParticipantFlexSlotsAsync(
        NpgsqlConnection conn,
        System.Data.Common.DbTransaction tx,
        IReadOnlyList<RecruitSlot> slots,
        IReadOnlyCollection<string> recruitPositions,
        long creatorUserId,
        bool disallowDuplicateJobs)
    {
        const string clearParticipantFlexSql = @"
UPDATE recruit_slots
SET flex_spec = NULL,
    flex_jobs_spec = NULL,
    updated_at = NOW()
WHERE id = @Id;";

        foreach (var slot in slots)
        {
            if (!slot.UserId.HasValue ||
                slot.UserId.Value == creatorUserId ||
                string.IsNullOrWhiteSpace(slot.FlexSpec))
            {
                continue;
            }

            if (!ShouldFixParticipantFlexToCurrentPosition(slot, slots, recruitPositions, disallowDuplicateJobs))
            {
                continue;
            }

            await conn.ExecuteAsync(clearParticipantFlexSql, new { Id = slot.Id }, transaction: tx);
            slot.FlexSpec = null;
            slot.FlexJobsSpec = null;
        }
    }

    private static bool ShouldFixParticipantFlexToCurrentPosition(
        RecruitSlot participantSlot,
        IReadOnlyCollection<RecruitSlot> slots,
        IReadOnlyCollection<string> recruitPositions,
        bool disallowDuplicateJobs)
    {
        if (!participantSlot.UserId.HasValue)
        {
            return false;
        }

        var flexPositions = ParseFlexPositions(participantSlot.FlexSpec);
        if (flexPositions.Count < 2)
        {
            return true;
        }

        if (!flexPositions.Contains(participantSlot.Position, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var participantJobs = ParseFlexJobsSpec(participantSlot.FlexJobsSpec);
        if (participantJobs.Count == 0 && !string.IsNullOrWhiteSpace(participantSlot.Job))
        {
            participantJobs = new[] { participantSlot.Job.Trim().ToUpperInvariant() };
        }
        if (participantJobs.Count == 0)
        {
            return true;
        }

        // 複数ジョブFLEXは、他メンバーの一時的なジョブ重複状態でも将来的に再配置可能なため維持する。
        if (participantJobs.Count >= 2)
        {
            return false;
        }

        // 実運用で割り当て可能な候補が1枠以下なら固定化する。
        var assignableCount = CountAssignableFlexPositions(
            slots,
            recruitPositions,
            flexPositions,
            participantJobs,
            participantSlot.UserId.Value,
            disallowDuplicateJobs);
        return assignableCount < 2;
    }

    private static int CountAssignableFlexPositions(
        IReadOnlyCollection<RecruitSlot> slots,
        IReadOnlyCollection<string> recruitPositions,
        IReadOnlyCollection<string> flexPositions,
        IReadOnlyCollection<string> candidateJobs,
        long userId,
        bool disallowDuplicateJobs)
    {
        if (flexPositions.Count == 0 || candidateJobs.Count == 0)
        {
            return 0;
        }

        var normalizedJobs = candidateJobs
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Select(job => job.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedJobs.Length == 0)
        {
            return 0;
        }

        var blockedJobs = disallowDuplicateJobs
            ? slots
                .Where(slot =>
                    !string.IsNullOrWhiteSpace(slot.Job) &&
                    ((slot.UserId.HasValue && slot.UserId.Value != userId) ||
                     (slot.IsClosed && !slot.UserId.HasValue && ShouldReserveClosedSlotJobForDuplicateCheck(slot))))
                .Select(slot => slot.Job!.Trim().ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var count = 0;
        foreach (var candidatePosition in flexPositions
                     .Where(position => !string.IsNullOrWhiteSpace(position))
                     .Select(position => position.Trim().ToUpperInvariant())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = slots.FirstOrDefault(slot =>
                string.Equals(slot.Position, candidatePosition, StringComparison.OrdinalIgnoreCase));
            if (target is null || target.IsClosed)
            {
                continue;
            }

            if (target.UserId.HasValue && target.UserId.Value != userId)
            {
                continue;
            }

            var canAssign = normalizedJobs.Any(job =>
                (!disallowDuplicateJobs || !blockedJobs.Contains(job)) &&
                IsJobAllowedForPosition(candidatePosition, job, recruitPositions) &&
                (string.IsNullOrWhiteSpace(target.RequiredJob) ||
                 string.Equals(target.RequiredJob, job, StringComparison.OrdinalIgnoreCase)));
            if (canAssign)
            {
                count++;
            }
        }

        return count;
    }

    private static async Task<bool> TryRebalanceFlexibleAssignmentsAsync(
        NpgsqlConnection conn,
        System.Data.Common.DbTransaction tx,
        IReadOnlyList<RecruitSlot> slots,
        RecruitPostLockInfo postInfo,
        IReadOnlyCollection<string> recruitPositions,
        long incomingUserId,
        string incomingUserName,
        string incomingPosition,
        string incomingJob,
        string? incomingComment,
        string? incomingFlexSpec,
        string? incomingFlexJobsSpec,
        bool disallowDuplicateJobs,
        string clearSlotSql,
        string updateSlotSql)
    {
        var normalizedIncomingPosition = incomingPosition.Trim().ToUpperInvariant();
        var normalizedIncomingJob = incomingJob.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedIncomingPosition) || string.IsNullOrWhiteSpace(normalizedIncomingJob))
        {
            return false;
        }

        var slotByPosition = slots
            .GroupBy(slot => slot.Position, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (!slotByPosition.TryGetValue(normalizedIncomingPosition, out var incomingTarget) ||
            incomingTarget.IsClosed)
        {
            return false;
        }

        var actorsByUser = new Dictionary<long, RebalanceActor>();
        foreach (var group in slots
                     .Where(slot => slot.UserId.HasValue)
                     .GroupBy(slot => slot.UserId!.Value))
        {
            var actor = BuildRebalanceActorForExistingUserSlots(group.ToArray(), postInfo, slotByPosition);
            if (actor is null)
            {
                return false;
            }

            actorsByUser[actor.UserId] = actor;
        }

        var incomingIsHost = incomingUserId == postInfo.CreatorUserId;
        var storedIncomingFlexSpec = incomingIsHost ? null : NormalizeFlexSpec(incomingFlexSpec);
        var storedIncomingFlexJobsSpec = incomingIsHost ? null : NormalizeFlexJobsSpec(incomingFlexJobsSpec);
        if (string.IsNullOrWhiteSpace(storedIncomingFlexSpec))
        {
            storedIncomingFlexJobsSpec = null;
        }

        var incomingRawPositions = ParseFlexPositions(incomingFlexSpec);
        var incomingUseFlexPositions = incomingRawPositions.Count >= 2 &&
                                       incomingRawPositions.Contains(normalizedIncomingPosition, StringComparer.OrdinalIgnoreCase);
        var incomingAllowedPositions = (incomingUseFlexPositions
                ? incomingRawPositions
                : new[] { normalizedIncomingPosition })
            .Where(position =>
                slotByPosition.TryGetValue(position, out var candidate) &&
                !candidate.IsClosed)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (incomingAllowedPositions.Length == 0)
        {
            incomingAllowedPositions = new[] { normalizedIncomingPosition };
        }

        var incomingRawJobs = ParseFlexJobsSpec(incomingFlexJobsSpec);
        var incomingAllowedJobs = incomingRawJobs
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Select(job => job.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (incomingAllowedJobs.Count == 0)
        {
            incomingAllowedJobs.Add(normalizedIncomingJob);
        }
        else if (!incomingAllowedJobs.Contains(normalizedIncomingJob, StringComparer.OrdinalIgnoreCase))
        {
            incomingAllowedJobs.Add(normalizedIncomingJob);
        }

        var incomingActor = new RebalanceActor(
            UserId: incomingUserId,
            UserName: incomingUserName,
            Comment: incomingComment,
            IsHost: incomingIsHost,
            StoredFlexSpec: storedIncomingFlexSpec,
            StoredFlexJobsSpec: storedIncomingFlexJobsSpec,
            AllowedPositions: incomingAllowedPositions,
            AllowedJobs: incomingAllowedJobs.ToArray(),
            PreferredPosition: normalizedIncomingPosition,
            PreferredJob: normalizedIncomingJob);
        actorsByUser[incomingUserId] = incomingActor;

        var actors = actorsByUser.Values.ToArray();
        if (actors.Length == 0)
        {
            return false;
        }

        if (actors.Any(actor => actor.AllowedPositions.Count == 0 || actor.AllowedJobs.Count == 0))
        {
            return false;
        }

        var blockedJobs = disallowDuplicateJobs
            ? slots
                .Where(slot =>
                    slot.IsClosed &&
                    !slot.UserId.HasValue &&
                    ShouldReserveClosedSlotJobForDuplicateCheck(slot) &&
                    !string.IsNullOrWhiteSpace(slot.Job))
                .Select(slot => slot.Job!.Trim().ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var orderedActors = actors
            .OrderBy(actor => actor.UserId == incomingUserId ? 0 : 1)
            .ThenBy(actor => actor.AllowedPositions.Count * Math.Max(actor.AllowedJobs.Count, 1))
            .ThenBy(actor => actor.AllowedPositions.Count)
            .ThenBy(actor => actor.UserId)
            .ToArray();
        var usedPositions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignments = new Dictionary<long, RebalanceAssignment>();

        bool Search(int index)
        {
            if (index >= orderedActors.Length)
            {
                return true;
            }

            var actor = orderedActors[index];
            var candidatePositions = actor.AllowedPositions
                .OrderBy(position => string.Equals(position, actor.PreferredPosition, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(position => position, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var candidatePosition in candidatePositions)
            {
                if (usedPositions.Contains(candidatePosition))
                {
                    continue;
                }

                if (!slotByPosition.TryGetValue(candidatePosition, out var slot))
                {
                    continue;
                }

                if (slot.IsClosed && (!slot.UserId.HasValue || slot.UserId.Value != actor.UserId))
                {
                    continue;
                }

                var candidateJobs = actor.AllowedJobs
                    .OrderBy(job => string.Equals(job, actor.PreferredJob, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(job => job, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var candidateJob in candidateJobs)
                {
                    if (string.IsNullOrWhiteSpace(candidateJob))
                    {
                        continue;
                    }

                    if (!IsJobAllowedForPosition(candidatePosition, candidateJob, recruitPositions))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(slot.RequiredJob) &&
                        !string.Equals(slot.RequiredJob, candidateJob, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (disallowDuplicateJobs &&
                        (blockedJobs.Contains(candidateJob) || usedJobs.Contains(candidateJob)))
                    {
                        continue;
                    }

                    usedPositions.Add(candidatePosition);
                    if (disallowDuplicateJobs)
                    {
                        usedJobs.Add(candidateJob);
                    }

                    assignments[actor.UserId] = new RebalanceAssignment(candidatePosition, candidateJob);
                    if (Search(index + 1))
                    {
                        return true;
                    }

                    assignments.Remove(actor.UserId);
                    usedPositions.Remove(candidatePosition);
                    if (disallowDuplicateJobs)
                    {
                        usedJobs.Remove(candidateJob);
                    }
                }
            }

            return false;
        }

        if (!Search(0))
        {
            return false;
        }

        if (!assignments.TryGetValue(incomingUserId, out var incomingAssignment) ||
            !string.Equals(incomingAssignment.Position, normalizedIncomingPosition, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var actorUserIds = actorsByUser.Keys.ToHashSet();
        foreach (var occupied in slots.Where(slot => slot.UserId.HasValue && actorUserIds.Contains(slot.UserId.Value)).ToArray())
        {
            await conn.ExecuteAsync(clearSlotSql, new { Id = occupied.Id }, transaction: tx);
            occupied.UserId = null;
            occupied.UserName = null;
            occupied.FlexSpec = null;
            occupied.FlexJobsSpec = null;
            occupied.Job = null;
            occupied.Comment = null;
            occupied.IsClosed = false;
        }

        foreach (var pair in assignments)
        {
            if (!actorsByUser.TryGetValue(pair.Key, out var actor))
            {
                continue;
            }

            var assigned = pair.Value;
            if (!slotByPosition.TryGetValue(assigned.Position, out var destination))
            {
                return false;
            }

            await conn.ExecuteAsync(updateSlotSql, new
            {
                Id = destination.Id,
                UserId = actor.UserId,
                UserName = actor.UserName,
                FlexSpec = actor.StoredFlexSpec,
                FlexJobsSpec = actor.StoredFlexJobsSpec,
                Job = assigned.Job,
                Comment = actor.Comment
            }, transaction: tx);

            destination.UserId = actor.UserId;
            destination.UserName = actor.UserName;
            destination.FlexSpec = actor.StoredFlexSpec;
            destination.FlexJobsSpec = actor.StoredFlexJobsSpec;
            destination.Job = assigned.Job;
            destination.Comment = actor.Comment;
            destination.IsClosed = false;
        }

        return true;
    }

    private static RebalanceActor? BuildRebalanceActorForExistingSlot(
        RecruitSlot slot,
        RecruitPostLockInfo postInfo,
        IReadOnlyDictionary<string, RecruitSlot> slotByPosition)
    {
        if (!slot.UserId.HasValue)
        {
            return null;
        }

        var userId = slot.UserId.Value;
        var currentPosition = slot.Position.Trim().ToUpperInvariant();
        var currentJob = slot.Job?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(currentPosition) || string.IsNullOrWhiteSpace(currentJob))
        {
            return null;
        }

        var isHost = userId == postInfo.CreatorUserId;
        var rawPositions = isHost ? ParseFlexPositions(postInfo.HostFlexSpec) : ParseFlexPositions(slot.FlexSpec);
        var useFlex = rawPositions.Count >= 2 && rawPositions.Contains(currentPosition, StringComparer.OrdinalIgnoreCase);
        var allowedPositions = (useFlex ? rawPositions : new[] { currentPosition })
            .Where(position =>
                slotByPosition.TryGetValue(position, out var candidate) &&
                !candidate.IsClosed)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allowedPositions.Length == 0)
        {
            if (!slotByPosition.TryGetValue(currentPosition, out var currentSlot) || currentSlot.IsClosed)
            {
                return null;
            }

            allowedPositions = new[] { currentPosition };
        }

        var rawJobs = isHost ? ParseFlexJobsSpec(postInfo.HostFlexJobsSpec) : ParseFlexJobsSpec(slot.FlexJobsSpec);
        var allowedJobs = (rawJobs.Count > 0 ? rawJobs : new[] { currentJob })
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Select(job => job.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allowedJobs.Length == 0)
        {
            return null;
        }

        var storedFlexSpec = isHost ? null : NormalizeFlexSpec(slot.FlexSpec);
        var storedFlexJobsSpec = isHost ? null : NormalizeFlexJobsSpec(slot.FlexJobsSpec);
        if (string.IsNullOrWhiteSpace(storedFlexSpec))
        {
            storedFlexJobsSpec = null;
        }

        return new RebalanceActor(
            UserId: userId,
            UserName: string.IsNullOrWhiteSpace(slot.UserName) ? (isHost ? "主催" : "参加者") : slot.UserName!,
            Comment: NullIfBlank(slot.Comment),
            IsHost: isHost,
            StoredFlexSpec: storedFlexSpec,
            StoredFlexJobsSpec: storedFlexJobsSpec,
            AllowedPositions: allowedPositions,
            AllowedJobs: allowedJobs,
            PreferredPosition: currentPosition,
            PreferredJob: currentJob);
    }

    private static RebalanceActor? BuildRebalanceActorForExistingUserSlots(
        IReadOnlyList<RecruitSlot> userSlots,
        RecruitPostLockInfo postInfo,
        IReadOnlyDictionary<string, RecruitSlot> slotByPosition)
    {
        if (userSlots.Count == 0)
        {
            return null;
        }

        RebalanceActor? bestActor = null;
        var bestScore = int.MinValue;
        var bestUpdatedAt = DateTimeOffset.MinValue;
        var bestSlotId = int.MinValue;

        foreach (var slot in userSlots)
        {
            var candidate = BuildRebalanceActorForExistingSlot(slot, postInfo, slotByPosition);
            if (candidate is null)
            {
                continue;
            }

            var score = ScoreRebalancePrimarySlot(slot, postInfo);
            var isBetter = score > bestScore ||
                           (score == bestScore && slot.UpdatedAt > bestUpdatedAt) ||
                           (score == bestScore && slot.UpdatedAt == bestUpdatedAt && slot.Id > bestSlotId);
            if (!isBetter)
            {
                continue;
            }

            bestActor = candidate;
            bestScore = score;
            bestUpdatedAt = slot.UpdatedAt;
            bestSlotId = slot.Id;
        }

        return bestActor;
    }

    private static int ScoreRebalancePrimarySlot(RecruitSlot slot, RecruitPostLockInfo postInfo)
    {
        var currentPosition = slot.Position?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(currentPosition))
        {
            return int.MinValue;
        }

        var isHost = slot.UserId.HasValue && slot.UserId.Value == postInfo.CreatorUserId;
        var rawPositions = isHost
            ? ParseFlexPositions(postInfo.HostFlexSpec)
            : ParseFlexPositions(slot.FlexSpec);
        var hasFlexiblePositions = rawPositions.Count >= 2;
        var includesCurrentPosition = hasFlexiblePositions &&
                                      rawPositions.Contains(currentPosition, StringComparer.OrdinalIgnoreCase);

        var score = 0;
        if (includesCurrentPosition)
        {
            score += 200;
        }
        else if (hasFlexiblePositions)
        {
            score -= 200;
        }

        if (!slot.IsClosed)
        {
            score += 20;
        }

        if (!string.IsNullOrWhiteSpace(slot.Job))
        {
            score += 10;
        }

        if (!string.IsNullOrWhiteSpace(slot.UserName))
        {
            score += 5;
        }

        return score;
    }

    private sealed record RebalanceActor(
        long UserId,
        string UserName,
        string? Comment,
        bool IsHost,
        string? StoredFlexSpec,
        string? StoredFlexJobsSpec,
        IReadOnlyList<string> AllowedPositions,
        IReadOnlyList<string> AllowedJobs,
        string PreferredPosition,
        string PreferredJob);

    private sealed record RebalanceAssignment(string Position, string Job);

    private static ulong ToUlong(long id) => unchecked((ulong)id);
    private static long ToDbId(ulong id) => unchecked((long)id);

    private static string? NullIfBlank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

public sealed class RecruitPostLockInfo
{
    public short Status { get; set; }
    public long CreatorUserId { get; set; }
    public string? HostFlexSpec { get; set; }
    public string? HostFlexJobsSpec { get; set; }
}

public sealed record RecruitPositionUpdateResult(bool Success, IReadOnlyList<string> BlockedPositions);
