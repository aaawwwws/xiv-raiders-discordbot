namespace RaidDcBot.Bot;

public static class RecruitmentFlexEngine
{
    public static bool CanIncludeHostPositionInCreatePrefillSelector(
        IReadOnlyList<string> requestedPositions,
        string? hostPositionRaw,
        string? hostFlexSpec,
        IReadOnlyList<string> orderedPositions)
    {
        if (requestedPositions.Count == 0 ||
            string.IsNullOrWhiteSpace(hostPositionRaw) ||
            string.IsNullOrWhiteSpace(hostFlexSpec))
        {
            return false;
        }

        var hostPosition = hostPositionRaw.Trim().ToUpperInvariant();
        if (!requestedPositions.Contains(hostPosition, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var hostFlexPositions = ResolveFlexCandidatePositions(hostFlexSpec, orderedPositions)
            .Select(position => position.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (hostFlexPositions.Length < 2 ||
            !hostFlexPositions.Contains(hostPosition, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return hostFlexPositions.Any(position =>
            !string.Equals(position, hostPosition, StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildCreatePreFilledSummary(
        IReadOnlyList<string> positions,
        IReadOnlyDictionary<string, string> preFilledSlots,
        IReadOnlyDictionary<string, RecruitPreFilledFlexSlot> preFilledFlexSlots,
        IReadOnlyDictionary<string, RecruitPreFilledMember> preFilledMembers,
        string? hostPosition)
    {
        var lines = new List<string>();
        var normalizedHostPosition = hostPosition?.Trim().ToUpperInvariant();
        foreach (var position in positions)
        {
            var isHostPosition = !string.IsNullOrWhiteSpace(normalizedHostPosition) &&
                                 string.Equals(position, normalizedHostPosition, StringComparison.OrdinalIgnoreCase);

            if (preFilledMembers.TryGetValue(position, out var preFilledMember) &&
                !string.IsNullOrWhiteSpace(preFilledMember.Job))
            {
                var memberJobs = ParseFlexJobCodes(preFilledMember.FlexJobsSpec);
                if (memberJobs.Count == 0)
                {
                    memberJobs = new[] { preFilledMember.Job };
                }

                var memberJobLabel = string.Join('/', memberJobs);
                var flexSpec = NormalizeFlexSpecForDisplay(preFilledMember.FlexSpec, positions);
                if (!string.IsNullOrWhiteSpace(flexSpec))
                {
                    lines.Add($"{BuildFlexEmbedPositionLabel(flexSpec, positions)}:{preFilledMember.UserName}({memberJobLabel})");
                }
                else
                {
                    if (isHostPosition)
                    {
                        continue;
                    }

                    lines.Add($"{position}:{preFilledMember.UserName}({memberJobLabel})");
                }

                continue;
            }

            if (preFilledFlexSlots.TryGetValue(position, out var preFilledFlexSlot) &&
                !string.IsNullOrWhiteSpace(preFilledFlexSlot.Job))
            {
                var flexJobs = ParseFlexJobCodes(preFilledFlexSlot.FlexJobsSpec);
                if (flexJobs.Count == 0)
                {
                    flexJobs = new[] { preFilledFlexSlot.Job };
                }

                var flexJobLabel = string.Join('/', flexJobs);
                var flexSpec = NormalizeFlexSpecForDisplay(preFilledFlexSlot.FlexSpec, positions);
                if (!string.IsNullOrWhiteSpace(flexSpec))
                {
                    lines.Add($"{BuildFlexEmbedPositionLabel(flexSpec, positions)}:外部({flexJobLabel})");
                }
                else
                {
                    if (isHostPosition)
                    {
                        continue;
                    }

                    lines.Add($"{position}:外部({flexJobLabel})");
                }

                continue;
            }

            if (isHostPosition)
            {
                continue;
            }

            if (!preFilledSlots.TryGetValue(position, out var job) || string.IsNullOrWhiteSpace(job))
            {
                continue;
            }

            lines.Add($"{position}:{job}");
        }

        if (lines.Count == 0)
        {
            return "なし";
        }

        return string.Join(", ", lines);
    }

    private static string? NormalizeFlexSpecForDisplay(string? flexSpec, IReadOnlyList<string> orderedPositions)
    {
        if (string.IsNullOrWhiteSpace(flexSpec))
        {
            return null;
        }

        var positions = ResolveFlexCandidatePositions(flexSpec, orderedPositions);
        if (positions.Count == 0)
        {
            return null;
        }

        return string.Join(',',
            positions
                .Select(position => position.Trim().ToUpperInvariant())
                .Where(position => !string.IsNullOrWhiteSpace(position))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildFlexEmbedPositionLabel(string flexSpec, IReadOnlyList<string> orderedPositions)
    {
        var requested = ParseFlexSpecPositions(flexSpec);
        if (requested.Count == 0 || requested.Contains("ANY", StringComparer.OrdinalIgnoreCase))
        {
            return "FLEX";
        }

        var orderedSet = new HashSet<string>(orderedPositions, StringComparer.OrdinalIgnoreCase);
        var positions = requested
            .Where(position => orderedSet.Contains(position))
            .ToArray();
        if (positions.Length == 0)
        {
            positions = requested.ToArray();
        }

        return $"FLEX({string.Join('/', positions)})";
    }

    private static IReadOnlyList<string> ResolveFlexCandidatePositions(string flexSpec, IReadOnlyList<string> orderedPositions)
    {
        if (orderedPositions.Count == 0)
        {
            return Array.Empty<string>();
        }

        var requested = ParseFlexSpecPositions(flexSpec);
        if (requested.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (requested.Contains("ANY", StringComparer.OrdinalIgnoreCase))
        {
            return orderedPositions;
        }

        var orderedSet = new HashSet<string>(orderedPositions, StringComparer.OrdinalIgnoreCase);
        return requested
            .Where(position => orderedSet.Contains(position))
            .ToArray();
    }

    private static IReadOnlyList<string> ParseFlexSpecPositions(string? flexSpec)
    {
        if (string.IsNullOrWhiteSpace(flexSpec))
        {
            return Array.Empty<string>();
        }

        var normalized = flexSpec.Trim();
        if (normalized.StartsWith("FLEX:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[5..].Trim();
        }
        else if (normalized.StartsWith("FLEX(", StringComparison.OrdinalIgnoreCase) &&
                 normalized.EndsWith(")", StringComparison.Ordinal))
        {
            normalized = normalized[5..^1].Trim();
        }
        else if (string.Equals(normalized, "FLEX", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "ANY";
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Array.Empty<string>();
        }

        return normalized
            .Split(new[] { ',', '/', '|', '，', '／', '｜', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(position => position.Trim().ToUpperInvariant())
            .Where(position => !string.IsNullOrWhiteSpace(position))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ParseFlexJobCodes(string? jobsSpec)
    {
        if (string.IsNullOrWhiteSpace(jobsSpec))
        {
            return Array.Empty<string>();
        }

        return jobsSpec
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(job => job.Trim().ToUpperInvariant())
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
