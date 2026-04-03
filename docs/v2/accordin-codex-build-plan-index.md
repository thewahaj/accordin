# AccordIn — Codex Build Plan: Demo Readiness

## Overview

Four sequential briefs. Run them in order. Each brief starts with a git commit.
Do not start a brief until the previous one is committed and pushed.

---

## Brief 1 — Reasoning Metadata Layer
File: `docs/accordin-codex-brief-1-reasoning.md`

**What it does:**
- Adds structured reasoning block to every recommendation, cadence, and action in the plan JSON
- Updates the cross-sell system prompt to classify reasoning as data / pattern / best_practice
- Updates PlanResponse.cs with ReasoningBlock and ReasoningSignal model classes
- Updates the hub to render reason type badges, evidence count, and enhanced info popovers

**Why first:** Every subsequent feature builds on the reasoning metadata. Brief 2 uses it
in the retention prompt. Brief 3 uses it in signal linking. Brief 4 polishes its display.

**Uses Dataverse MCP:** Yes — updates the cross-sell wrl_IntentPrompt record.

---

## Brief 2 — Retention Intent + Health Check
File: `docs/accordin-codex-brief-2-retention-health.md`

**What it does:**
- Creates the retention system prompt in wrl_IntentPrompt via Dataverse MCP
- Adds ActionType = "health-check" to RefinePlan.cs — reads signals newer than plan
  approval timestamp and returns a narrative without rewriting the plan
- Updates hub quick prompts to show health-check focused questions for existing plans
- Adds "anything new since approval?" phrase detection in sendChat

**Why second:** Needs the reasoning metadata schema from Brief 1 to be in the prompt
and model before the retention prompt is written.

**Uses Dataverse MCP:** Yes — creates a new wrl_IntentPrompt record for retention.

---

## Brief 3 — External Signals + Bidirectional Linking
File: `docs/accordin-codex-brief-3-signals-linking.md`

**What it does:**
- Creates 4 realistic demo signals on Apple Global Logistics via Dataverse MCP —
  2 external risk/news signals, 2 internal CRM signals, all dated after plan approval
- Adds bidirectional signal linking to the hub — clicking a watchout or signal on the
  left panel highlights the related plan cards on the right panel
- Adds hover states to signals and watchouts to indicate they are clickable

**Why third:** Requires health-check to be working (Brief 2) so the demo signals are
surfaced when the account manager asks "anything new since approval?"

**Uses Dataverse MCP:** Yes — creates wrl_businesssignal records.

---

## Brief 4 — Demo Polish
File: `docs/accordin-codex-brief-4-polish.md`

**What it does:**
- Fixes productName truncation in reasoning trace (45 → 60 chars)
- Capitalises recommendation type labels (relationship → Relationship)
- Replaces £0 on relationship/retention recommendations with "Value TBD"
- Fixes contact strip showing only 2 contacts when 3 available
- Fixes quick prompt buttons wrapping on narrow screens
- Fixes panel header button wrapping
- Verifies data limitations collapse consistently

**Uses Dataverse MCP:** No.

---

## Demo Scenario Coverage

After all four briefs are complete:

| Scenario | What is built | Where |
|---|---|---|
| 1. Cross-sell plan with reasoning | Reasoning badges, evidence count, enhanced popovers, reasoning trace in copilot | Hub + prompt |
| 1b. Refinement | Two-stage classifier, targeted Dataverse updates, chat history preserved | Plugin + hub |
| 2. Retention intent, same account | Retention prompt with inverted priorities | Prompt + D365 config |
| 3. Health check on approved plan | Health-check action type, signal delta narrative | Plugin + hub |
| 4. External signals | 4 demo signals in Dataverse, surfaces in health check | D365 data + hub |
| Bidirectional linking | Click signal → highlight plan cards, click card → scroll to signal | Hub |

---

## After the briefs — stop building

Once Brief 4 is committed and tested end to end:

1. Record a demo walkthrough video covering all four scenarios
2. Write the InfoQ architecture article (AccordIn as primary subject)
3. Prepare GT visa evidence artifacts
4. Write the technical blog post (Part 2 of "Integrations Are Not the Problem")
