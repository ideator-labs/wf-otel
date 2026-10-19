#!/usr/bin/env bash
# Proves: OpenTelemetry on a real Workflow Core host produces a parent span for
# each workflow instance and a child span for each step. A failing step gets
# ActivityStatusCode.Error. Step spans share the workflow TraceId and ParentSpanId.
#   bash demo.sh           all checks must PASS, exit 0
#   bash demo.sh --break   step spans start without a parent, a check must FAIL, exit 1
set -euo pipefail
cd "$(dirname "$0")"
[[ -d "$HOME/.dotnet" ]] && export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
command -v jq >/dev/null || { echo "jq is required"; exit 2; }

OUT="$PWD/.demo"
SRC=src
rm -rf "$OUT" && mkdir -p "$OUT"

sabotage() {  # sabotage <file> <exact text to remove>
  grep -qF -- "$2" "$1" || { echo "sabotage target not found in $1"; exit 2; }
  F="$1" T="$2" python3 -c 'import os; p=os.environ["F"]; s=open(p).read(); open(p,"w").write(s.replace(os.environ["T"], "", 1))'
}

case "${1:-}" in
  "") ;;
  --break)
    cp -r src "$OUT/src" && rm -rf "$OUT"/src/*/bin "$OUT"/src/*/obj && SRC="$OUT/src"
    # Remove the parentContext argument so step Activities become roots.
    sabotage "$SRC/WfOtel/OtelStepMiddleware.cs" $',\n            parent?.Context ?? default'
    echo "BREAK MODE: step spans no longer pass parentContext. Expect a FAIL." ;;
  *) echo "usage: bash demo.sh [--break]"; exit 2 ;;
esac

FAILED=0
pass() { echo "CHECK PASS  $1"; }
fail() { echo "CHECK FAIL  $1"; FAILED=1; }

if dotnet build "$SRC/WfOtel.Demo" -c Release -o "$OUT/bin" -v quiet -nologo > "$OUT/build.log" 2>&1; then
  pass "1 build: WfOtel + demo compile against WorkflowCore 3.21.0 and OpenTelemetry 1.12.0"
else
  cat "$OUT/build.log"; fail "1 build"; echo "RESULT: FAIL"; exit 1
fi

export SPANS_FILE="$OUT/spans.jsonl" RESULTS_FILE="$OUT/summary.json"
# Console exporter writes OTel text to stdout; host logs + summary echo go to stderr.
dotnet "$OUT/bin/WfOtel.Demo.dll" > "$OUT/otel-console.log" 2> "$OUT/host.log"

if [[ ! -f "$SPANS_FILE" ]]; then
  fail "2 spans file missing"
  cat "$OUT/host.log" || true
  echo "RESULT: FAIL"; exit 1
fi
pass "2 spans file written to .demo/spans.jsonl"

has_span() {
  jq -s --arg n "$1" '[.[] | select(.name == $n)] | length' "$SPANS_FILE"
}

wf_ok_count="$(jq -s '[.[] | select(.name == "workflow:OrderFulfillment")] | length' "$SPANS_FILE")"
reserve_n="$(has_span "workflow.step:Reserve")"
charge_n="$(has_span "workflow.step:Charge")"
ship_n="$(has_span "workflow.step:Ship")"

if [[ "$wf_ok_count" -ge 2 && "$reserve_n" -ge 2 && "$charge_n" -ge 2 && "$ship_n" -ge 1 ]]; then
  pass "3 spans present: >=2 workflow, >=2 Reserve, >=2 Charge, >=1 Ship (success + fail runs)"
else
  fail "3 spans present (workflow=$wf_ok_count reserve=$reserve_n charge=$charge_n ship=$ship_n)"
fi

success_wf="$(jq -s '
  ( [.[] | select(.name == "workflow.step:Ship")] | .[0].parentSpanId ) as $pid
  | [.[] | select(.name == "workflow:OrderFulfillment" and .spanId == $pid)] | .[0]
' "$SPANS_FILE")"

if [[ "$(echo "$success_wf" | jq -r '.spanId // empty')" != "" ]]; then
  pass "4 success: workflow span is parent of Ship"
else
  success_wf="$(jq -s '[.[] | select(.name == "workflow:OrderFulfillment")] | .[0]' "$SPANS_FILE")"
  fail "4 success: workflow span is parent of Ship (got parentSpanId mismatch or missing)"
fi

ok_trace="$(echo "$success_wf" | jq -r '.traceId // empty')"
ok_span="$(echo "$success_wf" | jq -r '.spanId // empty')"

children_same_trace="$(jq -s --arg t "$ok_trace" --arg p "$ok_span" '
  [.[] | select(.parentSpanId == $p)] | length > 0 and (map(.traceId == $t) | all)
' "$SPANS_FILE")"

if [[ "$children_same_trace" == "true" ]]; then
  pass "5 success: step spans share TraceId with workflow span"
else
  fail "5 success: step spans share TraceId with workflow span"
fi

parented="$(jq -s --arg p "$ok_span" '
  ( [.[] | select(.name == "workflow.step:Reserve" and .parentSpanId == $p)] | length >= 1 )
  and ( [.[] | select(.name == "workflow.step:Charge" and .parentSpanId == $p)] | length >= 1 )
  and ( [.[] | select(.name == "workflow.step:Ship" and .parentSpanId == $p)] | length >= 1 )
' "$SPANS_FILE")"

if [[ "$parented" == "true" ]]; then
  pass "6 success: Reserve, Charge, Ship have ParentSpanId = workflow SpanId"
else
  fail "6 success: Reserve, Charge, Ship have ParentSpanId = workflow SpanId"
fi

fail_charge="$(jq -s '
  [.[] | select(.name == "workflow.step:Charge" and .status == "Error")] | length >= 1
' "$SPANS_FILE")"

if [[ "$fail_charge" == "true" ]]; then
  pass "7 fail: Charge span has status=Error"
else
  fail "7 fail: Charge span has status=Error"
fi

fail_parented="$(jq -s '
  ( [.[] | select(.name == "workflow.step:Charge" and .status == "Error")] | .[0].parentSpanId ) as $pid
  | $pid != null
    and ( [.[] | select(.name == "workflow.step:Reserve" and .parentSpanId == $pid and .status == "Ok")] | length >= 1 )
    and ( [.[] | select(.name == "workflow.step:Ship" and .parentSpanId == $pid)] | length == 0 )
' "$SPANS_FILE")"

if [[ "$fail_parented" == "true" ]]; then
  pass "8 fail: Reserve status=Ok under same parent, no Ship under that parent"
else
  fail "8 fail: Reserve status=Ok under same parent, no Ship under that parent"
fi

if [[ ! -f "$RESULTS_FILE" ]]; then
  fail "9 host summary missing"
else
  ok_status="$(jq -r '.success.status' "$RESULTS_FILE")"
  fail_status="$(jq -r '.fail.status' "$RESULTS_FILE")"
  if [[ "$ok_status" == "Complete" && "$fail_status" == "Terminated" ]]; then
    pass "9 host: success workflow Complete, fail workflow Terminated"
  else
    fail "9 host: success=$ok_status fail=$fail_status"
  fi
fi

echo "--- summary ---"
cat "$RESULTS_FILE" 2>/dev/null || true
echo
echo "--- spans ---"
jq -c '{name, status, traceId: .traceId[0:8], spanId: .spanId[0:8], parentSpanId: (.parentSpanId // "" | .[0:8])}' "$SPANS_FILE"

if [[ $FAILED -ne 0 ]]; then echo "RESULT: FAIL (raw output in $OUT/)"; exit 1; fi
echo "RESULT: PASS"
