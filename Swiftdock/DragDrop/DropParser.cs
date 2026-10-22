using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Windows;
using Swiftdock.Models;
using Swiftdock.Services;

namespace Swiftdock.DragDrop;

public sealed record DropSnapshot(string[] Formats, string[] Paths, string? Text, string? Title);
public sealed record DropResolution(IReadOnlyList<LaunchItem> Items, IReadOnlyList<string> Errors);

public static class DropParser
{
    public const int MaxPayloadBytes = 1024 * 1024;
    private static readonly string[] UrlFormats = ["UniformResourceLocatorW", "text/x-moz-url", "UniformResourceLocator"];

    public static bool Supports(IDataObject data)
    {
        try { return data.GetDataPresent(DataFormats.FileDrop, false) || data.GetDataPresent(DataFormats.UnicodeText, false) || UrlFormats.Any(f => data.GetDataPresent(f, false)); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException) { return false; }
    }

    // Called on the OLE/UI thread; only copies source-owned data, never touches destination files.
    public static DropSnapshot Capture(IDataObject data)
    {
        string[] formats = data.GetFormats(false);
        if (data.GetDataPresent(DataFormats.FileDrop, false) && data.GetData(DataFormats.FileDrop, false) is string[] paths)
        {
            if (paths.Length > 256 || paths.Sum(p => (long)p.Length) > MaxPayloadBytes / 2)
                throw new InvalidDataException("一次最多添加 256 个项目，且拖入数据不能超过 1MB。");
            return new(formats, paths.ToArray(), null, null);
        }
        string? title = null;
        if (data.GetDataPresent("FileGroupDescriptorW", false))
        {
            try { title = DescriptorTitle(ReadBytes(data.GetData("FileGroupDescriptorW", false))); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Runtime.InteropServices.ExternalException) { }
        }
        foreach (string format in UrlFormats)
        {
            if (!data.GetDataPresent(format, false)) continue;
            string text;
            try
            {
                var value = data.GetData(format, false);
                text = value is string str ? str : DecodeUrl(ReadBytes(value), format != "UniformResourceLocator");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or System.Runtime.InteropServices.ExternalException) { continue; }
            if (text.Length > MaxPayloadBytes / 2) throw new InvalidDataException("拖入文本过长。");
            if (format == "text/x-moz-url")
            {
                var lines = text.Split('\n');
                text = lines[0].Trim();
                if (lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1])) title = lines[1].Trim();
            }
            text = text.Split('\0')[0].TrimStart('\uFEFF').Trim();
            if (LaunchService.IsWebUrl(text)) return new(formats, [], text, title);
        }
        string? plain = data.GetData(DataFormats.UnicodeText, false) as string;
        if (plain?.Length > MaxPayloadBytes / 2) throw new InvalidDataException("拖入文本过长。");
        return new(formats, [], plain?.Trim(), title);
    }

    private static byte[] ReadBytes(object? value)
    {
        if (value is byte[] bytes)
        {
            if (bytes.Length > MaxPayloadBytes) throw new InvalidDataException("拖入数据过大。");
            return bytes.ToArray();
        }
        if (value is not Stream source) return [];
        long? original = source.CanSeek ? source.Position : null;
        try
        {
            if (source.CanSeek) source.Position = 0;
            using var copy = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (copy.Length + read > MaxPayloadBytes) throw new InvalidDataException("拖入数据过大。");
                copy.Write(buffer, 0, read);
            }
            return copy.ToArray();
        }
        finally { if (original.HasValue) source.Position = original.Value; }
    }

    public static string DecodeUrl(byte[] bytes, bool unicode)
    {
        if (bytes.Length > MaxPayloadBytes || (unicode && bytes.Length % 2 != 0))
            throw new InvalidDataException("网址字节流长度无效。");
        // ANSI URL payloads are accepted only when ASCII; Unicode/text fallback is preferred.
        if (!unicode && bytes.Any(b => b > 127)) throw new InvalidDataException("请用 Unicode 网址或纯文本拖入。");
        return (unicode ? new UnicodeEncoding(false, false, true).GetString(bytes) : Encoding.ASCII.GetString(bytes)).Split('\0')[0].TrimStart('\uFEFF');
    }

    public static string? DescriptorTitle(byte[] bytes)
    {
        const int descriptorSize = 592;
        if (bytes.Length < 4) return null;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count == 0 || count > 256 || bytes.Length < 4L + count * descriptorSize) return null;
        string name = Encoding.Unicode.GetString(bytes, 4 + 72, 520).Split('\0')[0];
        return name.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ? name[..^4] : null;
    }

    public static Task<DropResolution> ResolveAsync(DropSnapshot snapshot, CancellationToken token) => Task.Run(async () =>
    {
        var items = new List<LaunchItem>();
        var errors = new List<string>();
        var paths = snapshot.Paths;
        if (paths.Length == 0 && snapshot.Text is { } text)
        {
            if (LaunchService.IsWebUrl(text))
                return new DropResolution([new(snapshot.Title ?? new Uri(text).Host, text, LaunchKind.Url)], []);
            string candidate = Environment.ExpandEnvironmentVariables(text.Trim('"'));
            if (!Path.IsPathFullyQualified(candidate)) return new DropResolution([], ["只接受 http/https 网址或绝对文件路径。"]);
            paths = [candidate];
        }
        foreach (var original in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string path = Environment.ExpandEnvironmentVariables(original);
                if (Directory.Exists(path))
                    items.Add(new(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path, ItemIdentity.PortablePath(path), LaunchKind.Folder));
                else if (!File.Exists(path)) errors.Add($"目标不存在或无法访问：{path}");
                else if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
                    if (reader.BaseStream.Length > 65536) throw new InvalidDataException(".url 文件过大。");
                    var content = await reader.ReadToEndAsync(token);
                    string? url = content.Split('\n').Select(line => line.Trim())
                        .FirstOrDefault(line => line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..].Trim();
                    if (url is null || !LaunchService.IsWebUrl(url)) throw new InvalidDataException(".url 不包含有效 http/https 网址。");
                    items.Add(new(Path.GetFileNameWithoutExtension(path), url, LaunchKind.Url));
                }
                else
                {
                    bool app = new[] { ".exe", ".lnk", ".bat", ".cmd" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        try { if (FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description) name = description; }
                        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
                    }
                    items.Add(new(name, ItemIdentity.PortablePath(path), app ? LaunchKind.App : LaunchKind.File));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { errors.Add(ex.Message); }
        }
        token.ThrowIfCancellationRequested();
        return new DropResolution(items, errors);
    }, token);
}
