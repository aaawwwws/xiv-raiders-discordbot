using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace RaidDcBot.Bot;

[Group("voice", "VC統計")]
public sealed class VoiceStatsModule : InteractionModuleBase<SocketInteractionContext>
{
    [RequireBotAdmin]
    [SlashCommand("weekly_rank", "（廃止）週間ランキングは /week で投稿先を設定してください")]
    public async Task WeeklyRankAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await RespondAsync("週間ランキングは `/week` で送信先を設定してください。表示は週次リセット時に自動投稿されます。", ephemeral: true);
    }
}
