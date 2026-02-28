using Discord;

namespace RaidDcBot.Bot;

public static class SsAlbumPanel
{
    public const string PanelTitle = "SSアルバムパネル";
    public const string OpenMyAlbumCustomId = "ss_album_open_me";
    public const string PickUserCustomIdPrefix = "ss_album_pick_user:";
    public const string PageCustomIdPrefix = "ss_album_page:";
    public const string PageSelectCustomIdPrefix = "ss_album_page_select:";
    public const string ImageSelectCustomIdPrefix = "ss_album_image_select:";
    public const string FilterOpenCustomIdPrefix = "ss_album_filter_open:";
    public const string FilterClearCustomIdPrefix = "ss_album_filter_clear:";
    public const string ToggleFavoritesOnlyCustomIdPrefix = "ss_album_favs_only:";
    public const string ToggleFavoriteCustomIdPrefix = "ss_album_fav_toggle:";

    public static string BuildPanelMessage(ulong? sourceChannelId = null)
    {
        var channelText = sourceChannelId.HasValue && sourceChannelId.Value != 0
            ? MentionUtils.MentionChannel(sourceChannelId.Value)
            : "#ss";
        return $"{PanelTitle}\n※ {channelText} の投稿画像は予告なく削除される可能性があります。";
    }

    public static MessageComponent BuildComponents()
    {
        var userSelect = new SelectMenuBuilder
        {
            CustomId = $"{PickUserCustomIdPrefix}0",
            Type = ComponentType.UserSelect,
            Placeholder = "他のメンバーのSSアルバムを見る",
            MinValues = 1,
            MaxValues = 1,
            IsDisabled = false
        };

        return new ComponentBuilder()
            .WithButton("自分のアルバムを見る", OpenMyAlbumCustomId, ButtonStyle.Success, row: 0)
            .WithSelectMenu(userSelect, row: 1)
            .Build();
    }

    public static bool LooksLikePanelMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return content.StartsWith(PanelTitle, StringComparison.Ordinal);
    }
}
