using System.Diagnostics;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WfOtel;

/// <summary>
/// Ensures a parent workflow Activity exists, then wraps each step in a child
/// Activity parented under it. On exception sets ActivityStatusCode.Error.
/// </summary>
public sealed class OtelStepMiddleware : IWorkflowStepMiddleware
{
    private readonly ActivitySource _source;
    private readonly WorkflowActivityStore _store;

    public OtelStepMiddleware(ActivitySource source, WorkflowActivityStore store)
    {
        _source = source;
        _store = store;
    }

    public async Task<ExecutionResult> HandleAsync(
        IStepExecutionContext context,
        IStepBody body,
        WorkflowStepDelegate next)
    {
        var stepName = context.Step.Name
            ?? context.Step.Id.ToString();
        var workflowId = context.Workflow.Id;
        var definition = context.Workflow.WorkflowDefinitionId;

        var parent = EnsureWorkflowActivity(workflowId, definition, context.Workflow.Version);

        // PARENT_LINK: StartActivity with parentContext so the step span nests
        // under the workflow span. demo.sh --break deletes this argument list.
        using var activity = _source.StartActivity(
            $"workflow.step:{stepName}",
            ActivityKind.Internal,
            parent?.Context ?? default);

        if (activity != null)
        {
            activity.SetTag("workflow.id", workflowId);
            activity.SetTag("workflow.definition", definition);
            activity.SetTag("step.id", context.Step.Id);
            activity.SetTag("step.name", stepName);
        }

        try
        {
            var result = await next();
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("exception.type", ex.GetType().FullName);
            activity?.SetTag("exception.message", ex.Message);
            parent?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private Activity? EnsureWorkflowActivity(string workflowId, string definition, int version)
    {
        if (_store.TryGet(workflowId, out var existing) && existing != null)
            return existing;

        // Start as a root span (clear ambient parent briefly).
        var prev = Activity.Current;
        Activity.Current = null;
        var activity = _source.StartActivity(
            $"workflow:{definition}",
            ActivityKind.Internal);
        Activity.Current = prev;

        if (activity == null)
            return null;

        activity.SetTag("workflow.id", workflowId);
        activity.SetTag("workflow.definition", definition);
        activity.SetTag("workflow.version", version);
        _store.Set(workflowId, activity);
        return activity;
    }
}
