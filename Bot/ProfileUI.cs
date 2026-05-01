using System;
using System.Collections.Generic;
using Discord;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

public static class ProfileUI
{
    public static MessageComponent BuildDataCenterSelect(Func<string, string> customIdFactory)
    {
        var builder = new ComponentBuilder();
        var names = DcResolver.KnownDcNames;
        for (var i = 0; i < names.Count; i++)
        {
            var dcName = names[i];
            builder.WithButton(
                dcName,
                customIdFactory(dcName),
                ButtonStyle.Primary,
                row: i / 5);
        }

        return builder.Build();
    }

    public static MessageComponent BuildWorldSelect(IReadOnlyList<string> worlds, Func<string, string> customIdFactory)
    {
        var builder = new ComponentBuilder();
        for (var i = 0; i < worlds.Count; i++)
        {
            var world = worlds[i];
            builder.WithButton(
                world,
                customIdFactory(world),
                ButtonStyle.Secondary,
                row: i / 5);
        }

        return builder.Build();
    }

    public static MessageComponent BuildStoryProgressionSelect(Func<string, string> customIdFactory)
    {
        var builder = new ComponentBuilder();
        var options = new[]
        {
            ("arr_hw", "新生〜蒼天", "🌱", ButtonStyle.Success),
            ("sb", "紅蓮", "🔥", ButtonStyle.Danger),
            ("shb", "漆黒", "🌑", ButtonStyle.Secondary),
            ("ew", "暁月", "🌙", ButtonStyle.Primary),
            ("dt", "黄金", "👑", ButtonStyle.Primary),
            ("completed", "最新パッチ済み", "✅", ButtonStyle.Success)
        };

        var index = 0;
        foreach (var (key, label, emoji, style) in options)
        {
            builder.WithButton(
                label,
                customIdFactory(key),
                style,
                emote: new Emoji(emoji),
                row: index / 3);
            index++;
        }

        return builder.Build();
    }
}
