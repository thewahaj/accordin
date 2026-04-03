# AccordIn — Brief 2: Retention Intent + Health Check Action

## Step 0 — Commit first

```bash
git add -A
git commit -m "checkpoint: before retention intent and health check"
git push origin main
```

---

## Overview

This brief covers two things:

1. Add the retention planning intent — a new system prompt in wrl_IntentPrompt, tested
   against the Apple Global Logistics account to demonstrate a different strategic plan
   from the same data
2. Add a health-check action type to RefinePlan.cs — when the account manager asks
   "anything new since we approved this plan?" the plugin reads recent signals and returns
   a narrative without rewriting the plan

The Dataverse MCP is available. Use it to create the new wrl_IntentPrompt record.

---

## Part A — Create the retention intent prompt via Dataverse MCP

Use the Dataverse MCP to create a new record in wrl_intentprompt with:
- wrl_intenttype: "retention"
- wrl_systemprompt: (the full prompt text below)

```
You are an expert B2B account strategist specialising in customer retention and renewal.
You will receive structured CRM data for a customer account and a plan intent from the account manager.

Act as a planning copilot. Propose a plan grounded entirely in the data provided.
Never invent facts not present in the data. If data is insufficient, say so explicitly.

For cadences: if a contact is in a different city or country, suggest online meetings by default.
Local contacts: in-person is appropriate for senior contacts.

RETENTION INTENT RULES — READ BEFORE ANYTHING ELSE
This is a retention plan. The strategic priority is different from cross-sell.
Rule 1: Renewal security comes before expansion. Never lead with a cross-sell recommendation
unless the renewal is explicitly confirmed or the account is low churn risk.
Rule 2: Churn signals are the primary watchout category. A contact going quiet, a support
issue unresolved, a competitor mention, a declining usage trend — these are retention risks
and must appear in watchouts.
Rule 3: The primary relationship contact may not be the most senior. It is the contact most
likely to advocate for renewal internally. They may be mid-level but highly engaged.
Rule 4: Cadence 1 always goes to the contact most likely to influence the renewal decision,
even if they are not the most senior.
Rule 5: If the account has no renewal signal (no contract end date, no renewal discussion),
the plan should prioritise discovering the renewal timeline as a first action.

SECTION 1 - OPENING STATEMENT RULES
- Explain WHY retention is the right focus using specific signals
- Reference at least two data points: one risk signal and one positive signal
- If no risk signals exist, state what the plan is protecting and why it matters

SECTION 2 - REVENUE PICTURE RULES
Pipeline: exactTotal and weightedTotal are PRE-CALCULATED. Use them directly.
Stage confidence weights: Discovery 20%, Qualify 40%, Propose 65%, Negotiation 85%.
revenueTarget = weightedTotal + whitespaceEstimate. Must equal totalMid.
For retention plans: existing contract value is more relevant than pipeline.
If contract value is available in signals, reference it in forecastNarrative.
forecastNarrative: explain the retention risk to existing revenue, not just pipeline.

SECTION 3 - WATCHOUT RULES
Every watchout must cite the specific data point AND the retention risk it creates.
Churn indicators to check: disengaged senior contacts, unresolved support issues,
competitor activity, usage decline, missed renewal discussion, budget pressure signals.
At least one watchout must address the renewal timeline if known.

SECTION 4 - RECOMMENDATION RULES
Priority order for retention plans:
1. Renewal security — confirm the renewal conversation is active
2. Risk mitigation — address the highest churn signal
3. Relationship deepening — only after renewal is on track
4. Expansion — only if renewal is explicitly secured or very low risk

First recommendation must address the renewal or the highest churn risk.
Every rationale must cite a specific date, name, activity, or signal.

SECTION 5 - CADENCE RULES
Before writing cadences, follow this sequence:
STEP 1: Identify the contact most likely to advocate for renewal internally. This is cadence 1.
STEP 2: Identify the contact with the highest churn risk (low engagement, disengaged,
controls budget). This is cadence 2 for re-engagement.
STEP 3: If a senior executive sponsor exists, they get cadence 3.
STEP 4: Verify the renewal advocate is in your list. If not, replace the lowest cadence.

locationAware: true for any contact not in the account manager's base city.
Cadence names must reflect the retention objective specifically.
Rationale must explain the retention relevance of this contact.

SECTION 6 - ONE-OFF ACTION TIMING RULES
For suggestedTiming, never use a specific calendar date.
Use relative timing only:
- "This week" — for urgent churn risk or Negotiation-stage renewal conversations
- "Within 2 weeks" — for high-priority re-engagement actions
- "Within 1 month" — for medium-priority relationship actions
- "Next quarter" — for low-priority or expansion-prep actions
Exception: if a renewal date exists in signals, timing may reference it as
"Before renewal in [month]".

SECTION 7 - REASONING METADATA RULES
Every recommendation, cadence, and action must include a reasoning block.

reasonType: classify as exactly one of:
  - "data" — directly supported by a named signal, activity date, or data point
  - "pattern" — inferred from a combination of signals suggesting a trend
  - "best_practice" — no direct signals; based on retention best practice for this sector

confidenceScore:
  - "high" — 2+ direct retention signals or confirmed renewal discussion
  - "medium" — 1 direct signal or qualified engagement indicators
  - "low" — no direct signals, Discovery stage, or best_practice basis

evidenceCount: count of distinct signals supporting this item. 0 for best_practice.
signals: array of specific evidence items with id and summary.
explanation: one sentence explaining why this item was included.

SECTION 8 - DATA LIMITATIONS RULES
Never leave empty. Check for: missing renewal dates, no contract value data, disengaged
decision-makers, no churn signals either way, missing usage data, no NPS or satisfaction data.
If severely limited: "This retention plan is based on industry best practices for [industry].
Validate renewal timeline and contract details before execution."

SECTION 9 - GENERAL RULES
- Max 3 recommendations, 3 cadences, 4 one-off actions
- All currency in GBP. revenueTarget must equal revenuePicture.totalMid
- Every rationale must cite specific data or state the industry best practice basis
- dataLimitations only references gaps in the provided data

contactEngagement rules:
- Include ALL contacts from the account data
- planRole: primary-relationship | opportunity-owner | approval-risk | low-priority | no-data
- For retention: primary-relationship is the renewal advocate, not necessarily most senior
- hasCadence: must accurately reflect whether this contact has a cadence in the current plan
- strategicNote: cite their relevance to the renewal decision specifically

SECTION 10 - RESPONSE FORMAT
Return ONLY valid JSON. No preamble, no markdown, no code fences.
Use the same JSON schema as the cross-sell prompt including the reasoning block on every
recommendation, cadence, and action.

SECTION 11 - FEW-SHOT EXAMPLES

--- EXAMPLE 1: Active account with renewal coming up ---
PURPOSE: Renewal security before expansion.

Account: Crestfield Energy, Tier 1. Contract renewal due 2026-08-01. Two contacts:
James Whitfield (CTO, London, Low engagement since 2025-10-01),
Sarah Park (IT Director, Edinburgh, Medium engagement, last activity 2026-01-15).
Signals: platform usage up 18% Q4 2025. No competitor signals.
Pipeline: exactTotal=95000, weightedTotal=38000, Qualify only.

REASONING: Renewal in 6 months. James Whitfield is most senior but disengaged since
October — churn risk. Sarah Park is mid-level but more recently engaged — she is the
renewal advocate. Cadence 1 = Sarah Park (renewal influence). Cadence 2 = James Whitfield
(re-engagement). Recommendation 1 = secure renewal conversation, not cross-sell.

KEY PATTERNS:
- recommendation 1: retention type, productName null, rationale cites renewal date and
  Whitfield disengagement
- recommendation 2: relationship re-engagement for Whitfield before any expansion
- recommendation 3 (if any): expansion flagged "only after renewal is confirmed"
- revenueTarget: 38000 not 95000
- forecastNarrative explains that retention of existing contract is the primary revenue
  protection goal
- cadence 1 name: "Renewal Alignment — IT Director"
- suggestedTiming uses "Before renewal in August" for the most urgent action

--- EXAMPLE 2: Disengaged account, no renewal signal ---
PURPOSE: Discover renewal timeline as priority zero.

Account: BlueSky Telecoms, Tier 2. No contract date known. Both contacts Low engagement
since Q3 2025. No recent signals. Pipeline: 0.

REASONING: No renewal date means the first action is to find it. Everything else is
secondary. Both contacts low — the less-disengaged one gets cadence 1.

KEY PATTERNS:
- recommendation 1: retention discovery action — "Establish renewal timeline and
  contract status"
- all reasoning.reasonType = "best_practice" except any item referencing the engagement
  data directly
- dataLimitations minimum 4 entries including missing contract date, missing usage data
- suggestedTiming: "This week" for the renewal discovery action

END OF EXAMPLES. Now apply these principles to the account data in the user message.
```

---

## Part B — Add health-check action type to RefinePlan.cs

File: `accordin-plugin/AccordIn.Plugin/RefinePlan.cs`

Read the file first.

### B1 — Add health-check routing in the main switch/routing block

After the `approve` handler and before the main refine flow, add:

```csharp
if (string.Equals(actionType, "health-check", StringComparison.OrdinalIgnoreCase))
{
    var healthResponse = HandleHealthCheck(service, planId, planRecord, tracingService);
    context.OutputParameters["ResponseMessage"] = healthResponse;
    context.OutputParameters["IsPlanUpdate"] = false;
    return;
}
```

### B2 — Add HandleHealthCheck method

Add this private method to the RefinePlan class:

```csharp
private string HandleHealthCheck(
    IOrganizationService service,
    Guid planId,
    Entity planRecord,
    ITracingService tracingService)
{
    tracingService.Trace("[AccordIn] RefinePlan — health-check requested");

    // Get plan approval/generation timestamp for signal comparison
    var approvedAt   = planRecord.GetAttributeValue<DateTime?>("wrl_confirmationtimestamp");
    var generatedAt  = planRecord.GetAttributeValue<DateTime?>("wrl_generatedtimestamp");
    var baseline     = approvedAt ?? generatedAt ?? DateTime.UtcNow.AddDays(-30);

    tracingService.Trace($"[AccordIn] Health-check baseline: {baseline:yyyy-MM-dd}");

    // Fetch signals newer than the baseline timestamp
    // wrl_businesssignal links to Account via wrl_account lookup
    // We need the accountId from the plan record
    var planPayload  = planRecord.GetAttributeValue<string>("wrl_planpayload");
    var accountRef   = planRecord.GetAttributeValue<EntityReference>("wrl_account");
    if (accountRef == null)
        return "I could not find the account linked to this plan. Please check the plan record.";

    var signalQuery = new QueryExpression("wrl_businesssignal")
    {
        ColumnSet = new ColumnSet(
            "wrl_signalsummary",
            "wrl_signalcategory",
            "wrl_signaltimestamp",
            "wrl_sentimentstatus",
            "wrl_sourcesystem")
    };
    signalQuery.Criteria.AddCondition(
        "_wrl_account_value", ConditionOperator.Equal, accountRef.Id);
    signalQuery.Criteria.AddCondition(
        "wrl_signaltimestamp", ConditionOperator.GreaterThan, baseline);

    var signalResults = service.RetrieveMultiple(signalQuery);
    var newSignals    = signalResults.Entities;

    tracingService.Trace($"[AccordIn] Health-check: {newSignals.Count} new signals since {baseline:yyyy-MM-dd}");

    if (newSignals.Count == 0)
    {
        var baselineLabel = approvedAt.HasValue ? "plan approval" : "plan generation";
        return $"No new signals have been recorded since {baselineLabel} on " +
               $"{baseline:dd MMM yyyy}. The account data is unchanged. " +
               $"The approved plan remains current.";
    }

    // Build a summary of new signals
    var sb = new System.Text.StringBuilder();
    var baselineDesc = approvedAt.HasValue ? "this plan was approved" : "this plan was generated";
    sb.AppendLine($"Since {baselineDesc} on {baseline:dd MMM yyyy}, " +
                  $"{newSignals.Count} new signal{(newSignals.Count > 1 ? "s have" : " has")} " +
                  $"been recorded for this account:");
    sb.AppendLine();

    foreach (var signal in newSignals.OrderByDescending(
        s => s.GetAttributeValue<DateTime>("wrl_signaltimestamp")))
    {
        var summary   = signal.GetAttributeValue<string>("wrl_signalsummary") ?? "No summary";
        var timestamp = signal.GetAttributeValue<DateTime>("wrl_signaltimestamp");
        var sentiment = signal.GetAttributeValue<OptionSetValue>("wrl_sentimentstatus");
        var sentLabel = sentiment?.Value switch
        {
            1 => "Positive",
            3 => "Risk",
            _ => "Neutral"
        };
        sb.AppendLine($"• [{sentLabel}] {timestamp:dd MMM yyyy}: {summary}");
    }

    sb.AppendLine();

    // Check for risk signals specifically
    var riskSignals = newSignals.Where(s =>
        s.GetAttributeValue<OptionSetValue>("wrl_sentimentstatus")?.Value == 3).ToList();

    if (riskSignals.Count > 0)
    {
        sb.AppendLine($"{riskSignals.Count} of these signal{(riskSignals.Count > 1 ? "s are" : " is")} " +
                      $"flagged as risk. You may want to review the plan cadences and actions " +
                      $"to address these. Ask me to update the plan if needed.");
    }
    else
    {
        sb.AppendLine("No risk signals detected. The new signals are informational. " +
                      "The approved plan remains appropriate.");
    }

    return sb.ToString().Trim();
}
```

### B3 — Verify the planRecord includes the needed columns

In the existing code that loads the plan record, ensure `wrl_confirmationtimestamp`,
`wrl_generatedtimestamp`, and `wrl_account` are included in the ColumnSet:

```csharp
var planRecord = service.Retrieve("wrl_accountplan", planId,
    new ColumnSet(
        "wrl_planpayload",
        "wrl_confirmationtimestamp",
        "wrl_generatedtimestamp",
        "wrl_account"          // EntityReference to Account
    ));
```

---

## Part C — Update the hub to send health-check action type

File: `power-platform/web-resources/accordin-hub/account-planning-hub-v3.html`

### C1 — Add health check quick prompt for existing plans

In `generateQuickPrompts(plan)`, the quick prompts are currently always the same four.
Add logic to show a different set when the plan mode is existing:

```javascript
function generateQuickPrompts(plan) {
  const rec0        = plan.recommendations?.[0];
  const riskContact = plan.contactEngagement?.find(c => c.planRole === 'approval-risk');

  let prompts;
  if (currentPlanMode === 'existing') {
    prompts = [
      'Anything new since approval?',
      riskContact ? `${riskContact.name.split(' ')[0]} still a risk?` : 'Any new risks?',
      'What needs attention this week?',
      'Is the plan still on track?'
    ];
  } else {
    prompts = [
      rec0 ? `Why ${(rec0.productName || 'top rec').split('–')[0].split('—')[0].trim().substring(0, 26)} first?` : 'Why top recommendation?',
      riskContact ? `${riskContact.name.split(' ')[0]} risk?` : 'Approval risk?',
      'Who to contact first?',
      'What needs this week?'
    ];
  }

  document.getElementById('qPrompts').innerHTML =
    prompts.map(p => `<button class="q-btn" onclick="qprompt(this)">${p}</button>`).join('');
}
```

### C2 — Handle "anything new" and health check phrases in sendChat

In `sendChat()`, before the API call, check if the message is a health-check intent.
If so, add `ActionType: 'health-check'` to the request:

```javascript
// Detect health-check intent
const healthCheckPhrases = [
  'anything new', 'what changed', 'new signals', 'since approval',
  'still on track', 'health check', 'any updates', 'what\'s new'
];
const isHealthCheck = healthCheckPhrases.some(p =>
  txt.toLowerCase().includes(p)
);

const request = {
  PlanId:      currentPlanId,
  UserMessage: txt,
  ActionType:  isHealthCheck ? 'health-check' : 'refine',
  getMetadata: function() {
    return {
      boundParameter: null,
      parameterTypes: {
        PlanId:      { typeName: 'Edm.String', structuralProperty: 1 },
        UserMessage: { typeName: 'Edm.String', structuralProperty: 1 },
        ActionType:  { typeName: 'Edm.String', structuralProperty: 1 }
      },
      operationType: 0,
      operationName: 'accordin_RefinePlan'
    };
  }
};
```

### C3 — Update mock responses for health check phrases

In the RESPONSES object, add entries for the health check quick prompts:

```javascript
'anything new':     'In the connected system, I would check for signals added since plan approval and tell you what changed. For now, ask me about specific contacts or opportunities.',
'still on track':   'I would check cadence completion and action status against due dates to confirm execution health. Ask me about a specific concern.',
'what changed':     'I would surface any new signals, stage changes, or contact engagement shifts since this plan was approved.',
'any updates':      'No new signals in mock mode. In the live system I would check for signals newer than the plan approval date.',
```

---

## Part D — Add retention intent to the hub intent selector

File: `power-platform/web-resources/accordin-hub/account-planning-hub-v3.html`

In `renderCreateMode()`, find the `.intent-opts` block. Add the retention option:

```javascript
<div class="intent-opt" onclick="selIntent(this)">
  <div class="intent-emoji">🔄</div>
  <div>
    <div class="intent-lbl">Retention</div>
    <div class="intent-desc">Secure renewal and prevent churn</div>
  </div>
</div>
```

This should already exist from earlier versions. If it does, verify it is present and
the intent label text matches exactly "Retention" — this is what gets sent as PlanType
to the plugin which looks up "retention" in wrl_intentprompt.

---

## Verification checklist

1. In D365, the wrl_intentprompt table has a record with wrl_intenttype = "retention"
2. Generate a retention plan on Apple Global Logistics — the recommendations prioritise
   relationship/retention over cross-sell, and cadences lead with the renewal-focused contact
3. The reasoning metadata block appears on recommendations, cadences, and actions in the
   retention plan JSON
4. Open an approved plan, type "anything new since approval?" — the copilot returns a
   signal summary (or "no new signals" message), IsPlanUpdate = false, plan does not re-render
5. The quick prompts change to health-check focused prompts when viewing an existing plan
6. Plugin builds with 0 errors

---

## Final commit

```bash
git add -A
git commit -m "feat: retention intent, health-check action type, health-check quick prompts"
git push origin main
```
