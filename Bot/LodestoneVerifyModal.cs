using Discord;
using Discord.Interactions;

namespace RaidDcBot.Bot;

public sealed class LodestoneVerifyModal : IModal
{
    public string Title => "Lodestone確認";

    [InputLabel("LodestoneプロフィールURL")]
    [ModalTextInput("lodestone_url_verify", TextInputStyle.Short, placeholder: "https://jp.finalfantasyxiv.com/lodestone/character/XXXX/", maxLength: 200)]
    public string Url { get; set; } = string.Empty;
}
