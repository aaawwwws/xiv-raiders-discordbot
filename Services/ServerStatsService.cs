using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using RaidDcBot.Data;

namespace RaidDcBot.Services;

public sealed class ServerStatsService
{
    private const int Width = 900;
    private const int Padding = 40;
    private const int CardGap = 16;
    private const int CardPadding = 20;
    private const int CardRadius = 16;
    private const int StatsPerRow = 2;
    private const int CardWidth = (Width - Padding * 2 - CardGap * (StatsPerRow - 1)) / StatsPerRow;
    private const int CardHeight = 100;

    private static readonly Color BackgroundColor = Color.ParseHex("#0f1117");
    private static readonly Color CardColor = Color.ParseHex("#1a1d27");
    private static readonly Color CardBorder = Color.ParseHex("#2a2d3a");
    private static readonly Color AccentColor = Color.ParseHex("#5865f2");
    private static readonly Color TextColor = Color.ParseHex("#dcddde");
    private static readonly Color SubtextColor = Color.ParseHex("#72767d");
    private static readonly Color GreenColor = Color.ParseHex("#57f287");
    private static readonly Color YellowColor = Color.ParseHex("#fee75c");
    private static readonly Color RedColor = Color.ParseHex("#ed4245");

    private static readonly FontFamily _fontFamily;
    private static readonly Font _titleFont;
    private static readonly Font _labelFont;
    private static readonly Font _valueFont;
    private static readonly Font _headerFont;
    private static readonly Font _graphLabelFont;

    static ServerStatsService()
    {
        try
        {
            var found = SystemFonts.Families.FirstOrDefault(f =>
                f.Name.Contains("Noto Sans JP", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("Yu Gothic", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("Meiryo", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("Segoe UI", StringComparison.OrdinalIgnoreCase));

            _fontFamily = found.Name is not null ? found : SystemFonts.Families.First();

            _titleFont = _fontFamily.CreateFont(28, FontStyle.Bold);
            _labelFont = _fontFamily.CreateFont(13, FontStyle.Regular);
            _valueFont = _fontFamily.CreateFont(26, FontStyle.Bold);
            _headerFont = _fontFamily.CreateFont(16, FontStyle.Bold);
            _graphLabelFont = _fontFamily.CreateFont(11, FontStyle.Regular);
        }
        catch
        {
            _fontFamily = SystemFonts.Families.First();
            _titleFont = _fontFamily.CreateFont(28, FontStyle.Bold);
            _labelFont = _fontFamily.CreateFont(13, FontStyle.Regular);
            _valueFont = _fontFamily.CreateFont(26, FontStyle.Bold);
            _headerFont = _fontFamily.CreateFont(16, FontStyle.Bold);
            _graphLabelFont = _fontFamily.CreateFont(11, FontStyle.Regular);
        }
    }

    public static Stream GenerateStatsImage(
        string guildName,
        int totalMembers,
        ServerStatsSnapshot stats,
        IReadOnlyList<(DateTimeOffset Hour, int MessageCount, int VcCount)> hourlyActivity,
        IReadOnlyList<(string Label, int Count)> storyProgression)
    {
        var statCards = new (string Label, string Value, Color Accent)[]
        {
            ("登録メンバー", $"{stats.RegisteredMemberCount:N0}", AccentColor),
            ("サーバー人数", $"{totalMembers:N0}", AccentColor),
            ("今日のメッセージ", $"{stats.TodayMessageCount:N0}", GreenColor),
            ("今日のVC参加", $"{stats.TodayJoinedCount:N0}", GreenColor),
            ("週間メッセージ", $"{stats.WeekMessageCount:N0}", YellowColor),
            ("週間VC参加", $"{stats.WeekVcCount:N0}", YellowColor),
            ("募集(OPEN/FULL)", $"{stats.OpenRecruitCount:N0}", AccentColor),
            ("今日の新規募集", $"{stats.TodayRecruitCount:N0}", GreenColor),
            ("お問い合わせ(OPEN)", $"{stats.OpenInquiryCount:N0}", YellowColor),
            ("抽選(OPEN)", $"{stats.OpenLotteryCount:N0}", AccentColor),
            ("今日の切断実行", $"{stats.TodayKickCount:N0}", RedColor),
        };

        var graphHours = 24;
        var graphHeight = 180;
        var storyBarHeight = 28;
        var storyBarGap = 6;
        var storySectionHeight = storyProgression.Count > 0
            ? 40 + storyProgression.Count * (storyBarHeight + storyBarGap) + 20
            : 0;
        var graphSectionY = Padding + 60 + ((statCards.Length + StatsPerRow - 1) / StatsPerRow) * (CardHeight + CardGap) + 30;
        var graphWidth = Width - Padding * 2 - 80;
        var graphX = Padding + 60;
        var storySectionY = graphSectionY + graphHeight + 60;
        var totalHeight = storySectionY + storySectionHeight + 40;

        using var image = new Image<Rgba32>(Width, totalHeight, BackgroundColor);

        image.Mutate(ctx =>
        {
            DrawTitle(ctx, guildName);
            DrawStatCards(ctx, statCards);
            DrawActivityGraph(ctx, hourlyActivity, graphX, graphSectionY, graphWidth, graphHeight, graphHours);
            if (storyProgression.Count > 0)
                DrawStoryProgressionBars(ctx, storyProgression, storySectionY);
            DrawFooter(ctx, totalHeight);
        });

        var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        stream.Position = 0;
        return stream;
    }

    private static void DrawTitle(IImageProcessingContext ctx, string guildName)
    {
        var titleText = $"📊 {guildName} - サーバー統計";
        ctx.DrawText(titleText, _titleFont, TextColor, new PointF(Padding, Padding));

        var now = DateTimeOffset.UtcNow;
        var dateText = now.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy/MM/dd HH:mm JST");
        ctx.DrawText(dateText, _labelFont, SubtextColor, new PointF(Padding, Padding + 36));
    }

    private static void DrawStatCards(IImageProcessingContext ctx, (string Label, string Value, Color Accent)[] cards)
    {
        var startY = Padding + 70;

        for (int i = 0; i < cards.Length; i++)
        {
            int row = i / StatsPerRow;
            int col = i % StatsPerRow;
            int x = Padding + col * (CardWidth + CardGap);
            int y = startY + row * (CardHeight + CardGap);

            var cardRect = new RectangleF(x, y, CardWidth, CardHeight);
            var cardPath = BuildRoundedRect(cardRect, CardRadius);

            ctx.Fill(CardColor, cardPath);
            ctx.Draw(CardBorder, 1, cardPath);

            var accentBar = BuildRoundedRect(new RectangleF(x, y, 4, CardRadius), 2);
            ctx.Fill(cards[i].Accent, accentBar);
            ctx.Fill(cards[i].Accent, BuildRoundedRect(new RectangleF(x, y + CardRadius, 4, CardHeight - CardRadius), 2));

            ctx.DrawText(cards[i].Label, _labelFont, SubtextColor, new PointF(x + CardPadding, y + 12));
            ctx.DrawText(cards[i].Value, _valueFont, TextColor, new PointF(x + CardPadding, y + 38));
        }
    }

    private static void DrawActivityGraph(
        IImageProcessingContext ctx,
        IReadOnlyList<(DateTimeOffset Hour, int MessageCount, int VcCount)> hourlyActivity,
        int x, int y, int width, int height, int hours)
    {
        ctx.DrawText("📈 24時間アクティビティ", _headerFont, TextColor, new PointF(Padding, y - 30));

        var graphArea = new RectangleF(x, y, width, height);
        ctx.Fill(Color.ParseHex("#14161e"), BuildRoundedRect(graphArea, 8));

        if (hourlyActivity.Count == 0)
        {
            ctx.DrawText("データなし", _labelFont, SubtextColor, new PointF(x + width / 2 - 30, y + height / 2));
            return;
        }

        var maxMsg = Math.Max(1, hourlyActivity.Max(a => a.MessageCount));
        var maxVc = Math.Max(1, hourlyActivity.Max(a => a.VcCount));
        var maxVal = Math.Max(maxMsg, maxVc);

        var barGroupWidth = (float)width / Math.Min(hourlyActivity.Count, hours);
        var barWidth = barGroupWidth * 0.35f;
        var graphBottom = y + height - 30;
        var graphTop = y + 10;
        var graphH = graphBottom - graphTop;

        for (int i = 0; i < Math.Min(hourlyActivity.Count, hours); i++)
        {
            var item = hourlyActivity[i];
            var gx = x + i * barGroupWidth + barGroupWidth * 0.15f;

            var msgH = (float)item.MessageCount / maxVal * graphH;
            var msgRect = new RectangleF(gx, graphBottom - msgH, barWidth, msgH);
            ctx.Fill(GreenColor, BuildRoundedRect(msgRect, 3));

            var vcH = (float)item.VcCount / maxVal * graphH;
            var vcRect = new RectangleF(gx + barWidth + 2, graphBottom - vcH, barWidth, vcH);
            ctx.Fill(AccentColor, BuildRoundedRect(vcRect, 3));

            if (i % 4 == 0)
            {
                var label = item.Hour.ToOffset(TimeSpan.FromHours(9)).ToString("HH:mm");
                ctx.DrawText(label, _graphLabelFont, SubtextColor, new PointF(gx - 5, graphBottom + 5));
            }
        }

        for (int i = 0; i <= 4; i++)
        {
            var val = (int)(maxVal * i / 4.0);
            var ly = graphBottom - (graphH * i / 4.0f);
            ctx.DrawText(val.ToString(), _graphLabelFont, SubtextColor, new PointF(x - 40, ly - 7));

            var linePath = new PathBuilder();
            linePath.AddLine(new Vector2(x, ly), new Vector2(x + width, ly));
            ctx.Draw(Color.ParseHex("#1e2028"), 1, linePath.Build());
        }

        var legendY = graphBottom + 22;
        ctx.Fill(GreenColor, BuildRoundedRect(new RectangleF(x, legendY, 12, 12), 2));
        ctx.DrawText("メッセージ", _graphLabelFont, SubtextColor, new PointF(x + 16, legendY - 1));
        ctx.Fill(AccentColor, BuildRoundedRect(new RectangleF(x + 100, legendY, 12, 12), 2));
        ctx.DrawText("VC参加", _graphLabelFont, SubtextColor, new PointF(x + 116, legendY - 1));
    }

    private static void DrawFooter(IImageProcessingContext ctx, int totalHeight)
    {
        var footerY = totalHeight - 30;
        ctx.DrawText("RaidDC Bot", _labelFont, SubtextColor, new PointF(Padding, footerY));
        var timeText = $"Generated at {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
        var timeSize = TextMeasurer.MeasureSize(timeText, new TextOptions(_labelFont));
        ctx.DrawText(timeText, _labelFont, SubtextColor, new PointF(Width - Padding - timeSize.Width, footerY));
    }

    private static readonly Color[] StoryColors =
    {
        Color.ParseHex("#5865f2"),
        Color.ParseHex("#57f287"),
        Color.ParseHex("#fee75c"),
        Color.ParseHex("#eb459e"),
        Color.ParseHex("#ed4245"),
        Color.ParseHex("#f47b67"),
        Color.ParseHex("#45ddc0"),
        Color.ParseHex("#9b84ec"),
        Color.ParseHex("#7d8d8e"),
        Color.ParseHex("#2a6dbf"),
    };

    private static void DrawStoryProgressionBars(
        IImageProcessingContext ctx,
        IReadOnlyList<(string Label, int Count)> storyProgression,
        int y)
    {
        ctx.DrawText("📖 ストーリー進行度分布", _headerFont, TextColor, new PointF(Padding, y - 30));

        var maxCount = Math.Max(1, storyProgression.Max(s => s.Count));
        var barAreaX = Padding + 160;
        var barAreaWidth = Width - Padding * 2 - 160 - 80;
        var barHeight = 28;
        var barGap = 6;

        var total = storyProgression.Sum(s => s.Count);

        for (int i = 0; i < storyProgression.Count; i++)
        {
            var (label, count) = storyProgression[i];
            var barY = y + i * (barHeight + barGap);
            var color = StoryColors[i % StoryColors.Length];

            var displayLabel = label.Length > 12 ? label[..12] : label;
            ctx.DrawText(displayLabel, _labelFont, TextColor, new PointF(Padding, barY + 5));

            var barWidth = (float)count / maxCount * barAreaWidth;
            if (barWidth < 4) barWidth = 4;
            var barRect = new RectangleF(barAreaX, barY + 2, barWidth, barHeight - 4);
            ctx.Fill(color, BuildRoundedRect(barRect, 6));

            var pct = total > 0 ? (int)Math.Round(100.0 * count / total) : 0;
            var valText = $"{count}人 ({pct}%)";
            ctx.DrawText(valText, _labelFont, TextColor, new PointF(barAreaX + barWidth + 8, barY + 5));
        }
    }

    private static IPath BuildRoundedRect(RectangleF rect, float radius)
    {
        var path = new PathBuilder();
        path.AddArc(new Vector2(rect.X + radius, rect.Y + radius), radius, radius, 0, 180, 90);
        path.AddLine(new Vector2(rect.X + rect.Width - radius, rect.Y), new Vector2(rect.X + rect.Width - radius, rect.Y));
        path.AddArc(new Vector2(rect.X + rect.Width - radius, rect.Y + radius), radius, radius, 0, 90, 90);
        path.AddLine(new Vector2(rect.X + rect.Width, rect.Y + rect.Height - radius), new Vector2(rect.X + rect.Width, rect.Y + rect.Height - radius));
        path.AddArc(new Vector2(rect.X + rect.Width - radius, rect.Y + rect.Height - radius), radius, radius, 0, 0, 90);
        path.AddLine(new Vector2(rect.X + radius, rect.Y + rect.Height), new Vector2(rect.X + radius, rect.Y + rect.Height));
        path.AddArc(new Vector2(rect.X + radius, rect.Y + rect.Height - radius), radius, radius, 0, 270, 90);
        path.CloseFigure();
        return path.Build();
    }

    public static Stream GenerateStoryProgressionImage(
        string guildName,
        IReadOnlyList<(string Label, int Count)> storyProgression)
    {
        var barHeight = 32;
        var barGap = 10;
        var headerHeight = 100;
        var footerHeight = 50;
        var barAreaPadding = 20;
        var totalHeight = headerHeight + storyProgression.Count * (barHeight + barGap) + barAreaPadding + footerHeight;
        if (totalHeight < 200) totalHeight = 200;

        using var image = new Image<Rgba32>(Width, totalHeight, BackgroundColor);

        image.Mutate(ctx =>
        {
            var titleText = $"📖 {guildName} - ストーリー進行度統計";
            ctx.DrawText(titleText, _titleFont, TextColor, new PointF(Padding, Padding));

            var now = DateTimeOffset.UtcNow;
            var dateText = now.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy/MM/dd HH:mm JST");
            ctx.DrawText(dateText, _labelFont, SubtextColor, new PointF(Padding, Padding + 36));

            if (storyProgression.Count == 0)
            {
                ctx.DrawText("データがありません", _labelFont, SubtextColor, new PointF(Padding, headerHeight));
                return;
            }

            var total = storyProgression.Sum(s => s.Count);
            var maxCount = Math.Max(1, storyProgression.Max(s => s.Count));
            var barAreaX = Padding + 160;
            var barAreaWidth = Width - Padding * 2 - 160 - 80;

            ctx.DrawText("ストーリー進行度分布", _headerFont, TextColor, new PointF(Padding, headerHeight - 30));

            for (int i = 0; i < storyProgression.Count; i++)
            {
                var (label, count) = storyProgression[i];
                var barY = headerHeight + i * (barHeight + barGap);
                var color = StoryColors[i % StoryColors.Length];

                var displayLabel = label.Length > 12 ? label[..12] : label;
                ctx.DrawText(displayLabel, _labelFont, TextColor, new PointF(Padding, barY + 7));

                var barWidth = (float)count / maxCount * barAreaWidth;
                if (barWidth < 4) barWidth = 4;
                var barRect = new RectangleF(barAreaX, barY + 2, barWidth, barHeight - 4);
                ctx.Fill(color, BuildRoundedRect(barRect, 6));

                var pct = total > 0 ? (int)Math.Round(100.0 * count / total) : 0;
                var valText = $"{count}人 ({pct}%)";
                ctx.DrawText(valText, _labelFont, TextColor, new PointF(barAreaX + barWidth + 8, barY + 7));
            }

            var footerY = totalHeight - 30;
            ctx.DrawText($"合計: {total}人", _labelFont, AccentColor, new PointF(Padding, footerY));
            ctx.DrawText("RaidDC Bot", _labelFont, SubtextColor, new PointF(Width - Padding - 80, footerY));
        });

        var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        stream.Position = 0;
        return stream;
    }
}
