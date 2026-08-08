using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.NfoGenerator.Config;
using Shoko.Plugin.NfoGenerator.Nfo;

var outputDir = Path.Combine(Path.GetTempPath(), "nfo-generator-selfcheck");
NfoWriter.SelfCheck(outputDir);
LanguageResolverCheck.SelfCheck();
Console.WriteLine($"Self-check passed. Output written to {outputDir}");

internal static class LanguageResolverCheck
{
    private sealed class FakeTitle(string value, string languageCode) : ITitle
    {
        public string Value { get; set; } = value;
        public string LanguageCode { get; set; } = languageCode;
        public string? CountryCode { get; set; }
        public TitleLanguage Language { get; set; }
        public TitleType Type { get; set; }
        public DataSource Source { get; set; }
        public bool Equals(ITitle? other) => other is not null && other.Value == Value;
        public bool Equals(IText? other) => other is not null && other.Value == Value;
    }

    private sealed class FakeTitled : IWithTitles
    {
        public ITitle DefaultTitle { get; set; } = new FakeTitle("", "");
        public ITitle? PreferredTitle { get; set; }
        public IReadOnlyList<ITitle> Titles { get; set; } = [];
        public string Title => PreferredTitle?.Value ?? DefaultTitle.Value;
    }

    private sealed class FakeDescribed : IWithDescriptions
    {
        public IText? DefaultDescription { get; set; }
        public IText? PreferredDescription { get; set; }
        public IReadOnlyList<IText> Descriptions { get; set; } = [];
    }

    public static void SelfCheck()
    {
        var entity = new FakeTitled
        {
            PreferredTitle = new FakeTitle("Shoko Preferred", "en-US"),
            DefaultTitle = new FakeTitle("オリジナル", "ja-JP"),
            Titles =
            [
                new FakeTitle("Die Original", "de-DE"),
                new FakeTitle("The Original", "en-US"),
                new FakeTitle("オリジナル", "ja-JP"),
                new FakeTitle("Genroku Hanami Ondo", "x-jat"),
            ],
        };

        Assert(LanguageResolver.Title(entity, "de-DE") == "Die Original", "first language wins");
        Assert(LanguageResolver.Title(entity, "de-de") == "Die Original", "language codes match case-insensitively");
        Assert(LanguageResolver.Title(entity, "fr-FR, en-US") == "The Original", "falls back to next language");
        Assert(LanguageResolver.Title(entity, "shoko") == "Shoko Preferred", "shoko token uses preferred title");
        Assert(LanguageResolver.Title(entity, "original") == "オリジナル", "original token uses default title");
        Assert(LanguageResolver.Title(entity, "x-jat, original") == "Genroku Hanami Ondo", "custom x- codes match");
        Assert(LanguageResolver.Title(entity, "fr-FR") == "Shoko Preferred", "no match falls back to preferred");
        Assert(LanguageResolver.Title(entity, "") == "Shoko Preferred", "empty chain falls back to preferred");

        var described = new FakeDescribed
        {
            PreferredDescription = new FakeTitle("English plot", "en-US"),
            DefaultDescription = new FakeTitle("Deutsche Handlung", "de-DE"),
            Descriptions =
            [
                new FakeTitle("Deutsche Handlung", "de-DE"),
                new FakeTitle("English plot", "en-US"),
            ],
        };
        Assert(LanguageResolver.Description(described, "de-DE, en-US") == "Deutsche Handlung", "description first language wins");
        Assert(LanguageResolver.Description(described, "fr-FR") == "English plot", "description falls back to preferred");
        Assert(LanguageResolver.Description(described, "original") == "Deutsche Handlung", "description original token works");

        Console.WriteLine("OK LanguageResolver");
    }

    private static void Assert(bool condition, string what)
    {
        if (!condition)
            throw new InvalidOperationException($"LanguageResolver: {what}");
    }
}
