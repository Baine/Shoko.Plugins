namespace Shoko.Plugin.TmdbLinkFixer.Configuration;

public sealed class TmdbLinkFixerSettings
{
    public string ApiCredential { get; set; } = string.Empty;
    public int RequestsPerSecond { get; set; } = 10;

    internal TmdbLinkFixerSettings Clone() => new()
    {
        ApiCredential = ApiCredential,
        RequestsPerSecond = Math.Clamp(RequestsPerSecond, 1, 10),
    };
}

public sealed record TmdbLinkFixerSettingsView(
    bool ApiCredentialConfigured,
    string CredentialType,
    int RequestsPerSecond,
    string SettingsPath);

public sealed class UpdateTmdbLinkFixerSettingsRequest
{
    public string? ApiCredential { get; init; }
    public bool ClearApiCredential { get; init; }
    public int RequestsPerSecond { get; init; } = 10;
}
