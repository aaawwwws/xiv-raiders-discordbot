using System.Linq;

namespace RaidDcBot.Services;

public static class RecruitmentRules {
    private static readonly IReadOnlyList<JobOption> TankJobs = new[]
    {
        new JobOption("ナ", "ナイト"),
        new JobOption("戦", "戦士"),
        new JobOption("暗", "暗黒騎士"),
        new JobOption("ガ", "ガンブレイカー")
    };

    private static readonly IReadOnlyList<JobOption> HealerJobs = new[]
    {
        new JobOption("白", "白魔道士"),
        new JobOption("学", "学者"),
        new JobOption("占", "占星術師"),
        new JobOption("賢", "賢者")
    };

    private static readonly IReadOnlyList<JobOption> MeleeJobs = new[]
    {
        new JobOption("モ", "モンク"),
        new JobOption("竜", "竜騎士"),
        new JobOption("忍", "忍者"),
        new JobOption("侍", "侍"),
        new JobOption("リ", "リーパー"),
        new JobOption("ヴ", "ヴァイパー")
    };

    private static readonly IReadOnlyList<JobOption> RangedJobs = new[]
    {
        new JobOption("詩", "詩人"),
        new JobOption("機", "機工士"),
        new JobOption("踊", "踊り子")
    };

    private static readonly IReadOnlyList<JobOption> CasterJobs = new[]
    {
        new JobOption("黒", "黒魔道士"),
        new JobOption("召", "召喚士"),
        new JobOption("赤", "赤魔道士"),
        new JobOption("ピ", "ピクトマンサー")
    };

    private static readonly IReadOnlyList<JobOption> MeleeCasterJobs = MeleeJobs.Concat(CasterJobs).ToArray();

    public static readonly IReadOnlyList<string> Positions = new[]
    {
        "MT", "ST", "H1", "H2", "D1", "D2", "D3", "D4"
    };

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<JobOption>> JobsByPosition =
        new Dictionary<string, IReadOnlyList<JobOption>>(StringComparer.OrdinalIgnoreCase) {
            ["MT"] = TankJobs,
            ["ST"] = TankJobs,
            ["H1"] = HealerJobs,
            ["H2"] = HealerJobs,
            ["D1"] = MeleeJobs,
            ["D2"] = MeleeCasterJobs,
            ["D3"] = RangedJobs,
            ["D4"] = CasterJobs
        };

    public static bool TryGetJobs(string position, out IReadOnlyList<JobOption> jobs)
        => JobsByPosition.TryGetValue(position, out jobs!);
}

public sealed record JobOption(string Code, string Name);
