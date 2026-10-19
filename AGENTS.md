# AGENTS.md

Handoff for a coding agent with no prior context on this repo.

## What this proves

One claim: register OpenTelemetry on a real Workflow Core host, and a completed workflow produces a parent span for the instance plus a child span for each step. A failing step produces a span with `ActivityStatusCode.Error`. Step spans share the workflow `TraceId` and set `ParentSpanId` to the workflow span.

It runs on the real `WorkflowCore` 3.21.0, `OpenTelemetry` 1.12.0, and `OpenTelemetry.Exporter.Console` 1.12.0 NuGet packages. Spans are collected with an `ActivityListener` into `.demo/spans.jsonl` so `demo.sh` can assert with `jq`. No paid APIs, no network after the NuGet restore.

Why it matters. Workflow Core has step middleware and lifecycle events, but no first-party OpenTelemetry instrumentation. Operators who already ship OTel for ASP.NET and HttpClient get a blank spot where durable workflow runs should be. Sibling engines (Temporal, Durable Task, Dapr workflows) expose workflow and activity spans. This POC shows the shape on WC with public APIs only.

## Status

| Piece | Real or stubbed |
|---|---|
| Workflow host, step middleware, error Terminate, in-memory persistence | Real. NuGet `WorkflowCore` 3.21.0. |
| `ActivitySource` / `Activity` parenting and status | Real. `System.Diagnostics` via OpenTelemetry SDK. |
| Console exporter | Real. NuGet `OpenTelemetry.Exporter.Console` 1.12.0. Writes to stdout (captured as `.demo/otel-console.log`). |
| `SpanCollector` JSONL | Real. `ActivityListener` on source `WfOtel`, one line per stopped span. |
| `OtelStepMiddleware` / `OtelWorkflowPostMiddleware` / `AddWorkflowOpenTelemetry` | Real, in `src/WfOtel`. Uses only public Workflow Core middleware APIs. |
| Order workflow | Toy. `Reserve`, `Charge`, `Ship`. Charge throws when `FailCharge` is true. |

`bash demo.sh` passes 9 of 9 checks on a fresh clone. `bash demo.sh --break` fails checks 4, 5, 6 and 8 and exits 1.

## Data shape

```
WorkflowSpan (one JSONL line in .demo/spans.jsonl)
  TraceId          string
  SpanId           string
  ParentSpanId     string?     null for the workflow root span
  Name             string      "workflow:{DefinitionId}" or "workflow.step:{StepName}"
  Kind             string      Internal
  Status           string      Unset | Ok | Error
  Attrs            object      workflow.id, workflow.definition, step.id, step.name, ...

OtelStepMiddleware : IWorkflowStepMiddleware
  EnsureWorkflowActivity(workflowId) starts the parent if missing
  StartActivity("workflow.step:{name}", Internal, parent.Context)
  On success: Status=Ok
  On exception: Status=Error, tag exception.*, rethrow

OtelWorkflowPostMiddleware : IWorkflowMiddleware (PostWorkflow)
  Sets Status=Ok if still Unset, then stops the parent Activity

Lifecycle: OnLifeCycleEvent WorkflowTerminated | WorkflowCompleted also stops the parent
  (PostWorkflow only runs on Complete, not Terminate)
```

Demo summary (`.demo/summary.json`):

```json
{"success":{"workflowId":"...","status":"Complete"},"fail":{"workflowId":"...","status":"Terminated"},"spansFile":"..."}
```

## Repo map

```
demo.sh                         the proof. Builds, runs success+fail, prints CHECK lines
src/WfOtel/
  SpanCollector.cs              ActivityListener -> SPANS_FILE JSONL
  WorkflowActivityStore.cs      open parent Activity per workflow instance id
  OtelStepMiddleware.cs         child spans + error status
  OtelWorkflowMiddleware.cs     PostWorkflow stop
  ServiceCollectionExtensions.cs AddWorkflowOpenTelemetry / UseWorkflowOpenTelemetry
src/WfOtel.Demo/
  Workflows.cs                  OrderFulfillmentWorkflow (Reserve, Charge, Ship)
  Program.cs                    runs order-ok then order-fail, writes summary.json
```

## Setup, run, verify (fresh clone)

Prereqs: .NET 8 SDK (`dotnet --list-sdks` shows 8.x), `jq`, and `python3` (break mode only). If the SDK lives in `~/.dotnet`, `demo.sh` puts it on PATH. The first build needs NuGet access.

```bash
git clone https://github.com/ideator-labs/wf-otel.git
cd wf-otel
bash demo.sh
```

Expected: nine `CHECK PASS` lines, the summary, a span table, then `RESULT: PASS`, exit 0. About 10 seconds after the first restore.

```
CHECK PASS  1 build: WfOtel + demo compile against WorkflowCore 3.21.0 and OpenTelemetry 1.12.0
CHECK PASS  2 spans file written to .demo/spans.jsonl
CHECK PASS  3 spans present: >=2 workflow, >=2 Reserve, >=2 Charge, >=1 Ship (success + fail runs)
CHECK PASS  4 success: workflow span is parent of Ship
CHECK PASS  5 success: step spans share TraceId with workflow span
CHECK PASS  6 success: Reserve, Charge, Ship have ParentSpanId = workflow SpanId
CHECK PASS  7 fail: Charge span has status=Error
CHECK PASS  8 fail: Reserve status=Ok under same parent, no Ship under that parent
CHECK PASS  9 host: success workflow Complete, fail workflow Terminated
RESULT: PASS
```

Break it on purpose:

```bash
bash demo.sh --break
```

It copies `src/` to `.demo/src/`, deletes `, parent?.Context ?? default` from the copy of `OtelStepMiddleware.cs`, then builds and runs the copy. Step spans become roots (empty `ParentSpanId`, different `TraceId`s). Checks 4, 5, 6 and 8 fail. Exit 1. The script exits 2 if the sabotage string is not found, so it can never silently do nothing. The real `src/` is never touched.

Raw output lands in `.demo/` (`spans.jsonl`, `summary.json`, `otel-console.log`, `host.log`, `build.log`).

## Design decisions

- **Lazy parent on first step, not PreWorkflow.** `RunPreMiddleware` runs before `CreateNewWorkflow`, so `WorkflowInstance.Id` is null. Starting the parent inside `OtelStepMiddleware` once the id exists avoids that race.
- **Parent by ActivityContext, not Ambient.** Step spans pass `parent.Context` into `StartActivity` so parenting does not depend on `Activity.Current` surviving across WC's async executor.
- **Assert against JSONL, not Console exporter text.** Console output is hard to parse. `SpanCollector` writes structured lines. The Console exporter still runs to prove a real OTel pipeline is wired.
- **Stop Terminated parents via lifecycle.** `PostWorkflow` only runs when status becomes `Complete`. `UseWorkflowOpenTelemetry` also stops on `WorkflowTerminated` / `WorkflowCompleted`.
- **Public middleware only.** No private WC hooks. `IWorkflowStepMiddleware` + `IWorkflowMiddleware` + `IWorkflowHost.OnLifeCycleEvent`.

## Limits

- Workflow parent span starts at the first step, not at `StartWorkflow`. Time before the first step is not in the span.
- In-memory persistence only. No cross-process or cross-host trace continuation.
- Linear toy workflow only. Containers (`Parallel`, `If`, `Saga`) and wait/event steps are untested.
- No OTLP exporter, no baggage / trace context propagation into HttpClient calls from steps.
- Console exporter noise goes to `.demo/otel-console.log`; checks never read it.
- Not a NuGet package yet.

## Next steps (in priority order)

1. **Start at StartWorkflow.** Assign a correlation id in PreWorkflow (or set `Reference`), start the parent once `CreateNewWorkflow` returns the id. Done when a check shows the parent `StartTime` is before the first step and still parents all steps.
2. **OTLP.** Add `OpenTelemetry.Exporter.OpenTelemetryProtocol` and an optional env endpoint. Done when a local collector receives the same span tree the JSONL shows.
3. **Containers.** A workflow with `Parallel` and a failing branch. Done when each branch step is a child of the workflow span and the failing one has `Error`.
4. **HttpClient propagation.** A step that calls HttpClient with `AddHttpClientInstrumentation`. Done when the outgoing client span parents under the step span.
5. **Package or core PR.** Ship `src/WfOtel` as a NuGet, or propose first-party OTel hooks upstream. Done when a fresh console app referencing only the package passes checks 2 to 9.

## Working rules

- `bash demo.sh` is the source of truth. Keep it passing and keep `--break` failing.
- Every new claim gets a CHECK line that reads `.demo/spans.jsonl` (or host status) and a sabotage that makes it fail.
- Demo machine-readable output is `summary.json` and `spans.jsonl`. Logs go to stderr / otel-console.log.
- Keep `WorkflowCore` at 3.21.x unless a step needs a newer API.
- Commit as `danielgerlag <daniel@gerlag.ca>`.
- Plain prose. No em-dashes.

## Sources

- Workflow Core: https://github.com/danielgerlag/workflow-core
- Workflow middleware docs: https://workflow-core.readthedocs.io/en/latest/workflow-middleware/
- WorkflowCore 3.21.0 on NuGet: https://www.nuget.org/packages/WorkflowCore/3.21.0
- OpenTelemetry .NET: https://github.com/open-telemetry/opentelemetry-dotnet
- OpenTelemetry 1.12.0 on NuGet: https://www.nuget.org/packages/OpenTelemetry/1.12.0
- Sibling POCs: https://github.com/ideator-labs/wf-rewind , https://github.com/ideator-labs/approval-deadline
