# AccordIn — Claude Code Context

## What is AccordIn

AccordIn is an AI-native account planning copilot built natively on Microsoft Dynamics 365 CE and Power Platform. The name combines Account + Coordination + Intelligence.

It surfaces account planning intelligence that account managers miss — who the primary relationship contact is, which opportunity should lead based on pipeline stage, where approval risks are, and what the grounded revenue forecast looks like — through a conversational interface embedded directly in the CRM.

**This is not a bolt-on overlay.** The plan lives in Dataverse. The intelligence comes from a fine-tuned GPT-4o model on Azure AI Foundry. The UI is a D365 web resource. The whole system is native to the Microsoft stack.

---

## Repository Structure

```
AccordIn/
├── docs/                          # Architecture decisions and article drafts
├── model/                         # AI model layer
│   ├── prompts/cross-sell/        # System prompts
│   └── training-data/             # Fine-tuning JSONL files
├── copilot-service/               # Node.js prototype backend
│   ├── src/routes/run.js          # Pipeline pre-calculation + model call
│   ├── src/routes/chat.js         # Conversational refinement
│   ├── src/azureClient.js         # Azure OpenAI client
│   └── test-data/cross-sell/      # System prompt + scenario JSON files
├── frontend/                      # React evaluation/testing app (prototype only)
├── power-platform/
│   ├── web-resources/accordin-hub/ # AccordIn Hub HTML web resource
│   ├── data-model/                # Dataverse table definitions
│   ├── flows/                     # Power Automate flows
│   └── environment-variables/     # D365 environment variable definitions
└── sample-data/                   # Demo account scenarios
```

---

## Architecture Overview

### Three Layers

**Model Layer (Azure AI Foundry)**
- Base model: GPT-4o (gpt-4o-2024-08-06)
- Fine-tuned model: gpt-4o-2024-08-06-v2 (deployment name, East US 2)
- Fine-tuning suffix: acc_plan_ft
- 16 training examples across 5 scenario types
- System prompt: model/prompts/cross-sell/system-prompt.txt (~2700 tokens)

**Service Layer (Node.js → Azure Function eventually)**
- Entry: copilot-service/src/server.js (Express, port 3001)
- Key file: copilot-service/src/routes/run.js — this is where the intelligence pipeline lives
- Calls Azure OpenAI with pre-calculated pipeline data injected into the user message

**CRM Layer (D365 CE + Dataverse)**
- Web resource: power-platform/web-resources/accordin-hub/account-planning-hub-v2.html
- Custom tables: see Dataverse Schema section below
- Dataverse Web API v9.2 for all reads and writes

---

## Key Architectural Decisions — DO NOT CHANGE WITHOUT UNDERSTANDING WHY

### 1. Pipeline Pre-Calculation (run.js)
The backend calculates exactTotal, weightedTotal, totalLow, and totalHigh BEFORE calling the model. These are injected as verified facts into the user message. After the model responds, the backend OVERWRITES the model's revenue figures with the pre-calculated values.

**Why:** LLMs cannot do arithmetic reliably. We tried letting the model calculate — it consistently got the wrong total. The fix was to make it impossible for the model to compute by stripping individual opportunity values from the JSON sent to the model.

```javascript
// Stage weights — never change these
const STAGE_WEIGHTS = {
  'negotiation': 0.85,
  'propose':     0.65,
  'qualify':     0.40,
  'discovery':   0.20,
};
// totalLow = highest stage opportunities only × their weight
// totalMid = all opportunities × their stage weights (this is revenueTarget)
// totalHigh = raw pipeline total (exactTotal)
```

### 2. Contact Role Enrichment (run.js — enrichContacts function)
Before calling the model, the backend derives a suggestedPlanRole for each contact using simple rules. This is injected into the user message as guidance. The model can override it but starts from a grounded signal.

Rules:
- Most senior title + highest engagement + most recent activity = primary-relationship
- Finance/legal/procurement title + Low engagement = approval-risk
- High engagement + recent activity OR owns named opportunity = opportunity-owner
- No activity = no-data
- Low/Unknown engagement with no opportunity = low-priority

### 3. hasCadence Post-Processing (run.js)
After the model responds, the backend checks every contact in contactEngagement against the actual cadences array and sets hasCadence correctly. The model self-reports this inaccurately — we fix it programmatically.

### 4. Plan Payload Storage (Dataverse)
The full JSON plan is stored in wrl_planpayload (nvarchar) on the wrl_accountplan record. Key fields are also mapped to structured columns for reporting. This means the UI can always reconstruct itself from the raw payload — no dependency on child records being loaded first.

### 5. Intent-Driven Routing
The system prompt and few-shot examples are per-intent. Currently only cross-sell is built. When adding upsell, retention, or relationship intents, create a new folder under copilot-service/test-data/ with its own system-prompt.txt and scenarios/.

---

## Dataverse Schema

**All custom tables use prefix: wrl_**

### wrl_accountplan (Account Plan)
Primary entity. Links to standard Account table.
- wrl_accountplanid — Primary Key
- wrl_account — Lookup to Account (required)
- wrl_planname — Text
- wrl_plantype — Choice (1=Cross-sell, 2=Upsell, 3=Retention, 4=Relationship)
- wrl_planstatus — Choice (1=Draft, 2=Pending Approval, 3=Active, 4=Archived)
- wrl_planintent — Text (the AM's intent typed into the copilot)
- wrl_planpayload — Multiline Text (full JSON from model — source of truth)
- wrl_aiopeningstatement — Text
- wrl_healthsummary — Text
- wrl_growthobjectives — Text
- wrl_revenuetarget — Currency (totalMid)
- wrl_actualrevenue — Currency (achieved YTD)
- wrl_planowneremail — Text
- wrl_generatedtimestamp — DateTime
- wrl_confirmationtimestamp — DateTime

### wrl_engagementcadence (Engagement Cadence)
Child of Account Plan.
- wrl_engagementcadenceid — Primary Key
- wrl_AccountPlan — Lookup to wrl_accountplan (use this exact casing)
- wrl_cadencename — Text
- wrl_contactname — Text (contact name — text for now, future lookup to Contact)
- wrl_frequency — Choice (1=Weekly, 2=Biweekly, 3=Monthly, 4=Quarterly)
- wrl_communicationchannel — Choice (1=Phone, 2=Online Meeting, 3=In-Person, 4=Email, 5=Other)
- wrl_purpose — Multiline Text
- wrl_rationale — Multiline Text
- wrl_locationawareness — Boolean
- wrl_status — Choice (1=Active, 2=Paused, 3=Completed)
- wrl_startdate — DateTime
- wrl_manageradjustment — Boolean (true if AM manually edited this cadence)

### wrl_actionplan (One-off Action)
Child of Account Plan.
- wrl_actionplanid — Primary Key
- wrl_AccountPlan — Lookup to wrl_accountplan
- wrl_actiondescription — Multiline Text
- wrl_prioritylevel — Choice (1=High, 2=Medium, 3=Low)
- wrl_communicationchannel — Choice (same as cadence)
- wrl_suggestedtiming — Text
- wrl_rationale — Multiline Text
- wrl_currentstatus — Choice (1=To Do, 2=In Progress, 3=Done)

### wrl_planrecommendation (Plan Recommendation) — CREATE THIS TABLE
Child of Account Plan. Replaces the incorrectly imported wrl_salesopportunity.
- wrl_planrecommendationid — Primary Key
- wrl_AccountPlan — Lookup to wrl_accountplan
- wrl_Opportunity — Lookup to Opportunity (nullable — links to native D365 opportunity)
- wrl_productname — Text
- wrl_recommendationtype — Choice (1=Cross-sell, 2=Upsell, 3=Retention, 4=Relationship)
- wrl_description — Multiline Text
- wrl_rationale — Multiline Text
- wrl_estimatedvalue — Currency
- wrl_confidence — Choice (1=High, 2=Medium, 3=Low)
- wrl_confidencereason — Text
- wrl_sortorder — Whole Number (1, 2, 3)

### wrl_conversationmessage (Chat Message)
Child of Account Plan. Stores copilot chat history.
- wrl_conversationmessageid — Primary Key
- wrl_AccountPlan — Lookup to wrl_accountplan
- wrl_messagecontent — Multiline Text
- wrl_userrole — Choice (1=User, 2=Assistant)
- wrl_messagetimestamp — DateTime
- wrl_messagesequence — Whole Number

### wrl_businesssignal (Business Signal)
Account-level. NOT linked to plan.
- wrl_businesssignalid — Primary Key
- wrl_Account — Lookup to Account
- wrl_signalsummary — Text
- wrl_signalcategory — Choice
- wrl_signaltimestamp — DateTime
- wrl_sentimentstatus — Choice (1=Positive, 2=Neutral, 3=Negative/Risk)
- wrl_signalpayload — Multiline Text (full signal JSON)

### wrl_marketingtouchpoint (Marketing Touchpoint)
Account-level. NOT linked to plan.
- wrl_marketingtouchpointid — Primary Key
- wrl_Account — Lookup to Account
- wrl_Contact — Lookup to Contact (optional)
- wrl_touchpointsummary — Text
- wrl_touchpointtype — Choice
- wrl_touchpointdatetime — DateTime
- wrl_engagementlevel — Choice (1=High, 2=Medium, 3=Low)

### Account table — custom fields needed (prefix wrl_)
- wrl_accounttier — Choice (1=Tier 1, 2=Tier 2, 3=Tier 3)
- wrl_relationshipscore — Whole Number (0-100)
- wrl_planstatus — Choice (mirrors wrl_accountplan planstatus — denormalised)
- wrl_ragstatus — Choice (1=Green, 2=Amber, 3=Red — denormalised)
- wrl_requiresattention — Text (single line — the one urgent thing, denormalised)
- wrl_plantarget — Currency (copied from active plan, denormalised)
- wrl_actualrevenueytd — Currency (denormalised)
- wrl_planprogress — Whole Number 0-100 (denormalised)

---

## Dataverse API Pattern

All API calls use standard Dataverse Web API v9.2. The API object in account-planning-hub-v2.html shows the full pattern. Key things:

```javascript
// Always use OData-EntityId header to get created record ID
const location = res.headers.get('OData-EntityId');
const match = location && location.match(/\(([^)]+)\)/);
const newId = match ? match[1] : null;

// Lookup binds use @odata.bind syntax
'wrl_AccountPlan@odata.bind': `/wrl_accountplans(${planId})`
'wrl_Account@odata.bind': `/accounts(${accountId})`

// Expand syntax for related records
$expand=wrl_account($select=name,wrl_accounttier,wrl_ragstatus)

// Filter by lookup
$filter=_wrl_AccountPlan_value eq '${planId}'
```

---

## The AccordIn Hub Web Resource

File: power-platform/web-resources/accordin-hub/account-planning-hub-v2.html

This is a single-file HTML web resource deployed to D365 Sales Hub as a dedicated area. It:
- Auto-detects D365 context via `typeof Xrm !== 'undefined'`
- In D365: calls Dataverse Web API directly (inherits session auth)
- Standalone: uses MOCK_ACCOUNTS and MOCK_PLAN data
- Three-column plan canvas: left (summary), middle (intelligence), right (copilot chat)
- Contact engagement section with planRole badges and coverage gap warning

**Do not refactor into multiple files.** D365 web resources work best as single files. If complexity grows, move to a PCF component instead (that is a separate build pipeline).

---

## Copilot Service — Key Files

### copilot-service/src/routes/run.js
The most important file in the service layer. Do not change the core logic without understanding the architecture decisions above.

Flow:
1. Load scenario data
2. calculatePipeline() — pre-calculate exactTotal, weightedTotal, totalLow, totalHigh
3. enrichContacts() — derive suggestedPlanRole for each contact
4. Strip individual opportunity values from JSON sent to model
5. Build user message with pre-calculated facts injected
6. Call model
7. Post-process: overwrite revenue figures with verified values
8. Post-process: fix hasCadence based on actual cadences array
9. Return to client

### copilot-service/test-data/cross-sell/system-prompt.txt
The current system prompt for cross-sell intent. ~2700 tokens with 5 compact few-shot examples covering: rich data with strategic contact, new account, dormant account, conflicting signals, cold account.

---

## Azure Configuration

- Resource: wahaj-mms8e7cm-eastus2 (East US 2)
- Base endpoint: https://wahaj-mms8e7cm-eastus2.cognitiveservices.azure.com/
- API version: 2024-12-01-preview
- Base model deployment: gpt-4o (gpt-4o-2024-08-06)
- Fine-tuned deployment: gpt-4o-2024-08-06-v2
- Fine-tuning job ID: ftjob-0ecbaac4f898421cbd89b1797189baa2
- Fine-tuning suffix: acc_plan_ft

Environment variables in copilot-service/.env:
```
AZURE_OPENAI_ENDPOINT=https://wahaj-mms8e7cm-eastus2.cognitiveservices.azure.com/
AZURE_OPENAI_API_KEY=<key>
AZURE_OPENAI_DEPLOYMENT_NAME=gpt-4o-2024-08-06-v2
AZURE_OPENAI_API_VERSION=2024-12-01-preview
PORT=3001
```

---

## What is Built vs What is Next

### Built and working
- Cross-sell intent: system prompt, few-shot examples, pipeline pre-calculation, contact enrichment
- AccordIn Hub v2: full hub list view + plan canvas + chat (mock data)
- Fine-tuned model v2 (16 training examples)
- Dataverse schema (minus wrl_planrecommendation which needs creating)
- React evaluation app (prototype only — not for production)

### Not built yet (in priority order)
1. wrl_planrecommendation table in D365
2. Account custom fields (wrl_accounttier, wrl_ragstatus etc) in D365
3. AccordIn Hub deployed as D365 web resource with real Dataverse data
4. Power Automate flow to collect account data and call copilot service
5. Azure Function wrapping the copilot service (replacing Node.js Express)
6. Plan writeback: save generated plan + child records to Dataverse
7. Upsell, retention, and relationship intents
8. RAG layer for dynamic industry knowledge
9. PCF component for the plan canvas (replaces HTML web resource eventually)

---

## Coding Principles — Always Follow These

- The model should carry the intelligence. Do not compensate for model weaknesses with backend engineering workarounds.
- Fine-tuning is for structural consistency. RAG is for dynamic knowledge. These are not interchangeable.
- Pipeline totals are always pre-calculated in the backend and post-processed after the model responds. Never trust the model's arithmetic.
- The plan payload JSON is the source of truth. Structured columns in Dataverse are denormalised for reporting only.
- Every rationale in the plan must cite a specific date, contact name, or signal from the account data. No generic phrases.
- The web resource is a single HTML file. Do not split into multiple files unless moving to PCF.

---

## Naming Conventions

- Product name: AccordIn (capital A, capital I)
- Table prefix: wrl_
- Lookup field casing in API calls: wrl_AccountPlan (camelCase with capital for entity name)
- Filter syntax for lookups: _wrl_AccountPlan_value (underscore prefix and suffix)
- Branch naming: feature/description, fix/description
- Commit format: feat: description / fix: description / refactor: description

---

## GT Visa Evidence Notes

This project is evidence for Wahaj Rashid's UK Global Talent Visa (Exceptional Promise, Digital Technology). Key innovations to highlight in any documentation:

1. Pre-calculated pipeline injection — LLM reasons, never computes
2. Stage-weighted forecasting (Discovery 20%, Qualify 40%, Propose 65%, Negotiation 85%)
3. Primary relationship contact intelligence via fine-tuned model
4. Contact engagement layer with planRole classification and coverage gap warnings
5. Five-scenario few-shot library for cross-sell intent
6. Native CRM orchestration — no third-party overlay
7. Fine-tuned GPT-4o on enterprise account planning patterns
8. Whitespace discipline — pipeline opportunities are never whitespace

All work is attributed to Wahaj Rashid personally. Visionet Systems is the employer context but the innovations are Wahaj's independent technical contribution.
