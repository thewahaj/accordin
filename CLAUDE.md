# AccordIn — Claude Code Context

## What is AccordIn

AccordIn is an AI-native account planning copilot built natively on Microsoft Dynamics 365 CE and Power Platform. The name combines Account + Coordination + Intelligence. Stylised as **AccordIn**.

It surfaces account planning intelligence that account managers miss — who the primary relationship contact is, which opportunity should lead based on pipeline stage, where approval risks are, and what the grounded revenue forecast looks like — through a conversational interface embedded directly in the CRM.

**This is not a bolt-on overlay.** The plan lives in Dataverse. The intelligence comes from a fine-tuned GPT-4o model on Azure AI Foundry. The UI is a D365 HTML web resource. The data collection and AI call happen inside a D365 C# plugin. The whole system is native to the Microsoft stack.

---

## Repository Structure

```
AccordIn/
├── CLAUDE.md                      # This file — read first every session
├── README.md
├── docs/                          # Architecture decisions and article drafts
├── model/                         # AI model layer
│   ├── prompts/cross-sell/        # System prompts (system-prompt.txt)
│   └── training-data/             # Fine-tuning JSONL files
├── copilot-service/               # Node.js prototype (reference implementation)
│   ├── src/routes/run.js          # KEY FILE: pipeline pre-calc + model call logic to port to C#
│   ├── src/routes/chat.js         # Conversational refinement logic
│   └── test-data/cross-sell/      # System prompt + scenario JSON files
├── frontend/                      # React evaluation app (prototype only)
├── accordin-plugin/               # C# D365 Plugin (production intelligence layer)
│   └── AccordIn.Plugin/
│       ├── GeneratePlan.cs        # IPlugin for accordin_GeneratePlan Custom API
│       ├── RefinePlan.cs          # IPlugin for accordin_RefinePlan Custom API
│       ├── Services/
│       │   ├── DataCollector.cs   # Reads account data from Dataverse
│       │   ├── PipelineCalculator.cs # Port of run.js calculatePipeline()
│       │   ├── ContactEnricher.cs    # Port of run.js enrichContacts()
│       │   ├── AzureOpenAIClient.cs  # Calls Azure OpenAI via HttpClient
│       │   └── PlanSaver.cs          # Writes plan + child records to Dataverse
│       └── Models/
│           ├── AccountPlanData.cs    # Input data model
│           └── PlanResponse.cs       # Output model matching JSON schema
├── power-platform/
│   ├── web-resources/accordin-hub/  # AccordIn Hub HTML web resource
│   ├── data-model/                  # Dataverse table definitions
│   └── environment-variables/       # D365 environment variable definitions
└── sample-data/                     # Demo account scenarios
```

---

## Architecture

### The clean architecture — no Azure Functions, no flows for data collection

```
AccordIn Hub (HTML web resource)
    ↓  Xrm.WebApi.online.execute('accordin_GeneratePlan', { accountId, planIntent })
accordin_GeneratePlan Custom API
    ↓  C# Plugin — GeneratePlan.cs
    ↓  DataCollector.cs      → reads Account, Opportunity, Contact, Activity, Signals
    ↓  PipelineCalculator.cs → calculates exactTotal, weightedTotal, totalLow, totalHigh
    ↓  ContactEnricher.cs    → derives suggestedPlanRole for each contact
    ↓  AzureOpenAIClient.cs  → calls Azure OpenAI (key from D365 environment variable)
    ↓  post-processing       → overwrite revenue figures, fix hasCadence
    ↓  PlanSaver.cs          → writes plan + cadences + actions + recommendations to Dataverse
    ↓  returns planPayload (JSON) + planId (GUID)
AccordIn Hub → renders plan canvas
```

**Why Custom API over Power Automate Flow:** Flows are slow (per-step overhead), hard to debug, and fragile. A C# plugin executes in milliseconds, runs inside D365 with no extra hosting, inherits D365 auth, and is version-controlled as code. Use flows only for event-driven triggers, not data collection and processing.

---

## Key Architectural Decisions — DO NOT CHANGE WITHOUT UNDERSTANDING WHY

### 1. Pipeline Pre-Calculation (PipelineCalculator.cs — ported from run.js)

Pre-calculate exactTotal, weightedTotal, totalLow, and totalHigh BEFORE calling the model. Inject as verified facts into the user message. After model responds, OVERWRITE the model's revenue figures with pre-calculated values.

**Why:** LLMs cannot do arithmetic reliably. Individual opportunity values are stripped from the JSON sent to the model so it cannot recompute them.

```csharp
// Stage weights — never change these
private static readonly Dictionary<string, double> StageWeights = new()
{
    { "negotiation", 0.85 },
    { "propose",     0.65 },
    { "qualify",     0.40 },
    { "discovery",   0.20 }
};
// totalLow  = highest-stage opportunities only × their weight (the floor)
// totalMid  = all opportunities × stage weights (= revenueTarget)
// totalHigh = raw pipeline total (= exactTotal)
```

### 2. Contact Role Enrichment (ContactEnricher.cs — ported from run.js)

Derive suggestedPlanRole for each contact before calling the model. Inject as guidance in the user message.

Rules (in priority order):
- Most senior title + highest engagement + most recent activity = **primary-relationship**
- Finance/legal/procurement title + Low engagement = **approval-risk**
- High engagement + recent activity OR owns named opportunity = **opportunity-owner**
- No activity recorded = **no-data**
- Low/Unknown engagement, no opportunity = **low-priority**

### 3. hasCadence Post-Processing (GeneratePlan.cs)

After the model responds, cross-check every contact in contactEngagement against the actual cadences array and set hasCadence correctly. The model self-reports this inaccurately — fix it programmatically after parsing the response.

### 4. Revenue Post-Processing (GeneratePlan.cs)

After parsing the model response, overwrite revenuePicture.totalLow, totalMid, totalHigh and revenueTarget with the pre-calculated verified values. Never trust the model's arithmetic for these fields.

### 5. Plan Payload as Source of Truth

Store the full JSON plan in wrl_planpayload (nvarchar) on the wrl_accountplan record. Key fields are also mapped to structured columns for reporting and list views. The UI reconstructs from wrl_planpayload.

### 6. Azure OpenAI Credentials

Store the API key in a D365 Environment Variable (type: Secret) named **accordin_AzureOpenAIKey**. The plugin reads it via IOrganizationService at runtime. Never hardcode credentials.

---

## C# Plugin — Key Implementation Notes

### GeneratePlan.cs entry point pattern
```csharp
public class GeneratePlan : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
        var service = serviceFactory.CreateOrganizationService(context.UserId);

        // Read input parameters
        var accountId = ((EntityReference)context.InputParameters["accountId"]).Id;
        var planIntent = (string)context.InputParameters["planIntent"];

        // Orchestrate services
        var data = new DataCollector(service).Collect(accountId);
        var pipeline = PipelineCalculator.Calculate(data.Opportunities);
        var enrichedContacts = ContactEnricher.Enrich(data.Contacts, data.Opportunities);
        var apiKey = GetEnvironmentVariable(service, "accordin_AzureOpenAIKey");
        var endpoint = GetEnvironmentVariable(service, "accordin_AzureOpenAIEndpoint");
        var modelDeployment = GetEnvironmentVariable(service, "accordin_ModelDeployment");
        var rawJson = new AzureOpenAIClient(endpoint, apiKey, modelDeployment)
            .GeneratePlan(data, pipeline, enrichedContacts, planIntent);
        var plan = PostProcess(rawJson, pipeline); // overwrite revenue figures, fix hasCadence
        var planId = new PlanSaver(service).Save(accountId, plan, planIntent);

        // Set output parameters
        context.OutputParameters["planPayload"] = System.Text.Json.JsonSerializer.Serialize(plan);
        context.OutputParameters["planId"] = planId.ToString();
    }
}
```

### Reading D365 Environment Variables from a plugin
```csharp
private string GetEnvironmentVariable(IOrganizationService service, string schemaName)
{
    var query = new QueryExpression("environmentvariablevalue")
    {
        ColumnSet = new ColumnSet("value"),
        Criteria = new FilterExpression()
    };
    query.Criteria.AddCondition("schemaname", ConditionOperator.Equal, schemaName);
    var results = service.RetrieveMultiple(query);
    return results.Entities.FirstOrDefault()?["value"]?.ToString() ?? string.Empty;
}
```

### Calling Dataverse Web API from the hub
```javascript
// Call Custom API from web resource
const request = {
    getMetadata: () => ({
        boundParameter: null,
        parameterTypes: {
            accountId: { typeName: "mscrm.account", structuralProperty: 5 },
            planIntent: { typeName: "Edm.String", structuralProperty: 1 }
        },
        operationType: 0,
        operationName: "accordin_GeneratePlan"
    }),
    accountId: { entityType: "account", id: accountId },
    planIntent: selectedIntent
};
const result = await Xrm.WebApi.online.execute(request);
const plan = JSON.parse(result.planPayload);
```

---

## Dataverse Schema

**Source of truth: all_tables_fields_fixed.csv**
Two name types for every field — always use the correct one for the context:
- **Schema Name** (camelCase, e.g. `wrl_AccountPlan`) — use in `@odata.bind`, `$expand`, and C# `Entity[]` attribute access
- **Logical Name** (lowercase, e.g. `wrl_accountplan`) — use in `$filter`, `$select`, `context.InputParameters[]`, `QueryExpression`, and `entity.Attributes[]`

**Filter syntax for lookups:** `_wrl_accountplan_value eq '${id}'` (underscore-wrap the logical name)
**Bind syntax for lookups:** `wrl_AccountPlan@odata.bind` (use Schema Name)

---

### Account (logical: `account`)
| Schema Name | Logical Name | Type |
|---|---|---|
| wrl_AccountTier | wrl_accounttier | picklist |
| wrl_ActualRevenueYTD | wrl_actualrevenueytd | money |
| wrl_PlanProgress | wrl_planprogress | int |
| wrl_PlanStatus | wrl_planstatus | picklist |
| wrl_PlanTarget | wrl_plantarget | money |
| wrl_RagStatus | wrl_ragstatus | picklist |
| wrl_RelationshipScore | wrl_relationshipscore | int |
| wrl_RequiresAttention | wrl_requiresattention | nvarchar |

---

### Contact (logical: `contact`)
| Schema Name | Logical Name | Type |
|---|---|---|
| wrl_EngagementLevel | wrl_engagementlevel | picklist |

---

### Account Plan (logical: `wrl_accountplan`)
| Schema Name | Logical Name | Type | Notes |
|---|---|---|---|
| wrl_Account | wrl_account | lookup | Link to Account |
| wrl_accountplanId | wrl_accountplanid | primarykey | PK |
| wrl_actualrevenue | wrl_actualrevenue | money | Achieved YTD |
| wrl_aiopeningstatement | wrl_aiopeningstatement | nvarchar | |
| wrl_confirmationtimestamp | wrl_confirmationtimestamp | datetime | Set on Approve |
| wrl_generatedtimestamp | wrl_generatedtimestamp | datetime | Set on Generate |
| wrl_growthobjectives | wrl_growthobjectives | nvarchar | |
| wrl_healthsummary | wrl_healthsummary | nvarchar | |
| wrl_marketsegment | wrl_marketsegment | nvarchar | |
| wrl_planintent | wrl_planintent | nvarchar | e.g. "cross-sell" |
| wrl_planname | wrl_planname | nvarchar | |
| wrl_planowneremail | wrl_planowneremail | nvarchar | |
| wrl_planpayload | wrl_planpayload | nvarchar | Full JSON — source of truth |
| wrl_planstatus | wrl_planstatus | picklist | 1=Draft, 2=Pending, 3=Active, 4=Archived |
| wrl_plantype | wrl_plantype | picklist | 1=Cross-sell, 2=Upsell, 3=Retention, 4=Relationship |
| wrl_revenuetarget | wrl_revenuetarget | money | = totalMid from pipeline calc |

---

### Engagement Cadence (logical: `wrl_engagementcadence`)
| Schema Name | Logical Name | Type | Notes |
|---|---|---|---|
| wrl_AccountPlan | wrl_accountplan | lookup | Link to Account Plan |
| wrl_cadencename | wrl_cadencename | nvarchar | |
| wrl_communicationchannel | wrl_communicationchannel | picklist | 1=Phone, 2=Online Meeting, 3=In-Person, 4=Email, 5=Other |
| wrl_contactname | wrl_contactname | nvarchar | Contact name (text, not lookup) |
| wrl_engagementcadenceId | wrl_engagementcadenceid | primarykey | PK |
| wrl_frequency | wrl_frequency | picklist | 1=Weekly, 2=Biweekly, 3=Monthly, 4=Quarterly |
| wrl_locationawareness | wrl_locationawareness | bit | True = remote contact |
| wrl_manageradjustment | wrl_manageradjustment | bit | True = AM manually edited |
| wrl_purpose | wrl_purpose | nvarchar | |
| wrl_rationale | wrl_rationale | nvarchar | |
| wrl_startdate | wrl_startdate | datetime | |
| wrl_status | wrl_status | picklist | 1=Active, 2=Paused, 3=Completed |

---

### Action Plan (logical: `wrl_actionplan`)
| Schema Name | Logical Name | Type | Notes |
|---|---|---|---|
| wrl_AccountPlan | wrl_accountplan | lookup | Link to Account Plan |
| wrl_actiondescription | wrl_actiondescription | nvarchar | |
| wrl_actionplanId | wrl_actionplanid | primarykey | PK |
| wrl_communicationchannel | wrl_communicationchannel | picklist | Same choices as cadence |
| wrl_currentstatus | wrl_currentstatus | picklist | 1=To Do, 2=In Progress, 3=Done |
| wrl_prioritylevel | wrl_prioritylevel | picklist | 1=High, 2=Medium, 3=Low |
| wrl_rationale | wrl_rationale | nvarchar | |
| wrl_suggestedtiming | wrl_suggestedtiming | nvarchar | |

---

### Plan Recommendation (logical: `wrl_planrecommendation`, table schema: `wrl_PlanRecommendation`)
| Schema Name | Logical Name | Type | Notes |
|---|---|---|---|
| wrl_AccountPlan | wrl_accountplan | lookup | Link to Account Plan |
| wrl_Confidence | wrl_confidence | picklist | 1=High, 2=Medium, 3=Low |
| wrl_ConfidenceReason | wrl_confidencereason | nvarchar | |
| wrl_Description | wrl_description | ntext | |
| wrl_EstimatedValue | wrl_estimatedvalue | money | |
| wrl_Name | wrl_name | nvarchar | Required field |
| wrl_Opportunity | wrl_opportunity | lookup | Link to native Opportunity (nullable) |
| wrl_PlanRecommendationId | wrl_planrecommendationid | primarykey | PK |
| wrl_ProductName | wrl_productname | nvarchar | |
| wrl_Rationale | wrl_rationale | ntext | |
| wrl_RecommendationType | wrl_recommendationtype | picklist | 1=Cross-sell, 2=Upsell, 3=Retention, 4=Relationship |
| wrl_SortOrder | wrl_sortorder | int | 1=highest priority |

---

### Conversation Message (logical: `wrl_conversationmessage`)
| Schema Name | Logical Name | Type | Notes |
|---|---|---|---|
| wrl_AccountPlan | wrl_accountplan | lookup | Link to Account Plan |
| wrl_conversationmessageId | wrl_conversationmessageid | primarykey | PK |
| wrl_messagecontent | wrl_messagecontent | nvarchar | |
| wrl_messagesequence | wrl_messagesequence | int | Ordering |
| wrl_messagetimestamp | wrl_messagetimestamp | datetime | |
| wrl_planidentifier | wrl_planidentifier | nvarchar | Legacy text link — prefer wrl_AccountPlan lookup |
| wrl_userrole | wrl_userrole | picklist | 1=User, 2=Assistant |

---

### Business Signal (logical: `wrl_businesssignal`)
| Schema Name | Logical Name | Type | Notes |
|---|---|---|---|
| wrl_Account | wrl_account | lookup | Link to Account (NOT plan) |
| wrl_AccountPlan | wrl_accountplan | lookup | Optional link to plan |
| wrl_businesssignalId | wrl_businesssignalid | primarykey | PK |
| wrl_recordcreatedon | wrl_recordcreatedon | datetime | |
| wrl_sentimentstatus | wrl_sentimentstatus | picklist | 1=Positive, 2=Neutral, 3=Risk |
| wrl_signalcategory | wrl_signalcategory | picklist | 1=Renewal, 2=Expansion, 3=Risk, 4=Engagement, 5=Market |
| wrl_signalpayload | wrl_signalpayload | nvarchar | Full signal JSON |
| wrl_signalsummary | wrl_signalsummary | nvarchar | |
| wrl_signaltimestamp | wrl_signaltimestamp | datetime | |
| wrl_sourcesystem | wrl_sourcesystem | picklist | 1=CRM, 2=ERP, 3=Email, 4=Other |

---

### Marketing Touchpoint (logical: `wrl_marketingtouchpoint`)
| Schema Name | Logical Name | Type | Notes |
|---|---|---|---|
| wrl_engagementlevel | wrl_engagementlevel | picklist | 1=High, 2=Medium, 3=Low |
| wrl_marketingtouchpointId | wrl_marketingtouchpointid | primarykey | PK |
| wrl_recordcreatedon | wrl_recordcreatedon | datetime | |
| wrl_touchpointdatetime | wrl_touchpointdatetime | datetime | |
| wrl_touchpointsummary | wrl_touchpointsummary | nvarchar | |
| wrl_touchpointtype | wrl_touchpointtype | picklist | |

---

### Intent Prompt (logical: `wrl_intentprompt`, table schema: `wrl_IntentPrompt`)
| Schema Name | Logical Name | Type | Notes |
|---|---|---|---|
| wrl_IntentPromptId | wrl_intentpromptid | primarykey | PK |
| wrl_IntentType | wrl_intenttype | nvarchar | e.g. "cross-sell" — used to look up correct prompt |
| wrl_SystemPrompt | wrl_systemprompt | ntext | Full system prompt text — read by plugin at runtime |

**Plugin reads system prompt at runtime:**
```csharp
// Query wrl_intentprompt where wrl_intenttype = planIntent
var query = new QueryExpression("wrl_intentprompt") {
    ColumnSet = new ColumnSet("wrl_systemprompt")
};
query.Criteria.AddCondition("wrl_intenttype", ConditionOperator.Equal, planIntent);
var results = service.RetrieveMultiple(query);
var systemPrompt = results.Entities.FirstOrDefault()?["wrl_systemprompt"]?.ToString();
```

---

### Lookup field API patterns (critical — get these right)
```
// Filter by lookup (use logical name with underscore wrap)
$filter=_wrl_accountplan_value eq '${planId}'

// Bind lookup on create/update (use Schema Name with @odata.bind)
"wrl_AccountPlan@odata.bind": "/wrl_accountplans(${planId})"
"wrl_Account@odata.bind": "/accounts(${accountId})"
"wrl_Opportunity@odata.bind": "/opportunities(${opportunityId})"

// C# plugin — always use logical name (lowercase)
entity["wrl_accountplan"] = new EntityReference("wrl_accountplan", planId);
entity["wrl_cadencename"] = "Strategic Review";
entity["wrl_prioritylevel"] = new OptionSetValue(1); // 1=High
```

---

## Azure Configuration

- Resource: wahaj-mms8e7cm-eastus2 (East US 2)
- Base endpoint: https://wahaj-mms8e7cm-eastus2.cognitiveservices.azure.com/
- API version: 2024-12-01-preview
- Base model deployment: gpt-4o (gpt-4o-2024-08-06)
- Fine-tuned deployment: gpt-4o-2024-08-06-v2

D365 Environment Variables (read by plugin at runtime):
- accordin_AzureOpenAIKey — Secret — Azure OpenAI API key
- accordin_AzureOpenAIEndpoint — Text — https://wahaj-mms8e7cm-eastus2.cognitiveservices.azure.com/
- accordin_ModelDeployment — Text — gpt-4o-2024-08-06-v2

---

## Current State

### Built and working
- C# plugin assembly (AccordIn.Plugin) — fully implemented and registered in D365
- Custom APIs registered: accordin_GeneratePlan, accordin_RefinePlan
- All Dataverse tables created including wrl_PlanRecommendation
- All Account custom fields created (wrl_AccountTier, wrl_RagStatus, wrl_RelationshipScore etc)
- D365 Environment Variables created (wrl_accordin_AzureOpenAIKey, wrl_accordin_AzureOpenAIEndpoint, wrl_accordin_ModelDeployment, wrl_accordin_AzureOpenAIApiVersion)
- wrl_IntentPrompt table exists — plugin reads system prompt from here at runtime
- AccordIn Hub v2 deployed as web resource — connected to real Dataverse data
- First end-to-end plan generation working — Apple Global Logistics test account
- Fine-tuned model v2 (gpt-4o-2024-08-06-v2) deployed in Azure East US 2
- AccordIn Hub area added to D365 Sales Hub sitemap

### In progress
- AccordIn Hub v3 — revised layout (Situation left panel, Plan/Contacts tabs right, collapsible copilot, contact strip, dynamic quick prompts)
- Pipeline calculator stage name matching — imported opportunities may not match expected stage names exactly

### Known issues to fix
- Stage names in D365 opportunities must match exactly: Negotiation, Propose, Qualify, Discovery (case-sensitive in PipelineCalculator.cs lookup)
- totalLow showing £0 when caused by stage name mismatch — no Negotiation stage opportunities found
- System prompt must enforce JSON-only output explicitly — base gpt-4o returns markdown without fine-tuning guidance

---

## Coding Principles

- The model carries the intelligence. Do not compensate for model weaknesses with engineering workarounds.
- Pipeline totals are always pre-calculated before the model call and post-processed after. Never trust model arithmetic.
- The plan payload JSON is the source of truth. Structured columns in Dataverse are denormalised for reporting only.
- hasCadence is always computed post-model by cross-checking the cadences array. Never trust model self-reporting.
- Azure OpenAI credentials live in D365 Environment Variables. Never hardcode them.
- The web resource is a single HTML file. Do not split into multiple files.
- Use Custom APIs for synchronous operations. Use flows only for event-driven triggers.

---

## Naming Conventions

- Product: AccordIn (capital A, capital I)
- Table prefix: wrl_
- Lookup API casing: wrl_AccountPlan (capital for entity name portion)
- Filter syntax: _wrl_AccountPlan_value
- C# namespace: AccordIn.Plugin
- Branch naming: feature/description, fix/description
- Commit format: feat: / fix: / refactor: / docs:

---

## GT Visa Evidence Notes

This project is evidence for Wahaj Rashid's UK Global Talent Visa (Exceptional Promise, Digital Technology). Key innovations:
1. Pre-calculated pipeline injection — LLM reasons, never computes
2. Stage-weighted forecasting (Discovery 20%, Qualify 40%, Propose 65%, Negotiation 85%)
3. Primary relationship contact intelligence via fine-tuned model
4. Contact engagement layer with planRole classification and coverage gap warnings
5. Five-scenario few-shot library for cross-sell intent
6. Native CRM orchestration — no third-party overlay
7. Fine-tuned GPT-4o on enterprise account planning patterns
8. Whitespace discipline — pipeline opportunities are never whitespace

All work attributed to Wahaj Rashid personally.
