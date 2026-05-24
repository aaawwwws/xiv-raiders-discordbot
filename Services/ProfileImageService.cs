using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;

namespace RaidDcBot.Services;

public sealed class ProfileImageService
{
    private readonly Db _db;
    private readonly LodestoneClient _lodestone;
    private readonly JobIconCacheService _jobIcons;
    private readonly ProfileCacheService _cache;
    private readonly DiscordOptions _options;
    private readonly ILogger<ProfileImageService> _logger;

    private static readonly Rgba32 BgColor = new(15, 16, 20);
    private static readonly Rgba32 NeonAccent = new(110, 140, 250); // Muted Blue Accent
    private static readonly Rgba32 NeonDanger = new(255, 100, 100); // Muted Red
    private static readonly Rgba32 TextMain = new(240, 245, 255);
    private static readonly Rgba32 TextMuted = new(100, 110, 130);

    private static readonly (string Label, string[] Jobs, Rgba32 Color)[] JobRoleGroups = new[]
    {
        ("TANK", new[] { "ナイト", "戦士", "暗黒騎士", "ガンブレイカー" }, new Rgba32(59, 130, 246)),
        ("HEALER", new[] { "白魔道士", "学者", "占星術師", "賢者" }, new Rgba32(34, 197, 94)),
        ("MELEE", new[] { "モンク", "竜騎士", "忍者", "侍", "リーパー", "ヴァイパー" }, new Rgba32(239, 68, 68)),
        ("RANGED", new[] { "吟遊詩人", "機工士", "踊り子" }, new Rgba32(234, 179, 8)),
        ("CASTER", new[] { "黒魔道士", "召喚士", "赤魔道士", "ピクトマンサー", "青魔道士" }, new Rgba32(168, 85, 247)),
        ("CRAFT", new[] { "木工師", "鍛冶師", "甲冑師", "裁縫師", "革細工師", "調理師", "錬金術師", "彫金師" }, new Rgba32(249, 115, 22)),
        ("GATHER", new[] { "採掘師", "園芸師", "漁師" }, new Rgba32(20, 184, 166)),
    };

    public ProfileImageService(
        Db db, LodestoneClient lodestone, JobIconCacheService jobIcons,
        ProfileCacheService cache, IOptions<DiscordOptions> options, ILogger<ProfileImageService> logger)
    {
        _db = db; _lodestone = lodestone; _jobIcons = jobIcons;
        _cache = cache; _options = options.Value; _logger = logger;
    }

    private static int EstimateJobSectionHeight(List<LodestoneJobLevel> jobs, int maxW)
    {
        if (jobs.Count == 0) return 0;
        var itemW = 80;
        var itemH = 62;
        var cols = Math.Max(1, maxW / itemW);
        int h = 0;
        foreach (var g in JobRoleGroups)
        {
            var gj = jobs.Where(j => g.Jobs.Contains(j.Name)).ToList();
            if (gj.Count == 0) continue;
            h += 28;
            h += (int)Math.Ceiling((double)gj.Count / cols) * itemH;
            h += 10;
        }
        return h;
    }

    public async Task<Stream> BuildProfileImageAsync(SocketGuildUser user, bool forceUpdate = false)
    {
        var profile = await _db.GetDiscordUserProfileAsync(user.Id);
        var character = await _db.GetMainCharacterAsync(user.Id);
        if (character is null) character = (await _db.ListLinkedCharactersAsync(user.Id)).FirstOrDefault();

        LodestoneCharacterExtrasDetailsSnapshot? extras = null;
        if (character?.LodestoneUrl is not null)
        {
            try
            {
                if (!forceUpdate)
                {
                    var cached = _cache.Get(user.Id);
                    if (cached is not null)
                        extras = new LodestoneCharacterExtrasDetailsSnapshot(
                            new LodestoneJobLevelsSnapshot(LodestonePageDataStatus.Available, cached.JobLevels),
                            new LodestoneCollectibleListSnapshot(LodestonePageDataStatus.Available, cached.MountCount, new List<LodestoneCollectibleItem>()),
                            new LodestoneCollectibleListSnapshot(LodestonePageDataStatus.Available, cached.MinionCount, new List<LodestoneCollectibleItem>()),
                            new LodestoneAchievementListSnapshot(LodestonePageDataStatus.Available, cached.AchievementCount, new List<LodestoneAchievementItem>())
                        );
                }
                if (extras is null)
                {
                    extras = await _lodestone.FetchCharacterExtrasDetailsAsync(character.LodestoneUrl);
                    if (extras is not null)
                        _cache.Set(user.Id, new ProfileCacheEntry
                        {
                            CharacterId = character.LodestoneId, CharacterName = character.Name ?? "",
                            World = character.World, DataCenter = character.DataCenter, PortraitUrl = character.PortraitUrl,
                            JobLevels = extras.JobLevels.Jobs, MountCount = extras.Mounts.Count ?? extras.Mounts.Items.Count,
                            MinionCount = extras.Minions.Count ?? extras.Minions.Items.Count,
                            AchievementCount = extras.Achievements.Count ?? extras.Achievements.Items.Count
                        });
                }
            } catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch Lodestone extras"); }
        }

        int width = 1200;
        int infoX = 80;
        int maxJobW = width - infoX - 40;

        int jobSectionH = 0;
        if (extras?.JobLevels.Jobs is not null)
            jobSectionH = EstimateJobSectionHeight(extras.JobLevels.Jobs.Where(j => j.Level >= 1).ToList(), maxJobW);

        var roles = user.Roles
            .Where(r => r.Id != user.Guild.EveryoneRole.Id && r.Name.StartsWith("[コンテンツ]"))
            .OrderByDescending(r => r.Position)
            .ThenBy(r => r.Id)
            .ToList();

        var fontTitle = LoadFont(44, FontStyle.Bold);
        var fontSub = LoadFont(18, FontStyle.Regular);
        var fontTech = LoadFont(12, FontStyle.Bold);
        var fontTiny = LoadFont(11, FontStyle.Regular);

        int rolesHeight = EstimateRolesHeight(roles, maxJobW, fontTech);
        int height = Math.Max(720, 340 + rolesHeight + jobSectionH + 50);
        
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx => ctx.Fill(BgColor));

        DrawCyberBackground(image, profile, width, height);

        var charName = character?.Name ?? profile?.FfxivName ?? user.DisplayName;
        var charWorld = character?.World ?? profile?.FfxivWorld;
        var charDc = character?.DataCenter ?? profile?.FfxivDataCenter;

        int sx = infoX;
        int sy = 60;

        image.Mutate(ctx => ctx.DrawText(new RichTextOptions(fontTiny) { Origin = new PointF(sx, sy - 20) }, "FFXIV RAIDERS // IDENTIFICATION", NeonAccent));
        image.Mutate(ctx => ctx.DrawText(new RichTextOptions(fontTitle) { Origin = new PointF(sx, sy) }, charName.ToUpper(), TextMain));
        
        if (!string.IsNullOrWhiteSpace(charWorld))
        {
            image.Mutate(ctx => ctx.DrawText(new RichTextOptions(fontSub) { Origin = new PointF(sx, sy + 54) }, $"{charWorld} / {charDc}", new Rgba32(160, 168, 185)));
        }
        
        image.Mutate(ctx => ctx.Fill(new Rgba32(255, 255, 255, 15), new RectangularPolygon(sx, sy + 88, maxJobW, 1)));

        if (roles.Count > 0)
        {
            DrawRoleTags(image, sx, sy + 100, roles, fontTech, maxJobW);
        }

        if (extras is not null)
        {
            DrawCyberCollectibles(image, extras, fontTech, width - 40, 40);
        }

        if (extras?.JobLevels.Jobs is not null)
        {
            await DrawCyberJobsAsync(image, extras.JobLevels.Jobs, fontTech, fontTiny, sx, sy + 100 + rolesHeight + 20, maxJobW);
        }

        var fontFooter = LoadFont(14, FontStyle.Regular);
        var fontFooterSm = LoadFont(11, FontStyle.Regular);
        DrawFooter(image, profile, fontFooter, fontFooterSm, sx, height - 80, maxJobW);

        var ms = new MemoryStream();
        await image.SaveAsPngAsync(ms);
        ms.Position = 0;
        return ms;
    }

    private static float DrawTag(Image<Rgba32> img, int x, int y, string text, Font font, Rgba32 color)
    {
        var sz = TextMeasurer.MeasureSize(text, new TextOptions(font));
        var w = sz.Width + 18;
        var h = 22;
        img.Mutate(ctx =>
        {
            ctx.Fill(new Rgba32(color.R, color.G, color.B, 30), new RectangularPolygon(x, y, w, h));
            ctx.DrawText(new RichTextOptions(font) { Origin = new PointF(x + w / 2f, y + 4), HorizontalAlignment = HorizontalAlignment.Center }, text, color);
        });
        return w;
    }

    private static string GetRoleDisplayName(SocketRole role) => role.Name.Replace("[コンテンツ]", "").Trim();

    private static int EstimateRolesHeight(IReadOnlyList<SocketRole> roles, int maxWidth, Font font)
    {
        if (roles.Count == 0) return 0;
        float tx = 0;
        int rows = 1;
        const int rowHeight = 28;
        foreach (var role in roles.OrderByDescending(r => r.Position))
        {
            var displayName = GetRoleDisplayName(role);
            var sz = TextMeasurer.MeasureSize(displayName, new TextOptions(font));
            var w = sz.Width + 18 + 6;
            if (tx + w > maxWidth && tx > 0)
            {
                tx = 0;
                rows++;
            }
            tx += w;
        }
        return rows * rowHeight;
    }

    private static void DrawRoleTags(Image<Rgba32> img, int x, int startY, IReadOnlyList<SocketRole> roles, Font font, int maxWidth)
    {
        float tx = x;
        int ty = startY;
        const int rowHeight = 28;
        foreach (var role in roles)
        {
            var displayName = GetRoleDisplayName(role);
            var sz = TextMeasurer.MeasureSize(displayName, new TextOptions(font));
            var w = sz.Width + 18;
            const int h = 22;
            if (tx + w > x + maxWidth && tx > x)
            {
                tx = x;
                ty += rowHeight;
            }
            #pragma warning disable CS0618
            var color = new Rgba32(role.Color.R, role.Color.G, role.Color.B);
            #pragma warning restore CS0618
            img.Mutate(ctx =>
            {
                ctx.Fill(new Rgba32(color.R, color.G, color.B, 30), new RectangularPolygon((int)tx, ty, w, h));
                ctx.DrawText(new RichTextOptions(font) { Origin = new PointF(tx + w / 2f, ty + 4), HorizontalAlignment = HorizontalAlignment.Center }, displayName, color);
            });
            tx += w + 6;
        }
    }

    private static void DrawFooter(Image<Rgba32> img, DiscordUserProfile? profile, Font font, Font fontSm, int x, int y, int maxW)
    {
        var bio = !string.IsNullOrWhiteSpace(profile?.Bio) ? profile.Bio : "";
        var xH = !string.IsNullOrWhiteSpace(profile?.XHandle) ? $"@{profile.XHandle}" : "";
        var bsH = !string.IsNullOrWhiteSpace(profile?.BlueskyHandle) ? $"@{profile.BlueskyHandle}" : "";

        if (!string.IsNullOrEmpty(bio))
            img.Mutate(ctx => ctx.DrawText(new RichTextOptions(font) { Origin = new PointF(x, y), WrappingLength = maxW }, bio, new Rgba32(160, 168, 185)));

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(xH)) parts.Add(xH);
        if (!string.IsNullOrEmpty(bsH)) parts.Add(bsH);
        if (parts.Count > 0)
            img.Mutate(ctx => ctx.DrawText(new RichTextOptions(fontSm) { Origin = new PointF(x, y + 24) }, string.Join("  ", parts), TextMuted));
    }

    private static void DrawCyberBackground(Image<Rgba32> img, DiscordUserProfile? profile, int w, int h)
    {
        if (!string.IsNullOrWhiteSpace(profile?.ProfileBgImageUrl))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var bytes = http.GetByteArrayAsync(profile.ProfileBgImageUrl).Result;
                var bg = Image.Load<Rgba32>(bytes);
                bg.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(w, h), Mode = ResizeMode.Crop }));
                img.Mutate(ctx => ctx.DrawImage(bg, new Point(0, 0), 1f));
                img.Mutate(ctx => ctx.Fill(new Rgba32(0, 0, 0, 140)));
            }
            catch { }
        }
        else
        {
            img.Mutate(ctx =>
            {
                var poly1 = new Polygon(new LinearLineSegment(new PointF(w - 300, 0), new PointF(w, 0), new PointF(w, h), new PointF(w - 450, h)));
                ctx.Fill(new Rgba32(30, 32, 40), poly1);
            });
        }

        img.Mutate(ctx =>
        {
            ctx.Fill(NeonAccent, new RectangularPolygon(0, 0, w, 6));
            ctx.Fill(NeonAccent, new RectangularPolygon(0, h - 6, w, 6));
        });
    }

    private static void DrawCyberPortrait(Image<Rgba32> img, Image<Rgba32> portrait, int w, int h)
    {
        portrait.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(550, h), Mode = ResizeMode.Crop }));
        
        using var mask = new Image<Rgba32>(550, h, Color.Transparent);
        var polyMask = new Polygon(new LinearLineSegment(new PointF(0, 0), new PointF(550, 0), new PointF(400, h), new PointF(0, h)));
        mask.Mutate(ctx => ctx.Fill(Color.White, polyMask));
        
        portrait.Mutate(ctx => ctx.DrawImage(mask, new Point(0, 0), PixelColorBlendingMode.Normal, PixelAlphaCompositionMode.DestIn, 1f));
        img.Mutate(ctx => ctx.DrawImage(portrait, new Point(0, 0), 1f));
        
        var linePoly = new Polygon(new LinearLineSegment(new PointF(550, 0), new PointF(556, 0), new PointF(406, h), new PointF(400, h)));
        img.Mutate(ctx => ctx.Fill(NeonAccent, linePoly));
        
        var shadowPoly = new Polygon(new LinearLineSegment(new PointF(556, 0), new PointF(576, 0), new PointF(426, h), new PointF(406, h)));
        img.Mutate(ctx => ctx.Fill(new LinearGradientBrush(new PointF(556, 0), new PointF(576, 0), GradientRepetitionMode.None, new ColorStop(0, new Rgba32(0, 0, 0, 100)), new ColorStop(1, new Rgba32(0, 0, 0, 0))), shadowPoly));
    }

    private static void DrawCyberCollectibles(Image<Rgba32> img, LodestoneCharacterExtrasDetailsSnapshot extras, Font font, int rx, int y)
    {
        var items = new List<(string Label, int Count)>();
        if (extras.Mounts.Status == LodestonePageDataStatus.Available)
            items.Add(("MNT", extras.Mounts.Count ?? extras.Mounts.Items.Count));
        if (extras.Minions.Status == LodestonePageDataStatus.Available)
            items.Add(("MIN", extras.Minions.Count ?? extras.Minions.Items.Count));
        if (extras.Achievements.Status == LodestonePageDataStatus.Available)
            items.Add(("ACH", extras.Achievements.Count ?? extras.Achievements.Items.Count));

        var cy = y + 6;
        foreach (var item in items)
        {
            var text = $"{item.Label} {item.Count}";
            var sz = TextMeasurer.MeasureSize(text, new TextOptions(font));
            var w = sz.Width + 18;
            var bx = rx - w - 10;
            img.Mutate(ctx =>
            {
                ctx.Fill(new Rgba32(255, 255, 255, 8), new RectangularPolygon(bx, cy, w, 22));
                ctx.DrawText(new RichTextOptions(font) { Origin = new PointF(bx + 9, cy + 4) }, text, TextMain);
            });
            cy += 28;
        }
    }

    private async Task DrawCyberJobsAsync(Image<Rgba32> img, IReadOnlyList<LodestoneJobLevel> jobs, Font fontLabel, Font fontTiny, int sx, int sy, int maxW)
    {
        var allJobs = jobs.Where(j => j.Level >= 1).ToList();
        if (allJobs.Count == 0) return;

        var iconSz = 26;
        var itemW = 80;
        var itemH = 62;
        var cols = Math.Max(1, maxW / itemW);
        var curY = sy;

        var icons = new Dictionary<string, Image<Rgba32>?>();
        foreach (var j in allJobs)
            if (!icons.ContainsKey(j.Name))
                icons[j.Name] = await _jobIcons.GetIconAsync(j.Name);

        foreach (var group in JobRoleGroups)
        {
            var gj = allJobs.Where(j => group.Jobs.Contains(j.Name)).OrderByDescending(j => j.Level).ThenBy(j => j.Name).ToList();
            if (gj.Count == 0) continue;

            // ロール色ラベル
            var labelText = group.Label;
            var labelSize = TextMeasurer.MeasureSize(labelText, new TextOptions(fontLabel));
            var badgeW = labelSize.Width + 16;
            var badgeH = 20;
            var badgeX = sx;
            var badgeY = curY;
            img.Mutate(ctx =>
            {
                ctx.Fill(group.Color, new RectangularPolygon(badgeX, badgeY, badgeW, badgeH));
            });
            img.Mutate(ctx => ctx.DrawText(new RichTextOptions(fontLabel) { Origin = new PointF(badgeX + 8, badgeY + 2) }, labelText, Color.White));
            curY += 28;

            for (int i = 0; i < gj.Count; i++)
            {
                var job = gj[i];
                var col = i % cols;
                var row = i / cols;
                var x = sx + col * itemW;
                var y = curY + row * itemH;

                // エアログラス風の統一背景（マックスとか関係なく）
                var glassBg = new Rgba32(255, 255, 255, 10);
                var glassBorder = new Rgba32(255, 255, 255, 20);

                img.Mutate(ctx =>
                {
                    ctx.Fill(glassBg, new RectangularPolygon(x, y, itemW - 6, itemH - 6));
                    ctx.Draw(new SolidPen(glassBorder, 1), new RectangleF(x, y, itemW - 6, itemH - 6));
                });

                var icon = icons.GetValueOrDefault(job.Name);
                if (icon is not null)
                {
                    using var rz = icon.Clone(c => c.Resize(iconSz, iconSz));
                    img.Mutate(ctx => ctx.DrawImage(rz, new Point(x + (itemW - 6 - iconSz) / 2, y + 3), 1f));
                }
                else
                {
                    img.Mutate(ctx => ctx.Fill(NeonAccent, new EllipsePolygon(x + itemW / 2f - 3, y + 3 + iconSz / 2f, iconSz / 2f, iconSz / 2f)));
                }

                img.Mutate(ctx => ctx.DrawText(new RichTextOptions(fontTiny) { Origin = new PointF(x + itemW / 2f - 3, y + iconSz + 8), HorizontalAlignment = HorizontalAlignment.Center }, job.Name, Color.White));
                img.Mutate(ctx => ctx.DrawText(new RichTextOptions(fontTiny) { Origin = new PointF(x + itemW / 2f - 3, y + iconSz + 20), HorizontalAlignment = HorizontalAlignment.Center }, $"{job.Level}", TextMain));
            }

            curY += (int)Math.Ceiling((double)gj.Count / cols) * itemH + 10;
        }

        foreach (var ic in icons.Values) ic?.Dispose();
    }

    private static async Task<Image<Rgba32>?> LoadPortraitAsync(SocketGuildUser user, LinkedCharacter? character)
    {
        var url = !string.IsNullOrWhiteSpace(character?.PortraitUrl) ? character.PortraitUrl : (user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl());
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var bytes = await http.GetByteArrayAsync(url);   
            return Image.Load<Rgba32>(bytes);
        }
        catch { return null; }
    }

    private Font LoadFont(float size, FontStyle style)       
    {
        var systemCandidates = new[] { "Noto Sans CJK JP", "Noto Sans CJK SC" };
        foreach (var candidate in systemCandidates)
        {
            if (SystemFonts.TryGet(candidate, out var sysFamily))
            {
                try { return sysFamily.CreateFont(size, style); } catch { }
                try { return sysFamily.CreateFont(size, FontStyle.Regular); } catch { }
            }
        }
        var paths = new[] { "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc" };
        foreach (var path in paths.Where(File.Exists))       
        {
            try { var collection = new FontCollection(); var family = collection.Add(path); try { return family.CreateFont(size, style); } catch { return family.CreateFont(size, FontStyle.Regular); } } catch { }
        }
        return SystemFonts.CreateFont("DejaVu Sans", size, style);
    }
}
