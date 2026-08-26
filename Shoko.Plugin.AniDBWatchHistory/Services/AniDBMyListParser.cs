using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Shoko.Plugin.AniDBWatchHistory.Models;

namespace Shoko.Plugin.AniDBWatchHistory.Services;

public sealed class AniDBMyListParser
{
    private static readonly string[] DateFormats =
    [
        "dd-MM-yyyy HH:mm", "dd-MM-yyyy HH:mm:ss",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssK",
        "dd.MM.yyyy HH:mm", "dd/MM/yyyy HH:mm"
    ];

    public async Task<(List<AniDBWatchRecord> Records, int Total, int NoDate, int InvalidDate)>
        ParseAsync(Stream xml, CancellationToken cancellationToken)
    {
        var records = new List<AniDBWatchRecord>();
        var total = 0;
        var noDate = 0;
        var invalidDate = 0;
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        using var reader = XmlReader.Create(xml, settings);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "file") continue;
            total++;
            using var subtree = reader.ReadSubtree();
            var fileElement = await XElement.LoadAsync(
                subtree, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in fileElement.Elements())
                values[element.Name.LocalName] = element.Value.Trim();

            values.TryGetValue("viewdate", out var rawDate);
            if (string.IsNullOrWhiteSpace(rawDate) || rawDate is "-" or "N/A")
            {
                noDate++;
                continue;
            }

            if (!TryParseViewDate(rawDate, out var viewDate))
            {
                invalidDate++;
                continue;
            }

            if (!TryInt(values, "fid", out var fid) || !TryInt(values, "ep_id", out var eid))
            {
                invalidDate++;
                continue;
            }

            records.Add(new(
                fid,
                eid,
                TryInt(values, "aid", out var aid) ? aid : null,
                Value(values, "a_title_eng") ?? Value(values, "aname"),
                Value(values, "crc"),
                DateTime.SpecifyKind(viewDate, DateTimeKind.Local)));
        }

        return (records, total, noDate, invalidDate);
    }

    private static bool TryParseViewDate(string value, out DateTime result)
        => DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture,
               DateTimeStyles.AllowWhiteSpaces, out result)
           || DateTime.TryParse(value, CultureInfo.GetCultureInfo("nl-NL"),
               DateTimeStyles.AllowWhiteSpaces, out result);

    private static bool TryInt(IReadOnlyDictionary<string, string> values, string key, out int value)
    {
        value = 0;
        return values.TryGetValue(key, out var text)
               && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static string? Value(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
