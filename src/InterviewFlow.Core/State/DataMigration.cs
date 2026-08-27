namespace InterviewFlow.Core.State;

/// <summary>The five phases of the data-folder move (docs/08 §8.5).</summary>
public enum MigrationPhase
{
    Confirm,
    Copy,
    Verify,
    SaveConfig,
    DeleteOriginals,
    Done,
}

/// <summary>One migration step's outcome; Error is null on success.</summary>
public sealed record MigrationStepResult(MigrationPhase Phase, bool Ok, string? Error = null);

/// <summary>
/// Data-folder migration (ports /api/data/copy, /verify, /delete-originals,
/// /apply-location). Deliberately ordered so a failure before SaveConfig leaves
/// the config pointing at the original folder — no data loss.
/// </summary>
public static class DataMigration
{
    /// <summary>The *.json files a migration moves, sorted by name.</summary>
    public static List<string> ListDataFiles(string dir)
    {
        if (!Directory.Exists(dir))
            return [];
        return Directory.EnumerateFiles(dir, "*.json")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when both paths resolve to the same directory.</summary>
    public static bool IsSameDirectory(string a, string b)
    {
        try
        {
            var fa = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            var fb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
            return string.Equals(fa, fb, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Phase 2 — copy every data file into the destination.</summary>
    public static MigrationStepResult Copy(string fromDir, string toDir, IReadOnlyList<string> files)
    {
        if (toDir.Trim().Length == 0)
            return new MigrationStepResult(MigrationPhase.Copy, false, "Destination path is required.");
        if (IsSameDirectory(fromDir, toDir))
            return new MigrationStepResult(MigrationPhase.Copy, false,
                "Destination is the same as the current data directory.");

        try
        {
            Directory.CreateDirectory(toDir);
        }
        catch (Exception ex)
        {
            return new MigrationStepResult(MigrationPhase.Copy, false,
                $"Cannot create destination directory: {ex.Message}");
        }

        foreach (var name in files)
        {
            try
            {
                File.Copy(Path.Combine(fromDir, name), Path.Combine(toDir, name), overwrite: true);
            }
            catch (Exception ex)
            {
                return new MigrationStepResult(MigrationPhase.Copy, false, $"Failed to copy {name}: {ex.Message}");
            }
        }

        return new MigrationStepResult(MigrationPhase.Copy, true);
    }

    /// <summary>Phase 3 — byte-for-byte verification of every copied file.</summary>
    public static MigrationStepResult Verify(string fromDir, string toDir, IReadOnlyList<string> files)
    {
        foreach (var name in files)
        {
            byte[] src, dst;
            try
            {
                src = File.ReadAllBytes(Path.Combine(fromDir, name));
            }
            catch (Exception ex)
            {
                return new MigrationStepResult(MigrationPhase.Verify, false,
                    $"Cannot read original file {name}: {ex.Message}");
            }

            try
            {
                dst = File.ReadAllBytes(Path.Combine(toDir, name));
            }
            catch (Exception ex)
            {
                return new MigrationStepResult(MigrationPhase.Verify, false,
                    $"Cannot read copied file {name}: {ex.Message}");
            }

            if (!src.AsSpan().SequenceEqual(dst))
            {
                return new MigrationStepResult(MigrationPhase.Verify, false,
                    $"Content mismatch for {name}: files are not identical.");
            }
        }

        return new MigrationStepResult(MigrationPhase.Verify, true);
    }

    /// <summary>Phase 5 — remove the originals once the copy is verified and saved.</summary>
    public static MigrationStepResult DeleteOriginals(string fromDir, IReadOnlyList<string> files)
    {
        var errors = new List<string>();
        foreach (var name in files)
        {
            var path = Path.Combine(fromDir, name);
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }

        return errors.Count > 0
            ? new MigrationStepResult(MigrationPhase.DeleteOriginals, false, string.Join("\n", errors))
            : new MigrationStepResult(MigrationPhase.DeleteOriginals, true);
    }
}
