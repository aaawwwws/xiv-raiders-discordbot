using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class BotNotificationService : IHostedService, IDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly ILogger<BotNotificationService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string? _apiOrigin;
    private readonly string? _frontendOrigin;
    private readonly string? _timelineSecret;
    private readonly TimeSpan _pollingInterval;
    private DateTimeOffset _lastFetchTime = DateTimeOffset.UtcNow.AddMinutes(-5);
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    private sealed record BotNotificationResponse(
        [property: JsonPropertyName("notifications")] BotNotificationItem[] Notifications,
        [property: JsonPropertyName("lastFetchTime")] string LastFetchTime,
        [property: JsonPropertyName("count")] int Count);

    private sealed record BotNotificationItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("recipientUserId")] ulong RecipientUserId,
        [property: JsonPropertyName("actorUserId")] ulong ActorUserId,
        [property: JsonPropertyName("actorName")] string ActorName,
        [property: JsonPropertyName("actorSlug")] string? ActorSlug,
        [property: JsonPropertyName("actorAvatarUrl")] string? ActorAvatarUrl,
        [property: JsonPropertyName("postId")] string PostId,
        [property: JsonPropertyName("emoji")] string? Emoji,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("postContent")] string? PostContent,
        [property: JsonPropertyName("createdAtUtc")] string CreatedAtUtc);

    public BotNotificationService(
        DiscordSocketClient client,
        ILogger<BotNotificationService> logger,
        IOptions<DiscordOptions> options)
    {
        var resolvedOptions = options.Value;
        _client = client;
        _logger = logger;
        _httpClient = new HttpClient();
        _apiOrigin = resolvedOptions.WebTimelineApiOrigin;
        _frontendOrigin = NormalizeOrigin(resolvedOptions.WebTimelineFrontendOrigin);
        _timelineSecret = resolvedOptions.WebTimelineBridgeSecret;
        _pollingInterval = TimeSpan.FromSeconds(Math.Clamp(resolvedOptions.WebTimelinePollingIntervalSeconds, 1, 60));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiOrigin) || string.IsNullOrWhiteSpace(_timelineSecret))
        {
            _logger.LogWarning("Bot notification service is disabled: missing API origin or timeline secret.");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollingTask = RunPollingLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        await (_pollingTask ?? Task.CompletedTask);
        _cts.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _httpClient.Dispose();
    }

    private async Task RunPollingLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Bot notification service started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await FetchAndProcessNotificationsAsync(cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in bot notification polling loop.");
            }

            try
            {
                await Task.Delay(_pollingInterval, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Bot notification service stopped.");
    }

    private async Task FetchAndProcessNotificationsAsync(CancellationToken cancellationToken)
    {
        var baseUri = new Uri(_apiOrigin!);
        var endpoint = new Uri(baseUri, $"/api/bot/notifications?since={_lastFetchTime:O}");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("X-Raid-Bot-Secret", _timelineSecret!);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch bot notifications. Status: {StatusCode}", response.StatusCode);
            return;
        }

        var apiResponse = await response.Content.ReadFromJsonAsync<BotNotificationResponse>(cancellationToken: cancellationToken);
        if (apiResponse is null)
        {
            return;
        }

        if (DateTimeOffset.TryParse(apiResponse.LastFetchTime, out var parsedLastFetchTime))
        {
            _lastFetchTime = parsedLastFetchTime;
        }

        foreach (var notification in apiResponse.Notifications)
        {
            await ProcessNotificationAsync(notification);
        }
    }

    private async Task ProcessNotificationAsync(BotNotificationItem notification)
    {
        if (notification.RecipientUserId == 0 || notification.ActorUserId == 0)
        {
            _logger.LogWarning("Invalid user IDs in notification. NotificationId: {NotificationId}", notification.Id);
            return;
        }

        var recipient = _client.GetUser(notification.RecipientUserId);
        if (recipient is null)
        {
            _logger.LogWarning("Recipient user not found. UserId: {UserId}", notification.RecipientUserId);
            return;
        }

        var actorDisplayName = string.IsNullOrWhiteSpace(notification.ActorName)
            ? $"User {notification.ActorUserId}"
            : notification.ActorName.Trim();

        var typeLabel = notification.Type.ToLowerInvariant() switch
        {
            "reply" => "返信",
            "repost" => "リポスト",
            "reaction" => "リアクション",
            "follow" => "フォロー",
            "quote" => "引用",
            "post_update" => "投稿更新",
            _ => notification.Type
        };

        var embed = new EmbedBuilder()
            .WithColor(Color.Gold)
            .WithTimestamp(DateTimeOffset.UtcNow)
            .WithTitle($"{actorDisplayName} が {typeLabel} しました");

        var description = new StringBuilder();
        if (string.Equals(notification.Type, "reaction", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(notification.Emoji))
            {
                description.AppendLine($"絵文字: {notification.Emoji}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(notification.Content))
        {
            description.AppendLine(notification.Content);
        }

        if (!string.IsNullOrWhiteSpace(notification.PostContent))
        {
            if (description.Length > 0)
            {
                description.AppendLine();
            }

            description.AppendLine("**対象の投稿:**");
            description.AppendLine(notification.PostContent);
        }

        if (description.Length > 0)
        {
            embed.WithDescription(description.ToString());
        }

        if (!string.IsNullOrWhiteSpace(notification.ActorAvatarUrl))
        {
            embed.WithThumbnailUrl(notification.ActorAvatarUrl);
        }

        var profileUrl = BuildUserProfileUrl(notification.ActorSlug);
        if (string.Equals(notification.Type, "follow", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(profileUrl))
        {
            embed.AddField("プロフィール", profileUrl, inline: false);
        }

        var postUrl = BuildPostUrl(notification.PostId);
        if (!string.IsNullOrWhiteSpace(postUrl))
        {
            embed.AddField("URL", postUrl, inline: false);
        }

        try
        {
            var dmChannel = await recipient.CreateDMChannelAsync();
            await dmChannel.SendMessageAsync(embed: embed.Build());
            _logger.LogInformation(
                "Sent bot notification DM to user {UserId}. NotificationId: {NotificationId}, Type: {Type}",
                notification.RecipientUserId,
                notification.Id,
                notification.Type);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to send bot notification DM to user {UserId}. NotificationId: {NotificationId}",
                notification.RecipientUserId,
                notification.Id);
        }
    }

    private string? BuildPostUrl(string? postId)
    {
        if (string.IsNullOrWhiteSpace(_frontendOrigin) || string.IsNullOrWhiteSpace(postId))
        {
            return null;
        }

        return $"{_frontendOrigin}/posts/{Uri.EscapeDataString(postId.Trim())}";
    }

    private string? BuildUserProfileUrl(string? actorSlug)
    {
        if (string.IsNullOrWhiteSpace(_frontendOrigin) || string.IsNullOrWhiteSpace(actorSlug))
        {
            return null;
        }

        return $"{_frontendOrigin}/users/{Uri.EscapeDataString(actorSlug.Trim())}";
    }

    private static string? NormalizeOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        return origin.Trim().TrimEnd('/');
    }
}
