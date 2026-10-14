using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SbConsole.Plugins.Sqlite.Query;

/// <summary>
/// How user SQL appears in audit rows (design spec §11.4): a short SHA-256 that identifies the exact
/// script text, and its first 200 characters on one line (whitespace runs collapsed).
/// </summary>
internal static partial class QueryAudit
{
    public const int PreviewLength = 200;

    public static string Hash(string sql) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)))[..12];

    public static string Preview(string sql)
    {
        var line = Whitespace().Replace(sql.Trim(), " ");
        if (line.Length <= PreviewLength)
        {
            return line;
        }

        // Never cut between the halves of a surrogate pair.
        var cut = char.IsHighSurrogate(line[PreviewLength - 1]) ? PreviewLength - 1 : PreviewLength;
        return line[..cut] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
