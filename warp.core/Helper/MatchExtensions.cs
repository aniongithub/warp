using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Warp.Core.Helper;
    // Extension method for wildcard matching
public static class WildcardExtensions
{
    public static bool MatchesWildcard(this IEnumerable<string> patterns, string input, string wildcard)
    {
        if (patterns == null || !patterns.Any())
            return false;

        foreach (var pattern in patterns)
        {
            if (pattern == wildcard) // Exact match with wildcard
                return true;

            var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            if (Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }
}
