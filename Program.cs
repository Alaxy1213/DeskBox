using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;
using System.Diagnostics;
using Microsoft.Win32;

[DataContract]
public class DItem
{
    [DataMember] public string Name;
    [DataMember] public string Path;
    public bool RemoveFlag;
}

[DataContract]
public class DTab
{
    [DataMember] public string Id;
    [DataMember] public string Name;
    [DataMember] public string Folder;
    [DataMember] public List<DItem> Items;
}

// 单个盒子的外观覆盖。`DBox.Style == null` 表示"全部跟随主题"，
// 也就是老版本的外观——所以老配置读进来不会有任何视觉变化。
// 三层（背景 / 边缘线 / 文字）各有一个 On 开关：开 = 这一层用这里存的值，关 = 跟主题走。
// 数值约定：-1 = 未设置，由 Store.Normalize 填成真实默认值。
// 注意 DataContract 反序列化**不调构造函数**，字段初始值对"JSON 里没这一项"是无效的，
// 所以所有默认值都必须靠 Normalize 兜，不能只靠这里的 = 初始化。
[DataContract]
public class DStyle
{
    [DataMember] public bool BgOn;
    [DataMember] public string BgColor;     // "#RRGGBB"，null = 用主题底色
    [DataMember] public int BgAlpha = -1;   // 0-100，真实不透明度（不再有"亮底偷偷 +26"）

    [DataMember] public bool EdgeOn;
    [DataMember] public string EdgeColor;
    [DataMember] public int EdgeAlpha = -1; // 0-100，0 = 不要边线
    [DataMember] public int EdgeWidth = -1; // 1-3 px

    [DataMember] public bool TextOn;
    [DataMember] public string TextColor;
    [DataMember] public int TextAlpha = -1; // 0-100

    // 标签栏下方那条分隔线（头部与内容区之间的结构边界）。
    // 老版本写死 Tint(30)（30/255 ≈ 12%），所以默认值取 12，观感不变。
    [DataMember] public bool SepOn;
    [DataMember] public string SepColor;
    [DataMember] public int SepAlpha = -1;  // 0-100，0 = 不画这条线

    [DataMember] public int Radius = -1;    // 0-28 px，-1 = 14

    // 结构版本。**加新字段必须同时升版本**：DataContract 反序列化不跑构造函数，
    // 老配置里没有的字段读出来是 0 而不是字段初始值，0 又刚好落在合法区间里，
    // ClampInt 不会纠正它 —— 于是"没写过"会被当成"用户设成 0"。
    // Ver<1 = 2026-10-24 之前（有 Bg/Edge/Text，但还没有分隔线层）。
    // **字段初始化 1**：new 出来的对象是"新结构"，只有反序列化出来的老配置才是 0。
    [DataMember] public int Ver = 1;
}

[DataContract]
public class DBox
{
    [DataMember] public string Id;
    [DataMember] public string Folder;
    [DataMember] public int X;
    [DataMember] public int Y;
    [DataMember] public int W;
    [DataMember] public int H;
    [DataMember] public int Opacity;      // 百分比，保留只为兼容旧配置
    // 不透明度档位 0~5（0 最透、5 最实）。**已停用**，只用于把老配置迁移成 Style.BgAlpha，
    // 迁移完就不再影响绘制。滑块也已经在 2026-10-24 从标题栏删掉了。
    [DataMember] public int OpacityStep = -1;
    [DataMember] public bool Locked;
    [DataMember] public string Title;
    [DataMember] public int Accent;
    [DataMember] public int ThemeId;
    [DataMember] public DStyle Style;
    [DataMember] public List<DTab> Tabs;
}

[DataContract]
public class DRoot
{
    [DataMember] public List<DBox> Boxes;
    [DataMember] public bool NoAutoMigrate;
}

static class Log
{
    public static void W(string s)
    {
        try
        {
            File.AppendAllText(Path.Combine(Store.BaseDir, "deskbox.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + "\r\n");
        }
        catch { }
    }
}

static class Store
{
    public static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
    public static string ConfigPath { get { return Path.Combine(BaseDir, "deskbox.json"); } }
    public static string ItemsRoot { get { return Path.Combine(BaseDir, "items"); } }

    // ================= 目录结构 =================
    // items\<盒子文件夹>\<子盒子文件夹>\<真实文件>
    // 图标指向的就是这里的实体文件，不再引用桌面。

    public static string SafeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "未命名";
        char[] bad = Path.GetInvalidFileNameChars();
        char[] chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(bad, chars[i]) >= 0) chars[i] = '_';
        }
        string r = new string(chars).Trim();
        r = r.TrimEnd('.');
        if (r.Length == 0) r = "未命名";
        if (r.Length > 60) r = r.Substring(0, 60).Trim();
        return r;
    }

    public static string LabelOf(string path)
    {
        try
        {
            string p = (path ?? "").TrimEnd('\\', '/');
            if (p.Length == 0) return "未命名";
            if (Directory.Exists(p)) return Path.GetFileName(p);
            string n = Path.GetFileNameWithoutExtension(p);
            if (string.IsNullOrEmpty(n)) return Path.GetFileName(p);
            return n;
        }
        catch { return "未命名"; }
    }

    public static string BoxDir(DBox b)
    {
        string name = string.IsNullOrEmpty(b.Folder) ? SafeName(b.Title) : b.Folder;
        string d = Path.Combine(ItemsRoot, name);
        try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
        return d;
    }

    public static string TabDir(DBox b, DTab t)
    {
        string name = string.IsNullOrEmpty(t.Folder) ? SafeName(t.Name) : t.Folder;
        string d = Path.Combine(BoxDir(b), name);
        try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
        return d;
    }

    static bool UnderDir(string dir, string path)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(path)) return false;
            string d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            string p = Path.GetFullPath(path);
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool IsManaged(string path)
    {
        return UnderDir(ItemsRoot, path);
    }

    public static bool InDir(string dir, string path)
    {
        return UnderDir(dir, path);
    }

    // 跨盘/跨卷移动：Directory.Move 不支持跨卷，需要退化成 复制 + 删除
    static void MoveEntry(string src, string dest)
    {
        if (Directory.Exists(src))
        {
            try { Directory.Move(src, dest); }
            catch
            {
                CopyTree(src, dest);
                Directory.Delete(src, true);
            }
            return;
        }

        try
        {
            File.Move(src, dest);
        }
        catch (UnauthorizedAccessException)
        {
            // 常见原因：跨卷复制成功但源文件删不掉（只读属性 / 权限 / 安全软件）
            // 这里换一条路径重试，失败时给出可读的原因，并把已复制的副本清掉
            try
            {
                File.Copy(src, dest, false);
                FileAttributes a = File.GetAttributes(src);
                if ((a & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(src, a & ~FileAttributes.ReadOnly);
                File.Delete(src);
            }
            catch (Exception ex2)
            {
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                throw new UnauthorizedAccessException(
                    "已能复制进盒子，但无法从原位置删除「" + src + "」，请检查权限或安全软件保护", ex2);
            }
        }
    }

    static void CopyTree(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string f in Directory.GetFiles(src))
        {
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), false);
        }
        foreach (string d in Directory.GetDirectories(src))
        {
            CopyTree(d, Path.Combine(dest, Path.GetFileName(d)));
        }
    }

    static void CopyEntry(string src, string dest)
    {
        if (Directory.Exists(src)) CopyTree(src, dest);
        else File.Copy(src, dest, false);
    }

    // 把文件/文件夹真正移动到 dir 里（剪切）。成功返回新路径，失败返回 null 并给出原因。
    public static string MoveToDir(string dir, string src, out string err)
    {
        err = null;
        try
        {
            if (string.IsNullOrEmpty(src)) { err = "源路径为空"; return null; }
            if (UnderDir(dir, src)) return src;
            bool isDir = Directory.Exists(src);
            bool isFile = File.Exists(src);
            if (!isDir && !isFile) { err = "源文件已不存在：" + src; return null; }
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string name = Path.GetFileName(src.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) { err = "无法识别的文件名：" + src; return null; }
            string dest = Unique(dir, name);
            MoveEntry(src, dest);
            return dest;
        }
        catch (Exception ex)
        {
            err = ex.Message + "  [" + ex.GetType().Name + "]";
            Log.W("move fail " + src + " -> " + dir + " :: " + ex);
            return null;
        }
    }

    public static string MoveToDesktop(string src, out string err)
    {
        err = null;
        try
        {
            if (string.IsNullOrEmpty(src)) { err = "源路径为空"; return null; }
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (UnderDir(desk, src)) return src;
            bool isDir = Directory.Exists(src);
            bool isFile = File.Exists(src);
            if (!isDir && !isFile) { err = "文件已不存在"; return null; }
            string dest = Unique(desk, Path.GetFileName(src.TrimEnd('\\', '/')));
            MoveEntry(src, dest);
            return dest;
        }
        catch (Exception ex) { err = ex.Message; return null; }
    }

    public static DRoot Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                using (FileStream fs = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read))
                {
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(DRoot));
                    DRoot r = (DRoot)ser.ReadObject(fs);
                    if (r != null && r.Boxes != null && r.Boxes.Count > 0) { Normalize(r); return r; }
                }
            }
        }
        catch { }
        DRoot fresh = new DRoot();
        fresh.Boxes = new List<DBox>();
        DBox b = MakeBox(140, 260, "我的桌面");
        b.Tabs.Add(MakeTab("全部"));
        fresh.Boxes.Add(b);
        Normalize(fresh);
        return fresh;
    }

    // 老的 0~5 档 → 真实不透明度百分比。
    // 老代码里亮色底板会偷偷 +26/255 补实度（因为浅色在亮壁纸上更"薄"），
    // 这里把那个补偿也算进去，迁移后看起来才和迁移前一模一样。
    static int MigratedAlpha(DBox b)
    {
        int st = b.OpacityStep;
        if (st < 0) st = 0;
        if (st > 5) st = 5;
        double eff = LayeredBox.OpacitySteps[st];
        int ti = (b.ThemeId >= 0 && b.ThemeId < Themes.All.Length) ? b.ThemeId : 0;
        Color p = Themes.All[ti].Plate;
        if ((p.R + p.G + p.B) / 3 > 140) eff = Math.Min(1.0, eff + 26 / 255.0);
        return (int)Math.Round(eff * 100);
    }

    // 把 DStyle 里的 -1（未设置）和越界值收拾成合法值。
    // 不这么兜的话，手改 JSON 或以后新增字段（DataContract 不调构造函数 → 新字段读成 0）都会出怪事。
    public static void RepairStyle(DStyle s)
    {
        if (s == null) return;
        s.BgAlpha = ClampInt(s.BgAlpha, 0, 100, 58);
        s.EdgeAlpha = ClampInt(s.EdgeAlpha, 0, 100, 30);   // 30% ≈ 老版本的 76/255，边线强度不变
        s.EdgeWidth = ClampInt(s.EdgeWidth, 1, 3, 1);      // 不要边线请把 EdgeAlpha 设 0
        s.TextAlpha = ClampInt(s.TextAlpha, 0, 100, 100);
        if (s.Ver < 1) { s.SepAlpha = -1; s.Ver = 1; }     // 老配置没有分隔线层 → 回落到默认
        s.SepAlpha = ClampInt(s.SepAlpha, 0, 100, 12);     // 12% ≈ 老的 Tint(30)
        s.Radius = ClampInt(s.Radius, 0, 28, 14);
        s.BgColor = NormHex(s.BgColor);
        s.EdgeColor = NormHex(s.EdgeColor);
        s.TextColor = NormHex(s.TextColor);
        s.SepColor = NormHex(s.SepColor);
    }

    static int ClampInt(int v, int lo, int hi, int def)
    {
        if (v < 0) return def;
        if (v < lo) return lo;
        if (v > hi) return hi;
        return v;
    }

    // 统一成 "#RRGGBB"；认不出就当"没设置"（返回 null = 跟随主题），绝不因为一个错字就崩
    public static string NormHex(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        string t = s.Trim().TrimStart('#');
        if (t.Length == 3) t = new string(new char[] { t[0], t[0], t[1], t[1], t[2], t[2] });
        if (t.Length != 6) return null;
        for (int i = 0; i < 6; i++) if (!Uri.IsHexDigit(t[i])) return null;
        return "#" + t.ToUpperInvariant();
    }

    public static Color ParseHex(string s, Color fallback)
    {
        string t = NormHex(s);
        if (t == null) return fallback;
        try
        {
            // **不能直接用 Color.FromArgb(int)**：那是按 ARGB 解析的，
            // "#DE35C6" 这类 6 位值高 8 位是 0，会变成"全透明色"。
            // 底板因为外面又套了一层显式 alpha 才没出问题，色块/取色器就露馅了。
            int rgb = Convert.ToInt32(t.Substring(1), 16);
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }
        catch { return fallback; }
    }

    public static string ToHex(Color c)
    {
        return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
    }

    public static void Normalize(DRoot r)
    {
        foreach (DBox b in r.Boxes)
        {
            if (string.IsNullOrEmpty(b.Id)) b.Id = Guid.NewGuid().ToString("N");
            if (b.W < 260) b.W = 420;
            if (b.H < 180) b.H = 300;
            if (b.Opacity < 20 || b.Opacity > 100) b.Opacity = 88;
            // 旧的 0~5 档只是为了把老配置算成透明度和兼容旧字段，已经不再影响绘制
            if (b.OpacityStep < 0 || b.OpacityStep > 5)
                b.OpacityStep = (int)Math.Round((b.Opacity - 20) / 16.0);
            if (b.OpacityStep < 0) b.OpacityStep = 0;
            if (b.OpacityStep > 5) b.OpacityStep = 5;
            b.Opacity = 20 + b.OpacityStep * 16;
            if (b.ThemeId < 0 || b.ThemeId > 4) b.ThemeId = 0;
            // 第一次升级到 DStyle：把"档位 + 亮底补偿"算出来的**实际**不透明度写进 BgAlpha。
            // 这样迁移前后肉眼一模一样，而从今往后这个数字就是真实 alpha，不再有隐藏加成。
            if (b.Style == null)
            {
                b.Style = new DStyle();
                b.Style.BgAlpha = MigratedAlpha(b);
            }
            RepairStyle(b.Style);
            if (b.Tabs == null) b.Tabs = new List<DTab>();
            if (b.Tabs.Count == 0) b.Tabs.Add(MakeTab("全部"));
            foreach (DTab t in b.Tabs)
            {
                if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("N");
                if (t.Items == null) t.Items = new List<DItem>();
            }
        }

        // 盒子文件夹名去重
        HashSet<string> boxNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DBox b in r.Boxes)
        {
            string want = string.IsNullOrEmpty(b.Folder) ? SafeName(b.Title) : b.Folder;
            string uniq = want;
            int i = 2;
            while (boxNames.Contains(uniq)) { uniq = want + " (" + i + ")"; i++; }
            b.Folder = uniq;
            boxNames.Add(uniq);
        }

        // 子盒子（标签）文件夹名在各自盒子内去重
        foreach (DBox b in r.Boxes)
        {
            HashSet<string> tabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DTab t in b.Tabs)
            {
                string want = string.IsNullOrEmpty(t.Folder) ? SafeName(t.Name) : t.Folder;
                string uniq = want;
                int i = 2;
                while (tabNames.Contains(uniq)) { uniq = want + " (" + i + ")"; i++; }
                t.Folder = uniq;
                tabNames.Add(uniq);
            }
        }

        // 盒子目录被改成 “名字 (2)” 之类的空壳时，搬回真正装着文件的那份
        RepairBoxFolders(r);

        // 整个程序目录被搬走时，把失效的条目路径接回当前 items 目录
        RebaseItems(r);
    }

    // ---------- 路径自愈（程序换目录后条目不会丢） ----------

    static Dictionary<string, List<string>> nameIndex;

    static void RebaseItems(DRoot r)
    {
        bool hasMissing = false;
        foreach (DBox b in r.Boxes)
        {
            foreach (DTab t in b.Tabs)
            {
                foreach (DItem it in t.Items)
                {
                    if (!string.IsNullOrEmpty(it.Path) && !File.Exists(it.Path) && !Directory.Exists(it.Path))
                    {
                        hasMissing = true;
                        break;
                    }
                }
                if (hasMissing) break;
            }
            if (hasMissing) break;
        }
        if (!hasMissing) return;

        nameIndex = null;
        foreach (DBox b in r.Boxes)
        {
            foreach (DTab t in b.Tabs)
            {
                foreach (DItem it in t.Items)
                {
                    if (string.IsNullOrEmpty(it.Path)) continue;
                    if (File.Exists(it.Path) || Directory.Exists(it.Path)) continue;
                    string fixedPath = RebaseOne(it.Path);
                    if (fixedPath != null)
                    {
                        Log.W("rebase  " + it.Path + " -> " + fixedPath);
                        it.Path = fixedPath;
                    }
                }
            }
        }
    }

    static string RebaseOne(string p)
    {
        try
        {
            // 优先：取 “\items\” 之后的相对路径，接到当前 items 目录
            string low = p.ToLower();
            int idx = low.IndexOf("\\items\\");
            if (idx >= 0)
            {
                string rel = p.Substring(idx + 7);
                string cand = Path.Combine(ItemsRoot, rel);
                if (File.Exists(cand) || Directory.Exists(cand)) return cand;
            }
            // 兜底 1：按文件名在 items 下找（同名唯一才认）
            string name = Path.GetFileName(p);
            if (string.IsNullOrEmpty(name)) return null;
            if (nameIndex == null) BuildNameIndex();
            List<string> hits;
            if (nameIndex.TryGetValue(name, out hits) && hits.Count == 1) return hits[0];
            // 兜底 2：迁移备份目录 _migrate_backup（同名唯一才认）。
            // 搬家做备份时是扁平复制的，路径不可靠，只能靠文件名认亲。
            hits = FindByName(Path.Combine(BaseDir, "_migrate_backup"), name);
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
            {
                // 多个备份里都有：优先最新那一份
                hits.Sort(StringComparer.OrdinalIgnoreCase);
                return hits[hits.Count - 1];
            }
            // 兜底 3：桌面（用户可能又把快捷方式放回桌面了）
            hits = FindByName(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
            if (hits.Count == 1) return hits[0];
        }
        catch { }
        return null;
    }

    static List<string> FindByName(string dir, string name)
    {
        List<string> r = new List<string>();
        try
        {
            if (!Directory.Exists(dir)) return r;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) r.Add(f);
            }
        }
        catch { }
        return r;
    }

    // 盒子文件夹漂移自愈：Folder 被改成 “名字 (2)” 之类、实体目录是空壳，
    // 而真正装着文件的同名（去掉末尾 " (n)"）目录还在 → 搬回去。
    static void RepairBoxFolders(DRoot r)
    {
        try
        {
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DBox b in r.Boxes) if (!string.IsNullOrEmpty(b.Folder)) used.Add(b.Folder);

            foreach (DBox b in r.Boxes)
            {
                string cur = string.IsNullOrEmpty(b.Folder) ? SafeName(b.Title) : b.Folder;
                string curDir = Path.Combine(ItemsRoot, cur);
                int curFiles = CountFiles(curDir);
                if (curFiles > 0) continue;                 // 现有目录里已经有东西，不动

                // 候选：Title 原名 / 去掉末尾 " (n)" 的当前名
                List<string> cands = new List<string>();
                string byTitle = SafeName(b.Title);
                cands.Add(byTitle);
                string stripped = System.Text.RegularExpressions.Regex.Replace(cur, @"\s*\(\d+\)$", "");
                if (stripped.Length > 0 && !string.Equals(stripped, byTitle, StringComparison.OrdinalIgnoreCase))
                    cands.Add(stripped);

                foreach (string cand in cands)
                {
                    if (string.Equals(cand, cur, StringComparison.OrdinalIgnoreCase)) continue;
                    if (used.Contains(cand)) continue;
                    string candDir = Path.Combine(ItemsRoot, cand);
                    if (!Directory.Exists(candDir)) continue;
                    if (CountFiles(candDir) == 0) continue;
                    // 改 Folder，并把条目路径前缀换过去
                    string oldPrefix = curDir.TrimEnd('\\') + "\\";
                    string newPrefix = candDir.TrimEnd('\\') + "\\";
                    foreach (DTab t in b.Tabs)
                        foreach (DItem it in t.Items)
                            if (!string.IsNullOrEmpty(it.Path) &&
                                it.Path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                                it.Path = newPrefix + it.Path.Substring(oldPrefix.Length);
                    Log.W("repair folder  " + cur + " -> " + cand);
                    b.Folder = cand;
                    used.Remove(cur);
                    used.Add(cand);
                    break;
                }
            }
        }
        catch (Exception ex) { Log.W("repair folders fail " + ex.Message); }
    }

    static int CountFiles(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
        }
        catch { return 0; }
    }

    // 启动体检：有悬空条目就写一份报告，绝不静默丢失
    public static int HealthCheck(DRoot r, string path)
    {
        int miss = 0;
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (DBox b in r.Boxes)
            foreach (DTab t in b.Tabs)
                foreach (DItem it in t.Items)
                {
                    if (string.IsNullOrEmpty(it.Path)) continue;
                    if (File.Exists(it.Path) || Directory.Exists(it.Path)) continue;
                    miss++;
                    sb.AppendLine("[" + b.Title + " / " + t.Name + "] " + it.Name + "  ->  " + it.Path);
                }
        if (miss > 0)
        {
            try
            {
                File.WriteAllText(path, "共 " + miss + " 个条目文件已丢失（下面列出，可在盒子里右键移除或重新拖入）\r\n\r\n" + sb.ToString(),
                    System.Text.Encoding.UTF8);
                Log.W("health: " + miss + " 个条目文件丢失，详见 " + Path.GetFileName(path));
            }
            catch { }
        }
        else
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        return miss;
    }

    static void BuildNameIndex()
    {
        nameIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string f in Directory.GetFiles(ItemsRoot, "*", SearchOption.AllDirectories))
            {
                string n = Path.GetFileName(f);
                List<string> l;
                if (!nameIndex.TryGetValue(n, out l)) { l = new List<string>(); nameIndex[n] = l; }
                l.Add(f);
            }
        }
        catch { }
    }

    public static string UniqueBoxFolder(DRoot r, DBox self, string want)
    {
        string uniq = want;
        int i = 2;
        while (true)
        {
            bool clash = false;
            foreach (DBox o in r.Boxes)
            {
                if (ReferenceEquals(o, self)) continue;
                if (string.Equals(o.Folder, uniq, StringComparison.OrdinalIgnoreCase)) { clash = true; break; }
            }
            if (!clash) return uniq;
            uniq = want + " (" + i + ")";
            i++;
        }
    }

    public static string UniqueTabFolder(DBox b, DTab self, string want)
    {
        string uniq = want;
        int i = 2;
        while (true)
        {
            bool clash = false;
            foreach (DTab o in b.Tabs)
            {
                if (ReferenceEquals(o, self)) continue;
                if (string.Equals(o.Folder, uniq, StringComparison.OrdinalIgnoreCase)) { clash = true; break; }
            }
            if (!clash) return uniq;
            uniq = want + " (" + i + ")";
            i++;
        }
    }

    public static DBox MakeBox(int x, int y, string title)
    {
        DBox b = new DBox();
        b.Id = Guid.NewGuid().ToString("N");
        b.X = x; b.Y = y; b.W = 420; b.H = 300;
        b.Opacity = 88; b.Locked = false;
        b.Title = title; b.Accent = 0;
        b.Folder = SafeName(title);
        b.Tabs = new List<DTab>();
        return b;
    }

    public static DTab MakeTab(string name)
    {
        DTab t = new DTab();
        t.Id = Guid.NewGuid().ToString("N");
        t.Name = name;
        t.Folder = SafeName(name);
        t.Items = new List<DItem>();
        return t;
    }

    public static void Save(DRoot r)
    {
        try
        {
            using (FileStream fs = new FileStream(ConfigPath, FileMode.Create, FileAccess.Write))
            {
                DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(DRoot));
                ser.WriteObject(fs, r);
            }
        }
        catch { }
    }

    public static string Unique(string dir, string fileName)
    {
        string target = Path.Combine(dir, fileName);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        string bare = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 2; i < 999; i++)
        {
            target = Path.Combine(dir, bare + " (" + i + ")" + ext);
            if (!File.Exists(target) && !Directory.Exists(target)) return target;
        }
        return Path.Combine(dir, bare + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
    }

    // ================= 旧数据迁移 =================

    public static bool NeedsMigrate(DRoot r)
    {
        foreach (DBox b in r.Boxes)
        {
            foreach (DTab t in b.Tabs)
            {
                string dir = TabDir(b, t);
                foreach (DItem it in t.Items)
                {
                    if (string.IsNullOrEmpty(it.Path)) return true;
                    if (!InDir(dir, it.Path)) return true;
                }
            }
        }
        return false;
    }

    public class MigrateResult
    {
        public int Moved;
        public int Copied;
        public int Shared;
        public int Already;
        public int Kept;
        public string BackupDir;
        public List<string> KeptNames = new List<string>();
    }

    static bool Exists(string p)
    {
        return File.Exists(p) || Directory.Exists(p);
    }

    // 迁移前把要动的文件复制一份到 _migrate_backup\<时间戳>\
    public static string BackupSources(DRoot r)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string dir = Path.Combine(BaseDir, "_migrate_backup", stamp);
        bool created = false;
        foreach (DBox b in r.Boxes)
        {
            foreach (DTab t in b.Tabs)
            {
                foreach (DItem it in t.Items)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(it.Path)) continue;
                        if (IsManaged(it.Path)) continue;
                        if (!File.Exists(it.Path)) continue;
                        FileInfo fi = new FileInfo(it.Path);
                        if (fi.Length > 64L * 1024 * 1024) continue;
                        if (!created) { Directory.CreateDirectory(dir); created = true; }
                        File.Copy(it.Path, Unique(dir, fi.Name), false);
                    }
                    catch { }
                }
            }
        }
        return created ? dir : null;
    }

    public static MigrateResult Migrate(DRoot r)
    {
        MigrateResult res = new MigrateResult();

        // 1) 旧版按盒子 Id 命名的文件夹 → 新的可读名文件夹
        foreach (DBox b in r.Boxes)
        {
            string legacy = Path.Combine(ItemsRoot, b.Id);
            string dir = BoxDir(b);
            try
            {
                if (Directory.Exists(legacy) &&
                    !string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string entry in Directory.GetFileSystemEntries(legacy))
                    {
                        string dest = Unique(dir, Path.GetFileName(entry));
                        try { MoveEntry(entry, dest); }
                        catch { }
                    }
                    try { if (Directory.GetFileSystemEntries(legacy).Length == 0) Directory.Delete(legacy); } catch { }
                }
            }
            catch { }
        }

        // 2) 逐个条目搬文件并改写路径。
        //    铁律：任何情况下都不删除条目，搬不动就保持原样。
        Dictionary<string, string> movedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DBox b in r.Boxes)
        {
            foreach (DTab t in b.Tabs)
            {
                string dir = TabDir(b, t);
                foreach (DItem it in t.Items)
                {
                    string tag = b.Title + "/" + t.Name + "/" + it.Name;
                    string src = (it.Path ?? "").TrimEnd('\\', '/');
                    if (src.Length == 0)
                    {
                        res.Kept++;
                        if (res.KeptNames.Count < 30) res.KeptNames.Add(tag + " —— 路径为空");
                        continue;
                    }

                    if (InDir(dir, src))
                    {
                        if (Exists(src)) res.Already++;
                        else
                        {
                            res.Kept++;
                            if (res.KeptNames.Count < 30) res.KeptNames.Add(tag + " —— 记录在盒子里，但文件已不存在");
                        }
                        continue;
                    }

                    string dest = null;
                    string err = null;
                    string prev;
                    if (movedMap.TryGetValue(src, out prev) && Exists(prev))
                    {
                        // 同一个源文件在别的子盒子已经搬过一次：复制一份，各自独立
                        dest = Unique(dir, Path.GetFileName(prev));
                        try { CopyEntry(prev, dest); res.Copied++; Log.W("migrate copy  " + tag + " <- " + prev); }
                        catch (Exception ex) { dest = null; err = "复制失败：" + ex.Message; }
                    }
                    else
                    {
                        dest = MoveToDir(dir, src, out err);
                        if (dest != null)
                        {
                            res.Moved++;
                            movedMap[src] = dest;
                            Log.W("migrate move  " + tag + " -> " + dest);
                        }
                    }

                    if (dest == null)
                    {
                        // 退一步：目标文件夹里已有同名文件就直接认它
                        string guess = Path.Combine(dir, SafeName(Path.GetFileName(src)));
                        if (Exists(guess)) { dest = guess; res.Shared++; }
                    }

                    if (dest == null)
                    {
                        res.Kept++;
                        if (res.KeptNames.Count < 30) res.KeptNames.Add(tag + " —— " + err);
                        Log.W("migrate keep  " + tag + " :: " + err);
                        continue;
                    }

                    it.Path = dest;
                    if (string.IsNullOrEmpty(it.Name)) it.Name = LabelOf(dest);
                }
            }
        }
        return res;
    }
}

// 就地重命名用的极简输入框：一个无边框窗口，正好盖在被编辑的那行文字上。
// 背景色取屏幕同位置采样，所以看起来是「原地变成可编辑」，不弹对话框。
// 之所以用真 TextBox 而不是自绘光标：中文输入法（组合串、候选窗）和剪贴板都得靠它。
class InlineEditor : Form
{
    public static InlineEditor Current;
    TextBox tb;
    bool finished;
    Action<string, bool> done;          // (新文字, 是否提交)

    InlineEditor(string text, Rectangle screen, Color bg, Color fg, int px, bool centered)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen;
        BackColor = bg;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;

        tb = new TextBox();
        tb.BorderStyle = BorderStyle.None;
        tb.BackColor = bg;
        tb.ForeColor = fg;
        // 用 Pixel 而不是磅：盒子里所有字都是 GraphicsUnit.Pixel 整数，
        // 一旦用「磅」就会落半像素，编辑器里的字和盒子里的字粗细对不上。
        tb.Font = new Font("Microsoft YaHei", px, FontStyle.Regular, GraphicsUnit.Pixel);
        tb.Text = text;
        tb.TextAlign = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        int hh = tb.PreferredHeight;
        int y = (screen.Height - hh) / 2;
        if (y < 0) y = 0;
        tb.Bounds = new Rectangle(centered ? 1 : 2, y,
                                  Math.Max(24, screen.Width - (centered ? 2 : 4)), hh);
        Controls.Add(tb);
    }

    public static void Begin(string text, Rectangle screenRect, Color bg, Color fg, int px,
                             bool centered, Action<string, bool> cb)
    {
        CloseCurrent(false);
        InlineEditor f = new InlineEditor(text, screenRect, bg, fg, px, centered);
        f.done = cb;
        Current = f;
        f.Show();
        f.Bounds = screenRect;  // 句柄创建期窗口会短暂带边框把 Bounds 撑大，Show 后必须再设一次
        f.Activate();
        f.tb.Focus();
        f.tb.SelectAll();       // 和 F2 一样：进来就全选，直接打字即替换
    }

    // 关掉当前编辑器。commit=false 什么都不写（换盒子 / 退出程序时用）
    public static void CloseCurrent(bool commit)
    {
        InlineEditor f = Current;
        if (f == null) return;
        Current = null;
        Action<string, bool> cb = f.done;
        f.done = null;
        f.finished = true;
        string t = null;
        try { t = f.tb.Text; } catch { }
        try { f.Hide(); f.Close(); } catch { }
        if (commit && cb != null) cb(t, true);
    }

    void Finish(bool commit)
    {
        if (finished) return;
        finished = true;
        Action<string, bool> cb = done;
        done = null;
        if (Current == this) Current = null;
        string t = null;
        try { t = tb.Text; } catch { }
        try { Hide(); } catch { }
        try { Close(); } catch { }
        if (cb != null) cb(t, commit);
    }

    public void TestSetText(string t) { tb.Text = t; }
    public void TestFinish(bool commit) { Finish(commit); }

    // 点到别处 = 提交（和资源管理器一致），不静默丢弃用户刚打的字
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!finished && Visible) Finish(true);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Enter) { Finish(true); return true; }
        if (keyData == Keys.Escape) { Finish(false); return true; }
        return base.ProcessDialogKey(keyData);
    }
}

static class IconCache
{
    static Dictionary<string, Image> cache = new Dictionary<string, Image>();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    public static Image Get(string path, string label) { return Get(path, label, 48); }

    public static int Count { get { return cache.Count; } }
    public static int Miss;    // 统计：真正的取图次数（缓存没命中）

    public static Image Get(string path, string label, int size)
    {
        if (path == null) return null;
        string key = path + "|" + label + "|" + size;
        if (cache.ContainsKey(key)) return cache[key];
        Miss++;
        Image img = null;
        bool needFit = false;
        try
        {
            string target = Resolve(path);
            // 解析出来的目标可能根本不存在：.url 会得到 steam://… 这种网址，
            // 目标已卸载的 .lnk 会得到一条死路径。这时必须退回“文件自己”，
            // 否则整个图标就直接掉成字母块（这正是“盒子图标被改掉”的元凶）。
            if (target == null || (!Directory.Exists(target) && !File.Exists(target))) target = path;
            if (Directory.Exists(target))
            {
                // 文件夹：系统原本的黄色文件夹样式（矢量绘制，直接按目标尺寸画，最清晰）
                img = DrawFolder(size);
            }
            else if (target != null && File.Exists(target))
            {
                string low = target.ToLower();
                if (low.EndsWith(".bat") || low.EndsWith(".cmd"))
                {
                    img = ScriptTile(size);
                }
                else
                {
                    img = BestFileIcon(target, size);
                    needFit = img != null;
                }
            }
        }
        catch { }
        if (img == null) img = LetterTile(label, size);
        // 已经是目标尺寸就原样用 —— 再走一遍 Fit 等于白磨一次
        else if (needFit && (img.Width != size || img.Height != size)) img = Fit(img, size);
        cache[key] = img;
        return img;
    }


    // ── Explorer 同源图标：IShellItemImageFactory::GetImage ─────────────────
    // shell 自己处理文件关联的图标扩展、图标缓存，以及“从最大资源高质量缩到请求尺寸”
    // 这件事。我们只要按显示尺寸要一张 32bpp 位图即可，和桌面画的是同一张。
    [StructLayout(LayoutKind.Sequential)]
    struct ShxSIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct ShxBMIHeader
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ShxBMI
    {
        public ShxBMIHeader bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[] bmiColors;
    }

    [DllImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName",
        CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int ShxCreateItem(string pszPath, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int ShxGetImageProc(IntPtr self, ShxSIZE size, int flags, out IntPtr hbm);

    [DllImport("gdi32.dll", EntryPoint = "GetDIBits")]
    static extern int ShxGetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
                                   byte[] bits, ref ShxBMI bi, uint usage);
    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    static extern IntPtr ShxCreateDC(IntPtr hdc);
    [DllImport("gdi32.dll", EntryPoint = "DeleteDC")]
    static extern bool ShxDeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    static extern bool ShxDeleteObject(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetDC")]
    static extern IntPtr ShxGetDC(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    static extern int ShxReleaseDC(IntPtr hwnd, IntPtr hdc);

    static readonly Guid ShxIImageFactory = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    static Dictionary<IntPtr, ShxGetImageProc> shxProcs = new Dictionary<IntPtr, ShxGetImageProc>();

    static Image FromShellFactory(string path, int size)
    {
        IntPtr item = IntPtr.Zero, hbm = IntPtr.Zero;
        try
        {
            Guid iid = ShxIImageFactory;
            if (ShxCreateItem(path, IntPtr.Zero, ref iid, out item) != 0 || item == IntPtr.Zero) return null;
            IntPtr vt = Marshal.ReadIntPtr(item);                 // 接口指针 -> vtable
            ShxGetImageProc get;
            if (!shxProcs.TryGetValue(vt, out get))
            {
                // IUnknown 三槽之后就是 IShellItemImageFactory::GetImage
                IntPtr fn = Marshal.ReadIntPtr(vt, 3 * IntPtr.Size);
                get = (ShxGetImageProc)Marshal.GetDelegateForFunctionPointer(fn, typeof(ShxGetImageProc));
                shxProcs[vt] = get;
            }
            ShxSIZE sz; sz.cx = size; sz.cy = size;
            // SIIGBF_ICONONLY = 4：只要图标，不要缩略图
            if (get(item, sz, 4, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            return ShxBitmapFrom(hbm, size);
        }
        catch { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) ShxDeleteObject(hbm);
            if (item != IntPtr.Zero) { try { Marshal.Release(item); } catch { } }
        }
    }

    // GetImage 返回 32bpp DIB，普通（非预乘）BGRA；Format32bppArgb 在小端上正是这个字节序
    static Bitmap ShxBitmapFrom(IntPtr hbm, int size)
    {
        IntPtr screen = ShxGetDC(IntPtr.Zero);
        IntPtr dc = ShxCreateDC(screen);
        try
        {
            ShxBMI bi = new ShxBMI();
            bi.bmiColors = new uint[3];
            bi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(ShxBMIHeader));
            bi.bmiHeader.biWidth = size;
            bi.bmiHeader.biHeight = -size;          // top-down
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = 0;        // BI_RGB
            byte[] buf = new byte[size * size * 4];
            if (ShxGetDIBits(dc, hbm, 0, (uint)size, buf, ref bi, 0) != size) return null;
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, size, size),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(buf, 0, bd.Scan0, buf.Length); }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
        catch { return null; }
        finally { ShxDeleteDC(dc); ShxReleaseDC(IntPtr.Zero, screen); }
    }

    // 取文件的图标。size 是“实际显示尺寸”，一律先向系统要这个尺寸的原生图标：
    // 资源管理器就是这么做的 —— .exe/.ico 里若含 48px，就原样取出来 1:1 贴，绝对清晰；
    // 以前走"要 256 再缩到 44"的路子，等于把系统挑好的高清图又自己糊一遍。
    static Image BestFileIcon(string file, int size)
    {
        // 1) .url（Internet 快捷方式）自带 IconFile=…，里面有品牌图标（Steam 游戏都靠它）。
        //    必须排在“文件自身图标”前面，否则拿到的是通用网址快捷方式图标。
        Image urlIcon = FromUrlIcon(file, size);
        if (urlIcon != null) return urlIcon;

        // 资源管理器 / 桌面并不是直接贴 .ico 里那张硬边 48px 变体，而是让 shell
        // 从最大的图标资源（通常 256px）高质量缩到显示尺寸。2026-11-12 实测
        // IShellItemImageFactory@48 与桌面图标一致，PrivateExtractIconsW(48) 放大全是台阶。
        // 失败就静默落回下面原来的递降取图链路，不影响任何老功能。
        if (File.Exists(file) &&
            !file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) &&
            !file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            Image shell = FromShellFactory(file, size);
            if (shell != null) return shell;
        }

        string t = Resolve(file);
        bool hasTarget = t != null && !string.Equals(t, file, StringComparison.OrdinalIgnoreCase) && File.Exists(t);

        // 2) .lnk：先解析目标，到目标文件里按显示尺寸取图。
        //    .lnk 自己没有图标资源，交给系统取只会得到一个通用快捷方式图标。
        if (hasTarget)
        {
            Image ti = FromIconResource(t, 0, size);
            if (ti != null) return ti;
        }

        // 3) 文件自身的图标（exe/dll/ico 直接就有）
        Image own = FromIconResource(file, 0, size);
        if (own != null) return own;

        // 4) 普通文件（.xlsx/.txt/.jpg…）：先用系统关联图标，
        //    再去查注册表登记的默认图标（那里可能有大图）
        try { Icon ic = Icon.ExtractAssociatedIcon(file); if (ic != null) return ic.ToBitmap(); } catch { }

        Image assoc = FromAssoc(file, size);
        if (assoc != null) return assoc;

        if (hasTarget)
        {
            try { Icon ic3 = Icon.ExtractAssociatedIcon(t); if (ic3 != null) return ic3.ToBitmap(); } catch { }
        }
        return null;
    }

    // 从一个图标资源文件（.ico/.dll/.exe）里取图标：
    // PrivateExtractIcons 对“没有该尺寸”的 .ico 会直接失败（比如只含 32px 的 Steam 图标），
    // 所以按尺寸递降重试，最后退回 ExtractAssociatedIcon。
    static Image FromIconResource(string file, int index, int size)
    {
        // .url 的 IconFile / 注册表里的文件关联默认图标也是"文件"，同样让 shell 按
        // 显示尺寸出图；带 IconIndex 的资源（.dll,3 之类）没法走这条路，仍用递降取图。
        if (index == 0 && File.Exists(file))
        {
            Image sh = FromShellFactory(file, size);
            if (sh != null) return sh;
        }
        Image r = FromPrivate(file, size, index);
        if (r != null) return r;
        int[] sizes = new int[] { 256, 128, 64, 48, 32, 16 };
        foreach (int s in sizes)
        {
            if (s == size) continue;
            r = FromPrivate(file, s, index);
            if (r != null) return r;
        }
        try
        {
            using (Icon ic = Icon.ExtractAssociatedIcon(file))
                if (ic != null) return ic.ToBitmap();
        }
        catch { }
        return null;
    }

    // 查文件类型（.xlsx/.txt…）在系统里登记的默认图标位置，再从那里取大图
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(int flags, int str, string assoc, string extra, System.Text.StringBuilder outBuf, ref int outLen);

    // .url 是 INI 格式，[InternetShortcut] 段里的 IconFile/IconIndex 就是它的图标
    static Image FromUrlIcon(string file, int size)
    {
        try
        {
            if (!file.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return null;
            string iconFile = null;
            int idx = 0;
            foreach (string line in File.ReadAllLines(file, System.Text.Encoding.Default))
            {
                string s = line.Trim();
                int eq = s.IndexOf('=');
                if (eq <= 0) continue;
                string k = s.Substring(0, eq).Trim();
                string v = s.Substring(eq + 1).Trim();
                if (string.Equals(k, "IconFile", StringComparison.OrdinalIgnoreCase))
                    iconFile = Environment.ExpandEnvironmentVariables(v.Trim('"'));
                else if (string.Equals(k, "IconIndex", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(v, out idx);
            }
            if (string.IsNullOrEmpty(iconFile) || idx < 0) return null;
            if (!File.Exists(iconFile)) return null;
            return FromIconResource(iconFile, idx, size);
        }
        catch { }
        return null;
    }

    static Image FromAssoc(string file, int size)
    {
        try
        {
            string ext = Path.GetExtension(file);
            if (string.IsNullOrEmpty(ext)) return null;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
            int len = sb.Capacity;
            // ASSOCF_NONE=0, ASSOCSTR_DEFAULTICON=13
            if (AssocQueryString(0, 13, ext, null, sb, ref len) != 0) return null;
            string s = sb.ToString();
            if (string.IsNullOrEmpty(s)) return null;

            string iconFile = s;
            int index = 0;
            int comma = s.LastIndexOf(',');
            if (comma > 1)
            {
                iconFile = s.Substring(0, comma).Trim().Trim('"');
                int.TryParse(s.Substring(comma + 1).Trim(), out index);
            }
            iconFile = iconFile.Trim('"');
            if (iconFile.StartsWith("%")) return null;   // "%1,-123" 这种间接引用没法解析
            iconFile = Environment.ExpandEnvironmentVariables(iconFile);
            if (!File.Exists(iconFile)) return null;
            return FromIconResource(iconFile, index, size);
        }
        catch { }
        return null;
    }

    // 文件夹图标：手绘，风格与系统黄色文件夹一致
    static Image DrawFolder(int size)
    {
        Bitmap b = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 64f, size / 64f);

            // 后面的小标签
            using (GraphicsPath tab = new GraphicsPath())
            {
                tab.AddArc(6, 11, 8, 8, 180, 90);
                tab.AddArc(30, 11, 8, 8, 270, 90);
                tab.AddLine(34, 25, 6, 25);
                tab.CloseFigure();
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(255, 233, 168, 20))) g.FillPath(tb, tab);
            }

            // 主体
            Rectangle body = new Rectangle(6, 18, 52, 36);
            using (GraphicsPath p = new GraphicsPath())
            {
                int r = 5;
                p.AddArc(body.X, body.Y, r * 2, r * 2, 180, 90);
                p.AddArc(body.Right - r * 2, body.Y, r * 2, r * 2, 270, 90);
                p.AddArc(body.Right - r * 2, body.Bottom - r * 2, r * 2, r * 2, 0, 90);
                p.AddArc(body.X, body.Bottom - r * 2, r * 2, r * 2, 90, 90);
                p.CloseFigure();
                using (LinearGradientBrush gb = new LinearGradientBrush(body,
                    Color.FromArgb(255, 255, 202, 68), Color.FromArgb(255, 240, 174, 25), 90f))
                {
                    g.FillPath(gb, p);
                }
                using (Pen pen = new Pen(Color.FromArgb(90, 255, 240, 190), 1f)) g.DrawPath(pen, p);
            }
        }
        return b;
    }

    // 批处理脚本图标：终端窗口 + ">_"
    static Image ScriptTile(int size)
    {
        Bitmap b = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 64f, size / 64f);

            Rectangle box = new Rectangle(4, 9, 56, 46);
            using (GraphicsPath p = new GraphicsPath())
            {
                int r = 7;
                p.AddArc(box.X, box.Y, r * 2, r * 2, 180, 90);
                p.AddArc(box.Right - r * 2, box.Y, r * 2, r * 2, 270, 90);
                p.AddArc(box.Right - r * 2, box.Bottom - r * 2, r * 2, r * 2, 0, 90);
                p.AddArc(box.X, box.Bottom - r * 2, r * 2, r * 2, 90, 90);
                p.CloseFigure();
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(255, 17, 25, 34))) g.FillPath(fill, p);
                using (Pen pen = new Pen(Color.FromArgb(255, 0, 220, 140), 1.5f)) g.DrawPath(pen, p);
            }
            using (SolidBrush bar = new SolidBrush(Color.FromArgb(255, 0, 220, 140)))
            {
                g.FillRectangle(bar, 8, 12, 48, 3);
            }
            using (Font f = new Font("Consolas", 19f, FontStyle.Bold))
            using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 130, 250, 205)))
            {
                g.DrawString(">_", f, br, new PointF(11f, 21f));
            }
        }
        return b;
    }

    public static void Clear() { cache.Clear(); }

    // 仅测试用：按指定尺寸直接向 shell 要图标（可能拿到原生尺寸，也可能被 shell 缩过）
    public static Image TestPrivate(string file, int size) { return FromPrivate(file, size, 0); }
    public static Image TestFit(Image src, int box) { return Fit(src, box); }

    static Image FromPrivate(string file, int size) { return FromPrivate(file, size, 0); }

    static Image FromPrivate(string file, int size, int index)
    {
        try
        {
            IntPtr[] icons = new IntPtr[1];
            uint[] ids = new uint[1];
            uint n = PrivateExtractIconsW(file, index, size, size, icons, ids, 1, 0);
            if (n > 0 && icons[0] != IntPtr.Zero)
            {
                Bitmap bmp;
                using (Icon ic = Icon.FromHandle(icons[0])) { bmp = ic.ToBitmap(); }
                DestroyIcon(icons[0]);
                return bmp;
            }
        }
        catch { }
        return null;
    }

    public static Image Fit(Image src, int box)
    {
        try
        {
            // 大比例缩小（256 → 44 差不多 6 倍）如果一步插值，细节会被糊平。
            // 先反复对半缩到接近目标，再做最后一跳，边缘锐利得多。
            Image cur = src;
            bool owned = false;
            while (cur.Width / 2 >= box * 2)
            {
                int nw = cur.Width / 2;
                Image half = ResizeTo(cur, nw);
                if (owned) cur.Dispose();
                cur = half;
                owned = true;
            }
            Bitmap bmp = new Bitmap(box, box, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(cur, 0, 0, box, box);
            }
            if (owned) cur.Dispose();
            return bmp;
        }
        catch { return src; }
    }

    static Image ResizeTo(Image src, int size)
    {
        Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(src, 0, 0, size, size);
        }
        return b;
    }

    static Image LetterTile(string label, int size)
    {
        string ch = "?";
        if (label != null && label.Length > 0) ch = label.Substring(0, 1).ToUpper();
        Bitmap b = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 64f, size / 64f);
            using (GraphicsPath p = new GraphicsPath())
            {
                int r = 13;
                p.AddArc(0, 0, r * 2, r * 2, 180, 90);
                p.AddArc(63 - r * 2, 0, r * 2, r * 2, 270, 90);
                p.AddArc(63 - r * 2, 63 - r * 2, r * 2, r * 2, 0, 90);
                p.AddArc(0, 63 - r * 2, r * 2, r * 2, 90, 90);
                p.CloseFigure();
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(255, 24, 32, 42))) g.FillPath(fill, p);
                using (Pen pen = new Pen(Color.FromArgb(255, 0, 235, 150), 1.4f)) g.DrawPath(pen, p);
            }
            using (Font f = new Font("Microsoft YaHei", 22f, FontStyle.Bold))
            using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 0, 235, 150)))
            {
                SizeF sz = g.MeasureString(ch, f);
                g.DrawString(ch, f, br, (64 - sz.Width) / 2f, (64 - sz.Height) / 2f);
            }
        }
        return b;
    }

    public static string Resolve(string path)
    {
        try
        {
            string low = path.ToLower();
            if (low.EndsWith(".lnk") || low.EndsWith(".url"))
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                object tp = lnk.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, lnk, null);
                if (tp != null && tp.ToString().Length > 0) return tp.ToString();
            }
        }
        catch { }
        return path;
    }
}

class Theme
{
    public string Name;
    public Color Plate;
    public Color Text;
    public Color Sub;
    public Color[] Accents;

    public Theme(string n, Color plate, Color text, Color sub, Color[] acc)
    {
        Name = n; Plate = plate; Text = text; Sub = sub; Accents = acc;
    }
}

static class Themes
{
    // 5 套配色的结构（照配色方法论来的，不是随手挑色卡）：
    //   Plate  = 中性底板，暗底不用纯黑（#000 会让文字发晕）、亮底不用纯白，且都带一点色相；
    //   Text   = 主文字，和 Plate 对比 ≥ 7:1；Sub = 次要文字（标签名/+/×），≥ 4.5:1；
    //   Accents= 每套 5 支强调色，只做点缀（标题左色条 / 选中下划线 / 滑块点 / 选中框 / 锁）。
    // 两条硬指标（palcheck.py 逐项算过，0 个不达标）：
    //   ① 每支 accent 对 Plate 对比 ≥ 3.2:1 —— 低于 3 在浅底上就等于"没有颜色"；
    //   ② 每支 accent 饱和度 HSL-S ≤ 78% —— 原来赛博 5 支里 4 支是 100% 纯霓虹，刺眼且廉价。
    // 改色值必须重跑 palcheck.py，别再凭眼睛调。
    public static Theme[] All = new Theme[] {
        new Theme("赛博", Color.FromArgb(22, 26, 33), Color.FromArgb(237, 242, 248), Color.FromArgb(151, 163, 180),
            new Color[] { Color.FromArgb(55, 199, 155), Color.FromArgb(226, 103, 139), Color.FromArgb(223, 164, 63), Color.FromArgb(82, 167, 219), Color.FromArgb(155, 140, 217) }),
        new Theme("马卡龙", Color.FromArgb(250, 247, 246), Color.FromArgb(59, 53, 66), Color.FromArgb(107, 99, 115),
            new Color[] { Color.FromArgb(201, 92, 124), Color.FromArgb(47, 142, 124), Color.FromArgb(90, 106, 196), Color.FromArgb(181, 113, 58), Color.FromArgb(162, 94, 166) }),
        new Theme("莫兰迪", Color.FromArgb(232, 228, 222), Color.FromArgb(62, 58, 54), Color.FromArgb(106, 100, 94),
            new Color[] { Color.FromArgb(125, 103, 87), Color.FromArgb(95, 119, 106), Color.FromArgb(142, 110, 103), Color.FromArgb(105, 115, 127), Color.FromArgb(126, 116, 73) }),
        new Theme("莫兰迪-深", Color.FromArgb(52, 54, 58), Color.FromArgb(239, 236, 230), Color.FromArgb(173, 169, 161),
            new Color[] { Color.FromArgb(196, 167, 141), Color.FromArgb(156, 181, 172), Color.FromArgb(205, 166, 159), Color.FromArgb(169, 182, 198), Color.FromArgb(197, 184, 124) }),
        new Theme("黑白", Color.FromArgb(22, 22, 26), Color.FromArgb(240, 240, 243), Color.FromArgb(155, 155, 161),
            new Color[] { Color.FromArgb(228, 228, 233), Color.FromArgb(184, 184, 192), Color.FromArgb(245, 245, 248), Color.FromArgb(207, 207, 214), Color.FromArgb(162, 162, 170) })
    };

    // 每支 accent 的中文名，给"盒子颜色"菜单用
    public static readonly string[][] AccentNames = new string[][] {
        new string[] { "薄荷", "玫瑰", "琥珀", "天青", "紫藤" },
        new string[] { "莓红", "松绿", "鸢尾", "杏橙", "兰紫" },
        new string[] { "灰褐", "灰绿", "灰粉", "灰蓝", "橄榄" },
        new string[] { "浅褐", "浅绿", "浅粉", "浅蓝", "浅橄榄" },
        new string[] { "亮灰", "中灰", "近白", "浅灰", "暗灰" }
    };
}

// 拖拽载荷：仅在同一进程内传递，Program.PendingDrag 兼作可靠通道
class DragPayload
{
    public LayeredBox Src;
    public string SrcBoxId;
    public int SrcTab;
    public List<DItem> Items;

    public DragPayload(LayeredBox src, string boxId, int tab, List<DItem> items)
    {
        Src = src; SrcBoxId = boxId; SrcTab = tab; Items = items;
    }
}

// ---------- 盒子配色对话框 ----------
// 自绘（不套系统主题），跟盒子用同一套视觉。三层各自：色块 / hex / 不透明度 0-100 / 跟随主题，
// 下面一个 7×7 调色板 + 「更多颜色…」走系统取色器兜底。
// 改动**实时**预览到真盒子上（直接改 Data.Style 再 Render），取消时按快照还原。
class StyleForm : Form
{
    LayeredBox box;
    DRoot root;
    DStyle S;
    DStyle snap;
    int editing;                 // 正在编辑哪一层：0 背景 1 边缘线 2 文字
    int dragKind = -1;           // 正在拖的滑块：0/1/2/3 对应四层外观，RADD = 圆角
    const int RADD = 4;

    Rectangle rTitle, rClose;
    Rectangle rRowBg, rRowEdge, rRowText, rRowSep, rRowRad;
    Rectangle rSwBg, rSwEdge, rSwText, rSwSep;
    Rectangle rPctBg, rPctEdge, rPctText, rPctSep, rPctRad;
    Rectangle rFollowBg, rFollowEdge, rFollowText, rFollowSep;
    Rectangle rW1, rW2, rW3;
    Rectangle rMore;
    Rectangle rApplyAll, rReset, rCancel, rSave;

    const int W = 480, H = 560;
    // 调色板：7 列 × 7 行。PAL_Y 从 288 起，给上面那行"调色板 / 编辑：xx"留出位置
    const int PAL_X = 144, PAL_Y = 288, PAL_CELL = 24, PAL_GAP = 4, PAL_COLS = 7;
    static int openCount;
    static readonly List<StyleForm> openForms = new List<StyleForm>();

    Color CBg = Color.FromArgb(28, 33, 41);
    Color CBorder = Color.FromArgb(62, 70, 82);
    Color CText = Color.FromArgb(237, 242, 248);
    Color CSub = Color.FromArgb(154, 166, 181);
    Color CField = Color.FromArgb(44, 51, 62);

    public static bool AnyOpen { get { return openCount > 0; } }

    // 程序要退出了：把还开着面板的盒子按「取消」还原。
    // Quit() 里有 Store.Save(root)，不还原的话"没点保存的预览"会被当成用户确认的改动写进配置。
    public static void CancelAllOpen()
    {
        StyleForm[] arr = openForms.ToArray();
        for (int i = 0; i < arr.Length; i++)
        {
            try { if (!arr[i].IsDisposed) arr[i].Cancel(); } catch { }
        }
    }

    // 仅给 --styleform / --stylesave 用：切层、灌参数、走保存，方便自动核对
    public void SetLayerForTest(int i) { editing = i; Invalidate(); Refresh(); }
    public Color TestPalette(int i) { return Palette[i]; }
    public void TestSet(DStyle d) { CopyTo(S, d); Preview(); }
    public void TestSave() { SaveAndClose(); }
    public void TestCancel() { Cancel(); }

    public StyleForm(LayeredBox b, DRoot r)
    {
        box = b; root = r; S = b.Data.Style;
        if (S == null) { S = new DStyle(); b.Data.Style = S; Store.RepairStyle(S); }
        snap = Clone(S);

        Text = "盒子配色";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(W, H);
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, false);
        BackColor = CBg;

        rTitle = new Rectangle(0, 0, W, 38);
        rClose = new Rectangle(W - 40, 8, 26, 22);

        int y = 52;
        rRowBg = new Rectangle(0, y, W, 40); rSwBg = new Rectangle(70, y + 8, 44, 22);
        rPctBg = new Rectangle(190, y + 20, 150, 5); rFollowBg = new Rectangle(392, y + 10, 16, 16);
        y += 40;
        rRowEdge = new Rectangle(0, y, W, 40); rSwEdge = new Rectangle(70, y + 8, 44, 22);
        rPctEdge = new Rectangle(190, y + 20, 150, 5); rFollowEdge = new Rectangle(392, y + 10, 16, 16);

        y += 40;
        rRowText = new Rectangle(0, y, W, 40); rSwText = new Rectangle(70, y + 8, 44, 22);
        rPctText = new Rectangle(190, y + 20, 150, 5); rFollowText = new Rectangle(392, y + 10, 16, 16);
        y += 40;
        rRowSep = new Rectangle(0, y, W, 40); rSwSep = new Rectangle(70, y + 8, 44, 22);
        rPctSep = new Rectangle(190, y + 20, 150, 5); rFollowSep = new Rectangle(392, y + 10, 16, 16);
        y += 40;
        rRowRad = new Rectangle(0, y, W, 40); rPctRad = new Rectangle(190, y + 20, 150, 5);

        rW1 = new Rectangle(282, PAL_Y - 26, 20, 20);
        rW2 = new Rectangle(304, PAL_Y - 26, 20, 20);
        rW3 = new Rectangle(326, PAL_Y - 26, 20, 20);
        rMore = new Rectangle(W - 118, PAL_Y - 26, 100, 22);
        rApplyAll = new Rectangle(18, 514, 132, 30);
        rReset = new Rectangle(158, 514, 132, 30);
        rCancel = new Rectangle(298, 514, 76, 30);
        rSave = new Rectangle(380, 514, 82, 30);

        openCount++;
        openForms.Add(this);
    }

    static DStyle Clone(DStyle s)
    {
        if (s == null) return null;
        return new DStyle
        {
            BgOn = s.BgOn, BgColor = s.BgColor, BgAlpha = s.BgAlpha,
            EdgeOn = s.EdgeOn, EdgeColor = s.EdgeColor, EdgeAlpha = s.EdgeAlpha, EdgeWidth = s.EdgeWidth,
            TextOn = s.TextOn, TextColor = s.TextColor, TextAlpha = s.TextAlpha,
            SepOn = s.SepOn, SepColor = s.SepColor, SepAlpha = s.SepAlpha,
            Radius = s.Radius, Ver = s.Ver
        };
    }

    static void CopyTo(DStyle d, DStyle s)
    {
        if (d == null || s == null) return;
        d.BgOn = s.BgOn; d.BgColor = s.BgColor; d.BgAlpha = s.BgAlpha;
        d.EdgeOn = s.EdgeOn; d.EdgeColor = s.EdgeColor; d.EdgeAlpha = s.EdgeAlpha; d.EdgeWidth = s.EdgeWidth;
        d.TextOn = s.TextOn; d.TextColor = s.TextColor; d.TextAlpha = s.TextAlpha;
        d.SepOn = s.SepOn; d.SepColor = s.SepColor; d.SepAlpha = s.SepAlpha;
        d.Radius = s.Radius; d.Ver = s.Ver;
    }

    // ── 取/存当前层的颜色与不透明度 ──
    static string LayerName(int i)
    {
        return i == 0 ? "背景" : (i == 1 ? "边缘线" : (i == 2 ? "文字" : "分隔线"));
    }

    Color LayerCol(int i)
    {
        if (i == 0) return box.BgCol;
        if (i == 1) return box.EdgeBase;
        if (i == 2) return box.TextBase;
        return box.SepBase;
    }
    int LayerPct(int i)
    {
        if (i == 0) return S.BgAlpha;
        if (i == 1) return S.EdgeAlpha;
        if (i == 2) return S.TextAlpha;
        return S.SepAlpha;
    }
    bool LayerFollow(int i)
    {
        if (i == 0) return !S.BgOn;
        if (i == 1) return !S.EdgeOn;
        if (i == 2) return !S.TextOn;
        return !S.SepOn;
    }
    void SetLayerCol(int i, Color c)
    {
        string hx = Store.ToHex(c);
        if (i == 0) { S.BgOn = true; S.BgColor = hx; }
        else if (i == 1) { S.EdgeOn = true; S.EdgeColor = hx; }
        else if (i == 2) { S.TextOn = true; S.TextColor = hx; }
        else { S.SepOn = true; S.SepColor = hx; }
        Preview();
    }
    void SetLayerPct(int i, int v)
    {
        if (v < 0) v = 0;
        if (v > 100) v = 100;
        if (i == 0) S.BgAlpha = v;
        else if (i == 1) S.EdgeAlpha = v;
        else if (i == 2) S.TextAlpha = v;
        else S.SepAlpha = v;
        Preview();
    }
    void SetFollow(int i, bool follow)
    {
        // 勾上"跟随主题"= 这一层交还给主题（清掉自定义色）
        if (i == 0) { S.BgOn = !follow; if (follow) S.BgColor = null; }
        else if (i == 1) { S.EdgeOn = !follow; if (follow) { S.EdgeColor = null; S.EdgeWidth = 1; } }
        else if (i == 2) { S.TextOn = !follow; if (follow) S.TextColor = null; }
        else { S.SepOn = !follow; if (follow) S.SepColor = null; }
        editing = i;
        Preview();
    }

    void Preview()
    {
        Store.RepairStyle(S);
        try { box.TestRender(); } catch { }
        Invalidate();
    }

    // ── 颜色 ──
    static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2, r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromArgb(
            (int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }

    // 7 列 × 7 行：第 1 行主题 5 支 + 纯白纯黑，中间 5 行是 7 个色相 × 5 档明度，最后一行灰阶
    static Color[] BuildPalette(Theme theme)
    {
        List<Color> list = new List<Color>();
        for (int i = 0; i < 5; i++) list.Add(theme.Accents[i % theme.Accents.Length]);
        list.Add(Color.White);
        list.Add(Color.FromArgb(13, 13, 18));
        double[] lv = { 0.30, 0.42, 0.54, 0.66, 0.80 };
        double[] sv = { 0.55, 0.68, 0.72, 0.58, 0.40 };
        for (int r = 0; r < 5; r++)
            for (int c = 0; c < 7; c++)
                list.Add(FromHsl(c * 360.0 / 7.0, sv[r], lv[r]));
        int[] g = { 255, 230, 194, 154, 110, 63, 26 };
        for (int i = 0; i < 7; i++) list.Add(Color.FromArgb(g[i], g[i], (int)Math.Min(255, g[i] + 4)));
        return list.ToArray();
    }

    Color[] palette;
    Color[] Palette { get { if (palette == null) palette = BuildPalette(box.T); return palette; } }

    Rectangle PalCell(int idx)
    {
        int r = idx / PAL_COLS, c = idx % PAL_COLS;
        return new Rectangle(PAL_X + c * (PAL_CELL + PAL_GAP), PAL_Y + r * (PAL_CELL + PAL_GAP), PAL_CELL, PAL_CELL);
    }

    // ── 画 ──
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using (GraphicsPath bp = LayeredBox.Round(new Rectangle(0, 0, W - 1, H - 1), 12))
        using (SolidBrush b = new SolidBrush(CBg)) g.FillPath(b, bp);
        using (GraphicsPath bp = LayeredBox.Round(new Rectangle(0, 0, W - 1, H - 1), 12))
        using (Pen p = new Pen(CBorder, 1)) g.DrawPath(p, bp);

        using (Font fT = new Font("Microsoft YaHei", 15f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Font fL = new Font("Microsoft YaHei", 13f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Font fS = new Font("Microsoft YaHei", 12f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Font fX = new Font("Consolas", 16f, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            Txt(g, box.Data.Title + " · 配色", fT, CText, new Rectangle(18, 10, W - 80, 20));
            Txt(g, "×", fX, CSub, rClose, true);

            DrawLayerRow(g, 0, "背景", rRowBg, rSwBg, rPctBg, rFollowBg, fL, fS);
            DrawLayerRow(g, 1, "边缘线", rRowEdge, rSwEdge, rPctEdge, rFollowEdge, fL, fS);
            DrawLayerRow(g, 2, "文字", rRowText, rSwText, rPctText, rFollowText, fL, fS);
            DrawLayerRow(g, 3, "分隔线", rRowSep, rSwSep, rPctSep, rFollowSep, fL, fS);

            // 圆角
            Txt(g, "圆角", fL, CSub, new Rectangle(18, rRowRad.Y + 12, 48, 18));
            DrawSlider(g, rPctRad, (S.Radius - 0) / 28f, box.accent);
            Txt(g, S.Radius + " px", fS, CText, new Rectangle(348, rRowRad.Y + 12, 60, 16));

            using (Pen p = new Pen(Color.FromArgb(46, CBorder), 1))
                g.DrawLine(p, 18, rRowRad.Bottom + 4, W - 18, rRowRad.Bottom + 4);

            Txt(g, "调色板", fS, CSub, new Rectangle(18, PAL_Y - 24, 60, 18));
            Txt(g, "编辑：" + LayerName(editing), fS, box.accent,
                new Rectangle(78, PAL_Y - 24, 158, 18));

            // 边缘线粗细 1/2/3：常驻在这一行，避免切层时控件跳动；不在边缘线层时淡化
            bool edgeActive = editing == 1;
            Txt(g, "粗细", fS, edgeActive ? CText : CSub, new Rectangle(244, PAL_Y - 26, 36, 20), true);
            for (int i = 0; i < 3; i++)
            {
                Rectangle rr = i == 0 ? rW1 : (i == 1 ? rW2 : rW3);
                bool on = S.EdgeWidth == i + 1;
                using (SolidBrush b = new SolidBrush(on ? box.accent : CField)) g.FillRectangle(b, rr);
                using (Pen p = new Pen(Color.FromArgb(on ? 200 : 80, on ? box.accent : CBorder), 1)) g.DrawRectangle(p, rr);
                Txt(g, (i + 1).ToString(), fS, on ? Color.White : CText, rr, true);
            }

            using (SolidBrush b = new SolidBrush(CField))
            using (GraphicsPath p = LayeredBox.Round(rMore, 6)) g.FillPath(b, p);
            Txt(g, "更多颜色…", fS, CText, rMore, true);

            for (int i = 0; i < Palette.Length; i++)
            {
                Rectangle rr = PalCell(i);
                using (SolidBrush b = new SolidBrush(Palette[i])) g.FillRectangle(b, rr);
                using (Pen p = new Pen(Color.FromArgb(70, 0, 0, 0), 1)) g.DrawRectangle(p, rr);
            }

            Txt(g, "改动会立刻作用在盒子上；「保存」写进配置，「取消」还原。", fS, CSub,
                new Rectangle(18, 490, W - 36, 16));

            Btn(g, rApplyAll, "应用到全部盒子", false, fL);
            Btn(g, rReset, "恢复跟随主题", false, fL);
            Btn(g, rCancel, "取消", false, fL);
            Btn(g, rSave, "保存", true, fL);
        }
    }

    void DrawLayerRow(Graphics g, int idx, string label, Rectangle row, Rectangle sw, Rectangle sl, Rectangle fw, Font fL, Font fS)
    {
        if (editing == idx)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(20, box.accent))) g.FillRectangle(b, 8, row.Y + 2, W - 16, row.Height - 4);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(235, box.accent))) g.FillRectangle(b, 8, row.Y + 6, 3, row.Height - 12);
        }
        Txt(g, label, fL, editing == idx ? CText : CSub, new Rectangle(20, row.Y + 11, 48, 18));

        // 色块（勾选了"跟随主题"时画成斜纹 + 淡色，表示这一层不由自定义控制）
        bool follow = LayerFollow(idx);
        Color col = LayerCol(idx);
        using (SolidBrush b = new SolidBrush(follow ? LayeredBox.Mix(col, CBg, 0.55f) : col)) g.FillRectangle(b, sw);
        using (Pen p = new Pen(Color.FromArgb(120, CBorder), 1)) g.DrawRectangle(p, sw);

        Txt(g, LayerFollow(idx) ? "跟随主题" : Store.ToHex(col), fS,
            follow ? CSub : CText, new Rectangle(122, row.Y + 13, 64, 16));

        DrawSlider(g, sl, LayerPct(idx) / 100f, box.accent);
        Txt(g, LayerPct(idx) + "%", fS, CText, new Rectangle(346, row.Y + 12, 40, 16));

        // "跟随主题"勾选框
        using (SolidBrush b = new SolidBrush(follow ? box.accent : CField)) g.FillRectangle(b, fw);
        using (Pen p = new Pen(Color.FromArgb(150, CBorder), 1)) g.DrawRectangle(p, fw);
        if (follow)
        {
            using (Pen p = new Pen(Color.White, 2f))
                g.DrawLines(p, new Point[] {
                    new Point(fw.X + 3, fw.Y + 8), new Point(fw.X + 7, fw.Y + 12), new Point(fw.X + 13, fw.Y + 4) });
        }
        Txt(g, "跟随", fS, CSub, new Rectangle(fw.Right + 4, fw.Y, 34, 16));
    }

    void DrawSlider(Graphics g, Rectangle r, float t, Color accent)
    {
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        int cy = r.Y + r.Height / 2;
        using (SolidBrush b = new SolidBrush(CField)) g.FillRectangle(b, r.X, r.Y, r.Width, r.Height);
        int fill = (int)Math.Round(r.Width * t);
        using (SolidBrush b = new SolidBrush(accent)) g.FillRectangle(b, r.X, r.Y, fill, r.Height);
        int kx = r.X + fill;
        using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, kx - 5, cy - 5, 10, 10);
        using (Pen p = new Pen(Color.FromArgb(90, 0, 0, 0), 1)) g.DrawEllipse(p, kx - 5, cy - 5, 10, 10);
    }

    void Btn(Graphics g, Rectangle r, string text, bool primary, Font f)
    {
        using (SolidBrush b = new SolidBrush(primary ? box.accent : CField))
        using (GraphicsPath p = LayeredBox.Round(r, 6)) g.FillPath(b, p);
        using (GraphicsPath p = LayeredBox.Round(r, 6))
        using (Pen pen = new Pen(Color.FromArgb(70, CBorder), 1)) g.DrawPath(pen, p);
        Txt(g, text, f, primary ? Color.White : CText, r, true);
    }

    void Txt(Graphics g, string s, Font f, Color c, Rectangle r) { Txt(g, s, f, c, r, false); }

    void Txt(Graphics g, string s, Font f, Color c, Rectangle r, bool center)
    {
        using (StringFormat sf = new StringFormat())
        {
            sf.Alignment = center ? StringAlignment.Center : StringAlignment.Near;
            sf.LineAlignment = center ? StringAlignment.Center : StringAlignment.Near;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, f, b, r, sf);
        }
    }

    // ── 鼠标 ──
    Point downAt;
    bool draggingWindow;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        downAt = e.Location;

        if (rClose.Contains(e.Location)) { Cancel(); return; }
        if (rTitle.Contains(e.Location)) { draggingWindow = true; return; }

        if (rSave.Contains(e.Location)) { SaveAndClose(); return; }
        if (rCancel.Contains(e.Location)) { Cancel(); return; }
        if (rApplyAll.Contains(e.Location)) { ApplyAll(); return; }
        if (rReset.Contains(e.Location)) { ResetAll(); return; }
        if (rMore.Contains(e.Location)) { MoreColor(); return; }

        if (rW1.Contains(e.Location)) { S.EdgeWidth = 1; editing = 1; S.EdgeOn = true; Preview(); return; }
        if (rW2.Contains(e.Location)) { S.EdgeWidth = 2; editing = 1; S.EdgeOn = true; Preview(); return; }
        if (rW3.Contains(e.Location)) { S.EdgeWidth = 3; editing = 1; S.EdgeOn = true; Preview(); return; }

        // 三层：色块点开取色器，勾选框切"跟随主题"
        if (HitSwatch(rSwBg, e.Location)) { editing = 0; PickColor(0); return; }
        if (HitSwatch(rSwEdge, e.Location)) { editing = 1; PickColor(1); return; }
        if (HitSwatch(rSwText, e.Location)) { editing = 2; PickColor(2); return; }
        if (HitSwatch(rSwSep, e.Location)) { editing = 3; PickColor(3); return; }
        if (rFollowBg.Contains(e.Location)) { SetFollow(0, !LayerFollow(0)); return; }
        if (rFollowEdge.Contains(e.Location)) { SetFollow(1, !LayerFollow(1)); return; }
        if (rFollowText.Contains(e.Location)) { SetFollow(2, !LayerFollow(2)); return; }
        if (rFollowSep.Contains(e.Location)) { SetFollow(3, !LayerFollow(3)); return; }

        // 点滑轨直接跳到那个值，然后进入拖动
        if (HitSlider(rPctBg, e.Location)) { editing = 0; dragKind = 0; DragSlider(e.X); return; }
        if (HitSlider(rPctEdge, e.Location)) { editing = 1; dragKind = 1; DragSlider(e.X); return; }
        if (HitSlider(rPctText, e.Location)) { editing = 2; dragKind = 2; DragSlider(e.X); return; }
        if (HitSlider(rPctSep, e.Location)) { editing = 3; dragKind = 3; DragSlider(e.X); return; }
        if (HitSlider(rPctRad, e.Location)) { dragKind = RADD; DragSlider(e.X); return; }

        // 调色板
        for (int i = 0; i < Palette.Length; i++)
        {
            if (!PalCell(i).Contains(e.Location)) continue;
            SetLayerCol(editing, Palette[i]);
            return;
        }

        // 点层级行的空白处 = 切换当前编辑层
        if (rRowBg.Contains(e.Location)) { editing = 0; Invalidate(); return; }
        if (rRowEdge.Contains(e.Location)) { editing = 1; Invalidate(); return; }
        if (rRowText.Contains(e.Location)) { editing = 2; Invalidate(); return; }
        if (rRowSep.Contains(e.Location)) { editing = 3; Invalidate(); return; }
    }

    static bool HitSwatch(Rectangle r, Point p)
    {
        return Rectangle.Inflate(r, 2, 2).Contains(p);
    }
    static bool HitSlider(Rectangle r, Point p)
    {
        return new Rectangle(r.X - 6, r.Y - 8, r.Width + 12, r.Height + 16).Contains(p);
    }

    void DragSlider(int x)
    {
        Rectangle r = dragKind == 0 ? rPctBg : (dragKind == 1 ? rPctEdge : (dragKind == 2 ? rPctText : (dragKind == 3 ? rPctSep : rPctRad)));
        float t = (x - r.X) / (float)r.Width;
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        if (dragKind == RADD) { S.Radius = (int)Math.Round(t * 28); Preview(); }
        else SetLayerPct(dragKind, (int)Math.Round(t * 100));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (draggingWindow && (e.Button & MouseButtons.Left) != 0)
        {
            Point d = new Point(e.X - downAt.X, e.Y - downAt.Y);
            Location = new Point(Location.X + d.X, Location.Y + d.Y);
            return;
        }
        if (dragKind >= 0 && (e.Button & MouseButtons.Left) != 0) DragSlider(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        draggingWindow = false;
        dragKind = -1;
    }

    void PickColor(int layer)
    {
        using (ColorDialog cd = new ColorDialog())
        {
            cd.Color = LayerCol(layer);
            cd.FullOpen = true;
            cd.AnyColor = true;
            if (cd.ShowDialog(this) == DialogResult.OK) SetLayerCol(layer, cd.Color);
            else Invalidate();
        }
    }

    void MoreColor()
    {
        PickColor(editing);
    }

    void ResetAll()
    {
        S.BgOn = false; S.BgColor = null; S.BgAlpha = 58;
        S.EdgeOn = false; S.EdgeColor = null; S.EdgeAlpha = 30; S.EdgeWidth = 1;
        S.TextOn = false; S.TextColor = null; S.TextAlpha = 100;
        S.SepOn = false; S.SepColor = null; S.SepAlpha = 12;
        S.Radius = 14;
        Preview();
    }

    void ApplyAll()
    {
        int n = 0;
        foreach (DBox b in root.Boxes)
        {
            if (b.Style == null) b.Style = new DStyle();
            CopyTo(b.Style, S);
            Store.RepairStyle(b.Style);
            n++;
        }
        Store.Save(root);
        for (int i = 0; i < Program.forms.Count; i++) Program.forms[i].TestRender();
        Log.W("配色 应用到全部盒子 " + n + " 个");
        Close();
    }

    void SaveAndClose()
    {
        Store.Save(root);
        Log.W("配色 保存 " + box.Data.Title);
        Close();
    }

    void Cancel()
    {
        CopyTo(S, snap);
        try { box.TestRender(); } catch { }
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        openForms.Remove(this);
        openCount--;
        if (openCount < 0) openCount = 0;
        base.OnFormClosed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Cancel(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

class LayeredBox : Form
{
    public DBox Data;
    DRoot root;
    public Color accent;

    public Theme T
    {
        get
        {
            int i = Data.ThemeId;
            if (i < 0 || i >= Themes.All.Length) i = 0;
            return Themes.All[i];
        }
    }

    // 调试用：强制指定底板不透明度（--plate 0.75），只用于出图比档，正常运行走 Style.BgAlpha
    public static double PlateAlphaOverride = -1;

    // 老的 6 档表。**只留给 Store 做老配置迁移**，绘制已经改成读 Style.BgAlpha（0-100 连续值）。
    public static readonly double[] OpacitySteps = { 0.58, 0.66, 0.74, 0.82, 0.90, 0.98 };
    public const int OPACITY_STEPS = 6;

    public int OpStep
    {
        get
        {
            int i = Data.OpacityStep;
            if (i < 0) i = 0;
            if (i > OPACITY_STEPS - 1) i = OPACITY_STEPS - 1;
            return i;
        }
    }

    DStyle S { get { return Data.Style; } }

    // ── 三层外观（背景 / 边缘线 / 文字）+ 圆角 ─────────────────────
    // 每一层：Style 里 On=true 且存了值就用它，否则回落到主题。这样 5 套主题还能当预设起点用。

    public Color BgCol
    {
        get
        {
            if (S != null && S.BgOn && S.BgColor != null) return Store.ParseHex(S.BgColor, T.Plate);
            return T.Plate;
        }
    }

    public int BgPct { get { return (S != null && S.BgAlpha >= 0) ? S.BgAlpha : 58; } }

    double PlateAlpha
    {
        get
        {
            if (PlateAlphaOverride >= 0) return PlateAlphaOverride > 1 ? 1 : PlateAlphaOverride;
            return BgPct / 100.0;
        }
    }

    public int RadiusVal { get { return (S != null && S.Radius >= 0) ? Math.Min(28, S.Radius) : 14; } }

    public int EdgePct { get { return (S != null && S.EdgeAlpha >= 0) ? S.EdgeAlpha : 30; } }
    public Color EdgeBase
    {
        get
        {
            if (S != null && S.EdgeOn && S.EdgeColor != null) return Store.ParseHex(S.EdgeColor, Color.White);
            return LightPlate ? Color.FromArgb(26, 22, 28) : Color.White;
        }
    }
    public float EdgeW { get { return (S != null && S.EdgeWidth >= 1) ? S.EdgeWidth : 1; } }

    public Color TextBase
    {
        get
        {
            if (S != null && S.TextOn && S.TextColor != null) return Store.ParseHex(S.TextColor, T.Text);
            return T.Text;
        }
    }
    int TextPct { get { return (S != null && S.TextAlpha >= 0) ? S.TextAlpha : 100; } }

    // 文字色 + 额外倍率 k（原来那些 FromArgb(250, T.Text) 的地方改走这里，
    // 这样"文字不透明度"才能整体生效）
    public Color TextA(float k) { return Color.FromArgb(AlphaOf(TextPct, k), TextBase); }

    // 次要文字（未选中标签、+ ×、空提示）：自定义文字色时按"文字色向底色混 45%"自动派生，
    // 层级关系不塌；没自定义就沿用主题的 Sub。
    public Color SubBase
    {
        get
        {
            if (S != null && S.TextOn && S.TextColor != null) return Mix(TextBase, BgCol, 0.45f);
            return T.Sub;
        }
    }
    public Color SubA(float k) { return Color.FromArgb(AlphaOf(TextPct, k), SubBase); }

    // 标签栏下方那条分隔线（第四层外观）。没自定义时等价于老的 Tint(30)。
    public int SepPct { get { return (S != null && S.SepAlpha >= 0) ? S.SepAlpha : 12; } }
    public Color SepBase
    {
        get
        {
            if (S != null && S.SepOn && S.SepColor != null) return Store.ParseHex(S.SepColor, T.Text);
            return LightPlate ? Color.FromArgb(26, 24, 28) : Color.White;
        }
    }
    public Color SepLine { get { return Color.FromArgb(AlphaOf(SepPct, 1f), SepBase); } }

    static int AlphaOf(int pct, float k)
    {
        int a = (int)Math.Round(pct * 2.55 * k);
        if (a < 0) a = 0;
        if (a > 255) a = 255;
        return a;
    }

    public static Color Mix(Color a, Color b, float t)
    {
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    // 玻璃拟态的两件套：外描边 + 顶部高光。暗底用白线，亮底用近黑线。
    // 描边是这张卡唯一的边界，太淡就"没有卡"了：亮底尤其需要，否则和浅色壁纸糊在一起。
    Color EdgeColor
    {
        get { return Color.FromArgb(AlphaOf(EdgePct, 1f), EdgeBase); }
    }
    Color TopHi
    {
        get { return LightPlate ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(38, 255, 255, 255); }
    }
    // 底板很透的时候（<60%），文字下面垫一层同色暗影，才能压在花壁纸上还看得清
    Color TextShadow
    {
        get { return LightPlate ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0); }
    }

    // 明暗按**有效背景色**判断（自定义背景后主题的 Plate 不再是准的）
    public bool LightPlate
    {
        get
        {
            Color c = BgCol;
            return (c.R + c.G + c.B) / 3 > 140;
        }
    }

    Color Tint(int a)
    {
        return LightPlate ? Color.FromArgb(a, 26, 24, 28) : Color.FromArgb(a, 255, 255, 255);
    }

    int curTab;
    int scrollY;
    int hoverTile = -1;
    int hoverTab = -1;
    bool headHot;      // 鼠标在标题栏上：操作控件（滑块/锁/加/关）从淡出恢复亮度
    bool resizing;
    bool pendingDrag;
    Point downPoint;
    bool draggingWindow;
    Point resizeStart;
    Size sizeStart;

    // 自定义拖动 + 吸附对齐
    bool windowDrag;
    Point dragStartMouse;
    Point dragStartPos;
    int snapEdgeX = -1;   // -1 无, 0 左边, 1 右边, 2 中线
    int snapEdgeY = -1;
    Bitmap frame;
    const int SNAP = 10;

    // 拖出（拖到桌面 / 浏览器上传框）时的视觉状态
    DragGhost dragGhost;    // 跟随鼠标的小方块
    bool dragLift;          // 拖动中：被拖的图标画成半透明（"已经被拎起来了"）
    static readonly List<Timer> pruneTimers = new List<Timer>();   // 拖动结束后延时核对文件是否已被移走

    // 选择
    HashSet<int> sel = new HashSet<int>();
    HashSet<int> bandBase = new HashSet<int>();
    int selAnchor = -1;
    bool bandSelecting;
    Point bandStart, bandCur;
    bool deferClear;
    bool movedSincePress;
    int pressTile = -1;
    Point pressPoint;

    // 拖放反馈
    bool dragOver;
    bool dropOnTab;
    int dropTabHi = -1;
    int dropIndex = -1;

    // 双击判定
    int lastClickTile = -1;
    int lastClickTicks;

    const int HEAD_H = 34;
    const int STRIP_H = 28;
    const int TILE_W = 78;
    const int TILE_H = 80;
    const int ICON = 48;
    const int GRIP = 18;
    const int MIN_OPACITY = 20;

    Rectangle headerRect, closeRect, addRect, lockRect, stripRect, contentRect;
    List<Rectangle> tabRects = new List<Rectangle>();
    List<Rectangle> tileRects = new List<Rectangle>();

    // 全部用整数像素字号（GraphicsUnit.Pixel）。原先的 8.2f/8.5f 是“磅”，
    // 换算成像素是 10.93 / 11.33 这种小数，每个字形都落在半像素上 → 一律发虚。
    Font fTitle = new Font("Microsoft YaHei", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
    Font fTab = new Font("Microsoft YaHei", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    Font fLbl = new Font("Microsoft YaHei", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    Font fSmall = new Font("Microsoft YaHei", 11f, FontStyle.Regular, GraphicsUnit.Pixel);

    const int WM_NCLBUTTONDOWN = 0xA1;
    const int HTCAPTION = 0x2;
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    public LayeredBox(DRoot r, DBox d)
    {
        root = r;
        Data = d;
        accent = T.Accents[(d.Accent % T.Accents.Length + T.Accents.Length) % T.Accents.Length];
        Text = "DeskBox - " + d.Title;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(d.X, d.Y, d.W, d.H);
        AllowDrop = true;
        BackColor = Color.FromArgb(13, 16, 22);
        SetStyle(ControlStyles.Opaque, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        // 关键：显式打开标准单击/双击，保证能收到 WM_LBUTTONDBLCLK
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        KeyPreview = true;
        LogGeom("ctor");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LogGeom("handleCreated");
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab、不在任务栏
            return cp;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    const int WM_SYSCOMMAND = 0x0112;
    const int SC_MINIMIZE = 0xF020;
    const int WM_WINDOWPOSCHANGING = 0x0046;
    const int WM_SHOWWINDOW = 0x0018;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    static readonly IntPtr HWND_BOTTOM = (IntPtr)1;
    static readonly IntPtr HWND_TOP = IntPtr.Zero;
    static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);

    // ── 桌面子窗口化（Win11 24H2+ 唯一可靠的做法） ─────────────────────
    // --diag 实测：点“显示桌面”时 vis=1 / iconic=0 / cloak=0 / top=0 全部正常，
    // 只有 under 从“自己”变成 SysListView32 —— 盒子被桌面图标层盖住了。
    // 那一刻整个桌面窗口是 topmost 的，顶层窗口怎么调 z 序都赢不了它
    // （Rainmeter 在 24H2 上就是这么废掉的）。所以改成：
    // 把自己 SetParent 成桌面窗口（Progman / WorkerW）的子窗口，并排在桌面图标层之前。
    // 子窗口不是顶层窗口 → “显示桌面”根本不碰它，桌面被抬起时它跟着一起走。
    // Wallpaper Engine / Lively / Fences 都是这个路子。
    public static bool UseDesktopChild = false;  // 本机实测：分层子窗口 DWM 不合成（画面全透明），默认关闭

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string win);
    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr h, ref POINT p);

    static IntPtr FindDesktopHost()
    {
        try
        {
            IntPtr prog = FindWindow("Progman", null);
            if (prog == IntPtr.Zero) return IntPtr.Zero;
            // 桌面图标列表 SHELLDLL_DefView 的父窗口才是“桌面容器”。
            // 把盒子做成它的兄弟、并排在它前面，就能压住桌面图标（Fences 的效果）。
            IntPtr defView = FindWindowEx(prog, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                IntPtr par = GetParent(defView);
                if (par != IntPtr.Zero) return par;
            }
            return prog;
        }
        catch { return IntPtr.Zero; }
    }

    IntPtr lastParentLogged;

    const int WS_CHILD = 0x40000000;
    const int WS_POPUP = unchecked((int)0x80000000);
    const int GWL_STYLE = -16;
    [DllImport("kernel32.dll")]
    static extern void SetLastError(int err);

    // 让盒子真正成为桌面的**子窗口**。
    // 注意：SetParent 不会改窗口样式——对 WS_POPUP 窗口调用它只是换了 owner，
    // 窗口仍然是顶层窗口（实测 child=0，显示桌面照样被盖住）。
    // 必须先把 WS_POPUP 换成 WS_CHILD，再 SetParent，最后 SWP_FRAMECHANGED 生效。
    void AdoptDesktopParent()
    {
        if (!UseDesktopChild || IsDisposed || Disposing || !IsHandleCreated || AllowHide) return;
        try
        {
            IntPtr host = FindDesktopHost();
            if (host == IntPtr.Zero) { Log.W("adopt: host=0"); return; }
            IntPtr cur = GetAncestor(Handle, 1);        // GA_PARENT
            int style0 = GetWindowLong(Handle, GWL_STYLE);
            bool isChild = (style0 & WS_CHILD) != 0;
            if (cur == host && isChild) return;

            RECT r;
            if (!GetWindowRect(Handle, out r)) return;

            if (!isChild)
            {
                int st = (style0 & ~WS_POPUP) | WS_CHILD;
                SetWindowLong(Handle, GWL_STYLE, st);
            }
            SetParent(Handle, host);

            POINT org;
            org.X = 0; org.Y = 0;
            if (!ClientToScreen(host, ref org)) { org.X = 0; org.Y = 0; }

            // 目标位置用配置里的屏幕坐标（Data.X/Y）为准，而不是当前窗口矩形：
            // 改成 WS_CHILD 之后窗口矩形会带上一点非客户区偏移（实测 +8/+31），
            // 所以设完之后再按真实 GetWindowRect 纠一次差，保证盒子停在原来的位置。
            int wantX = Data.X, wantY = Data.Y;
            SetWindowPos(Handle, HWND_TOP, wantX - org.X, wantY - org.Y, 0, 0,
                SWP_NOSIZE | SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
            RECT got;
            if (GetWindowRect(Handle, out got))
            {
                int dx = wantX - got.L, dy = wantY - got.T;
                if (dx != 0 || dy != 0)
                {
                    SetWindowPos(Handle, HWND_TOP, wantX - org.X + dx, wantY - org.Y + dy, 0, 0,
                        SWP_NOSIZE | SWP_NOACTIVATE);
                    GetWindowRect(Handle, out got);
                }
            }

            if (cur != lastParentLogged)
            {
                lastParentLogged = cur;
                Log.W("parent -> " + ClassOf(host) + " hwnd=0x" + host.ToInt64().ToString("X") +
                      " child=" + (((GetWindowLong(Handle, GWL_STYLE) & WS_CHILD) != 0) ? 1 : 0) +
                      " org=" + org.X + "," + org.Y +
                      " from=" + r.L + "," + r.T +
                      " (was " + (cur == IntPtr.Zero ? "0" : ClassOf(cur)) + ")");
            }
            else
            {
                Log.W("parent retry, 之前设的没保住  was=" + ClassOf(cur));
            }
        }
        catch (Exception ex) { Log.W("parent fail " + ex.Message); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextLength(IntPtr h);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc2 cb, IntPtr p);
    delegate bool EnumProc2(IntPtr h, IntPtr p);

    // 排障：把"配置坐标 / 托管坐标 / 真实窗口矩形 / 客户区原点"一次列清（查位置偏移用）
    // 平时只留 shown-end 一条（一眼看出重启后有没有跑位）；
    // 加命令行 `--geomlog` 才把 ctor / handleCreated / apply#N 那些中间态全打出来。
    public static bool GeomVerbose;
    void LogGeom(string tag)
    {
        if (!GeomVerbose && tag != "shown-end") return;
        try
        {
            string rs = "noHandle";
            RECT r;
            if (IsHandleCreated && GetWindowRect(Handle, out r)) rs = r.L + "," + r.T + "," + r.R + "," + r.B;
            Log.W("geom " + tag + " bounds=" + Bounds.X + "," + Bounds.Y + " " + Bounds.Width + "x" + Bounds.Height +
                  " loc=" + Left + "," + Top + " size=" + Width + "x" + Height +
                  " clientOrg=" + (IsHandleCreated ? PointToScreen(Point.Empty).ToString() : "-") +
                  " winRect=" + rs +
                  " style=" + (IsHandleCreated ? "0x" + GetWindowLong(Handle, -16).ToString("X") : "-"));
        }
        catch { }
    }

    public string GeomDump()
    {
        string rs = "?";
        try { RECT r; if (GetWindowRect(Handle, out r)) rs = r.L + "," + r.T + "," + r.R + "," + r.B; } catch { }
        int orgx = -1, orgy = -1;
        try { Point sp = PointToScreen(Point.Empty); orgx = sp.X; orgy = sp.Y; } catch { }
        return "cfg=" + Data.X + "," + Data.Y + " " + Data.W + "x" + Data.H +
               " | bounds=" + Bounds.X + "," + Bounds.Y + " " + Bounds.Width + "x" + Bounds.Height +
               " | loc=" + Left + "," + Top + " size=" + Width + "x" + Height +
               " client=" + ClientSize.Width + "x" + ClientSize.Height +
               " | clientOrg=" + orgx + "," + orgy +
               " | winRect=" + rs +
               " | dpi=" + (IsHandleCreated ? DeviceDpi : 0) +
               " scale=" + (IsHandleCreated ? (Width * 96 / Math.Max(1, Data.W)) : 0) + "%";
    }

    // 排障用：把本进程所有顶层窗口列出来（类名 / owner / 样式 / 标题 / 矩形）。
    // 桌面右键菜单开着的时候前台偶尔会是本进程的某个窗口（类名
    // WindowsForms10.Window.8…），光看类名猜不出它是谁，这个清单能直接定位。
    public static string DumpOwnWindows()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        try
        {
            List<IntPtr> all = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid == SelfPid) all.Add(h);
                return true;
            }, IntPtr.Zero);
            foreach (IntPtr h in all)
            {
                RECT r; bool hasRect = GetWindowRect(h, out r);
                int tl = GetWindowTextLength(h);
                string title = "-";
                if (tl > 0)
                {
                    System.Text.StringBuilder tb = new System.Text.StringBuilder(tl + 1);
                    GetWindowText(h, tb, tb.Capacity);
                    if (tb.Length > 0) title = tb.ToString();
                }
                IntPtr o = GetWindow(h, GW_OWNER);
                sb.Append("  0x").Append(h.ToInt64().ToString("X"))
                  .Append(" cls=").Append(ClassOf(h))
                  .Append(" owner=").Append(o == IntPtr.Zero ? "0" : ClassOf(o) + "/0x" + o.ToInt64().ToString("X"))
                  .Append(" vis=").Append(IsWindowVisible(h) ? 1 : 0)
                  .Append(" iconic=").Append(IsIconic(h) ? 1 : 0)
                  .Append(" ex=0x").Append(GetWindowLong(h, -20).ToString("X"))
                  .Append(" rect=").Append(hasRect ? (r.L + "," + r.T + "," + r.R + "," + r.B) : "?")
                  .Append(" title=").Append(title)
                  .AppendLine();
            }
            if (all.Count == 0) sb.AppendLine("  (无窗口)");
        }
        catch (Exception ex) { sb.AppendLine("  dump fail " + ex.Message); }
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")]
    static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr h, int idx, int val);
    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr h);

    // 只有我们自己主动隐藏时才允许隐藏（“显示桌面”之类的系统隐藏请求会被忽略）
    public bool AllowHide;

    protected override void SetVisibleCore(bool value)
    {
        if (!value && !AllowHide && IsHandleCreated && !Disposing) return;
        base.SetVisibleCore(value);
    }

    public void HideByUser()
    {
        AllowHide = true;
        try { Hide(); } finally { AllowHide = false; }
    }

    // 钉回桌面：显示出来 + 保持在桌面这一层
    public void KeepOnDesktop()
    {
        if (IsDisposed || Disposing || AllowHide) return;
        try
        {
            if (!Visible) { base.SetVisibleCore(true); }
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            AdoptDesktopParent();
            SetWindowPos(Handle, UseDesktopChild ? HWND_TOP : HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        if (DiagMode) Program.DiagMsg(this, m.Msg, m.WParam, m.LParam);

        // 任务栏“显示桌面”/Win+D/Win+M 会给每个顶层窗口发 SC_MINIMIZE，
        // 盒子要常驻桌面，这里直接吞掉，不让它最小化。
        if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == SC_MINIMIZE)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        // 任何改变 z 序的请求都改写成“在我们这一层的最上面”：
        // 子窗口模式下，同一父窗口（桌面）里我们就是最前，程序窗口仍然天然盖住我们；
        // 回退模式（--top）下则沉到最底，永远在程序窗口下面。
        if (m.Msg == WM_WINDOWPOSCHANGING)
        {
            try
            {
                // 注意：临时浮于桌面之上时（keepTopmost），这里的改写必须写成 HWND_TOPMOST，
                // 否则会把自己刚设的 topmost 立刻压回底部 —— 之前“浮不起来”就是这个原因。
                IntPtr want = keepTopmost ? HWND_TOPMOST
                             : (UseDesktopChild ? HWND_TOP : HWND_BOTTOM);
                WINDOWPOS wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
                if ((wp.flags & SWP_NOZORDER) == 0 && wp.hwndInsertAfter != want)
                {
                    wp.hwndInsertAfter = want;
                    wp.flags |= SWP_NOACTIVATE;
                    Marshal.StructureToPtr(wp, m.LParam, false);
                }
            }
            catch { }
        }

        base.WndProc(ref m);
    }

    // 判定 / 观察用的取样工具（--probe、--watchtest 和守护自检都用它）。
    // 正常运行时盒子不做任何 z 序决策，这些只用来“确认没问题”。
    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }
    const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    Timer desktopGuard;             // 安全网 + “显示桌面”应对
    bool keepTopmost;               // 临时浮在桌面之上（仅在桌面真的盖住盒子时）
    int topmostSince;
    int appVisSince;                // 程序窗口连续可见的起点（防单帧误判）
    int coveredSince;               // 被桌面盖住连续成立的起点（防单帧误判）
    int raiseWantedSince;           // 想浮起来连续成立的起点（两个 raise 分支共用）
    bool deskShownState;            // 稳定后的“桌面独占”状态：所有 z 序决策只读它
    bool deskRawLast;               // 上一次采到的瞬时值
    int deskRawSince;               // 当前瞬时值持续成立的起点
    static int dragGuardUntil;      // 拖放期间冻结 z 序：OLE 靠 WindowFromPoint 找 target，
                                    // 盒子上下乱跳会让 drop target 不断失效
    public static int WatchTicks;   // 自测用：采样 tick 次数
    public static string ClassNameOf(IntPtr h) { return ClassOf(h); }
    public static bool IsOwnHwnd(IntPtr h) { return IsOwnWindow(h); }
    public static bool DisableAuto;  // 自测用：关掉守护纠错，纯观察
    public static bool DiagMode;     // --diag：记录收到的消息

    // ── 壳自己的浮层（桌面右键菜单、开始菜单、通知中心…） ──────────────────
    // Win11 上右键桌面时，前台会变成 XamlExplorerHostIslandWindow_WASDK，菜单本体是
    // Microsoft.UI.Content.PopupWindowSiteBridge，两个都是 explorer 进程里的窗口。
    // 把它们当成"程序窗口"，盒子就会不停 下沉→被桌面压住→浮起→再下沉，表现为持续闪烁。
    // 判据用"宿主进程是 explorer 且不是资源管理器窗口"：类名每个版本都在变，抓进程更稳。
    static readonly Dictionary<uint, string> procNameCache = new Dictionary<uint, string>();
    static string ProcNameCached(uint pid)
    {
        string n;
        if (procNameCache.TryGetValue(pid, out n)) return n;
        n = "";
        try { n = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { n = ""; }
        if (procNameCache.Count > 64) procNameCache.Clear();
        procNameCache[pid] = n;
        return n;
    }

    public static bool IsShellOverlay(IntPtr h, string c)
    {
        if (h == IntPtr.Zero) return false;
        // 资源管理器窗口本身是用户真正在用的程序窗口，必须放行（盒子该沉到它下面）
        if (c == "CabinetWClass" || c == "ExploreWClass") return false;
        if (IsDesktopClass(c)) return false;
        // #32769 就是桌面自己（显示桌面时它在前台），不是浮层
        if (c == "#32769") return false;
        // 经典 #32768 菜单是被 Progman/WorkerW own 的，也走这条
        for (int i = 0; i < 3; i++)
        {
            IntPtr o = GetWindow(h, GW_OWNER);
            if (o == IntPtr.Zero) break;
            string oc = ClassOf(o);
            if (oc == "Progman" || oc == "WorkerW" || oc == "Shell_TrayWnd" ||
                oc == "Shell_Secondary_TrayWnd" || oc == "SHELLDLL_DefView") return true;
            h = o;
        }
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (pid == 0) return false;
        return string.Equals(ProcNameCached(pid), "explorer", StringComparison.OrdinalIgnoreCase);
    }

    // 此刻前台是不是壳浮层（桌面自己的右键菜单/开始菜单之类）。
    // 用来做"滞后"：盒子已经沉下去了，只要壳浮层还在，就不急着浮回来，否则会来回抖。
    public static bool ShellOverlayForeground()
    {
        try
        {
            IntPtr f = GetForegroundWindow();
            if (f == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(f, GA_ROOT);
            if (root == IntPtr.Zero) root = f;
            string c = ClassOf(root);
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" ||
                c == "Shell_Secondary_TrayWnd" || c == "#32769") return false;
            return IsShellOverlay(root, c);
        }
        catch { return false; }
    }

    // 把一个窗口描述成人能看的样子：类名 / 宿主窗口类名 / 所属进程（排障用）
    public static string DescribeWnd(IntPtr h)
    {
        if (h == IntPtr.Zero) return "0";
        try
        {
            string c = ClassOf(h);
            IntPtr owner = GetWindow(h, GW_OWNER);
            string oc = owner == IntPtr.Zero ? "-" : ClassOf(owner);
            string proc = "-";
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != 0)
            {
                try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
                catch { proc = "pid" + pid; }
            }
            return c + " owner=" + oc + " proc=" + proc;
        }
        catch { return "?"; }
    }

    const uint GW_OWNER = 4;


    // --diag 用：一帧的完整处境。vis 用 IsWindowVisible 真值（托管 Visible 可能不更新）
    public string DiagDump()
    {
        try
        {
            IntPtr h = Handle;
            RECT r; GetWindowRect(h, out r);
            POINT c; c.X = (r.L + r.R) / 2; c.Y = (r.T + r.B) / 2;
            IntPtr at = WindowFromPoint(c);
            IntPtr par = GetParent(h);
            IntPtr anc = GetAncestor(h, 1);            // GA_PARENT
            int st = GetWindowLong(h, -16);
            int stx = GetWindowLong(h, -20);
            return "vis=" + (IsWindowVisible(h) ? 1 : 0) +
                   " mVis=" + (Visible ? 1 : 0) +
                   " iconic=" + (IsIconic(h) ? 1 : 0) +
                   " cloak=" + (IsCloaked(h) ? 1 : 0) +
                   " top=" + ((stx & 0x8) != 0 ? 1 : 0) +
                   " child=" + ((st & 0x40000000) != 0 ? 1 : 0) +
                   " par=" + (par == IntPtr.Zero ? "0" : ClassOf(par)) +
                   " anc=" + (anc == IntPtr.Zero ? "0" : ClassOf(anc)) +
                   " z=" + ZIndex(h) +
                   " under=" + (at == IntPtr.Zero ? "0" : (at == h ? "自己" : (IsOwnWindow(at) ? "兄弟" : ClassOf(at)))) +
                   " rect=" + r.L + "," + r.T + "," + r.R + "," + r.B;
        }
        catch (Exception ex) { return "err " + ex.Message; }
    }

    // 在 z 序里它前面还有几个窗口（0 = 最前面）
    public static int ZIndex(IntPtr h)
    {
        try
        {
            IntPtr x = GetTopWindow(IntPtr.Zero);
            int i = 0;
            while (x != IntPtr.Zero && i < 2000)
            {
                if (x == h) return i;
                x = GetWindow(x, GW_HWNDNEXT);
                i++;
            }
        }
        catch { }
        return -1;
    }

    // 自测用：把盒子此刻在屏幕上的真实处境描述出来（under=自己 才是真的露在桌面上）
    public string StateDump()
    {
        try
        {
            IntPtr h = Handle;
            int ex = GetWindowLong(h, -20);
            RECT r; GetWindowRect(h, out r);
            POINT c; c.X = (r.L + r.R) / 2; c.Y = (r.T + r.B) / 2;
            IntPtr at = WindowFromPoint(c);
            string under = at == h ? "自己" : (IsOwnWindow(at) ? "自己(兄弟盒子)" : ClassOf(at));
            IntPtr own = GetWindow(h, GW_OWNER);
            string ownName = own == IntPtr.Zero ? "none" : ClassOf(own);
            return string.Format("hwnd=0x{0} owner={1} vis={2} iconic={3} topmost={4} under={5}",
                h.ToInt64().ToString("X"), ownName, Visible, IsIconic(h), (ex & 0x8) != 0, under);
        }
        catch (Exception ex) { return "err " + ex.Message; }
    }

    static uint selfPid;
    static uint SelfPid
    {
        get
        {
            if (selfPid == 0) { try { selfPid = (uint)Process.GetCurrentProcess().Id; } catch { selfPid = 1u; } }
            return selfPid;
        }
    }

    // 本进程的窗口（其它盒子、托盘、提示框）一律算“自己”。
    // 之前只跳过自己的 Handle，两个盒子就会把对方当程序窗口 → raise/sink 每 350ms 来回抖（日志已证实）。
    static bool IsOwnWindow(IntPtr h)
    {
        try
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            return pid == SelfPid;
        }
        catch { return false; }
    }

    public static string ClassOf(IntPtr h)
    {
        try
        {
            if (h == IntPtr.Zero) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            GetClassName(h, sb, 256);
            return sb.ToString();
        }
        catch { return ""; }
    }

    static bool IsDesktopClass(string c)
    {
        return c == "Progman" || c == "WorkerW" || c == "SysListView32" ||
               c == "SHELLDLL_DefView" || c == "DesktopBannerWindowClass";
    }

    // ── 桌面守护（安全网，不是主机制） ───────────────────────────────────
    // 主机制：盒子是桌面窗口的子窗口，和桌面同一个图层 → “显示桌面”碰不到它，
    // 程序窗口天然盖住它。这里只处理几种“真出事”的情况，正常使用时不会触发。
    int lastResinkTick;
    int sunkTick;                   // 上次沉回桌面层的时刻（用于浮起冷却）

    // ---------- 前台窗口事件钩子 ----------
    // 定时器最快也就 40ms，而“显示桌面”还原时盒子会在程序窗口上停留一帧。
    // 用 WinEvent 在“前台窗口变了”的瞬间做判断，延迟从 ~40ms 降到 ~1ms。
    delegate void WinEventProc(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    WinEventProc winEventCb;                 // 每个盒子各挂一个钩子，必须是实例字段
    IntPtr winEventHook = IntPtr.Zero;
    IntPtr winEventHook2 = IntPtr.Zero;
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint EVENT_OBJECT_REORDER = 0x8004;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint evMin, uint evMax, IntPtr hmod, WinEventProc cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);

    void StartWinEvent()
    {
        if (winEventHook != IntPtr.Zero) return;
        try
        {
            winEventCb = OnWinEvent;
            // 0x0003 前台切换 + 0x8004 z 序重排（“显示桌面”把桌面窗口抬起来时会触发）
            winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, winEventCb, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            winEventHook2 = SetWinEventHook(EVENT_OBJECT_REORDER, EVENT_OBJECT_REORDER,
                IntPtr.Zero, winEventCb, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            Log.W("winevent hook=" + (winEventHook != IntPtr.Zero ? 1 : 0) + "/" + (winEventHook2 != IntPtr.Zero ? 1 : 0));
        }
        catch (Exception ex) { Log.W("winevent fail " + ex.Message); }
    }

    void StopWinEvent()
    {
        try
        {
            if (winEventHook != IntPtr.Zero) { UnhookWinEvent(winEventHook); winEventHook = IntPtr.Zero; }
            if (winEventHook2 != IntPtr.Zero) { UnhookWinEvent(winEventHook2); winEventHook2 = IntPtr.Zero; }
        }
        catch { }
    }

    void OnWinEvent(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (DisableAuto || IsDisposed || Disposing || AllowHide || !IsHandleCreated) return;
            if (ev == EVENT_OBJECT_REORDER)
            {
                // 桌面窗口刚被重排到最前（“显示桌面”）→ 抢在它盖住我们之前浮上去
                if (keepTopmost || hwnd == IntPtr.Zero) return;
                string cls = ClassOf(hwnd);
                if (cls != "Progman" && cls != "WorkerW" && cls != "SHELLDLL_DefView" && cls != "SysListView32")
                    return;
                // 壳浮层（桌面右键菜单）在场时别浮：会被它盖住，一浮一沉就成了持续闪烁
                if (ShellOverlayOpen()) return;
                // 桌面层重排不只是"显示桌面"才有：打开/切换新程序时也会来一发。
                // 不挡住它，就会 sink -> covered -> raise -> sink 每秒一轮（用户看到的闪烁）。
                // 唯一仲裁者：必须是**稳定**的桌面独占状态，而且前台不是活着的程序窗口。
                // 打开新程序时前台就是新程序（或桌面态还没成立），这里直接挡掉，
                // 不许绕过轮询的 latch 单独抬一次。
                if (LiveForegroundApp() || !deskShownState) return;
                if (CoveredByDesktop()) RaiseAboveDesktop("reorder");
                return;
            }
            if (!keepTopmost) return;                                   // 本来就在桌面层，不用管
            if (Environment.TickCount - topmostSince < 120) return;     // 刚浮起时的抖动忽略
            if (!LiveForegroundApp()) return;                           // 前台还是桌面壳（比如还在显示桌面）
            SinkBelowDesktop("winevent-fg");                            // 真有程序窗口到前台了，立刻沉回去
            appVisSince = 0; coveredSince = 0; raiseWantedSince = 0;
        }
        catch { }
    }

    // 屏幕上是否真的露出程序窗口（= 桌面已经不是“只剩自己”的状态）
    // 注意：不能再只靠“前台窗口变了”来判断。按 Win+D 还原窗口时前台窗口往往**根本不变**
    // （还是原来那个，只是从最小化恢复），拿不到 EVENT_SYSTEM_FOREGROUND，
    // 盒子就会一直停在最上面盖着程序 —— 这就是用户报的“恢复窗口时盖住程序”。
    public static bool AnyAppVisible()
    {
        // 以前用 SampleDesktopZ()（只看 z 序最前那个真窗口），它内部走
        // ReallyVisible -> WindowFromPoint(窗口中心)：盒子自己浮着时会把别的窗口中心点
        // 挡住，于是这一拍 0 下一拍 1。撒 35 点取样稳定得多，而且自己的盒子直接跳过，
        // 点盒子 / 开新程序时不会再凭空冒出"有程序窗口"。
        return SampleDesktop() == 2;
    }

    // ── raise / sink 的唯一仲裁者 ──────────────────────────────────────
    // "桌面独占"= 撒点取样一个程序窗口都没碰到（Win+D 把窗口全最小化了）。
    // 这条判据 2026-11-08 实测：正常桌面 app>0 / desk>0；显示桌面 app=0 desk=35；
    // 还原后 app=35 desk=0，区分度很干净（全屏 Chrome 渲染窗不是干扰，它在显示桌面时会被最小化）。
    // 关键：只有它为真才准 raise；只有它为假才准 sink。两个条件互斥，
    // 才不会像以前那样 "沉底后被桌面盖住" 与 "有程序窗口露着" 同时成立、来回翻。
    static bool DesktopOnly() { return SampleDesktop() == 1; }

    // 把瞬时采样收敛成一个**稳定**状态，供 GuardTick / WinEvent 共用。
    // 进入立刻生效（Win+D 那一帧要抢回来），退出按证据强弱决定去抖时间：
    // 明确采到大量程序窗口 160ms 就退，只是“判断不了”则等 400ms。
    // 这样既压得住单帧杂点，还原窗口时又不会让盒子在程序上多盖半秒。
    void UpdateDeskState(int tick)
    {
        int st = SampleDesktop();
        bool raw = st == 1;
        if (raw != deskRawLast) { deskRawLast = raw; deskRawSince = tick; }
        if (raw) deskShownState = true;
        else if (deskShownState && tick - deskRawSince >= (st == 2 ? 160 : 400))
            deskShownState = false;
    }

    int suppressLogTick;
    void LogRaiseSuppressed(string why)
    {
        int now = Environment.TickCount;
        if (now - suppressLogTick < 2000) return;
        suppressLogTick = now;
        Log.W("raise 抑制 [" + why + "]（有程序窗口，盒子保持桌面层）");
    }

    // 前台是否真的是一个“活着的程序窗口”（不是任务栏/桌面/最小化/挂起的窗口）
    public static bool LiveForegroundApp()
    {
        try
        {
            IntPtr f = GetForegroundWindow();
            if (f == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(f, GA_ROOT);
            if (root == IntPtr.Zero) root = f;
            string c = ClassOf(root);
            // 只排掉“壳”类窗口；#32770 是对话框，属于正常程序窗口，不能排除
            if (c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" ||
                c == "Progman" || c == "WorkerW" || c == "#32769") return false;
            if (IsOwnHwnd(root)) return false;
            // 壳浮层（桌面右键菜单、开始菜单…）不是程序窗口：盒子浮着时它一拿到前台
            // 不该把盒子沉下去（沉了就被桌面盖住，浮层一关又浮起来 → 闪）
            if (IsShellOverlay(root, c)) return false;
            if (IsIconic(root)) return false;           // 被“显示桌面”最小化了
            if (!IsWindowVisible(root)) return false;
            if (IsCloaked(root)) return false;          // UWP 挂起后的幽灵窗口
            return true;
        }
        catch { return false; }
    }

    // 浮着期间每 1 秒记一行现场，万一还出问题可以直接看日志定位，不用靠猜
    int keepLogTick;
    void LogKeepState(bool live, bool appVis)
    {
        int now = Environment.TickCount;
        if (now - keepLogTick < 1000) return;
        keepLogTick = now;
        try
        {
            IntPtr f = GetForegroundWindow();
            string fc = f == IntPtr.Zero ? "0" : ClassOf(f);
            Log.W("keep fg=" + fc + " fgIconic=" + (IsIconic(f) ? 1 : 0) +
                  " fgVis=" + (IsWindowVisible(f) ? 1 : 0) +
                  " live=" + (live ? 1 : 0) + " appVis=" + (appVis ? 1 : 0));
        }
        catch { }
    }

    void StartGuard()
    {
        if (desktopGuard != null) return;
        lastResinkTick = Environment.TickCount - 10000;
        desktopGuard = new Timer();
        desktopGuard.Interval = 40;
        desktopGuard.Tick += delegate { GuardTick(); };
        desktopGuard.Start();
        StartWinEvent();
    }

    void GuardTick()
    {
        if (DisableAuto) return;
        if (IsDisposed || Disposing || AllowHide || !IsHandleCreated) return;
        WatchTicks++;
        try
        {
            // 0) WinForms 偶尔会把 parent 抢回去（重新 Show / 改 WindowState 时），持续复核
            AdoptDesktopParent();

            // 0.5) 右键菜单必须始终压在盒子上面：盒子钉桌面时自己是 topmost，
            //      任何一次抬升/重排都可能把菜单盖住，这里持续兜底
            GuardMenuZ();

            // 0.6) 正在拖放 -> 一个 z 序都不写。
            //      OLE 每次 DragOver 都重新用 WindowFromPoint 找 drop target，
            //      盒子这期间上蹿下跳，目标窗口就一直变，松手时收不到 OnDragDrop。
            if (Environment.TickCount < dragGuardUntil) return;


            // 1) 被最小化了 → 立刻弹回来
            if (IsIconic(Handle))
            {
                ShowWindow(Handle, SW_RESTORE);
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                SetWindowPos(Handle, UseDesktopChild ? HWND_TOP : HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                Log.W("guard unminimize");
                return;
            }

            // 2) 被藏起来了 → 重新显示
            if (!Visible)
            {
                SetVisibleCore(true);
                AdoptDesktopParent();
                SetWindowPos(Handle, UseDesktopChild ? HWND_TOP : HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                Log.W("guard reshow");
                return;
            }

            // 3) 有人把盒子抬成了 topmost（个别第三方工具、系统动作）→ 退回桌面这一层。
            //    注意：keepTopmost 期间是我们自己有意浮在桌面之上（显示桌面时），不能动。
            int ex = GetWindowLong(Handle, -20);
            if (!keepTopmost && (ex & 0x8) != 0)
            {
                SetWindowPos(Handle, UseDesktopChild ? HWND_TOP : HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                Log.W("guard untopmost");
            }

            // 4) 被桌面图标层 / 壁纸压住了 → 在桌面这一层重新置顶。
            //    壳浮层（桌面右键菜单、开始菜单）在场时不要浮：它会把盒子盖住，
            //    一浮一沉就抖成筛子。等浮层消失再一口气浮回来。
            //    子窗口模式下这个间隔要短（400ms），回退模式放宽到 3 秒。
            int now = Environment.TickCount;
            int gap = UseDesktopChild ? 400 : 3000;
            // 回退模式下这句写的是 HWND_BOTTOM，根本不是置顶，反而把刚浮起来的盒子又按回去；
            // 下一拍第 5 步又要 raise —— 同一拍一沉一浮，日志里 guard resink + raise 成对出现。
            // 真正要浮起交给第 5 步统一做（带去抖），这里只在子窗口模式才有意义。
            if (UseDesktopChild && !keepTopmost && !MenuOpen() && now - lastResinkTick >= gap && CoveredByDesktop() && !ShellOverlayOpen())
            {
                lastResinkTick = now;
                SetWindowPos(Handle, UseDesktopChild ? HWND_TOP : HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                Log.W("guard resink（被桌面压住）");
            }

            // 5) “显示桌面”：Win11 25H2 上整个桌面会被抬成 topmost，
            //    顶层窗口再怎么 SetWindowPos(HWND_BOTTOM) 都会被盖住
            //    （--diag 实测：under 从 自己 变成 SysListView32，vis/iconic/cloak 全部正常）。
            //    所以只在“真的被桌面盖住”时临时浮到桌面之上；一旦有程序窗口回到前台立刻沉回去。
            //    这个判据是本地的、无歧义的，不会像全局采样那样来回抖。
            int tick = Environment.TickCount;
            UpdateDeskState(tick);          // 一拍只采一次，下面两个分支读同一个状态
            if (keepTopmost)
            {
                // 浮着的时候：只要屏幕上露出任何程序窗口就立刻沉回去（这时才可能挡住别人）。
                // 两条判据缺一不可：
                //   LiveForegroundApp() —— 前台换成了活着的程序窗口（快，靠事件）；
                //   AnyAppVisible()     —— 屏幕上真有程序窗口（兜住“前台没变、只是从最小化恢复”的情况）。
                bool live = LiveForegroundApp();
                // AnyAppVisible 走 ReallyVisible -> WindowFromPoint(窗口中心)。盒子自己浮着时
                // 会把别的窗口中心点挡住，于是这一拍 0 下一拍 1：sink -> 被桌面盖住 -> raise
                // -> 又 sink，就是用户看到的闪烁。必须连续成立才采信。
                bool appVisRaw = AnyAppVisible();
                if (appVisRaw) { if (appVisSince == 0) appVisSince = tick; }
                else appVisSince = 0;
                bool appVis = appVisSince != 0 && tick - appVisSince >= 350;
                LogKeepState(live, appVis);
                // 桌面独占状态里绝不因为撒点杂点下沉：右下角显示桌面的按钮旁边
                // 常驻着提示窗/Peek 幽灵窗，旧版就是被它骗出 sink->raise 循环的。
                if (tick - topmostSince > 300 && !deskShownState && (live || appVis))
                    SinkBelowDesktop("poll-fg");
            }
            else
            {
                bool overlay = ShellOverlayOpen() || MenuOpen();
                bool coveredRaw = !overlay && CoveredByDesktop();
                bool shellRaw = !overlay && ShellIsForeground() && !FrontIsApp();
                bool wantRaw = coveredRaw || shellRaw;
                bool desktopShown = deskShownState;   // 不再是每拍重新撒点得出的瞬时值

                // 只有"屏幕上一个程序窗口都没有"时才允许浮起。
                // 打开新程序时，盒子沉在桌面那一层本来就是对的；以前只要 CoveredByDesktop
                // 就 raise，于是 sink -> raise -> sink 每秒一轮，用户看到的就是
                // "一开程序盒子就闪一下"。这里把这个循环从结构上掐断。
                if (wantRaw && !desktopShown)
                {
                    LogRaiseSuppressed(coveredRaw ? "covered" : "shell-fg");
                    wantRaw = false;
                }

                if (wantRaw) { if (raiseWantedSince == 0) raiseWantedSince = tick; }
                else raiseWantedSince = 0;

                // 仲裁者已经把"打开新程序"整类误判挡掉了，只需一拍防抖压住
                // 显示桌面动画里的瞬时判据翻转；再快（20ms 轮询 / 零去抖）实测会
                // 在动画中多出一轮 raise-sink，反而更闪。
                if (raiseWantedSince != 0 && tick - raiseWantedSince >= 40)
                    RaiseAboveDesktop(coveredRaw ? "covered" : "shell-fg");
            }

            // 6) 菜单永远是最后一步：上面任何一次 raise 都可能把盒子抬到菜单前面，
            //    所以这一拍收尾再顶一次（原生 shell 菜单尤其需要，它不是我们自己管的窗口）
            if (MenuOpen()) GuardMenuZ();
        }
        catch { }
    }

    // 屏幕上是否真的有一个程序窗口露着（1=只有桌面 2=有程序窗口 0=判断不了）
    static bool FrontIsApp() { return SampleDesktopZ() == 2; }

    static bool ShellIsForeground()
    {
        try
        {
            IntPtr f = GetForegroundWindow();
            if (f == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(f, GA_ROOT);
            string c = ClassOf(root == IntPtr.Zero ? f : root);
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" ||
                   c == "Progman" || c == "WorkerW";
        }
        catch { return false; }
    }

    void RaiseAboveDesktop(string src)
    {
        if (keepTopmost) return;
        // 刚沉下去的那一下不要马上又浮起来：判据容易来回翻，冷却一下免得叮当响。
        // 但 shell-fg 是用户明确点了任务栏 / 显示了桌面，不该被冷却挡住，
        // 否则 Win+D 那一下盒子会被桌面盖住一帧（--win 量到 20ms）。
        if (src != "shell-fg" && Environment.TickCount - sunkTick < 500) return;
        keepTopmost = true;
        topmostSince = Environment.TickCount;
        appVisSince = 0; coveredSince = 0; raiseWantedSince = 0;
        try
        {
            if (desktopGuard != null) desktopGuard.Interval = 15;   // 浮着时高频盯防，配合 WinEvent 双保险
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            Log.W("raise [" + src + "] topmost=" +
                  (((GetWindowLong(Handle, -20) & 0x8) != 0) ? 1 : 0));
        }
        catch (Exception ex) { Log.W("raise fail " + ex.Message); }
    }

    void SinkBelowDesktop(string src)
    {
        if (!keepTopmost) return;
        keepTopmost = false;
        sunkTick = Environment.TickCount;
        appVisSince = 0; coveredSince = 0; raiseWantedSince = 0;
        try
        {
            if (desktopGuard != null) desktopGuard.Interval = 40;
            SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            Log.W("sink [" + src + "] topmost=" +
                  (((GetWindowLong(Handle, -20) & 0x8) != 0) ? 1 : 0));
        }
        catch (Exception ex) { Log.W("sink fail " + ex.Message); }
    }

    // 盒子中心那一点现在是不是被桌面类窗口占着（= 盒子跑到壁纸下面去了）
    bool CoveredByDesktop()
    {
        try
        {
            RECT r;
            if (!GetWindowRect(Handle, out r)) return false;
            POINT c;
            c.X = (r.L + r.R) / 2;
            c.Y = (r.T + r.B) / 2;
            IntPtr at = WindowFromPoint(c);
            if (at == IntPtr.Zero || at == Handle || IsOwnWindow(at)) return false;
            return IsDesktopClass(ClassOf(at));
        }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr h);

    // 在屏幕上撒点取样：只要还有任何程序窗口露在桌面上，就不算“显示桌面”。
    // 1=桌面独显  2=有程序窗口  0=判断不了（取样点全落在自己的盒子上）
    static int SampleDesktop() { string d; return SampleDesktop(out d); }

    static int SampleDesktop(out string detail)
    {
        detail = "";
        try
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            if (vs.Width <= 0 || vs.Height <= 0) return 0;
            int desktopHits = 0, appHits = 0;
            System.Text.StringBuilder seen = new System.Text.StringBuilder();
            for (int py = 1; py <= 5; py++)
            {
                for (int px = 1; px <= 7; px++)
                {
                    POINT pt;
                    pt.X = vs.Left + vs.Width * px / 8;
                    pt.Y = vs.Top + vs.Height * py / 6;
                    IntPtr at = WindowFromPoint(pt);
                    if (at == IntPtr.Zero) continue;
                    if (IsOwnWindow(at)) continue;              // 自己的盒子，跳过
                    string c = ClassOf(at);
                    if (seen.Length < 160) seen.Append(c).Append('|');
                    if (IsDesktopClass(c)) desktopHits++;
                    else if (c != "Shell_TrayWnd") appHits++;
                }
            }
            detail = "app=" + appHits + " desk=" + desktopHits + " [" + seen + "]";
            // 容差（2026-11-12）：点任务栏右下角「显示桌面」时，任务栏提示 / Peek 残留
            // 壳窗口会占掉 1~3 个采样点并且时有时无。以前 appHits>0 就算“有程序窗口”，
            // 判据在 1/2 之间单帧翻转：raise 与 sink 各读一次得出相反结论，
            // 盒子就一沉一浮（用户报的“点显示桌面还闪”）。
            // 实测正常桌面 app>=18，显示桌面 app<=2 / desk>=33，用 4 个点做分界很干净。
            if (appHits > 4) return 2;
            if (desktopHits >= 5) return 1;
            return 0;
        }
        catch { }
        return 0;
    }

    // 自测用：把刚才那一轮取样的明细吐出来
    public static string Probe() { string d; SampleDesktop(out d); return d; }
    public static int ProbeState(out string detail) { return SampleDesktop(out detail); }

    // ── 判定“桌面是不是单独露在最前面” ────────────────────────────────
    // 撒点取样（WindowFromPoint）有两个坑：DWM 动画期间窗口“查不到”，
    // Win+D 有时是“隐去（cloaked）”而不是“最小化”，被隐去的窗口仍然能被点到。
    // 所以真正做决定时改成直接走 z 序：从最前面开始，跳过看不见 / 最小化 / 被隐去 /
    // 自己 / 任务栏，第一个"真正露着"的窗口如果是桌面类，就说明桌面在独占屏幕。
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    const uint GW_HWNDNEXT = 2;
    const uint GA_ROOT = 2;
    const int DWMWA_CLOAKED = 14;

    static bool IsCloaked(IntPtr h)
    {
        try { int v; if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out v, 4) == 0) return v != 0; } catch { }
        return false;
    }

    static bool IsTrayClass(string c)
    {
        return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" ||
               c == "NotifyIconOverlayWindow" || c == "TopLevelWindowForOverflowXamlIsland" ||
               c == "Windows.Internal.Shell.TabProxyWindow" || c == "TaskListThumbnailWnd";
    }

    // 壳自己的窗口（桌面宿主、开始菜单、任务视图…）：它们出现在盒子上面不代表"程序回来了"，
    // 盒子本来就在它们下面，不需要下沉。误判会让盒子在显示桌面状态下突然消失。
    static bool IsShellGhost(string c)
    {
        if (string.IsNullOrEmpty(c)) return true;
        return c.IndexOf("XamlExplorerHostIsland", StringComparison.OrdinalIgnoreCase) >= 0
            || c.StartsWith("Windows.UI.Core", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith("Windows.Internal.Shell", StringComparison.OrdinalIgnoreCase)
            || c == "MultitaskingViewFrame" || c == "ForegroundStaging"
            || c == "Windows.UI.Composition.DesktopWindowContentBridge";
    }

    // 真正“用户点得到的”窗口：窗口中心那一点，命中的顶层窗口必须是它自己。
    // Win11 新桌面有个 XamlExplorerHostIslandWindow_WASDK 一直挂在最前面，但它对输入是透明的，
    // 不排掉它，判定永远认为“有程序窗口”，盒子就永远浮不起来。
    static bool ReallyVisible(IntPtr h)
    {
        try
        {
            RECT r;
            if (!GetWindowRect(h, out r)) return false;
            POINT c;
            c.X = (r.L + r.R) / 2;
            c.Y = (r.T + r.B) / 2;
            IntPtr at = WindowFromPoint(c);
            if (at == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(at, GA_ROOT);
            if (root == h) return true;
            // 桌面自己的层级：点在图标列表上，根是 Progman/WorkerW，和候选不是同一个 hwnd
            return IsDesktopClass(ClassOf(h)) && IsDesktopClass(ClassOf(root));
        }
        catch { return false; }
    }

    // z 序里第一个"算得了数"的顶层窗口。
    // skipOverlay=true 时把壳自己的浮层（桌面右键菜单 / 开始菜单 / 各种 XAML 弹出）也跳过去——
    // 它们是壳的一部分，不是"程序窗口回来了"。之前不跳，桌面右键菜单
    // （Microsoft.UI.Content.PopupWindowSiteBridge，explorer 进程）会被当成程序窗口，
    // 盒子浮着的时候判定 appVis=1 → 下沉 → 被桌面盖住 → 再浮起 → 持续闪烁。
    // skipOverlay=false 用来判断"现在有没有壳浮层挡在最前面"。
    static IntPtr FrontRealWindow(bool skipOverlay)
    {
        try
        {
            IntPtr h = GetTopWindow(IntPtr.Zero);
            int guard = 0;
            while (h != IntPtr.Zero && guard++ < 600)
            {
                IntPtr next = GetWindow(h, GW_HWNDNEXT);
                if (!IsWindowVisible(h) || IsIconic(h) || IsCloaked(h) || IsOwnWindow(h)) { h = next; continue; }
                string c = ClassOf(h);
                if (IsTrayClass(c) || IsShellGhost(c)) { h = next; continue; }
                if (skipOverlay && IsShellOverlay(h, c)) { h = next; continue; }
                if (!ReallyVisible(h)) { h = next; continue; }
                return h;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    public static int SampleDesktopZ() { string d; return SampleDesktopZ(out d); }
    // 1=桌面独占最前  2=前面有程序窗口  0=判断不了
    public static int SampleDesktopZ(out string detail)
    {
        detail = "";
        IntPtr h = FrontRealWindow(true);
        if (h == IntPtr.Zero) { detail = "no front window"; return 0; }
        string c = ClassOf(h);
        detail = "front=" + c + " hwnd=0x" + h.ToInt64().ToString("X");
        return IsDesktopClass(c) ? 1 : 2;
    }

    // 此刻是不是有壳浮层（桌面右键菜单 / 开始菜单 / 通知…）挡在 z 序最前面。
    // 只看 z 序、不看前台：Win11 上这些浮层经常根本不抢前台
    // （实测桌面右键菜单开着时，GetForegroundWindow 甚至是本进程的一个窗口）。
    public static bool ShellOverlayUp()
    {
        IntPtr h = FrontRealWindow(false);
        if (h == IntPtr.Zero) return false;
        string c = ClassOf(h);
        if (IsDesktopClass(c) || c == "#32769") return false;    // 就是桌面自己，不算浮层
        return IsShellOverlay(h, c);
    }

    // 壳浮层在场（前台是它，或者它压在 z 序最前）——盒子这时既别沉也别浮，
    // 一浮一沉就是用户看到的"桌面状态下右键 → 盒子持续闪烁"。
    public static bool ShellOverlayOpen() { return ShellOverlayForeground() || ShellOverlayUp(); }

    // 自测用：0=此刻正常  1=桌面已显示但盒子被压在下面（“消失一下”）  2=盒子被抬到程序窗口之上（“压住程序”）
    public int FlashKind()
    {
        try
        {
            int st = SampleDesktop();
            IntPtr h = Handle;
            RECT r; GetWindowRect(h, out r);
            POINT c; c.X = (r.L + r.R) / 2; c.Y = (r.T + r.B) / 2;
            IntPtr at = WindowFromPoint(c);
            bool covered = at != h && !IsOwnWindow(at);
            bool topmost = (GetWindowLong(h, -20) & 0x8) != 0;
            if (covered && st == 1) return 1;
            if (topmost && st == 2) return 2;
            return 0;
        }
        catch { return 0; }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 兜底：万一被系统强行最小化（部分“显示桌面”实现走 ShowWindow），立刻弹回来
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (IsHandleCreated) { Layout2(); Render(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && desktopGuard != null)
        {
            try { desktopGuard.Stop(); desktopGuard.Dispose(); } catch { }
            desktopGuard = null;
            StopWinEvent();
        }
        if (disposing && frame != null)
        {
            try { frame.Dispose(); } catch { }
            frame = null;
        }
        base.Dispose(disposing);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LogGeom("shown-start");
        Layout2();
        Render();
        LogGeom("shown-afterRender");
        // 变成桌面窗口的子窗口：显示桌面时它跟着桌面一起留在屏幕上，程序窗口照样盖住它。
        AdoptDesktopParent();
        Render();              // parent 变了之后重新贴一次（坐标语义变了）
        PinToConfig();         // 收尾：确保窗口真的停在配置坐标上（见 PinToConfig 注释）
        LogGeom("shown-end");
        StartGuard();          // 安全网：只在真被最小化 / 藏起来 / 压住时救一次
    }

    // 把窗口钉回配置坐标。启动瞬间窗口可能短暂带着边框/标题（客户区原点比窗口原点大
    // 8/31），任何"按客户区算位置"的调用都会把窗口整体推一点；这里按配置坐标再对齐一次，
    // 保证"配置里写哪儿，盒子就出现在哪儿"，不会逐次重启往下往右漂。
    void PinToConfig()
    {
        try
        {
            if (!IsHandleCreated) return;
            RECT r;
            if (!GetWindowRect(Handle, out r)) return;
            if (r.L == Data.X && r.T == Data.Y) return;
            SetWindowPos(Handle, IntPtr.Zero, Data.X, Data.Y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | 0x0400 /*SWP_NOOWNERZORDER*/);
            Log.W("pin 位置纠回 " + r.L + "," + r.T + " -> " + Data.X + "," + Data.Y);
        }
        catch (Exception ex) { Log.W("pin fail " + ex.Message); }
    }

    DTab Cur
    {
        get
        {
            if (Data.Tabs == null) Data.Tabs = new List<DTab>();
            if (Data.Tabs.Count == 0) Data.Tabs.Add(Store.MakeTab("全部"));
            if (curTab < 0 || curTab >= Data.Tabs.Count) curTab = 0;
            return Data.Tabs[curTab];
        }
    }

    void Layout2()
    {
        closeRect = new Rectangle(Width - 38, 4, 26, 26);
        addRect = new Rectangle(Width - 68, 4, 26, 26);
        lockRect = new Rectangle(Width - 98, 4, 26, 26);
        headerRect = new Rectangle(0, 0, Width, HEAD_H);
        stripRect = new Rectangle(8, HEAD_H, Width - 16, STRIP_H);
        contentRect = new Rectangle(8, HEAD_H + STRIP_H + 2, Width - 16, Height - (HEAD_H + STRIP_H + 2) - 8);
    }

    public static GraphicsPath Round(Rectangle r, int rad)
    {
        GraphicsPath p = new GraphicsPath();
        // 圆角可以为 0（方角）：AddArc 遇到半径 0 会得到奇怪路径，直接走矩形
        if (rad < 1) { p.AddRectangle(r); return p; }
        int d = rad * 2;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        rad = d / 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // 整幅透明度（拖动时把源图标画淡）：尺寸不变，1:1 贴图，不会因此变糊
    static ImageAttributes AlphaAttrs(float a)
    {
        ImageAttributes ia = new ImageAttributes();
        ColorMatrix cm = new ColorMatrix();
        cm.Matrix33 = a;
        ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        return ia;
    }

    void TextOut(Graphics g, string s, Font f, Color c, Rectangle r, StringAlignment h, StringAlignment v)
    {
        TextOut(g, s, f, c, r, h, v, false);
    }

    // 文字输出：自己算起点并向下取整到整像素。
    // GDI+ 的 StringAlignment.Center 会算出 0.5px 这种小数偏移，字形一律发虚；
    // 手动对齐到整数后，配合 AntiAliasGridFit 才真的清晰。
    void TextOut(Graphics g, string s, Font f, Color c, Rectangle r, StringAlignment h, StringAlignment v, bool forceShadow)
    {
        if (string.IsNullOrEmpty(s)) return;
        SizeF sz;
        using (StringFormat mf = (StringFormat)StringFormat.GenericTypographic.Clone())
            sz = g.MeasureString(s, f, new SizeF(10000f, 10000f), mf);

        int tw = (int)Math.Ceiling(sz.Width);
        int th = (int)Math.Ceiling(sz.Height);
        int x = r.X, y = r.Y;
        if (h == StringAlignment.Center) x = r.X + (r.Width - tw) / 2;
        else if (h == StringAlignment.Far) x = r.Right - tw;
        // 文本比格子宽时，居中对齐会算出负偏移，把开头几个字推到格子外面去
        // （曾经把 "DeepSeek - Ha…" 显示成 "jeepSeek - Ha…"）。夹回格子内，让省略号在格子边缘正常出现。
        if (x < r.X) x = r.X;
        if (v == StringAlignment.Center) y = r.Y + (r.Height - th) / 2;
        else if (v == StringAlignment.Far) y = r.Bottom - th;

        // 宽/高留 2px 余量：MeasureString 的量出来值常比实际绘制略小，
        // 贴着给的宽度会被 Triming 误判成"放不下"，末尾多出一个省略号（"20%" 变 "2…"）。
        Rectangle rr = new Rectangle(x, y, Math.Max(1, r.Right - x + 2), Math.Max(1, r.Height + 2));
        using (StringFormat sf = new StringFormat())
        {
            sf.Alignment = StringAlignment.Near;
            sf.LineAlignment = StringAlignment.Near;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags = StringFormatFlags.NoWrap;

            // 底板不够实的时候垫一层暗影，压在花壁纸上也不糊
            bool shadow = forceShadow || PlateAlpha < 0.86;
            if (shadow)
            {
                Color sc = TextShadow;
                using (SolidBrush sb = new SolidBrush(sc))
                    g.DrawString(s, f, sb, new Rectangle(rr.X, rr.Y + 1, rr.Width, rr.Height), sf);
            }
            using (SolidBrush br = new SolidBrush(c))
                g.DrawString(s, f, br, rr, sf);
        }
    }

    Rectangle BandRect()
    {
        Rectangle r = Rectangle.FromLTRB(
            Math.Min(bandStart.X, bandCur.X), Math.Min(bandStart.Y, bandCur.Y),
            Math.Max(bandStart.X, bandCur.X), Math.Max(bandStart.Y, bandCur.Y));
        r.Intersect(contentRect);
        return r;
    }

    // 只负责“排版 + 画图”，返回一张位图，不碰窗口句柄。
    // 拆出来是为了 `--render out.png`：不用显示桌面、不用截屏，直接把界面 1:1 灌成 PNG 检查。
    public Bitmap Compose()
    {
        Layout2();
        Bitmap bmp = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // AntiAliasGridFit：把字形笔画吸到整像素上，小字号才不会糊成一团
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.Transparent);

            // 底板不透明度 = Style.BgAlpha（0-100 的连续值，就是真实 alpha）。
            // 老版本那套"亮底偷偷 +26"已经并进迁移值里了，这里不再有隐藏补偿。
            int alpha = (int)Math.Round(PlateAlpha * 255.0);
            Rectangle plateR = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = RadiusVal;
            using (GraphicsPath plate = Round(plateR, rad))
            {
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(alpha, BgCol)))
                {
                    g.FillPath(fill, plate);
                }
                // 玻璃的另一半是"光从上面来"：顶亮底暗的竖向渐变，比纯色平板有实体感
                GraphicsState gs = g.Save();
                g.SetClip(plate);
                using (LinearGradientBrush sheen = new LinearGradientBrush(
                    new Rectangle(0, 0, Width, Height),
                    LightPlate ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(30, 255, 255, 255),
                    LightPlate ? Color.FromArgb(10, 0, 0, 0) : Color.FromArgb(44, 0, 0, 0), 90f))
                {
                    g.FillRectangle(sheen, plateR);
                }
                g.Restore(gs);
                // 边缘线（颜色 / 不透明度 / 粗细都可调）+ 顶部一道高光，玻璃的边界才立得起来
                if (EdgePct > 0)
                {
                    using (Pen edge = new Pen(EdgeColor, EdgeW))
                    {
                        edge.Alignment = PenAlignment.Inset;   // 画在内侧，粗线才不会出界被裁掉
                        g.DrawPath(edge, plate);
                    }
                }
                using (SolidBrush hi = new SolidBrush(TopHi))
                {
                    g.FillRectangle(hi, 14, 1, Width - 28, 1);
                }
            }

            // header
            using (SolidBrush bar = new SolidBrush(Color.FromArgb(255, accent)))
            {
                g.FillRectangle(bar, 14, HEAD_H / 2 - 8, 3, 16);
            }
            // 标题用文字色，accent 只留给左边的色条（原来标题直接用霓虹 accent，才那么难看）
            // 标题区宽度：右边只剩 锁 / + / × 三个 26px 控件（不透明度滑块已删）
            TextOut(g, Data.Title, fTitle, TextA(1f), new Rectangle(24, 0, Width - 132, HEAD_H), StringAlignment.Near, StringAlignment.Center);

            // 操作控件平时淡一档，鼠标进标题栏才亮起来：标题是主角，控件是配角
            int ca = headHot ? 255 : 205;

            DrawLock(g, ca);

            using (Font fPlus = new Font("Consolas", 17f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font fClose = new Font("Consolas", 16f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                TextOut(g, "+", fPlus, SubA(ca / 255f), addRect, StringAlignment.Center, StringAlignment.Center);
                TextOut(g, "×", fClose, SubA(ca / 255f), closeRect, StringAlignment.Center, StringAlignment.Center);
            }

            // tabs
            tabRects.Clear();
            // 标签栏下方一条极淡的分隔线：给"头部"和"内容区"一个结构边界，
            // 否则一堆文字直接浮在壁纸上，看起来像没排版。
            // 这一层也进配色（Style.SepOn/SepColor/SepAlpha），默认 12% ≈ 老的 Tint(30)。
            if (SepPct > 0)
            {
                using (SolidBrush sep = new SolidBrush(SepLine))
                {
                    g.FillRectangle(sep, 10, stripRect.Bottom + 1, Width - 20, 1);
                }
            }
            int tx = stripRect.X + 2;
            for (int i = 0; i < Data.Tabs.Count; i++)
            {
                int tw = TextRenderer.MeasureText(Data.Tabs[i].Name, fTab).Width + 14;
                Rectangle tr = new Rectangle(tx, stripRect.Y, tw, STRIP_H);
                tabRects.Add(tr);
                if (i == curTab)
                {
                    using (SolidBrush bg = new SolidBrush(Tint(30)))
                    using (GraphicsPath p = Round(tr, 6)) g.FillPath(bg, p);
                    // 选中项也用文字色，靠背景药丸 + 下方 accent 线表达“选中”，不再靠彩色字
                    TextOut(g, Data.Tabs[i].Name, fTab, TextA(1f), tr, StringAlignment.Center, StringAlignment.Center);
                    using (SolidBrush bar = new SolidBrush(Color.FromArgb(255, accent)))
                    {
                        g.FillRectangle(bar, tr.X + 8, tr.Bottom - 4, tr.Width - 16, 2);
                    }
                }
                else
                {
                    Color c = (hoverTab == i) ? TextA(1f) : SubA(1f);
                    TextOut(g, Data.Tabs[i].Name, fTab, c, tr, StringAlignment.Center, StringAlignment.Center);
                }
                if (dragOver && dropTabHi == i)
                {
                    using (Pen bpen = new Pen(Color.FromArgb(230, accent), 1.8f))
                    using (GraphicsPath bp = Round(tr, 6)) g.DrawPath(bpen, bp);
                }
                tx += tw + 4;
            }

            // content
            tileRects.Clear();
            DTab tab = Data.Tabs[curTab];
            int innerX = contentRect.X + 4;
            int innerY = contentRect.Y + 4;
            int cols = Math.Max(1, (contentRect.Width - 8) / TILE_W);
            int rows = (int)Math.Ceiling(tab.Items.Count / (double)cols);
            int contentH = rows * TILE_H + 8;
            int maxScroll = Math.Max(0, contentH - contentRect.Height);
            if (scrollY > maxScroll) scrollY = maxScroll;
            if (scrollY < 0) scrollY = 0;

            Region oldClip = g.Clip;
            g.SetClip(contentRect);
            for (int i = 0; i < tab.Items.Count; i++)
            {
                int cx = innerX + (i % cols) * TILE_W;
                int cy = innerY + (i / cols) * TILE_H - scrollY;
                Rectangle cell = new Rectangle(cx, cy, TILE_W - 2, TILE_H - 4);
                tileRects.Add(cell);

                bool isSel = sel.Contains(i);
                if (isSel)
                {
                    using (GraphicsPath p = Round(cell, 8))
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(52, accent)))
                    {
                        g.FillPath(bg, p);
                    }
                    using (GraphicsPath p2 = Round(Rectangle.Inflate(cell, -1, -1), 7))
                    using (Pen bp = new Pen(Color.FromArgb(235, accent), 1.6f))
                    {
                        g.DrawPath(bp, p2);
                    }
                }
                else if (i == hoverTile)
                {
                    using (GraphicsPath p = Round(cell, 8))
                    using (SolidBrush bg = new SolidBrush(Tint(32)))
                    {
                        g.FillPath(bg, p);
                    }
                }

                // 正在被拖走的图标画成半透明：一眼看出"这几项已经被拎起来了"
                bool lifted = dragLift && isSel;
                Image icon = IconCache.Get(tab.Items[i].Path, tab.Items[i].Name, ICON);
                if (icon != null)
                {
                    // 目标尺寸与位图一致，1:1 直接贴，不再二次缩放
                    if (lifted)
                    {
                        using (ImageAttributes ia = AlphaAttrs(0.38f))
                        {
                            // 关掉 Half 偏移，见下面 else 分支的注释
                            GraphicsState gsi = g.Save();
                            g.PixelOffsetMode = PixelOffsetMode.None;
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.DrawImage(icon, new Rectangle(cx + (TILE_W - 2 - ICON) / 2, cy + 6, ICON, ICON),
                                        0, 0, ICON, ICON, GraphicsUnit.Pixel, ia);
                            g.Restore(gsi);
                        }
                        using (Pen dp = new Pen(TextA(170 / 255f), 1))
                        {
                            dp.DashStyle = DashStyle.Dash;
                            g.DrawRectangle(dp, cell.X + 2, cell.Y + 2, cell.Width - 4, cell.Height - 4);
                        }
                    }
                    else
                    {
                        // **必须临时关掉 PixelOffsetMode.Half**：Compose 为了文字/矢量抗锯齿
                        // 全局设了 Half，那会让 1:1 贴图也偏移半像素再插值一次，
                        // 整张图标都被磨软一层——这是"图标不如资源管理器清楚"的主因。
                        GraphicsState gsi = g.Save();
                        g.PixelOffsetMode = PixelOffsetMode.None;
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.DrawImageUnscaled(icon, cx + (TILE_W - 2 - ICON) / 2, cy + 6);
                        g.Restore(gsi);
                    }
                }
                TextOut(g, tab.Items[i].Name, fLbl, TextA(lifted ? 120 / 255f : 250 / 255f),
                    new Rectangle(cx, cy + ICON + 10, TILE_W - 2, 18), StringAlignment.Center, StringAlignment.Near);
            }

            // 拖拽插入位置指示
            if (dragOver && !dropOnTab && dropIndex >= 0)
            {
                int dcols = cols;
                if (dropIndex >= tab.Items.Count && tab.Items.Count > 0)
                {
                    int lastCol = (tab.Items.Count - 1) % dcols;
                    int lastRow = (tab.Items.Count - 1) / dcols;
                    int lx = innerX + lastCol * TILE_W + TILE_W - 4;
                    int ly = innerY + lastRow * TILE_H - scrollY;
                    using (SolidBrush ib = new SolidBrush(Color.FromArgb(255, accent)))
                    {
                        g.FillRectangle(ib, lx - 1, ly + 4, 3, TILE_H - 12);
                    }
                }
                else
                {
                    int dx = innerX + (dropIndex % dcols) * TILE_W;
                    int dy = innerY + (dropIndex / dcols) * TILE_H - scrollY;
                    using (SolidBrush ib = new SolidBrush(Color.FromArgb(255, accent)))
                    {
                        g.FillRectangle(ib, dx - 2, dy + 4, 3, TILE_H - 12);
                    }
                }
            }

            if (tab.Items.Count == 0)
            {
                TextOut(g, "把桌面图标拖进来（剪切）\r\n双击启动 · 拖动换位 · 框选／Ctrl 多选 · 拖到其他标签或盒子",
                    fSmall, SubA(190 / 255f), contentRect, StringAlignment.Center, StringAlignment.Center);
            }
            g.Clip = oldClip;

            // 框选矩形
            if (bandSelecting)
            {
                Rectangle br = BandRect();
                if (br.Width > 1 && br.Height > 1)
                {
                    using (SolidBrush fb = new SolidBrush(Color.FromArgb(34, accent))) g.FillRectangle(fb, br);
                    using (Pen bp = new Pen(Color.FromArgb(210, accent), 1.2f))
                    {
                        bp.DashStyle = DashStyle.Dash;
                        g.DrawRectangle(bp, br);
                    }
                }
            }

            // scrollbar hint
            if (maxScroll > 0)
            {
                int trackH = contentRect.Height;
                int barH = Math.Max(24, trackH * contentRect.Height / contentH);
                int barY = contentRect.Y + (trackH - barH) * scrollY / maxScroll;
                using (SolidBrush sb = new SolidBrush(Tint(60)))
                {
                    g.FillRectangle(sb, contentRect.Right - 3, barY, 3, barH);
                }
            }

            // 吸附对齐提示线
            if (windowDrag || resizing)
            {
                using (SolidBrush gb = new SolidBrush(Color.FromArgb(150, accent)))
                {
                    if (snapEdgeX >= 0)
                    {
                        int gx = snapEdgeX == 0 ? 1 : (snapEdgeX == 1 ? Width - 3 : Width / 2 - 1);
                        g.FillRectangle(gb, gx, 6, 2, Height - 12);
                    }
                    if (snapEdgeY >= 0)
                    {
                        int gy = snapEdgeY == 0 ? 1 : (snapEdgeY == 1 ? Height - 3 : Height / 2 - 1);
                        g.FillRectangle(gb, 6, gy, Width - 12, 2);
                    }
                }
            }

            // resize grip
            if (!Data.Locked)
            {
                using (Pen gp = new Pen(Color.FromArgb(60, accent), 1.4f))
                {
                    g.DrawLine(gp, Width - 8, Height - 16, Width - 16, Height - 8);
                    g.DrawLine(gp, Width - 8, Height - 10, Width - 10, Height - 8);
                }
            }
        }
        return bmp;
    }

    void Render()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        Bitmap bmp = Compose();
        Apply(bmp);
        if (frame != null && !ReferenceEquals(frame, bmp)) frame.Dispose();
        frame = bmp;
    }

    // 只重新贴一次已缓存的位图（拖动时用，避免每帧重绘）
    void BlitFrame()
    {
        if (frame != null) Apply(frame);
    }

    void DrawLock(Graphics g, int ca)
    {
        // 解锁态用次要文字色（原来写死 (168,180,176)，在浅底上几乎看不见，也不跟主题走）
        Color c = Data.Locked ? Color.FromArgb(ca, accent) : SubA(ca * 0.72f / 255f);
        int cx = lockRect.X + lockRect.Width / 2;
        int bodyW = 12, bodyH = 10;
        int bodyX = cx - bodyW / 2, bodyY = lockRect.Y + 11;
        using (Pen pen = new Pen(c, 1.6f))
        {
            if (Data.Locked) g.DrawArc(pen, cx - 5f, lockRect.Y + 4f, 10, 10, 180, 180);
            else g.DrawArc(pen, cx - 5f, lockRect.Y + 2.5f, 10, 10, 170, 150);
            using (SolidBrush br = new SolidBrush(Data.Locked ? c : Color.FromArgb(0, c)))
            {
                g.FillRectangle(br, bodyX, bodyY, bodyW, bodyH);
            }
            g.DrawRectangle(pen, bodyX, bodyY, bodyW, bodyH);
        }
    }

    static int applyLogCount;
    static int geomApplyLog;        // 排障：只记前几次 Apply 的坐标
    void Apply(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        SIZE size = new SIZE();
        size.cx = bmp.Width; size.cy = bmp.Height;
        POINT src = new POINT();
        POINT dst = new POINT();
        // UpdateLayeredWindow 的 pptDst 要的是**窗口**的屏幕坐标，必须用 GetWindowRect 的真实
        // 窗口原点。曾经用的是 PointToScreen(Point.Empty)（= **客户区**原点）——启动瞬间窗口
        // 会短暂带着 WS_CAPTION，客户区原点比窗口原点正好大 8/31，于是 UpdateLayeredWindow
        // 把整个窗口往右下挪了 8/31；拖一次窗口就把这个偏移存进配置，下次启动再叠加一次。
        // 这就是"每次重启盒子位置都偏一点"的根因。窗口带不带边框都不该影响位置。
        RECT wr;
        int wx = Left, wy = Top;
        if (IsHandleCreated && GetWindowRect(Handle, out wr)) { wx = wr.L; wy = wr.T; }
        dst.X = wx; dst.Y = wy;
        if (geomApplyLog < 3) { geomApplyLog++; LogGeom("apply#" + geomApplyLog + " dst=" + dst.X + "," + dst.Y); }
        BLENDFUNCTION blend = new BLENDFUNCTION();
        blend.BlendOp = 0;
        blend.SourceConstantAlpha = 255;
        blend.AlphaFormat = 1;
        SetLastError(0);
        bool ok = UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        if (!ok && applyLogCount < 3)
        {
            applyLogCount++;
            Log.W("apply FAIL err=" + Marshal.GetLastWin32Error() + " dst=" + dst.X + "," + dst.Y +
                  " child=" + (((GetWindowLong(Handle, GWL_STYLE) & WS_CHILD) != 0) ? 1 : 0));
        }
        SelectObject(memDc, old);
        DeleteObject(hBmp);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    int HitTab(Point p)
    {
        for (int i = 0; i < tabRects.Count; i++) if (tabRects[i].Contains(p)) return i;
        return -1;
    }

    int HitTile(Point p)
    {
        if (!contentRect.Contains(p)) return -1;
        for (int i = 0; i < tileRects.Count; i++) if (tileRects[i].Contains(p)) return i;
        return -1;
    }

    // 不透明度滑块在 2026-10-24 从标题栏删掉了（用户要求）：透明度改在「配色…」对话框里按 0-100 调。
    // 原来这里还有 OpStepX() 和 SetOpacityFromX()，跟着滑块一起删了。

    void ToggleLock()
    {
        Data.Locked = !Data.Locked;
        Render();
        Store.Save(root);
    }

    // ---------- 选择辅助 ----------

    List<DItem> SelectedItems()
    {
        DTab t = Cur;
        List<DItem> r = new List<DItem>();
        List<int> idx = new List<int>(sel);
        idx.Sort();
        HashSet<DItem> seen = new HashSet<DItem>();
        foreach (int i in idx)
        {
            if (i >= 0 && i < t.Items.Count)
            {
                DItem it = t.Items[i];
                if (seen.Add(it)) r.Add(it);
            }
        }
        return r;
    }

    void ApplyBand()
    {
        Rectangle r = BandRect();
        sel.Clear();
        foreach (int i in bandBase) sel.Add(i);
        for (int i = 0; i < tileRects.Count; i++)
        {
            if (tileRects[i].IntersectsWith(r)) sel.Add(i);
        }
        if (sel.Count > 0) { List<int> l = new List<int>(sel); l.Sort(); selAnchor = l[0]; }
    }

    // ---------- 启动 ----------

    void LaunchItems(List<DItem> items)
    {
        if (items == null || items.Count == 0) return;
        string firstError = null;
        foreach (DItem it in items)
        {
            try
            {
                string path = it.Path;
                if (string.IsNullOrEmpty(path)) continue;
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    string r = IconCache.Resolve(path);
                    if (!string.IsNullOrEmpty(r) && (File.Exists(r) || Directory.Exists(r))) path = r;
                }
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = path;
                psi.UseShellExecute = true;
                try
                {
                    string d = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) psi.WorkingDirectory = d;
                }
                catch { }
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                if (firstError == null) firstError = "无法启动：" + it.Path + "\r\n" + ex.Message;
                Log.W("launch fail " + it.Path + " :: " + ex.Message);
            }
        }
        if (firstError != null) MessageBox.Show(firstError, "DeskBox");
    }

    // ---------- 吸附对齐 ----------

    List<int> CollectX()
    {
        List<int> t = new List<int>();
        try
        {
            Rectangle wa = Screen.FromRectangle(Bounds).WorkingArea;
            t.Add(wa.Left);
            t.Add(wa.Right);
            t.Add(wa.Left + wa.Width / 2);
        }
        catch { }
        foreach (LayeredBox o in Program.forms)
        {
            if (ReferenceEquals(o, this) || !o.Visible) continue;
            t.Add(o.Left);
            t.Add(o.Left + o.Width);
            t.Add(o.Left + o.Width / 2);
        }
        return t;
    }

    List<int> CollectY()
    {
        List<int> t = new List<int>();
        try
        {
            Rectangle wa = Screen.FromRectangle(Bounds).WorkingArea;
            t.Add(wa.Top);
            t.Add(wa.Bottom);
            t.Add(wa.Top + wa.Height / 2);
        }
        catch { }
        foreach (LayeredBox o in Program.forms)
        {
            if (ReferenceEquals(o, this) || !o.Visible) continue;
            t.Add(o.Top);
            t.Add(o.Top + o.Height);
            t.Add(o.Top + o.Height / 2);
        }
        return t;
    }

    // 返回吸附后的左上角 X，并给出是哪条边对齐的
    int SnapX(int nx, out int edge)
    {
        edge = -1;
        int best = SNAP + 1;
        int result = nx;
        int[] mine = new int[] { nx, nx + Width, nx + Width / 2 };
        List<int> targets = CollectX();
        for (int k = 0; k < mine.Length; k++)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                int d = targets[i] - mine[k];
                int ad = Math.Abs(d);
                if (ad <= SNAP && ad < best)
                {
                    best = ad;
                    result = nx + d;
                    edge = k;
                }
            }
        }
        return result;
    }

    int SnapY(int ny, out int edge)
    {
        edge = -1;
        int best = SNAP + 1;
        int result = ny;
        int[] mine = new int[] { ny, ny + Height, ny + Height / 2 };
        List<int> targets = CollectY();
        for (int k = 0; k < mine.Length; k++)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                int d = targets[i] - mine[k];
                int ad = Math.Abs(d);
                if (ad <= SNAP && ad < best)
                {
                    best = ad;
                    result = ny + d;
                    edge = k;
                }
            }
        }
        return result;
    }

    // ---------- 鼠标 ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // 顺手把"拖出去之后应该已经不在"的僵尸条目清掉（系统可能过了很久才删源）
        if (pendingPrune.Count > 0) PruneMissing(pendingPrune, "drag-out(lazy)", false);
        Point p = e.Location;

        if (e.Button == MouseButtons.Right)
        {
            int rt = HitTile(p);
            if (rt >= 0) ShowTileMenu(rt, p);
            else ShowBoxMenu(p);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (closeRect.Contains(p)) { HideByUser(); return; }
        if (addRect.Contains(p)) { AddTabDialog(); return; }
        if (lockRect.Contains(p)) { ToggleLock(); return; }

        int tab = HitTab(p);
        if (tab >= 0)
        {
            curTab = tab;
            scrollY = 0;
            sel.Clear();
            selAnchor = -1;
            Render();
            return;
        }

        int tile = HitTile(p);
        if (tile >= 0)
        {
            // 手动双击判定（不依赖 WM_LBUTTONDBLCLK，双保险）
            int dt = SystemInformation.DoubleClickTime;
            int now = Environment.TickCount;
            if (lastClickTile == tile && Math.Abs(now - lastClickTicks) <= dt)
            {
                lastClickTile = -1;
                pressTile = -1;
                deferClear = false;
                movedSincePress = false;
                DTab t0 = Cur;
                if (tile >= 0 && tile < t0.Items.Count) LaunchItems(new List<DItem> { t0.Items[tile] });
                return;
            }
            lastClickTile = tile;
            lastClickTicks = now;

            bool ctrl = (ModifierKeys & Keys.Control) != 0;
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            if (ctrl)
            {
                if (!sel.Add(tile)) sel.Remove(tile);
                selAnchor = tile;
                deferClear = false;
            }
            else if (shift && selAnchor >= 0)
            {
                sel.Clear();
                int a = Math.Min(selAnchor, tile), b = Math.Max(selAnchor, tile);
                for (int i = a; i <= b; i++) sel.Add(i);
                deferClear = false;
            }
            else if (sel.Contains(tile) && sel.Count > 1)
            {
                deferClear = true;
            }
            else
            {
                sel.Clear();
                sel.Add(tile);
                selAnchor = tile;
                deferClear = false;
            }

            pressTile = tile;
            pressPoint = p;
            movedSincePress = false;
            Render();
            return;
        }

        if (!Data.Locked && e.X > Width - GRIP && e.Y > Height - GRIP)
        {
            resizing = true;
            resizeStart = p;
            sizeStart = Size;
            return;
        }

        if (contentRect.Contains(p))
        {
            bool ctrl = (ModifierKeys & Keys.Control) != 0;
            bandBase.Clear();
            if (ctrl) { foreach (int i in sel) bandBase.Add(i); }
            else { sel.Clear(); selAnchor = -1; }
            bandSelecting = true;
            bandStart = p;
            bandCur = p;
            Render();
            return;
        }

        if (headerRect.Contains(p) && !Data.Locked)
        {
            pendingDrag = true;
            downPoint = p;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        // 图标双击启动已由 OnMouseDown 手动判定，这里只处理标题/标签
        if (headerRect.Contains(e.Location)) { RenameBox(); return; }
        if (HitTile(e.Location) >= 0) return;
        // 这里**不能**再改名：切标签时手快点两下就会跳进重命名，
        // 用户体感就是"我就切了个标签，怎么让我改名"。改名走菜单 / F2。
        int tab = HitTab(e.Location);
        if (tab >= 0 && tab != curTab) { curTab = tab; sel.Clear(); scrollY = 0; Render(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.Location;


        if (bandSelecting)
        {
            bandCur = new Point(
                Math.Max(contentRect.Left, Math.Min(contentRect.Right, e.X)),
                Math.Max(contentRect.Top, Math.Min(contentRect.Bottom, e.Y)));
            ApplyBand();
            Render();
            return;
        }

        if (pressTile >= 0 && e.Button == MouseButtons.Left)
        {
            if (!movedSincePress && (Math.Abs(e.X - pressPoint.X) > 4 || Math.Abs(e.Y - pressPoint.Y) > 4))
            {
                movedSincePress = true;
                int tile = pressTile;
                pressTile = -1;
                deferClear = false;
                BeginItemDrag(tile);
                return;
            }
        }

        if (pendingDrag && e.Button == MouseButtons.Left)
        {
            if (Math.Abs(e.X - downPoint.X) > 4 || Math.Abs(e.Y - downPoint.Y) > 4)
            {
                pendingDrag = false;
                windowDrag = true;
                try { dragStartMouse = PointToScreen(p); } catch { dragStartMouse = Cursor.Position; }
                dragStartPos = new Point(Left, Top);
                snapEdgeX = -1;
                snapEdgeY = -1;
                Cursor = Cursors.SizeAll;
                return;
            }
        }

        if (windowDrag && e.Button == MouseButtons.Left)
        {
            Point now;
            try { now = PointToScreen(p); } catch { now = Cursor.Position; }
            int nx = dragStartPos.X + (now.X - dragStartMouse.X);
            int ny = dragStartPos.Y + (now.Y - dragStartMouse.Y);

            int ex, ey;
            nx = SnapX(nx, out ex);
            ny = SnapY(ny, out ey);

            bool hintChanged = (ex != snapEdgeX || ey != snapEdgeY);
            snapEdgeX = ex;
            snapEdgeY = ey;

            Location = new Point(nx, ny);
            if (hintChanged) Render(); else BlitFrame();
            return;
        }

        if (resizing && !Data.Locked)
        {
            int w = sizeStart.Width + (e.X - resizeStart.X);
            int h = sizeStart.Height + (e.Y - resizeStart.Y);
            if (w < 280) w = 280;
            if (h < 180) h = 180;

            // 右边缘 / 下边缘吸附
            int ex = -1, ey = -1;
            int right = Left + w, bottom = Top + h;
            int bestW = SNAP + 1, bestH = SNAP + 1;
            foreach (int c in CollectX())
            {
                int d = c - right;
                int ad = Math.Abs(d);
                if (ad <= SNAP && ad < bestW) { bestW = ad; w = c - Left; ex = 1; }
            }
            foreach (int c in CollectY())
            {
                int d = c - bottom;
                int ad = Math.Abs(d);
                if (ad <= SNAP && ad < bestH) { bestH = ad; h = c - Top; ey = 1; }
            }
            if (w < 280) { w = 280; ex = -1; }
            if (h < 180) { h = 180; ey = -1; }
            snapEdgeX = ex;
            snapEdgeY = ey;

            Width = w;
            Height = h;
            return;
        }

        int t = HitTile(p);
        int tb = HitTab(p);
        bool hh = headerRect.Contains(p);
        if (t != hoverTile || tb != hoverTab || hh != headHot)
        {
            hoverTile = t;
            hoverTab = tb;
            headHot = hh;
            Render();
        }
        if (!Data.Locked && e.X > Width - GRIP && e.Y > Height - GRIP) Cursor = Cursors.SizeNWSE;
        else if (headerRect.Contains(p) && !Data.Locked) Cursor = Cursors.SizeAll;
        else Cursor = Cursors.Default;
    }

    // 鼠标离开盒子：把悬停高亮和标题栏状态清掉，否则高亮会留在原地不走
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverTile != -1 || hoverTab != -1 || headHot)
        {
            hoverTile = -1;
            hoverTab = -1;
            headHot = false;
            Render();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        pendingDrag = false;

        if (windowDrag)
        {
            windowDrag = false;
            snapEdgeX = -1;
            snapEdgeY = -1;
            Cursor = Cursors.Default;
            // 以真实窗口矩形为准，不用托管 Left/Top：句柄创建期若带过边框，
            // 两者会差 8/31，写进配置就会让盒子每次重启都偏一点。
            RECT dr;
            if (GetWindowRect(Handle, out dr)) { Data.X = dr.L; Data.Y = dr.T; }
            else { Data.X = Left; Data.Y = Top; }
            Render();
            Store.Save(root);
        }

        if (bandSelecting)
        {
            bandSelecting = false;
            bandBase.Clear();
            Render();
        }

        if (pressTile >= 0 && deferClear && !movedSincePress)
        {
            sel.Clear();
            sel.Add(pressTile);
            selAnchor = pressTile;
            deferClear = false;
            Render();
        }
        else if (deferClear && movedSincePress)
        {
            deferClear = false;
        }

        pressTile = -1;
        movedSincePress = false;

        if (resizing)
        {
            resizing = false;
            Data.W = Width; Data.H = Height;
            Store.Save(root);
        }
        // 注意：这里**不能**再无条件写 Data.X = Left / Data.Y = Top。
        // 位置只在"真的拖过窗口"那个分支里保存；否则随便点一下盒子就会把窗口当时的
        // 真实坐标固化进内存，一旦系统/边框给过偏移，就会被下一次保存带进配置。
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        DTab tab = Cur;
        int cols = Math.Max(1, (contentRect.Width - 8) / TILE_W);
        int rows = (int)Math.Ceiling(tab.Items.Count / (double)cols);
        int contentH = rows * TILE_H + 8;
        int maxScroll = Math.Max(0, contentH - contentRect.Height);
        if (maxScroll <= 0) return;
        scrollY -= e.Delta / 120 * (TILE_H / 2);
        if (scrollY < 0) scrollY = 0;
        if (scrollY > maxScroll) scrollY = maxScroll;
        Render();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Control && e.KeyCode == Keys.A)
        {
            sel.Clear();
            DTab t = Cur;
            for (int i = 0; i < t.Items.Count; i++) sel.Add(i);
            Render();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Escape)
        {
            sel.Clear();
            bandSelecting = false;
            Render();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Delete && sel.Count > 0)
        {
            RemoveSelectedToDesktop();
            e.Handled = true;
        }
        // F2：选中一个图标就改图标名，没选就改当前子盒子（和资源管理器一致）
        if (e.KeyCode == Keys.F2)
        {
            if (sel.Count == 1)
            {
                int idx = -1;
                for (int i = 0; i < Cur.Items.Count; i++) if (sel.Contains(i)) { idx = i; break; }
                BeginRenameItem(idx);
            }
            else BeginRenameTab();
            e.Handled = true;
        }
    }

    // ---------- 拖拽 ----------

    int InsertIndexAt(Point p)
    {
        if (!contentRect.Contains(p)) return -1;
        DTab t = Cur;
        if (t.Items.Count == 0) return 0;
        int cols = Math.Max(1, (contentRect.Width - 8) / TILE_W);
        int innerX = contentRect.X + 4;
        int innerY = contentRect.Y + 4;
        int col = (p.X - innerX) / TILE_W;
        if (col < 0) col = 0;
        if (col >= cols) col = cols - 1;
        int row = (p.Y - innerY + scrollY) / TILE_H;
        if (row < 0) row = 0;
        int idx = row * cols + col;
        if (idx < 0) idx = 0;
        if (idx > t.Items.Count) idx = t.Items.Count;
        return idx;
    }

    void BeginItemDrag(int tile)
    {
        DTab t = Cur;
        if (tile < 0 || tile >= t.Items.Count) return;
        if (!sel.Contains(tile)) { sel.Clear(); sel.Add(tile); }
        selAnchor = tile;

        List<DItem> items = SelectedItems();
        if (items.Count == 0) return;

        Program.PendingDrag = new DragPayload(this, Data.Id, curTab, items);
        lastClickTile = -1;

        // 真正交给系统的数据：CF_HDROP（资源管理器能收、浏览器的上传框也认这个）
        // + 内部标记（自己的另一个盒子吃到时走"搬进子盒子"而不是复制一份在桌面）
        DataObject dobj = BuildDragData(items, (ModifierKeys & Keys.Control) != 0);

        // 视觉：被拖的图标半透明 + 鼠标跟一个小方块
        dragLift = true;
        Render();
        try { dragGhost = DragGhost.Create(items); }
        catch (Exception ex) { dragGhost = null; Log.W("ghost fail " + ex.Message); }

        DragDropEffects result = DragDropEffects.None;
        try
        {
            // Copy|Move 都给：桌面默认按"剪切"（由上面的 Preferred DropEffect 指定），
            // 浏览器上传框只会要 Copy，两条路都通。
            result = DoDragDrop(dobj, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex) { Log.W("drag fail " + ex.Message); }
        finally
        {
            if (dragGhost != null) { try { dragGhost.Close(); dragGhost.Dispose(); } catch { } dragGhost = null; }
            dragLift = false;
            Program.PendingDrag = null;
            dragOver = false;
            dropOnTab = false;
            dropTabHi = -1;
            dropIndex = -1;
            hoverTile = -1;
            Cursor = Cursors.Default;
            Render();
            SchedulePruneMovedOut(items);
        }
    }

    // 拖拽载荷（拖拽和 --dragtest 共用一份，保证测的就是真跑的）
    public static DataObject BuildDragData(List<DItem> items, bool copy)
    {
        string[] paths = new string[items.Count];
        int okCount = 0;
        foreach (DItem it in items)
            if (File.Exists(it.Path) || Directory.Exists(it.Path)) paths[okCount++] = it.Path;
        Array.Resize(ref paths, okCount);

        DataObject dobj = new DataObject();
        dobj.SetData("DeskBoxItems", "1");
        if (paths.Length > 0)
        {
            dobj.SetData(DataFormats.FileDrop, paths);
            // "Preferred DropEffect"：告诉目标这次拖拽是"剪切"。
            // 不加这一条，G:（盒子里）拖到 C:（桌面）跨盘会被当成复制；
            // 按住 Ctrl 拖 = 复制，跟资源管理器的习惯一致。
            byte[] eff = BitConverter.GetBytes(copy ? 1 : 2);   // 1=DROPEFFECT_COPY 2=DROPEFFECT_MOVE
            dobj.SetData("Preferred DropEffect", new MemoryStream(eff));
        }
        return dobj;
    }

    // 拖拽结束后核对：哪些条目已经不在了 → 说明目标端执行的是"剪切"（桌面/资源管理器移动了它），
    // 从盒子里把条目摘掉。目标端是浏览器上传 / 复制到别处时文件还在，条目保留。
    //
    // **必须多次重试**：跨盘（G: → C: 桌面）的"移动"是"先复制、再删源"，
    // 大文件复制完可能要好几秒，Drop 返回时源文件还在。只查一次就会永远留下僵尸图标。
    static readonly int[] PRUNE_DELAYS = new int[] { 600, 1500, 3000, 6000, 12000, 25000 };
    void SchedulePruneMovedOut(List<DItem> items)
    {
        if (items == null || items.Count == 0) return;

        // 注意：这里**不能**先过滤掉"已经不存在的"再判断是否为空。
        // 小文件跨盘移动瞬间就完成，DoDragDrop 返回时源文件已经没了 ——
        // 早期版本就是在这里 return，于是图标永远留在盒子里删不掉。
        // 正确做法：全挂上，校验时只摘"不存在"的。
        List<DItem> watch = new List<DItem>(items);
        Log.W("prune 排期 " + watch.Count + " 项（最多重试 " + PRUNE_DELAYS.Length + " 次）");

        // 除了定时器，用户下次碰盒子时也会顺手再查一遍，
        // 这样系统把文件拖到一半、过很久才删源的情况也能收干净。
        foreach (DItem it in watch)
            if (!pendingPrune.Contains(it)) pendingPrune.Add(it);

        for (int k = 0; k < PRUNE_DELAYS.Length; k++)
        {
            bool last = (k == PRUNE_DELAYS.Length - 1);
            Timer tm = new Timer();
            tm.Interval = PRUNE_DELAYS[k];
            tm.Tick += delegate
            {
                tm.Stop();
                pruneTimers.Remove(tm);
                tm.Dispose();
                PruneMissing(pendingPrune, "drag-out", last);
            };
            pruneTimers.Add(tm);
            tm.Start();
        }
    }

    // 只处理"明确是拖出去的那几项"，绝不碰"需重加"里那些故意保留的丢失条目
    static readonly List<DItem> pendingPrune = new List<DItem>();
    void PruneMissing(List<DItem> list, string why, bool dropSurvivors)
    {
        if (list == null || list.Count == 0) return;
        int gone = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            DItem it = list[i];
            if (File.Exists(it.Path) || Directory.Exists(it.Path))
            {
                // 还在 = 目标端只是复制/上传（或还没删源）；最后一轮就把它从清单里放掉，
                // 免得以后每次点盒子都去 stat 一堆早就没事的路径。
                if (dropSurvivors) list.RemoveAt(i);
                continue;
            }
            if (RemoveItemRef(it)) gone++;
            list.RemoveAt(i);
        }
        if (gone > 0)
        {
            Store.Save(root);
            IconCache.Clear();
            Program.StartIconPrewarm(true);
            sel.Clear();
            Log.W(why + " 已移出 " + gone + " 项（目标端做了剪切）");
            Render();
        }
    }

    bool RemoveItemRef(DItem it)
    {
        foreach (DTab tb in Data.Tabs)
            if (tb.Items != null && tb.Items.Remove(it)) return true;
        return false;
    }

    protected override void OnGiveFeedback(GiveFeedbackEventArgs e)
    {
        base.OnGiveFeedback(e);
        // 拖拽期间 UI 线程被 DoDragDrop 占着，定时器不跑，只能在这个回调里挪拖影
        if (dragGhost != null) { try { dragGhost.FollowCursor(); } catch { } }
        e.UseDefaultCursors = true;
    }

    // 拖放期间冻结 z 序（每次 DragOver 续期 600ms）。
    // OLE 每隔几毫秒重新用 WindowFromPoint 找 drop target；盒子这期间在桌面层上下翻，
    // 目标窗口就一直变，松手时 OnDragDrop 根本不会到我们这里来。
    void TouchDragGuard() { dragGuardUntil = Environment.TickCount + 600; }

    static bool IsOurDrag(DragEventArgs e)
    {
        return Program.PendingDrag != null && e.Data != null && e.Data.GetDataPresent("DeskBoxItems");
    }

    void UpdateDrag(DragEventArgs e)
    {
        Point p = PointToClient(new Point(e.X, e.Y));
        if (IsOurDrag(e))
        {
            int newTab = -1;
            int newIndex = -1;
            bool onTab = false;
            int tb = HitTab(p);
            if (tb >= 0)
            {
                onTab = true;
                newTab = tb;
            }
            else
            {
                newTab = curTab;
                newIndex = InsertIndexAt(p);
                if (newIndex < 0) newIndex = Cur.Items.Count;
            }

            bool changed = (!dragOver) || onTab != dropOnTab || newTab != dropTabHi || newIndex != dropIndex;
            dragOver = true;
            dropOnTab = onTab;
            dropTabHi = newTab;
            dropIndex = newIndex;
            e.Effect = DragDropEffects.Move;
            if (changed) Render();
            return;
        }
        bool wasActive = dragOver;
        int prevIdx = dropIndex;
        dragOver = false;
        dropOnTab = false;
        dropTabHi = -1;
        dropIndex = -1;
        if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // 以前写死 Move。桌面快捷方式 / 跨盘 / 部分 shell 项只给 Copy|Link，
            // Effect 不是 AllowedEffect 的子集时 OLE 直接出"禁止"光标，
            // 松手根本不触发 OnDragDrop —— 这就是"桌面图标拖不进盒子"。
            DragDropEffects ok = e.AllowedEffect &
                (DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link);
            if ((ok & DragDropEffects.Move) != 0) ok = DragDropEffects.Move;
            else if ((ok & DragDropEffects.Copy) != 0) ok = DragDropEffects.Copy;
            e.Effect = ok;
            // 外部拖入也要画插入指示线。以前 dragOver 一直是 false，
            // 拖到盒子上一点反馈都没有，用户会以为"这里放不进去"。
            if (ok != DragDropEffects.None)
            {
                int at = InsertIndexAt(p);
                if (at >= 0)
                {
                    dragOver = true;
                    dropTabHi = curTab;
                    dropIndex = at;
                }
            }
        }
        else e.Effect = DragDropEffects.None;
        if (wasActive != dragOver || prevIdx != dropIndex) Render();
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        TouchDragGuard();
        Log.W("drag enter allowed=" + drgevent.AllowedEffect +
              " ours=" + IsOurDrag(drgevent) +
              " files=" + (drgevent.Data != null && drgevent.Data.GetDataPresent(DataFormats.FileDrop)));
        UpdateDrag(drgevent);
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        base.OnDragOver(drgevent);
        TouchDragGuard();
        UpdateDrag(drgevent);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        TouchDragGuard();
        dragOver = false;
        dropOnTab = false;
        dropTabHi = -1;
        dropIndex = -1;
        Render();
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        Log.W("drop allowed=" + drgevent.AllowedEffect + " ours=" + IsOurDrag(drgevent));

        DragPayload p = Program.PendingDrag;
        bool ours = IsOurDrag(drgevent);
        if (p != null && !ours) Program.PendingDrag = null;   // 残留标记别再污染后续拖入
        int tgtTab = (dropTabHi >= 0 && dropTabHi < Data.Tabs.Count) ? dropTabHi : curTab;
        int tgtIdx = dropOnTab ? Data.Tabs[tgtTab].Items.Count : dropIndex;
        if (tgtIdx < 0) tgtIdx = Data.Tabs[tgtTab].Items.Count;

        dragOver = false;
        dropOnTab = false;
        dropTabHi = -1;
        dropIndex = -1;

        if (ours)
        {
            ApplyDrop(p, tgtTab, tgtIdx);
            return;
        }

        if (drgevent.Data == null || !drgevent.Data.GetDataPresent(DataFormats.FileDrop))
        {
            Render();
            return;
        }
        string[] paths = (string[])drgevent.Data.GetData(DataFormats.FileDrop);
        if (paths == null || paths.Length == 0) { Render(); return; }
        Log.W("drop-in paths=" + paths.Length + " first=" + (paths.Length > 0 ? paths[0] : ""));

        DTab t = (tgtTab >= 0 && tgtTab < Data.Tabs.Count) ? Data.Tabs[tgtTab] : Cur;
        string dir = Store.TabDir(Data, t);
        List<DItem> added = new List<DItem>();
        List<string> fails = new List<string>();
        foreach (string path in paths)
        {
            string err;
            string moved = Store.MoveToDir(dir, path, out err);
            if (moved == null)
            {
                fails.Add(Store.LabelOf(path) + " —— " + err);
                continue;
            }
            DItem it = new DItem();
            it.Path = moved;
            it.Name = Store.LabelOf(moved);
            added.Add(it);
        }
        if (added.Count > 0)
        {
            int at = tgtIdx;
            if (at < 0 || at > t.Items.Count) at = t.Items.Count;
            t.Items.InsertRange(at, added);
            curTab = Data.Tabs.IndexOf(t);
            if (curTab < 0) curTab = 0;
            sel.Clear();
            foreach (DItem it in added)
            {
                int ix = t.Items.IndexOf(it);
                if (ix >= 0) sel.Add(ix);
            }
            scrollToSelection();
        }
        Render();
        Store.Save(root);
        if (fails.Count > 0)
        {
            MessageBox.Show("以下项目没能移动过来（文件被占用或已不存在）：\r\n\r\n" + string.Join("\r\n", fails.ToArray()), "DeskBox");
        }
    }

    void scrollToSelection()
    {
        // 保持简单：选中项在第一屏可见
        scrollY = 0;
    }

    // --droptest：只跑一遍 UpdateDrag 的 Effect 判定，不搬文件、不写配置。
    public string TestDragEffect(DragDropEffects allowed)
    {
        DataObject d = new DataObject();
        d.SetData(DataFormats.FileDrop, new string[] { "G:\\DeskBox\\_droptest_dummy.txt" });
        DragEventArgs e = new DragEventArgs(d, 0, 40, 120, allowed, DragDropEffects.None);
        UpdateDrag(e);
        return e.Effect.ToString();
    }

    public void AfterExternalDrop()
    {
        Render();
    }

    public void DetachItem(DItem it)
    {
        foreach (DTab t in Data.Tabs) t.Items.Remove(it);
        Render();
        Store.Save(root);
    }

    void ReorderMulti(DTab t, List<DItem> moving, int insertAt)
    {
        if (insertAt < 0 || insertAt > t.Items.Count) insertAt = t.Items.Count;
        int before = 0;
        for (int i = 0; i < insertAt && i < t.Items.Count; i++)
        {
            if (moving.Contains(t.Items[i])) before++;
        }
        foreach (DItem it in moving) t.Items.Remove(it);
        int pos = insertAt - before;
        if (pos < 0) pos = 0;
        if (pos > t.Items.Count) pos = t.Items.Count;
        t.Items.InsertRange(pos, moving);
    }

    void ApplyDrop(DragPayload p, int tgtTab, int tgtIndex)
    {
        if (p == null || p.Items == null || p.Items.Count == 0) { Render(); return; }
        if (Data.Tabs == null || Data.Tabs.Count == 0) Data.Tabs.Add(Store.MakeTab("全部"));
        if (tgtTab < 0 || tgtTab >= Data.Tabs.Count) tgtTab = 0;
        DTab target = Data.Tabs[tgtTab];
        List<DItem> items = p.Items;
        List<string> fails = new List<string>();

        if (p.Src == this)
        {
            DTab srcTab = (p.SrcTab >= 0 && p.SrcTab < Data.Tabs.Count) ? Data.Tabs[p.SrcTab] : target;
            if (ReferenceEquals(srcTab, target))
            {
                ReorderMulti(target, items, tgtIndex);
            }
            else
            {
                // 换子盒子：文件也要跟着搬到目标子文件夹
                string dir = Store.TabDir(Data, target);
                List<DItem> ok = new List<DItem>();
                foreach (DItem it in items)
                {
                    string err;
                    string dest = Store.MoveToDir(dir, it.Path, out err);
                    if (dest == null) { fails.Add(Store.LabelOf(it.Path) + " —— " + err); continue; }
                    it.Path = dest;
                    ok.Add(it);
                }
                foreach (DItem it in ok) srcTab.Items.Remove(it);
                int at = tgtIndex;
                if (at < 0 || at > target.Items.Count) at = target.Items.Count;
                target.Items.InsertRange(at, ok);
                items = ok;
            }
        }
        else
        {
            // 换盒子：文件搬进目标盒子的目标子文件夹
            string dir = Store.TabDir(Data, target);
            List<DItem> ok = new List<DItem>();
            foreach (DItem it in items)
            {
                string err;
                string dest = Store.MoveToDir(dir, it.Path, out err);
                if (dest == null) { fails.Add(Store.LabelOf(it.Path) + " —— " + err); continue; }
                it.Path = dest;
                ok.Add(it);
            }
            if (p.Src != null && ok.Count > 0) p.Src.DetachItem_NoRender(ok);
            int at = tgtIndex;
            if (at < 0 || at > target.Items.Count) at = target.Items.Count;
            target.Items.InsertRange(at, ok);
            items = ok;
        }

        curTab = tgtTab;
        sel.Clear();
        foreach (DItem it in items)
        {
            int ix = target.Items.IndexOf(it);
            if (ix >= 0) sel.Add(ix);
        }
        if (sel.Count > 0)
        {
            List<int> l = new List<int>(sel);
            l.Sort();
            selAnchor = l[0];
        }
        Render();
        Store.Save(root);
        if (p.Src != null && p.Src != this) p.Src.AfterExternalDrop();
        if (fails.Count > 0)
        {
            MessageBox.Show("以下项目没能移动过来（文件被占用或已不存在）：\r\n\r\n" + string.Join("\r\n", fails.ToArray()), "DeskBox");
        }
    }

    public void DetachItem_NoRender(List<DItem> items)
    {
        foreach (DItem it in items)
        {
            foreach (DTab t in Data.Tabs) t.Items.Remove(it);
        }
        sel.Clear();
        Render();
        Store.Save(root);
    }

    // ---------- 菜单 ----------

    // 右键菜单统一从这里弹出。
    // 坑：盒子钉在桌面时自己是 WS_EX_TOPMOST，而右键菜单只是个普通弹窗，
    // 于是菜单被自己的盒子盖住（表现为菜单发灰、盒子的图标压在菜单文字上）。
    // 三层防护，一层都别删：
    //  1) Opened 里 SetWindowPos 顶成 topmost：菜单一出现就压到盒子上面；
    //  2) GuardMenuZ() 常驻守护：菜单开着时，只要发现哪个盒子跑到菜单前面就立刻把菜单顶回去
    //     （用户机器上确实出现过"菜单刚打开还在上面，随后被盒子反超"的情况，靠 1 拦不住）。
    //  （ToolStripDropDown.TopMost 是 protected，外部设不了，所以没有"创建时即 topmost"这一层。）
    static void ShowMenuTopmost(ContextMenuStrip m, Control owner, Point where)
    {
        activeMenu = m;
        m.Closed += delegate {
            if (ReferenceEquals(activeMenu, m)) activeMenu = null;
            // "盒子颜色"的色点是每次打开菜单现画的 Bitmap，菜单不释放就等于漏 GDI 句柄
            DisposeItemImages(m.Items);
        };
        m.Opened += delegate
        {
            try
            {
                if (m.Handle != IntPtr.Zero)
                    SetWindowPos(m.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            }
            catch (Exception ex) { Log.W("menu topmost fail " + ex.Message); }
        };
        m.Show(owner, where);
        LastMenuHwnd = m.Handle;   // 仅 --menutest 用
    }

    static ContextMenuStrip activeMenu;

    static void DisposeItemImages(ToolStripItemCollection items)
    {
        foreach (ToolStripItem it in items)
        {
            Image img = it.Image;
            it.Image = null;
            if (img != null) { try { img.Dispose(); } catch { } }
            ToolStripMenuItem mi = it as ToolStripMenuItem;
            if (mi != null && mi.DropDownItems.Count > 0) DisposeItemImages(mi.DropDownItems);
        }
    }

    // 当前是不是有我们自己的右键菜单开着（排障用）
    public static bool MenuActive { get { return activeMenu != null; } }
    public static ContextMenuStrip CurrentMenu { get { return activeMenu; } }

    // 有没有任何菜单开着（自绘的 ContextMenuStrip 或原生 shell 菜单）
    // 配色对话框开着时也算"有菜单开着"：不然 GuardTick 会把盒子重新抬到对话框前面
    // 就地改名框也是盖在盒子上面的窗口：守护若照常 RaiseAboveDesktop，
    // 盒子会被抬到输入框前面，输入框就被盖住了。
    static bool MenuOpen()
    {
        return activeMenu != null || ShellMenu.Active || StyleForm.AnyOpen || InlineEditor.Current != null;
    }

    // 菜单开着时，有没有哪个自己的盒子排在它前面（0 = 最前面，越小越靠前）
    public static bool AnyBoxAbove(IntPtr menu)
    {
        if (menu == IntPtr.Zero) return false;
        IntPtr h = GetTopWindow(IntPtr.Zero);
        int i = 0;
        while (h != IntPtr.Zero && i < 400)
        {
            if (h == menu) return false;
            if (IsOwnHwnd(h)) return true;
            h = GetWindow(h, GW_HWNDNEXT);
            i++;
        }
        return false;
    }

    // 子菜单（配色主题）展开时不能动父菜单的层级，否则子菜单会被父菜单盖住
    static bool MenuSubOpen()
    {
        if (activeMenu == null) return false;
        try
        {
            foreach (ToolStripItem it in activeMenu.Items)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null && mi.DropDown != null && mi.DropDown.Visible) return true;
            }
        }
        catch { }
        return false;
    }

    // 菜单层级守护：跟盒子自己的守护同一个节奏跑（15ms / 40ms）。
    // 无条件把菜单顶回最前 —— 不依赖"先发现盒子在菜单前面"：用户机器上的反超时机和路径
    // 我没能复现出来（试过强行抬盒子、重绘、等桌面动画，全都复现不了），所以这里直接
    // 持续兜底：只要菜单开着，每 15ms 重新声明一次层级，任何反超都会在一帧内被抹掉。
    // 子菜单（配色主题）展开时不能动父菜单的层级，否则子菜单会被父菜单盖住。
    static int menuGuardLog;
    static void GuardMenuZ()
    {
        // 原生 shell 菜单（#32768）不是 topmost，盒子自己是 topmost，会被自己的盒子盖住
        if (ShellMenu.Active) ShellMenu.PinTopmost();
        if (activeMenu == null) return;
        if (MenuSubOpen()) return;
        IntPtr h = IntPtr.Zero;
        try { h = activeMenu.Handle; } catch { return; }
        if (h == IntPtr.Zero) return;
        try
        {
            if (AnyBoxAbove(h) && Environment.TickCount - menuGuardLog > 1000)
            {
                menuGuardLog = Environment.TickCount;
                Log.W("guard menuz  盒子跑到菜单前面，已把菜单顶回");
            }
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }
        catch (Exception ex) { Log.W("menuz fail " + ex.Message); }
    }

    public static IntPtr LastMenuHwnd;

    // 仅 --perftest：切到第 i 个标签并计时（含 Compose + Apply 整条渲染）
    public double TestSwitchTabMs(int i)
    {
        if (i < 0 || i >= Data.Tabs.Count) return -1;
        int t0 = Environment.TickCount;
        curTab = i;
        scrollY = 0;
        sel.Clear();
        Render();
        return Environment.TickCount - t0;
    }

    public int TestTabCount { get { return Data.Tabs.Count; } }

    // 仅 --prunetest：模拟"拖出去之后目标端把文件搬走了"，看条目会不会被自动摘掉
    public string TestPruneOnce()
    {
        DTab t = Cur;
        string dir = Store.TabDir(Data, t);
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "_prunetest.tmp");
        File.WriteAllText(f, "deskbox prune test");
        DItem it = new DItem();
        it.Path = f;
        it.Name = "_prunetest.tmp";
        t.Items.Add(it);
        Render();
        File.Delete(f);                       // 模拟被目标端剪切走
        SchedulePruneMovedOut(new List<DItem> { it });
        return "已加入条目并删掉文件，等 1.5 秒看是否自动摘掉";
    }

    public bool TestHasItem(string name)
    {
        foreach (DTab tb in Data.Tabs)
            foreach (DItem it in tb.Items)
                if (it.Name == name) return true;
        return false;
    }

    public void TestShowBoxMenu(Point p) { ShowBoxMenu(p); }
    public string TestTitle() { return Data.Title; }
    public string TestTabName(int i) { return (i >= 0 && i < Data.Tabs.Count) ? Data.Tabs[i].Name : null; }
    public string TestItemName(int i) { return (i >= 0 && i < Cur.Items.Count) ? Cur.Items[i].Name : null; }
    public void TestRender() { Render(); }

    // 仅 --shellmenu：对第一个"真实存在"的条目弹原生菜单（阻塞到菜单关闭）
    public bool TestShowFirstTileMenu()
    {
        DTab t = Cur;
        for (int i = 0; i < t.Items.Count; i++)
        {
            DItem it = t.Items[i];
            if (!File.Exists(it.Path) && !Directory.Exists(it.Path)) continue;
            Rectangle r = (i < tileRects.Count) ? tileRects[i] : new Rectangle(60, 80, 60, 60);
            Log.W("shellmenu 弹菜单 tile=" + i + " " + it.Name);
            ShowTileMenu(i, new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
            Log.W("shellmenu 菜单已返回");
            return true;
        }
        Log.W("shellmenu 没有真实存在的条目可测");
        return false;
    }

    // 仅 --dragtest：把真实载荷的格式列出来核对
    public string TestDragDataDump()
    {
        List<DItem> items = new List<DItem>();
        DTab t = Cur;
        for (int i = 0; i < t.Items.Count && items.Count < 2; i++)
            if (File.Exists(t.Items[i].Path) || Directory.Exists(t.Items[i].Path)) items.Add(t.Items[i]);
        if (items.Count == 0) return "没有真实存在的条目可测";

        StringBuilder sb = new StringBuilder();
        for (int k = 0; k < 2; k++)
        {
            bool copy = (k == 1);
            DataObject d = BuildDragData(items, copy);
            sb.AppendLine((copy ? "[Ctrl 拖 = 复制]" : "[默认拖 = 剪切]"));
            sb.AppendLine("  formats=" + string.Join(",", d.GetFormats()));
            string[] fd = d.GetData(DataFormats.FileDrop) as string[];
            sb.AppendLine("  FileDrop=" + (fd == null ? "空" : string.Join(" | ", fd)));
            object pe = d.GetData("Preferred DropEffect");
            int val = -1;
            if (pe is MemoryStream)
            {
                MemoryStream ms = (MemoryStream)pe;
                byte[] buf = ms.ToArray();
                if (buf.Length >= 4) val = BitConverter.ToInt32(buf, 0);
            }
            sb.AppendLine("  PreferredDropEffect=" + val + "（1=COPY 2=MOVE）");
        }
        return sb.ToString();
    }

    // 仅 --menutest：人为把菜单插到盒子正下方，模拟"盒子盖住菜单"的故障态，
    // 用来看 GuardMenuZ() 能不能把它救回来。
    public void TestForceMenuBelowBox()
    {
        if (LastMenuHwnd == IntPtr.Zero) return;
        SetWindowPos(LastMenuHwnd, Handle, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }
    public void TestRaise() { RaiseAboveDesktop("test"); }
    public bool TestTopmost { get { return IsHandleCreated && (GetWindowLong(Handle, -20) & 0x8) != 0; } }

    void LaunchItem(int index)
    {
        DTab t = Cur;
        if (index < 0 || index >= t.Items.Count) return;
        LaunchItems(new List<DItem> { t.Items[index] });
    }

    // 右键菜单（用户 2026-10-24 定稿）：只保留 8 项，第三方扩展项全部不要。
    // 右键本身**不再调用 shell 的 QueryContextMenu**（那一步会把 WinRAR / 网盘等
    // 扩展全加载一遍，250~850ms，菜单 127 项），改成自己建 8 项菜单（≈0ms），
    // 用户点了哪一项再按"动词名"单独调 shell。
    // Shift + 右键 = DeskBox 自己的功能（移动到其他标签 / 重命名显示名 /
    // 从盒子里移除 / 搬进当前子文件夹 / 在资源管理器中定位）。
    void ShowTileMenu(int index, Point where)
    {
        DTab t = Cur;
        if (index < 0 || index >= t.Items.Count) return;
        if (!sel.Contains(index)) { sel.Clear(); sel.Add(index); selAnchor = index; Render(); }
        List<DItem> targets = SelectedItems();
        if (targets.Count == 0) { targets.Add(t.Items[index]); }
        int n = targets.Count;

        // Shift + 右键：DeskBox 自己的功能
        if ((ModifierKeys & Keys.Shift) != 0) { ShowTileMenuExtras(where, targets); return; }

        List<string> real = new List<string>();
        foreach (DItem it in targets)
            if (File.Exists(it.Path) || Directory.Exists(it.Path)) real.Add(it.Path);

        // 文件全都不在了：只能给"清图标"，没法再调 shell
        if (real.Count == 0) { ShowTileMenuExtras(where, targets); return; }

        bool canRunAs = n == 1 && CanRunAs(targets[0].Path);
        string[] labels = new string[10];
        bool[] off = new bool[10];
        labels[0] = n > 1 ? ("打开所选（" + n + " 项）") : "打开";
        off[0] = n > 1;
        labels[1] = "打开文件所在的位置";
        off[1] = n > 1;
        labels[2] = "以管理员身份运行";
        off[2] = !canRunAs;
        labels[3] = null;                       // 分隔线
        labels[4] = "剪切";
        labels[5] = "复制";
        labels[6] = "创建快捷方式";
        labels[7] = null;                       // 分隔线
        labels[8] = "删除";
        labels[9] = "属性";

        int pick = ShellMenu.ShowQuickMenu(PointToScreen(where), labels, off);
        if (pick < 0) return;
        Log.W("tilemenu " + (pick < labels.Length ? labels[pick] : "?"));

        string[] paths = real.ToArray();
        switch (pick)
        {
            case 0: LaunchItems(targets); break;
            case 1: RevealInExplorer(targets[0]); break;
            case 2: ShellMenu.InvokeVerb(paths, "runas"); break;
            case 4: ShellMenu.InvokeVerb(paths, "cut"); break;
            case 5: ShellMenu.InvokeVerb(paths, "copy"); break;
            case 6: CreateShortcuts(targets); break;
            case 8:
                ShellMenu.InvokeVerb(paths, "delete");
                SchedulePruneMovedOut(targets);      // 删掉之后自动摘条目
                break;
            case 9: ShellMenu.InvokeVerb(paths, "properties"); break;
        }
        if (pick == 6 || pick == 8)
        {
            IconCache.Clear();
            Program.StartIconPrewarm(true);
        }
        Render();
    }

    // 哪些文件"以管理员身份运行"有意义
    static bool CanRunAs(string path)
    {
        try
        {
            string low = path.ToLower();
            return low.EndsWith(".exe") || low.EndsWith(".com") || low.EndsWith(".bat") ||
                   low.EndsWith(".cmd") || low.EndsWith(".msi") || low.EndsWith(".lnk");
        }
        catch { return false; }
    }

    // "创建快捷方式"：shell 没有稳定可用的动词名，这里自己做
    void CreateShortcuts(List<DItem> items)
    {
        int made = 0;
        foreach (DItem it in items)
        {
            try
            {
                string dir = Path.GetDirectoryName(it.Path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                string baseName = Path.GetFileNameWithoutExtension(it.Path) + " - 快捷方式";
                string dest = Path.Combine(dir, baseName + ".lnk");
                int k = 2;
                while (File.Exists(dest))
                {
                    dest = Path.Combine(dir, baseName + " (" + k + ").lnk");
                    k++;
                }
                Type wt = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(wt);
                object lnk = wt.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { dest });
                lnk.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { it.Path });
                lnk.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);

                DItem ni = new DItem();
                ni.Path = dest;
                ni.Name = Store.LabelOf(dest);
                Cur.Items.Add(ni);
                made++;
            }
            catch (Exception ex) { Log.W("shortcut fail " + ex.Message); }
        }
        if (made > 0)
        {
            Store.Save(root);
            Log.W("创建快捷方式 " + made + " 个");
        }
    }

    void RevealInExplorer(DItem it)
    {
        try
        {
            string p = it.Path;
            // 快捷方式要定位到它**指向的目标**，而不是 .lnk 文件自己。
            // 解析出来目标不存在（死链接）或 .url 解析出的是网址时，退回定位快捷方式本身。
            string tp = IconCache.Resolve(p);
            if (tp != p && (File.Exists(tp) || Directory.Exists(tp))) p = tp;
            if (File.Exists(p) || Directory.Exists(p))
                Process.Start("explorer.exe", "/select,\"" + p + "\"");
            else OpenFolder();
        }
        catch { OpenFolder(); }
    }

    // DeskBox 自己的功能菜单（Shift + 右键；文件已不存在时也走这里，只为清图标）
    void ShowTileMenuExtras(Point where, List<DItem> targets)
    {
        DTab t = Cur;
        int n = targets.Count;
        ContextMenuStrip m = new ContextMenuStrip();
        m.Items.Add(n > 1 ? ("打开所选（" + n + " 项）") : "打开", null, delegate { LaunchItems(targets); });

        if (n == 1)
        {
            DItem single = targets[0];
            m.Items.Add("重命名显示名", null, delegate { RenameItem(single); });
        }

        ToolStripMenuItem mv = new ToolStripMenuItem(n > 1 ? ("移动所选到其他标签（" + n + "）") : "移动到其他标签");
        foreach (DTab tb in Data.Tabs)
        {
            DTab target = tb;
            ToolStripMenuItem mi = new ToolStripMenuItem(tb.Name);
            mi.Click += delegate { MoveItemsToTab(targets, target); };
            mv.DropDownItems.Add(mi);
        }
        m.Items.Add(mv);

        if (!Store.InDir(Store.TabDir(Data, t), targets[0].Path) || n > 1)
        {
            m.Items.Add(n > 1 ? ("搬进当前子盒子文件夹（" + n + " 项）") : "搬进当前子盒子文件夹", null, delegate { MoveIntoCurrentTab(targets); });
        }

        m.Items.Add(new ToolStripSeparator());
        bool anyMissing = false;
        foreach (DItem it in targets)
            if (!File.Exists(it.Path) && !Directory.Exists(it.Path)) { anyMissing = true; break; }
        m.Items.Add(n > 1
            ? (anyMissing ? ("从盒子里移除所选（" + n + " 项，文件已不在的只清图标）") : ("从盒子里移除所选（" + n + " 项，放回桌面）"))
            : (anyMissing ? "从盒子里移除（文件已不在，只清图标）" : "从盒子里移除（放回桌面）"),
            null, delegate { RemoveSelectedToDesktop(); });
        m.Items.Add("在资源管理器中定位", null, delegate { RevealInExplorer(targets[0]); });
        ShowMenuTopmost(m, this, where);
    }

    void ShowBoxMenu(Point where)
    {
        ContextMenuStrip m = new ContextMenuStrip();

        // 增删这四件事排在最上面并挨在一起：盒子级一对、子盒子级一对。
        // 以前它们散在菜单头尾，中间夹着一堆"搬桌面快捷方式 / 诊断权限 / 打开文件夹"，
        // 每次都要在 13 项里找，属于纯噪音。
        m.Items.Add("新建盒子", null, delegate { Program.NewBox(Left + 40, Top + 40); });
        m.Items.Add("新建子盒子", null, delegate { AddTabDialog(); });
        m.Items.Add("重命名子盒子", null, delegate { BeginRenameTab(); });
        m.Items.Add("删除盒子", null, delegate { Program.DeleteBox(this); });
        ToolStripMenuItem delTab = new ToolStripMenuItem("删除子盒子", null, delegate { DeleteTab(); });
        delTab.Enabled = Data.Tabs.Count > 1;   // 只剩一个子盒子时置灰（DeleteTab 里也有同样的守卫）
        m.Items.Add(delTab);

        m.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem th = new ToolStripMenuItem("配色主题");
        for (int i = 0; i < Themes.All.Length; i++)
        {
            int ti = i;
            ToolStripMenuItem sub = new ToolStripMenuItem(Themes.All[i].Name);
            sub.Checked = (Data.ThemeId == i);
            sub.Click += delegate {
                Data.ThemeId = ti;
                accent = T.Accents[(Data.Accent % T.Accents.Length + T.Accents.Length) % T.Accents.Length];
                Render();
                Store.Save(root);
            };
            th.DropDownItems.Add(sub);
        }
        m.Items.Add(th);

        // 盒子颜色：以前 Accent 只能在 deskbox.json 里手改，界面上根本没有入口。
        // 每个色点就是 T.Accents 里的一支，点一下立刻换色并存盘。
        ToolStripMenuItem ac = new ToolStripMenuItem("盒子颜色");
        int ai0 = (Data.Accent % T.Accents.Length + T.Accents.Length) % T.Accents.Length;
        int ti0 = (Data.ThemeId >= 0 && Data.ThemeId < Themes.All.Length) ? Data.ThemeId : 0;
        for (int i = 0; i < T.Accents.Length; i++)
        {
            int ai = i;
            Color col = T.Accents[i];
            ToolStripMenuItem sub = new ToolStripMenuItem(
                Themes.AccentNames[ti0][i] + "   " + Hex(col), Swatch(col));
            sub.Checked = (ai0 == i);
            sub.Click += delegate {
                Data.Accent = ai;
                accent = T.Accents[ai];
                Render();
                Store.Save(root);
            };
            ac.DropDownItems.Add(sub);
        }
        m.Items.Add(ac);
        m.Items.Add("配色…", null, delegate { OpenStyleForm(); });

        m.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem boot = new ToolStripMenuItem("开机自动启动");
        boot.Checked = Program.StartupEnabled;
        boot.CheckState = Program.StartupEnabled ? CheckState.Checked : CheckState.Unchecked;
        boot.Click += delegate {
            Program.ToggleStartup();
            boot.Checked = Program.StartupEnabled;
            boot.CheckState = Program.StartupEnabled ? CheckState.Checked : CheckState.Unchecked;
        };
        m.Items.Add(boot);

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("退出 DeskBox", null, delegate { Program.Quit(); });
        ShowMenuTopmost(m, this, where);
    }

    static string Hex(Color c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }

    // 打开配色对话框。不设 owner：盒子是 topmost 的，owner 会让对话框跟着盒子一起沉下去。
    // 靠 TopMost + MenuOpen() 里认 StyleForm.AnyOpen 来保证它始终在最前面。
    public StyleForm OpenStyleForm()
    {
        StyleForm f = new StyleForm(this, root);
        f.Show();
        f.Activate();
        return f;
    }

    // 菜单里的色点：16×16 圆点 + 一圈淡描边。用圆形而不是方块，在深色菜单底上更清楚是"颜色样本"。
    static Bitmap Swatch(Color c)
    {
        Bitmap b = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (SolidBrush br = new SolidBrush(c)) g.FillEllipse(br, 1, 1, 14, 14);
            using (Pen p = new Pen(Color.FromArgb(90, 0, 0, 0), 1f)) g.DrawEllipse(p, 1.5f, 1.5f, 13, 13);
        }
        return b;
    }

    void OpenFolder()
    {
        try
        {
            string folder = Store.BoxDir(Data);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            ProcessStartInfo psi = new ProcessStartInfo(folder);
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开文件夹失败：" + Store.BoxDir(Data) + "\r\n" + ex.Message, "DeskBox");
        }
    }

    // 注意：OpenTabFolder / ImportDesktop 已经从盒子右键菜单里移除（用户 2026-10-24 圈删）。
    // 函数保留不动，将来要恢复入口直接加一行菜单即可，别当死代码删掉。
    void OpenTabFolder()
    {
        try
        {
            string folder = Store.TabDir(Data, Cur);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            ProcessStartInfo psi = new ProcessStartInfo(folder);
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开文件夹失败：" + Store.TabDir(Data, Cur) + "\r\n" + ex.Message, "DeskBox");
        }
    }

    void ImportDesktop()
    {
        DTab t = Cur;
        string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string dir = Store.TabDir(Data, t);
        int added = 0;
        List<string> fails = new List<string>();
        try
        {
            foreach (string f in Directory.GetFiles(desk))
            {
                string ext = Path.GetExtension(f).ToLower();
                if (ext != ".lnk" && ext != ".url") continue;
                string err;
                string moved = Store.MoveToDir(dir, f, out err);
                if (moved == null) { fails.Add(Path.GetFileName(f) + " —— " + err); continue; }
                DItem it = new DItem();
                it.Path = moved;
                it.Name = Store.LabelOf(moved);
                t.Items.Add(it);
                added++;
            }
        }
        catch (Exception ex) { fails.Add(ex.Message); }
        sel.Clear();
        Render();
        Store.Save(root);
        string msg = "已把 " + added + " 个快捷方式搬进「" + t.Name + "」文件夹。";
        if (fails.Count > 0) msg += "\r\n\r\n失败：\r\n" + string.Join("\r\n", fails.ToArray());
        MessageBox.Show(msg, "DeskBox");
    }

    void MoveIntoCurrentTab(List<DItem> items)
    {
        string dir = Store.TabDir(Data, Cur);
        List<string> fails = new List<string>();
        foreach (DItem it in items)
        {
            string err;
            string dest = Store.MoveToDir(dir, it.Path, out err);
            if (dest == null) { fails.Add(Store.LabelOf(it.Path) + " —— " + err); continue; }
            it.Path = dest;
        }
        Render();
        Store.Save(root);
        if (fails.Count > 0)
            MessageBox.Show("部分项目没搬动：\r\n\r\n" + string.Join("\r\n", fails.ToArray()), "DeskBox");
    }

    void MoveItemsToTab(List<DItem> items, DTab to)
    {
        string dir = Store.TabDir(Data, to);
        List<string> fails = new List<string>();
        foreach (DItem it in items)
        {
            string err;
            string dest = Store.MoveToDir(dir, it.Path, out err);
            if (dest == null) { fails.Add(Store.LabelOf(it.Path) + " —— " + err); continue; }
            it.Path = dest;
            foreach (DTab tb in Data.Tabs) { if (!ReferenceEquals(tb, to)) tb.Items.Remove(it); }
            if (!to.Items.Contains(it)) to.Items.Add(it);
        }
        sel.Clear();
        Render();
        Store.Save(root);
        if (fails.Count > 0)
            MessageBox.Show("部分项目没搬动：\r\n\r\n" + string.Join("\r\n", fails.ToArray()), "DeskBox");
    }

    void RemoveSelectedToDesktop()
    {
        DTab t = Cur;
        List<DItem> targets = SelectedItems();
        if (targets.Count == 0) return;
        string msg = targets.Count > 1
            ? ("从盒子里移除 " + targets.Count + " 项？（真实文件会放回桌面）")
            : ("从盒子里移除“" + targets[0].Name + "”？（真实文件会放回桌面）");
        if (MessageBox.Show(msg, "DeskBox", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        List<string> fails = new List<string>();
        int removed = 0;
        foreach (DItem it in targets)
        {
            // 文件已经不在了（拖出去时被目标端剪切走了，只是校验还没跑到）：
            // 这时"移除"就是单纯把条目摘掉，绝不能再报"文件已不存在"然后什么都不做，
            // 否则图标永远卡在盒子里删不掉。
            if (!File.Exists(it.Path) && !Directory.Exists(it.Path))
            {
                if (RemoveItemRef(it)) removed++;
                continue;
            }
            string err;
            string dest = Store.MoveToDesktop(it.Path, out err);
            if (dest == null) { fails.Add(Store.LabelOf(it.Path) + " —— " + err); continue; }
            it.Path = dest;
            if (t.Items.Remove(it)) removed++;
        }
        sel.Clear();
        scrollY = 0;
        Render();
        Store.Save(root);
        if (removed > 0) Log.W("remove-to-desktop 摘掉 " + removed + " 项");
        if (fails.Count > 0)
            MessageBox.Show("部分文件没能放回桌面：\r\n\r\n" + string.Join("\r\n", fails.ToArray()), "DeskBox");
    }

    // ---------- 就地重命名（不弹对话框） ----------
    // 背景色直接从屏幕上采样同位置的一个像素：底板是半透明的，算不出来，
    // 但"此刻屏幕上是什么颜色"拿得到。采样点一律落在同一行、远离文字的地方
    // （底板的光泽是竖向渐变，同一 y 上颜色不变），所以盖上去看不出来。
    Color SampleScreenColor(Point client)
    {
        try
        {
            Point s = PointToScreen(client);
            using (Bitmap b = new Bitmap(1, 1))
            {
                using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(s, Point.Empty, new Size(1, 1));
                Color c = b.GetPixel(0, 0);
                return Color.FromArgb(255, c);
            }
        }
        catch { return Color.FromArgb(255, BgCol); }
    }

    void BeginInline(Rectangle r, Point sample, string text, int px, bool centered, Color fg, Action<string, bool> cb)
    {
        if (r.Width < 20 || r.Height < 10) { Log.W("inline 放弃 rect=" + r); return; }
        Color bg = SampleScreenColor(sample);
        Rectangle scr = RectangleToScreen(r);
        Log.W("inline r=" + r + " bg=" + Store.ToHex(bg) + " text=" + text);
        InlineEditor.Begin(text, scr, bg, fg, px, centered, cb);
    }

    public void BeginRenameBox()
    {
        Rectangle r = new Rectangle(22, 2, Math.Max(60, Width - 130), HEAD_H - 4);
        BeginInline(r, new Point(18, r.Y + r.Height / 2), Data.Title, 15, false, TextBase,
            delegate(string v, bool ok) { if (ok) ApplyBoxName(v); });
    }

    public void BeginRenameTab()
    {
        if (curTab < 0 || curTab >= Data.Tabs.Count) return;
        if (tabRects.Count <= curTab) Render();
        if (tabRects.Count <= curTab) return;
        Rectangle tr = tabRects[curTab];
        // 必须和药丸严丝合缝：药丸底色是 Tint(30)，和旁边底板不同色，
        // 编辑框一旦比药丸宽，多出来的部分就是一块看得见的灰方块。
        Rectangle r = tr;
        BeginInline(r, new Point(tr.X + 3, tr.Y + tr.Height / 2), Data.Tabs[curTab].Name, 12, true, TextBase,
            delegate(string v, bool ok) { if (ok) ApplyTabName(v); });
    }

    public void BeginRenameItem(int idx)
    {
        DTab t = Cur;
        if (idx < 0 || idx >= t.Items.Count) return;
        if (tileRects.Count <= idx) Render();
        if (tileRects.Count <= idx) return;
        Rectangle cell = tileRects[idx];
        string nm = t.Items[idx].Name;
        // 标签格只有 76px，长名字居中被裁成「eek Harness」半截。按文字实际宽度放宽，
        // 底板光泽是竖向渐变、同一 y 上横向同色，所以横向变宽不会露出色差。
        int tw = TextRenderer.MeasureText(nm, fLbl).Width + 12;
        int w = Math.Max(TILE_W - 2, Math.Min(190, tw));
        int x0 = cell.X + (TILE_W - 2 - w) / 2;
        if (x0 < contentRect.X + 2) x0 = contentRect.X + 2;
        if (x0 + w > contentRect.Right - 2) x0 = Math.Max(contentRect.X + 2, contentRect.Right - 2 - w);
        Rectangle r = new Rectangle(x0, cell.Y + ICON + 9, w, 20);
        BeginInline(r, new Point(cell.X + 4, r.Y + 2), nm, 12, true, TextBase,
            delegate(string v, bool ok) { if (ok) ApplyItemName(idx, v); });
    }

    void ApplyBoxName(string v)
    {
        if (v != null) v = v.Trim();
        if (string.IsNullOrEmpty(v) || v == Data.Title) { Render(); return; }
        Data.Title = v;
        Text = "DeskBox - " + Data.Title;
        try { SyncBoxFolder(); }
        catch (Exception ex) { Log.W("rename box folder fail " + ex.Message); }
        Render();
        Store.Save(root);
    }

    void ApplyTabName(string v)
    {
        if (v != null) v = v.Trim();
        if (curTab < 0 || curTab >= Data.Tabs.Count) { Render(); return; }
        DTab t = Data.Tabs[curTab];
        if (string.IsNullOrEmpty(v) || v == t.Name) { Render(); return; }
        t.Name = v;
        try { SyncTabFolder(t); }
        catch (Exception ex) { Log.W("rename tab folder fail " + ex.Message); }
        Render();
        Store.Save(root);
    }

    void ApplyItemName(int idx, string v)
    {
        if (v != null) v = v.Trim();
        DTab t = Cur;
        if (idx < 0 || idx >= t.Items.Count) { Render(); return; }
        DItem it = t.Items[idx];
        if (string.IsNullOrEmpty(v) || v == it.Name) { Render(); return; }
        it.Name = v;
        Render();
        Store.Save(root);
    }

    void RenameItem(DItem it)
    {
        int idx = Cur.Items.IndexOf(it);
        if (idx >= 0) BeginRenameItem(idx);
    }

    void AddTabDialog()
    {
        string n = Ask("新标签名称", "标签 " + (Data.Tabs.Count + 1));
        if (n == null || n.Trim().Length == 0) return;
        DTab nt = Store.MakeTab(n.Trim());
        nt.Folder = Store.UniqueTabFolder(Data, nt, Store.SafeName(nt.Name));
        Data.Tabs.Add(nt);
        curTab = Data.Tabs.Count - 1;
        Store.TabDir(Data, nt);
        scrollY = 0;
        sel.Clear();
        Render();
        Store.Save(root);
    }

    void RenameTab()
    {
        BeginRenameTab();
    }

    // 子盒子改名 → 文件夹跟着改名，图标路径同步
    void SyncTabFolder(DTab t)
    {
        string boxDir = Store.BoxDir(Data);
        string oldDir = Store.TabDir(Data, t);
        string uniq = Store.UniqueTabFolder(Data, t, Store.SafeName(t.Name));
        string newDir = Path.Combine(boxDir, uniq);
        if (string.Equals(Path.GetFullPath(oldDir).TrimEnd('\\'),
                          Path.GetFullPath(newDir).TrimEnd('\\'),
                          StringComparison.OrdinalIgnoreCase))
        {
            t.Folder = uniq;
            return;
        }
        if (Directory.Exists(oldDir))
        {
            Directory.Move(oldDir, newDir);
            foreach (DItem it in t.Items)
            {
                string fn = Path.GetFileName(it.Path);
                if (!string.IsNullOrEmpty(fn)) it.Path = Path.Combine(newDir, fn);
            }
        }
        t.Folder = uniq;
    }

    void DeleteTab()
    {
        if (Data.Tabs.Count <= 1) { MessageBox.Show("至少保留一个标签。", "DeskBox"); return; }
        if (MessageBox.Show("删除子盒子“" + Data.Tabs[curTab].Name + "”？里面的真实文件会被放回桌面。", "DeskBox", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        DTab t = Data.Tabs[curTab];
        string dir = Store.TabDir(Data, t);
        List<string> fails = new List<string>();
        foreach (DItem it in t.Items)
        {
            string err;
            if (Store.MoveToDesktop(it.Path, out err) == null)
                fails.Add(Store.LabelOf(it.Path) + " —— " + err);
        }
        try
        {
            if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
        }
        catch { }
        Data.Tabs.RemoveAt(curTab);
        if (curTab >= Data.Tabs.Count) curTab = Data.Tabs.Count - 1;
        sel.Clear();
        Render();
        Store.Save(root);
        if (fails.Count > 0)
            MessageBox.Show("部分文件没能放回桌面：\r\n\r\n" + string.Join("\r\n", fails.ToArray()), "DeskBox");
    }

    void RenameBox()
    {
        BeginRenameBox();
    }

    // 盒子改名 → 盒子文件夹跟着改名
    void SyncBoxFolder()
    {
        string oldDir = Store.BoxDir(Data);
        string uniq = Store.UniqueBoxFolder(root, Data, Store.SafeName(Data.Title));
        string newDir = Path.Combine(Store.ItemsRoot, uniq);
        if (string.Equals(Path.GetFullPath(oldDir).TrimEnd('\\'),
                          Path.GetFullPath(newDir).TrimEnd('\\'),
                          StringComparison.OrdinalIgnoreCase))
        {
            Data.Folder = uniq;
            return;
        }
        if (Directory.Exists(oldDir)) Directory.Move(oldDir, newDir);
        Data.Folder = uniq;
        foreach (DTab t in Data.Tabs)
        {
            string td = Store.TabDir(Data, t);
            foreach (DItem it in t.Items)
            {
                string fn = Path.GetFileName(it.Path);
                if (!string.IsNullOrEmpty(fn)) it.Path = Path.Combine(td, fn);
            }
        }
    }

    string Ask(string caption, string def)
    {
        Form f = new Form();
        f.Text = caption;
        f.Width = 370; f.Height = 140;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.StartPosition = FormStartPosition.CenterParent;
        f.MaximizeBox = false; f.MinimizeBox = false;
        f.Font = new Font("Microsoft YaHei", 9f);
        f.TopMost = true;
        TextBox tb = new TextBox();
        tb.Left = 14; tb.Top = 14; tb.Width = 330; tb.Text = def;
        Button ok = new Button();
        ok.Text = "确定"; ok.Left = 176; ok.Top = 50; ok.Width = 78; ok.DialogResult = DialogResult.OK;
        Button no = new Button();
        no.Text = "取消"; no.Left = 262; no.Top = 50; no.Width = 78; no.DialogResult = DialogResult.Cancel;
        f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(no);
        f.AcceptButton = ok; f.CancelButton = no;
        string result = null;
        if (f.ShowDialog(this) == DialogResult.OK) result = tb.Text;
        f.Dispose();
        return result;
    }
}

class Program
{
    public static DRoot root;
    public static List<LayeredBox> forms = new List<LayeredBox>();

    // ---------- 图标预热 ----------
    // 切子标签慢的真原因：每个标签的图标是"第一次显示时才去取"的（.lnk 还要走
    // WScript.Shell 解析目标 + PrivateExtractIcons 递降取图，单个 3~6ms）。
    // 一个标签十来个新图标就是 50~110ms，看起来就像在"预加载"。
    // 所以启动后按 25ms/个 在后台悄悄取一遍，切标签就都是热缓存、0ms。
    static Queue<KeyValuePair<string, string>> warmQueue;
    static Timer warmTimer;
    public static void StartIconPrewarm(bool rebuild)
    {
        try
        {
            if (rebuild || warmQueue == null || warmQueue.Count == 0)
            {
                warmQueue = new Queue<KeyValuePair<string, string>>();
                foreach (LayeredBox f in forms)
                    foreach (DTab tb in f.Data.Tabs)
                        foreach (DItem it in tb.Items)
                            warmQueue.Enqueue(new KeyValuePair<string, string>(it.Path, it.Name));
            }
            if (warmTimer == null)
            {
                warmTimer = new Timer();
                warmTimer.Interval = 25;
                warmTimer.Tick += delegate
                {
                    if (warmQueue == null || warmQueue.Count == 0)
                    {
                        warmTimer.Stop();
                        Log.W("icon prewarm 完成 缓存=" + IconCache.Count);
                        return;
                    }
                    KeyValuePair<string, string> kv = warmQueue.Dequeue();
                    // 必须和 LayeredBox.ICON(=48) 一致：以前写成 44，缓存键里带尺寸，
                    // 预热出来的 97 张全用不上，切每个标签仍要现场取图（--perftest 首轮 31ms）。
                    try { IconCache.Get(kv.Key, kv.Value, 48); } catch { }
                };
            }
            if (!warmTimer.Enabled) warmTimer.Start();
        }
        catch (Exception ex) { Log.W("icon prewarm fail " + ex.Message); }
    }

    public static DragPayload PendingDrag;
    static NotifyIcon tray;
    static Icon trayIcon;

    [STAThread]
    static void Main()
    {
        string[] argv = Environment.GetCommandLineArgs();
        for (int i = 1; i < argv.Length; i++)
        {
            if (string.Equals(argv[i], "--migrate", StringComparison.OrdinalIgnoreCase))
            {
                RunMigrateHeadless();
                return;
            }
            if (string.Equals(argv[i], "--pingsave", StringComparison.OrdinalIgnoreCase))
            {
                PingSave();
                return;
            }
            if (string.Equals(argv[i], "--iconcmp", StringComparison.OrdinalIgnoreCase) && i + 2 < argv.Length)
            {
                RunIconCompare(argv[i + 1], argv, i + 2);
                return;
            }
            if (string.Equals(argv[i], "--icons", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
            {
                RunIconDump(argv[i + 1], argv, i + 2);
                return;
            }
            if (string.Equals(argv[i], "--startup", StringComparison.OrdinalIgnoreCase))
            {
                RunStartupFix();
                return;
            }
            if (string.Equals(argv[i], "--watchtest", StringComparison.OrdinalIgnoreCase))
            {
                watchTest = true;   // 继续正常启动，窗体起来后再自测
            }
            if (string.Equals(argv[i], "--win", StringComparison.OrdinalIgnoreCase))
            {
                // 用 Shell.Application.ToggleDesktop（任务栏按钮的真实通路）触发，
                // 而不是 WM_COMMAND 419 那条旧消息通路
                winTrigger = true;
                watchTest = true;
            }
            if (string.Equals(argv[i], "--nofg", StringComparison.OrdinalIgnoreCase))
            {
                watchNoForeground = true;   // 自测：还原窗口但不激活（复现“前台没变”）
                watchTest = true;
            }
            if (string.Equals(argv[i], "--probe", StringComparison.OrdinalIgnoreCase))
            {
                RunProbe();
                return;
            }
            if (string.Equals(argv[i], "--wins", StringComparison.OrdinalIgnoreCase))
            {
                // 排障"重启后位置偏移"：正常启动，1.5 秒后把每个盒子的配置坐标 / 托管坐标 /
                // 真实窗口矩形 / 客户区原点全打出来（wins-report.txt），然后退出。
                winsDump = true;
            }
            if (string.Equals(argv[i], "--geomlog", StringComparison.OrdinalIgnoreCase))
            {
                // 把每次启动的几何中间态（ctor / handleCreated / apply#N）全打进日志，
                // 查"位置又偏了"时用；平时只留 shown-end 一条。
                LayeredBox.GeomVerbose = true;
            }
            if (string.Equals(argv[i], "--shellmenu", StringComparison.OrdinalIgnoreCase))
            {
                shellMenuTest = true;   // 弹一次原生右键菜单并截图，然后退出
            }
            if (string.Equals(argv[i], "--prunetest", StringComparison.OrdinalIgnoreCase))
            {
                pruneTest = true;   // 验证"拖走后图标残留会不会自动清掉"
            }
            if (string.Equals(argv[i], "--perftest", StringComparison.OrdinalIgnoreCase))
            {
                perfTest = true;   // 切标签 / 右键菜单构建耗时体检
            }
            if (string.Equals(argv[i], "--boxmenu", StringComparison.OrdinalIgnoreCase))
            {
                boxMenuTest = true;     // 弹盒子菜单 + 展开"盒子颜色"子菜单并截图（核对色点渲染）
            }
            if (string.Equals(argv[i], "--droptest", StringComparison.OrdinalIgnoreCase))
            {
                dropTest = true;
            }
            if (string.Equals(argv[i], "--renametest", StringComparison.OrdinalIgnoreCase))
            {
                // 就地重命名自测：0=标题 1=标签 2=图标名（默认 1）
                renameTest = true;
                if (i + 1 < argv.Length)
                {
                    int m;
                    if (int.TryParse(argv[i + 1], out m)) renameMode = m;
                }
            }
            if (string.Equals(argv[i], "--styleform", StringComparison.OrdinalIgnoreCase))
            {
                styleFormTest = true;   // 打开「配色」对话框并截图（核对自绘控件）
                // 可选盒子序号：--styleform 1 = 第 2 个盒子（有自定义色的盒子才看得出色块对不对）
                if (i + 1 < argv.Length)
                {
                    int bi;
                    if (int.TryParse(argv[i + 1], out bi)) styleFormBox = bi;
                }
            }
            if (string.Equals(argv[i], "--stylecolors", StringComparison.OrdinalIgnoreCase))
            {
                // 配合 --styleform：先灌一组演示色（复刻用户截图里那四个值），
                // 这样"色块有没有跟着变"才有东西可看。不保存。
                styleColors = true;
            }
            if (string.Equals(argv[i], "--stylesave", StringComparison.OrdinalIgnoreCase))
            {
                styleSaveTest = true;   // 走一遍"改样式 → 保存 → 重新读配置"，验证 DStyle 能真的落到 json
            }
            if (string.Equals(argv[i], "--styletest", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
            {
                RunStyleTest(argv[i + 1]);   // 用 6 组极端 DStyle 批量出图，不改配置
                return;
            }
            if (string.Equals(argv[i], "--shellmenucmd", StringComparison.OrdinalIgnoreCase))
            {
                shellMenuCmdTest = true;   // 验证菜单命令真的能执行（copy 到剪贴板）
            }
            if (string.Equals(argv[i], "--dragtest", StringComparison.OrdinalIgnoreCase))
            {
                dragTest = true;        // 只核对拖拽载荷（FileDrop + 剪切标记），不开拖拽
            }
            if (string.Equals(argv[i], "--toggle", StringComparison.OrdinalIgnoreCase))
            {
                // 把"显示桌面"状态翻回去（测试收尾用）。两种通路依次试，每试一次探一次：
                //   ① 任务栏 WM_COMMAND 419（可重复，但要同步发；实测 PostMessage 不理会）
                //   ② COM Shell.Application.ToggleDesktop（每个进程只有第一次有效）
                // 翻过去就停，避免两种都生效变成"翻两次 = 没翻"。
                RunToggleBack();
                return;
            }
            if (string.Equals(argv[i], "--shot", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
            {
                RunShot(argv[i + 1]);
                return;
            }
            if (string.Equals(argv[i], "--render", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
            {
                // 离线出图：DeskBox.exe --render out.png [盒子序号|all] [--step 0..5] [--theme 0..4] [--plate 0.7]
                // 不显示桌面、不截屏，直接按当前配置把盒子界面 1:1 画成 PNG，用来核对字号/配色/底板。
                int boxIdx = 0;
                if (i + 2 < argv.Length && !argv[i + 2].StartsWith("--"))
                    boxIdx = string.Equals(argv[i + 2], "all", StringComparison.OrdinalIgnoreCase) ? -1 : int.Parse(argv[i + 2]);
                int op = -1, th = -1;
                for (int k = 1; k < argv.Length - 1; k++)
                {
                    if (string.Equals(argv[k], "--step", StringComparison.OrdinalIgnoreCase)) op = int.Parse(argv[k + 1]);
                    if (string.Equals(argv[k], "--theme", StringComparison.OrdinalIgnoreCase)) th = int.Parse(argv[k + 1]);
                    if (string.Equals(argv[k], "--plate", StringComparison.OrdinalIgnoreCase))
                        LayeredBox.PlateAlphaOverride = double.Parse(argv[k + 1], System.Globalization.CultureInfo.InvariantCulture);
                }
                RunRender(argv[i + 1], boxIdx, op, th);
                return;
            }
            if (string.Equals(argv[i], "--deskmenu", StringComparison.OrdinalIgnoreCase))
            {
                // 复现"桌面状态下右键，盒子持续闪烁"：显示桌面 → 右键桌面 → 数 topmost 翻转次数。
                // 用法：DeskBox.exe --deskmenu [秒数] [desktop|taskbar]，结果写 deskmenu-report.txt
                // 第二参数选右键目标：desktop=桌面图标层（默认），taskbar=任务栏空白处
                // （任务栏菜单可能是另一个壳进程托管的，得单独验）
                deskMenuTest = true;
                if (i + 1 < argv.Length)
                {
                    int n;
                    if (int.TryParse(argv[i + 1], out n) && n >= 2 && n <= 30) deskMenuSeconds = n;
                    if (i + 2 < argv.Length && string.Equals(argv[i + 2], "taskbar", StringComparison.OrdinalIgnoreCase))
                        deskMenuTray = true;
                }
            }
            if (string.Equals(argv[i], "--menutest", StringComparison.OrdinalIgnoreCase))
            {
                menuTest = true;   // 继续正常启动，窗体起来后弹一次盒子菜单并核对 z 序
            }
            if (string.Equals(argv[i], "--noauto", StringComparison.OrdinalIgnoreCase))
            {
                LayeredBox.DisableAuto = true;   // 自测：关掉守护纠错，纯观察
            }
            if (string.Equals(argv[i], "--child", StringComparison.OrdinalIgnoreCase))
            {
                LayeredBox.UseDesktopChild = true;    // 试验：桌面子窗口（本机分层绘制不合成，仅排障用）
            }
            if (string.Equals(argv[i], "--diag", StringComparison.OrdinalIgnoreCase))
            {
                // 纯记录模式：不改任何行为，逐 40ms 记录盒子真实处境 + 收到的每条相关消息。
                // 用法：DeskBox.exe --diag [秒数]，期间自己去点“显示桌面”。结果写 diag-report.txt
                diagOn = true;
                diagSeconds = 30;
                if (i + 1 < argv.Length)
                {
                    int n;
                    if (int.TryParse(argv[i + 1], out n) && n >= 5 && n <= 600) diagSeconds = n;
                }
                LayeredBox.DiagMode = true;
                LayeredBox.DisableAuto = true;   // 记录期间不干扰，纯观察
            }
        }
#if NET
        try
        {
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        }
        catch { }
#endif
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 兜底记录：任何未处理异常都写日志，避免“静默退出”
            AppDomain.CurrentDomain.UnhandledException += delegate(object s0, UnhandledExceptionEventArgs e0)
            {
                try { Log.W("UNHANDLED " + e0.ExceptionObject); } catch { }
            };
            Application.ThreadException += delegate(object s0, System.Threading.ThreadExceptionEventArgs e0)
            {
                try { Log.W("THREAD-EX " + e0.Exception); } catch { }
            };
            root = Store.Load();
            try { Directory.CreateDirectory(Store.ItemsRoot); } catch { }

            // 启动体检：有悬空条目就写 health-report.txt，绝不静默丢
            try { Store.HealthCheck(root, Path.Combine(Store.BaseDir, "health-report.txt")); }
            catch { }

            SetupTray();

            // 旧数据（指向桌面）→ 搬进 DeskBox 自己的文件夹
            try
            {
                if (Store.NeedsMigrate(root) && !root.NoAutoMigrate) RunMigrate(true);
            }
            catch (Exception ex)
            {
                Log.W("migrate fail " + ex.Message);
                MessageBox.Show("整理文件时出错（不影响程序运行）：\r\n" + ex.Message, "DeskBox");
            }

            foreach (DBox b in root.Boxes)
            {
                LayeredBox f = new LayeredBox(root, b);
                forms.Add(f);
                f.Show();
            }
            Store.Save(root);
            if (watchTest) BeginWatchTest();
            if (menuTest) BeginMenuTest();
            if (deskMenuTest) BeginDeskMenuTest();
            if (winsDump) BeginWinsDump();
            if (shellMenuTest) BeginShellMenuTest();
            if (shellMenuCmdTest) BeginShellMenuCmdTest();
            if (perfTest) BeginPerfTest();
            if (pruneTest) BeginPruneTest();
            if (boxMenuTest) BeginBoxMenuTest();
            if (renameTest) BeginRenameTest(renameMode);
            if (styleFormTest) BeginStyleFormTest();
            if (styleSaveTest) BeginStyleSaveTest();
            if (dragTest) BeginDragTest();
            if (dropTest) BeginDropTest();

            // 启动后台预热图标（切标签不再卡）。
            // 右键菜单已改成固定 8 项、不再调 shell 的 QueryContextMenu，
            // 所以不需要再预热 shell 扩展了。
            StartIconPrewarm(true);
            if (diagOn) StartDiag(diagSeconds);
            Application.Run();
        }
        catch (Exception ex)
        {
            Log.W("STARTUP CRASH " + ex.ToString());
            try { MessageBox.Show(ex.ToString(), "DeskBox 启动失败"); } catch { }
        }
    }

    // ---------- 托盘（右下角角标） ----------

    static void SetupTray()
    {
        try
        {
            trayIcon = BuildTrayIcon();
        }
        catch (Exception ex)
        {
            Log.W("icon fail " + ex.Message);
            trayIcon = SystemIcons.Application;
        }
        try
        {
            tray = new NotifyIcon();
            tray.Icon = trayIcon;
            tray.Text = "DeskBox · 桌面分类盒子";
            tray.Visible = true;

            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("显示所有盒子", null, delegate { ShowAll(); });
            m.Items.Add("隐藏所有盒子", null, delegate { HideAll(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("新建盒子", null, delegate { NewBox(300, 300); });

            ToolStripMenuItem auto = new ToolStripMenuItem("开机自动启动");
            auto.Click += delegate
            {
                ToggleStartup();
                auto.Checked = StartupEnabled;
                auto.CheckState = StartupEnabled ? CheckState.Checked : CheckState.Unchecked;
            };
            m.Items.Add(auto);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("打开整理文件夹", null, delegate {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Store.ItemsRoot);
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                catch { }
            });
            m.Items.Add("诊断写入权限（拖不进去时用）", null, delegate { Diagnose(); });
            m.Items.Add("退出", null, delegate { Quit(); });

            m.Opening += delegate
            {
                bool on = StartupEnabled;
                auto.Checked = on;
                auto.CheckState = on ? CheckState.Checked : CheckState.Unchecked;
            };

            tray.ContextMenuStrip = m;
            tray.DoubleClick += delegate { ToggleAll(); };
        }
        catch (Exception ex)
        {
            Log.W("tray fail " + ex.Message);
        }
        // 兜底：确保可见
        try { if (tray != null) tray.Visible = true; } catch { }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    static Icon BuildTrayIcon()
    {
        Bitmap b = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (GraphicsPath p = new GraphicsPath())
            {
                int r = 7;
                p.AddArc(1, 1, r * 2, r * 2, 180, 90);
                p.AddArc(31 - r * 2, 1, r * 2, r * 2, 270, 90);
                p.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
                p.AddArc(1, 31 - r * 2, r * 2, r * 2, 90, 90);
                p.CloseFigure();
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(255, 16, 20, 26))) g.FillPath(fill, p);
                using (Pen pen = new Pen(Color.FromArgb(255, 0, 235, 150), 1.5f)) g.DrawPath(pen, p);
                using (SolidBrush sq = new SolidBrush(Color.FromArgb(255, 0, 235, 150)))
                {
                    g.FillRectangle(sq, 8, 8, 6, 6);
                    g.FillRectangle(sq, 18, 8, 6, 6);
                    g.FillRectangle(sq, 8, 18, 6, 6);
                    g.FillRectangle(sq, 18, 18, 6, 6);
                }
            }
        }
        // 优先放在程序自己的目录（权限最稳），再退到系统 TEMP
        string[] candidates = new string[] {
            Path.Combine(Store.BaseDir, "deskbox-tray.ico"),
            Path.Combine(Path.GetTempPath(), "DeskBox_tray.ico")
        };
        Icon cloned = null;
        try
        {
            IntPtr h = b.GetHicon();
            cloned = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
        }
        catch (Exception ex) { Log.W("ico clone fail " + ex.Message); }

        string okPath = null;
        foreach (string p in candidates)
        {
            try { WriteIco(b, p); okPath = p; break; }
            catch (Exception ex) { Log.W("ico write fail " + p + " :: " + ex.Message); }
        }
        b.Dispose();

        if (okPath != null)
        {
            try { return new Icon(okPath, 32, 32); }
            catch (Exception ex) { Log.W("ico load fail " + okPath + " :: " + ex.Message); }
        }
        if (cloned != null) return cloned;
        return SystemIcons.Application;
    }

    static void WriteIco(Bitmap bmp, string path)
    {
        int w = bmp.Width, h = bmp.Height;
        int xorSize = w * h * 4;
        int andStride = ((w + 31) / 32) * 4;
        int andSize = andStride * h;
        int imgSize = 40 + xorSize + andSize;
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)1);
            bw.Write((byte)(w >= 256 ? 0 : w));
            bw.Write((byte)(h >= 256 ? 0 : h));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write((uint)imgSize);
            bw.Write((uint)22);

            bw.Write((uint)40);
            bw.Write((int)w);
            bw.Write((int)(h * 2));
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write((uint)0);
            bw.Write((uint)xorSize);
            bw.Write((int)0);
            bw.Write((int)0);
            bw.Write((uint)0);
            bw.Write((uint)0);

            for (int y = h - 1; y >= 0; y--)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    bw.Write(c.B);
                    bw.Write(c.G);
                    bw.Write(c.R);
                    bw.Write(c.A);
                }
            }
            bw.Write(new byte[andSize]);
            bw.Flush();
        }
    }

    // ---------- 开机启动 ----------

    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "DeskBox";

    public static string StartupLnk
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DeskBox.lnk"); }
    }

    public static bool StartupEnabled
    {
        get
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (k != null && k.GetValue(RunValueName) != null) return true;
                }
            }
            catch { }
            try { return File.Exists(StartupLnk); } catch { return false; }
        }
    }

    public static void SetStartup(bool on)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (k != null)
                {
                    if (on) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunValueName, false);
                }
            }
        }
        catch (Exception ex) { Log.W("startup registry fail " + ex.Message); }

        // 清理旧的启动文件夹快捷方式，避免重复启动
        try { if (File.Exists(StartupLnk)) File.Delete(StartupLnk); } catch { }
    }

    public static void ToggleStartup()
    {
        SetStartup(!StartupEnabled);
    }

    // 维护用：程序换目录后，把“开机启动”注册表指向当前 exe
    public static void RunStartupFix()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        try
        {
            sb.AppendLine("BaseDir=" + Store.BaseDir);
            sb.AppendLine("exe=" + Application.ExecutablePath);
            string before = null;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (k != null) before = k.GetValue(RunValueName) as string;
            }
            sb.AppendLine("before=" + (before == null ? "(none)" : before));
            sb.AppendLine("lnk=" + (File.Exists(StartupLnk) ? StartupLnk : "(none)"));
            if (before != null)
            {
                SetStartup(true);
                string after = null;
                using (RegistryKey k2 = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (k2 != null) after = k2.GetValue(RunValueName) as string;
                }
                sb.AppendLine("after=" + (after == null ? "(none)" : after));
            }
            else
            {
                sb.AppendLine("skip: 开机启动未开启，注册表未改动");
            }
        }
        catch (Exception ex) { sb.AppendLine("FAIL " + ex); }
        try { File.WriteAllText(Path.Combine(Store.BaseDir, "startup-report.txt"), sb.ToString()); } catch { }
    }

    // ---------- 窗口集合 ----------

    public static void ShowAll()
    {
        foreach (LayeredBox f in forms)
        {
            try
            {
                if (!f.Visible) f.Show();
                f.Activate();
            }
            catch { }
        }
    }

    public static void HideAll()
    {
        foreach (LayeredBox f in forms) { try { f.HideByUser(); } catch { } }
    }

    static void ToggleAll()
    {
        bool any = false;
        foreach (LayeredBox f in forms) { if (f.Visible) { any = true; break; } }
        if (any) HideAll(); else ShowAll();
    }

    public static void NewBox(int x, int y)
    {
        DBox b = Store.MakeBox(x, y, "新盒子");
        b.Accent = root.Boxes.Count;
        b.Folder = Store.UniqueBoxFolder(root, b, Store.SafeName(b.Title));
        b.Tabs.Add(Store.MakeTab("全部"));
        root.Boxes.Add(b);
        LayeredBox f = new LayeredBox(root, b);
        forms.Add(f);
        f.Show();
        Store.Save(root);
    }

    public static void DeleteBox(LayeredBox f)
    {
        if (MessageBox.Show("删除盒子“" + f.Data.Title + "”？里面的真实文件会被放回桌面。", "DeskBox", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        List<string> fails = new List<string>();
        string boxDir = Store.BoxDir(f.Data);
        foreach (DTab t in f.Data.Tabs)
        {
            foreach (DItem it in t.Items)
            {
                string err;
                if (Store.MoveToDesktop(it.Path, out err) == null)
                    fails.Add(Store.LabelOf(it.Path) + " —— " + err);
            }
        }
        try
        {
            if (Directory.Exists(boxDir) && Directory.GetFileSystemEntries(boxDir, "*", SearchOption.AllDirectories).Length == 0)
                Directory.Delete(boxDir, true);
            else if (Directory.Exists(boxDir))
                foreach (string d in Directory.GetDirectories(boxDir))
                    if (Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d);
        }
        catch { }
        root.Boxes.Remove(f.Data);
        forms.Remove(f);
        f.AllowHide = true;
        f.Hide();
        f.Dispose();
        Store.Save(root);
        if (fails.Count > 0)
            MessageBox.Show("部分文件没能放回桌面：\r\n\r\n" + string.Join("\r\n", fails.ToArray()), "DeskBox");
    }

    // 无界面迁移（维护用）：DeskBox.exe --migrate，结果写到 migrate-report.txt
    static void RunMigrateHeadless()
    {
        string rep;
        try
        {
            root = Store.Load();
            try { Directory.CreateDirectory(Store.ItemsRoot); } catch { }
            string backup = Store.BackupSources(root);
            Store.MigrateResult res = Store.Migrate(root);
            Store.Save(root);
            rep = "moved=" + res.Moved + " copied=" + res.Copied + " shared=" + res.Shared + " already=" + res.Already + " kept=" + res.Kept + "\r\n";
            if (!string.IsNullOrEmpty(backup)) rep += "backup=" + backup + "\r\n";
            foreach (string n in res.KeptNames) rep += "kept: " + n + "\r\n";
            foreach (DBox b in root.Boxes)
            {
                rep += "box[" + b.Folder + "]\r\n";
                foreach (DTab t in b.Tabs)
                {
                    rep += "  tab[" + t.Folder + "] items=" + t.Items.Count + "\r\n";
                    foreach (DItem it in t.Items) rep += "    " + it.Path + "\r\n";
                }
            }
        }
        catch (Exception ex)
        {
            rep = "FAIL " + ex;
        }
        try { File.WriteAllText(Path.Combine(Store.BaseDir, "migrate-report.txt"), rep); } catch { }
    }

    // 调试用：DeskBox.exe --icons 输出.png <文件或文件夹...>
    // 每个条目画三份：44px 实尺寸、3 倍放大（看清晰度）、原图尺寸
    static void RunIconDump(string outPath, string[] argv, int start)
    {
        List<string> files = new List<string>();
        for (int i = start; i < argv.Length; i++) files.Add(argv[i]);
        int cellW = 152, cellH = 210, pad = 10;
        int cols = Math.Max(1, Math.Min(4, files.Count));
        int rows = Math.Max(1, (files.Count + cols - 1) / cols);
        using (Bitmap b = new Bitmap(cols * cellW + pad * 2, rows * cellH + pad))
        {
          using (Graphics g = Graphics.FromImage(b))
          {
            g.Clear(Color.FromArgb(255, 34, 40, 48));
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            for (int i = 0; i < files.Count; i++)
            {
                int cx = pad + (i % cols) * cellW;
                int cy = pad + (i / cols) * cellH;
                Image icon = IconCache.Get(files[i], Path.GetFileName(files[i]), 44);
                if (icon != null)
                {
                    g.DrawImageUnscaled(icon, cx, cy);                 // 实际显示尺寸
                    g.DrawImage(icon, cx, cy + 52, 44 * 3, 44 * 3);    // 3 倍放大看糊不糊
                }
                using (Font f = new Font("Microsoft YaHei", 7.5f))
                using (SolidBrush br = new SolidBrush(Color.White))
                    g.DrawString(Path.GetFileName(files[i]).Length > 12
                        ? Path.GetFileName(files[i]).Substring(0, 12) : Path.GetFileName(files[i]),
                        f, br, cx, cy + 190);
            }
          }
          b.Save(outPath, ImageFormat.Png);   // 等 Graphics 释放后再存
        }
    }

    // 维护开关：--pingsave  只回答一个问题：这个进程能不能在位图目录里存 PNG。
    // （实测：从 bin\Release 里跑进程会被宿主限制写 G://DeskBox，所以测试必须用部署到
    //   DeskBox 目录的 exe；这条就是给这种"莫名其妙存不进图"的情形准备的探针。）
    static void PingSave()
    {
        try
        {
            using (Bitmap bb = new Bitmap(32, 32, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(bb)) g.Clear(Color.Red);
                string f = Path.Combine(Store.BaseDir, "_ping.png");
                bb.Save(f, ImageFormat.Png);
                Log.W("pingsave OK " + f);
                try { File.Delete(f); } catch { }
            }
        }
        catch (Exception ex) { Log.W("pingsave FAIL " + ex.GetType().Name + " " + ex.Message); }
    }

    // ---------- 维护开关：--iconcmp ----------
    // 同一个文件用四种取图策略各画一行（1:1 + 4 倍放大），用来判断"哪种最接近资源管理器"：
    //   A 直接向 shell 要 48（有原生 48 时就是像素级 1:1）
    //   B 现在线上用的：要 256，再 Fit 到 44
    //   C 要 256，Fit 到 48
    //   D 直接向 shell 要 44
    static string Dsc(Image im) { return im == null ? "null" : im.Width + "x" + im.Height; }
    static void RunIconCompare(string outPath, string[] argv, int start)
    {
        List<string> files = new List<string>();
        for (int i = start; i < argv.Length; i++) files.Add(argv[i]);
        string[] names = { "A shell@48", "B 256→Fit44", "C 256→Fit48", "D shell@44" };
        int rows = 4, cell = 120, pad = 8, labelH = 16;
        using (Bitmap b = new Bitmap(files.Count * (cell * 2 + 70) + pad,
                                     rows * (cell + labelH) + pad * 2))
        {
          using (Graphics g = Graphics.FromImage(b))
          {
            g.Clear(Color.FromArgb(255, 236, 238, 241));   // 浅底，和资源管理器一致
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.None;
            for (int i = 0; i < files.Count; i++)
            {
                // .lnk 要先解析到目标（exe/ico）再取图，否则 PrivateExtractIcons 只给通用快捷方式图标
                string real = files[i];
                try
                {
                    string t = IconCache.Resolve(files[i]);
                    if (t != null && File.Exists(t)) real = t;
                }
                catch { }
                string why = "";
                Image big = null;
                try { big = IconCache.TestPrivate(real, 256); }
                catch (Exception ex) { why += " big:" + ex.GetType().Name + ex.Message; }
                Image[] variants = new Image[4];
                for (int k = 0; k < 4; k++)
                {
                    try
                    {
                        if (k == 0) variants[0] = IconCache.TestPrivate(real, 48);
                        if (k == 1 && big != null) variants[1] = IconCache.TestFit(big, 44);
                        if (k == 2 && big != null) variants[2] = IconCache.TestFit(big, 48);
                        if (k == 3) variants[3] = IconCache.TestPrivate(real, 44);
                    }
                    catch (Exception ex) { why += " v" + k + ":" + ex.GetType().Name + ex.Message; }
                }
                Log.W("iconcmp " + i + " real=" + real + " big=" + (big != null ? big.Width + "px" : "null")
                      + " a=" + Dsc(variants[0]) + " b=" + Dsc(variants[1]) + " c=" + Dsc(variants[2]) + " d=" + Dsc(variants[3]) + why);
                for (int r = 0; r < rows; r++)
                {
                    int x = pad + i * (cell * 2 + 70);
                    int y = pad + r * (cell + labelH);
                    Image v = variants[r];
                    if (v == null) continue;
                    g.DrawImageUnscaled(v, x, y);                       // 1:1
                    g.DrawImage(v, x + 60, y, v.Width * 2, v.Height * 2); // 2 倍
                    using (Font f = new Font("Microsoft YaHei", 7f))
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(90, 96, 104)))
                        g.DrawString(names[r] + " " + v.Width + "px", f, br, x, y + cell - 4);
                }
                if (big != null) big.Dispose();
            }
          }
          // 必须等 Graphics 释放后再 Save
          b.Save(outPath, ImageFormat.Png);
          Log.W("iconcmp -> " + outPath + " n=" + files.Count);
        }
    }

    // ---------- 诊断 ----------

    // 维护开关：--shot  只读：整屏截图（缩到 50%，方便直接看/发给别人核对）
    static void RunShot(string path)
    {
        try
        {
            Rectangle b = SystemInformation.VirtualScreen;
            using (Bitmap full = new Bitmap(b.Width, b.Height))
            {
                using (Graphics g = Graphics.FromImage(full)) g.CopyFromScreen(b.Left, b.Top, 0, 0, full.Size);
                int w = Math.Max(1, full.Width / 2), h = Math.Max(1, full.Height / 2);
                using (Bitmap small = new Bitmap(w, h))
                using (Graphics g2 = Graphics.FromImage(small))
                {
                    g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g2.DrawImage(full, 0, 0, w, h);
                    small.Save(path, ImageFormat.Png);
                }
                // 顺带把每个盒子所在的屏幕区域 1:1 切出来（不缩放）：
                // 判断字号/图标清不清晰，只能看真机原尺寸，缩略图看不出来。
                try
                {
                    DRoot r = Store.Load();
                    for (int i = 0; i < r.Boxes.Count; i++)
                    {
                        DBox d = r.Boxes[i];
                        Rectangle br = new Rectangle(d.X - b.Left, d.Y - b.Top, d.W, d.H);
                        br.Intersect(new Rectangle(0, 0, full.Width, full.Height));
                        if (br.Width < 2 || br.Height < 2) continue;
                        using (Bitmap crop = full.Clone(br, full.PixelFormat))
                            crop.Save(Path.Combine(Path.GetDirectoryName(path),
                                Path.GetFileNameWithoutExtension(path) + "-box" + i + ".png"), ImageFormat.Png);
                    }
                }
                catch (Exception ex) { Log.W("shot crop fail " + ex.Message); }
            }
        }
        catch (Exception ex) { Log.W("shot fail " + ex.Message); }
    }

    // ---------- 美化调试开关：--render ----------
    // 直接调用 LayeredBox.Compose() 把界面画成 PNG（1:1，不缩放）。
    // 好处：不用显示桌面、不用截屏、不受壁纸和窗口遮挡干扰，改字号/配色后立刻能看效果。
    static void RunRender(string outPath, int boxIdx, int stepOverride, int themeOverride)
    {
        DRoot root = Store.Load();
        List<DBox> boxes = root.Boxes;
        if (boxes.Count == 0) return;
        string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        for (int i = 0; i < boxes.Count; i++)
        {
            if (boxIdx >= 0 && i != boxIdx) continue;
            DBox d = boxes[i];
            if (stepOverride >= 0)
            {
                d.OpacityStep = stepOverride;
                d.Opacity = 20 + stepOverride * 16;
            }
            if (themeOverride >= 0) d.ThemeId = themeOverride;
            LayeredBox b = new LayeredBox(root, d);
            b.Width = d.W;
            b.Height = d.H;
            using (Bitmap bmp = b.Compose())
            {
                string f = boxIdx >= 0 ? outPath : Path.Combine(dir ?? ".",
                    Path.GetFileNameWithoutExtension(outPath) + "-box" + i + ".png");
                bmp.Save(f, ImageFormat.Png);
                PreviewOnBusy(bmp, Path.Combine(Path.GetDirectoryName(f),
                    Path.GetFileNameWithoutExtension(f) + "-onbg.png"));
                Log.W("render " + f + " " + bmp.Width + "x" + bmp.Height);
            }
            b.Dispose();
        }
    }

    // 把渲染结果叠到一张“花壁纸”上，再切一块 2x 特写。
    // 底板上是不透明率有限的玻璃，只看纯色背景永远判断不出对比度够不够——
    // 必须压在有高频细节的底上，才能看出文件名到底读不读得清。
    static void PreviewOnBusy(Bitmap src, string path)
    {
        int w = src.Width, h = src.Height;
        using (Bitmap bg = new Bitmap(w, h))
        {
            using (Graphics g = Graphics.FromImage(bg))
            {
                using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                    Color.FromArgb(255, 118, 178, 112), Color.FromArgb(255, 238, 232, 196), 35f))
                    g.FillRectangle(lg, 0, 0, w, h);
                // 固定种子的随机色块：每次出图一致，方便前后对比
                Random rnd = new Random(7);
                for (int k = 0; k < 70; k++)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(190, rnd.Next(0, 256), rnd.Next(0, 256), rnd.Next(0, 256))))
                        g.FillEllipse(b, rnd.Next(-40, w), rnd.Next(-40, h), rnd.Next(20, 130), rnd.Next(20, 130));
                }
                g.DrawImage(src, 0, 0);
            }
            bg.Save(path, ImageFormat.Png);

            // 2x 最近邻放大，专门看字形有没有半像素发虚
            int zw = Math.Min(w, 346), zh = Math.Min(h, 160);
            using (Bitmap zoom = new Bitmap(zw * 2, zh * 2))
            {
                using (Graphics g = Graphics.FromImage(zoom))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(bg, new Rectangle(0, 0, zw * 2, zh * 2), new Rectangle(0, 0, zw, zh), GraphicsUnit.Pixel);
                }
                zoom.Save(Path.Combine(Path.GetDirectoryName(path),
                    Path.GetFileNameWithoutExtension(path).Replace("-onbg", "") + "-zoom.png"), ImageFormat.Png);
            }
        }
    }

    // ---------- 维护开关：--menutest ----------
    // 验证右键菜单不会被盒子自己盖住：弹出盒子菜单，然后按顶层 z 序数一下菜单和盒子分别在
    // 第几位（0 = 最前面）。菜单的序号比盒子小才算过。结果写 menutest-report.txt。
    static bool menuTest;
    static void BeginMenuTest()
    {
        Timer boot = new Timer();
        boot.Interval = 800;
        boot.Tick += delegate
        {
            boot.Stop(); boot.Dispose();
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                LayeredBox box = forms.Count > 0 ? forms[0] : null;
                if (box == null) { sb.AppendLine("没有盒子"); }
                else
                {
                    // 完全复现用户的操作：真的"显示桌面" → 等一会儿 → 往盒子标题栏发一次真实的
                    // 鼠标右键消息（点标题空白处弹盒子菜单；不直接调 ShowBoxMenu，那样绕过了
                    // OnMouseDown 和 WinForms 自己那一套菜单创建流程）。
                    if (winTrigger) { ToggleDesktopEx(); sb.AppendLine("[已触发] 显示桌面"); }
                    else box.TestRaise();
                    sb.AppendLine("右键前 box topmost=" + (box.TestTopmost ? 1 : 0));
                    int wait = 0, sample = 0, worst = int.MaxValue;
                    Timer step = new Timer();
                    step.Interval = 120;
                    step.Tick += delegate
                    {
                        // 阶段一：等桌面动画/壳反应结束（用户也是停一下才右键的）
                        if (wait < 12) { wait++; return; }
                        // 阶段二：发一次真实右键
                        if (wait == 12)
                        {
                            wait++;
                            int x = 26, y = 10;   // 标题栏空白处 → 盒子菜单
                            IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
                            SendMessage(box.Handle, WM_RBUTTONDOWN, (IntPtr)2, lp);
                            SendMessage(box.Handle, WM_RBUTTONUP, IntPtr.Zero, lp);
                            sb.AppendLine("[已右键] 菜单 hwnd=" + LayeredBox.LastMenuHwnd.ToString("X"));
                            return;
                        }
                        // 阶段三：连续采样。中间做两件事：
                        //  a) sample==5 时强行把盒子抬成 topmost —— 模拟真实世界里"菜单打开后
                        //     盒子又被某个事件反超"的那个瞬间，看守护能不能把菜单救回来；
                        //  b) sample==3/8/15 时触发 Render()（用户右键时鼠标在动，hover 会重绘）。
                        if (sample == 5)
                        {
                            box.TestForceMenuBelowBox();
                            sb.AppendLine("  [强行] 把菜单插到盒子正下方，模拟故障态");
                        }
                        if (sample == 3 || sample == 8 || sample == 15) box.TestRender();
                        int zm = LayeredBox.ZIndex(LayeredBox.LastMenuHwnd);
                        int zb = LayeredBox.ZIndex(box.Handle);
                        if (zm >= 0) worst = Math.Min(worst, zm - zb);
                        sample++;
                        if ((sample & 3) == 0)
                            sb.AppendLine("  t+" + (sample * 120) + "ms 菜单=" + zm + " 盒子=" + zb +
                                          " boxTopmost=" + (box.TestTopmost ? 1 : 0));
                        if (sample >= 40)
                        {
                            step.Stop(); step.Dispose();
                            int zm2 = LayeredBox.ZIndex(LayeredBox.LastMenuHwnd);
                            int zb2 = LayeredBox.ZIndex(box.Handle);
                            sb.AppendLine("最终: 菜单=" + zm2 + " 盒子=" + zb2 + "（0 最前）");
                            sb.AppendLine(worst > 0 ? "结果: 失败，菜单被盒子盖住（最坏被压 " + worst + " 位）"
                                                    : "结果: 通过，菜单始终在盒子上面（最小差 " + worst + "）");
                            try { GrabScreen(Path.Combine(Store.BaseDir, "menutest-shot.png")); } catch { }
                            try { File.WriteAllText(Path.Combine(Store.BaseDir, "menutest-report.txt"), sb.ToString(), System.Text.Encoding.UTF8); } catch { }
                            Log.W("menutest 菜单=" + zm2 + " 盒子=" + zb2);
                            ToggleDesktopBack("menutest");   // 别把用户的桌面留在"显示桌面"状态
                            Quit();
                        }
                    };
                    step.Start();
                    return;
                }
            }
            catch (Exception ex) { sb.AppendLine("出错: " + ex.Message); }
            try { File.WriteAllText(Path.Combine(Store.BaseDir, "menutest-report.txt"), sb.ToString(), System.Text.Encoding.UTF8); } catch { }
            Quit();
        };
        boot.Start();
    }

    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_RBUTTONDOWN = 0x0204;
    const int WM_RBUTTONUP = 0x0205;

    // ---------- 维护开关：--wins ----------
    // 查"重启后盒子位置偏移"：正常启动 → 等布局稳定 → 把每个盒子的
    // 配置坐标 / 托管坐标 / 真实窗口矩形 / 客户区原点写进 wins-report.txt → 退出。
    static bool winsDump;
    static void BeginWinsDump()
    {
        Timer t = new Timer();
        t.Interval = 1500;
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("wins " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                sb.AppendLine("虚拟屏幕=" + SystemInformation.VirtualScreen);
            }
            catch { }
            for (int i = 0; i < forms.Count; i++)
                sb.AppendLine("box" + i + " " + forms[i].Text + "  " + forms[i].GeomDump());
            try
            {
                File.WriteAllText(Path.Combine(Store.BaseDir, "wins-report.txt"), sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }
            Log.W("wins dump\n" + sb);
            Quit();
        };
        t.Start();
    }

    // ---------- 维护开关：--shellmenu ----------
    // 对盒子里第一个真实存在的条目弹一次原生 Windows 菜单，
    // 弹出来 1 秒后截图（shellmenu-shot.png）再按 ESC 关掉，用来看：
    //   1) 菜单是不是真的跟资源管理器一样（打开方式/发送到/属性…）
    //   2) 菜单有没有被钉在桌面的盒子盖住
    static bool shellMenuTest;
    static void BeginShellMenuTest()
    {
        Timer boot = new Timer();
        boot.Interval = 4500;   // 等 2 秒那次 shell 菜单预热完成，测到的才是"热态"
        boot.Tick += delegate
        {
            boot.Stop(); boot.Dispose();
            if (forms.Count == 0) { Quit(); return; }
            LayeredBox box = forms[0];

            // 后台线程负责"截图 + 关菜单"：TrackPopupMenuEx 会占住 UI 线程，
            // 不能指望 UI 定时器一定被泵到，后台线程最稳。
            System.Threading.Thread th = new System.Threading.Thread(delegate ()
            {
                try
                {
                    // 连续采样：菜单和盒子重叠的地方，屏幕上到底画的是谁
                    for (int k = 0; k < 8; k++)
                    {
                        System.Threading.Thread.Sleep(220);
                        IntPtr menu = ShellMenu.FindMenuWindow();
                        if (menu == IntPtr.Zero) { Log.W("shellmenu 采样" + k + " 菜单还没出现"); continue; }
                        int mz = LayeredBox.ZIndex(menu), bz = LayeredBox.ZIndex(box.Handle);
                        int ml, mt, mrr, mb;
                        ShellMenu.GetMenuRect(menu, out ml, out mt, out mrr, out mb);
                        Rectangle inter = Rectangle.Intersect(
                            new Rectangle(ml, mt, mrr - ml, mb - mt), box.Bounds);
                        string px = "无重叠";
                        if (inter.Width > 4 && inter.Height > 4)
                        {
                            int sx = inter.X + inter.Width / 2, sy = inter.Y + inter.Height / 2;
                            Color c = PixelAt(sx, sy);
                            px = "重叠区@" + sx + "," + sy + "=" + c.R + "/" + c.G + "/" + c.B +
                                 (c.R > 150 ? "（亮=菜单在上面）" : "（暗=盒子在上面）");
                        }
                        Log.W("shellmenu 采样" + k + " menuZ=" + mz + " boxZ=" + bz + " rect=" +
                              ml + "," + mt + "," + mrr + "," + mb + " " + px);
                    }
                    GrabScreen("shellmenu-shot.png");
                    IntPtr m2 = ShellMenu.FindMenuWindow();
                    try
                    {
                        if (m2 != IntPtr.Zero)
                        {
                            int l, t, r, b;
                            ShellMenu.GetMenuRect(m2, out l, out t, out r, out b);
                            int w = r - l, h = b - t;
                            if (w > 8 && h > 8)
                            {
                                using (Bitmap crop = new Bitmap(w, h))
                                {
                                    using (Graphics g = Graphics.FromImage(crop))
                                        g.CopyFromScreen(l, t, 0, 0, new Size(w, h));
                                    using (Bitmap big = new Bitmap(w * 3, h * 3))
                                    using (Graphics g2 = Graphics.FromImage(big))
                                    {
                                        g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                                        g2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                                        g2.DrawImage(crop, 0, 0, w * 3, h * 3);
                                        big.Save("shellmenu-zoom.png", ImageFormat.Png);
                                    }
                                }
                                Log.W("shellmenu 放大图已存 " + w + "x" + h);
                            }
                        }
                    }
                    catch (Exception ex) { Log.W("shellmenu zoom fail " + ex.Message); }
                    Log.W("shellmenu 截图完成 菜单窗口=" + m2);
                    if (m2 != IntPtr.Zero)
                    {
                        // 模拟"选中『复制』并回车"：菜单自己处理键盘消息，
                        // 这样才能验证"菜单 → 返回值 → 按动词执行"整条链。
                        for (int k = 0; k < 5; k++)
                        {
                            ShellMenu.PostToMenu(m2, 0x0100, (IntPtr)0x28, IntPtr.Zero);   // VK_DOWN
                            System.Threading.Thread.Sleep(60);
                        }
                        ShellMenu.PostToMenu(m2, 0x0100, (IntPtr)0x0D, IntPtr.Zero);       // VK_RETURN
                        System.Threading.Thread.Sleep(900);
                        m2 = ShellMenu.FindMenuWindow();
                        if (m2 != IntPtr.Zero)
                        {
                            ShellMenu.PostToMenu(m2, 0x0100, (IntPtr)0x1B, IntPtr.Zero);   // ESC
                            System.Threading.Thread.Sleep(700);
                            m2 = ShellMenu.FindMenuWindow();
                            if (m2 != IntPtr.Zero)
                                ShellMenu.PostToMenu(m2, 0x001F, IntPtr.Zero, IntPtr.Zero); // WM_CANCELMODE
                            System.Threading.Thread.Sleep(600);
                        }
                    }
                }
                catch (Exception ex) { Log.W("shellmenu test " + ex.Message); }
                try { box.BeginInvoke((MethodInvoker)delegate { Quit(); }); } catch { }
            });
            th.IsBackground = true;
            th.Start();

            box.TestShowFirstTileMenu();   // 阻塞到菜单关闭
        };
        boot.Start();
    }

    // ---------- 维护开关：--prunetest ----------
    // 验证"拖出去之后盒子里还会留图标"这个坑：造一个条目 → 删掉文件 → 走真实校验流程
    // → 1.5 秒后看条目是不是自动消失了（同时核对 deskbox.json）。
    static bool pruneTest;

    // ---------- 维护开关：--boxmenu ----------
    // 弹一次盒子菜单，再展开"盒子颜色"子菜单，把菜单+盒子一起截图。
    // 目的：确认色点（Swatch 位图）真的画出来了、没有被 ImageScalingSize 挤掉，
    // 以及子菜单不会被盒子自己盖住（这个坑之前只在 tile 菜单上验证过）。
    static bool boxMenuTest;
    static bool dropTest;
    static bool renameTest;
    static int renameMode = 1;

    // ---------- 维护开关：--styleform ----------
    // 打开「配色」对话框，等它画完后截一张图再关掉退出。用来核对自绘控件有没有错位/漏画。
    static bool styleFormTest;
    static int styleFormBox;
    static bool styleColors;
    static void BeginStyleFormTest()
    {
        Timer t = new Timer();
        t.Interval = 2000;
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            int bi = styleFormBox < 0 ? 0 : (styleFormBox >= forms.Count ? forms.Count - 1 : styleFormBox);
            LayeredBox box = forms.Count > 0 ? forms[bi] : null;
            if (box == null) { Log.W("styleform 没有盒子"); Quit(); return; }
            Log.W("styleform 盒子#" + bi + " " + box.Data.Title +
                  " bg=" + Store.ToHex(box.BgCol) + "/a" + box.BgCol.A + " edge=" + Store.ToHex(box.EdgeBase) +
                  "/a" + box.EdgeBase.A + " sep=" + Store.ToHex(box.SepBase) + "/a" + box.SepBase.A);
            StyleForm f = null;
            try
            {
                f = box.OpenStyleForm();
                if (styleColors)
                {
                    DStyle d = new DStyle();
                    d.BgOn = true; d.BgColor = "#F2F5F8"; d.BgAlpha = 68;
                    d.EdgeOn = true; d.EdgeColor = "#DE35C6"; d.EdgeAlpha = 30; d.EdgeWidth = 2;
                    d.TextOn = true; d.TextColor = "#C95C7C"; d.TextAlpha = 100;
                    d.SepOn = true; d.SepColor = "#DB7676"; d.SepAlpha = 79;
                    d.Radius = 15;
                    f.TestSet(d);
                    Log.W("styleform 演示色 bg=" + Store.ToHex(box.BgCol) + "/a" + box.BgCol.A +
                          " edge=" + Store.ToHex(box.EdgeBase) + "/a" + box.EdgeBase.A +
                          " text=" + Store.ToHex(box.TextBase) + "/a" + box.TextBase.A +
                          " sep=" + Store.ToHex(box.SepBase) + "/a" + box.SepBase.A);
                }
            }
            catch (Exception ex) { Log.W("styleform 打开失败 " + ex.Message); Quit(); return; }
            ShotStyleForm(f, 0);
        };
        t.Start();
    }

    // 三层各截一张，确认"边缘线"层高亮、粗细按钮激活态都对
    static void ShotStyleForm(StyleForm f, int layer)
    {
        Timer s = new Timer();
        s.Interval = 700;
        s.Tick += delegate
        {
            s.Stop(); s.Dispose();
            if (f == null || f.IsDisposed) { Quit(); return; }
            try
            {
                if (layer == 4)
                {
                    // 真的往对话框发一次左键，点调色板第 16 格：验证"命中 → 应用 → 重画"整条链。
                    // 消息是 Post 的，**必须等下一拍再截图**，否则拍到的是上一帧。
                    ClickPalette(f, 16);
                    ShotStyleForm(f, 5);
                    return;
                }

                f.SetLayerForTest(layer == 5 ? 3 : layer);

                Rectangle b = f.Bounds;
                using (Bitmap bmp = new Bitmap(b.Width, b.Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
                    bmp.Save(Path.Combine(Store.BaseDir,
                        "styleform-shot" + (layer == 0 ? "" : (layer == 5 ? "-pal" : "-" + layer)) + ".png"),
                        ImageFormat.Png);
                }
                Log.W("styleform 截图 layer=" + layer + " " + b + " size=" + f.ClientSize);
            }
            catch (Exception ex) { Log.W("styleform 截图失败 " + ex.Message); }

            if (layer < 3) { ShotStyleForm(f, layer + 1); return; }
            if (layer == 3 && styleColors) { ShotStyleForm(f, 4); return; }

            // 收尾：灌过演示色的必须走「取消」把它撤掉，
            // 否则 Quit() 里的 Store.Save 会把演示色当成用户确认的改动写进 deskbox.json。
            try { if (styleColors) f.TestCancel(); else f.Close(); } catch { }
            Timer q = new Timer();
            q.Interval = 400;
            q.Tick += delegate { q.Stop(); q.Dispose(); Quit(); };
            q.Start();
        };
        s.Start();
    }

    // ---------- 维护开关：--stylesave ----------
    // 把一组"刻意的怪值"走对话框的保存路径写进配置，再重新 Load 回来逐项比对。
    // 会改 deskbox.json —— 跑之前先备份、跑完还原（和 --prunetest 一样）。
    static bool styleSaveTest;
    static void BeginStyleSaveTest()
    {
        Timer t = new Timer();
        t.Interval = 1800;
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            LayeredBox box = forms.Count > 0 ? forms[0] : null;
            if (box == null) { Log.W("stylesave 没有盒子"); Quit(); return; }

            DStyle d = new DStyle();
            d.BgOn = true; d.BgColor = "#12AB34"; d.BgAlpha = 71;
            d.EdgeOn = true; d.EdgeColor = "#FF0000"; d.EdgeAlpha = 44; d.EdgeWidth = 3;
            d.TextOn = true; d.TextColor = "#00FF00"; d.TextAlpha = 88;
            d.SepOn = true; d.SepColor = "#0000FF"; d.SepAlpha = 55;
            d.Radius = 22;
            try
            {
                StyleForm f = box.OpenStyleForm();
                f.TestSet(d);
                f.TestSave();
            }
            catch (Exception ex) { Log.W("stylesave 失败 " + ex.Message); Quit(); return; }

            Timer s = new Timer();
            s.Interval = 400;
            s.Tick += delegate
            {
                s.Stop(); s.Dispose();
                DRoot again = Store.Load();
                DStyle r = (again.Boxes.Count > 0 && again.Boxes[0].Style != null) ? again.Boxes[0].Style : null;
                if (r == null) { Log.W("stylesave 回读失败：Style 是 null"); Quit(); return; }
                bool ok = r.BgOn && r.BgColor == "#12AB34" && r.BgAlpha == 71
                          && r.EdgeOn && r.EdgeColor == "#FF0000" && r.EdgeAlpha == 44 && r.EdgeWidth == 3
                          && r.TextOn && r.TextColor == "#00FF00" && r.TextAlpha == 88
                          && r.SepOn && r.SepColor == "#0000FF" && r.SepAlpha == 55
                          && r.Radius == 22;
                Log.W("stylesave 回读 bg=" + r.BgOn + "/" + r.BgColor + "/" + r.BgAlpha
                      + " edge=" + r.EdgeOn + "/" + r.EdgeColor + "/" + r.EdgeAlpha + "/" + r.EdgeWidth
                      + " text=" + r.TextOn + "/" + r.TextColor + "/" + r.TextAlpha
                      + " sep=" + r.SepOn + "/" + r.SepColor + "/" + r.SepAlpha
                      + " radius=" + r.Radius + " => " + (ok ? "PASS" : "FAIL"));
                Quit();
            };
            s.Start();
        };
        t.Start();
    }

    // 往对话框发一次左键（真的走鼠标消息，不是直接改字段），点调色板第 i 格
    static void ClickPalette(StyleForm f, int i)
    {
        int col = i % 7, row = i / 7;
        int x = 144 + col * 28 + 12;
        int y = 288 + row * 28 + 12;
        IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
        PostMessage(f.Handle, 0x0201, (IntPtr)1, lp);      // WM_LBUTTONDOWN（MK_LBUTTON）
        PostMessage(f.Handle, 0x0202, IntPtr.Zero, lp);    // WM_LBUTTONUP
        Log.W("styleform 点击调色板 #" + i + " @" + x + "," + y + " 期望 " + Store.ToHex(f.TestPalette(i)));
    }

    // ---------- 维护开关：--styletest ----------
    // 6 组极端参数直接套到第一个盒子上离线出图，不碰 deskbox.json。
    // 方便一眼看出"背景全透 / 无边框 / 文字很淡 / 方角 / 超大圆角"这些边界情况对不对。
    static void RunStyleTest(string outPath)
    {
        DRoot root = Store.Load();
        if (root.Boxes.Count == 0) return;
        DBox d = root.Boxes[0];
        DBox origin = d;
        string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        string baseName = Path.GetFileNameWithoutExtension(outPath);

        DStyle[] cases = new DStyle[] {
            // 0 全跟随主题（= 默认外观）
            null,
            // 1 背景：近纯黑 88%
            new DStyle { BgOn = true, BgColor = "#0B0D10", BgAlpha = 88 },
            // 2 亮背景 + 完全不要边线
            new DStyle { BgOn = true, BgColor = "#FFFFFF", BgAlpha = 62, EdgeOn = true, EdgeColor = "#FFFFFF", EdgeAlpha = 0 },
            // 3 文字 35%，其余跟随
            new DStyle { TextOn = true, TextColor = "#F2E9D8", TextAlpha = 35 },
            // 4 方角 + 3px 亮黄边
            new DStyle { Radius = 0, EdgeOn = true, EdgeColor = "#FFC24B", EdgeAlpha = 100, EdgeWidth = 3 },
            // 5 大圆角 26 + 深酒红背景 + 纯白文字
            new DStyle { BgOn = true, BgColor = "#4A1F2B", BgAlpha = 78, Radius = 26, TextOn = true, TextColor = "#FFFFFF", TextAlpha = 100 },
            // 6 标签栏分隔线明显（红 70%），其余跟随
            new DStyle { SepOn = true, SepColor = "#FF3B30", SepAlpha = 70 },
        };
        string[] names = { "0默认", "1深底88", "2白底无边", "3文字35", "4方角3px边", "5大圆角", "6分隔线红" };

        for (int i = 0; i < cases.Length; i++)
        {
            d.Style = cases[i] == null ? new DStyle() : cases[i];
            Store.RepairStyle(d.Style);
            LayeredBox b = new LayeredBox(root, d);
            b.Width = d.W; b.Height = d.H;
            using (Bitmap bmp = b.Compose())
            {
                string f = Path.Combine(dir ?? ".", baseName + "-" + i + ".png");
                bmp.Save(f, ImageFormat.Png);
                Log.W("styletest " + names[i] + " -> " + f + " rad=" + b.RadiusVal +
                      " bg=" + Store.ToHex(b.BgCol) + "/" + b.BgPct + " edge=" + Store.ToHex(b.EdgeBase) +
                      "/" + b.EdgePct + " sep=" + Store.ToHex(b.SepBase) + "/" + b.SepPct);
            }
            b.Dispose();
        }
        d.Style = null;   // 不写回配置
    }
    // ---------- 维护开关：--renametest ----------
    // ---------- 维护开关：--droptest ----------
    // 验证外部拖入时 e.Effect 一定落在源允许的集合里。
    // 写死 Move 的情况下，桌面快捷方式 / 跨盘拖入只给 Copy|Link 时 OLE 会出"禁止"光标，
    // OnDragDrop 根本不触发 —— 这就是"桌面图标拖不进来"。
    static void BeginDropTest()
    {
        Timer t = new Timer();
        t.Interval = 1500;
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("droptest " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            LayeredBox box = forms.Count > 0 ? forms[0] : null;
            int pass = 0, total = 0;
            DragDropEffects[] cases = {
                DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link,
                DragDropEffects.Copy,
                DragDropEffects.Copy | DragDropEffects.Link,
                DragDropEffects.Link,
                DragDropEffects.None
            };
            string[] want = { "Move", "Copy", "Copy", "Link", "None" };
            for (int i = 0; i < cases.Length; i++)
            {
                string got = box == null ? "no-box" : box.TestDragEffect(cases[i]);
                total++;
                bool ok = got == want[i];
                if (ok) pass++;
                sb.AppendLine("  allowed=" + cases[i] + " -> effect=" + got +
                              " (期望 " + want[i] + ") " + (ok ? "PASS" : "FAIL"));
            }
            sb.AppendLine("结果 " + pass + "/" + total);
            try { File.WriteAllText(Path.Combine(Store.BaseDir, "droptest-report.txt"), sb.ToString()); }
            catch { }
            Log.W("droptest " + pass + "/" + total);
            Quit();
        };
        t.Start();
    }

    static void BeginRenameTest(int mode)
    {
        Timer t = new Timer();
        t.Interval = 2500;
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            LayeredBox box = forms.Count > 0 ? forms[0] : null;
            if (box == null) { Log.W("renametest 没有盒子"); Quit(); return; }
            if (mode == 0) box.BeginRenameBox();
            else if (mode == 1) box.BeginRenameTab();
            else box.BeginRenameItem(0);

            // 3 = 标签改名提交  4 = 图标改名提交（真的写配置，跑前备份跑后还原）
            bool doCommit = (mode == 3 || mode == 4);
            if (doCommit && mode == 4) { mode = 2; box.BeginRenameItem(0); }
            else if (doCommit) { mode = 1; box.BeginRenameTab(); }

            if (doCommit)
            {
                Timer c1 = new Timer();
                c1.Interval = 500;
                c1.Tick += delegate
                {
                    c1.Stop(); c1.Dispose();
                    InlineEditor ed0 = InlineEditor.Current;
                    if (ed0 == null) { Log.W("renametest 提交失败：没有编辑器"); Quit(); return; }
                    ed0.TestSetText("自测改名XYZ");
                    ed0.TestFinish(true);
                    Timer c2 = new Timer();
                    c2.Interval = 500;
                    c2.Tick += delegate
                    {
                        c2.Stop(); c2.Dispose();
                        Log.W("renametest 提交后 title=" + box.TestTitle() +
                              " tab0=" + box.TestTabName(0) + " item0=" + box.TestItemName(0));
                        Quit();
                    };
                    c2.Start();
                };
                c1.Start();
                return;
            }

            Timer s1 = new Timer();
            s1.Interval = 800;
            s1.Tick += delegate
            {
                s1.Stop(); s1.Dispose();
                try
                {
                    InlineEditor ed = InlineEditor.Current;
                    Log.W("renametest mode=" + mode + " editor=" + (ed != null ? ed.Bounds.ToString() : "null"));
                    if (ed != null)
                    {
                        Rectangle w = ed.Bounds;
                        Rectangle head = new Rectangle(box.Left, box.Top, box.Width, 140);
                        Rectangle grab = Rectangle.Union(w, head);
                        grab.Inflate(10, 10);
                        using (Bitmap b = new Bitmap(grab.Width, grab.Height))
                        {
                            using (Graphics g = Graphics.FromImage(b))
                                g.CopyFromScreen(grab.Left, grab.Top, 0, 0, b.Size);
                            b.Save(Path.Combine(Store.BaseDir, "rename-shot-" + mode + ".png"), ImageFormat.Png);
                        }
                        Log.W("renametest 截图 " + grab);
                    }
                }
                catch (Exception ex) { Log.W("renametest 截图失败 " + ex.GetType().Name + " " + ex.Message); }
                InlineEditor.CloseCurrent(false);
                Quit();
            };
            s1.Start();
        };
        t.Start();
    }

    static void BeginBoxMenuTest()
    {
        Timer t = new Timer();
        t.Interval = 2500;
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            LayeredBox box = forms.Count > 0 ? forms[0] : null;
            if (box == null) { Log.W("boxmenu 没有盒子"); Quit(); return; }

            box.TestShowBoxMenu(new Point(28, 40));
            Timer s1 = new Timer();
            s1.Interval = 700;
            s1.Tick += delegate
            {
                s1.Stop(); s1.Dispose();
                ContextMenuStrip am = LayeredBox.CurrentMenu;
                Rectangle grab = Rectangle.Empty;
                try
                {
                    if (am != null)
                    {
                        grab = am.Bounds;
                        foreach (ToolStripItem it in am.Items)
                        {
                            ToolStripMenuItem mi = it as ToolStripMenuItem;
                            if (mi == null || mi.Text != "盒子颜色") continue;
                            mi.ShowDropDown();
                            break;
                        }
                    }
                }
                catch (Exception ex) { Log.W("boxmenu 展开失败 " + ex.Message); }

                Timer s2 = new Timer();
                s2.Interval = 900;
                s2.Tick += delegate
                {
                    s2.Stop(); s2.Dispose();
                    try
                    {
                        if (am != null)
                        {
                            grab = am.Bounds;
                            foreach (ToolStripItem it in am.Items)
                            {
                                ToolStripMenuItem mi = it as ToolStripMenuItem;
                                if (mi == null || !mi.DropDown.Visible) continue;
                                grab = Rectangle.Union(grab, mi.DropDown.Bounds);
                                foreach (ToolStripItem sub in mi.DropDownItems)
                                {
                                    ToolStripMenuItem smi = sub as ToolStripMenuItem;
                                    Log.W("boxmenu 子项 [" + sub.Text + "] img=" + (sub.Image != null) +
                                          ((smi != null && smi.Checked) ? " checked" : ""));
                                }
                            }
                        }
                        if (grab.Width > 0)
                        {
                            grab.Inflate(12, 12);
                            using (Bitmap b = new Bitmap(grab.Width, grab.Height))
                            {
                                using (Graphics g = Graphics.FromImage(b))
                                    g.CopyFromScreen(grab.Left, grab.Top, 0, 0, b.Size);
                                b.Save(Path.Combine(Store.BaseDir, "boxmenu-shot.png"), ImageFormat.Png);
                            }
                            Log.W("boxmenu 截图 " + grab);
                        }
                    }
                    catch (Exception ex) { Log.W("boxmenu 截图失败 " + ex.Message); }
                    try { if (am != null) am.Close(); } catch { }
                    Quit();
                };
                s2.Start();
            };
            s1.Start();
        };
        t.Start();
    }
    static void BeginPruneTest()
    {
        Timer t = new Timer();
        t.Interval = 1500;
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("prunetest " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            LayeredBox box = forms.Count > 0 ? forms[0] : null;
            if (box == null) { sb.AppendLine("没有盒子"); }
            else sb.AppendLine(box.TestPruneOnce());
            // 1.5 秒后再看
            Timer chk = new Timer();
            chk.Interval = 1500;
            chk.Tick += delegate
            {
                chk.Stop(); chk.Dispose();
                bool still = box != null && box.TestHasItem("_prunetest.tmp");
                sb.AppendLine("1.5 秒后条目是否还在 = " + (still ? "在（失败）" : "已摘掉（正常）"));
                try
                {
                    string json = File.ReadAllText(Path.Combine(Store.BaseDir, "deskbox.json"));
                    sb.AppendLine("deskbox.json 里还有 _prunetest = " + (json.Contains("_prunetest") ? "有（失败）" : "没有（正常）"));
                }
                catch (Exception ex) { sb.AppendLine("读配置失败 " + ex.Message); }
                try { File.WriteAllText(Path.Combine(Store.BaseDir, "prunetest-report.txt"), sb.ToString(), System.Text.Encoding.UTF8); } catch { }
                Log.W("prunetest\n" + sb);
                Quit();
            };
            chk.Start();
        };
        t.Start();
    }

    // ---------- 维护开关：--perftest ----------
    // 体检"切子标签慢"和"右键慢"：切标签渲染计时（冷/热各两轮）+ 单独构建一次 shell 菜单的拆解耗时。
    static bool perfTest;
    static void BeginPerfTest()
    {
        Timer t = new Timer();
        t.Interval = 5000;   // 等图标预热（25ms/个）和 shell 菜单预热都跑完，测到的才是用户实际体验
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("perftest " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                if (forms.Count == 0) { sb.AppendLine("没有盒子"); }
                else
                {
                    LayeredBox box = forms[0];
                    sb.AppendLine("标签数=" + box.TestTabCount + " 图标缓存=" + IconCache.Count + " 取图次数=" + IconCache.Miss);
                    for (int pass = 1; pass <= 3; pass++)
                    {
                        for (int i = 0; i < box.TestTabCount; i++)
                        {
                            double ms = box.TestSwitchTabMs(i);
                            sb.AppendLine("  第" + pass + "轮 切到标签" + i + " = " + ms + "ms  缓存=" + IconCache.Count + " 取图=" + IconCache.Miss);
                        }
                    }
                    // 右键菜单构建耗时（连做两次：第一次含加载 shell 扩展，第二次是热的）
                    string firstPath = null;
                    foreach (DTab tb in box.Data.Tabs)
                        foreach (DItem it in tb.Items)
                            if (File.Exists(it.Path)) { firstPath = it.Path; break; }
                    if (firstPath != null)
                    {
                        sb.AppendLine("shell 菜单构建 #1（冷） " + firstPath);
                        sb.Append(ShellMenu.MeasureBuild(firstPath));
                        sb.AppendLine("shell 菜单构建 #2（热）");
                        sb.Append(ShellMenu.MeasureBuild(firstPath));
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("异常：" + ex.Message); }
            try { File.WriteAllText(Path.Combine(Store.BaseDir, "perftest-report.txt"), sb.ToString(), System.Text.Encoding.UTF8); } catch { }
            Log.W("perftest\n" + sb);
            Quit();
        };
        t.Start();
    }

    // ---------- 维护开关：--shellmenucmd ----------
    // 验证"菜单选中之后真的能执行"：直接按动词名调一次 shell 的 copy
    // （copy 只写剪贴板、不弹窗），然后检查剪贴板里有没有出现这个文件。
    static bool shellMenuCmdTest;
    static void BeginShellMenuCmdTest()
    {
        Timer t = new Timer();
        t.Interval = 1500;
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("shellmenucmd " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                string path = null;
                if (forms.Count > 0)
                {
                    LayeredBox box = forms[0];
                    foreach (DTab tb in box.Data.Tabs)
                        foreach (DItem it in tb.Items)
                            if (File.Exists(it.Path)) { path = it.Path; break; }
                }
                if (path == null) { sb.AppendLine("没有真实文件可测"); }
                else
                {
                    sb.AppendLine("目标=" + path);
                    int hr = ShellMenu.InvokeVerbByName(path, "copy");
                    sb.AppendLine("InvokeCommand(copy) hr=0x" + hr.ToString("X8"));
                    System.Threading.Thread.Sleep(400);
                    Application.DoEvents();
                    bool onClip = false;
                    try
                    {
                        if (Clipboard.ContainsFileDropList())
                        {
                            foreach (string s in Clipboard.GetFileDropList())
                                if (string.Equals(s, path, StringComparison.OrdinalIgnoreCase)) onClip = true;
                        }
                    }
                    catch (Exception ex) { sb.AppendLine("剪贴板读取失败 " + ex.Message); }
                    sb.AppendLine("剪贴板里有这个文件 = " + (onClip ? "是" : "否"));
                }
            }
            catch (Exception ex) { sb.AppendLine("异常：" + ex.Message); }
            try { File.WriteAllText(Path.Combine(Store.BaseDir, "shellmenucmd-report.txt"), sb.ToString(), System.Text.Encoding.UTF8); } catch { }
            Log.W("shellmenucmd\n" + sb);
            Quit();
        };
        t.Start();
    }

    // ---------- 维护开关：--dragtest ----------
    // 不开拖拽，只把"真要交给系统的拖拽数据"列出来核对：
    // 必须有 FileDrop（CF_HDROP，资源管理器/浏览器上传都靠它），
    // 且 PreferredDropEffect 默认 2（剪切），Ctrl 时 1（复制）。
    static bool dragTest;
    static void BeginDragTest()
    {
        Timer t = new Timer();
        t.Interval = 1200;
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("dragtest " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                if (forms.Count == 0) sb.AppendLine("没有盒子");
                else sb.AppendLine(forms[0].TestDragDataDump());
            }
            catch (Exception ex) { sb.AppendLine("异常：" + ex.Message); }
            try { File.WriteAllText(Path.Combine(Store.BaseDir, "dragtest-report.txt"), sb.ToString(), System.Text.Encoding.UTF8); } catch { }
            Log.W("dragtest\n" + sb);
            Quit();
        };
        t.Start();
    }

    // ---------- 维护开关：--deskmenu ----------
    // 复现"桌面状态下右键，盒子持续闪烁"：真的显示桌面 → 右键桌面图标层（弹壳自己的菜单）
    // → 每 40ms 记录盒子的 topmost 标志翻了几次，并记下前台窗口的类名/宿主/进程。
    static bool deskMenuTest;
    static bool deskMenuTray;               // --deskmenu 的第二个参数：右键任务栏而不是桌面
    static int deskMenuSeconds = 4;
    static void BeginDeskMenuTest()
    {
        Timer boot = new Timer();
        boot.Interval = 800;
        boot.Tick += delegate
        {
            boot.Stop(); boot.Dispose();
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                if (forms.Count == 0) { sb.AppendLine("没有盒子"); WriteDeskMenu(sb); return; }
                LayeredBox box = forms[0];
                string d0;
                int st0 = LayeredBox.ProbeState(out d0);
                if (st0 != 2)
                {
                    // 上一轮自测若把桌面留在显示态，这里再 toggle 一次就变成"翻回正常"，
                    // 于是 app=35 desk=0 一直等不到显示状态（旧版超时就是这么来的）。
                    ToggleDesktopBack("deskmenu-boot");
                    sb.AppendLine("[已复位] 测试前桌面不在正常态，先还原（" + d0 + "）");
                }
                ToggleDesktopEx(); sb.AppendLine("[已触发] 显示桌面");
                sb.AppendLine("[本进程窗口清单·右键前]");
                sb.Append(LayeredBox.DumpOwnWindows());
                int phase = 0, ticks = 0, flips = 0, settle = 0;
                bool last = box.TestTopmost;
                bool dumped = false;
                Timer step = new Timer();
                step.Interval = 40;
                step.Tick += delegate
                {
                    if (phase == 0)
                    {
                        // 等桌面真的进入"显示桌面"状态、盒子也浮到位了再右键。
                        // 动画期间右键桌面是不弹壳菜单的（第一版就是踩这个坑：菜单没弹，
                        // 等于没测到正题），所以不能靠固定拍数，要按状态等。
                        if (++ticks < 10) return;
                        string dd;
                        if (LayeredBox.ProbeState(out dd) != 1)
                        {
                            if (ticks < 250) return;                 // 最多等 10 秒
                            sb.AppendLine("[超时] 桌面一直没进入显示状态（" + dd + "）");
                            WriteDeskMenu(sb); Quit(); return;
                        }
                        // 盒子在"显示桌面"时**不一定要**抬成 topmost：只要没被桌面图标层
                        // 盖住，就留在桌面层（这正是 2026-11-08 掐掉的浮动来源）。
                        // 以前这里死等 TestTopmost，新行为下 --deskmenu 会永远卡在阶段 0。
                        if (++settle < 8) return;                    // 让桌面动画收尾
                        ticks = 0;
                        last = box.TestTopmost;                      // 以"已浮好"为基准计数
                        phase++;
                        if (deskMenuTray)
                        {
                            // 右键任务栏空白处 → 任务栏自己的右键菜单（可能是另一个壳进程托管的）。
                            // Win11 任务栏是 XAML 托管的，直接发给 Shell_TrayWnd 没人理，
                            // 必须用 WindowFromPoint 找到真正接收输入的那个窗口，再换算客户区坐标。
                            Rectangle vs = SystemInformation.VirtualScreen;
                            LayeredBox.POINT tp; tp.X = vs.Left + vs.Width / 2; tp.Y = vs.Bottom - 22;
                            IntPtr tgt = LayeredBox.WindowFromPoint(tp);
                            if (tgt == IntPtr.Zero || LayeredBox.IsOwnHwnd(tgt))
                            { sb.AppendLine("任务栏上找不到可点的窗口"); WriteDeskMenu(sb); Quit(); return; }
                            LayeredBox.ScreenToClient(tgt, ref tp);
                            PostClick(tgt, WM_RBUTTONDOWN, tp.X, tp.Y, 2);
                            PostClick(tgt, WM_RBUTTONUP, tp.X, tp.Y, 0);
                            sb.AppendLine("[已右键] 任务栏空白处 hwnd=0x" + tgt.ToInt64().ToString("X") +
                                          " cls=" + LayeredBox.ClassOf(tgt) + " (" + LayeredBox.DescribeWnd(tgt) + ")");
                            return;
                        }
                        IntPtr prog = FindWindow("Progman", null);
                        IntPtr defView = FindWindowEx(prog, IntPtr.Zero, "SHELLDLL_DefView", null);
                        IntPtr lv = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
                        if (lv == IntPtr.Zero) { sb.AppendLine("找不到桌面图标层 SysListView32"); WriteDeskMenu(sb); return; }
                        int x = 1400, y = 300;
                        PostClick(lv, WM_RBUTTONDOWN, x, y, 2);
                        PostClick(lv, WM_RBUTTONUP, x, y, 0);
                        sb.AppendLine("[已右键] 桌面图标层");
                        return;
                    }
                    bool t = box.TestTopmost;
                    IntPtr fg = GetForegroundWindow();
                    // 前台落在本进程窗口上时，把本进程所有窗口列一遍（只列一次）：
                    // 只凭类名 WindowsForms10.Window.8 猜不出那是谁，列出来直接定位
                    if (!dumped && LayeredBox.IsOwnHwnd(fg))
                    {
                        dumped = true;
                        sb.AppendLine("  [前台是本进程窗口] 0x" + fg.ToInt64().ToString("X") +
                                      " activeMenu=" + (LayeredBox.MenuActive ? "有" : "null") +
                                      " LastMenuHwnd=0x" + LayeredBox.LastMenuHwnd.ToInt64().ToString("X"));
                        sb.Append(LayeredBox.DumpOwnWindows());
                    }
                    if (t != last)
                    {
                        flips++;
                        sb.AppendLine("  [" + (flips) + "] topmost " + (last ? 1 : 0) + " -> " + (t ? 1 : 0) +
                                      "  fg=" + LayeredBox.DescribeWnd(GetForegroundWindow()));
                        last = t;
                    }
                    if ((++ticks & 12) == 0)
                    {
                        string dz;
                        LayeredBox.SampleDesktopZ(out dz);
                        sb.AppendLine("  采样 topmost=" + (t ? 1 : 0) + " live=" + (LayeredBox.LiveForegroundApp() ? 1 : 0) +
                                      " appVis=" + (LayeredBox.AnyAppVisible() ? 1 : 0) +
                                      " ovl=" + (LayeredBox.ShellOverlayUp() ? 1 : 0) +
                                      " z=" + dz + " fg=" + LayeredBox.DescribeWnd(GetForegroundWindow()));
                    }
                    if (ticks >= deskMenuSeconds * 25)
                    {
                        step.Stop(); step.Dispose();
                        // 收尾：在桌面空地处点一下左键，把壳菜单点掉（任务栏菜单、桌面菜单
                        // 都是这么关的），别留在用户屏幕上。
                        // 壳菜单是异步关的，必须等它真的关了再探状态，否则菜单本身会被
                        // 当成"程序窗口"，判定成"桌面没在显示"，还原就跳过了。
                        IntPtr prog = FindWindow("Progman", null);
                        IntPtr defView = FindWindowEx(prog, IntPtr.Zero, "SHELLDLL_DefView", null);
                        IntPtr lv = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
                        if (lv != IntPtr.Zero)
                        {
                            PostClick(lv, WM_LBUTTONDOWN, 1400, 300, 1);
                            PostClick(lv, WM_LBUTTONUP, 1400, 300, 0);
                        }
                        System.Threading.Thread.Sleep(700);
                        sb.AppendLine("--- end --- topmost 翻转次数=" + flips + "（0 才正常）");
                        // 收尾：把桌面状态还原，别把用户的窗口都留在最小化
                        string dEnd;
                        if (LayeredBox.ProbeState(out dEnd) == 2) sb.AppendLine("桌面未处于显示状态，无需还原");
                        else
                        {
                            bool restored = ToggleDesktopBack("deskmenu");
                            sb.AppendLine(restored ? "[已还原] 退出显示桌面" : "[!!] 没能还原桌面状态，请手动再按一次 Win+D");
                        }
                        WriteDeskMenu(sb);
                        Log.W("deskmenu flips=" + flips);
                        Quit();
                    }
                };
                step.Start();
                return;
            }
            catch (Exception ex) { sb.AppendLine("出错: " + ex.Message); }
            WriteDeskMenu(sb);
            Quit();
        };
        boot.Start();
    }

    static void WriteDeskMenu(System.Text.StringBuilder sb)
    {
        try
        {
            File.WriteAllText(Path.Combine(Store.BaseDir, "deskmenu-report.txt"), sb.ToString(), System.Text.Encoding.UTF8);
        }
        catch { }
    }

    // ---------- 维护开关：--diag ----------
    // 纯观察：不改任何行为，逐 40ms 记录“盒子真实处境”的变化，并记录窗口收到的每一条
    // 会改变可见性 / z 序 / 状态的消息，以及系统事件。用来回答一个问题：
    // 按“显示桌面”时，盒子到底是被最小化、被隐藏（SW_HIDE）、还是被 DWM 隐去（cloaked）。
    static bool diagOn;
    static int diagSeconds = 30;
    static System.Text.StringBuilder diagSb;
    static int diagT0;
    static Timer diagTick;
    static string[] diagLast;

    static void StartDiag(int seconds)
    {
        diagSb = new System.Text.StringBuilder();
        diagT0 = Environment.TickCount;
        diagLast = new string[forms.Count];
        diagSb.AppendLine("DeskBox diag   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        diagSb.AppendLine("os=" + OsBuildText() + "  " + Environment.OSVersion.VersionString);
        diagSb.AppendLine("boxes=" + forms.Count + "  duration=" + seconds + "s");
        diagSb.AppendLine("请在记录期间【点两次任务栏“显示桌面”按钮，或按两次 Win+D】");
        diagSb.AppendLine("字段：vis=真实可见(IsWindowVisible) mVis=托管Visible iconic=最小化 cloak=DWM隐去 " +
                          "top=topmost par=父窗口 z=z序名次(0=最前) under=中心点被谁占着");
        StartDiagEventTrace();
        diagTick = new Timer();
        diagTick.Interval = 40;
        diagTick.Tick += delegate { DiagStep(seconds); };
        diagTick.Start();
    }

    static void DiagStep(int seconds)
    {
        int ms = Environment.TickCount - diagT0;
        try
        {
            for (int i = 0; i < forms.Count; i++)
            {
                string s = forms[i].DiagDump();
                if (s != diagLast[i])
                {
                    diagLast[i] = s;
                    diagSb.AppendLine("[" + ms.ToString().PadLeft(6) + "ms] box" + i + " " + s);
                }
            }
            if (ms >= seconds * 1000)
            {
                diagTick.Stop(); diagTick.Dispose();
                diagSb.AppendLine("--- end ---");
                try { File.WriteAllText(Path.Combine(Store.BaseDir, "diag-report.txt"), diagSb.ToString(), System.Text.Encoding.UTF8); }
                catch { }
                Log.W("diag report written");
                try { Application.Exit(); } catch { Environment.Exit(0); }
            }
        }
        catch { }
    }

    // 只记和"盒子为什么不见了"有关的事件，避免刷屏
    static IntPtr diagHook;
    static WinEventDelegate2 diagProc;
    static int diagEvCount;

    // 未加 Win10 清单的程序，Environment.OSVersion 只会报 6.2.9200，真正的版本号在注册表里
    static string OsBuildText()
    {
        try
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (k != null)
                {
                    object pn = k.GetValue("ProductName");
                    object dn = k.GetValue("DisplayVersion");
                    object bn = k.GetValue("CurrentBuildNumber");
                    object ub = k.GetValue("UBR");
                    return (pn == null ? "?" : pn.ToString()) + " " +
                           (dn == null ? "" : dn.ToString()) + " build=" +
                           (bn == null ? "?" : bn.ToString()) + "." + (ub == null ? "?" : ub.ToString());
                }
            }
        }
        catch { }
        return "?";
    }

    // 这些事件才和"盒子被藏起来/被抬起来"有关；CREATE/NAMECHANGE 之类纯噪声
    static bool DiagEventWanted(uint ev)
    {
        return ev == 0x0003 ||   // FOREGROUND
               ev == 0x0016 ||   // MINIMIZESTART
               ev == 0x0017 ||   // MINIMIZEEND
               ev == 0x8002 ||   // OBJECT_SHOW
               ev == 0x8003 ||   // OBJECT_HIDE
               ev == 0x8004 ||   // OBJECT_REORDER
               ev == 0x800A ||   // OBJECT_STATECHANGE
               ev == 0x8011 ||   // OBJECT_CLOAKED
               ev == 0x8012;     // OBJECT_UNCLOAKED
    }

    static void StartDiagEventTrace()
    {
        if (diagHook != IntPtr.Zero) return;
        diagProc = new WinEventDelegate2(delegate (IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try
            {
                if (diagSb == null || diagEvCount > 500) return;
                if (!DiagEventWanted(ev)) return;
                string cls = LayeredBox.ClassNameOf(hwnd);
                bool ours = (uint)Process.GetCurrentProcess().Id == 0 ? false : LayeredBox.IsOwnHwnd(hwnd);
                bool interesting = ours || cls == "Progman" || cls == "WorkerW" || cls == "SHELLDLL_DefView" ||
                                   cls == "SysListView32" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";
                if (!interesting) return;
                diagEvCount++;
                diagSb.AppendLine("[" + (Environment.TickCount - diagT0).ToString().PadLeft(6) + "ms]   ev=0x" +
                    ev.ToString("X4") + " " + (ours ? "本程序 " : "系统 ") + cls);
            }
            catch { }
        });
        diagHook = SetWinEventHook(0x0001, 0x8018, IntPtr.Zero, diagProc, 0, 0,
            TRACE_OUTOFCONTEXT);   // 注意：不跳过本进程，我们要看自己的事件
    }

    // 盒子收到消息（由 LayeredBox.WndProc 调用）
    public static void DiagMsg(LayeredBox f, int msg, IntPtr w, IntPtr l)
    {
        try
        {
            if (diagSb == null) return;
            int idx = forms.IndexOf(f);
            string name;
            switch (msg)
            {
                case 0x0018: name = "WM_SHOWWINDOW"; break;
                case 0x0046: name = "WM_WINDOWPOSCHANGING"; break;
                case 0x0047: name = "WM_WINDOWPOSCHANGED"; break;
                case 0x0112: name = "WM_SYSCOMMAND"; break;
                case 0x0005: name = "WM_SIZE"; break;
                case 0x0006: name = "WM_ACTIVATE"; break;
                case 0x001C: name = "WM_ACTIVATEAPP"; break;
                case 0x0086: name = "WM_NCACTIVATE"; break;
                case 0x0007: name = "WM_SETFOCUS"; break;
                case 0x0008: name = "WM_KILLFOCUS"; break;
                case 0x0010: name = "WM_CLOSE"; break;
                default: return;   // 其它消息不记
            }
            string extra = "";
            if (msg == 0x0046 && l != IntPtr.Zero)
            {
                try
                {
                    LayeredBox.WINDOWPOS wp = (LayeredBox.WINDOWPOS)Marshal.PtrToStructure(l, typeof(LayeredBox.WINDOWPOS));
                    extra = " after=0x" + wp.hwndInsertAfter.ToInt64().ToString("X") +
                            " flags=0x" + wp.flags.ToString("X") +
                            " rect=" + wp.x + "," + wp.y + "," + wp.cx + "," + wp.cy;
                }
                catch { }
            }
            diagSb.AppendLine("[" + (Environment.TickCount - diagT0).ToString().PadLeft(6) + "ms]   box" + idx +
                " recv " + name + " w=0x" + w.ToInt64().ToString("X") + extra);
        }
        catch { }
    }

    // ---------- 维护开关：--watchtest ----------
    // 自己按一次“显示桌面”（Shell.Application.ToggleDesktop），逐帧记录每个盒子
    // 究竟露没露脸：under=自己 才叫真的显示在桌面上。用来确认没有来回闪。
    static bool watchTest;
    static bool shot2Done;
    static Timer watchTick;
    static int watchPhase, watchT0, watchRaises, watchSinks;
    static System.Text.StringBuilder watchSb;
    static string[] watchLast;

    static bool winTrigger;   // --win：用 ToggleDesktop 触发
    static bool watchNoForeground;   // --nofg：还原窗口时不激活（模拟 Win+D 还原的真实情况）

    static void BeginWatchTest()
    {
        watchSb = new System.Text.StringBuilder();
        watchSb.AppendLine("DeskBox watchtest  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        watchSb.AppendLine("boxes=" + forms.Count);
        watchLast = new string[forms.Count + 1];
        flashGapMs = new int[forms.Count + 1];
        flashOverMs = new int[forms.Count + 1];
        flashLast = new int[forms.Count + 1];
        Timer boot = new Timer();
        boot.Interval = 2000;
        boot.Tick += delegate
        {
            boot.Stop(); boot.Dispose();
            string d0;
            int st0 = LayeredBox.ProbeState(out d0);
            watchSb.AppendLine("[ boot] 起始 state=" + st0 + " " + d0);
            if (st0 == 2)
            {
                if (winTrigger) { ToggleDesktopEx(); watchSb.AppendLine("[ boot] ToggleDesktop（真实通路）→ 显示桌面"); }
                else { ShowDesktopTray(); watchSb.AppendLine("[ boot] 托盘命令 → 显示桌面"); }
            }
            else watchSb.AppendLine("[ boot] 桌面本来就在显示状态，不再触发");
            watchT0 = Environment.TickCount;
            watchPhase = 0;
            StartEventTrace();
            watchTick = new Timer();
            watchTick.Interval = 20;
            watchTick.Tick += delegate { WatchStep(); };
            watchTick.Start();
            SampleAll(0);
        };
        boot.Start();
    }

    // “显示桌面”通路：给任务栏发 WM_COMMAND 419，可重复触发（COM 的 ToggleDesktop 只会生效一次）
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string win);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // 给桌面图标层发鼠标消息一律用 PostMessage：SendMessage 是同步的，
    // 壳窗口偶尔不按时处理就会把我们的 UI 线程卡死（整个测试挂住）。
    static void PostClick(IntPtr h, int msg, int x, int y, int mk)
    {
        if (h == IntPtr.Zero) return;
        PostMessage(h, msg, (IntPtr)mk, (IntPtr)((y << 16) | (x & 0xFFFF)));
    }

    // 给任务栏发 WM_COMMAND 419（"显示桌面"按钮的命令）：可重复触发。
    // COM 的 Shell.Application.ToggleDesktop 每个进程只会生效一次，收尾还原必须走这条。
    // 用 SendMessageTimeout：同步发（PostMessage 实测任务栏不理会），但带超时，
    // 壳窗口不按时处理也不会把 UI 线程卡死。
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, UIntPtr wp, IntPtr lp, uint flags, uint timeout, out UIntPtr result);
    const uint SMTO_ABORTIFHUNG = 0x0002;

    static void ShowDesktopTray()
    {
        try
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) { Log.W("tray toggle: 找不到 Shell_TrayWnd"); return; }
            UIntPtr r;
            SendMessageTimeout(tray, 0x0111, (UIntPtr)419, IntPtr.Zero, SMTO_ABORTIFHUNG, 3000, out r);   // WM_COMMAND / 419
            Log.W("tray toggle: 已发任务栏命令");
        }
        catch (Exception ex) { Log.W("tray toggle fail " + ex.Message); }
    }

    // 把"显示桌面"状态翻回去。两种通路依次试，每试一次探一次，翻过去就停
    // （避免两种都生效变成"翻两次 = 没翻"）：
    //   ① 任务栏 WM_COMMAND 419：可重复，但要同步发（PostMessage 实测任务栏不理会）
    //   ② COM Shell.Application.ToggleDesktop：每个进程只有第一次有效
    // 返回 true = 桌面已经不在"显示桌面"状态
    static bool ToggleDesktopBack(string who)
    {
        try
        {
            string d;
            // 只有"确实看到程序窗口"才算安全；0=判断不出（动画中/菜单还没关）时也当
            // "可能还在显示桌面"，照样走一遍还原，免得把用户的桌面留在最小化状态
            if (LayeredBox.ProbeState(out d) == 2) { Log.W(who + " 桌面未处于显示状态，无需还原"); return true; }
            Log.W(who + " 桌面处于显示状态，开始还原");
            ShowDesktopTray();
            if (WaitNotShown(1000, who + " 托盘命令")) return true;
            Log.W(who + " 托盘命令没翻回来，改试 COM ToggleDesktop");
            ToggleDesktopEx();
            if (WaitNotShown(1500, who + " COM")) return true;
            // 本机实测（Win11 25H2）："显示桌面"就是把窗口最小化，两种 toggle 都翻不回来。
            // 直接把最小化的窗口恢复出来最可靠 —— 等于手动替用户按了一次 Win+D。
            int n = RestoreAMinimizedApp();
            Log.W(who + " 两种 toggle 都没生效，改为直接恢复最小化窗口 " + n + " 个");
            if (WaitNotShown(1500, who + " 恢复窗口")) return true;
            Log.W(who + " !! 还是没还原，桌面仍在显示状态");
            return false;
        }
        catch (Exception ex) { Log.W(who + " restore fail " + ex.Message); return false; }
    }

    // 等最多 ms 毫秒：桌面退出"显示桌面"状态就返回 true
    static bool WaitNotShown(int ms, string who)
    {
        string d;
        for (int i = 0; i < ms / 50; i++)
        {
            System.Threading.Thread.Sleep(50);
            if (LayeredBox.ProbeState(out d) != 1) { Log.W(who + " 已还原（" + d + "）"); return true; }
        }
        return false;
    }

    static void RunToggleBack()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        string d;
        int st = LayeredBox.ProbeState(out d);
        sb.AppendLine("toggle " + DateTime.Now.ToString("HH:mm:ss") + "  起始 state=" + st + "  " + d);
        bool ok = ToggleDesktopBack("toggle");
        sb.AppendLine("结果：" + (ok ? "桌面已退出显示状态" : "!! 仍在显示状态"));
        try { File.WriteAllText(Path.Combine(Store.BaseDir, "toggle-report.txt"), sb.ToString(), System.Text.Encoding.UTF8); }
        catch { }
        Log.W("toggle done ok=" + ok);
    }

    static void WatchStep()
    {
        int ms = Environment.TickCount - watchT0;
        if (watchPhase == 0 && ms >= 3000)
        {
            // 截图很慢（要存几 MB PNG，会卡住 UI 线程），单独占一拍，
            // 否则后面“恢复窗口”的毫秒戳会被它带偏。
            watchPhase = 1;
            for (int i = 0; i < forms.Count; i++)
                watchSb.AppendLine("[" + ms.ToString().PadLeft(5) + "ms] 桌面状态 box" + i + " " + forms[i].DiagDump());
            GrabScreen(Path.Combine(Store.BaseDir, "watchtest-shot.png"));
            return;
        }
        if (watchPhase == 1 && ms >= 3500)
        {
            watchPhase = 2;
            traceOn = true; traceT0 = Environment.TickCount; traceCount = 0;
            int n = RestoreAMinimizedApp();
            watchSb.AppendLine("[" + ms.ToString().PadLeft(5) + "ms] 恢复最小化窗口 " + n + " 个（以下为事件时序）");
        }
        else if (watchPhase == 2 && ms >= 4500 && !shot2Done)
        {
            // 还原窗口之后再来一张截图：用来看“盒子到底有没有浮在程序窗口上面”
            shot2Done = true;
            for (int i = 0; i < forms.Count; i++)
                watchSb.AppendLine("[" + ms.ToString().PadLeft(5) + "ms] 还原后 box" + i + " " + forms[i].DiagDump());
            GrabScreen(Path.Combine(Store.BaseDir, "watchtest-shot2.png"));
            return;
        }
        else if (watchPhase == 2 && ms >= 7000)
        {
            string d;
            int st = LayeredBox.ProbeState(out d);
            watchSb.AppendLine("[" + ms.ToString().PadLeft(5) + "ms] 收尾 state=" + st + " " + d);
            FinishWatchTest();
            return;
        }
        SampleAll(ms);
        // 每 200ms 记录一次判定结果，看清"桌面到底是不是独占屏幕"
        if (ms % 200 < 20)
        {
            string dz;
            int z = LayeredBox.SampleDesktopZ(out dz);
            watchSb.AppendLine("[" + ms.ToString().PadLeft(5) + "ms]   z=" + z + " " + dz);
        }
        // 显示桌面没生效就重试（测试环境里别的窗口可能干扰）
        if (ms > 800 && ms < 1000 && LayeredBox.SampleDesktopZ() != 1)
        {
            watchSb.AppendLine("[" + ms.ToString().PadLeft(5) + "ms]   显示桌面似乎没生效，重发一次");
            ShowDesktopTray();
        }
    }

    // 自测用：闪现计时（每拍 20ms）
    static int[] flashGapMs, flashOverMs, flashLast;

    // 自测用：事件时序追踪——还原那一刻到底哪个事件先到
    delegate void WinEventDelegate2(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate2 proc, uint pid, uint tid, uint flags);

    static WinEventDelegate2 traceProc;
    static IntPtr traceHook;
    static bool traceOn;
    static int traceT0, traceCount;
    const uint TRACE_OUTOFCONTEXT = 0x0000;
    const uint TRACE_SKIPOWNPROCESS = 0x0002;

    static void StartEventTrace()
    {
        if (traceHook != IntPtr.Zero) return;
        traceProc = new WinEventDelegate2(delegate (IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (!traceOn) return;
            if (idObject != 0 || idChild != 0) return;
            if (traceCount > 60) return;
            traceCount++;
            string cls = "";
            try { cls = LayeredBox.ClassNameOf(hwnd); } catch { }
            watchSb.AppendLine("    [+" + (Environment.TickCount - traceT0) + "ms] ev=0x" +
                ev.ToString("X4") + " cls=" + cls + " hwnd=0x" + hwnd.ToInt64().ToString("X"));
        });
        traceHook = SetWinEventHook(0x0001, 0x8018, IntPtr.Zero, traceProc, 0, 0,
            TRACE_OUTOFCONTEXT | TRACE_SKIPOWNPROCESS);
    }

    // 模拟“用户点开程序”：把所有被最小化的、有标题的窗口恢复出来
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);

    static int RestoreAMinimizedApp()
    {
        try
        {
            List<IntPtr> found = new List<IntPtr>();
            uint self = (uint)Process.GetCurrentProcess().Id;
            EnumWindows(delegate (IntPtr h, IntPtr p)
            {
                if (!IsWindowVisible(h)) return true;
                if (!IsIconic(h)) return true;
                if (GetWindowTextLength(h) == 0) return true;      // 无标题的多半是隐藏工具窗
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid == self) return true;
                found.Add(h);
                return true;
            }, IntPtr.Zero);
            // --nofg：模拟按 Win+D 还原窗口时“窗口回来了、但前台窗口没变”的真实情况。
            // 做法：先记下当前前台（显示桌面时是 Progman/任务栏），正常还原窗口，
            // 再把前台还给原来那个 —— SW_RESTORE 会顺手激活窗口，不还原前台就测不出问题。
            IntPtr prevFg = GetForegroundWindow();
            foreach (IntPtr h in found) ShowWindow(h, 9);           // SW_RESTORE
            if (found.Count > 0)
            {
                if (watchNoForeground)
                {
                    if (prevFg != IntPtr.Zero) SetForegroundWindow(prevFg);
                }
                else SetForegroundWindow(found[0]);
            }
            return found.Count;
        }
        catch { return 0; }
    }

    // COM 通路：只在进程内第一次调用时生效（之后静默无效），留作最后一次复原兜底
    static void ToggleDesktopEx()
    {
        try
        {
            Type t = Type.GetTypeFromProgID("Shell.Application");
            if (t == null) { watchSb.AppendLine("  !! 找不到 Shell.Application"); return; }
            object o = Activator.CreateInstance(t);
            t.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, o, null);
            Marshal.ReleaseComObject(o);
        }
        catch (Exception ex) { watchSb.AppendLine("  !! ToggleDesktop 失败: " + ex.Message); }
    }

    // 取屏幕上某一点的像素（判断"重叠区域画的是谁"最直接的证据）
    static Color PixelAt(int x, int y)
    {
        try
        {
            using (Bitmap b = new Bitmap(1, 1))
            {
                using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(x, y, 0, 0, new Size(1, 1));
                return b.GetPixel(0, 0);
            }
        }
        catch { return Color.Black; }
    }

    static void GrabScreen(string path)
    {
        try
        {
            Rectangle b = SystemInformation.VirtualScreen;
            using (Bitmap full = new Bitmap(b.Width, b.Height))
            {
                using (Graphics g = Graphics.FromImage(full)) g.CopyFromScreen(b.Left, b.Top, 0, 0, full.Size);
                // 存 50% 缩放：8MB 的整屏 PNG 又大又不好看，缩小后足够核对位置和渲染
                int w = Math.Max(1, full.Width / 2), h = Math.Max(1, full.Height / 2);
                using (Bitmap small = new Bitmap(w, h))
                using (Graphics g2 = Graphics.FromImage(small))
                {
                    g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g2.DrawImage(full, 0, 0, w, h);
                    small.Save(path, ImageFormat.Png);
                }
                // 再存一份每个盒子的 1:1 原尺寸特写：核对文字/图标清晰度只能看原像素
                for (int i = 0; i < forms.Count; i++)
                {
                    try
                    {
                        Rectangle br = forms[i].Bounds;
                        br.Intersect(b);
                        if (br.Width < 8 || br.Height < 8) continue;
                        using (Bitmap crop = new Bitmap(br.Width, br.Height))
                        {
                            using (Graphics gc = Graphics.FromImage(crop))
                                gc.CopyFromScreen(br.Left, br.Top, 0, 0, crop.Size);
                            string bp = Path.Combine(Path.GetDirectoryName(path),
                                Path.GetFileNameWithoutExtension(path) + "-box" + i + ".png");
                            crop.Save(bp, ImageFormat.Png);
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) { watchSb.AppendLine("  !! 截图失败: " + ex.Message); }
    }

    static void SampleAll(int ms)
    {
        for (int i = 0; i < forms.Count; i++)
        {
            string s = forms[i].StateDump();
            if (s != watchLast[i])
            {
                if (watchLast[i] != null)
                {
                    // 现在没有抬升/下沉了，这里改成数“被抬成 topmost”的次数（正常应当为 0）
                    if (s.Contains("topmost=True")) watchRaises++;
                    else watchSinks++;
                }
                watchLast[i] = s;
                watchSb.AppendLine("[" + ms.ToString().PadLeft(5) + "ms] box" + i + " " + s);
            }
            int k = forms[i].FlashKind();
            if (k != 0)
            {
                if (flashLast[i] != k)
                    watchSb.AppendLine("[" + ms.ToString().PadLeft(5) + "ms] box" + i +
                        (k == 1 ? " 闪现：被桌面盖住" : " 闪现：压在程序窗口上"));
                if (k == 1) flashGapMs[i] += 20; else flashOverMs[i] += 20;
            }
            flashLast[i] = k;
        }
    }

    static void FinishWatchTest()
    {
        try
        {
            if (watchTick != null) { watchTick.Stop(); watchTick.Dispose(); }
            watchSb.AppendLine("--- end ---  topmost 抬升次数=" + watchRaises + "  归位次数=" + watchSinks);
            for (int i = 0; i < forms.Count; i++)
                watchSb.AppendLine("  box" + i + " 闪现(被桌面盖住)=" + flashGapMs[i] + "ms  闪现(压住程序)=" + flashOverMs[i] + "ms");
            File.WriteAllText(Path.Combine(Store.BaseDir, "watchtest-report.txt"),
                watchSb.ToString(), System.Text.Encoding.UTF8);
        }
        catch { }
        // 别把桌面留在“显示桌面”状态，第二次按 Win+D 应该能复原画面
        ToggleDesktopBack("watchtest");
        try { Application.Exit(); } catch { Environment.Exit(0); }
    }

    // 维护开关：--probe  只读：把当前“显示桌面/有程序窗口”的判定结果写进 probe-report.txt
    static int RunProbe()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        string d;
        int st = LayeredBox.ProbeState(out d);
        sb.AppendLine("撒点 state=" + st + "  (1=桌面独显 2=有程序窗口 0=判断不了)");
        sb.AppendLine(d);
        string dz;
        int stz = LayeredBox.SampleDesktopZ(out dz);
        sb.AppendLine("z序 state=" + stz + "  " + dz);
        try { File.WriteAllText(Path.Combine(Store.BaseDir, "probe-report.txt"), sb.ToString(), System.Text.Encoding.UTF8); }
        catch { }
        return stz;
    }

    static string IsAdmin()
    {
        try
        {
            System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
            System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "是" : "否";
        }
        catch { return "未知"; }
    }

    static void Probe(System.Text.StringBuilder sb, string label, string dir)
    {
        try
        {
            string f = Path.Combine(dir, "_deskbox_probe_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp");
            File.WriteAllText(f, "x");
            bool written = File.Exists(f);
            bool deleted = true;
            try { File.Delete(f); } catch { deleted = false; }
            sb.AppendLine("· " + label + "：写入" + (written ? "正常" : "异常") + "，删除" + (deleted ? "正常" : "被拒绝"));
        }
        catch (Exception ex)
        {
            sb.AppendLine("· " + label + "：写入被拒绝（" + ex.GetType().Name + "）" + ex.Message);
        }
    }

    public static void Diagnose()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("DeskBox 写入权限诊断  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("程序目录：" + Store.BaseDir);
        sb.AppendLine("运行用户：" + Environment.UserName + "    管理员：" + IsAdmin());
        sb.AppendLine();
        Probe(sb, "程序目录 items", Store.ItemsRoot);
        Probe(sb, "系统 TEMP", Path.GetTempPath());
        Probe(sb, "桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        sb.AppendLine();

        string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try
        {
            string[] lnks = Directory.GetFiles(desk, "*.lnk");
            if (lnks.Length == 0)
            {
                sb.AppendLine("· 跨盘移动演练：桌面上没有 .lnk，跳过");
            }
            else
            {
                string copy = Path.Combine(desk, "_deskbox_move_probe.lnk");
                File.Copy(lnks[0], copy, true);
                string dest = Path.Combine(Store.ItemsRoot, "_deskbox_move_probe.lnk");
                try
                {
                    File.Move(copy, dest);
                    sb.AppendLine("· 跨盘移动演练（桌面 → 程序目录）：成功");
                    try { File.Delete(dest); } catch { }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("· 跨盘移动演练：失败 " + ex.GetType().Name + " — " + ex.Message);
                    try { if (File.Exists(copy)) File.Delete(copy); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("· 跨盘移动演练：准备失败 " + ex.GetType().Name + " — " + ex.Message);
        }

        string text = sb.ToString();
        Log.W(text);
        MessageBox.Show(text, "DeskBox 诊断");
    }

    // ---------- 迁移 ----------

    public static void RunMigrate(bool silentIfClean)
    {
        int pending = 0;
        foreach (DBox b in root.Boxes)
            foreach (DTab t in b.Tabs)
            {
                string dir = Store.TabDir(b, t);
                foreach (DItem it in t.Items)
                    if (string.IsNullOrEmpty(it.Path) || !Store.InDir(dir, it.Path)) pending++;
            }
        if (pending == 0)
        {
            if (!silentIfClean) MessageBox.Show("所有图标都已经在 DeskBox 自己的文件夹里了。", "DeskBox");
            return;
        }

        string msg = "DeskBox 现在会把盒子里的文件真正保存到自己的文件夹：\r\n"
            + Store.ItemsRoot + "\\<盒子>\\<子盒子>\\\r\n\r\n"
            + "检测到 " + pending + " 个图标还指向桌面或其他外部位置。\r\n"
            + "继续会把它们【剪切】到对应文件夹，桌面上对应的文件会消失。\r\n\r\n"
            + "会先把这些文件复制一份到 _migrate_backup\\ 作为备份。\r\n确定现在执行吗？";
        if (MessageBox.Show(msg, "DeskBox 整理文件", MessageBoxButtons.OKCancel) != DialogResult.OK) return;

        string backup = Store.BackupSources(root);
        try
        {
            if (File.Exists(Store.ConfigPath))
                File.Copy(Store.ConfigPath, Store.ConfigPath + ".before-migrate", true);
        }
        catch { }
        Store.MigrateResult res = Store.Migrate(root);
        res.BackupDir = backup;
        root.NoAutoMigrate = true;
        Store.Save(root);
        foreach (LayeredBox f in forms) f.AfterExternalDrop();
        IconCache.Clear();
        StartIconPrewarm(true);

        string report = "已搬进盒子 " + res.Moved + " 个，本来就在盒子里 " + res.Already + " 个";
        if (res.Copied > 0) report += "，同一文件在多个子盒子而复制了 " + res.Copied + " 份";
        if (res.Shared > 0) report += "，认领已有同名文件 " + res.Shared + " 个";
        report += "。";
        if (res.Kept > 0)
        {
            report += "\r\n\r\n有 " + res.Kept + " 个没能搬动，已原样保留（条目没有丢失）：";
            foreach (string n in res.KeptNames) report += "\r\n  · " + n;
        }
        if (!string.IsNullOrEmpty(backup)) report += "\r\n\r\n备份位置：\r\n" + backup;
        MessageBox.Show(report, "DeskBox 整理完成");
    }

    public static void Quit()
    {
        // 还开着的配色面板按「取消」还原：Quit 会 Store.Save，别把预览值当已保存
        try { StyleForm.CancelAllOpen(); } catch { }
        try { InlineEditor.CloseCurrent(false); } catch { }
        Store.Save(root);
        try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
        Application.Exit();
    }
}

// ==================== 原生 Windows 右键菜单 ====================
// 右键盒子里的文件 → 弹和资源管理器一模一样的菜单（打开方式 / 剪切 / 复制 /
// 创建快捷方式 / 发送到 / 属性 …），并把 DeskBox 自己的几项插在菜单底部。
//
// 关键点：
//  * 必须走 IShellFolder.GetUIObjectOf + IContextMenu，不能自己攒菜单；
//  * 子菜单（发送到 / 打开方式）靠 IContextMenu2/3.HandleMenuMsg 转发
//    WM_INITMENUPOPUP / WM_DRAWITEM / WM_MEASUREITEM，否则子菜单是空的；
//  * 菜单窗口（#32768）不是 topmost，会被钉在桌面的盒子盖住 —— 所以
//    用一个可以抢到前台的隐藏宿主窗口当 owner（菜单才能正常"点外面关掉"），
//    并且在 GuardMenuZ 里持续把它顶回最前。
static class ShellMenu
{
    public static bool Active;
    static MenuHost host;

    const int ID_BASE = 0x7000;     // 自定义项从这里起
    const int ID_TAB0 = 0x7100;     // "移动到其他标签" 的子项

    static readonly Guid IID_IShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
    static readonly Guid IID_IContextMenu = new Guid("000214E4-0000-0000-C000-000000000046");

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, ref uint eaten, out IntPtr pidl, ref uint attr);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint uFlags, out IntPtr pidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cchMax);
    }



    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public LayeredBox.POINT ptInvoke;
    }

    // 隐藏宿主窗口：能抢前台（菜单才会"点外面自动关"），又不会出现在 Alt+Tab 里
    class MenuHost : NativeWindow
    {
        public MenuHost()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "DeskBoxShellMenuHost";
            cp.X = -8; cp.Y = -8;
            cp.Width = 1; cp.Height = 1;
            cp.Style = unchecked((int)0x80000000) | 0x10000000;   // WS_POPUP | WS_VISIBLE
            cp.ExStyle = 0x00000080;                               // WS_EX_TOOLWINDOW
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            // 菜单弹出时立刻把它顶到最前（盒子是 topmost，会盖住菜单）。
            // 定时器在 TrackPopupMenuEx 里不一定被泵到，这里是最可靠的一处。
            if (Active && m.Msg == 0x0117) PinTopmost();
            base.WndProc(ref m);
        }
    }


    // 菜单开着时把 #32768 顶回最前（盒子自己是 topmost，会盖住菜单）
    public static void PinTopmost()
    {
        try
        {
            IntPtr m = host == null ? IntPtr.Zero : GetLastActivePopup(host.Handle);
            if (m == IntPtr.Zero || m == host.Handle) m = FindWindow("#32768", null);
            if (m == IntPtr.Zero) return;
            SetWindowPos(m, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }
        catch { }
    }

    public static IntPtr HostHandle { get { return host == null ? IntPtr.Zero : host.Handle; } }

    // 测试用：找当前弹出的菜单窗口（#32768）／往菜单窗口发消息
    public static IntPtr FindMenuWindow() { try { return FindWindow("#32768", null); } catch { return IntPtr.Zero; } }
    public static void PostToMenu(IntPtr h, int msg, IntPtr w, IntPtr l) { try { PostMessage(h, msg, w, l); } catch { } }

    public static void GetMenuRect(IntPtr h, out int l, out int t, out int r, out int b)
    {
        l = t = r = b = 0;
        try { RECT32 rc; if (GetWindowRect(h, out rc)) { l = rc.L; t = rc.T; r = rc.R; b = rc.B; } } catch { }
    }

    // 测试用：菜单窗口的可见性 / 矩形 / 样式
    public static string DescribeMenu(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(无)";
        try
        {
            RECT32 r;
            GetWindowRect(h, out r);
            int ex = GetWindowLong(h, -20);
            return "vis=" + (IsWindowVisible(h) ? 1 : 0) + " rect=" + r.L + "," + r.T + "," + r.R + "," + r.B +
                   " ex=0x" + ex.ToString("X");
        }
        catch (Exception ex) { return "describe fail " + ex.Message; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT32 { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT32 r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    // ---- 快速菜单：自己的 HMENU，只放固定几项 ----
    // 为什么要自己建菜单：`QueryContextMenu` 会把 WinRAR / 百度网盘 / 手机助手那些
    // 右键扩展全部加载并拼一遍（实测冷 844ms / 热 250ms，127 项），这块没法只挑一部分。
    // 现在的做法：右键只弹这个固定菜单（≈0ms），用户点了哪一项，再按"动词名"
    // 单独去调 shell（`InvokeCommand("copy")` 只走那一个处理程序，不再碰其它扩展）。
    const int QID_BASE = 1000;
    public static int ShowQuickMenu(Point screen, string[] labels, bool[] disabled)
    {
        IntPtr hMenu = IntPtr.Zero;
        try
        {
            int t0 = Environment.TickCount;
            if (host == null) host = new MenuHost();
            hMenu = CreatePopupMenu();
            for (int i = 0; i < labels.Length; i++)
            {
                if (string.IsNullOrEmpty(labels[i]))
                {
                    AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }
                bool off = disabled != null && i < disabled.Length && disabled[i];
                AppendMenu(hMenu, MF_STRING | (off ? MF_GRAYED : 0), (UIntPtr)(QID_BASE + i), labels[i]);
            }
            // 光标可能被上一次的 shell 扩展留成"忙碌"，这里强制复位成箭头
            try { Cursor.Current = Cursors.Default; } catch { }
            int buildMs = Environment.TickCount - t0;
            SetForegroundWindow(host.Handle);
            Active = true;
            Log.W("quickmenu 构建 " + buildMs + "ms 项数=" + labels.Length);
            int cmd;
            try
            {
                cmd = TrackPopupMenuEx(hMenu,
                    TPM_RETURNCMD | TPM_LEFTALIGN | TPM_TOPALIGN | TPM_RIGHTBUTTON,
                    screen.X, screen.Y, host.Handle, IntPtr.Zero);
            }
            finally
            {
                Active = false;
                PostMessage(host.Handle, 0x0000, IntPtr.Zero, IntPtr.Zero);
            }
            if (cmd < QID_BASE) return -1;
            return cmd - QID_BASE;
        }
        catch (Exception ex)
        {
            Log.W("quickmenu fail " + ex.Message);
            return -1;
        }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
        }
    }

    // ---- 按"动词名"执行 shell 命令（open / cut / copy / delete / properties / runas …）----
    // 不建菜单、不调 QueryContextMenu，所以不会加载任何右键扩展。
    public static int InvokeVerb(string[] paths, string verb)
    {
        if (paths == null || paths.Length == 0) return -1;
        IntPtr pcm = IntPtr.Zero;
        IShellFolder folder = null;
        IContextMenu cm = null;
        List<IntPtr> pidls = new List<IntPtr>();
        List<IntPtr> children = new List<IntPtr>();
        IntPtr verbPtr = IntPtr.Zero;
        try
        {
            string dir = Path.GetDirectoryName(paths[0]);
            Guid iidFolder = IID_IShellFolder;
            foreach (string p in paths)
            {
                if (!string.Equals(Path.GetDirectoryName(p), dir, StringComparison.OrdinalIgnoreCase)) continue;
                IntPtr pidl = IntPtr.Zero;
                uint attr = 0;
                if (SHParseDisplayName(p, IntPtr.Zero, out pidl, 0, out attr) != 0 || pidl == IntPtr.Zero) continue;
                IShellFolder f2;
                IntPtr child;
                if (SHBindToParent(pidl, ref iidFolder, out f2, out child) != 0 || f2 == null)
                {
                    Marshal.FreeCoTaskMem(pidl);
                    continue;
                }
                if (folder == null) folder = f2;
                pidls.Add(pidl);
                children.Add(child);
            }
            if (folder == null || children.Count == 0) return -2;

            Guid iidCm = IID_IContextMenu;
            if (folder.GetUIObjectOf(IntPtr.Zero, (uint)children.Count, children.ToArray(), ref iidCm, IntPtr.Zero, out pcm) != 0) return -3;
            cm = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);

            verbPtr = Marshal.StringToHGlobalAnsi(verb);
            CMINVOKECOMMANDINFOEX ici = new CMINVOKECOMMANDINFOEX();
            ici.cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX));
            ici.fMask = 0;
            ici.hwnd = IntPtr.Zero;
            ici.lpVerb = verbPtr;
            ici.nShow = 1;
            int hr = cm.InvokeCommand(ref ici);
            Log.W("shell verb " + verb + " hr=0x" + hr.ToString("X8") + " n=" + children.Count);
            return hr;
        }
        catch (Exception ex) { Log.W("shell verb fail " + verb + " " + ex.Message); return -9; }
        finally
        {
            if (verbPtr != IntPtr.Zero) Marshal.FreeHGlobal(verbPtr);
            if (cm != null) { try { Marshal.ReleaseComObject(cm); } catch { } }
            if (folder != null) { try { Marshal.ReleaseComObject(folder); } catch { } }
            if (pcm != IntPtr.Zero) { try { Marshal.Release(pcm); } catch { } }
            foreach (IntPtr p in pidls) { try { Marshal.FreeCoTaskMem(p); } catch { } }
        }
    }

    // 自测：直接按"动词名"执行一个 shell 动词（"copy" 不弹任何窗口，能验证
    // CMINVOKECOMMANDINFOEX 的结构体和 COM 调用是不是对的）
    public static int InvokeVerbByName(string path, string verb)
    {
        IntPtr pcm = IntPtr.Zero;
        IShellFolder folder = null;
        IContextMenu cm = null;
        IntPtr pidl = IntPtr.Zero;
        IntPtr verbPtr = IntPtr.Zero;
        try
        {
            Guid iidFolder = IID_IShellFolder;
            uint attr = 0;
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attr) != 0 || pidl == IntPtr.Zero) return -1;
            IntPtr child;
            if (SHBindToParent(pidl, ref iidFolder, out folder, out child) != 0 || folder == null) return -2;
            Guid iidCm = IID_IContextMenu;
            if (folder.GetUIObjectOf(IntPtr.Zero, 1, new IntPtr[] { child }, ref iidCm, IntPtr.Zero, out pcm) != 0) return -3;
            cm = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);

            verbPtr = Marshal.StringToHGlobalAnsi(verb);
            CMINVOKECOMMANDINFOEX ici = new CMINVOKECOMMANDINFOEX();
            ici.cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX));
            ici.fMask = 0;
            ici.hwnd = IntPtr.Zero;
            ici.lpVerb = verbPtr;
            ici.nShow = 1;
            int hr = cm.InvokeCommand(ref ici);
            Log.W("shellmenu invokeverb " + verb + " hr=0x" + hr.ToString("X8") + " cbSize=" + ici.cbSize);
            return hr;
        }
        catch (Exception ex) { Log.W("shellmenu invokeverb fail " + ex.Message); return -9; }
        finally
        {
            if (verbPtr != IntPtr.Zero) Marshal.FreeHGlobal(verbPtr);
            if (cm != null) { try { Marshal.ReleaseComObject(cm); } catch { } }
            if (folder != null) { try { Marshal.ReleaseComObject(folder); } catch { } }
            if (pcm != IntPtr.Zero) { try { Marshal.Release(pcm); } catch { } }
            if (pidl != IntPtr.Zero) { try { Marshal.FreeCoTaskMem(pidl); } catch { } }
        }
    }

    // 自测：只"构建"一次 shell 菜单（不显示），把每一步耗时拆开打印。
    // 用来判断右键慢到底是加载 shell 扩展（GetUIObjectOf / QueryContextMenu）还是别的。
    public static string MeasureBuild(string path)
    {
        StringBuilder sb = new StringBuilder();
        IntPtr pcm = IntPtr.Zero, pidl = IntPtr.Zero, hMenu = IntPtr.Zero;
        IShellFolder folder = null;
        IContextMenu cm = null;
        try
        {
            int t0 = Environment.TickCount;
            uint attr = 0;
            int hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attr);
            int t1 = Environment.TickCount;
            if (hr != 0 || pidl == IntPtr.Zero) return "SHParseDisplayName 失败 hr=" + hr;
            Guid iidFolder = IID_IShellFolder;
            IntPtr child;
            hr = SHBindToParent(pidl, ref iidFolder, out folder, out child);
            int t2 = Environment.TickCount;
            if (hr != 0 || folder == null) return "SHBindToParent 失败 hr=" + hr;
            Guid iidCm = IID_IContextMenu;
            hr = folder.GetUIObjectOf(IntPtr.Zero, 1, new IntPtr[] { child }, ref iidCm, IntPtr.Zero, out pcm);
            int t3 = Environment.TickCount;
            if (hr != 0 || pcm == IntPtr.Zero) return "GetUIObjectOf 失败 hr=" + hr;
            cm = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);
            int t4 = Environment.TickCount;
            sb.AppendLine("  parse=" + (t1 - t0) + "ms bind=" + (t2 - t1) + "ms uiobj=" + (t3 - t2) +
                          "ms（不再调用 QueryContextMenu，右键菜单只用固定项）");
            // 把前 40 项的文字列出来：确认是"文件菜单 + 一堆扩展"，而不是我们传错了 PIDL
            int total = GetMenuItemCount(hMenu);
            for (int i = 0; i < total && i < 40; i++)
            {
                StringBuilder t = new StringBuilder(128);
                int len = GetMenuString(hMenu, (uint)i, t, 128, 0x0400 /*MF_BYPOSITION*/);
                if (len > 0) sb.Append("    [" + i + "] " + t.ToString().Replace("&", "") + "\n");
            }
        }
        catch (Exception ex) { sb.AppendLine("  MeasureBuild 异常 " + ex.Message); }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (cm != null) { try { Marshal.ReleaseComObject(cm); } catch { } }
            if (folder != null) { try { Marshal.ReleaseComObject(folder); } catch { } }
            if (pcm != IntPtr.Zero) { try { Marshal.Release(pcm); } catch { } }
            if (pidl != IntPtr.Zero) { try { Marshal.FreeCoTaskMem(pidl); } catch { } }
        }
        return sb.ToString();
    }

    static void InvokeCommand(IContextMenu cm, int offset, IntPtr hwnd, Point screen)
    {
        try
        {
            CMINVOKECOMMANDINFOEX ici = new CMINVOKECOMMANDINFOEX();
            ici.cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX));
            ici.fMask = 0;
            ici.hwnd = hwnd;
            ici.lpVerb = (IntPtr)offset;      // MAKEINTRESOURCE(offset)
            ici.nShow = 1;                    // SW_SHOWNORMAL
            ici.ptInvoke.X = screen.X;
            ici.ptInvoke.Y = screen.Y;
            cm.InvokeCommand(ref ici);
        }
        catch (Exception ex) { Log.W("shellmenu invoke fail " + ex.Message); }
    }

    // ---- Win32 ----
    const uint MF_STRING = 0x00000000;
    const uint MF_SEPARATOR = 0x00000800;
    const uint MF_GRAYED = 0x00000001;
    const uint MF_POPUP = 0x00000010;
    const uint TPM_LEFTALIGN = 0x0000;
    const uint TPM_TOPALIGN = 0x0000;
    const uint TPM_RIGHTBUTTON = 0x0002;
    const uint TPM_RETURNCMD = 0x0100;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr pbc, out IntPtr pidl, uint attrIn, out uint attrOut);
    [DllImport("shell32.dll")]
    static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IShellFolder ppv, out IntPtr pidlLast);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetMenuString(IntPtr hMenu, uint item, StringBuilder text, int max, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool AppendMenu(IntPtr hMenu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")]
    static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string win);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
}

// ==================== 拖动时的"幽灵"跟随图 ====================
// 拖动文件时跟着鼠标走的小方块（半透明 + 图标 + 名字），
// 让"我正在搬这个东西"看得见。窗口带 WS_EX_TRANSPARENT，鼠标穿透，
// 不会干扰下面拖放目标的命中测试。
class DragGhost : Form
{
    Image icon;
    string label;

    DragGhost(Image ic, string text)
    {
        icon = ic;
        label = text ?? "";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(28, 30, 34);
        Opacity = 0.82;
        Size = new Size(104, 96);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT（鼠标穿透）
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        using (Pen p = new Pen(Color.FromArgb(90, Color.White), 1))
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        if (icon != null)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(icon, new Rectangle((Width - 44) / 2, 12, 44, 44));
        }
        using (StringFormat sf = new StringFormat())
        {
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 240, 244)))
                e.Graphics.DrawString(label, f, b, new RectangleF(4, 62, Width - 8, 28), sf);
        }
    }

    public static DragGhost Create(List<DItem> items)
    {
        if (items == null || items.Count == 0) return null;
        Image ic = null;
        try { ic = IconCache.Get(items[0].Path, items[0].Name, 128); } catch { }
        string text = items[0].Name;
        if (items.Count > 1) text += "  等 " + items.Count + " 项";
        DragGhost g = new DragGhost(ic, text);
        g.FollowCursor();
        g.Show();
        return g;
    }

    public void FollowCursor()
    {
        Point c = Cursor.Position;
        Location = new Point(c.X - Width / 2, c.Y - Height / 2);
    }
}
