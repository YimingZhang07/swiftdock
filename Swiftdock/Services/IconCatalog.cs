using System.Windows.Media;

namespace Swiftdock.Services;

public sealed record LibraryGlyph(int CodePoint, string Name, string Group)
{
    public string Text => char.ConvertFromUtf32(CodePoint);
}
public sealed record IconColor(string Name, string Hex);

public static class IconCatalog
{
    // Windows font glyphs only; configuration membership never depends on the installed font.
    public static IReadOnlyList<LibraryGlyph> Glyphs { get; } = Array.AsReadOnly<LibraryGlyph>([
        new(0xE943, "代码", "应用 / 开发"), new(0xE977, "设备连接", "应用 / 开发"),
        new(0xE756, "终端", "应用 / 开发"), new(0xEE94, "数据库", "应用 / 开发"),
        new(0xE968, "服务器", "应用 / 开发"), new(0xE71D, "应用", "应用 / 开发"),
        new(0xE950, "设备", "应用 / 开发"), new(0xEBE8, "调试", "应用 / 开发"),
        new(0xE774, "网页", "网络"), new(0xE701, "无线网络", "网络"),
        new(0xE753, "云", "网络"),
        new(0xE896, "下载", "网络"), new(0xE898, "上传", "网络"), new(0xE71B, "链接", "网络"),
        new(0xE715, "邮件", "沟通"), new(0xE8F2, "聊天", "沟通"),
        new(0xE717, "电话", "沟通"), new(0xE714, "视频会议", "沟通"), new(0xE716, "联系人", "沟通"),
        new(0xE8D6, "音乐", "媒体"), new(0xE8B2, "视频", "媒体"), new(0xEB9F, "图片", "媒体"),
        new(0xE722, "相机", "媒体"), new(0xE7FC, "游戏", "媒体"), new(0xE768, "播放", "媒体"),
        new(0xE7F6, "耳机", "媒体"), new(0xE720, "麦克风", "媒体"),
        new(0xE8A5, "文档", "办公"), new(0xE70B, "笔记", "办公"), new(0xE80A, "表格", "办公"),
        new(0xE787, "日历", "办公"), new(0xE917, "时钟", "办公"), new(0xE8EF, "计算器", "办公"),
        new(0xE82D, "书", "办公"), new(0xE749, "打印", "办公"), new(0xE73E, "待办", "办公"),
        new(0xE8B7, "文件夹", "文件"), new(0xE7B8, "压缩包", "文件"), new(0xE728, "收藏", "文件"),
        new(0xE735, "星标", "文件"), new(0xE8EC, "标签", "文件"), new(0xE723, "附件", "文件"),
        new(0xE713, "设置", "系统"), new(0xE90F, "工具", "系统"), new(0xEA18, "盾牌", "系统"),
        new(0xE72E, "锁", "系统"), new(0xE8D7, "钥匙", "系统"), new(0xE7E8, "电源", "系统"),
        new(0xE721, "搜索", "系统"), new(0xE80F, "主页", "系统"),
        new(0xE7BF, "购物", "生活"), new(0xE8C7, "钱包", "生活"), new(0xE800, "地图", "生活"),
        new(0xE706, "天气", "生活"), new(0xEB52, "心形", "生活"), new(0xE7C1, "旗帜", "生活"),
        new(0xEA80, "灯泡", "生活"), new(0xF133, "礼物", "生活"), new(0xE707, "位置", "生活"),
        new(0xE709, "出行", "生活")
    ]);

    public static IReadOnlyList<IconColor> Colors { get; } = Array.AsReadOnly<IconColor>([
        new("蓝", "#2F6FDB"), new("青", "#0F7B6C"), new("紫", "#5B4FD1"), new("橙", "#C2410C"),
        new("琥珀", "#B7791F"), new("粉", "#B83280"), new("绿", "#3F7D20"), new("灰", "#4B5563")
    ]);

    private static readonly Lazy<(string Name, GlyphTypeface? Face)> Font = new(() =>
        FindTypeface("Segoe Fluent Icons") is { } fluent ? ("Segoe Fluent Icons", fluent)
        : ("Segoe MDL2 Assets", FindTypeface("Segoe MDL2 Assets")));
    public static string FontName => Font.Value.Name;
    public static GlyphTypeface? Typeface => Font.Value.Face;
    public static IEnumerable<LibraryGlyph> AvailableGlyphs => Glyphs.Where(g => Typeface?.CharacterToGlyphMap.ContainsKey(g.CodePoint) == true);

    public static GlyphTypeface? FindTypeface(string name)
    {
        // Looking up an absent FontFamily by name alone can silently substitute a different font.
        var family = Fonts.SystemFontFamilies.FirstOrDefault(f => f.Source.Equals(name, StringComparison.OrdinalIgnoreCase)
            || f.FamilyNames.Values.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)));
        return family?.GetTypefaces().Select(t => t.TryGetGlyphTypeface(out var face) ? face : null).FirstOrDefault(f => f is not null);
    }
}
