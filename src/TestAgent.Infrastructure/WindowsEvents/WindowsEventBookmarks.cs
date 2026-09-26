using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

internal sealed class WindowsEventBookmarks(AppPaths paths, string workspaceId)
{
    private const int MaxBookmarkBytes = 16384;

    public string? Load(WindowsEventQuery query)
    {
        var path = ResolvePath(query);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaxBookmarkBytes)
            throw new InvalidDataException("事件监控书签文件超出允许大小。");
        return Normalize(File.ReadAllText(path, Encoding.UTF8), query.Channel);
    }

    public void Save(WindowsEventQuery query, string bookmarkXml)
    {
        var content = Normalize(bookmarkXml, query.Channel);
        var path = ResolvePath(query);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Forget(WindowsEventQuery query)
    {
        var path = ResolvePath(query);
        if (File.Exists(path)) File.Delete(path);
    }

    private string ResolvePath(WindowsEventQuery query)
    {
        var filter = WindowsEventQueryPolicy.BuildXPath(query with { BeforeRecordId = null });
        var identity = workspaceId + "\n" + query.Channel + "\n" + filter;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(paths.Root, "windows-events", hash + ".bookmark.xml");
    }

    public static long ReadRecordId(string xml, string channel)
    {
        if (Encoding.UTF8.GetByteCount(xml) > MaxBookmarkBytes)
            throw new InvalidDataException("事件监控书签过大。");
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBookmarkBytes });
        var root = XDocument.Load(reader).Root;
        var bookmark = root?.Elements("Bookmark").SingleOrDefault();
        if (root?.Name != "BookmarkList" || root.Elements().Count() != 1 || bookmark is null ||
            bookmark.HasElements || bookmark.Attributes().Any(x => x.Name.LocalName is not ("Channel" or "RecordId" or "IsCurrent")) ||
            (string?)bookmark.Attribute("Channel") != channel ||
            !long.TryParse((string?)bookmark.Attribute("RecordId"), NumberStyles.None, CultureInfo.InvariantCulture, out var record) || record <= 0)
            throw new InvalidDataException("事件监控书签格式或频道不匹配。");
        return record;
    }

    private static string Normalize(string xml, string channel)
    {
        try
        {
            var id = ReadRecordId(xml, channel);
            return new XElement("BookmarkList", new XElement("Bookmark", new XAttribute("Channel", channel),
                new XAttribute("RecordId", id.ToString(CultureInfo.InvariantCulture)), new XAttribute("IsCurrent", "true")))
                .ToString(SaveOptions.DisableFormatting);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        { throw new InvalidDataException("事件监控书签格式无效。"); }
    }
}
