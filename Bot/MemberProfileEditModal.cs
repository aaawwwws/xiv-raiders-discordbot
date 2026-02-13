using Discord;
using Discord.Interactions;

namespace RaidDcBot.Bot;

public sealed class MemberProfileEditModal : IModal
{
    public string Title => "プロフィール編集";

    [InputLabel("FFXIV名")]
    [ModalTextInput("member_profile_ffxiv_name", TextInputStyle.Short, placeholder: "未入力なら変更しない", maxLength: 40)]
    [RequiredInput(false)]
    public string FfxivName { get; set; } = string.Empty;

    [InputLabel("一言")]
    [ModalTextInput("member_profile_bio", TextInputStyle.Paragraph, placeholder: "未入力なら変更しない", maxLength: 200)]
    [RequiredInput(false)]
    public string Bio { get; set; } = string.Empty;

    [InputLabel("X (任意)")]
    [ModalTextInput("member_profile_x", TextInputStyle.Short, placeholder: "@example または example", maxLength: 50)]
    [RequiredInput(false)]
    public string XHandle { get; set; } = string.Empty;

    [InputLabel("Bluesky (任意)")]
    [ModalTextInput("member_profile_bluesky", TextInputStyle.Short, placeholder: "example または example.bsky.social", maxLength: 80)]
    [RequiredInput(false)]
    public string BlueskyHandle { get; set; } = string.Empty;
}
