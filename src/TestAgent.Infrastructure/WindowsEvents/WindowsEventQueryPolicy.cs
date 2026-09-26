using System.Globalization;
using System.Text.RegularExpressions;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

public static partial class WindowsEventQueryPolicy
{
    public static WindowsEventQuery Validate(WindowsEventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Channel is not ("Application" or "System"))
            throw new ArgumentException("仅支持本机 Application 和 System 频道。", nameof(query));
        if (query.LookbackMinutes is < 1 or > 10080)
            throw new ArgumentException("查询时间范围必须为 1 到 10080 分钟。", nameof(query));
        if (query.MaxEvents is < 1 or > 200)
            throw new ArgumentException("每次读取条数必须为 1 到 200。", nameof(query));
        if (query.EventId is < 0 or > 65535 || query.MaximumLevel is < 1 or > 5 || query.BeforeRecordId is <= 0)
            throw new ArgumentException("事件 ID、级别或分页位置无效。", nameof(query));
        var provider = string.IsNullOrWhiteSpace(query.Provider) ? null : query.Provider.Trim();
        if (provider is not null && (provider.Length > 128 || !ProviderName().IsMatch(provider)))
            throw new ArgumentException("来源名称仅支持字母、数字、空格、点、下划线、连字符和花括号，最多 128 个字符。", nameof(query));
        return query with { Provider = provider };
    }

    public static string BuildXPath(WindowsEventQuery query)
    {
        query = Validate(query);
        var clauses = new List<string>
        {
            "TimeCreated[timediff(@SystemTime) >= 0 and timediff(@SystemTime) <= " +
            ((long)query.LookbackMinutes * 60000).ToString(CultureInfo.InvariantCulture) + "]"
        };
        if (query.EventId is { } id) clauses.Add($"EventID={id.ToString(CultureInfo.InvariantCulture)}");
        if (query.MaximumLevel is { } level) clauses.Add($"(Level >= 1 and Level <= {level.ToString(CultureInfo.InvariantCulture)})");
        if (query.Provider is { } provider) clauses.Add($"Provider[@Name='{provider}']");
        if (query.BeforeRecordId is { } record) clauses.Add($"EventRecordID < {record.ToString(CultureInfo.InvariantCulture)}");
        return "*[System[" + string.Join(" and ", clauses) + "]]";
    }

    [GeneratedRegex(@"\A[\p{L}\p{N} ._{}\-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderName();
}
