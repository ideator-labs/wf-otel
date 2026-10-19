using System.Diagnostics;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WfOtel;

/// <summary>
/// Stops the parent workflow Activity after a successful Complete.
/// The parent Activity itself is started lazily on the first step
/// (PreWorkflow runs before CreateNewWorkflow, so WorkflowInstance.Id is null there).
/// </summary>
public sealed class OtelWorkflowPostMiddleware : IWorkflowMiddleware
{
    private readonly WorkflowActivityStore _store;

    public OtelWorkflowPostMiddleware(WorkflowActivityStore store) => _store = store;

    public WorkflowMiddlewarePhase Phase => WorkflowMiddlewarePhase.PostWorkflow;

    public Task HandleAsync(WorkflowInstance workflow, WorkflowDelegate next)
    {
        if (_store.TryGet(workflow.Id, out var activity) && activity != null
            && activity.Status == ActivityStatusCode.Unset)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
        _store.StopAndRemove(workflow.Id);
        return next();
    }
}
