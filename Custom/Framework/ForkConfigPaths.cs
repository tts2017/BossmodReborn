using System.IO;
using Dalamud.Plugin;

namespace BossMod;

// This fork keeps its settings next to Dalamud's default ones but under its own names (BossModReborn_re.json and the
// BossModReborn_re folder), so another BossMod Reborn build installed side by side does not read or overwrite them.
// On the first start the default file and folder are copied over once (the replays are left where they are).
public static class ForkConfigPaths
{
    public const string Suffix = "_re";

    public static DirectoryInfo Directory { get; private set; } = null!;
    public static FileInfo File { get; private set; } = null!;

    public static void Initialize(IDalamudPluginInterface dalamud)
    {
        var baseDir = dalamud.ConfigDirectory;
        var baseFile = dalamud.ConfigFile;
        Directory = new(baseDir.FullName + Suffix);
        File = new(Path.Combine(baseFile.DirectoryName!, Path.GetFileNameWithoutExtension(baseFile.Name) + Suffix + baseFile.Extension));

        if (!File.Exists && baseFile.Exists)
            baseFile.CopyTo(File.FullName);
        if (!Directory.Exists)
        {
            Directory.Create();
            if (baseDir.Exists)
                CopyContents(baseDir, Directory, isRoot: true);
        }
        Directory.Refresh();
        File.Refresh();
    }

    private static void CopyContents(DirectoryInfo source, DirectoryInfo target, bool isRoot)
    {
        foreach (var file in source.GetFiles())
            file.CopyTo(Path.Combine(target.FullName, file.Name), false);
        foreach (var dir in source.GetDirectories())
        {
            if (isRoot && dir.Name.Equals("replays", StringComparison.OrdinalIgnoreCase))
                continue;
            CopyContents(dir, target.CreateSubdirectory(dir.Name), isRoot: false);
        }
    }
}
