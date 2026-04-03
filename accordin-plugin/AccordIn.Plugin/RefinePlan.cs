using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using AccordIn.Plugin.Models;
using AccordIn.Plugin.Services;

namespace AccordIn.Plugin
{
    public class RefinePlan : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = serviceFactory.CreateOrganizationService(context.UserId);
            var tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            try
            {
                Run(context, service, tracingService);
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracingService?.Trace($"[AccordIn] RefinePlan unhandled exception: {ex}");
                throw new InvalidPluginExecutionException($"AccordIn RefinePlan failed: {ex.Message}", ex);
            }
        }

        private static void Run(
            IPluginExecutionContext context,
            IOrganizationService service,
            ITracingService tracingService)
        {
            var planId = ReadPlanId(context);
            var userMessage = ReadOptionalString(context, "UserMessage", null);
            var actionType = ReadOptionalString(context, "ActionType", "refine");

            var endpoint = GetEnvironmentVariable(service, "wrl_accordin_AzureOpenAIEndpoint");
            var apiKey = GetEnvironmentVariable(service, "wrl_accordin_AzureOpenAIKey");
            var mainDeployment = GetEnvironmentVariable(service, "wrl_accordin_ModelDeployment");
            var classifierDeployment = GetOptionalEnvironmentVariable(service, "wrl_accordin_ClassifierModelDeployment") ?? mainDeployment;
            var apiVersion = GetEnvironmentVariable(service, "wrl_accordin_AzureOpenAIApiVersion");

            tracingService?.Trace($"[AccordIn] RefinePlan - plan {planId}, actionType '{actionType}'");

            if (string.Equals(actionType, "approve", StringComparison.OrdinalIgnoreCase))
            {
                var approveEntity = new Entity("wrl_accountplan", planId)
                {
                    ["wrl_planstatus"] = new OptionSetValue(1),
                    ["wrl_confirmationtimestamp"] = DateTime.UtcNow
                };

                service.Update(approveEntity);
                context.OutputParameters["ResponseMessage"] = "Plan approved. Status updated to Active.";
                context.OutputParameters["IsPlanUpdate"] = false;
                return;
            }

            var planRecord = service.Retrieve("wrl_accountplan", planId, new ColumnSet(
                "wrl_planpayload",
                "wrl_aiopeningstatement",
                "wrl_healthsummary",
                "wrl_growthobjectives",
                "wrl_revenuetarget",
                "wrl_confirmationtimestamp",
                "wrl_generatedtimestamp",
                "wrl_account"));

            if (string.Equals(actionType, "health-check", StringComparison.OrdinalIgnoreCase))
            {
                var healthResponse = HandleHealthCheck(service, planId, planRecord, tracingService);
                context.OutputParameters["ResponseMessage"] = healthResponse;
                context.OutputParameters["IsPlanUpdate"] = false;
                return;
            }

            if (string.IsNullOrWhiteSpace(userMessage))
                throw new InvalidPluginExecutionException("UserMessage is required for refinement.");

            var planPayloadJson = planRecord.GetAttributeValue<string>("wrl_planpayload");

            if (string.IsNullOrWhiteSpace(planPayloadJson))
                throw new InvalidPluginExecutionException("Plan payload is empty. Regenerate the plan first.");

            var plan = JsonConvert.DeserializeObject<PlanResponse>(planPayloadJson);
            if (plan == null)
                throw new InvalidPluginExecutionException("Plan payload could not be parsed.");

            var planContext = BuildPlanContext(plan);
            var classifierPrompt = GetIntentPrompt(service, tracingService, "classify-intent", GetDefaultClassifierPrompt());

            tracingService?.Trace("[AccordIn] RefinePlan - classifying intent");
            var classifier = new IntentClassifier(endpoint, apiKey, classifierDeployment, apiVersion);
            var intent = classifier.Classify(userMessage, planContext, classifierPrompt) ?? new IntentResult
            {
                Action = "query",
                Response = "I could not understand that request."
            };

            tracingService?.Trace($"[AccordIn] Intent classified as '{intent.Action}' for target '{intent.TargetId}'");

            var refineExecutor = new RefineExecutor(service, tracingService);
            var outcome = refineExecutor.Execute(planId, plan, planPayloadJson, userMessage, intent, endpoint, apiKey, mainDeployment, apiVersion);

            context.OutputParameters["ResponseMessage"] = outcome.ResponseMessage;
            context.OutputParameters["IsPlanUpdate"] = outcome.IsPlanUpdate;
        }

        private static string HandleHealthCheck(
            IOrganizationService service,
            Guid planId,
            Entity planRecord,
            ITracingService tracingService)
        {
            tracingService?.Trace("[AccordIn] RefinePlan - health-check requested");

            var approvedAt = planRecord.GetAttributeValue<DateTime?>("wrl_confirmationtimestamp");
            var generatedAt = planRecord.GetAttributeValue<DateTime?>("wrl_generatedtimestamp");
            var baseline = approvedAt ?? generatedAt ?? DateTime.UtcNow.AddDays(-30);

            tracingService?.Trace($"[AccordIn] Health-check baseline for plan {planId}: {baseline:yyyy-MM-dd}");

            var accountRef = planRecord.GetAttributeValue<EntityReference>("wrl_account");
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
            signalQuery.Criteria.AddCondition("wrl_account", ConditionOperator.Equal, accountRef.Id);
            signalQuery.Criteria.AddCondition("wrl_signaltimestamp", ConditionOperator.GreaterThan, baseline);
            signalQuery.AddOrder("wrl_signaltimestamp", OrderType.Descending);

            var newSignals = service.RetrieveMultiple(signalQuery).Entities;

            tracingService?.Trace($"[AccordIn] Health-check: {newSignals.Count} new signals since {baseline:yyyy-MM-dd}");

            if (newSignals.Count == 0)
            {
                var baselineLabel = approvedAt.HasValue ? "plan approval" : "plan generation";
                return $"No new signals have been recorded since {baselineLabel} on " +
                       $"{baseline:dd MMM yyyy}. The account data is unchanged. " +
                       "The approved plan remains current.";
            }

            var sb = new StringBuilder();
            var baselineDesc = approvedAt.HasValue ? "this plan was approved" : "this plan was generated";
            sb.AppendLine($"Since {baselineDesc} on {baseline:dd MMM yyyy}, " +
                          $"{newSignals.Count} new signal{(newSignals.Count > 1 ? "s have" : " has")} " +
                          "been recorded for this account:");
            sb.AppendLine();

            foreach (var signal in newSignals)
            {
                var summary = signal.GetAttributeValue<string>("wrl_signalsummary") ?? "No summary";
                var timestamp = signal.GetAttributeValue<DateTime?>("wrl_signaltimestamp") ?? baseline;
                var sentiment = signal.GetAttributeValue<OptionSetValue>("wrl_sentimentstatus")?.Value;
                var sentimentLabel = "Neutral";
                if (sentiment == 1)
                    sentimentLabel = "Positive";
                else if (sentiment == 0 || sentiment == 3)
                    sentimentLabel = "Risk";

                sb.AppendLine($"- [{sentimentLabel}] {timestamp:dd MMM yyyy}: {summary}");
            }

            sb.AppendLine();

            var riskSignals = new List<Entity>();
            foreach (var signal in newSignals)
            {
                var sentiment = signal.GetAttributeValue<OptionSetValue>("wrl_sentimentstatus")?.Value;
                if (sentiment == 0 || sentiment == 3)
                    riskSignals.Add(signal);
            }

            if (riskSignals.Count > 0)
            {
                sb.AppendLine($"{riskSignals.Count} of these signal{(riskSignals.Count > 1 ? "s are" : " is")} " +
                              "flagged as risk. You may want to review the plan cadences and actions " +
                              "to address these. Ask me to update the plan if needed.");
            }
            else
            {
                sb.AppendLine("No risk signals detected. The new signals are informational. " +
                              "The approved plan remains appropriate.");
            }

            return sb.ToString().Trim();
        }

        private static PlanContext BuildPlanContext(PlanResponse plan)
        {
            var ctx = new PlanContext();

            foreach (var cadence in plan.Cadences ?? new List<Cadence>())
            {
                ctx.Cadences.Add(new CadenceContext
                {
                    D365Id = cadence.D365Id,
                    ContactName = cadence.ContactName ?? cadence.ContactTitle,
                    Name = cadence.Name,
                    Frequency = cadence.Frequency,
                    Channel = cadence.Channel,
                    Purpose = cadence.Purpose
                });
            }

            foreach (var action in plan.OneOffActions ?? new List<OneOffAction>())
            {
                ctx.Actions.Add(new ActionContext
                {
                    D365Id = action.D365Id,
                    Description = action.Description,
                    Priority = action.Priority,
                    Channel = action.Channel,
                    SuggestedTiming = action.SuggestedTiming
                });
            }

            foreach (var recommendation in plan.Recommendations ?? new List<Recommendation>())
            {
                ctx.Recommendations.Add(new RecommendationContext
                {
                    D365Id = recommendation.D365Id,
                    ProductName = recommendation.ProductName,
                    Description = recommendation.Description,
                    Rationale = recommendation.Rationale,
                    Confidence = recommendation.Confidence,
                    EstimatedValue = (double)recommendation.EstimatedValue
                });
            }

            foreach (var contact in plan.ContactEngagement ?? new List<ContactEngagementItem>())
            {
                ctx.Contacts.Add(new ContactContext
                {
                    D365ContactId = contact.D365ContactId,
                    Name = contact.Name,
                    PlanRole = contact.PlanRole
                });
            }

            return ctx;
        }

        private static string GetIntentPrompt(
            IOrganizationService service,
            ITracingService tracingService,
            string intentType,
            string fallback)
        {
            var query = new QueryExpression("wrl_intentprompt")
            {
                ColumnSet = new ColumnSet("wrl_systemprompt"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wrl_intenttype", ConditionOperator.Equal, intentType)
                    }
                },
                TopCount = 1
            };

            var results = service.RetrieveMultiple(query).Entities;
            if (results.Count == 0)
            {
                tracingService?.Trace($"[AccordIn] No intent prompt found for '{intentType}', using fallback");
                return fallback;
            }

            var prompt = results[0].GetAttributeValue<string>("wrl_systemprompt");
            if (!string.IsNullOrWhiteSpace(prompt))
                return prompt.Trim();

            try
            {
                var initResponse = (InitializeFileBlocksDownloadResponse)service.Execute(
                    new InitializeFileBlocksDownloadRequest
                    {
                        Target = results[0].ToEntityReference(),
                        FileAttributeName = "wrl_systempromptfile"
                    });

                var downloadResponse = (DownloadBlockResponse)service.Execute(
                    new DownloadBlockRequest
                    {
                        FileContinuationToken = initResponse.FileContinuationToken,
                        Offset = 0,
                        BlockLength = initResponse.FileSizeInBytes
                    });

                var filePrompt = Encoding.UTF8.GetString(downloadResponse.Data).Trim();
                if (!string.IsNullOrWhiteSpace(filePrompt))
                    return filePrompt;
            }
            catch (Exception ex)
            {
                tracingService?.Trace($"[AccordIn] Prompt file download failed for '{intentType}': {ex.Message}");
            }

            return fallback;
        }

        private static Guid ReadPlanId(IPluginExecutionContext context)
        {
            if (context.InputParameters.Contains("PlanId"))
            {
                var raw = context.InputParameters["PlanId"];

                if (raw is EntityReference entityReference)
                    return entityReference.Id;

                if (raw is string text && Guid.TryParse(text, out var parsed))
                    return parsed;
            }

            if (context.PrimaryEntityName == "wrl_accountplan" && context.PrimaryEntityId != Guid.Empty)
                return context.PrimaryEntityId;

            throw new InvalidPluginExecutionException("PlanId is required.");
        }

        private static string ReadOptionalString(IPluginExecutionContext context, string name, string defaultValue)
        {
            if (context.InputParameters.Contains(name))
            {
                var value = context.InputParameters[name] as string;
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return defaultValue;
        }

        private static string GetEnvironmentVariable(IOrganizationService service, string schemaName)
        {
            var value = GetOptionalEnvironmentVariable(service, schemaName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            throw new InvalidPluginExecutionException(
                $"AccordIn: environment variable '{schemaName}' is missing or has no value.");
        }

        private static string GetOptionalEnvironmentVariable(IOrganizationService service, string schemaName)
        {
            var query = new QueryExpression("environmentvariabledefinition")
            {
                ColumnSet = new ColumnSet("defaultvalue"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("schemaname", ConditionOperator.Equal, schemaName)
                    }
                },
                TopCount = 1
            };

            var valueLink = query.AddLink(
                "environmentvariablevalue",
                "environmentvariabledefinitionid",
                "environmentvariabledefinitionid",
                JoinOperator.LeftOuter);
            valueLink.Columns = new ColumnSet("value");
            valueLink.EntityAlias = "envval";

            var results = service.RetrieveMultiple(query).Entities;
            if (results.Count == 0)
                return null;

            var definition = results[0];
            var currentValue = (definition.GetAttributeValue<AliasedValue>("envval.value")?.Value as string)?.Trim();
            var defaultValue = definition.GetAttributeValue<string>("defaultvalue")?.Trim();
            return string.IsNullOrWhiteSpace(currentValue) ? defaultValue : currentValue;
        }

        private static string GetDefaultClassifierPrompt()
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
For query responses, answer directly from the stripped plan context. Recommendation entries may include description and rationale to support ""why"" questions.
Return JSON only. No markdown. No explanation.";
        }

        private static string GetDefaultRefinePrompt()
        {
            return @"You are a plan refinement assistant for an enterprise CRM account planning system.
You receive a complete account plan JSON and a user instruction.
Return ONLY the updated plan JSON with the requested changes applied.
Rules:
- Preserve all d365Id and d365ContactId values exactly - never modify or remove them
- Only change what the user explicitly requested
- Keep all other fields identical to the input
- Return valid JSON only - no markdown, no explanation
- Start your response with { and end with }";
        }

        private sealed class RefineExecutor
        {
            private readonly IOrganizationService _service;
            private readonly ITracingService _tracingService;

            public RefineExecutor(IOrganizationService service, ITracingService tracingService)
            {
                _service = service;
                _tracingService = tracingService;
            }

            public RefineOutcome Execute(
                Guid planId,
                PlanResponse plan,
                string planPayloadJson,
                string userMessage,
                IntentResult intent,
                string endpoint,
                string apiKey,
                string deployment,
                string apiVersion)
            {
                var action = (intent?.Action ?? "query").Trim().ToLowerInvariant();

                switch (action)
                {
                    case "update_cadence":
                        UpdateCadence(intent, plan);
                        SavePayload(planId, plan);
                        return RefineOutcome.Updated(intent.Response ?? "Cadence updated.");
                    case "update_action":
                        UpdateAction(intent, plan);
                        SavePayload(planId, plan);
                        return RefineOutcome.Updated(intent.Response ?? "Action updated.");
                    case "update_recommendation":
                        UpdateRecommendation(intent, plan);
                        SavePayload(planId, plan);
                        return RefineOutcome.Updated(intent.Response ?? "Recommendation updated.");
                    case "add_cadence":
                        AddCadence(planId, intent, plan);
                        SavePayload(planId, plan);
                        return RefineOutcome.Updated(intent.Response ?? "Cadence added.");
                    case "add_action":
                        AddAction(planId, intent, plan);
                        SavePayload(planId, plan);
                        return RefineOutcome.Updated(intent.Response ?? "Action added.");
                    case "delete_cadence":
                        DeleteCadence(intent, plan);
                        SavePayload(planId, plan);
                        return RefineOutcome.Updated(intent.Response ?? "Cadence removed.");
                    case "delete_action":
                        DeleteAction(intent, plan);
                        SavePayload(planId, plan);
                        return RefineOutcome.Updated(intent.Response ?? "Action removed.");
                    case "query":
                        return RefineOutcome.Unchanged(intent.Response ?? "I could not find an answer to that.");
                    case "complex_refine":
                    default:
                        return HandleComplexRefine(planId, planPayloadJson, userMessage, endpoint, apiKey, deployment, apiVersion);
                }
            }

            private void UpdateCadence(IntentResult intent, PlanResponse plan)
            {
                var targetId = ParseRequiredGuid(intent?.TargetId, "cadence");
                var entity = new Entity("wrl_engagementcadence", targetId);
                ApplyChangesToEntity(entity, intent.Changes, "cadence");
                _service.Update(entity);

                var cadence = plan.Cadences?.Find(c => StringEquals(c.D365Id, intent.TargetId));
                if (cadence != null) ApplyChangesToModel(cadence, intent.Changes);
                SyncContactCadenceFlags(plan);
            }

            private void UpdateAction(IntentResult intent, PlanResponse plan)
            {
                var targetId = ParseRequiredGuid(intent?.TargetId, "action");
                var entity = new Entity("wrl_actionplan", targetId);
                ApplyChangesToEntity(entity, intent.Changes, "action");
                _service.Update(entity);

                var action = plan.OneOffActions?.Find(a => StringEquals(a.D365Id, intent.TargetId));
                if (action != null) ApplyChangesToModel(action, intent.Changes);
            }

            private void UpdateRecommendation(IntentResult intent, PlanResponse plan)
            {
                var targetId = ParseRequiredGuid(intent?.TargetId, "recommendation");
                var entity = new Entity("wrl_planrecommendation", targetId);
                ApplyChangesToEntity(entity, intent.Changes, "recommendation");
                _service.Update(entity);

                var recommendation = plan.Recommendations?.Find(r => StringEquals(r.D365Id, intent.TargetId));
                if (recommendation != null) ApplyChangesToModel(recommendation, intent.Changes);
            }

            private void AddCadence(Guid planId, IntentResult intent, PlanResponse plan)
            {
                if (!intent.Changes.HasValue)
                    throw new InvalidPluginExecutionException("Classifier did not return cadence changes.");

                var changes = intent.Changes.Value;
                var entity = new Entity("wrl_engagementcadence")
                {
                    ["wrl_accountplan"] = new EntityReference("wrl_accountplan", planId),
                    ["wrl_cadencename"] = GetString(changes, "name") ?? "New Cadence",
                    ["wrl_contactname"] = GetString(changes, "contactName") ?? string.Empty,
                    ["wrl_frequency"] = new OptionSetValue(Helpers.MapFrequency(GetString(changes, "frequency"))),
                    ["wrl_communicationchannel"] = new OptionSetValue(Helpers.MapCadenceChannel(GetString(changes, "channel"))),
                    ["wrl_purpose"] = GetString(changes, "purpose") ?? string.Empty,
                    ["wrl_rationale"] = GetString(changes, "rationale") ?? string.Empty,
                    ["wrl_status"] = new OptionSetValue(1),
                    ["wrl_startdate"] = DateTime.UtcNow,
                    ["wrl_manageradjustment"] = true
                };

                if (TryGetBoolean(changes, "locationAware", out var locationAware))
                    entity["wrl_locationawareness"] = locationAware;

                var newId = _service.Create(entity);
                if (plan.Cadences == null) plan.Cadences = new List<Cadence>();
                plan.Cadences.Add(new Cadence
                {
                    D365Id = newId.ToString(),
                    D365ContactId = GetString(changes, "d365ContactId"),
                    Name = GetString(changes, "name"),
                    ContactName = GetString(changes, "contactName"),
                    ContactTitle = GetString(changes, "contactTitle"),
                    Frequency = GetString(changes, "frequency"),
                    Channel = GetString(changes, "channel"),
                    Purpose = GetString(changes, "purpose"),
                    Rationale = GetString(changes, "rationale"),
                    LocationAware = TryGetBoolean(changes, "locationAware", out var modelLocationAware) && modelLocationAware
                });
                SyncContactCadenceFlags(plan);
            }

            private void AddAction(Guid planId, IntentResult intent, PlanResponse plan)
            {
                if (!intent.Changes.HasValue)
                    throw new InvalidPluginExecutionException("Classifier did not return action changes.");

                var changes = intent.Changes.Value;
                var entity = new Entity("wrl_actionplan")
                {
                    ["wrl_accountplan"] = new EntityReference("wrl_accountplan", planId),
                    ["wrl_actiondescription"] = GetString(changes, "description") ?? string.Empty,
                    ["wrl_prioritylevel"] = new OptionSetValue(Helpers.MapPriority(GetString(changes, "priority"))),
                    ["wrl_communicationchannel"] = new OptionSetValue(Helpers.MapActionChannel(GetString(changes, "channel"))),
                    ["wrl_suggestedtiming"] = GetString(changes, "suggestedTiming") ?? string.Empty,
                    ["wrl_rationale"] = GetString(changes, "rationale") ?? string.Empty,
                    ["wrl_currentstatus"] = new OptionSetValue(1)
                };

                var newId = _service.Create(entity);
                if (plan.OneOffActions == null) plan.OneOffActions = new List<OneOffAction>();
                plan.OneOffActions.Add(new OneOffAction
                {
                    D365Id = newId.ToString(),
                    Description = GetString(changes, "description"),
                    Priority = GetString(changes, "priority"),
                    Channel = GetString(changes, "channel"),
                    SuggestedTiming = GetString(changes, "suggestedTiming"),
                    Rationale = GetString(changes, "rationale")
                });
            }

            private void DeleteCadence(IntentResult intent, PlanResponse plan)
            {
                var targetId = ParseRequiredGuid(intent?.TargetId, "cadence");
                _service.Delete("wrl_engagementcadence", targetId);
                plan.Cadences?.RemoveAll(c => StringEquals(c.D365Id, intent.TargetId));
                SyncContactCadenceFlags(plan);
            }

            private void DeleteAction(IntentResult intent, PlanResponse plan)
            {
                var targetId = ParseRequiredGuid(intent?.TargetId, "action");
                _service.Delete("wrl_actionplan", targetId);
                plan.OneOffActions?.RemoveAll(a => StringEquals(a.D365Id, intent.TargetId));
            }

            private RefineOutcome HandleComplexRefine(
                Guid planId,
                string planPayloadJson,
                string userMessage,
                string endpoint,
                string apiKey,
                string deployment,
                string apiVersion)
            {
                var refinePrompt = GetIntentPrompt(_service, _tracingService, "refine", GetDefaultRefinePrompt());
                var aiClient = new AzureOpenAIClient(endpoint, apiKey, deployment, apiVersion, _tracingService);
                var updatedJson = aiClient.RefineWithFullContext(planPayloadJson, userMessage, refinePrompt);

                var parseResult = AzureOpenAIClient.ParseResponse(updatedJson);
                if (!parseResult.Success || parseResult.Parsed == null)
                    throw new InvalidPluginExecutionException(
                        $"AccordIn RefinePlan: updated plan JSON could not be parsed - {parseResult.ParseError}");

                SaveFullPlanUpdate(planId, parseResult.Parsed);
                return RefineOutcome.Updated("Plan updated based on your instructions.");
            }

            private void SaveFullPlanUpdate(Guid planId, PlanResponse updatedPlan)
            {
                var payload = JsonConvert.SerializeObject(updatedPlan, Formatting.None);
                var headerUpdate = new Entity("wrl_accountplan", planId)
                {
                    ["wrl_planpayload"] = payload,
                    ["wrl_aiopeningstatement"] = updatedPlan.OpeningStatement,
                    ["wrl_healthsummary"] = updatedPlan.HealthSummary,
                    ["wrl_growthobjectives"] = updatedPlan.GrowthObjectives,
                    ["wrl_revenuetarget"] = new Money(updatedPlan.RevenueTarget)
                };
                _service.Update(headerUpdate);

                DeleteChildren(planId, "wrl_engagementcadence");
                DeleteChildren(planId, "wrl_actionplan");
                DeleteChildren(planId, "wrl_planrecommendation");

                CreateCadences(planId, updatedPlan.Cadences);
                CreateActions(planId, updatedPlan.OneOffActions);
                CreateRecommendations(planId, updatedPlan.Recommendations);
                SyncContactCadenceFlags(updatedPlan);

                _service.Update(new Entity("wrl_accountplan", planId)
                {
                    ["wrl_planpayload"] = JsonConvert.SerializeObject(updatedPlan, Formatting.None)
                });
            }

            private void SavePayload(Guid planId, PlanResponse plan)
            {
                SyncContactCadenceFlags(plan);
                _service.Update(new Entity("wrl_accountplan", planId)
                {
                    ["wrl_planpayload"] = JsonConvert.SerializeObject(plan, Formatting.None)
                });
            }

            private void DeleteChildren(Guid planId, string entityLogicalName)
            {
                var query = new QueryExpression(entityLogicalName)
                {
                    ColumnSet = new ColumnSet(false),
                    Criteria = new FilterExpression
                    {
                        Conditions =
                        {
                            new ConditionExpression("wrl_accountplan", ConditionOperator.Equal, planId)
                        }
                    }
                };

                foreach (var entity in _service.RetrieveMultiple(query).Entities)
                    _service.Delete(entityLogicalName, entity.Id);
            }

            private void CreateCadences(Guid planId, IList<Cadence> cadences)
            {
                if (cadences == null) return;
                foreach (var cadence in cadences)
                {
                    if (cadence == null) continue;
                    var entity = new Entity("wrl_engagementcadence")
                    {
                        ["wrl_accountplan"] = new EntityReference("wrl_accountplan", planId),
                        ["wrl_cadencename"] = cadence.Name,
                        ["wrl_contactname"] = cadence.ContactName ?? cadence.ContactTitle,
                        ["wrl_frequency"] = new OptionSetValue(Helpers.MapFrequency(cadence.Frequency)),
                        ["wrl_communicationchannel"] = new OptionSetValue(Helpers.MapCadenceChannel(cadence.Channel)),
                        ["wrl_purpose"] = cadence.Purpose,
                        ["wrl_rationale"] = cadence.Rationale,
                        ["wrl_locationawareness"] = cadence.LocationAware,
                        ["wrl_status"] = new OptionSetValue(1),
                        ["wrl_startdate"] = DateTime.UtcNow,
                        ["wrl_manageradjustment"] = true
                    };

                    cadence.D365Id = _service.Create(entity).ToString();
                }
            }

            private void CreateActions(Guid planId, IList<OneOffAction> actions)
            {
                if (actions == null) return;
                foreach (var action in actions)
                {
                    if (action == null) continue;
                    var entity = new Entity("wrl_actionplan")
                    {
                        ["wrl_accountplan"] = new EntityReference("wrl_accountplan", planId),
                        ["wrl_actiondescription"] = action.Description,
                        ["wrl_prioritylevel"] = new OptionSetValue(Helpers.MapPriority(action.Priority)),
                        ["wrl_communicationchannel"] = new OptionSetValue(Helpers.MapActionChannel(action.Channel)),
                        ["wrl_suggestedtiming"] = action.SuggestedTiming,
                        ["wrl_rationale"] = action.Rationale,
                        ["wrl_currentstatus"] = new OptionSetValue(1)
                    };

                    action.D365Id = _service.Create(entity).ToString();
                }
            }

            private void CreateRecommendations(Guid planId, IList<Recommendation> recommendations)
            {
                if (recommendations == null) return;

                var sortOrder = 1;
                foreach (var recommendation in recommendations)
                {
                    if (recommendation == null) continue;
                    var entity = new Entity("wrl_planrecommendation")
                    {
                        ["wrl_accountplan"] = new EntityReference("wrl_accountplan", planId),
                        ["wrl_productname"] = recommendation.ProductName,
                        ["wrl_recommendationtype"] = new OptionSetValue(Helpers.MapRecommendationType(recommendation.Type)),
                        ["wrl_description"] = recommendation.Description,
                        ["wrl_rationale"] = recommendation.Rationale,
                        ["wrl_confidence"] = new OptionSetValue(Helpers.MapConfidence(recommendation.Confidence)),
                        ["wrl_confidencereason"] = recommendation.ConfidenceReason,
                        ["wrl_sortorder"] = sortOrder++
                    };

                    if (recommendation.EstimatedValue > 0)
                        entity["wrl_estimatedvalue"] = new Money(recommendation.EstimatedValue);

                    recommendation.D365Id = _service.Create(entity).ToString();
                }
            }

            private static Guid ParseRequiredGuid(string value, string entityLabel)
            {
                if (Guid.TryParse(value, out var parsed))
                    return parsed;

                throw new InvalidPluginExecutionException($"Classifier did not return a valid {entityLabel} targetId.");
            }

            private static bool StringEquals(string left, string right)
            {
                return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            private static void SyncContactCadenceFlags(PlanResponse plan)
            {
                var cadenceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cadence in plan.Cadences ?? new List<Cadence>())
                {
                    if (!string.IsNullOrWhiteSpace(cadence.ContactName))
                        cadenceNames.Add(cadence.ContactName.Trim());
                    if (!string.IsNullOrWhiteSpace(cadence.ContactTitle))
                        cadenceNames.Add(cadence.ContactTitle.Trim());
                }

                foreach (var contact in plan.ContactEngagement ?? new List<ContactEngagementItem>())
                {
                    contact.HasCadence =
                        (!string.IsNullOrWhiteSpace(contact.Name) && cadenceNames.Contains(contact.Name.Trim())) ||
                        (!string.IsNullOrWhiteSpace(contact.Title) && cadenceNames.Contains(contact.Title.Trim()));
                }
            }

            private static void ApplyChangesToEntity(Entity entity, JsonElement? changes, string type)
            {
                if (!changes.HasValue) return;
                var current = changes.Value;

                if (type == "cadence")
                {
                    if (TryGetString(current, "name", out var name))
                        entity["wrl_cadencename"] = name;
                    if (TryGetString(current, "contactName", out var contactName))
                        entity["wrl_contactname"] = contactName;
                    if (TryGetString(current, "frequency", out var frequency))
                        entity["wrl_frequency"] = new OptionSetValue(Helpers.MapFrequency(frequency));
                    if (TryGetString(current, "channel", out var channel))
                        entity["wrl_communicationchannel"] = new OptionSetValue(Helpers.MapCadenceChannel(channel));
                    if (TryGetString(current, "purpose", out var purpose))
                        entity["wrl_purpose"] = purpose;
                    if (TryGetString(current, "rationale", out var rationale))
                        entity["wrl_rationale"] = rationale;
                    if (TryGetBoolean(current, "locationAware", out var locationAware))
                        entity["wrl_locationawareness"] = locationAware;
                }
                else if (type == "action")
                {
                    if (TryGetString(current, "description", out var description))
                        entity["wrl_actiondescription"] = description;
                    if (TryGetString(current, "priority", out var priority))
                        entity["wrl_prioritylevel"] = new OptionSetValue(Helpers.MapPriority(priority));
                    if (TryGetString(current, "channel", out var channel))
                        entity["wrl_communicationchannel"] = new OptionSetValue(Helpers.MapActionChannel(channel));
                    if (TryGetString(current, "suggestedTiming", out var suggestedTiming))
                        entity["wrl_suggestedtiming"] = suggestedTiming;
                    if (TryGetString(current, "rationale", out var rationale))
                        entity["wrl_rationale"] = rationale;
                }
                else if (type == "recommendation")
                {
                    if (TryGetString(current, "productName", out var productName))
                        entity["wrl_productname"] = productName;
                    if (TryGetString(current, "description", out var description))
                        entity["wrl_description"] = description;
                    if (TryGetString(current, "rationale", out var rationale))
                        entity["wrl_rationale"] = rationale;
                    if (TryGetString(current, "confidence", out var confidence))
                        entity["wrl_confidence"] = new OptionSetValue(Helpers.MapConfidence(confidence));
                    if (TryGetDecimal(current, "estimatedValue", out var estimatedValue))
                        entity["wrl_estimatedvalue"] = new Money(estimatedValue);
                }
            }

            private static void ApplyChangesToModel(Cadence cadence, JsonElement? changes)
            {
                if (!changes.HasValue) return;
                var current = changes.Value;
                if (TryGetString(current, "name", out var name)) cadence.Name = name;
                if (TryGetString(current, "contactName", out var contactName)) cadence.ContactName = contactName;
                if (TryGetString(current, "contactTitle", out var contactTitle)) cadence.ContactTitle = contactTitle;
                if (TryGetString(current, "frequency", out var frequency)) cadence.Frequency = frequency;
                if (TryGetString(current, "channel", out var channel)) cadence.Channel = channel;
                if (TryGetString(current, "purpose", out var purpose)) cadence.Purpose = purpose;
                if (TryGetString(current, "rationale", out var rationale)) cadence.Rationale = rationale;
                if (TryGetBoolean(current, "locationAware", out var locationAware)) cadence.LocationAware = locationAware;
            }

            private static void ApplyChangesToModel(OneOffAction action, JsonElement? changes)
            {
                if (!changes.HasValue) return;
                var current = changes.Value;
                if (TryGetString(current, "description", out var description)) action.Description = description;
                if (TryGetString(current, "priority", out var priority)) action.Priority = priority;
                if (TryGetString(current, "channel", out var channel)) action.Channel = channel;
                if (TryGetString(current, "suggestedTiming", out var suggestedTiming)) action.SuggestedTiming = suggestedTiming;
                if (TryGetString(current, "rationale", out var rationale)) action.Rationale = rationale;
            }

            private static void ApplyChangesToModel(Recommendation recommendation, JsonElement? changes)
            {
                if (!changes.HasValue) return;
                var current = changes.Value;
                if (TryGetString(current, "productName", out var productName)) recommendation.ProductName = productName;
                if (TryGetString(current, "description", out var description)) recommendation.Description = description;
                if (TryGetString(current, "rationale", out var rationale)) recommendation.Rationale = rationale;
                if (TryGetString(current, "confidence", out var confidence)) recommendation.Confidence = confidence;
                if (TryGetDecimal(current, "estimatedValue", out var estimatedValue)) recommendation.EstimatedValue = estimatedValue;
            }

            private static bool TryGetString(JsonElement element, string property, out string value)
            {
                value = null;
                if (!element.TryGetProperty(property, out var prop))
                    return false;

                if (prop.ValueKind == JsonValueKind.String)
                {
                    value = prop.GetString();
                    return !string.IsNullOrWhiteSpace(value);
                }

                return false;
            }

            private static string GetString(JsonElement element, string property)
            {
                return TryGetString(element, property, out var value) ? value : null;
            }

            private static bool TryGetBoolean(JsonElement element, string property, out bool value)
            {
                value = false;
                if (!element.TryGetProperty(property, out var prop))
                    return false;

                if (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
                {
                    value = prop.GetBoolean();
                    return true;
                }

                if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed))
                {
                    value = parsed;
                    return true;
                }

                return false;
            }

            private static bool TryGetDecimal(JsonElement element, string property, out decimal value)
            {
                value = 0m;
                if (!element.TryGetProperty(property, out var prop))
                    return false;

                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var decimalValue))
                {
                    value = decimalValue;
                    return true;
                }

                if (prop.ValueKind == JsonValueKind.String && decimal.TryParse(prop.GetString(), out var parsed))
                {
                    value = parsed;
                    return true;
                }

                return false;
            }
        }

        private sealed class RefineOutcome
        {
            public bool IsPlanUpdate { get; private set; }
            public string ResponseMessage { get; private set; }

            public static RefineOutcome Updated(string message)
            {
                return new RefineOutcome { IsPlanUpdate = true, ResponseMessage = message };
            }

            public static RefineOutcome Unchanged(string message)
            {
                return new RefineOutcome { IsPlanUpdate = false, ResponseMessage = message };
            }
        }
    }
}
