using System.Globalization;
using System.Text.RegularExpressions;

namespace RetroBat.Api.Infrastructure;

internal static class RetroAchievementsLeaderboardDefinitionRules
{
    private static readonly Regex StartEqualityRegex = new(
        @"(?<![A-Za-z0-9])0x[HhWwXx]?\s*(?<address>[0-9A-Fa-f]+)\s*=\s*(?<value>-?\d+)(?![0-9A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static Dictionary<int, List<RetroAchievementsStoredMemorySelector>> BuildLevelSelectors(
        IEnumerable<RetroAchievementsLeaderboardInfo> leaderboards)
    {
        var parsed = leaderboards.ToDictionary(item => item.Id, item => ParseStartEqualities(item.Definition));
        var discriminatingAddresses = parsed.Values
            .SelectMany(items => items)
            .GroupBy(item => item.Address)
            .Where(group => group.Select(item => item.Value).Distinct().Take(2).Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        return parsed.ToDictionary(
            item => item.Key,
            item => item.Value
                .Where(selector => discriminatingAddresses.Contains(selector.Address))
                .OrderBy(selector => selector.Address)
                .ToList());
    }

    public static IReadOnlyList<RetroAchievementsStoredMemorySelector> ParseStartEqualities(string definition)
    {
        if (string.IsNullOrWhiteSpace(definition)) return Array.Empty<RetroAchievementsStoredMemorySelector>();
        var start = definition.Split(new[] { "::" }, StringSplitOptions.None)[0];
        return StartEqualityRegex.Matches(start)
            .Select(match => new RetroAchievementsStoredMemorySelector
            {
                Address = long.Parse(match.Groups["address"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                Value = long.Parse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            })
            .DistinctBy(item => (item.Address, item.Value))
            .ToList();
    }
}
