using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace MacosUtils.Core;

/// <summary>
/// Reads property lists (LaunchAgents, Info.plist, SystemVersion.plist). XML plists are parsed
/// directly; binary ones are converted with /usr/bin/plutil first. Values become plain .NET types:
/// dict → Dictionary&lt;string, object?&gt;, array → List&lt;object?&gt;, string, long, double, bool,
/// DateTime and byte[].
/// </summary>
public static class Plist
{
    private const string PlistTool = "/usr/bin/plutil";

    // DTDs are never fetched: a plist is data, and resolving its DOCTYPE would reach apple.com.
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
    };

    /// <summary>The root value of the plist at <paramref name="path"/>, or null when it cannot be read.</summary>
    public static object? Load(string path)
    {
        var root = LoadXml(path);
        var value = root?.Elements().FirstOrDefault();
        return value is null ? null : Convert(value);
    }

    /// <summary>The root dictionary, or null when the file is missing, unreadable or not a dictionary.</summary>
    public static Dictionary<string, object?>? LoadDictionary(string path) =>
        Load(path) as Dictionary<string, object?>;

    /// <summary>Top-level string values of a plist (all others are left out).</summary>
    public static Dictionary<string, string> ReadTopLevelStrings(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in LoadDictionary(path) ?? [])
        {
            if (value is string text)
                result[key] = text;
        }
        return result;
    }

    /// <summary>Parses XML plist text; public for tests.</summary>
    public static object? Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings);
        var value = XDocument.Load(reader).Root?.Elements().FirstOrDefault();
        return value is null ? null : Convert(value);
    }

    private static XElement? LoadXml(string path)
    {
        try
        {
            string text;
            using (var stream = File.OpenRead(path))
            {
                var header = new byte[8];
                var read = stream.Read(header, 0, header.Length);
                var isBinary = read >= 6 && System.Text.Encoding.ASCII.GetString(header, 0, 6) == "bplist";
                if (isBinary)
                {
                    stream.Close();
                    text = Processes.TryRun(PlistTool, ["-convert", "xml1", "-o", "-", path]) ?? "";
                }
                else
                {
                    stream.Position = 0;
                    using var streamReader = new StreamReader(stream);
                    text = streamReader.ReadToEnd();
                }
            }
            if (text.Length == 0)
                return null;
            using var reader = XmlReader.Create(new StringReader(text), ReaderSettings);
            return XDocument.Load(reader).Root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    private static object? Convert(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "dict":
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                string? key = null;
                foreach (var child in element.Elements())
                {
                    if (child.Name.LocalName == "key")
                        key = child.Value;
                    else if (key is not null)
                    {
                        dict[key] = Convert(child);
                        key = null;
                    }
                }
                return dict;
            }
            case "array":
                return element.Elements().Select(Convert).ToList();
            case "string":
                return element.Value;
            case "integer":
                return long.TryParse(element.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0L;
            case "real":
                return double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? real : 0.0;
            case "true":
                return true;
            case "false":
                return false;
            case "date":
                return DateTime.TryParse(element.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date) ? date : null;
            case "data":
                try
                {
                    return System.Convert.FromBase64String(string.Concat(element.Value.Where(c => !char.IsWhiteSpace(c))));
                }
                catch (FormatException)
                {
                    return null;
                }
            default:
                return null;
        }
    }
}
