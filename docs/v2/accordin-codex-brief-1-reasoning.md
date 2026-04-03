# AccordIn — Brief 1: Reasoning Metadata Layer

## Step 0 — Commit first

```bash
git add -A
git commit -m "checkpoint: before reasoning metadata layer"
git push origin main
```

---

## Overview

This brief adds structured reasoning metadata to every recommendation, cadence, and action
in the plan JSON. It covers three layers:

1. System prompt update — add reasoning block to the JSON schema and classification rules
2. Plugin model update — add ReasoningBlock class to PlanResponse.cs
3. Hub update — render reason type badge, confidence indicator, evidence count, and enhanced
   info popover

The Dataverse MCP is available. Use it to update the wrl_IntentPrompt record directly.

---

## Part A — Update the cross-sell system prompt via Dataverse MCP

Use the Dataverse MCP to find and update the cross-sell intent prompt record.

Query: find the record in wrl_intentprompt where wrl_intenttype = "cross-sell"

Update wrl_systemprompt with the content below. This is a full replacement — replace the
entire existing value with the text that follows.

The new prompt is identical to the existing one with these additions:

### Addition 1 — New section before SECTION 8 (RESPONSE FORMAT)

Insert this as the new SECTION 7 (shift existing Section 7 General Rules to Section 8,
Response Format to Section 9, Few-Shot Examples to Section 10):

```
SECTION 7 - REASONING METADATA RULES
Every recommendation, cadence, and action must include a reasoning block.

reasonType: classify as exactly one of:
  - "data" — directly supported by a named signal, activity date, engagement event,
    or pipeline stage in the account data
  - "pattern" — inferred from a combination of signals that together suggest a pattern
    (e.g. three consecutive quarters of growth + formal pricing request = buying momentum)
  - "best_practice" — no direct signals; based on industry best practice for this
    account's sector and deal stage

confidenceScore: derive from evidence strength:
  - "high" — 2+ direct data signals supporting this item, or Negotiation/Propose stage
  - "medium" — 1 direct signal, or Qualify stage with engagement indicators
  - "low" — Discovery stage, no activity, or best_practice basis only

evidenceCount: integer — count of distinct signals, activities, or data points that
directly support this item. Set to 0 for best_practice items.

signals: array of the specific evidence items. Each signal has:
  - id: short identifier e.g. "signal_1", "activity_1", "stage_1"
  - summary: one sentence naming the specific data point

explanation: one sentence explaining WHY this item was included and what the reasoning
block adds beyond the rationale field. This should complete the sentence
"This was included because..."
```

### Addition 2 — Add reasoning block to JSON schema in RESPONSE FORMAT section

In the recommendations array schema, after "confidenceReason", add:

```json
"reasoning": {
  "reasonType": "data|pattern|best_practice",
  "confidenceScore": "high|medium|low",
  "evidenceCount": 2,
  "signals": [
    {
      "id": "signal_1",
      "summary": "string — one sentence naming the specific data point"
    }
  ],
  "explanation": "string"
}
```

Add the same reasoning block to the cadences array schema (after "rationale") and to the
oneOffActions array schema (after "rationale").

### Addition 3 — Add reasoning example to each few-shot example

In EXAMPLE 1, add this to the KEY PATTERNS section:

```
- recommendations[0].reasoning.reasonType = "data", evidenceCount = 2,
  signals reference the 2026-02-10 demo and 2026-02-18 case study click
- cadences[0].reasoning.reasonType = "data", confidenceScore = "high" (primary relationship,
  High engagement)
- oneOffActions[0].reasoning.reasonType = "best_practice" if no specific signal exists
```

In EXAMPLE 2 (minimal data), add:

```
- all reasoning.reasonType = "best_practice", evidenceCount = 0, signals = []
- confidenceScore = "low" for all items
```

---

## Part B — Update PlanResponse.cs

File: `accordin-plugin/AccordIn.Plugin/Models/PlanResponse.cs`

Read the file first. Add a ReasoningBlock class and a ReasoningSignal class. Add a
Reasoning property to the recommendation, cadence, and action item classes.

```csharp
public class ReasoningSignal
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; }
}

public class ReasoningBlock
{
    [JsonPropertyName("reasonType")]
    public string ReasonType { get; set; }        // "data" | "pattern" | "best_practice"

    [JsonPropertyName("confidenceScore")]
    public string ConfidenceScore { get; set; }   // "high" | "medium" | "low"

    [JsonPropertyName("evidenceCount")]
    public int EvidenceCount { get; set; }

    [JsonPropertyName("signals")]
    public List<ReasoningSignal> Signals { get; set; } = new List<ReasoningSignal>();

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; }
}
```

Add to RecommendationItem class:

```csharp
[JsonPropertyName("reasoning")]
public ReasoningBlock Reasoning { get; set; }
```

Add the same property to CadenceItem and ActionItem (or whatever the action class is named).

Rebuild the project. Verify 0 errors.

---

## Part C — Update account-planning-hub-v3.html

File: `power-platform/web-resources/accordin-hub/account-planning-hub-v3.html`

Read the full file before making any changes.

### C1 — Add CSS for reasoning badges

Add these rules in the style block after the existing `.conf-b` rules:

```css
/* Reason type badges */
.rt-badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-size: 9px;
  font-weight: 700;
  padding: 1px 6px;
  border-radius: 3px;
  text-transform: uppercase;
  letter-spacing: .05em;
  white-space: nowrap;
}
.rt-data         { background: #eff6ff; color: #2463eb; border: 1px solid #bfdbfe; }
.rt-pattern      { background: #f0fdf4; color: #15803d; border: 1px solid #bbf7d0; }
.rt-best-practice{ background: #f8fafc; color: #64748b; border: 1px solid #e2e8f0; }

/* Evidence count */
.evidence-count {
  font-size: 10px;
  color: var(--text-muted);
  font-style: italic;
}

/* Override marker */
.override-marker {
  font-size: 9px;
  font-weight: 600;
  color: var(--amber);
  background: var(--amber-pale);
  padding: 1px 6px;
  border-radius: 3px;
  border: 1px solid #fde68a;
  white-space: nowrap;
}
```

### C2 — Add helper functions for reasoning rendering

Add these functions before `renderPlanTab()`:

```javascript
function reasonTypeBadge(r) {
  if (!r) return '';
  const cfg = {
    'data':         { cls: 'rt-data',          label: '⬡ Data-driven'  },
    'pattern':      { cls: 'rt-pattern',        label: '◎ Pattern-based' },
    'best_practice':{ cls: 'rt-best-practice',  label: '○ Best practice' }
  };
  const c = cfg[r.reasonType] || cfg['best_practice'];
  return `<span class="rt-badge ${c.cls}">${c.label}</span>`;
}

function evidenceCountLabel(r) {
  if (!r) return '';
  if (r.evidenceCount === 0 || r.reasonType === 'best_practice')
    return `<span class="evidence-count">No direct signals</span>`;
  const plural = r.evidenceCount === 1 ? 'signal' : 'signals';
  return `<span class="evidence-count">Backed by ${r.evidenceCount} ${plural}</span>`;
}

function buildPopoverContent(rationale, reasoning, insightRef, popId) {
  const signalsList = reasoning?.signals?.length
    ? `<div style="margin-top:8px;border-top:1px solid var(--border);padding-top:8px">
         <div style="font-size:10px;font-weight:700;text-transform:uppercase;letter-spacing:.07em;color:var(--text-muted);margin-bottom:5px">Evidence</div>
         ${reasoning.signals.map(s =>
           `<div style="font-size:11px;color:var(--text-mid);margin-bottom:3px;display:flex;gap:6px;align-items:flex-start">
             <span style="color:var(--blue);flex-shrink:0">·</span>${s.summary}
           </div>`
         ).join('')}
       </div>`
    : '';

  const explanationEl = reasoning?.explanation
    ? `<div style="font-size:11px;color:var(--text-muted);font-style:italic;margin-top:6px">${reasoning.explanation}</div>`
    : '';

  const insightLink = insightRef
    ? `<div class="rat-pop-link" onclick="scrollToInsight('${insightRef}','${popId}')">See linked insight</div>`
    : '';

  return `
    <div class="rat-pop-title">Why this item</div>
    <div style="display:flex;gap:5px;align-items:center;margin-bottom:8px;flex-wrap:wrap">
      ${reasoning ? reasonTypeBadge(reasoning) : ''}
      ${reasoning ? evidenceCountLabel(reasoning) : ''}
    </div>
    <div class="rat-pop-text">${rationale || '—'}</div>
    ${explanationEl}
    ${signalsList}
    ${insightLink}`;
}
```

### C3 — Update renderPlanTab() to use new reasoning rendering

In `renderPlanTab()`, update the recommendation card template.

Find the section that builds the `.rec` card HTML. Update the rec-hdr section to include
the reason type badge and evidence count below the product name, and update the rat-pop
div to use `buildPopoverContent`:

```javascript
// In the recommendation card template, replace the rat-pop div with:
`<div class="rat-pop" id="pop-rec-${i}">
  ${buildPopoverContent(r.rationale, r.reasoning, r.insightRef, `pop-rec-${i}`)}
</div>`

// Below rec-type, add the reasoning badges row:
`<div style="display:flex;gap:5px;align-items:center;margin-top:4px;flex-wrap:wrap">
  ${reasonTypeBadge(r.reasoning)}
  ${evidenceCountLabel(r.reasoning)}
  ${r.userOverride?.isOverridden ? '<span class="override-marker">Adjusted by Account Manager</span>' : ''}
</div>`
```

Apply the same pattern to cadence cards — update the rat-pop div to use
`buildPopoverContent(c.rationale, c.reasoning, c.insightRef, popId)` and add the badges
row below the cadence name.

Apply the same pattern to action cards — update the rat-pop div and add badges below the
priority badge and description.

### C4 — Fix productName truncation in buildReasoningTrace()

Find `buildReasoningTrace()`. The `shortName()` helper currently truncates at 45 characters.
Change it to 60:

```javascript
function shortName(str) {
  return (str || '').split('–')[0].split('—')[0].trim().substring(0, 60);
}
```

---

## Verification checklist

1. Generate a plan in mock mode — recommendation cards show a reason type badge
   (Data-driven, Pattern-based, or Best practice) below the product name
2. Click the ℹ button on a recommendation — the popover shows the reason type badge,
   evidence count, rationale text, evidence signals list (if any), and explanation
3. Cadence cards and action cards also show the reason type badge and evidence count
4. The reasoning trace in the copilot no longer truncates product names mid-word
5. In D365, generate a new plan — the plan payload JSON contains a reasoning block on
   each recommendation, cadence, and action
6. Build the plugin with 0 errors

---

## Final commit

```bash
git add -A
git commit -m "feat: reasoning metadata layer - reason type badges, evidence count, enhanced popovers"
git push origin main
```
