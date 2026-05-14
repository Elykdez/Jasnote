using System.Text;
using System.Text.RegularExpressions;

namespace Jesnote.Core;

/// <summary>
/// Used for search patterns
/// </summary>
internal static class Wildcard
{
    /// <summary>
    /// Converts a wildcard pattern (* matches any) into a regex anchored to the
    /// whole string. Matches the Go implementation's wildCardToRegexp.
    /// </summary>
    public static Regex Compile(string pattern, RegexOptions options = RegexOptions.None)
    {
        var parts = pattern.Split('*');
        var sb = new StringBuilder();
        sb.Append('^');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                sb.Append(".*");
            sb.Append(Regex.Escape(parts[i]));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), options | RegexOptions.Compiled);
    }
}
