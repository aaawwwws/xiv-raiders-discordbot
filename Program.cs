using System.Net;
using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RaidDcBot.Bot;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

Console.OutputEncoding = Encoding.UTF8;

EnvLoader.Load();

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables();

builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection("Discord"));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<LodestoneOptions>(builder.Configuration.GetSection("Lodestone"));
builder.Services.Configure<FfLogsOptions>(builder.Configuration.GetSection("FFLogs"));

builder.Services.AddSingleton(sp => {
    var lodestone = sp.GetRequiredService<IConfiguration>().GetSection("Lodestone").Get<LodestoneOptions>() ?? new LodestoneOptions();
    var handler = new SocketsHttpHandler {
        AutomaticDecompression = DecompressionMethods.All
    };

    var client = new HttpClient(handler) {
        Timeout = TimeSpan.FromSeconds(lodestone.TimeoutSeconds > 0 ? lodestone.TimeoutSeconds : 15)
    };

    var userAgent = string.IsNullOrWhiteSpace(lodestone.UserAgent)
        ? LodestoneOptions.DefaultUserAgent
        : lodestone.UserAgent;

    client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,ja;q=0.8");
    client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

    return client;
});

builder.Services.AddSingleton<BotNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BotNotificationService>());

builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<LodestoneClient>();
builder.Services.AddSingleton<FfLogsClient>();
builder.Services.AddSingleton<LodestoneRateLimitService>();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<VoiceTransferTracker>();
builder.Services.AddSingleton<VoiceChannelTextService>();
builder.Services.AddSingleton<UserProfileEmbedService>();
builder.Services.AddSingleton<JobIconCacheService>();
builder.Services.AddSingleton<ProfileCacheService>();
builder.Services.AddSingleton<ProfileImageService>();
builder.Services.AddSingleton<AchievementResumeService>();
builder.Services.AddSingleton<GuildAuditLogService>();

builder.Services.AddSingleton<FfLogsReportWatcherService>();
builder.Services.AddSingleton<RuntimeDiagnosticsService>();
builder.Services.AddSingleton<RuntimeTuningService>();
builder.Services.AddSingleton<RecruitUndoService>();
builder.Services.AddSingleton<StatsEmbedService>();
builder.Services.AddSingleton<VoiceStatsService>();
builder.Services.AddSingleton<VcHourlyChartService>();
builder.Services.AddSingleton<VoiceSessionReconciliationService>();
builder.Services.AddSingleton<SleepKickService>();
builder.Services.AddSingleton<RecruitmentListService>();
builder.Services.AddSingleton<RecruitJoinContextTracker>();
builder.Services.AddSingleton<RecruitCommentEditContextTracker>();
builder.Services.AddSingleton<RecruitCreateDraftContextTracker>();
builder.Services.AddSingleton<RecruitPostEditContextTracker>();
builder.Services.AddSingleton<MemberRegistrationDraftContextTracker>();
builder.Services.AddSingleton<LotteryCreateDraftContextTracker>();

var discordOptions = builder.Configuration.GetSection("Discord").Get<DiscordOptions>() ?? new DiscordOptions();

builder.Services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig {
    GatewayIntents = GatewayIntents.Guilds
        | GatewayIntents.GuildMembers
        | GatewayIntents.GuildMessages
        | GatewayIntents.GuildMessageReactions
        | GatewayIntents.MessageContent
        | GatewayIntents.GuildVoiceStates,
    LogLevel = LogSeverity.Info,
    MessageCacheSize = discordOptions.MessageCacheSize,
    EnableVoiceDaveEncryption = true
}));

builder.Services.AddSingleton(sp => new InteractionService(
    sp.GetRequiredService<DiscordSocketClient>(),
    new InteractionServiceConfig {
        LogLevel = LogSeverity.Info,
        UseCompiledLambda = true,
        DefaultRunMode = Discord.Interactions.RunMode.Async
    }));

builder.Services.AddHostedService<DiscordBotService>();

var host = builder.Build();
await host.RunAsync();
