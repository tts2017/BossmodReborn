using System.IO;

namespace BossMod;

// Writes of the automatic timelines' files (summary caches, zone files, the report): the contents go to a temp file that is then
// moved over the target, so nothing ever reads half a file. When a step fails the temp file is removed and the failure still
// reaches the caller.
public static class ReplacingFile
{
    public static void WriteAllText(string path, string contents)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }
}
