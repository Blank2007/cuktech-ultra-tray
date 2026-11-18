namespace CuktechTraySetup;

internal static class Paths
{
    /// <summary>展开成绝对路径并去掉末尾多余的分隔符（盘符根除外）。</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        while (full.Length > root.Length &&
               (full[^1] == Path.DirectorySeparatorChar || full[^1] == Path.AltDirectorySeparatorChar))
        {
            full = full[..^1];
        }
        return full;
    }

    /// <summary>a 是否等于 dir 或位于 dir 之下。</summary>
    public static bool IsSameOrUnder(string path, string dir)
    {
        var p = Normalize(path);
        var d = Normalize(dir);
        if (p.Equals(d, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>明显不能当安装目录的位置：磁盘根、Windows 目录、用户目录本身。</summary>
    public static string? UnsafeReason(string dir)
    {
        var full = Normalize(dir);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        if (full.Length <= root.TrimEnd(Path.DirectorySeparatorChar).Length)
            return "不能直接把磁盘根目录当作安装位置。";

        if (File.Exists(full)) return "这个路径已经是一个文件。";

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && IsSameOrUnder(full, windows))
            return "不能安装到 Windows 系统目录里。";

        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(system32) && IsSameOrUnder(full, system32))
            return "不能安装到 System32 里。";

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) &&
            Normalize(profile).Equals(full, StringComparison.OrdinalIgnoreCase))
            return "请选一个具体的子目录，而不是整个用户目录。";

        return null;
    }

    /// <summary>沿着目录往上找到第一个真实存在的祖先，用于写权限探测。</summary>
    public static string ExistingAncestor(string dir)
    {
        var cur = Normalize(dir);
        while (cur.Length > 0)
        {
            if (Directory.Exists(cur)) return cur;
            var parent = Path.GetDirectoryName(cur);
            if (string.IsNullOrEmpty(parent) || parent == cur) return cur;
            cur = parent;
        }
        return dir;
    }
}
