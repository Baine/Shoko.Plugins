using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;

namespace Shoko.Plugin.NfoGenerator.Config;

/// <summary>
/// Resolves titles and descriptions against a comma-separated language
/// preference chain. Tokens: language codes (matched against
/// <see cref="IText.LanguageCode"/>), "shoko" (Shoko's preferred value) and
/// "original" (the source default). Each token falls back to the next.
/// </summary>
internal static class LanguageResolver
{
    public static string? Title(IWithTitles entity, string chain)
        => Resolve(chain, entity.PreferredTitle?.Value, entity.DefaultTitle.Value, entity.Titles, t => t.LanguageCode, t => t.Value);

    public static string? Description(IWithDescriptions entity, string chain)
        => Resolve(chain, entity.PreferredDescription?.Value, entity.DefaultDescription?.Value, entity.Descriptions, t => t.LanguageCode, t => t.Value);

    private static string? Resolve<T>(string chain, string? preferred, string? original, IEnumerable<T> items, Func<T, string> getLangCode, Func<T, string> getValue)
    {
        foreach (var token in Parse(chain))
        {
            if (token.Equals("shoko", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(preferred))
                    return preferred;
                continue;
            }
            if (token.Equals("original", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(original))
                    return original;
                continue;
            }
            var match = items.FirstOrDefault(t => getLangCode(t).Equals(token, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(getValue(t)));
            if (match is not null)
                return getValue(match);
        }
        return preferred ?? original ?? items.Select(getValue).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static IEnumerable<string> Parse(string chain)
        => (chain ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
