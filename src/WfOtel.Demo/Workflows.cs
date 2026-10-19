using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WfOtel.Demo;

public class OrderData
{
    public string OrderId { get; set; } = "";
    public bool FailCharge { get; set; }
}

public class ReserveStep : StepBody
{
    public string OrderId { get; set; } = "";

    public override ExecutionResult Run(IStepExecutionContext context)
    {
        Console.Error.WriteLine($"Reserve ok order={OrderId}");
        return ExecutionResult.Next();
    }
}

public class ChargeStep : StepBody
{
    public string OrderId { get; set; } = "";
    public bool FailCharge { get; set; }

    public override ExecutionResult Run(IStepExecutionContext context)
    {
        if (FailCharge)
            throw new InvalidOperationException($"charge declined for {OrderId}");
        Console.Error.WriteLine($"Charge ok order={OrderId}");
        return ExecutionResult.Next();
    }
}

public class ShipStep : StepBody
{
    public string OrderId { get; set; } = "";

    public override ExecutionResult Run(IStepExecutionContext context)
    {
        Console.Error.WriteLine($"Ship ok order={OrderId}");
        return ExecutionResult.Next();
    }
}

public class OrderFulfillmentWorkflow : IWorkflow<OrderData>
{
    public string Id => "OrderFulfillment";
    public int Version => 1;

    public void Build(IWorkflowBuilder<OrderData> builder)
    {
        builder
            .StartWith<ReserveStep>()
                .Input(s => s.OrderId, d => d.OrderId)
                .Name("Reserve")
            .Then<ChargeStep>()
                .Input(s => s.OrderId, d => d.OrderId)
                .Input(s => s.FailCharge, d => d.FailCharge)
                .Name("Charge")
                .OnError(WorkflowErrorHandling.Terminate)
            .Then<ShipStep>()
                .Input(s => s.OrderId, d => d.OrderId)
                .Name("Ship");
    }
}
