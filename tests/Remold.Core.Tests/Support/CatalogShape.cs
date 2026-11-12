using System.Text.RegularExpressions;

namespace Remold.Core.Tests.Support;

/// <summary>Matches text against a catalog entry whose arguments a test cannot name in full: format the entry
/// with <see cref="Any"/> in each such hole, and <see cref="Of"/> turns the result into a pattern that takes
/// any text there and the entry's own words everywhere else.</summary>
internal static class CatalogShape
{
    /// <summary>An argument the pattern leaves open.</summary>
    public const string Any = "\u0001";

    /// <summary>A pattern matching the whole of any text the entry formats to.</summary>
    public static Regex Of(string formatted) =>
        new("^" + Regex.Escape(formatted).Replace(Any, "(.+?)") + "$", RegexOptions.Singleline);

    /// <summary>A pattern matching the entry anywhere inside a longer text.</summary>
    public static Regex Within(string formatted) =>
        new(Regex.Escape(formatted).Replace(Any, "(.+?)"), RegexOptions.Singleline);
}
