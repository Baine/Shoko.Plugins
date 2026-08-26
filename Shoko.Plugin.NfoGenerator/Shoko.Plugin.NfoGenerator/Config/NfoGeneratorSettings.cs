using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Shoko.Abstractions.Config;

namespace Shoko.Plugin.NfoGenerator.Config;

/// <summary>
/// Plugin settings surfaced in the Shoko WebUI (registered automatically as a
/// plugin <see cref="IConfiguration"/>).
/// </summary>
public sealed class NfoGeneratorSettings : IConfiguration
{
    /// <summary>
    /// Priority, comma-separated list of languages for titles. Tokens are
    /// language codes (e.g. de-DE, en-US, ja-JP, x-jat), "shoko" for Shoko's
    /// preferred title, or "original" for the source default.
    /// </summary>
    [Display(
        Name = "Title Language",
        Description = "Priority, comma separated: language codes (de-DE, en-US, ja-JP, x-jat), 'shoko' for Shoko's preferred title, 'original' for the source default. Falls back to the next token.")]
    [DefaultValue("shoko")]
    public string TitleLanguage { get; set; } = "shoko";

    /// <summary>
    /// Priority, comma-separated list of languages for descriptions/plots.
    /// Same tokens as <see cref="TitleLanguage"/>.
    /// </summary>
    [Display(
        Name = "Description Language",
        Description = "Priority, comma separated. Same tokens as Title Language.")]
    [DefaultValue("shoko")]
    public string DescriptionLanguage { get; set; } = "shoko";

    /// <summary>Generate NFO files whenever a release is matched by Shoko.</summary>
    [Display(
        Name = "Generate On Import",
        Description = "Generate NFO files whenever a video file is matched to metadata.")]
    [DefaultValue(true)]
    public bool GenerateOnImport { get; set; } = true;

    /// <summary>Regenerate NFO files whenever series metadata is updated.</summary>
    [Display(
        Name = "Generate On Metadata Update",
        Description = "Regenerate NFO files when series metadata changes. Unchanged files are not rewritten.")]
    [DefaultValue(true)]
    public bool GenerateOnMetadataUpdate { get; set; } = true;
}
