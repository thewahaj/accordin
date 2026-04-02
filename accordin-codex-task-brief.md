# AccordIn Plugin — Two-Stage Refinement Architecture

## Overview

This brief implements a two-stage refinement pipeline for the AccordIn copilot.

**Stage 1 — Intent Classification (`IntentClassifier.cs`):** A lightweight call that reads the user message plus a stripped context object and returns a structured intent JSON identifying what action to take and which record to target.

**Stage 2 — Targeted Execution (`RefinePlan.cs`):** Routes the classified intent to a direct Dataverse operation (no model call for simple updates), or falls back to a full-plan model call for complex instructions.

This replaces the current approach of sending the full plan JSON to the model for every refinement request.

---

## D365 Configuration Steps (Do These Before Running the Plugin)

### Step 1 — Add new Environment Variables in D365

Go to make.powerapps.com → your solution → Environment Variables → New.

Add the following:

| Display Name | Schema Name | Type | Value |
|---|---|---|---|
| AccordIn Classifier Model Deployment | wrl_accordin_ClassifierModelDeployment | Text | gpt-4o (same as main model for now — swap to gpt-4o-mini in production) |

The existing variables remain unchanged:
- `wrl_accordin_AzureOpenAIKey`
- `wrl_accordin_AzureOpenAIEndpoint`
- `wrl_accordin_ModelDeployment`
- `wrl_accordin_AzureOpenAIApiVersion`

### Step 2 — Add new Intent Prompt records in D365

Go to make.powerapps.com → your solution → Data → wrl_IntentPrompt table → Edit data.

Add two new rows:

**Row 1 — Intent classifier prompt**

| Field | Value |
|---|---|
| wrl_IntentType | `classify-intent` |
| wrl_SystemPrompt | (paste the prompt from the IntentClassifier section below) |

**Row 2 — Refinement fallback prompt**

| Field | Value |
|---|---|
| wrl_IntentType | `refine` |
| wrl_SystemPrompt | (paste the prompt from the Fallback section below) |

### Step 3 — Update accordin_RefinePlan Custom API input parameters

Go to make.powerapps.com → your solution → Custom APIs → accordin_RefinePlan → Request Parameters.

The existing parameters are:
- `PlanId` (String)
- `UserMessage` (String)

Verify `ActionType` exists. If not, add it:

| Name | Display Name | Type | Required |
|---|---|---|---|
| ActionType | Action Type | String | No |

### Step 4 — Verify accordin_RefinePlan Custom API response properties

Go to the same Custom API → Response Properties.

Verify these exist. Add any that are missing:

| Name | Display Name | Type |
|---|---|---|
| ResponseMessage | Response Message | String |
| IsPlanUpdate | Is Plan Update | Boolean |

### Step 5 — After building and registering the updated assembly

Re-register the assembly using Plugin Registration Tool.

Update the plugin step for `accordin_RefinePlan`:
- Message: `accordin_RefinePlan`
- Execution Mode: Synchronous
- Stage: Post-operation

No changes needed to `accordin_GeneratePlan` registration.

---

## Repository Changes

### New file: `Services/IntentClassifier.cs`

Create this file. It is responsible for Stage 1 only — classifying user intent and returning a structured result. It does not execute any Dataverse operations.

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccordIn.Plugin.Services
{
    public class IntentClassifier
    {
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly string _deployment;
        private readonly string _apiVersion;

        public IntentClassifier(string endpoint, string apiKey, string deployment, string apiVersion)
        {
            _endpoint = endpoint;
            _apiKey = apiKey;
            _deployment = deployment;
            _apiVersion = apiVersion;
        }

        /// <summary>
        /// Classifies the user's refinement intent against a stripped plan context.
        /// Returns a structured IntentResult without making any Dataverse calls.
        /// </summary>
        public IntentResult Classify(string userMessage, PlanContext context, string systemPrompt)
        {
            var contextJson = JsonSerializer.Serialize(context, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            var userContent = $"Plan context:\n{contextJson}\n\nUser instruction: {userMessage}";

            var requestBody = new
            {
                model = _deployment,
                max_tokens = 500,
                temperature = 0,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userContent  }
                }
            };

            var json = JsonSerializer.Serialize(requestBody);

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("api-key", _apiKey);

                var url = $"{_endpoint}openai/deployments/{_deployment}/chat/completions?api-version={_apiVersion}";
                var response = client.PostAsync(url,
                    new StringContent(json, Encoding.UTF8, "application/json")).Result;

                var responseJson = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"[AccordIn] IntentClassifier error: {responseJson}");

                using var doc = JsonDocument.Parse(responseJson);
                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                // Strip markdown fences if model includes them
                var cleaned = content?.Trim();
                if (cleaned != null && cleaned.StartsWith("```"))
                {
                    var start = cleaned.IndexOf('\n') + 1;
                    var end   = cleaned.LastIndexOf("```");
                    if (end > start) cleaned = cleaned.Substring(start, end - start).Trim();
                }

                return JsonSerializer.Deserialize<IntentResult>(cleaned ?? "{}",
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new IntentResult { Action = "query", Response = "I could not understand that request." };
            }
        }
    }

    // ── Stripped context sent to classifier — NOT the full plan JSON ──────────
    public class PlanContext
    {
        [JsonPropertyName("cadences")]
        public List<CadenceContext> Cadences { get; set; } = new List<CadenceContext>();

        [JsonPropertyName("actions")]
        public List<ActionContext> Actions { get; set; } = new List<ActionContext>();

        [JsonPropertyName("recommendations")]
        public List<RecommendationContext> Recommendations { get; set; } = new List<RecommendationContext>();

        [JsonPropertyName("contacts")]
        public List<ContactContext> Contacts { get; set; } = new List<ContactContext>();
    }

    public class CadenceContext
    {
        [JsonPropertyName("d365Id")]       public string D365Id        { get; set; }
        [JsonPropertyName("contactName")]  public string ContactName   { get; set; }
        [JsonPropertyName("name")]         public string Name          { get; set; }
        [JsonPropertyName("frequency")]    public string Frequency     { get; set; }
        [JsonPropertyName("channel")]      public string Channel       { get; set; }
        [JsonPropertyName("purpose")]      public string Purpose       { get; set; }
    }

    public class ActionContext
    {
        [JsonPropertyName("d365Id")]       public string D365Id        { get; set; }
        [JsonPropertyName("description")]  public string Description   { get; set; }
        [JsonPropertyName("priority")]     public string Priority      { get; set; }
        [JsonPropertyName("channel")]      public string Channel       { get; set; }
        [JsonPropertyName("suggestedTiming")] public string SuggestedTiming { get; set; }
    }

    public class RecommendationContext
    {
        [JsonPropertyName("d365Id")]       public string D365Id        { get; set; }
        [JsonPropertyName("productName")]  public string ProductName   { get; set; }
        [JsonPropertyName("confidence")]   public string Confidence    { get; set; }
        [JsonPropertyName("estimatedValue")] public double EstimatedValue { get; set; }
    }

    public class ContactContext
    {
        [JsonPropertyName("d365ContactId")] public string D365ContactId { get; set; }
        [JsonPropertyName("name")]           public string Name          { get; set; }
        [JsonPropertyName("planRole")]       public string PlanRole      { get; set; }
    }

    // ── Result returned by classifier ─────────────────────────────────────────
    public class IntentResult
    {
        [JsonPropertyName("action")]
        public string Action { get; set; }          // update_cadence | update_action | update_recommendation
                                                     // add_cadence | add_action
                                                     // delete_cadence | delete_action
                                                     // query | complex_refine

        [JsonPropertyName("targetId")]
        public string TargetId { get; set; }        // d365Id of the record to change (null for add/query)

        [JsonPropertyName("changes")]
        public JsonElement? Changes { get; set; }   // fields to change with new values

        [JsonPropertyName("response")]
        public string Response { get; set; }        // natural language confirmation shown to user
    }
}
```

---

### Updated file: `RefinePlan.cs`

Replace the current `RefinePlan.cs` with the following. Read the existing file first to preserve any environment variable helper methods already there.

```csharp
using System;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using AccordIn.Plugin.Services;
using AccordIn.Plugin.Models;
using System.Collections.Generic;

namespace AccordIn.Plugin
{
    public class RefinePlan : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context        = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service        = serviceFactory.CreateOrganizationService(context.UserId);
            var tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            // ── Read input parameters ────────────────────────────────────────
            var planIdStr    = context.InputParameters.Contains("PlanId")      ? context.InputParameters["PlanId"]?.ToString()      : null;
            var userMessage  = context.InputParameters.Contains("UserMessage") ? context.InputParameters["UserMessage"]?.ToString() : null;
            var actionType   = context.InputParameters.Contains("ActionType")  ? context.InputParameters["ActionType"]?.ToString()  : "refine";

            if (string.IsNullOrEmpty(planIdStr))
                throw new InvalidPluginExecutionException("PlanId is required.");

            var planId = Guid.Parse(planIdStr);

            // ── Read Azure OpenAI credentials ────────────────────────────────
            var endpoint          = GetEnvironmentVariable(service, "wrl_accordin_AzureOpenAIEndpoint");
            var apiKey            = GetEnvironmentVariable(service, "wrl_accordin_AzureOpenAIKey");
            var mainDeployment    = GetEnvironmentVariable(service, "wrl_accordin_ModelDeployment");
            var classifierDeploy  = GetEnvironmentVariable(service, "wrl_accordin_ClassifierModelDeployment");
            var apiVersion        = GetEnvironmentVariable(service, "wrl_accordin_AzureOpenAIApiVersion");

            // Fall back to main deployment if classifier deployment not configured
            if (string.IsNullOrEmpty(classifierDeploy))
                classifierDeploy = mainDeployment;

            // ── Handle approve ───────────────────────────────────────────────
            if (string.Equals(actionType, "approve", StringComparison.OrdinalIgnoreCase))
            {
                var approveEntity = new Entity("wrl_accountplan", planId);
                approveEntity["wrl_planstatus"]            = new OptionSetValue(3);
                approveEntity["wrl_confirmationtimestamp"] = DateTime.UtcNow;
                service.Update(approveEntity);

                context.OutputParameters["ResponseMessage"] = "Plan approved. Status updated to Active.";
                context.OutputParameters["IsPlanUpdate"]    = false;
                return;
            }

            // ── Load plan payload ────────────────────────────────────────────
            var planRecord     = service.Retrieve("wrl_accountplan", planId, new ColumnSet("wrl_planpayload"));
            var planPayloadJson = planRecord.GetAttributeValue<string>("wrl_planpayload");

            if (string.IsNullOrEmpty(planPayloadJson))
                throw new InvalidPluginExecutionException("Plan payload is empty. Regenerate the plan first.");

            var plan = JsonSerializer.Deserialize<PlanResponse>(planPayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            // ── Build stripped context for classifier ────────────────────────
            var planContext = BuildPlanContext(plan);

            // ── Load classifier system prompt ────────────────────────────────
            var classifierPrompt = GetIntentPrompt(service, "classify-intent");
            if (string.IsNullOrEmpty(classifierPrompt))
                classifierPrompt = GetDefaultClassifierPrompt();

            // ── Stage 1: Classify intent ─────────────────────────────────────
            tracingService.Trace("[AccordIn] RefinePlan — classifying intent for: " + userMessage);

            var classifier = new IntentClassifier(endpoint, apiKey, classifierDeploy, apiVersion);
            var intent     = classifier.Classify(userMessage, planContext, classifierPrompt);

            tracingService.Trace("[AccordIn] Intent classified as: " + intent.Action);

            // ── Stage 2: Route and execute ───────────────────────────────────
            bool isPlanUpdate = false;
            string responseMessage;

            switch (intent.Action?.ToLower())
            {
                case "update_cadence":
                    UpdateCadence(service, intent, plan);
                    SavePayload(service, planId, plan);
                    isPlanUpdate   = true;
                    responseMessage = intent.Response ?? "Cadence updated.";
                    break;

                case "update_action":
                    UpdateAction(service, intent, plan);
                    SavePayload(service, planId, plan);
                    isPlanUpdate   = true;
                    responseMessage = intent.Response ?? "Action updated.";
                    break;

                case "update_recommendation":
                    UpdateRecommendation(service, intent, plan);
                    SavePayload(service, planId, plan);
                    isPlanUpdate   = true;
                    responseMessage = intent.Response ?? "Recommendation updated.";
                    break;

                case "add_cadence":
                    AddCadence(service, planId, intent, plan);
                    SavePayload(service, planId, plan);
                    isPlanUpdate   = true;
                    responseMessage = intent.Response ?? "Cadence added.";
                    break;

                case "add_action":
                    AddAction(service, planId, intent, plan);
                    SavePayload(service, planId, plan);
                    isPlanUpdate   = true;
                    responseMessage = intent.Response ?? "Action added.";
                    break;

                case "delete_cadence":
                    DeleteCadence(service, intent, plan);
                    SavePayload(service, planId, plan);
                    isPlanUpdate   = true;
                    responseMessage = intent.Response ?? "Cadence removed.";
                    break;

                case "delete_action":
                    DeleteAction(service, intent, plan);
                    SavePayload(service, planId, plan);
                    isPlanUpdate   = true;
                    responseMessage = intent.Response ?? "Action removed.";
                    break;

                case "query":
                    // No Dataverse changes — just return the response
                    isPlanUpdate   = false;
                    responseMessage = intent.Response ?? "I could not find an answer to that.";
                    break;

                case "complex_refine":
                default:
                    // Fall back to full-plan model call for ambiguous or multi-step instructions
                    responseMessage = HandleComplexRefine(service, planId, plan, planPayloadJson,
                        userMessage, endpoint, apiKey, mainDeployment, apiVersion, out isPlanUpdate);
                    break;
            }

            context.OutputParameters["ResponseMessage"] = responseMessage;
            context.OutputParameters["IsPlanUpdate"]    = isPlanUpdate;
        }

        // ── Build stripped context (Stage 1 input) ────────────────────────────
        private PlanContext BuildPlanContext(PlanResponse plan)
        {
            var ctx = new PlanContext();

            foreach (var c in plan.Cadences ?? new System.Collections.Generic.List<CadenceItem>())
                ctx.Cadences.Add(new CadenceContext
                {
                    D365Id      = c.D365Id,
                    ContactName = c.ContactName,
                    Name        = c.Name,
                    Frequency   = c.Frequency,
                    Channel     = c.Channel,
                    Purpose     = c.Purpose
                });

            foreach (var a in plan.OneOffActions ?? new System.Collections.Generic.List<ActionItem>())
                ctx.Actions.Add(new ActionContext
                {
                    D365Id          = a.D365Id,
                    Description     = a.Description,
                    Priority        = a.Priority,
                    Channel         = a.Channel,
                    SuggestedTiming = a.SuggestedTiming
                });

            foreach (var r in plan.Recommendations ?? new System.Collections.Generic.List<RecommendationItem>())
                ctx.Recommendations.Add(new RecommendationContext
                {
                    D365Id         = r.D365Id,
                    ProductName    = r.ProductName,
                    Confidence     = r.Confidence,
                    EstimatedValue = r.EstimatedValue
                });

            foreach (var c in plan.ContactEngagement ?? new System.Collections.Generic.List<ContactEngagementItem>())
                ctx.Contacts.Add(new ContactContext
                {
                    D365ContactId = c.D365ContactId,
                    Name          = c.Name,
                    PlanRole      = c.PlanRole
                });

            return ctx;
        }

        // ── Targeted Dataverse update methods ─────────────────────────────────

        private void UpdateCadence(IOrganizationService service, IntentResult intent, PlanResponse plan)
        {
            if (string.IsNullOrEmpty(intent.TargetId)) return;
            var id = Guid.Parse(intent.TargetId);
            var entity = new Entity("wrl_engagementcadence", id);

            ApplyChangesToEntity(entity, intent.Changes, "cadence");
            service.Update(entity);

            // Update the payload in memory
            var cadence = plan.Cadences?.Find(c => c.D365Id == intent.TargetId);
            if (cadence != null) ApplyChangesToModel(cadence, intent.Changes);
        }

        private void UpdateAction(IOrganizationService service, IntentResult intent, PlanResponse plan)
        {
            if (string.IsNullOrEmpty(intent.TargetId)) return;
            var id = Guid.Parse(intent.TargetId);
            var entity = new Entity("wrl_actionplan", id);

            ApplyChangesToEntity(entity, intent.Changes, "action");
            service.Update(entity);

            var action = plan.OneOffActions?.Find(a => a.D365Id == intent.TargetId);
            if (action != null) ApplyChangesToModel(action, intent.Changes);
        }

        private void UpdateRecommendation(IOrganizationService service, IntentResult intent, PlanResponse plan)
        {
            if (string.IsNullOrEmpty(intent.TargetId)) return;
            var id = Guid.Parse(intent.TargetId);
            var entity = new Entity("wrl_planrecommendation", id);

            ApplyChangesToEntity(entity, intent.Changes, "recommendation");
            service.Update(entity);

            var rec = plan.Recommendations?.Find(r => r.D365Id == intent.TargetId);
            if (rec != null) ApplyChangesToModel(rec, intent.Changes);
        }

        private void AddCadence(IOrganizationService service, Guid planId, IntentResult intent, PlanResponse plan)
        {
            if (!intent.Changes.HasValue) return;

            var changes = intent.Changes.Value;
            var entity  = new Entity("wrl_engagementcadence");

            entity["wrl_cadencename"]          = GetString(changes, "name") ?? "New Cadence";
            entity["wrl_contactname"]          = GetString(changes, "contactName") ?? "";
            entity["wrl_frequency"]            = new OptionSetValue(Helpers.MapFrequency(GetString(changes, "frequency")));
            entity["wrl_communicationchannel"] = new OptionSetValue(Helpers.MapChannel(GetString(changes, "channel")));
            entity["wrl_purpose"]              = GetString(changes, "purpose") ?? "";
            entity["wrl_rationale"]            = GetString(changes, "rationale") ?? "";
            entity["wrl_AccountPlan@odata.bind"] = $"/wrl_accountplans({planId})";

            var newId = service.Create(entity);

            // Add to in-memory plan
            if (plan.Cadences == null) plan.Cadences = new System.Collections.Generic.List<CadenceItem>();
            plan.Cadences.Add(new CadenceItem
            {
                D365Id      = newId.ToString(),
                Name        = GetString(changes, "name"),
                ContactName = GetString(changes, "contactName"),
                Frequency   = GetString(changes, "frequency"),
                Channel     = GetString(changes, "channel"),
                Purpose     = GetString(changes, "purpose")
            });
        }

        private void AddAction(IOrganizationService service, Guid planId, IntentResult intent, PlanResponse plan)
        {
            if (!intent.Changes.HasValue) return;

            var changes = intent.Changes.Value;
            var entity  = new Entity("wrl_actionplan");

            entity["wrl_actiondescription"]    = GetString(changes, "description") ?? "";
            entity["wrl_prioritylevel"]        = new OptionSetValue(Helpers.MapPriority(GetString(changes, "priority")));
            entity["wrl_communicationchannel"] = new OptionSetValue(Helpers.MapChannel(GetString(changes, "channel")));
            entity["wrl_suggestedtiming"]      = GetString(changes, "suggestedTiming") ?? "";
            entity["wrl_rationale"]            = GetString(changes, "rationale") ?? "";
            entity["wrl_currentstatus"]        = new OptionSetValue(0); // To Do
            entity["wrl_AccountPlan@odata.bind"] = $"/wrl_accountplans({planId})";

            var newId = service.Create(entity);

            if (plan.OneOffActions == null) plan.OneOffActions = new System.Collections.Generic.List<ActionItem>();
            plan.OneOffActions.Add(new ActionItem
            {
                D365Id          = newId.ToString(),
                Description     = GetString(changes, "description"),
                Priority        = GetString(changes, "priority"),
                Channel         = GetString(changes, "channel"),
                SuggestedTiming = GetString(changes, "suggestedTiming")
            });
        }

        private void DeleteCadence(IOrganizationService service, IntentResult intent, PlanResponse plan)
        {
            if (string.IsNullOrEmpty(intent.TargetId)) return;
            service.Delete("wrl_engagementcadence", Guid.Parse(intent.TargetId));
            plan.Cadences?.RemoveAll(c => c.D365Id == intent.TargetId);
        }

        private void DeleteAction(IOrganizationService service, IntentResult intent, PlanResponse plan)
        {
            if (string.IsNullOrEmpty(intent.TargetId)) return;
            service.Delete("wrl_actionplan", Guid.Parse(intent.TargetId));
            plan.OneOffActions?.RemoveAll(a => a.D365Id == intent.TargetId);
        }

        // ── Save updated payload back to Dataverse ────────────────────────────
        private void SavePayload(IOrganizationService service, Guid planId, PlanResponse plan)
        {
            var update = new Entity("wrl_accountplan", planId);
            update["wrl_planpayload"] = JsonSerializer.Serialize(plan,
                new JsonSerializerOptions { DefaultIgnoreCondition = 
                    System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            service.Update(update);
        }

        // ── Complex refinement fallback (full JSON to model) ──────────────────
        private string HandleComplexRefine(
            IOrganizationService service,
            Guid planId,
            PlanResponse plan,
            string planPayloadJson,
            string userMessage,
            string endpoint, string apiKey, string deployment, string apiVersion,
            out bool isPlanUpdate)
        {
            var refinePrompt = GetIntentPrompt(service, "refine");
            if (string.IsNullOrEmpty(refinePrompt))
                refinePrompt = GetDefaultRefinePrompt();

            var aiClient = new AzureOpenAIClient(endpoint, apiKey, deployment, apiVersion);
            var updatedJson = aiClient.RefineWithFullContext(planPayloadJson, userMessage, refinePrompt);

            var updatedPlan = JsonSerializer.Deserialize<PlanResponse>(updatedJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (updatedPlan != null)
            {
                var update = new Entity("wrl_accountplan", planId);
                update["wrl_planpayload"] = updatedJson;
                service.Update(update);
                isPlanUpdate = true;
                return "Plan updated based on your instructions.";
            }

            isPlanUpdate = false;
            return "I was unable to process that request. Please try rephrasing.";
        }

        // ── Apply classifier changes to Dataverse entity ──────────────────────
        private void ApplyChangesToEntity(Entity entity, JsonElement? changes, string type)
        {
            if (!changes.HasValue) return;
            var c = changes.Value;

            if (type == "cadence")
            {
                if (TryGetString(c, "frequency", out var freq))
                    entity["wrl_frequency"] = new OptionSetValue(Helpers.MapFrequency(freq));
                if (TryGetString(c, "channel", out var chan))
                    entity["wrl_communicationchannel"] = new OptionSetValue(Helpers.MapChannel(chan));
                if (TryGetString(c, "purpose", out var purpose))
                    entity["wrl_purpose"] = purpose;
                if (TryGetString(c, "rationale", out var rat))
                    entity["wrl_rationale"] = rat;
                if (TryGetString(c, "name", out var name))
                    entity["wrl_cadencename"] = name;
                if (TryGetString(c, "contactName", out var cn))
                    entity["wrl_contactname"] = cn;
            }
            else if (type == "action")
            {
                if (TryGetString(c, "description", out var desc))
                    entity["wrl_actiondescription"] = desc;
                if (TryGetString(c, "priority", out var pri))
                    entity["wrl_prioritylevel"] = new OptionSetValue(Helpers.MapPriority(pri));
                if (TryGetString(c, "channel", out var chan))
                    entity["wrl_communicationchannel"] = new OptionSetValue(Helpers.MapChannel(chan));
                if (TryGetString(c, "suggestedTiming", out var st))
                    entity["wrl_suggestedtiming"] = st;
                if (TryGetString(c, "rationale", out var rat))
                    entity["wrl_rationale"] = rat;
            }
            else if (type == "recommendation")
            {
                if (TryGetString(c, "productName", out var pn))
                    entity["wrl_ProductName"] = pn;
                if (TryGetString(c, "description", out var desc))
                    entity["wrl_Description"] = desc;
                if (TryGetString(c, "rationale", out var rat))
                    entity["wrl_Rationale"] = rat;
                if (TryGetString(c, "confidence", out var conf))
                    entity["wrl_Confidence"] = new OptionSetValue(Helpers.MapConfidence(conf));
                if (c.TryGetProperty("estimatedValue", out var ev) && ev.TryGetDouble(out var evd))
                    entity["wrl_EstimatedValue"] = new Money((decimal)evd);
            }
        }

        private void ApplyChangesToModel(object item, JsonElement? changes)
        {
            if (!changes.HasValue) return;
            var c = changes.Value;

            if (item is CadenceItem cad)
            {
                if (TryGetString(c, "frequency",   out var v)) cad.Frequency  = v;
                if (TryGetString(c, "channel",     out var ch)) cad.Channel   = ch;
                if (TryGetString(c, "purpose",     out var p))  cad.Purpose   = p;
                if (TryGetString(c, "name",        out var n))  cad.Name      = n;
                if (TryGetString(c, "contactName", out var cn)) cad.ContactName = cn;
            }
            else if (item is ActionItem act)
            {
                if (TryGetString(c, "description",    out var d))  act.Description    = d;
                if (TryGetString(c, "priority",       out var p))  act.Priority       = p;
                if (TryGetString(c, "channel",        out var ch)) act.Channel        = ch;
                if (TryGetString(c, "suggestedTiming",out var st)) act.SuggestedTiming = st;
            }
            else if (item is RecommendationItem rec)
            {
                if (TryGetString(c, "productName",  out var pn)) rec.ProductName = pn;
                if (TryGetString(c, "confidence",   out var cf)) rec.Confidence  = cf;
                if (TryGetString(c, "description",  out var d))  rec.Description = d;
                if (c.TryGetProperty("estimatedValue", out var ev) && ev.TryGetDouble(out var evd))
                    rec.EstimatedValue = evd;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private bool TryGetString(JsonElement element, string property, out string value)
        {
            if (element.TryGetProperty(property, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                value = prop.GetString();
                return !string.IsNullOrEmpty(value);
            }
            value = null;
            return false;
        }

        private string GetString(JsonElement element, string property)
        {
            TryGetString(element, property, out var val);
            return val;
        }

        private string GetIntentPrompt(IOrganizationService service, string intentType)
        {
            var query = new QueryExpression("wrl_intentprompt")
            {
                ColumnSet = new ColumnSet("wrl_systemprompt")
            };
            query.Criteria.AddCondition("wrl_intenttype", ConditionOperator.Equal, intentType);
            var results = service.RetrieveMultiple(query);
            return results.Entities.Count > 0
                ? results.Entities[0].GetAttributeValue<string>("wrl_systemprompt")
                : null;
        }

        private string GetEnvironmentVariable(IOrganizationService service, string schemaName)
        {
            var query = new QueryExpression("environmentvariablevalue")
            {
                ColumnSet = new ColumnSet("value")
            };
            query.Criteria.AddCondition("schemaname", ConditionOperator.Equal, schemaName);
            var results = service.RetrieveMultiple(query);
            return results.Entities.Count > 0
                ? results.Entities[0].GetAttributeValue<string>("value")
                : null;
        }

        private string GetDefaultClassifierPrompt()
        {
            return @"You are an intent classifier for an enterprise CRM account planning copilot.
You receive a stripped plan context (cadences, actions, recommendations, contacts with their d365Id values) and a user instruction.
Return ONLY a JSON object with these fields:
- action: one of update_cadence | update_action | update_recommendation | add_cadence | add_action | delete_cadence | delete_action | query | complex_refine
- targetId: the d365Id of the record to change (null for add, query, or complex_refine)
- changes: an object containing only the fields to change with their new values (null for query)
- response: a brief natural language confirmation to show the user (1 sentence)

Field names for changes must match these exact keys:
  Cadence: name, contactName, frequency, channel, purpose, rationale
  Action: description, priority, channel, suggestedTiming, rationale
  Recommendation: productName, description, rationale, confidence, estimatedValue

Frequency values must be one of: monthly, biweekly, quarterly, weekly, ad-hoc
Channel values must be one of: phone, online-meeting, in-person, email, other
Priority values must be one of: high, medium, low
Confidence values must be one of: high, medium, low

Use complex_refine only if the instruction requires changes to multiple different record types simultaneously or requires understanding the full plan narrative.
Use query for questions that do not require any record changes.
Return JSON only. No markdown. No explanation.";
        }

        private string GetDefaultRefinePrompt()
        {
            return @"You are a plan refinement assistant for an enterprise CRM account planning system.
You receive a complete account plan JSON and a user instruction.
Return ONLY the updated plan JSON with the requested changes applied.
Rules:
- Preserve all d365Id and d365ContactId values exactly — never modify or remove them
- Only change what the user explicitly requested
- Keep all other fields identical to the input
- Return valid JSON only — no markdown, no explanation
- Start your response with { and end with }";
        }
    }
}
```

---

### Updated file: `Services/AzureOpenAIClient.cs`

Add a `RefineWithFullContext` method to the existing class. Do not change the existing `GeneratePlan` method or constructor:

```csharp
/// <summary>
/// Sends the full plan JSON to the model for complex multi-step refinements.
/// Used only as a fallback when intent classification returns complex_refine.
/// </summary>
public string RefineWithFullContext(string planJson, string userMessage, string systemPrompt)
{
    var requestBody = new
    {
        model = _deployment,
        max_tokens = 4000,
        temperature = 0,
        messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = $"Current plan:\n{planJson}\n\nInstruction: {userMessage}" }
        }
    };

    var json = System.Text.Json.JsonSerializer.Serialize(requestBody);

    using (var client = new System.Net.Http.HttpClient())
    {
        client.DefaultRequestHeaders.Add("api-key", _apiKey);
        var url = $"{_endpoint}openai/deployments/{_deployment}/chat/completions?api-version={_apiVersion}";
        var response = client.PostAsync(url,
            new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")).Result;
        var responseJson = response.Content.ReadAsStringAsync().Result;

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"[AccordIn] RefineWithFullContext error: {responseJson}");

        using var doc = System.Text.Json.JsonDocument.Parse(responseJson);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        // Strip markdown fences
        var cleaned = content?.Trim();
        if (cleaned != null && cleaned.StartsWith("```"))
        {
            var start = cleaned.IndexOf('\n') + 1;
            var end   = cleaned.LastIndexOf("```");
            if (end > start) cleaned = cleaned.Substring(start, end - start).Trim();
        }

        return cleaned;
    }
}
```

---

### Updated file: `Services/Helpers.cs` (or wherever the map methods live)

Ensure these static methods exist and use the correct D365 option set values:

```csharp
public static int MapFrequency(string f) => (f?.ToLower().Trim()) switch
{
    "monthly"   => 0,
    "biweekly"  => 1,
    "quarterly" => 2,
    "weekly"    => 3,
    "ad-hoc"    => 4,
    _           => 0
};

public static int MapChannel(string c) => (c?.ToLower().Trim()) switch
{
    "phone"          => 0,
    "online-meeting" => 1,
    "in-person"      => 2,
    "email"          => 3,
    "other"          => 4,
    _                => 1
};

public static int MapPriority(string p) => (p?.ToLower().Trim()) switch
{
    "high"   => 0,
    "medium" => 1,
    "low"    => 2,
    _        => 1
};

public static int MapConfidence(string c) => (c?.ToLower().Trim()) switch
{
    "high"   => 0,
    "medium" => 1,
    "low"    => 2,
    _        => 1
};
```

---

## Intent Prompt Records to Add in D365

### classify-intent system prompt

Paste this into the `wrl_SystemPrompt` field of the `classify-intent` record:

```
You are an intent classifier for an enterprise CRM account planning copilot.
You receive a stripped plan context (cadences, actions, recommendations, contacts with their d365Id values) and a user instruction.
Return ONLY a JSON object with these fields:
- action: one of update_cadence | update_action | update_recommendation | add_cadence | add_action | delete_cadence | delete_action | query | complex_refine
- targetId: the d365Id of the record to change (null for add, query, or complex_refine)
- changes: an object containing only the fields to change with their new values (null for query)
- response: a brief natural language confirmation to show the user (1 sentence)

Field names for changes must match these exact keys:
  Cadence: name, contactName, frequency, channel, purpose, rationale
  Action: description, priority, channel, suggestedTiming, rationale
  Recommendation: productName, description, rationale, confidence, estimatedValue

Frequency values: monthly, biweekly, quarterly, weekly, ad-hoc
Channel values: phone, online-meeting, in-person, email, other
Priority values: high, medium, low
Confidence values: high, medium, low

Use complex_refine only if the instruction requires changes to multiple record types simultaneously or requires understanding the full plan narrative.
Use query for questions that do not require any record changes.
Return JSON only. No markdown. No explanation.
```

### refine system prompt

Paste this into the `wrl_SystemPrompt` field of the `refine` record:

```
You are a plan refinement assistant for an enterprise CRM account planning system.
You receive a complete account plan JSON and a user instruction.
Return ONLY the updated plan JSON with the requested changes applied.
Rules:
- Preserve all d365Id and d365ContactId values exactly — never modify or remove them
- Only change what the user explicitly requested
- Keep all other fields identical to the input
- Return valid JSON only — no markdown, no explanation
- Start your response with { and end with }
```

---

## Constraints

- Target framework is .NET Framework 4.6.2
- Switch expressions with `=>` require C# 8 — if `<LangVersion>` is below 8, replace all switch expressions with if/else chains or Dictionary lookups
- Do not add new NuGet packages
- Do not change Custom API message names
- Do not remove the `.snk` signing key
- `System.Text.Json` is already available — use it throughout
- `HttpClient` is already used in `AzureOpenAIClient.cs` — follow the same pattern in `IntentClassifier.cs`

---

## Definition of Done

1. Assembly builds with 0 errors and 0 warnings
2. "Change Sophia's cadence to weekly" — plugin classifies as `update_cadence`, calls `service.Update()` on the correct record, saves updated payload, returns `IsPlanUpdate = true`
3. "Add an action to send the new product catalog by email next month" — classifies as `add_action`, creates a new `wrl_actionplan` record, injects the new GUID into the payload, returns `IsPlanUpdate = true`
4. "Why is the Digital Supply Chain Bundle the top recommendation?" — classifies as `query`, returns a natural language response, `IsPlanUpdate = false`, no Dataverse writes
5. "Restructure the entire contact engagement plan" — classifies as `complex_refine`, falls through to `RefineWithFullContext`, full plan JSON is sent to the model
6. Approve button — sets `wrl_planstatus = 3` and `wrl_confirmationtimestamp`, returns `IsPlanUpdate = false`
7. After re-registering in D365, all test cases above pass without error in the plugin trace log

---

## Suggested Approach for Codex

1. Read all existing files before writing any code
2. Create `IntentClassifier.cs` first and verify it compiles in isolation
3. Add `RefineWithFullContext` to `AzureOpenAIClient.cs`
4. Update `RefinePlan.cs` last — it depends on both of the above
5. Do not modify `GeneratePlan.cs` or any model files in `Models/` unless a missing property is found
