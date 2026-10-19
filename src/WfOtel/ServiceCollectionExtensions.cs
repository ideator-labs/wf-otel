using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using WorkflowCore.Interface;
using WorkflowCore.Models.LifeCycleEvents;

namespace WfOtel;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers ActivitySource, SpanCollector, workflow/step OTel middleware,
    /// and a Console TracerProvider. Call after AddWorkflow().
    /// </summary>
    public static IServiceCollection AddWorkflowOpenTelemetry(
        this IServiceCollection services,
        string? spansFile = null)
    {
        var source = new ActivitySource(SpanCollector.SourceName);
        services.AddSingleton(source);
        services.AddSingleton<WorkflowActivityStore>();
        services.AddSingleton(_ => new SpanCollector(spansFile));

        // Real OpenTelemetry TracerProvider with Console exporter.
        // SpanCollector (ActivityListener) is what demo.sh asserts against.
        var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(SpanCollector.SourceName)
            .AddConsoleExporter()
            .Build();
        services.AddSingleton<TracerProvider>(tracer);

        services.AddWorkflowMiddleware<OtelWorkflowPostMiddleware>();
        services.AddWorkflowStepMiddleware<OtelStepMiddleware>();

        return services;
    }

    /// <summary>
    /// Hook host lifecycle so Terminated workflows still stop their parent span
    /// (PostWorkflow only runs on Complete).
    /// </summary>
    public static void UseWorkflowOpenTelemetry(this IWorkflowHost host, IServiceProvider sp)
    {
        // Force SpanCollector construction so the ActivityListener is attached.
        _ = sp.GetRequiredService<SpanCollector>();
        var store = sp.GetRequiredService<WorkflowActivityStore>();

        host.OnLifeCycleEvent += evt =>
        {
            if (evt is WorkflowTerminated or WorkflowCompleted)
                store.StopAndRemove(evt.WorkflowInstanceId);
        };
    }
}
