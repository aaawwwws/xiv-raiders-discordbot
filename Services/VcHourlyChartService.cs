using RaidDcBot.Data;
using RaidDcBot.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RaidDcBot.Services;

public sealed class VcHourlyChartService
{
    private readonly Db _db;

    private static readonly string[] PreferredFonts =
    [
        "Yu Gothic UI", "Meiryo", "BIZ UDPGothic",
        "Noto Sans CJK JP", "Noto Sans JP", "Source Han Sans JP",
        "IPAexGothic", "IPAGothic", "TakaoPGothic", "VL Gothic",
        "DejaVu Sans", "Liberation Sans", "Noto Sans", "FreeSans",
        "Arial", "Helvetica"
    ];

    public VcHourlyChartService(Db db) => _db = db;

    public async Task<byte[]?> BuildHourlyAverageChartAsync(ulong guildId, int days = 7)
    {
        var data = await _db.ListVoiceHourlyAverageAsync(guildId, DateTimeOffset.UtcNow.AddDays(-days), DateTimeOffset.UtcNow);
        return data.Count == 0 ? null : BuildSmoothChart(data, days);
    }

    private static byte[]? BuildSmoothChart(IReadOnlyList<VoiceHourlyAverage> data, int days)
    {
        const int w = 1920, h = 1080;
        const int ml = 160, mr = 120, mt = 200, mb = 180;
        const int cw = w - ml - mr, ch = h - mt - mb;

        var max = Math.Max(1, data.Max(d => d.AverageUsers));
        var scale = Math.Max(5, Math.Ceiling(max * 1.3));
        var peaks = data.OrderByDescending(d => d.AverageUsers).Take(3).Select(d => d.Hour).ToList();

        using var img = new Image<Rgba32>(w, h, new Rgba32(255, 255, 255, 255));
        try
        {
            var fTitle = GetFont(72, FontStyle.Bold);
            var fSub = GetFont(36, FontStyle.Regular);
            var fAxis = GetFont(24, FontStyle.Bold);
            var fVal = GetFont(20, FontStyle.Bold);
            var fHour = GetFont(26, FontStyle.Bold);
            var fPeak = GetFont(28, FontStyle.Bold);

            // Title
            var t = "VC 平均参加者数";
            var ts = Measure(t, fTitle);
            Text(img, t, fTitle, new(15, 23, 42), (w - ts.Width) / 2, 50);

            t = $"過去{days}日間の時間帯別分析";
            ts = Measure(t, fSub);
            Text(img, t, fSub, new(100, 116, 139), (w - ts.Width) / 2, 135);

            // Chart area background
            DrawRoundedRect(img, ml - 20, mt - 20, cw + 40, ch + 40, new(250, 251, 252), 16);

            // Grid lines
            for (int i = 0; i <= 5; i++)
            {
                int y = mt + ch - (int)(i * ch / 5.0);
                DrawDashedLine(img, ml, y, ml + cw, y, new(226, 232, 240), 2);
                var vt = $"{scale * i / 5:0}";
                var vs = Measure(vt, fAxis);
                Text(img, vt, fAxis, new(148, 163, 184), ml - vs.Width - 30, y - vs.Height / 2);
            }

            // Y axis label
            var yl = "平均参加者数";
            var yls = Measure(yl, fAxis);
            Text(img, yl, fAxis, new(100, 116, 139), 40, mt + ch / 2 + (int)(yls.Width / 2));

            // Calculate points
            double step = (double)cw / 23;
            var points = new List<(int x, int y, double val, int hour)>();
            for (int i = 0; i < 24; i++)
            {
                var entry = data.FirstOrDefault(d => d.Hour == i);
                double val = entry?.AverageUsers ?? 0;
                int px = ml + (int)(i * step);
                int py = mt + ch - (int)(val / scale * ch);
                points.Add((px, py, val, i));
            }

            // Draw smooth area
            if (points.Count > 1)
            {
                DrawSmoothArea(img, points, ch, mt, ml, cw);
                DrawSmoothLine(img, points, new(59, 130, 246), 6);
            }

            // Hour labels and peak dots
            for (int i = 0; i < points.Count; i++)
            {
                var (px, py, val, hour) = points[i];
                bool isPeak = peaks.Contains(hour);

                // Hour label
                var ht = $"{hour:00}";
                var hs = Measure(ht, fHour);
                var hc = isPeak ? new Rgba32(239, 68, 68) : new Rgba32(100, 116, 139);
                var hf = isPeak ? GetFont(26, FontStyle.Bold) : fHour;
                Text(img, ht, hf, hc, px - (int)(hs.Width / 2), mt + ch + 20);

                // Value above point
                if (val > 0)
                {
                    var vt = $"{val:0.0}";
                    var vs = Measure(vt, fVal);
                    Text(img, vt, fVal, new(71, 85, 105), px - (int)(vs.Width / 2), py - vs.Height - 12);
                }

                // Peak highlight dot
                if (isPeak)
                {
                    DrawCircle(img, px, py, 10, new(239, 68, 68));
                    DrawCircle(img, px, py, 6, new(255, 255, 255));
                }
                else if (val > 0)
                {
                    DrawCircle(img, px, py, 6, new(59, 130, 246));
                }
            }

            // X axis label
            var xl = "時間帯 (時)";
            var xls = Measure(xl, fAxis);
            Text(img, xl, fAxis, new(100, 116, 139), (w - xls.Width) / 2, h - 60);

            // Peak info box
            var peakText = $"🔥 ピーク: {string.Join(", ", peaks.OrderBy(h => h).Select(h => $"{h:00}:00"))}";
            var peakSize = Measure(peakText, fPeak);
            DrawRoundedRect(img, 80, h - 120, (int)peakSize.Width + 40, 50, new(254, 242, 242), 12);
            Text(img, peakText, fPeak, new(239, 68, 68), 100, h - 112);

            // Average info box
            double avg = data.Average(d => d.AverageUsers);
            var avgText = $"📊 全体平均 {avg:0.0}人";
            var avgSize = Measure(avgText, fPeak);
            DrawRoundedRect(img, w - 80 - (int)avgSize.Width - 40, h - 120, (int)avgSize.Width + 40, 50, new(239, 246, 255), 12);
            Text(img, avgText, fPeak, new(59, 130, 246), w - 60 - (int)avgSize.Width, h - 112);

            using var ms = new MemoryStream();
            img.SaveAsPng(ms, new PngEncoder());
            return ms.ToArray();
        }
        catch { return null; }
    }

    private static void DrawSmoothArea(Image<Rgba32> img, List<(int x, int y, double val, int hour)> points, int ch, int mt, int ml, int cw)
    {
        var path = new List<(int x, int y)>();
        
        for (int i = 0; i < points.Count - 1; i++)
        {
            var p0 = i > 0 ? points[i - 1] : points[i];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = i < points.Count - 2 ? points[i + 2] : p2;

            for (double t = 0; t < 1; t += 0.05)
            {
                double x = CatmullRom(t, p0.x, p1.x, p2.x, p3.x);
                double y = CatmullRom(t, p0.y, p1.y, p2.y, p3.y);
                path.Add(((int)x, (int)y));
            }
        }
        path.Add((points[^1].x, points[^1].y));

        // Fill gradient area
        for (int py = mt; py < mt + ch; py++)
        {
            var intersections = new List<int>();
            for (int i = 0; i < path.Count - 1; i++)
            {
                if ((path[i].y <= py && path[i + 1].y > py) || (path[i].y > py && path[i + 1].y <= py))
                {
                    if (path[i + 1].y != path[i].y)
                    {
                        int ix = path[i].x + (path[i + 1].x - path[i].x) * (py - path[i].y) / (path[i + 1].y - path[i].y);
                        intersections.Add(ix);
                    }
                }
            }

            intersections.Sort();
            for (int i = 0; i < intersections.Count - 1; i += 2)
            {
                int x1 = Math.Max(ml, intersections[i]);
                int x2 = Math.Min(ml + cw, intersections[i + 1]);
                
                for (int px = x1; px <= x2; px++)
                {
                    double ratio = (double)(py - mt) / ch;
                    byte r = (byte)(239 + (255 - 239) * ratio);
                    byte g = (byte)(68 + (255 - 68) * ratio);
                    byte b = (byte)(68 + (255 - 68) * ratio);
                    byte a = (byte)(255 * (1 - ratio * 0.9));
                    img[px, py] = new Rgba32(r, g, b, a);
                }
            }
        }
    }

    private static void DrawSmoothLine(Image<Rgba32> img, List<(int x, int y, double val, int hour)> points, Rgba32 color, int thickness)
    {
        for (int i = 0; i < points.Count - 1; i++)
        {
            var p0 = i > 0 ? points[i - 1] : points[i];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = i < points.Count - 2 ? points[i + 2] : p2;

            int prevX = p1.x, prevY = p1.y;
            for (double t = 0.05; t <= 1; t += 0.05)
            {
                int x = (int)CatmullRom(t, p0.x, p1.x, p2.x, p3.x);
                int y = (int)CatmullRom(t, p0.y, p1.y, p2.y, p3.y);
                DrawThickLine(img, prevX, prevY, x, y, color, thickness);
                prevX = x; prevY = y;
            }
        }
    }

    private static double CatmullRom(double t, double p0, double p1, double p2, double p3)
    {
        return 0.5 * (
            (2 * p1) +
            (-p0 + p2) * t +
            (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t +
            (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t
        );
    }

    private static void DrawThickLine(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 c, int th)
    {
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while (true)
        {
            for (int t = -th / 2; t <= th / 2; t++)
                for (int u = -th / 2; u <= th / 2; u++)
                {
                    int px = x0 + t, py = y0 + u;
                    if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                        img[px, py] = c;
                }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    private static void DrawDashedLine(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 c, int th)
    {
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy, step = 0;
        while (true)
        {
            if (step % 12 < 8)
            {
                for (int t = -th / 2; t <= th / 2; t++)
                {
                    int px = x0 + (dx > dy ? 0 : t), py = y0 + (dx > dy ? t : 0);
                    if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                        img[px, py] = c;
                }
            }
            step++;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    private static void DrawRoundedRect(Image<Rgba32> img, int x, int y, int w, int h, Rgba32 c, int r)
    {
        for (int py = y; py < y + h; py++)
            for (int px = x; px < x + w; px++)
            {
                if (px < 0 || px >= img.Width || py < 0 || py >= img.Height) continue;
                bool corner = false;
                if (py < y + r && px < x + r)
                {
                    int dx = px - (x + r), dy = py - (y + r);
                    corner = dx * dx + dy * dy > r * r;
                }
                else if (py < y + r && px >= x + w - r)
                {
                    int dx = px - (x + w - r - 1), dy = py - (y + r);
                    corner = dx * dx + dy * dy > r * r;
                }
                else if (py >= y + h - r && px < x + r)
                {
                    int dx = px - (x + r), dy = py - (y + h - r - 1);
                    corner = dx * dx + dy * dy > r * r;
                }
                else if (py >= y + h - r && px >= x + w - r)
                {
                    int dx = px - (x + w - r - 1), dy = py - (y + h - r - 1);
                    corner = dx * dx + dy * dy > r * r;
                }
                if (!corner) img[px, py] = c;
            }
    }

    private static void DrawCircle(Image<Rgba32> img, int cx, int cy, int r, Rgba32 c)
    {
        for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++)
                if (x * x + y * y <= r * r)
                {
                    int px = cx + x, py = cy + y;
                    if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                        img[px, py] = c;
                }
    }

    private static Font GetFont(float size, FontStyle style)
    {
        foreach (var name in PreferredFonts)
            if (SystemFonts.TryGet(name, out var family))
                try { return family.CreateFont(size, style); } catch { }
        foreach (var family in SystemFonts.Collection.Families)
            try { return family.CreateFont(size, style); } catch { }
        throw new InvalidOperationException("No usable fonts");
    }

    private static FontRectangle Measure(string text, Font font) =>
        TextMeasurer.MeasureBounds(text, new TextOptions(font));

    private static void Text(Image<Rgba32> img, string t, Font f, Rgba32 c, float x, float y) =>
        img.Mutate(ctx => ctx.DrawText(t, f, c, new PointF(x, y)));
}
