using System.Xml.Linq;
using InterviewFlow.MacPackaging;

namespace InterviewFlow.Tests.App;

public sealed class UpdaterPackagingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "if-packaging-" + Guid.NewGuid());
    private static string PublicKey => Convert.ToBase64String(new byte[32]);
    private static string Signature => Convert.ToBase64String(new byte[64]);

    public UpdaterPackagingTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    public void Configure_preserves_metadata_and_is_idempotent(string rid)
    {
        var file = WritePlist();
        UpdaterPackaging.Configure(file, rid, PublicKey, "owner/repo");
        UpdaterPackaging.Configure(file, rid, PublicKey, "owner/repo");
        var dict = XDocument.Load(file).Root!.Element("dict")!;
        XElement Value(string key) => dict.Elements("key").Single(k => k.Value == key).ElementsAfterSelf().First();
        Assert.Equal("1.2.3", Value("CFBundleVersion").Value);
        Assert.Equal("kept", Value("Nested").Element("string")!.Value);
        Assert.Equal(PublicKey, Value("SUPublicEDKey").Value);
        Assert.Equal($"https://github.com/owner/repo/releases/latest/download/appcast-{rid}.xml", Value("SUFeedURL").Value);
        Assert.Equal("true", Value("SUEnableAutomaticChecks").Name.LocalName);
        Assert.Equal("true", Value("SUVerifyUpdateBeforeExtraction").Name.LocalName);
        Assert.Equal("false", Value("SUAutomaticallyUpdate").Name.LocalName);
    }

    [Theory]
    [InlineData("win-x64", "valid", "owner/repo")]
    [InlineData("osx-arm64", "", "owner/repo")]
    [InlineData("osx-arm64", "invalid!", "owner/repo")]
    [InlineData("osx-arm64", "valid", "owner/repo?redirect=x")]
    public void Invalid_configuration_does_not_change_file(string rid, string key, string repo)
    {
        var file = WritePlist();
        var original = File.ReadAllBytes(file);
        Assert.Throws<ArgumentException>(() => UpdaterPackaging.Configure(file, rid, key == "valid" ? PublicKey : key, repo));
        Assert.Equal(original, File.ReadAllBytes(file));
    }

    [Fact]
    public void Verify_accepts_signed_enclosure_with_encoded_filename()
    {
        var (feed, archive) = WriteFeed(Signature, "7", "https://github.com/owner/repo/releases/download/v1/test%20app.zip");
        UpdaterPackaging.Verify(feed, archive);
    }

    [Theory]
    [InlineData("", "7", "https://github.com/test%20app.zip")]
    [InlineData("invalid!", "7", "https://github.com/test%20app.zip")]
    [InlineData("valid", "8", "https://github.com/test%20app.zip")]
    [InlineData("valid", "bad", "https://github.com/test%20app.zip")]
    [InlineData("valid", "7", "http://github.com/test%20app.zip")]
    [InlineData("valid", "7", "https://example.com/test%20app.zip")]
    [InlineData("valid", "7", "https://github.com/other.zip")]
    public void Verify_rejects_unsigned_wrong_size_or_wrong_url(string signature, string size, string url)
    {
        var (feed, archive) = WriteFeed(signature == "valid" ? Signature : signature, size, url);
        Assert.Throws<InvalidDataException>(() => UpdaterPackaging.Verify(feed, archive));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Verify_requires_exactly_one_enclosure(int count)
    {
        var (feed, archive) = WriteFeed(Signature, "7", "https://github.com/test%20app.zip");
        var document = XDocument.Load(feed);
        var item = document.Root!.Element("channel")!.Element("item")!;
        if (count == 0) item.RemoveNodes();
        else item.Add(new XElement(item.Element("enclosure")!));
        document.Save(feed);
        Assert.Throws<InvalidDataException>(() => UpdaterPackaging.Verify(feed, archive));
    }

    private string WritePlist()
    {
        var file = Path.Combine(_directory, "Info.plist");
        File.WriteAllText(file, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>CFBundleVersion</key><string>1.2.3</string>
            <key>Nested</key><array><string>kept</string></array></dict></plist>
            """);
        return file;
    }

    private (string Feed, string Archive) WriteFeed(string signature, string size, string url)
    {
        var archive = Path.Combine(_directory, "test app.zip");
        File.WriteAllText(archive, "archive");
        var feed = Path.Combine(_directory, "appcast.xml");
        XNamespace sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";
        new XDocument(new XElement("rss", new XElement("channel", new XElement("item",
            new XElement("enclosure", new XAttribute(sparkle + "edSignature", signature),
                new XAttribute("length", size), new XAttribute("url", url)))))).Save(feed);
        return (feed, archive);
    }
}
