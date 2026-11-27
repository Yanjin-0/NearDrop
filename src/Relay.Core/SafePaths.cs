using System.Text.RegularExpressions;

namespace Relay.Core;

public static partial class SafePaths
{
    public static void NoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (current != null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("不接受符號連結、接合點或雲端佔位檔：" + Path.GetFileName(current));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
    public static string Relative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 28000 || Path.IsPathRooted(relative)) throw new IOException("無效的相對路徑。");
        foreach (var segment in relative.Split(['/', '\\']))
        {
            var stem = segment.Split('.')[0];
            if (segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Reserved().IsMatch(stem))
                throw new IOException("拒絕不安全的檔名。");
        }
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(full, root)) throw new IOException("路徑超出允許範圍。");
        NoLinks(full); return full;
    }
    public static bool Within(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)) return false;
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
    public static bool Overlap(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase) || Within(a, b) || Within(b, a);
    public static IEnumerable<string> Walk(string directory)
    {
        NoLinks(directory);
        var stack = new Stack<string>(); stack.Push(directory);
        while (stack.TryPop(out var dir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                NoLinks(entry); yield return entry;
                if (Directory.Exists(entry)) stack.Push(entry);
            }
        }
    }
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reserved();
}
