using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

/// <summary>
/// Groups a bounded, already selected local snapshot without I/O, model calls or message parsing.
/// Time proximity is only a candidate association; it cannot establish a process, service or cause.
/// </summary>
public sealed class WindowsIncidentCorrelator : IWindowsIncidentCorrelator
{
    public const int MaxInputEvents = 200;
    public static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(5);

    public IReadOnlyList<WindowsIncident> Build(IReadOnlyList<WindowsEventItem> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count > MaxInputEvents)
            throw new InvalidOperationException($"一次最多关联 {MaxInputEvents} 条事件，请缩小查询范围。");

        var unique = new Dictionary<string, WindowsIncidentEvidence>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (item is null || item.Channel is not ("Application" or "System") || item.RecordId <= 0)
                throw new InvalidDataException("只支持有记录 ID 的本机 Application/System 事件。");
            var evidence = new WindowsIncidentEvidence(item.Channel, item.RecordId, item.EventId,
                WindowsEventPrivacy.Sanitize(item.Provider, 128), item.Level,
                item.Timestamp?.ToUniversalTime());
            var key = EvidenceKey(evidence);
            if (unique.TryGetValue(key, out var existing) && existing != evidence)
                throw new InvalidDataException("同一事件引用的元数据不一致，请重新查询后关联。");
            unique[key] = evidence;
        }

        var pending = unique.Values.OrderBy(item => item.Timestamp is null).ThenBy(item => item.Timestamp)
            .ThenBy(item => item.Channel, StringComparer.Ordinal).ThenBy(item => item.RecordId)
            .ThenBy(item => item.EventId).ThenBy(item => item.Provider, StringComparer.Ordinal).ToList();
        var incidents = new List<WindowsIncident>();
        while (pending.Count > 0)
        {
            var anchor = pending[0];
            pending.RemoveAt(0);
            var group = new List<WindowsIncidentEvidence> { anchor };
            var category = Classify(anchor);
            if (anchor.Timestamp is { } from)
            {
                // Anchor to the first event, rather than chaining adjacent events indefinitely.
                for (var index = 0; index < pending.Count;)
                {
                    var item = pending[index];
                    if (item.Timestamp is not { } timestamp || timestamp - from > CorrelationWindow) break;
                    var compatible = category != EventCategory.Other
                        ? Classify(item) == category
                        : item.Channel == anchor.Channel && item.EventId == anchor.EventId &&
                          string.Equals(item.Provider, anchor.Provider, StringComparison.OrdinalIgnoreCase);
                    if (compatible)
                    {
                        group.Add(item);
                        pending.RemoveAt(index);
                    }
                    else index++;
                }
            }
            incidents.Add(Create(group, category));
        }
        return incidents.AsReadOnly();
    }

    private static WindowsIncident Create(List<WindowsIncidentEvidence> evidence, EventCategory category)
    {
        var first = evidence[0];
        var signatures = evidence.GroupBy(item => (item.Channel, Provider: item.Provider.ToUpperInvariant(), item.EventId))
            .ToArray();
        var candidate = signatures.Length > 1;
        var repeated = signatures.Any(group => group.Count() > 1);
        var facts = new List<string> { $"当前输入包含 {evidence.Count} 条唯一事件；每条证据仅归属一个分组。" };
        foreach (var signature in signatures)
        {
            var sample = signature.First();
            facts.Add($"{sample.Channel} / {sample.Provider} / Event ID {sample.EventId}：{signature.Count()} 条。");
        }
        var hypotheses = new List<string>();
        var steps = new List<string>();
        string title;
        string confidence;
        if (first.Timestamp is null)
        {
            title = $"缺少时间的单条事件 · {first.EventId}";
            facts.Add("该事件没有时间戳，已单独保留，不参与时间窗口关联。");
            hypotheses.Add("无法从缺失时间戳判断先后关系或重复时间窗口。");
            confidence = "单条证据；关联未知";
        }
        else if (candidate)
        {
            title = category == EventCategory.ApplicationFailure
                ? $"应用异常候选关联 · {evidence.Count} 条事件"
                : $"服务控制管理器候选关联 · {evidence.Count} 条事件";
            facts.Add("这些事件位于同一个不超过 5 分钟的窗口内。时间相近不证明因果关系。");
            hypotheses.Add(category == EventCategory.ApplicationFailure
                ? "这些来源可能描述相关应用异常，但证据中没有用于匹配的进程身份，不能确定属于同一进程或同一次故障。"
                : "这些记录可能描述相关服务异常，但未匹配服务身份，不能确定属于同一服务或同一次故障。");
            confidence = "仅候选关联；原因未验证";
        }
        else if (repeated)
        {
            title = $"重复事件 · {first.Provider} / {first.EventId} · {evidence.Count} 次";
            facts.Add("同一频道、来源和事件 ID 在不超过 5 分钟内重复出现；次数只反映当前输入。");
            hypotheses.Add("重复事件可能来自不同进程、服务或操作；尚未验证共同原因。");
            confidence = "重复模式已确认；原因未验证";
        }
        else
        {
            title = category == EventCategory.ServiceFailure
                ? $"服务控制管理器事件 · {first.EventId}"
                : $"单条事件 · {first.Provider} / {first.EventId}";
            hypotheses.Add("当前仅有单条证据，不能确认重复故障、共同原因或影响范围。");
            confidence = "单条证据；原因未验证";
        }

        if (category == EventCategory.ApplicationFailure)
            steps.Add("只读核对事件详情中的应用名、模块、进程标识和时间；身份吻合后再评估是否为同一次异常。");
        if (category == EventCategory.ServiceFailure)
            steps.Add("只读核对事件中的服务名称，并查看该服务当前状态及相邻事件；不重启或修改服务。");
        steps.Add("在事件中心查看原记录与前后时间范围，确认是否存在采集缺口、过滤或日志轮转。");
        steps.Add("如需模型分析，先选择并检查脱敏证据，再手动发送；本地分组没有调用模型或执行修复。");

        var identity = string.Join("\n", evidence.Select(EvidenceKey).Order(StringComparer.Ordinal));
        var id = "INC-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return new(id, title, first.Timestamp, evidence[^1].Timestamp,
            evidence.Select(item => item.Level is >= 1 and <= 5 ? item.Level : 4).Min(),
            evidence.AsReadOnly(), facts.AsReadOnly(), hypotheses.AsReadOnly(), steps.AsReadOnly(), confidence);
    }

    private static string EvidenceKey(WindowsIncidentEvidence item) =>
        item.Channel + "|" + item.RecordId.ToString(CultureInfo.InvariantCulture) + "|" +
        (item.Timestamp?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "unknown");

    private static EventCategory Classify(WindowsIncidentEvidence item)
    {
        if (item.Channel == "Application" &&
            ((item.EventId == 1000 && ProviderIs(item, "Application Error")) ||
             (item.EventId == 1026 && ProviderIs(item, ".NET Runtime")) ||
             (item.EventId == 1001 && ProviderIs(item, "Windows Error Reporting"))))
            return EventCategory.ApplicationFailure;
        if (item.Channel == "System" && ProviderIs(item, "Service Control Manager") &&
            item.EventId is 7000 or 7001 or 7023 or 7031 or 7034)
            return EventCategory.ServiceFailure;
        return EventCategory.Other;
    }

    private static bool ProviderIs(WindowsIncidentEvidence item, string provider) =>
        string.Equals(item.Provider, provider, StringComparison.OrdinalIgnoreCase);

    private enum EventCategory { Other, ApplicationFailure, ServiceFailure }
}
