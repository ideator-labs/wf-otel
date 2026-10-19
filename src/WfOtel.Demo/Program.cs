using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkflowCore.Interface;
using WorkflowCore.Models;
using WfOtel;
using WfOtel.Demo;

var spansFile = Environment.GetEnvironmentVariable("SPANS_FILE")
    ?? Path.Combine(".demo", "spans.jsonl");
var resultsFile = Environment.GetEnvironmentVariable("RESULTS_FILE")
    ?? Path.Combine(".demo", "summary.json");

var services = new ServiceCollection();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddConsole());
services.AddWorkflow();
services.AddWorkflowOpenTelemetry(spansFile);
services.AddTransient<ReserveStep>();
services.AddTransient<ChargeStep>();
services.AddTransient<ShipStep>();

await using var sp = services.BuildServiceProvider();
var host = sp.GetRequiredService<IWorkflowHost>();
host.RegisterWorkflow<OrderFulfillmentWorkflow, OrderData>();
host.UseWorkflowOpenTelemetry(sp);
host.Start();

async Task<(string id, WorkflowStatus status)> RunAsync(string orderId, bool failCharge)
{
    var data = new OrderData { OrderId = orderId, FailCharge = failCharge };
    var id = await host.StartWorkflow("OrderFulfillment", 1, data);
    WorkflowStatus status = WorkflowStatus.Runnable;
    for (var i = 0; i < 100; i++)
    {
        await Task.Delay(50);
        var inst = await host.PersistenceStore.GetWorkflowInstance(id);
        if (inst == null) continue;
        status = inst.Status;
        if (status is WorkflowStatus.Complete or WorkflowStatus.Terminated)
            break;
    }
    // Give lifecycle handlers a beat to stop the parent span.
    await Task.Delay(100);
    return (id, status);
}

var (okId, okStatus) = await RunAsync("order-ok", failCharge: false);
var (failId, failStatus) = await RunAsync("order-fail", failCharge: true);

// Flush any leftover open activities (safety net).
sp.GetRequiredService<WorkflowActivityStore>().StopAll();
await Task.Delay(50);

host.Stop();
sp.GetService<OpenTelemetry.Trace.TracerProvider>()?.Dispose();

var summary = new
{
    success = new { workflowId = okId, status = okStatus.ToString() },
    fail = new { workflowId = failId, status = failStatus.ToString() },
    spansFile
};
var json = JsonSerializer.Serialize(summary);
File.WriteAllText(resultsFile, json);
// Also echo to stderr so humans can see it without Console exporter noise on stdout.
Console.Error.WriteLine(json);
