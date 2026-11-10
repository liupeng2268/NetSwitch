using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NetSwitch.Infrastructure;

/// <summary>
/// 与 PowerShell 5.1 <c>ConvertTo-Json -Depth 12</c> 输出格式逐字对齐的读写器。
///
/// 为什么要自己写：PS 5.1 的 JSON 格式很反常，标准序列化器无法产出同样字节：
///   <code>
///   {
///       "version":  1,                      ← 冒号后有【两个】空格
///       "profiles":  [                      ← 数组括号紧跟在 "profiles":  之后
///                        {                  ← 元素缩进 = 括号所在列 + 4
///                            "name":  "x"
///                        }
///                    ]
///   }
///   </code>
/// 括号所在列 = 属性缩进 + 属性名长度 + 5（即 <c>"name":  </c> 这串的长度）。
/// 因此元素缩进会随属性名长短而变化，不是固定倍数。
///
/// 实测结论：PS 5.1 对该格式是【幂等】的 —— 读入再写出与原文件逐字节相同。
/// 复刻它可保证「记事本里看到的格式不变」「git 差异干净」「新旧版可互相接管」。
///
/// 另有两条已从真实文件确认的行为：
///   • 空列表写成 <c>null</c>（不是 <c>[]</c>）
///   • 文件带 UTF-8 BOM、换行为 CRLF、结尾无换行
/// </summary>
public static class PsJson
{
    private const int IndentStep = 4;
    private const string KeySuffix = ":  ";   // 冒号后两个空格，PS 特有
    private const int KeySuffixLen = 3;       // ':' + 两个空格

    // ── 读 ────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>读取 JSON 文本，自动处理 BOM，失败返回 null（不抛异常）。</summary>
    public static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var text = StripBom(json);
            return JsonSerializer.Deserialize<T>(text, ReadOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>去掉可能存在的 BOM 字符。</summary>
    public static string StripBom(string text)
        => text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

    /// <summary>按 PS 的行为读文件：UTF-8、去 BOM。文件不存在返回 null。</summary>
    public static string? TryReadAllText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return StripBom(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    // ── 写 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把配置对象序列化成 PS 5.1 风格的 JSON 文本（不含 BOM）。
    /// 属性顺序固定为 PS 版的声明顺序，与现有文件保持一致。
    /// </summary>
    public static string Serialize(Models.ConfigFile data)
    {
        var sb = new StringBuilder(4096);
        sb.Append("{\r\n");

        var top = new List<(string Key, Action<StringBuilder, int> Write)>
        {
            ("version",   (b, c) => b.Append(data.Version.ToString(CultureInfo.InvariantCulture))),
            ("updatedAt", (b, c) => WriteString(b, data.UpdatedAt)),
            ("profiles",  (b, c) => WriteProfiles(b, data.Profiles, c)),
        };
        WriteMembers(sb, top, 0);

        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>把路由记录数组序列化（PS 风格，带 BOM 由调用方决定）。</summary>
    public static string SerializeRoutes(List<Models.RouteRecord> routes)
    {
        var sb = new StringBuilder(512);
        if (routes.Count == 0)
        {
            sb.Append("[]");
            return sb.ToString();
        }

        sb.Append("[\r\n");
        for (var i = 0; i < routes.Count; i++)
        {
            var r = routes[i];
            const int elemIndent = IndentStep;
            sb.Append(' ', elemIndent).Append("{\r\n");
            var members = new List<(string, Action<StringBuilder, int>)>
            {
                ("interfaceIndex",    (b, c) => b.Append(r.InterfaceIndex.ToString(CultureInfo.InvariantCulture))),
                ("destinationPrefix", (b, c) => WriteString(b, r.DestinationPrefix)),
                ("nextHop",           (b, c) => WriteString(b, r.NextHop)),
            };
            WriteMembers(sb, members, elemIndent);
            sb.Append(' ', elemIndent).Append('}');
            if (i < routes.Count - 1) sb.Append(',');
            sb.Append("\r\n");
        }
        sb.Append(']');
        return sb.ToString();
    }

    // ── 内部：对象与数组的排版 ─────────────────────────────────────────────

    /// <summary>
    /// 按列缩进写出一个对象的全部成员。
    /// <paramref name="bracketCol"/> 是该对象左花括号所在的列，属性从 +4 列开始。
    /// </summary>
    private static void WriteMembers(
        StringBuilder sb, List<(string Key, Action<StringBuilder, int> Write)> members, int bracketCol)
    {
        var propCol = bracketCol + IndentStep;
        for (var i = 0; i < members.Count; i++)
        {
            var (key, writeValue) = members[i];
            sb.Append(' ', propCol);
            WriteString(sb, key);
            sb.Append(KeySuffix);

            // 值的「起始列」＝ 属性列 + "key":  这串的长度；数组/对象的括号就落在这里
            var valueCol = propCol + key.Length + 2 + KeySuffixLen;
            writeValue(sb, valueCol);

            if (i < members.Count - 1) sb.Append(',');
            sb.Append("\r\n");
        }
    }

    private static void WriteProfiles(StringBuilder sb, List<Models.Profile> profiles, int bracketCol)
    {
        if (profiles.Count == 0)
        {
            sb.Append("null");
            return;
        }

        sb.Append("[\r\n");
        var elemCol = bracketCol + IndentStep;
        for (var i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            sb.Append(' ', elemCol).Append("{\r\n");
            var members = new List<(string, Action<StringBuilder, int>)>
            {
                ("name",     (b, c) => WriteString(b, p.Name)),
                ("remark",   (b, c) => WriteString(b, p.Remark)),
                ("adapters", (b, c) => WriteAdapters(b, p.Adapters, c)),
            };
            WriteMembers(sb, members, elemCol);
            sb.Append(' ', elemCol).Append('}');
            if (i < profiles.Count - 1) sb.Append(',');
            sb.Append("\r\n");
        }
        sb.Append(' ', bracketCol).Append(']');
    }

    private static void WriteAdapters(StringBuilder sb, List<Models.AdapterConfig> adapters, int bracketCol)
    {
        if (adapters.Count == 0)
        {
            sb.Append("null");
            return;
        }

        sb.Append("[\r\n");
        var elemCol = bracketCol + IndentStep;
        for (var i = 0; i < adapters.Count; i++)
        {
            var a = adapters[i];
            sb.Append(' ', elemCol).Append("{\r\n");
            var members = new List<(string, Action<StringBuilder, int>)>
            {
                ("adapter",       (b, c) => WriteString(b, a.Adapter)),
                ("enabled",       (b, c) => b.Append(a.Enabled ? "true" : "false")),
                ("mode",          (b, c) => WriteString(b, a.Mode)),
                ("ip",            (b, c) => WriteString(b, a.Ip)),
                ("mask",          (b, c) => WriteString(b, a.Mask)),
                ("dhcpDns",       (b, c) => b.Append(a.DhcpDns ? "true" : "false")),
                ("defaultMetric", (b, c) => b.Append(a.DefaultMetric?.ToString(CultureInfo.InvariantCulture) ?? "null")),
                ("gateways",      (b, c) => WriteStringList(b, a.Gateways, c)),
                ("dns",           (b, c) => WriteStringList(b, a.Dns, c)),
                ("routes",        (b, c) => WriteStringList(b, a.Routes, c)),
            };

            // 两个新增键是「可选键」：仅当模型里显式设置过（HasValue）才加入成员列表。
            // 旧配置文件缺这两个键 → 反序列化后为 null → 列表恰好仍是原来 10 个成员，
            // 顺序 / 缩进 / 逗号 / defaultMetric 无条件写 null 全部不变，Repair→Serialize 逐字节一致。
            if (a.DhcpStaticCoexist.HasValue)
            {
                members.Add(("dhcpStaticCoexist", (b, c) => b.Append(a.DhcpStaticCoexist.Value ? "true" : "false")));
            }
            if (a.Ipv6Enabled.HasValue)
            {
                members.Add(("ipv6Enabled", (b, c) => b.Append(a.Ipv6Enabled.Value ? "true" : "false")));
            }

            WriteMembers(sb, members, elemCol);
            sb.Append(' ', elemCol).Append('}');
            if (i < adapters.Count - 1) sb.Append(',');
            sb.Append("\r\n");
        }
        sb.Append(' ', bracketCol).Append(']');
    }

    /// <summary>
    /// 写字符串列表。空列表按 PS 的实际行为写成 <c>null</c>（真实 profiles.json 可证），
    /// 非空则每行一个元素。
    /// </summary>
    private static void WriteStringList(StringBuilder sb, List<string>? list, int bracketCol)
    {
        if (list is null || list.Count == 0)
        {
            sb.Append("null");
            return;
        }

        sb.Append("[\r\n");
        var elemCol = bracketCol + IndentStep;
        for (var i = 0; i < list.Count; i++)
        {
            sb.Append(' ', elemCol);
            WriteString(sb, list[i]);
            if (i < list.Count - 1) sb.Append(',');
            sb.Append("\r\n");
        }
        sb.Append(' ', bracketCol).Append(']');
    }

    /// <summary>
    /// 写字符串字面量。中文与常见符号直出（PS 不转义），
    /// 仅转义 JSON 规范要求的最小集。
    /// </summary>
    private static void WriteString(StringBuilder sb, string? value)
    {
        sb.Append('"');
        if (!string.IsNullOrEmpty(value))
        {
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (ch < 0x20)
                        {
                            sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(ch);
                        }
                        break;
                }
            }
        }
        sb.Append('"');
    }
}

/// <summary>
/// 文件写入策略：集中管理编码与 BOM 规则，避免各处口径不一。
///
/// 规则来自旧版实测：
///   • profiles.json / 备份 / 日志 → UTF-8 <b>带</b> BOM（记事本能正确识别中文）
///   • applied-routes.json        → UTF-8 <b>无</b> BOM
/// </summary>
public static class FileIo
{
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>带 BOM 写入（配置文件、备份、日志）。</summary>
    public static void WriteAllTextWithBom(string path, string content)
    {
        EnsureDirectory(path);
        File.WriteAllText(path, content, Utf8Bom);
    }

    /// <summary>不带 BOM 写入（applied-routes.json）。</summary>
    public static void WriteAllTextNoBom(string path, string content)
    {
        EnsureDirectory(path);
        File.WriteAllText(path, content, Utf8NoBom);
    }

    /// <summary>追加一行（日志用），带 BOM 语义由文件首次创建决定。</summary>
    public static void AppendLine(string path, string line)
    {
        EnsureDirectory(path);
        File.AppendAllText(path, line + "\r\n", Utf8Bom);
    }

    private static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
