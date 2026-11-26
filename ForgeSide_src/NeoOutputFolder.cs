namespace ForgeSide;

/// <summary>
/// 「NEO 到底往哪儿出图」这件事只该有一个答案。
///
/// 以前这个判断散在三处：图库按 webui/ 在不在二选一，输出监控按候选顺序挨个试，
/// 启动时建目录又是第三种写法。结果就是监控盯着的目录和图库读的目录可能不是同一个——
/// 新图既不响提示音、也永远不会出现在图库里，而用户只能想到「去设置里刷新一下」。
/// 桃华把三处合成这一份，以后要改判定条件，改这里就够了。
/// </summary>
public static class NeoOutputFolder
{
    /// <summary>各整合包约定俗成的出图目录，按可信度排序</summary>
    private static readonly string[] Candidates =
    {
        Path.Combine("webui", "output", "images"),   // 第三方整合包
        Path.Combine("core",  "output", "images"),   // licyk 版
        "outputs",                                   // 官方版
        "output",                                    // 其它魔改
    };

    /// <summary>
    /// 实际存在的出图目录；都不存在时按包结构给出最可能的那个。
    /// 返回的一定是绝对路径——它会被当成缓存里的 folder 键比较，
    /// 「C:\\a\\b」和「c:/a/b」在这里是两张表，马虎不得
    /// </summary>
    public static string Find(string neoRoot)
    {
        foreach (string rel in Candidates)
        {
            string dir = Path.GetFullPath(Path.Combine(neoRoot, rel));
            if (Directory.Exists(dir)) return dir;
        }
        return Path.GetFullPath(Path.Combine(neoRoot, ExpectedSubPath(neoRoot)));
    }

    /// <summary>
    /// 出图目录的上一层：images / grids 都挂在它底下。
    /// 启动时要建的正是这两个子目录
    /// </summary>
    public static string ParentOf(string neoRoot)
    {
        string images = Find(neoRoot);
        return Path.GetFileName(images).Equals("images", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(images)!
            : images;
    }

    /// <summary>
    /// 什么都还没建的时候往哪儿猜。webui/ 是第三方包与官方包天然的分界，
    /// 这个判定跟老版本图库完全一致——不能让老用户升级后图库突然空掉
    /// </summary>
    private static string ExpectedSubPath(string neoRoot)
        => Directory.Exists(Path.Combine(neoRoot, "webui"))
            ? Candidates[0]
            : Candidates[2];
}
