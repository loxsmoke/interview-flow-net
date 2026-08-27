using System.Text;

namespace InterviewFlow.Core.State;

/// <summary>
/// Write-to-temp-then-rename, matching the original's tempfile.mkstemp +
/// os.replace (and openlogi-net's SaveAtomic): a crash mid-write never leaves a
/// truncated data file behind.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = Path.Combine(dir ?? ".", Path.GetRandomFileName() + ".tmp");
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                // Best-effort temp cleanup; the original exception matters more.
            }

            throw;
        }
    }
}
