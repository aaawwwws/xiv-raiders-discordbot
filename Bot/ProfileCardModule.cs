using System.Collections.Concurrent;
using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

public sealed class ProfileCardModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly UserProfileEmbedService _profileEmbed;
    private readonly ProfileImageService _profileImage;
    private readonly ProfileCacheService _cache;
    private readonly Db _db;

    private const int BgStateTtlMinutes = 10;
    private static readonly ConcurrentDictionary<ulong, BgPaginationState> BgStates = new();

    public ProfileCardModule(UserProfileEmbedService profileEmbed, ProfileImageService profileImage, ProfileCacheService cache, Db db)
    {
        _profileEmbed = profileEmbed;
        _profileImage = profileImage;
        _cache = cache;
        _db = db;
    }

    private static void PruneBgStates()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in BgStates)
        {
            if (pair.Value.ExpiresAt <= now)
                BgStates.TryRemove(pair.Key, out _);
        }
    }

    private static void SetBgState(ulong userId, BgPaginationState state) => BgStates[userId] = state;

    private static bool TryGetBgState(ulong userId, out BgPaginationState? state)
    {
        PruneBgStates();
        if (BgStates.TryGetValue(userId, out state) && state is not null && state.ExpiresAt > DateTimeOffset.UtcNow)
            return true;
        state = null;
        return false;
    }

    [SlashCommand("profile", "強化プロフィールカードを表示します")]
    public async Task ShowProfileCardAsync(
        [Summary("user", "表示するユーザー（省略で自分）")] IUser? target = null)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var user = target ?? Context.User;
        if (user is not SocketGuildUser guildUser)
        {
            await RespondAsync("サーバーメンバーのみ表示できます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: false);

        try
        {
            var imageStream = await _profileImage.BuildProfileImageAsync(guildUser, forceUpdate: false);
            await FollowupWithFileAsync(imageStream, $"profile_{guildUser.Id}.png", text: $"🎴 {guildUser.DisplayName} の強化プロフィールカード");
        }
        catch (Exception ex)
        {
            await FollowupAsync($"プロフィールカードの生成に失敗しました: {ex.Message}", ephemeral: true);
        }
    }

    [SlashCommand("profile-update", "Lodestoneデータを強制更新してプロフィールカードを表示します（5分クールダウン）")]
    public async Task ForceUpdateProfileAsync(
        [Summary("user", "表示するユーザー（省略で自分）")] IUser? target = null)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var user = target ?? Context.User;
        if (user is not SocketGuildUser guildUser)
        {
            await RespondAsync("サーバーメンバーのみ表示できます。", ephemeral: true);
            return;
        }

        // クールダウンチェック
        if (!_cache.CanForceUpdate(guildUser.Id, out var remaining))
        {
            await RespondAsync($"更新はクールダウン中です。あと `{remaining.Minutes:D2}:{remaining.Seconds:D2}` 後に再度お試しください。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: false);

        try
        {
            _cache.Invalidate(guildUser.Id);
            var imageStream = await _profileImage.BuildProfileImageAsync(guildUser, forceUpdate: true);
            _cache.MarkForceUpdated(guildUser.Id);
            await FollowupWithFileAsync(imageStream, $"profile_{guildUser.Id}.png", text: $"🔄 {guildUser.DisplayName} のプロフィールカード（強制更新）");
        }
        catch (Exception ex)
        {
            await FollowupAsync($"プロフィールカードの生成に失敗しました: {ex.Message}", ephemeral: true);
        }
    }

    [SlashCommand("profile-bg", "プロフィールカードの背景をSSアルバムから選びます")]
    public async Task SelectBackgroundAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var allImages = await _db.ListUserSsImagesAsync(Context.Guild.Id, Context.User.Id, limit: 50);
        var favs = await _db.ListSsAlbumFavoritesAsync(Context.Guild.Id, Context.User.Id, maxRows: 50);

        if (allImages.Count == 0 && favs.Count == 0)
        {
            await RespondAsync("SSアルバムに画像が見つかりません。先に `/ssalbum` でアルバムを確認するか、画像を投稿してください。", ephemeral: true);
            return;
        }

        var images = new List<BgImageItem>();
        var existingUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // お気に入りを先頭に
        foreach (var f in favs)
        {
            if (existingUrls.Contains(f.ImageUrl)) continue;
            images.Add(new BgImageItem(f.ImageUrl, $"⭐ {f.FileName}", f.PostedAt));
            existingUrls.Add(f.ImageUrl);
        }

        // 全画像を追加（重複除外）
        foreach (var img in allImages)
        {
            if (existingUrls.Contains(img.ImageUrl)) continue;
            images.Add(new BgImageItem(img.ImageUrl, img.FileName, img.CreatedAt));
            existingUrls.Add(img.ImageUrl);
        }

        if (images.Count == 0)
        {
            await RespondAsync("表示できる画像が見つかりません。", ephemeral: true);
            return;
        }

        var state = new BgPaginationState
        {
            UserId = Context.User.Id,
            Images = images,
            CurrentIndex = 0,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(BgStateTtlMinutes)
        };
        SetBgState(Context.User.Id, state);

        var embed = BuildBgEmbed(state);
        var buttons = BuildBgButtons(state.CurrentIndex > 0, state.CurrentIndex < state.Images.Count - 1);

        await RespondAsync(embed: embed, components: buttons, ephemeral: true);
    }

    [ComponentInteraction("profile_bg_prev")]
    public async Task HandleBgPrevAsync()
    {
        await UpdateBgPaginationAsync(-1);
    }

    [ComponentInteraction("profile_bg_next")]
    public async Task HandleBgNextAsync()
    {
        await UpdateBgPaginationAsync(1);
    }

    [ComponentInteraction("profile_bg_pick")]
    public async Task HandleBgPickAsync()
    {
        await DeferAsync();
        PruneBgStates();
        if (!TryGetBgState(Context.User.Id, out var state) || state is null)
        {
            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Content = "セッションが期限切れです。もう一度 `/profile-bg` を実行してください。";
                msg.Embeds = null;
                msg.Components = new ComponentBuilder().Build();
            });
            return;
        }

        var img = state.Images[state.CurrentIndex];
        await _db.UpdateProfileBgImageUrlAsync(Context.User.Id, img.ImageUrl);

        await ModifyOriginalResponseAsync(msg =>
        {
            msg.Content = $"✅ 背景を「{img.DisplayName}」に設定しました！ `/profile` で確認できます。";
            msg.Embeds = null;
            msg.Components = new ComponentBuilder().Build();
        });
    }

    [ComponentInteraction("profile_bg_reset")]
    public async Task HandleBgResetAsync()
    {
        await DeferAsync();
        await _db.UpdateProfileBgImageUrlAsync(Context.User.Id, null);
        await ModifyOriginalResponseAsync(msg =>
        {
            msg.Content = "🗑️ 背景をデフォルトにリセットしました。";
            msg.Embeds = null;
            msg.Components = new ComponentBuilder().Build();
        });
        BgStates.TryRemove(Context.User.Id, out _);
    }

    [ComponentInteraction("profile_bg_cancel")]
    public async Task HandleBgCancelAsync()
    {
        await DeferAsync();
        await ModifyOriginalResponseAsync(msg =>
        {
            msg.Content = "❌ 背景選択をキャンセルしました。";
            msg.Embeds = null;
            msg.Components = new ComponentBuilder().Build();
        });
        BgStates.TryRemove(Context.User.Id, out _);
    }

    private static Embed BuildBgEmbed(BgPaginationState state)
    {
        var img = state.Images[state.CurrentIndex];
        var desc = new StringBuilder();
        desc.AppendLine($"**{img.DisplayName ?? "image"}**");
        desc.AppendLine($"投稿日: {img.PostedAt:yyyy/MM/dd}");
        desc.AppendLine();
        desc.AppendLine($"{state.CurrentIndex + 1} / {state.Images.Count}");

        return new EmbedBuilder()
            .WithTitle("プロフィール背景を選んでください")
            .WithDescription(desc.ToString())
            .WithImageUrl(img.ImageUrl)
            .WithColor(new Color(110, 140, 250))
            .WithFooter("◀ ▶ で画像を切り替え、「選ぶ」ボタンで確定してください")
            .Build();
    }

    private static MessageComponent BuildBgButtons(bool hasPrev, bool hasNext)
    {
        var builder = new ComponentBuilder();
        builder.WithButton("◀", "profile_bg_prev", ButtonStyle.Secondary, disabled: !hasPrev, row: 0);
        builder.WithButton("▶", "profile_bg_next", ButtonStyle.Secondary, disabled: !hasNext, row: 0);
        builder.WithButton("✅ 選ぶ", "profile_bg_pick", ButtonStyle.Success, row: 1);
        builder.WithButton("🗑️ リセット", "profile_bg_reset", ButtonStyle.Danger, row: 1);
        builder.WithButton("❌ キャンセル", "profile_bg_cancel", ButtonStyle.Secondary, row: 1);
        return builder.Build();
    }

    private async Task UpdateBgPaginationAsync(int delta)
    {
        await DeferAsync();
        PruneBgStates();
        if (!TryGetBgState(Context.User.Id, out var state) || state is null)
        {
            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Content = "セッションが期限切れです。もう一度 `/profile-bg` を実行してください。";
                msg.Embeds = null;
                msg.Components = new ComponentBuilder().Build();
            });
            return;
        }

        var next = state.CurrentIndex + delta;
        if (next < 0 || next >= state.Images.Count) return;
        state.CurrentIndex = next;

        var embed = BuildBgEmbed(state);
        var buttons = BuildBgButtons(state.CurrentIndex > 0, state.CurrentIndex < state.Images.Count - 1);

        await ModifyOriginalResponseAsync(msg =>
        {
            msg.Content = null;
            msg.Embeds = new[] { embed };
            msg.Components = buttons;
        });
    }

    private sealed class BgPaginationState
    {
        public required ulong UserId { get; init; }
        public required IReadOnlyList<BgImageItem> Images { get; init; }
        public int CurrentIndex { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed record BgImageItem(string ImageUrl, string? DisplayName, DateTimeOffset PostedAt);
}
