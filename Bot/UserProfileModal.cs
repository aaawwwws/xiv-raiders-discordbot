using Discord;
using Discord.Interactions;

namespace RaidDcBot.Bot;

public sealed class UserProfileModal : IModal
{
    public string Title => "メンバー登録";

    [InputLabel("FFXIV名")]
    [ModalTextInput("profile_ffxiv_name", TextInputStyle.Short, placeholder: "Y'shtola Rhul", maxLength: 40)]
    public string FfxivName { get; set; } = string.Empty;

    [InputLabel("一言")]
    [ModalTextInput("profile_bio", TextInputStyle.Paragraph, placeholder: "よろしくお願いします！", maxLength: 200)]
    public string Bio { get; set; } = string.Empty;

    [InputLabel("X (任意)")]
    [ModalTextInput("profile_x", TextInputStyle.Short, placeholder: "@example または example", maxLength: 50)]
    [RequiredInput(false)]
    public string XHandle { get; set; } = string.Empty;

    [InputLabel("Bluesky (任意)")]
    [ModalTextInput("profile_bluesky", TextInputStyle.Short, placeholder: "example または example.bsky.social", maxLength: 80)]
    [RequiredInput(false)]
    public string BlueskyHandle { get; set; } = string.Empty;
}
