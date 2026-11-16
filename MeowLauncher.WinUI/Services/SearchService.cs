using MeowLauncher.WinUI.Models;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 全局搜索索引：页面、设置项（来自 SettingsCatalog）、全部实例、当前实例的快捷设置、联网兜底。
/// 规则：多个词用空格分开、每个词都要命中（AND）；没命中的条目一律不出现；
/// 联网兜底永远排在最后且不参与排序；没有任何本地命中时先给一行「没有找到…」占位，再给兜底。
/// 动作仍由 MainWindow.CommitSearchHit 统一执行。
/// </summary>
public static class SearchService
{
    private const int MaxLocalHits = 8;

    private static readonly SearchHit[] PageHits =
    [
        new() { Title = "主页", Group = "页面", Subtitle = "继续玩与组件板", Glyph = "", PageTag = "home", Keywords = "主页 首页 继续玩 组件板 home" },
        new() { Title = "全部实例", Group = "页面", Subtitle = "本地实例、加载器与启动设置", Glyph = "", PageTag = "versions", Keywords = "版本 实例 加载器 forge fabric neoforge quilt 重命名 删除 隐藏" },
        new() { Title = "下载版本", Group = "页面", Subtitle = "Mojang 版本与加载器安装", Glyph = "", PageTag = "download", Keywords = "下载 安装 mojang 版本 加载器 快照 正式版" },
        new() { Title = "在线资源", Group = "页面", Subtitle = "Modrinth / CurseForge", Glyph = "", PageTag = "online", Keywords = "在线 资源 modrinth curseforge 整合包 光影 资源包 数据包" },
        new() { Title = "Mod 管理", Group = "页面", Subtitle = "实例 Mod 扫描、启停与删除", Glyph = "", PageTag = "mods", Keywords = "mod 模组 管理 启停 导入" },
        new() { Title = "实例资源", Group = "页面", Subtitle = "资源包、光影、存档、截图", Glyph = "", PageTag = "assets", Keywords = "资源包 光影 shader resource pack 存档 世界 截图" },
        new() { Title = "日志", Group = "页面", Subtitle = "游戏日志与崩溃报告", Glyph = "", PageTag = "logs", Keywords = "日志 log 崩溃 报错 crash latest.log" },
        new() { Title = "账户", Group = "页面", Subtitle = "正版 / 离线账户与皮肤", Glyph = "", PageTag = "account", Keywords = "账户 账号 登录 正版 离线 皮肤 披风 微软" },
        new() { Title = "设置", Group = "页面", Subtitle = "常规、外观、下载与网络、游戏与进程", Glyph = "", PageTag = "settings", Keywords = "设置 选项 偏好" },
        new() { Title = "任务与通知", Group = "页面", Subtitle = "后台任务与运行状态", Glyph = "", PageTag = "tasks", Keywords = "任务 通知 进度 后台 下载进度" },
    ];

    private static IEnumerable<SearchHit> SettingHits() => SettingsCatalog.All.Select(s => new SearchHit
    {
        Title = $"设置 · {s.Title}",
        Group = "设置",
        Subtitle = s.Subtitle,
        Glyph = s.Glyph,
        Action = SearchActionKind.Navigate,
        PageTag = "settings",
        Focus = s.Focus,
        Keywords = s.Keywords,
    });

    public static IReadOnlyList<SearchHit> Query(Snapshot? snapshot, string section, string query, bool global,
        string? contextVersionId = null)
    {
        var raw = (query ?? "").Trim();
        var tokens = Tokenize(raw);
        var local = new List<(SearchHit Hit, int Score)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versions = snapshot?.Versions.Where(v => v.Valid && !v.Hidden).ToList() ?? [];
        var multiRoot = versions.Select(v => GameDirectoryResolver.Normalize(v.Root)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        var context = versions.FirstOrDefault(v => !string.IsNullOrWhiteSpace(contextVersionId)
                          && string.Equals(v.Id, contextVersionId, StringComparison.OrdinalIgnoreCase))
                      ?? snapshot?.Current;

        void Add(SearchHit hit, int bonus = 0)
        {
            var key = $"{hit.Group}|{hit.Title}|{hit.PageTag}|{hit.VersionId}|{hit.Focus}";
            if (!seen.Add(key)) return;
            var score = Score(hit, tokens);
            if (tokens.Count > 0 && score == 0) return;   // 有关键词时不命中的不出现（旧版会把所有页面当填充塞进来）
            local.Add((hit, score + bonus));
        }

        // —— 当前实例快捷设置：只在有实例时出现
        if (context is not null)
        {
            foreach (var (title, focus, glyph, keywords) in InstanceQuick)
            {
                Add(new SearchHit
                {
                    Title = title, Group = "当前实例", Subtitle = $"{context.Id} · 仅此实例", Glyph = glyph,
                    Action = SearchActionKind.Version, VersionId = context.Id, Focus = focus, Keywords = keywords,
                }, bonus: 5);   // 快捷设置只给一点加权：不让它压过标题完全匹配的页面（搜「设置」时）
            }
        }

        // —— 页面 / 设置：本页作用域下只保留本页相关
        foreach (var p in PageHits) if (global || section == "home" || PageMatchesSection(p.PageTag, section)) Add(p);
        if (global || section == "settings") foreach (var s in SettingHits()) Add(s);

        // —— 全部实例（旧版全局只取前 8 个，30 个实例的用户搜不到后面的）
        foreach (var v in versions)
        {
            Add(new SearchHit
            {
                Title = v.Id, Group = "实例",
                Subtitle = multiRoot ? $"{v.Subtitle} · {Path.GetFileName(v.Root.TrimEnd('\\', '/'))}" : v.Subtitle,
                Glyph = "", Action = SearchActionKind.Version, VersionId = v.Id,
                Keywords = $"{v.BaseVersion} {v.Loader} {v.LoaderVersion} 实例 版本",
            }, bonus: (v.IsCurrent ? 6 : 0) + (v.LastPlayed > 0 ? 3 : 0));
        }
        Add(PickInstanceHit);

        var ordered = local
            .OrderByDescending(x => x.Score)
            .ThenBy(x => GroupRank(x.Hit.Group))
            .Take(tokens.Count == 0 ? 7 : MaxLocalHits)
            .Select(x => x.Hit)
            .ToList();

        if (raw.Length == 0) return Finish(ordered, []);

        // —— 联网兜底：永远在最后，不参与本地排序
        var web = new List<SearchHit>
        {
            new()
            {
                Title = "在“在线资源”中搜索", Group = "联网搜索", Subtitle = $"Modrinth / CurseForge · {raw}", Glyph = "",
                Action = SearchActionKind.Online, PageTag = "online", Focus = raw,
            },
            MakeWebHit("在 MC 百科搜索", raw, "https://search.mcmod.cn/s?key=", ""),
            MakeWebHit("在 Bing 搜索", raw, "https://www.bing.com/search?q=", ""),
        };

        if (ordered.Count == 0)
        {
            ordered.Add(new SearchHit
            {
                Title = $"没有找到与“{raw}”匹配的页面、设置或实例", Group = "无结果",
                Subtitle = "试试更短的词，或用下面的联网搜索", Glyph = "", Action = SearchActionKind.None,
            });
        }
        return Finish(ordered, web);
    }

    private static readonly (string Title, string Focus, string Glyph, string Keywords)[] InstanceQuick =
    [
        ("实例 Java 设置", "java", "", "java版本 java jdk 运行时 路径 java设置"),
        ("实例内存设置", "memory", "", "内存 分配 mem 内存分配"),
        ("实例启动设置", "launch", "", "启动 jvm 参数 启动设置"),
    ];

    private static bool PageMatchesSection(string pageTag, string section) => section switch
    {
        "get" => pageTag is "download" or "online",
        "instance" => pageTag is "mods" or "assets",
        _ => pageTag == section,
    };

    private static readonly SearchHit PickInstanceHit = new()
    {
        Title = "选择其他实例…", Group = "切换", Subtitle = "打开实例列表，或进入全局设置",
        Glyph = "", Action = SearchActionKind.PickInstance, Keywords = "实例 切换 选择 换 全局",
    };

    private static SearchHit MakeWebHit(string prefix, string query, string urlPrefix, string glyph) => new()
    {
        Title = $"{prefix} “{query}”", Group = "联网搜索", Subtitle = "在浏览器中打开", Glyph = glyph,
        Action = SearchActionKind.External, Url = urlPrefix + Uri.EscapeDataString(query),
    };

    private static List<SearchHit> Finish(List<SearchHit> local, List<SearchHit> web)
    {
        var all = local.Concat(web).ToList();
        string? last = null;
        foreach (var hit in all)
        {
            hit.ShowGroupHeader = false;
            if (string.Equals(hit.Group, last, StringComparison.Ordinal)) continue;
            hit.ShowGroupHeader = true;
            hit.GroupHeader = GroupTitle(hit);
            last = hit.Group;
        }
        return all;
    }

    private static int GroupRank(string group) => group switch
    {
        "当前实例" => 0, "页面" => 1, "设置" => 2, "实例" => 3, "切换" => 4, _ => 5,
    };

    private static string GroupTitle(SearchHit hit) => hit.Group switch
    {
        "当前实例" => string.IsNullOrWhiteSpace(hit.VersionId) ? "当前实例" : $"当前实例 · {hit.VersionId}",
        "切换" => "实例",
        "实例" => "实例",
        _ => hit.Group,
    };

    /// <summary>每个词都要命中；返回各词最佳权重的平均值，保持与旧版相近的 0–140 量级。</summary>
    private static int Score(SearchHit hit, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return 1;
        var title = Normalize(hit.Title);
        var subtitle = Normalize(hit.Subtitle);
        var keywords = Normalize(hit.Keywords);
        var group = Normalize(hit.Group);
        var total = 0;
        foreach (var t in tokens)
        {
            var s = title == t ? 140
                : title.StartsWith(t, StringComparison.Ordinal) ? 120
                : title.Contains(t, StringComparison.Ordinal) ? 96
                : keywords.Contains(t, StringComparison.Ordinal) ? 72
                : subtitle.Contains(t, StringComparison.Ordinal) ? 48
                : group.Contains(t, StringComparison.Ordinal) ? 20
                : 0;
            if (s == 0) return 0;
            total += s;
        }
        return total / tokens.Count;
    }

    private static List<string> Tokenize(string raw) => raw
        .Split([' ', '\t', '，', ',', '　'], StringSplitOptions.RemoveEmptyEntries)
        .Select(Normalize).Where(t => t.Length > 0).ToList();

    private static string Normalize(string text)
    {
        Span<char> buffer = stackalloc char[text.Length];
        var n = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            buffer[n++] = char.ToLowerInvariant(ch);
        }
        return new string(buffer[..n]);
    }
}
