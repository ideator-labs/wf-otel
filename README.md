# WfOtel

OpenTelemetry spans for [Workflow Core](https://github.com/danielgerlag/workflow-core): one parent span per workflow instance, one child span per step, error status on failing steps.

```bash
bash demo.sh          # must PASS
bash demo.sh --break  # must FAIL (parenting sabotaged)
```

Agent handoff, data shape, limits, and next steps: [AGENTS.md](AGENTS.md).
