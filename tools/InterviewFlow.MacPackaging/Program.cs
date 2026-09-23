namespace InterviewFlow.MacPackaging;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["configure", var plist, var rid]:
                    UpdaterPackaging.Configure(plist, rid,
                        Environment.GetEnvironmentVariable("SPARKLE_PUBLIC_ED_KEY") ?? "",
                        Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "loxsmoke/interview-flow-net");
                    break;
                case ["verify", var feed, var archive]:
                    UpdaterPackaging.Verify(feed, archive);
                    break;
                default:
                    Console.Error.WriteLine("Usage: MacPackaging configure <Info.plist> <osx-arm64|osx-x64> | verify <appcast.xml> <archive.zip>");
                    return 2;
            }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"macOS packaging: {ex.Message}");
            return 1;
        }
    }
}
