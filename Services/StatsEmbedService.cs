using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using RaidDcBot.Data;
using RaidDcBot.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RaidDcBot.Services;

public sealed class StatsEmbedService
{
    private const string DataCenterTitle = "データセンター人口分布";
    private const string ServerTitleSuffix = "サーバー人口分布";
    private const int ChartMaxSlices = 8;

    private static readonly Rgba32[][] ChartPalettes =
    [
        [Hex("#2ecc71"), Hex("#27ae60"), Hex("#1abc9c"), Hex("#16a085"), Hex("#f1c40f"), Hex("#f39c12"), Hex("#e67e22"), Hex("#d35400"), Hex("#7f8c8d"), Hex("#95a5a6"), Hex("#3498db"), Hex("#2980b9"), Hex("#8e44ad"), Hex("#9b59b6"), Hex("#e74c3c")],
        [Hex("#3498db"), Hex("#2980b9"), Hex("#5dade2"), Hex("#85c1e9"), Hex("#1abc9c"), Hex("#16a085"), Hex("#f4d03f"), Hex("#f5b041"), Hex("#ec7063"), Hex("#e74c3c"), Hex("#a569bd"), Hex("#8e44ad"), Hex("#7f8c8d"), Hex("#95a5a6"), Hex("#2ecc71")],
        [Hex("#e67e22"), Hex("#d35400"), Hex("#f39c12"), Hex("#f5b041"), Hex("#e74c3c"), Hex("#ec7063"), Hex("#f1948a"), Hex("#a569bd"), Hex("#8e44ad"), Hex("#3498db"), Hex("#2980b9"), Hex("#1abc9c"), Hex("#16a085"), Hex("#7f8c8d"), Hex("#95a5a6")],
        [Hex("#9b59b6"), Hex("#8e44ad"), Hex("#a569bd"), Hex("#bb8fce"), Hex("#3498db"), Hex("#5dade2"), Hex("#2ecc71"), Hex("#58d68d"), Hex("#f4d03f"), Hex("#f5b041"), Hex("#e67e22"), Hex("#d35400"), Hex("#7f8c8d"), Hex("#95a5a6"), Hex("#e74c3c")],
        [Hex("#e74c3c"), Hex("#ec7063"), Hex("#f1948a"), Hex("#f5b7b1"), Hex("#f39c12"), Hex("#f5b041"), Hex("#f7dc6f"), Hex("#2ecc71"), Hex("#58d68d"), Hex("#1abc9c"), Hex("#3498db"), Hex("#5dade2"), Hex("#8e44ad"), Hex("#a569bd"), Hex("#7f8c8d")],
        [Hex("#16a085"), Hex("#1abc9c"), Hex("#48c9b0"), Hex("#76d7c4"), Hex("#27ae60"), Hex("#2ecc71"), Hex("#58d68d"), Hex("#3498db"), Hex("#5dade2"), Hex("#85c1e9"), Hex("#8e44ad"), Hex("#a569bd"), Hex("#f39c12"), Hex("#f5b041"), Hex("#e74c3c")]
    ];

    private static readonly string[] PreferredChartFonts =
    [
        "Segoe UI",
        "Yu Gothic UI",
        "Meiryo",
        "Noto Sans CJK JP",
        "Arial"
    ];

    private static readonly Dictionary<char, string[]> Glyph5x7 = new()
    {
        ['A'] = ["01110","10001","10001","11111","10001","10001","10001"],
        ['B'] = ["11110","10001","10001","11110","10001","10001","11110"],
        ['C'] = ["01111","10000","10000","10000","10000","10000","01111"],
        ['D'] = ["11110","10001","10001","10001","10001","10001","11110"],
        ['E'] = ["11111","10000","10000","11110","10000","10000","11111"],
        ['F'] = ["11111","10000","10000","11110","10000","10000","10000"],
        ['G'] = ["01111","10000","10000","10111","10001","10001","01111"],
        ['H'] = ["10001","10001","10001","11111","10001","10001","10001"],
        ['I'] = ["11111","00100","00100","00100","00100","00100","11111"],
        ['J'] = ["00111","00010","00010","00010","10010","10010","01100"],
        ['K'] = ["10001","10010","10100","11000","10100","10010","10001"],
        ['L'] = ["10000","10000","10000","10000","10000","10000","11111"],
        ['M'] = ["10001","11011","10101","10101","10001","10001","10001"],
        ['N'] = ["10001","10001","11001","10101","10011","10001","10001"],
        ['O'] = ["01110","10001","10001","10001","10001","10001","01110"],
        ['P'] = ["11110","10001","10001","11110","10000","10000","10000"],
        ['Q'] = ["01110","10001","10001","10001","10101","10010","01101"],
        ['R'] = ["11110","10001","10001","11110","10100","10010","10001"],
        ['S'] = ["01111","10000","10000","01110","00001","00001","11110"],
        ['T'] = ["11111","00100","00100","00100","00100","00100","00100"],
        ['U'] = ["10001","10001","10001","10001","10001","10001","01110"],
        ['V'] = ["10001","10001","10001","10001","10001","01010","00100"],
        ['W'] = ["10001","10001","10001","10101","10101","10101","01010"],
        ['X'] = ["10001","10001","01010","00100","01010","10001","10001"],
        ['Y'] = ["10001","10001","01010","00100","00100","00100","00100"],
        ['Z'] = ["11111","00001","00010","00100","01000","10000","11111"],
        ['0'] = ["01110","10001","10011","10101","11001","10001","01110"],
        ['1'] = ["00100","01100","00100","00100","00100","00100","01110"],
        ['2'] = ["01110","10001","00001","00010","00100","01000","11111"],
        ['3'] = ["11110","00001","00001","01110","00001","00001","11110"],
        ['4'] = ["00010","00110","01010","10010","11111","00010","00010"],
        ['5'] = ["11111","10000","10000","11110","00001","00001","11110"],
        ['6'] = ["01110","10000","10000","11110","10001","10001","01110"],
        ['7'] = ["11111","00001","00010","00100","01000","01000","01000"],
        ['8'] = ["01110","10001","10001","01110","10001","10001","01110"],
        ['9'] = ["01110","10001","10001","01111","00001","00001","01110"],
        [' '] = ["00000","00000","00000","00000","00000","00000","00000"],
        ['-'] = ["00000","00000","00000","11111","00000","00000","00000"],
        ['.'] = ["00000","00000","00000","00000","00000","01100","01100"],
        ['('] = ["00010","00100","01000","01000","01000","00100","00010"],
        [')'] = ["01000","00100","00010","00010","00010","00100","01000"],
        ['%'] = ["11001","11010","00100","00100","01011","10011","00000"],
        ['/'] = ["00001","00010","00100","01000","10000","00000","00000"],
        [':'] = ["00000","01100","01100","00000","01100","01100","00000"]
    };

    private readonly Db _db;
    private readonly ILogger<StatsEmbedService> _logger;

    public StatsEmbedService(Db db, ILogger<StatsEmbedService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<StatsRefreshResult> SetupAndRefreshAsync(SocketGuild guild, IMessageChannel channel)
        => RefreshPopulationEmbedsInternalAsync(guild, channel, persistPanel: true);

    public async Task<StatsRefreshResult> RefreshIfConfiguredAsync(SocketGuild guild)
    {
        var panel = await _db.GetGuildStatsPanelAsync(guild.Id);
        if (panel is null || panel.ChannelId == 0)
        {
            return new StatsRefreshResult(false, false, 0, "未設定");
        }

        var channel = guild.GetTextChannel(ToUlong(panel.ChannelId));
        if (channel is null)
        {
            return new StatsRefreshResult(false, false, 0, "統計チャンネル未検出");
        }

        return await RefreshPopulationEmbedsInternalAsync(guild, channel, persistPanel: true, panel);
    }

    private async Task<StatsRefreshResult> RefreshPopulationEmbedsInternalAsync(
        SocketGuild guild,
        IMessageChannel channel,
        bool persistPanel,
        GuildStatsPanel? panel = null)
    {
        panel ??= await _db.GetGuildStatsPanelAsync(guild.Id);

        try
        {
            await guild.DownloadUsersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download guild users before stats refresh. GuildId={GuildId}", guild.Id);
        }

        var allRegistered = await _db.ListRegisteredDiscordUserProfilesAsync();
        var normalizedAll = NormalizeRegisteredProfiles(allRegistered);
        var guildUserIds = guild.Users
            .Where(user => !user.IsBot)
            .Select(user => user.Id)
            .ToHashSet();
        var registered = normalizedAll
            .Where(profile => guildUserIds.Contains(ToUlong(profile.DiscordUserId)))
            .ToList();

        var source = "このサーバーの在籍メンバー";

        var specs = BuildChartSpecs(registered);
        var desiredTitles = new HashSet<string>(specs.Select(spec => spec.EmbedTitle), StringComparer.Ordinal);
        var existingMessages = await ListManagedStatsMessagesAsync(channel, guild.CurrentUser.Id, desiredTitles, panel);
        var existingByTitle = existingMessages
            .Where(message => message.Embeds.FirstOrDefault()?.Title is not null)
            .GroupBy(message => message.Embeds.First().Title!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var createdByTitle = new Dictionary<string, IUserMessage>(StringComparer.Ordinal);

        foreach (var spec in specs)
        {
            existingByTitle.TryGetValue(spec.EmbedTitle, out var existing);
            var chartPng = BuildPieChartPng(spec.ChartTitle, spec.ChartSlices, spec.TotalCount, spec.PaletteIndex);
            var embed = BuildDistributionEmbed(
                spec.EmbedTitle,
                spec.TotalCount,
                spec.DisplayRows,
                spec.MaxRows,
                spec.Color,
                spec.ChartFileName);

            var posted = await UpsertStatsMessageAsync(channel, existing, embed, chartPng, spec.ChartFileName);
            createdByTitle[spec.EmbedTitle] = posted;
        }

        foreach (var stale in existingMessages)
        {
            var title = stale.Embeds.FirstOrDefault()?.Title;
            if (string.IsNullOrWhiteSpace(title) || desiredTitles.Contains(title))
            {
                continue;
            }

            try
            {
                await stale.DeleteAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete stale stats message. MessageId={MessageId}", stale.Id);
            }
        }

        if (persistPanel && createdByTitle.TryGetValue(DataCenterTitle, out var dcMessage))
        {
            var firstServerMessageId = specs
                .FirstOrDefault(spec => spec.EmbedTitle != DataCenterTitle) is { } firstServer
                && createdByTitle.TryGetValue(firstServer.EmbedTitle, out var serverMessage)
                ? serverMessage.Id
                : dcMessage.Id;

            await _db.UpsertGuildStatsPanelAsync(guild.Id, channel.Id, dcMessage.Id, firstServerMessageId);
        }

        return new StatsRefreshResult(true, false, registered.Count, source);
    }

    private static List<ChartSpec> BuildChartSpecs(IReadOnlyList<RegisteredProfile> registered)
    {
        var specs = new List<ChartSpec>();
        var dcRows = registered
            .GroupBy(profile => profile.DataCenter, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DistributionRow(group.Key, group.Count()))
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        specs.Add(new ChartSpec(
            DataCenterTitle,
            "DATA CENTERS",
            registered.Count,
            dcRows,
            BuildChartSlices(dcRows, ChartMaxSlices),
            20,
            ToDiscordColor(GetPalette(0)[0]),
            BuildChartFileName(0),
            0));

        var dcGroups = registered
            .GroupBy(profile => profile.DataCenter, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                DataCenter = group.Key,
                Count = group.Count(),
                Rows = group
                    .GroupBy(profile => profile.World, StringComparer.OrdinalIgnoreCase)
                    .Select(worldGroup => new DistributionRow(worldGroup.Key, worldGroup.Count()))
                    .OrderByDescending(row => row.Count)
                    .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.DataCenter, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var index = 1;
        foreach (var group in dcGroups)
        {
            var paletteIndex = index % ChartPalettes.Length;
            specs.Add(new ChartSpec(
                $"{group.DataCenter} {ServerTitleSuffix}",
                $"{group.DataCenter.ToUpperInvariant()} SERVERS",
                group.Count,
                group.Rows,
                BuildChartSlices(group.Rows, ChartMaxSlices),
                40,
                ToDiscordColor(GetPalette(paletteIndex)[0]),
                BuildChartFileName(index),
                paletteIndex));
            index++;
        }

        return specs;
    }

    private async Task<IReadOnlyList<IUserMessage>> ListManagedStatsMessagesAsync(
        IMessageChannel channel,
        ulong botUserId,
        IReadOnlySet<string> desiredTitles,
        GuildStatsPanel? panel = null)
    {
        const int pageSize = 100;
        const int maxScanMessages = 2000;

        var managedById = new Dictionary<ulong, IUserMessage>();
        var foundDesired = new HashSet<string>(StringComparer.Ordinal);
        void TrackManagedMessage(IUserMessage message)
        {
            if (message.Author.Id != botUserId)
            {
                return;
            }

            var title = message.Embeds.FirstOrDefault()?.Title;
            if (!IsManagedStatsTitle(title))
            {
                return;
            }

            managedById[message.Id] = message;
            if (!string.IsNullOrWhiteSpace(title) && desiredTitles.Contains(title))
            {
                foundDesired.Add(title);
            }
        }

        if (panel is not null)
        {
            var seedIds = new[] { panel.DataCenterMessageId, panel.WorldMessageId }
                .Where(id => id > 0)
                .Select(ToUlong)
                .Distinct()
                .ToArray();
            foreach (var seedId in seedIds)
            {
                IUserMessage? seed = null;
                try
                {
                    seed = await channel.GetMessageAsync(seedId) as IUserMessage;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to load stats seed message. MessageId={MessageId}", seedId);
                }

                if (seed is null)
                {
                    continue;
                }

                TrackManagedMessage(seed);

                foreach (var direction in new[] { Direction.Before, Direction.After })
                {
                    try
                    {
                        var nearby = await channel.GetMessagesAsync(seed.Id, direction, 50).FlattenAsync();
                        foreach (var nearbyMessage in nearby.OfType<IUserMessage>())
                        {
                            TrackManagedMessage(nearbyMessage);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to load nearby stats messages. MessageId={MessageId} Direction={Direction}", seed.Id, direction);
                    }
                }
            }

            if (desiredTitles.Count > 0 && foundDesired.Count >= desiredTitles.Count)
            {
                return managedById.Values
                    .OrderByDescending(message => message.Id)
                    .ToArray();
            }
        }

        ulong? beforeMessageId = null;
        var scanned = 0;

        while (scanned < maxScanMessages)
        {
            var page = beforeMessageId.HasValue
                ? await channel.GetMessagesAsync(beforeMessageId.Value, Direction.Before, pageSize).FlattenAsync()
                : await channel.GetMessagesAsync(pageSize).FlattenAsync();

            var pageMessages = page.OfType<IUserMessage>().ToList();
            if (pageMessages.Count == 0)
            {
                break;
            }

            scanned += pageMessages.Count;
            foreach (var message in pageMessages)
            {
                TrackManagedMessage(message);
            }

            if (desiredTitles.Count > 0 && foundDesired.Count >= desiredTitles.Count)
            {
                break;
            }

            beforeMessageId = pageMessages.Min(message => message.Id);
            if (pageMessages.Count < pageSize)
            {
                break;
            }
        }

        return managedById.Values
            .OrderByDescending(message => message.Id)
            .ToArray();
    }

    private static bool IsManagedStatsTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return string.Equals(title, DataCenterTitle, StringComparison.Ordinal) ||
               title.EndsWith(ServerTitleSuffix, StringComparison.Ordinal);
    }

    private async Task<IUserMessage> UpsertStatsMessageAsync(
        IMessageChannel channel,
        IUserMessage? existing,
        Embed embed,
        byte[]? chartPng,
        string chartFileName)
    {
        if (existing is not null)
        {
            try
            {
                if (chartPng is null || chartPng.Length == 0)
                {
                    await existing.ModifyAsync(props =>
                    {
                        props.Embed = embed;
                        props.Attachments = Optional.Create<IEnumerable<FileAttachment>>(Array.Empty<FileAttachment>());
                    });
                }
                else
                {
                    await using var updateStream = new MemoryStream(chartPng, writable: false);
                    await existing.ModifyAsync(props =>
                    {
                        props.Embed = embed;
                        props.Attachments = Optional.Create<IEnumerable<FileAttachment>>(
                            [new FileAttachment(updateStream, chartFileName)]);
                    });
                }

                return existing;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update stats message. MessageId={MessageId}. Falling back to repost.", existing.Id);
            }
        }

        if (chartPng is null || chartPng.Length == 0)
        {
            return await channel.SendMessageAsync(embed: embed, flags: MessageFlags.SuppressNotification);
        }

        await using var stream = new MemoryStream(chartPng, writable: false);
        return await channel.SendFileAsync(stream, chartFileName, embed: embed, flags: MessageFlags.SuppressNotification);
    }

    private static Embed BuildDistributionEmbed(
        string title,
        int total,
        IReadOnlyList<DistributionRow> rows,
        int maxRows,
        Discord.Color color,
        string chartFileName)
    {
        var displayRows = rows.Take(Math.Max(1, maxRows)).ToList();
        var otherCount = rows.Skip(displayRows.Count).Sum(row => row.Count);
        var lines = displayRows
            .Select((row, index) => $"{index + 1}. {row.Name}: {row.Count} ({ToPercent(row.Count, total):0.0}%)")
            .ToList();

        if (otherCount > 0)
        {
            lines.Add($"その他: {otherCount} ({ToPercent(otherCount, total):0.0}%)");
        }

        if (lines.Count == 0)
        {
            lines.Add("データなし");
        }

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color)
            .WithDescription(string.Join('\n', lines))
            .AddField("対象登録数", total, true)
            .WithCurrentTimestamp();

        if (!string.IsNullOrWhiteSpace(chartFileName))
        {
            embed.WithImageUrl($"attachment://{chartFileName}");
        }

        return embed.Build();
    }

    private static byte[]? BuildPieChartPng(string chartTitle, IReadOnlyList<DistributionRow> slices, int total, int paletteIndex)
    {
        if (total <= 0 || slices.Count == 0)
        {
            return null;
        }

        const int width = 1480;
        const int height = 940;
        const int pieCenterX = width / 2;
        const int pieCenterY = height / 2;
        const int pieRadius = 320;

        using var image = new Image<Rgba32>(width, height, new Rgba32(234, 234, 234, 255));
        DrawVerticalGradient(image, new Rgba32(238, 238, 238, 255), new Rgba32(231, 231, 231, 255));

        var palette = GetPalette(paletteIndex);
        var segments = BuildSegments(slices, total, palette);
        var radiusSquared = pieRadius * pieRadius;

        for (var y = pieCenterY - pieRadius; y <= pieCenterY + pieRadius; y++)
        {
            if (y < 0 || y >= height)
            {
                continue;
            }

            for (var x = pieCenterX - pieRadius; x <= pieCenterX + pieRadius; x++)
            {
                if (x < 0 || x >= width)
                {
                    continue;
                }

                var dx = x - pieCenterX;
                var dy = y - pieCenterY;
                var distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > radiusSquared)
                {
                    continue;
                }

                var angle = Math.Atan2(dy, dx) - (-Math.PI / 2.0);
                if (angle < 0)
                {
                    angle += Math.PI * 2.0;
                }

                var ratio = angle / (Math.PI * 2.0);
                var seg = segments.FirstOrDefault(segment => ratio <= segment.EndRatio);
                if (seg is null)
                {
                    continue;
                }

                var distance = Math.Sqrt(distanceSquared);
                image[x, y] = ApplySegmentShading(seg.Color, dx, dy, distance, 0, pieRadius, strength: 0.02);
            }
        }

        foreach (var seg in segments)
        {
            var angle = RatioToAngle(seg.StartRatio);
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var edgeX = pieCenterX + (int)Math.Round(cos * pieRadius);
            var edgeY = pieCenterY + (int)Math.Round(sin * pieRadius);
            DrawLineThick(image, pieCenterX, pieCenterY, edgeX, edgeY, new Rgba32(246, 246, 246, 255), 4);
        }

        DrawCircleOutline(image, pieCenterX, pieCenterY, pieRadius, new Rgba32(245, 245, 245, 255));
        DrawCircleOutline(image, pieCenterX, pieCenterY, pieRadius - 1, new Rgba32(245, 245, 245, 255));

        var titleFont = GetChartFont(66, FontStyle.Bold);
        DrawTextSmooth(image, chartTitle, titleFont, new Rgba32(35, 35, 35, 255), 64, 76);

        foreach (var seg in segments)
        {
            var span = seg.EndRatio - seg.StartRatio;
            if (span < 0.02)
            {
                continue;
            }

            var percent = ToPercent(seg.Row.Count, total);
            var (nameFontSize, percentFontSize) = span switch
            {
                >= 0.20 => (52f, 56f),
                >= 0.13 => (44f, 48f),
                >= 0.09 => (36f, 40f),
                _ => (30f, 34f)
            };

            var nameFont = GetChartFont(nameFontSize, FontStyle.Bold);
            var percentFont = GetChartFont(percentFontSize, FontStyle.Bold);
            var angle = RatioToAngle(seg.MidRatio);
            var radius = pieRadius * 0.58;
            var centerX = pieCenterX + (float)(Math.Cos(angle) * radius);
            var centerY = pieCenterY + (float)(Math.Sin(angle) * radius);

            var name = TruncateLegendText(seg.Row.Name, span >= 0.12 ? 10 : 7);
            var percentText = $"{percent:0.#}%";

            var rendered = false;
            var fitRadius = radius;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var nameSize = MeasureTextSmooth(name, nameFont);
                var percentSize = MeasureTextSmooth(percentText, percentFont);
                var totalHeight = nameSize.Height + 8 + percentSize.Height;
                var arcLength = span * 2.0 * Math.PI * fitRadius;
                var maxWidth = Math.Max(nameSize.Width, percentSize.Width);
                var canPlaceInside =
                    span >= 0.05 &&
                    maxWidth <= (arcLength * 0.92) &&
                    totalHeight <= (pieRadius * 0.58);

                if (canPlaceInside)
                {
                    DrawTextCenteredSmoothShadow(image, name, nameFont, new Rgba32(255, 255, 255, 255), centerX, centerY - (totalHeight / 2f) + (nameSize.Height / 2f), shadowAlpha: 82);
                    DrawTextCenteredSmoothShadow(image, percentText, percentFont, new Rgba32(255, 255, 255, 255), centerX, centerY + (totalHeight / 2f) - (percentSize.Height / 2f), shadowAlpha: 82);
                    rendered = true;
                    break;
                }

                var nextNameSize = nameFont.Size - 2f;
                var nextPercentSize = percentFont.Size - 2f;
                if (nextNameSize < 20f || nextPercentSize < 22f)
                {
                    break;
                }

                nameFont = GetChartFont(nextNameSize, FontStyle.Bold);
                percentFont = GetChartFont(nextPercentSize, FontStyle.Bold);
                fitRadius = Math.Min(pieRadius * 0.82, fitRadius + 12);
                centerX = pieCenterX + (float)(Math.Cos(angle) * fitRadius);
                centerY = pieCenterY + (float)(Math.Sin(angle) * fitRadius);
            }

            if (rendered)
            {
                continue;
            }

            var percentOnlyRadius = Math.Min(pieRadius * 0.86, fitRadius + 10);
            var percentOnlyX = pieCenterX + (float)(Math.Cos(angle) * percentOnlyRadius);
            var percentOnlyY = pieCenterY + (float)(Math.Sin(angle) * percentOnlyRadius);
            var percentOnlyFont = GetChartFont(Math.Max(20f, percentFont.Size), FontStyle.Bold);

            for (var attempt = 0; attempt < 6; attempt++)
            {
                var percentSize = MeasureTextSmooth(percentText, percentOnlyFont);
                var arcLength = span * 2.0 * Math.PI * percentOnlyRadius;
                if (percentSize.Width <= (arcLength * 0.96))
                {
                    DrawTextCenteredSmoothShadow(image, percentText, percentOnlyFont, new Rgba32(255, 255, 255, 255), percentOnlyX, percentOnlyY, shadowAlpha: 78);
                    rendered = true;
                    break;
                }

                var next = percentOnlyFont.Size - 2f;
                if (next < 16f)
                {
                    break;
                }

                percentOnlyFont = GetChartFont(next, FontStyle.Bold);
            }
        }

        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static List<DistributionRow> BuildChartSlices(IReadOnlyList<DistributionRow> rows, int maxSlices)
    {
        var slices = rows.Take(Math.Max(1, maxSlices)).ToList();
        var otherCount = rows.Skip(slices.Count).Sum(row => row.Count);
        if (otherCount > 0)
        {
            slices.Add(new DistributionRow("OTHERS", otherCount));
        }

        return slices;
    }

    private static List<PieSegment> BuildSegments(IReadOnlyList<DistributionRow> rows, int total, Rgba32[] palette)
    {
        var segments = new List<PieSegment>(rows.Count);
        var cumulative = 0.0;
        for (var i = 0; i < rows.Count; i++)
        {
            var startRatio = cumulative;
            var ratio = total <= 0 ? 0 : (double)rows[i].Count / total;
            cumulative += ratio;
            var endRatio = i == rows.Count - 1 ? 1.0 : Math.Min(1.0, cumulative);
            var midRatio = (startRatio + endRatio) / 2.0;
            segments.Add(new PieSegment(startRatio, endRatio, midRatio, palette[i % palette.Length], rows[i]));
        }

        return segments;
    }

    private static void DrawText(Image<Rgba32> image, int x, int y, string text, Rgba32 color, int scale)
    {
        var cursor = x;
        var spacing = Math.Max(1, scale);
        foreach (var rawChar in text)
        {
            var ch = char.ToUpperInvariant(rawChar);
            if (!Glyph5x7.TryGetValue(ch, out var glyph))
            {
                ch = ' ';
                glyph = Glyph5x7[ch];
            }

            for (var row = 0; row < glyph.Length; row++)
            {
                var line = glyph[row];
                for (var col = 0; col < line.Length; col++)
                {
                    if (line[col] != '1')
                    {
                        continue;
                    }

                    FillRect(
                        image,
                        cursor + (col * scale),
                        y + (row * scale),
                        cursor + (col * scale) + scale - 1,
                        y + (row * scale) + scale - 1,
                        color);
                }
            }

            cursor += (5 * scale) + spacing;
        }
    }

    private static void DrawTextCentered(Image<Rgba32> image, int centerX, int centerY, string text, Rgba32 color, int scale)
    {
        var normalized = NormalizeLegendText(text);
        var width = MeasureTextWidth(normalized, scale);
        var height = MeasureTextHeight(scale);
        DrawText(image, centerX - (width / 2), centerY - (height / 2), normalized, color, scale);
    }

    private static string NormalizeLegendText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .Select(ch =>
            {
                var up = char.ToUpperInvariant(ch);
                if (Glyph5x7.ContainsKey(up))
                {
                    return up;
                }

                return ' ';
            })
            .ToArray();
        return new string(chars).Trim();
    }

    private static string TruncateLegendText(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxChars)
        {
            return value;
        }

        return value[..Math.Max(1, maxChars - 1)] + ".";
    }

    private static void DrawVerticalGradient(Image<Rgba32> image, Rgba32 topColor, Rgba32 bottomColor)
    {
        for (var y = 0; y < image.Height; y++)
        {
            var ratio = image.Height <= 1 ? 0f : y / (float)(image.Height - 1);
            var rowColor = LerpColor(topColor, bottomColor, ratio);
            for (var x = 0; x < image.Width; x++)
            {
                image[x, y] = rowColor;
            }
        }
    }

    private static Rgba32 LerpColor(Rgba32 start, Rgba32 end, float t)
    {
        var ratio = Math.Clamp(t, 0f, 1f);
        var r = (byte)Math.Clamp((int)Math.Round(start.R + ((end.R - start.R) * ratio)), 0, 255);
        var g = (byte)Math.Clamp((int)Math.Round(start.G + ((end.G - start.G) * ratio)), 0, 255);
        var b = (byte)Math.Clamp((int)Math.Round(start.B + ((end.B - start.B) * ratio)), 0, 255);
        var a = (byte)Math.Clamp((int)Math.Round(start.A + ((end.A - start.A) * ratio)), 0, 255);
        return new Rgba32(r, g, b, a);
    }

    private static Font GetChartFont(float size, FontStyle style)
    {
        foreach (var name in PreferredChartFonts)
        {
            if (SystemFonts.TryGet(name, out var family))
            {
                return family.CreateFont(size, style);
            }
        }

        if (SystemFonts.Collection.Families.Any())
        {
            return SystemFonts.Collection.Families.First().CreateFont(size, style);
        }

        throw new InvalidOperationException("No system fonts available.");
    }

    private static FontRectangle MeasureTextSmooth(string text, Font font)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new FontRectangle(0, 0, 0, 0);
        }

        return TextMeasurer.MeasureBounds(text, new TextOptions(font));
    }

    private static void DrawTextSmooth(Image<Rgba32> image, string text, Font font, Rgba32 color, float x, float y)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        image.Mutate(ctx =>
        {
            ctx.DrawText(new RichTextOptions(font)
            {
                Origin = new PointF(x, y)
            }, text, color);
        });
    }

    private static void DrawTextCenteredSmooth(Image<Rgba32> image, string text, Font font, Rgba32 color, float centerX, float centerY)
    {
        var size = MeasureTextSmooth(text, font);
        DrawTextSmooth(image, text, font, color, centerX - (size.Width / 2f), centerY - (size.Height / 2f));
    }

    private static void DrawTextCenteredSmoothShadow(Image<Rgba32> image, string text, Font font, Rgba32 color, float centerX, float centerY, byte shadowAlpha = 76)
    {
        var shadowColor = new Rgba32(24, 24, 24, shadowAlpha);
        DrawTextCenteredSmooth(image, text, font, shadowColor, centerX + 2f, centerY + 2f);
        DrawTextCenteredSmooth(image, text, font, color, centerX, centerY);
    }

    private static void DrawTwoLineLabel(Image<Rgba32> image, CalloutLabel label, Font nameFont, Font percentFont, int anchorX, int topY)
    {
        var nameSize = MeasureTextSmooth(label.Name, nameFont);
        var percentSize = MeasureTextSmooth(label.PercentText, percentFont);

        float nameX;
        float percentX;
        if (label.Side > 0)
        {
            nameX = anchorX;
            percentX = anchorX;
        }
        else if (label.Side < 0)
        {
            nameX = anchorX - nameSize.Width;
            percentX = anchorX - percentSize.Width;
        }
        else
        {
            nameX = anchorX - (nameSize.Width / 2f);
            percentX = anchorX - (percentSize.Width / 2f);
        }

        var accent = LerpColor(label.Color, new Rgba32(35, 35, 35, 255), 0.1f);
        DrawTextSmooth(image, label.Name, nameFont, accent, nameX, topY);
        DrawTextSmooth(image, label.PercentText, percentFont, accent, percentX, topY + nameSize.Height + 6f);
    }

    private static Rgba32 ApplySegmentShading(Rgba32 baseColor, int dx, int dy, double distance, int innerRadius, int outerRadius, double strength)
    {
        var ringSpan = Math.Max(1, outerRadius - innerRadius);
        var radial = (distance - innerRadius) / ringSpan;
        var radialHighlight = 1.0 + (strength * 0.45) - (radial * strength);
        var directional = 1.0 + (((-dx - dy) / (double)(outerRadius * 2)) * strength);
        var shade = Math.Clamp(radialHighlight * directional, 0.88, 1.14);

        var r = (byte)Math.Clamp((int)Math.Round(baseColor.R * shade), 0, 255);
        var g = (byte)Math.Clamp((int)Math.Round(baseColor.G * shade), 0, 255);
        var b = (byte)Math.Clamp((int)Math.Round(baseColor.B * shade), 0, 255);
        return new Rgba32(r, g, b, 255);
    }

    private static void FillRoundedRect(Image<Rgba32> image, int left, int top, int right, int bottom, int radius, Rgba32 color)
    {
        if (left > right || top > bottom)
        {
            return;
        }

        var clippedRadius = Math.Max(0, Math.Min(radius, Math.Min((right - left) / 2, (bottom - top) / 2)));
        var radiusSquared = clippedRadius * clippedRadius;

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                if (clippedRadius == 0)
                {
                    SetPixel(image, x, y, color);
                    continue;
                }

                var inLeft = x < left + clippedRadius;
                var inRight = x > right - clippedRadius;
                var inTop = y < top + clippedRadius;
                var inBottom = y > bottom - clippedRadius;

                if ((!inLeft && !inRight) || (!inTop && !inBottom))
                {
                    SetPixel(image, x, y, color);
                    continue;
                }

                var cx = inLeft ? left + clippedRadius : right - clippedRadius;
                var cy = inTop ? top + clippedRadius : bottom - clippedRadius;
                var dx = x - cx;
                var dy = y - cy;
                if ((dx * dx) + (dy * dy) <= radiusSquared)
                {
                    SetPixel(image, x, y, color);
                }
            }
        }
    }

    private static void FillCircleBlend(Image<Rgba32> image, int centerX, int centerY, int radius, Rgba32 color)
    {
        var radiusSquared = radius * radius;
        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                if ((dx * dx) + (dy * dy) <= radiusSquared)
                {
                    BlendPixel(image, x, y, color);
                }
            }
        }
    }

    private static void FillRect(Image<Rgba32> image, int left, int top, int right, int bottom, Rgba32 color)
    {
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                SetPixel(image, x, y, color);
            }
        }
    }

    private static void DrawRect(Image<Rgba32> image, int left, int top, int right, int bottom, Rgba32 color)
    {
        for (var x = left; x <= right; x++)
        {
            SetPixel(image, x, top, color);
            SetPixel(image, x, bottom, color);
        }

        for (var y = top; y <= bottom; y++)
        {
            SetPixel(image, left, y, color);
            SetPixel(image, right, y, color);
        }
    }

    private static void DrawLineThick(Image<Rgba32> image, int x0, int y0, int x1, int y1, Rgba32 color, int thickness)
    {
        var radius = Math.Max(0, (thickness - 1) / 2);
        if (radius == 0)
        {
            DrawLine(image, x0, y0, x1, y1, color);
            return;
        }

        for (var ox = -radius; ox <= radius; ox++)
        {
            for (var oy = -radius; oy <= radius; oy++)
            {
                if ((ox * ox) + (oy * oy) > (radius * radius))
                {
                    continue;
                }

                DrawLine(image, x0 + ox, y0 + oy, x1 + ox, y1 + oy, color);
            }
        }
    }

    private static void DrawLine(Image<Rgba32> image, int x0, int y0, int x1, int y1, Rgba32 color)
    {
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;

        while (true)
        {
            SetPixel(image, x0, y0, color);
            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            var e2 = 2 * error;
            if (e2 >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private static void DrawCircleOutline(Image<Rgba32> image, int centerX, int centerY, int radius, Rgba32 color)
    {
        var x = radius;
        var y = 0;
        var decision = 1 - x;
        while (y <= x)
        {
            SetPixel(image, centerX + x, centerY + y, color);
            SetPixel(image, centerX + y, centerY + x, color);
            SetPixel(image, centerX - y, centerY + x, color);
            SetPixel(image, centerX - x, centerY + y, color);
            SetPixel(image, centerX - x, centerY - y, color);
            SetPixel(image, centerX - y, centerY - x, color);
            SetPixel(image, centerX + y, centerY - x, color);
            SetPixel(image, centerX + x, centerY - y, color);
            y++;

            if (decision <= 0)
            {
                decision += 2 * y + 1;
            }
            else
            {
                x--;
                decision += 2 * (y - x) + 1;
            }
        }
    }

    private static void FillCircle(Image<Rgba32> image, int centerX, int centerY, int radius, Rgba32 color)
    {
        var radiusSquared = radius * radius;
        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                if ((dx * dx) + (dy * dy) <= radiusSquared)
                {
                    SetPixel(image, x, y, color);
                }
            }
        }
    }

    private static void ResolveLabelCollisions(List<CalloutLabel> labels, int minY, int maxY, int gap)
    {
        if (labels.Count == 0)
        {
            return;
        }

        foreach (var label in labels)
        {
            label.Y = Math.Clamp(label.DesiredY, minY, maxY);
        }

        for (var pass = 0; pass < 6; pass++)
        {
            labels.Sort((a, b) => a.Y.CompareTo(b.Y));
            for (var i = 1; i < labels.Count; i++)
            {
                var minCurrent = labels[i - 1].Y + gap;
                if (labels[i].Y < minCurrent)
                {
                    labels[i].Y = minCurrent;
                }
            }

            var overflow = labels[^1].Y - maxY;
            if (overflow > 0)
            {
                foreach (var label in labels)
                {
                    label.Y -= overflow;
                }
            }

            for (var i = labels.Count - 2; i >= 0; i--)
            {
                var maxCurrent = labels[i + 1].Y - gap;
                if (labels[i].Y > maxCurrent)
                {
                    labels[i].Y = maxCurrent;
                }
            }

            var underflow = minY - labels[0].Y;
            if (underflow > 0)
            {
                foreach (var label in labels)
                {
                    label.Y += underflow;
                }
            }
        }
    }

    private static int MeasureTextWidth(string text, int scale)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var spacing = Math.Max(1, scale);
        return (text.Length * (5 * scale)) + ((text.Length - 1) * spacing);
    }

    private static int MeasureTextHeight(int scale) => 7 * scale;

    private static double RatioToAngle(double ratio)
    {
        return (-Math.PI / 2.0) + (ratio * Math.PI * 2.0);
    }

    private static Rgba32 GetContrastTextColor(Rgba32 background)
    {
        var luminance = (background.R * 0.299) + (background.G * 0.587) + (background.B * 0.114);
        return luminance >= 140 ? new Rgba32(25, 25, 25, 255) : new Rgba32(250, 250, 250, 255);
    }

    private static void SetPixel(Image<Rgba32> image, int x, int y, Rgba32 color)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
        {
            return;
        }

        image[x, y] = color;
    }

    private static void BlendPixel(Image<Rgba32> image, int x, int y, Rgba32 source)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
        {
            return;
        }

        if (source.A == 255)
        {
            image[x, y] = source;
            return;
        }

        var destination = image[x, y];
        var alpha = source.A / 255f;
        var invAlpha = 1f - alpha;

        var r = (byte)Math.Clamp((int)Math.Round((source.R * alpha) + (destination.R * invAlpha)), 0, 255);
        var g = (byte)Math.Clamp((int)Math.Round((source.G * alpha) + (destination.G * invAlpha)), 0, 255);
        var b = (byte)Math.Clamp((int)Math.Round((source.B * alpha) + (destination.B * invAlpha)), 0, 255);
        image[x, y] = new Rgba32(r, g, b, 255);
    }

    private static double ToPercent(int value, int total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return (double)value / total * 100.0;
    }

    private static List<RegisteredProfile> NormalizeRegisteredProfiles(IEnumerable<DiscordUserProfile> profiles)
    {
        return profiles
            .Select(TryNormalizeRegisteredProfile)
            .Where(profile => profile is not null)
            .Select(profile => profile!)
            .ToList();
    }

    private static RegisteredProfile? TryNormalizeRegisteredProfile(DiscordUserProfile profile)
    {
        var name = profile.FfxivName?.Trim();
        var world = profile.FfxivWorld?.Trim();
        var dataCenter = profile.FfxivDataCenter?.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dataCenter))
        {
            dataCenter = DcResolver.Resolve(null, world)?.Trim();
        }

        if (string.IsNullOrWhiteSpace(dataCenter))
        {
            return null;
        }

        return new RegisteredProfile(profile.DiscordUserId, name, world, dataCenter);
    }

    private static string BuildChartFileName(int index) => $"stats_chart_{index}.png";
    private static ulong ToUlong(long value) => unchecked((ulong)value);
    private static Rgba32 Hex(string hex) => Rgba32.ParseHex(hex.TrimStart('#'));
    private static Rgba32[] GetPalette(int index) => ChartPalettes[Math.Abs(index) % ChartPalettes.Length];
    private static Discord.Color ToDiscordColor(Rgba32 color) => new(color.R, color.G, color.B);

    private sealed record DistributionRow(string Name, int Count);
    private sealed record RegisteredProfile(long DiscordUserId, string Name, string World, string DataCenter);
    private sealed record PieSegment(double StartRatio, double EndRatio, double MidRatio, Rgba32 Color, DistributionRow Row);
    private sealed class CalloutLabel
    {
        public CalloutLabel(string name, string percentText, Rgba32 color, int side, int x, int desiredY)
        {
            Name = name;
            PercentText = percentText;
            Color = color;
            Side = side;
            X = x;
            DesiredY = desiredY;
            Y = desiredY;
        }

        public string Name { get; }
        public string PercentText { get; }
        public Rgba32 Color { get; }
        public int Side { get; }
        public int X { get; }
        public int DesiredY { get; }
        public int Y { get; set; }
    }

    private sealed record ChartSpec(
        string EmbedTitle,
        string ChartTitle,
        int TotalCount,
        IReadOnlyList<DistributionRow> DisplayRows,
        IReadOnlyList<DistributionRow> ChartSlices,
        int MaxRows,
        Discord.Color Color,
        string ChartFileName,
        int PaletteIndex);
}

public sealed record StatsRefreshResult(bool Updated, bool UsedFallback, int RegisteredCount, string Source);
