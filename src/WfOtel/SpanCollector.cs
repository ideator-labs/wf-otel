using System.Diagnostics;
using System.Text.Json;

namespace WfOtel;

/// <summary>
/// Listens to Activities from the WfOtel ActivitySource and appends one JSON
/// object per stopped span to the file at SPANS_FILE (or .demo/spans.jsonl).
/// </summary>
public sealed class SpanCollector : IDisposable
{
    public const string SourceName = "WfOtel";

    private readonly ActivityListener _listener;
    private readonly string _path;
    private readonly object _gate = new();

    public SpanCollector(string? path = null)
    {
        _path = path
            ?? Environment.GetEnvironmentVariable("SPANS_FILE")
            ?? Path.Combine(".demo", "spans.jsonl");
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(_path))
            File.Delete(_path);

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnStopped
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private void OnStopped(Activity activity)
    {
        var attrs = new Dictionary<string, string?>();
        foreach (var tag in activity.TagObjects)
            attrs[tag.Key] = tag.Value?.ToString();

        var record = new Dictionary<string, object?>
        {
            ["traceId"] = activity.TraceId.ToString(),
            ["spanId"] = activity.SpanId.ToString(),
            ["parentSpanId"] = activity.ParentSpanId == default
                ? null
                : activity.ParentSpanId.ToString(),
            ["name"] = activity.DisplayName,
            ["kind"] = activity.Kind.ToString(),
            ["status"] = activity.Status.ToString(),
            ["attrs"] = attrs
        };

        var line = JsonSerializer.Serialize(record);
        lock (_gate)
        {
            File.AppendAllText(_path, line + "\n");
        }
    }

    public void Dispose() => _listener.Dispose();
}
