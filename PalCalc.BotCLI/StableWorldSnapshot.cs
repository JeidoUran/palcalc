using System;
using System.IO;
using System.Linq;

static class StableWorldSnapshot
{
    public static string Create(string worldDir, bool includeAllPlayers, string? onlyPlayerHex32NoDashesUpper, bool debug)
    {
        var srcLevel = Path.Combine(worldDir, "Level.sav");
        var srcPlayersDir = Path.Combine(worldDir, "Players");

        if (!File.Exists(srcLevel))
            throw new FileNotFoundException("Level.sav not found", srcLevel);
        if (!Directory.Exists(srcPlayersDir))
            throw new DirectoryNotFoundException($"Players dir not found: {srcPlayersDir}");

        var tmpRoot = Path.Combine(worldDir, "_palcalc_tmp");
        Directory.CreateDirectory(tmpRoot);

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
            // Copie le .sav du joueur + le _dps.sav du joueur si présent
            var wanted = onlyPlayerHex32NoDashesUpper.Trim().ToUpperInvariant();
            playerFiles = playerFiles
                .Where(p =>
                {
                    var name = Path.GetFileName(p).ToUpperInvariant();
                    return name == (wanted + ".SAV") || name == (wanted + "_DPS.SAV");
                })
                .ToList();
        }
        else
        {
            // Tout copier (inclut aussi *_dps.sav)
            // Rien à faire
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
