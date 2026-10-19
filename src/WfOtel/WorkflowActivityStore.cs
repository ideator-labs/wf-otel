using System.Collections.Concurrent;
using System.Diagnostics;

namespace WfOtel;

/// <summary>
/// Holds the open parent Activity for each running workflow instance so step
/// spans can parent under it. Keyed by WorkflowInstance.Id.
/// </summary>
public sealed class WorkflowActivityStore
{
    private readonly ConcurrentDictionary<string, Activity> _open = new();

    public void Set(string workflowId, Activity activity) =>
        _open[workflowId] = activity;

    public bool TryGet(string workflowId, out Activity? activity) =>
        _open.TryGetValue(workflowId, out activity);

    public Activity? StopAndRemove(string workflowId)
    {
        if (!_open.TryRemove(workflowId, out var activity))
            return null;
        activity.Stop();
        return activity;
    }

    public void StopAll()
    {
        foreach (var id in _open.Keys.ToArray())
            StopAndRemove(id);
    }
}
