using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RaidDcBot.Models;

namespace RaidDcBot.Bot;

public sealed class RequireBotAdminAttribute : PreconditionAttribute
{
    public override Task<PreconditionResult> CheckRequirementsAsync(
        Discord.IInteractionContext context,
        ICommandInfo commandInfo,
        IServiceProvider services)
    {
        if (context.Guild is null || context.User is not SocketGuildUser guildUser)
        {
            return Task.FromResult(PreconditionResult.FromError("サーバー内で実行してください。"));
        }

        if (guildUser.GuildPermissions.Administrator)
        {
            return Task.FromResult(PreconditionResult.FromSuccess());
        }

        var options = services.GetRequiredService<IOptions<DiscordOptions>>().Value;
        var roleName = string.IsNullOrWhiteSpace(options.AdminRoleName) ? "RaidDC Admin" : options.AdminRoleName;

        var hasRole = guildUser.Roles.Any(role =>
            string.Equals(role.Name, roleName, StringComparison.OrdinalIgnoreCase) ||
            role.Name.Contains("[コンテンツ]", StringComparison.OrdinalIgnoreCase));

        return hasRole
            ? Task.FromResult(PreconditionResult.FromSuccess())
            : Task.FromResult(PreconditionResult.FromError($"{roleName} ロールが必要です。"));
    }
}
