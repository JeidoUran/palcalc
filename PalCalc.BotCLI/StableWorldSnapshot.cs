using System;
using System.IO;
using System.Linq;

static class StableWorldSnapshot
{
    // Par défaut : on garde un petit historique (utile en debug). Mets 0 pour tout supprimer.
    const int KeepLastSnapshots = 2;

    public static string Create(
        string worldDir,
        bool includeAllPlayers,
        string? onlyPlayerHex32NoDashesUpper,
        bool debug)
    {
        var srcLevel = Path.Combine(worldDir, "Level.sav");
        var srcPlayersDir = Path.Combine(worldDir, "Players");

        if (!File.Exists(srcLevel))
            throw new FileNotFoundException("Level.sav not found", srcLevel);
        if (!Directory.Exists(srcPlayersDir))
            throw new DirectoryNotFoundException($"Players dir not found: {srcPlayersDir}");

        // ✅ IMPORTANT : tmpRoot en dehors du worldDir => ne gonfle plus tes backups.
        var worldName = new DirectoryInfo(worldDir).Name;
        var tmpRoot = Path.Combine(Path.GetTempPath(), "palcalc_tmp", worldName);
        Directory.CreateDirectory(tmpRoot);

        // ✅ Nettoyage des anciens snapshots
        CleanupOldSnapshots(tmpRoot, keepLast: KeepLastSnapshots, debug: debug);

        var snapshotDir = Path.Combine(tmpRoot, $"world.snapshot.{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}");
        Directory.CreateDirectory(snapshotDir);

        var snapshotPlayersDir = Path.Combine(snapshotDir, "Players");
        Directory.CreateDirectory(snapshotPlayersDir);

        // Level
        CopyStableFile(srcLevel, Path.Combine(snapshotDir, "Level.sav"));

        // Players
        var playerFiles = Directory.EnumerateFiles(srcPlayersDir, "*.sav", SearchOption.TopDirectoryOnly).ToList();

        if (!includeAllPlayers && !string.IsNullOrWhiteSpace(onlyPlayerHex32NoDashesUpper))
        {
            var wanted = onlyPlayerHex32NoDashesUpper.Trim().ToUpperInvariant();
            playerFiles = playerFiles
                .Where(p =>
                {
                    var name = Path.GetFileName(p).ToUpperInvariant();
                    return name == (wanted + ".SAV") || name == (wanted + "_DPS.SAV");
                })
                .ToList();
        }

        foreach (var src in playerFiles)
        {
            var dst = Path.Combine(snapshotPlayersDir, Path.GetFileName(src));
            CopyStableFile(src, dst);
        }

        if (debug)
        {
            Console.Error.WriteLine($"[snapshot] dir={snapshotDir}");
            Console.Error.WriteLine($"[snapshot] playersCopied={playerFiles.Count}");
        }

        return snapshotDir;
    }

    public static void TryDeleteSnapshotDir(string snapshotDir, bool debug = false)
    {
        try
        {
            if (Directory.Exists(snapshotDir))
                Directory.Delete(snapshotDir, recursive: true);

            if (debug)
                Console.Error.WriteLine($"[snapshot] deleted={snapshotDir}");
        }
        catch (Exception ex)
        {
            if (debug)
                Console.Error.WriteLine($"[snapshot] delete failed: {ex.Message}");
        }
    }

    static void CleanupOldSnapshots(string tmpRoot, int keepLast, bool debug)
    {
        try
        {
            if (!Directory.Exists(tmpRoot)) return;

            var dirs = new DirectoryInfo(tmpRoot)
                .EnumerateDirectories("world.snapshot.*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(d => d.CreationTimeUtc)
                .ToList();

            var toDelete = (keepLast <= 0) ? dirs : dirs.Skip(keepLast).ToList();

            foreach (var d in toDelete)
            {
                try
                {
                    d.Delete(recursive: true);
                    if (debug) Console.Error.WriteLine($"[snapshot] cleanup deleted={d.FullName}");
                }
                catch { /* on ignore, c’est du housekeeping */ }
            }
        }
        catch { /* on ignore */ }
    }

    static void CopyStableFile(string srcPath, string dstPath)
    {
        const int attempts = 6;
        Exception? last = null;

        for (int a = 0; a < attempts; a++)
        {
            try
            {
                File.Copy(srcPath, dstPath, overwrite: true);

                long s1 = new FileInfo(dstPath).Length;
                System.Threading.Thread.Sleep(120);
                long s2 = new FileInfo(dstPath).Length;

                if (s1 != s2 || s2 < 64)
                    throw new IOException($"Snapshot unstable for '{Path.GetFileName(srcPath)}' (size {s1} -> {s2}). Server is probably writing.");

                return;
            }
            catch (Exception ex)
            {
                last = ex;
                try { if (File.Exists(dstPath)) File.Delete(dstPath); } catch { }
                System.Threading.Thread.Sleep(180);
            }
        }

        throw new Exception($"Failed to snapshot '{srcPath}' after retries.", last);
    }
}
