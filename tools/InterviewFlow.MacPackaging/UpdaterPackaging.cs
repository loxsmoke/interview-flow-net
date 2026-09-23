using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace InterviewFlow.MacPackaging;

/// <summary>Build-time helpers only; this project is not referenced by the app.</summary>
public static class UpdaterPackaging
{
    public static void Configure(string plistPath, string rid, string publicKey, string repository)
    {
        if (rid is not ("osx-arm64" or "osx-x64"))
            throw new ArgumentException("Unsupported macOS runtime");
        if (!IsBase64(publicKey, 32))
            throw new ArgumentException("SPARKLE_PUBLIC_ED_KEY must be a base64-encoded 32-byte public key");
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid GitHub repository");

        // Our bundle template is an XML plist. Ignore its external Apple DTD;
        // packaging must not depend on fetching it from the network.
        var document = ReadXml(plistPath);
        var dictionary = document.Root?.Name == "plist" ? document.Root.Element("dict") : null;
        if (dictionary is null)
            throw new InvalidDataException("Expected an XML plist with a root dictionary");
        Set("SUFeedURL", new XElement("string", $"https://github.com/{repository}/releases/latest/download/appcast-{rid}.xml"));
        Set("SUPublicEDKey", new XElement("string", publicKey));
        Set("SUEnableAutomaticChecks", new XElement("true"));
        Set("SUAutomaticallyUpdate", new XElement("false"));
        Set("SUVerifyUpdateBeforeExtraction", new XElement("true"));
        document.Save(plistPath);

        void Set(string name, XElement value)
        {
            var keys = dictionary.Elements("key").Where(k => k.Value == name).ToArray();
            if (keys.Length > 1)
                throw new InvalidDataException($"Duplicate plist key: {name}");
            if (keys.Length == 0)
            {
                dictionary.Add(new XElement("key", name), value);
                return;
            }
            var previous = keys[0].ElementsAfterSelf().FirstOrDefault();
            if (previous is null || previous.Name == "key")
                throw new InvalidDataException($"Missing plist value for {name}");
            previous.ReplaceWith(value);
        }
    }

    // This checks the generated feed's structure, not cryptographic validity.
    // Sparkle's generator signs using the key matching the bundle's public key;
    // Sparkle verifies the signature again on the user's Mac before extraction.
    public static void Verify(string feed, string archive)
    {
        var document = ReadXml(feed);
        var enclosures = document.Root?.Name == "rss"
            ? document.Root.Elements("channel").Elements("item").Elements("enclosure").ToArray()
            : [];
        if (enclosures.Length != 1)
            throw new InvalidDataException("Expected exactly one full update");
        var enclosure = enclosures[0];
        XNamespace sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";
        if (!IsBase64((string?)enclosure.Attribute(sparkle + "edSignature") ?? "", 64))
            throw new InvalidDataException("Missing or invalid update signature");
        if (!long.TryParse((string?)enclosure.Attribute("length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
            length != new FileInfo(archive).Length)
            throw new InvalidDataException("Incorrect update size");
        if (!Uri.TryCreate((string?)enclosure.Attribute("url"), UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" ||
            Uri.UnescapeDataString(url.AbsolutePath.Split('/')[^1]) != Path.GetFileName(archive))
            throw new InvalidDataException("Incorrect update URL");
    }

    private static XDocument ReadXml(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        });
        return XDocument.Load(reader);
    }

    private static bool IsBase64(string value, int size)
    {
        if (value.Any(char.IsWhiteSpace)) return false;
        Span<byte> bytes = stackalloc byte[size];
        return Convert.TryFromBase64String(value, bytes, out var count) && count == size;
    }
}
