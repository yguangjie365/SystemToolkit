namespace SystemToolkit.Core.Utilities;

/// <summary>路径越界守卫工具：解压/恢复/外部路径拼接前校验目标落在允许根目录内。</summary>
public static class PathUtil
{
    /// <summary>取全路径并把分隔符统一为反斜杠、去掉末尾分隔符，供前缀比对用。</summary>
    public static string NormalizeDirectory(string path)
    {
        return Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>判断 path 是否位于 root 目录之内（不区分大小写，非法路径按不在处理）。</summary>
    public static bool IsUnder(string path, string root)
    {
        try
        {
            string text = NormalizeDirectory(path) + Path.DirectorySeparatorChar;
            string value = NormalizeDirectory(root) + Path.DirectorySeparatorChar;
            return text.StartsWith(value, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>判断 target 是否落在任一 basePaths 之内或与之相等（空基路径跳过）。</summary>
    public static bool IsUnderAny(string target, IEnumerable<string> basePaths)
    {
        foreach (string basePath in basePaths)
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                continue;
            }
            try
            {
                if (IsUnder(target, basePath))
                {
                    return true;
                }
                string a = NormalizeDirectory(basePath);
                string b = NormalizeDirectory(target);
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
            }
        }
        return false;
    }

    /// <summary>校验相对路径是否安全：非空、非根、无盘符、无".. "回退段。</summary>
    public static bool IsSafeRelativePath(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel))
        {
            return false;
        }
        if (rel.StartsWith('/') || rel.StartsWith('\\'))
        {
            return false;
        }
        if (rel.Contains(':'))
        {
            return false;
        }
        if (Path.IsPathRooted(rel))
        {
            return false;
        }
        // 【P3.3 防御纵深】连续斜杠 a//b/c 会被 Split 拆出空串，空串再 == ".." 是 false，
        // 但 Path.Combine("root", "a//b/../c") 会把 ".." 当回退（空段被跳过处理）。
        // 加 RemoveEmptyEntries 把空段去掉，让 "a//b/../c" 的每一段真实语义都被检查。
        // 三重校验（IsUnderAny + SystemCriticalPath + 本段）仍兜底，此处纯纵深增强——
        // 即使将来有人重构移除前两层，本段也能独立守住回退路径。
        if (rel.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any((string p) => p == ".."))
        {
            return false;
        }
        return true;
    }
}
