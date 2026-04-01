using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using AccordIn.Plugin.Models;
using AccordIn.Plugin.Services;

namespace AccordIn.Plugin
{
    /// <summary>
    /// D365 plugin bound to the accordin_RefinePlan custom action.
    ///
    /// Handles conversational refinement of an existing plan. On each turn:
    ///   1. Loads wrl_accountplan header (payload + account ref + plan type)
    ///   2. Loads wrl_conversationmessage history for this plan
    ///   3. Builds message list: system prompt + suffix → original plan → history → new user message
    ///   4. Calls the model (2500 tokens — chat mode)
    ///   5. If response contains [PLAN_UPDATE]: extracts JSON, post-processes, updates plan + child records
    ///   6. Saves user + assistant messages to wrl_conversationmessage
    ///   7. Returns explanation text and IsPlanUpdate flag to the caller
    ///
    /// Input parameters:
    ///   PlanId       (String)  — GUID of the wrl_accountplan to refine — required
    ///   UserMessage  (String)  — the AM's chat message — required
    ///
    /// Output parameters:
    ///   ResponseMessage  (String)   — plain-English response shown in the chat UI
    ///   IsPlanUpdate     (Boolean)  — true when the model returned an updated plan JSON
    /// </summary>
    public class RefinePlan : IPlugin
    {
        // Injected into the system message on the first chat turn — port of CONVERSATION_SYSTEM_SUFFIX
        // in copilot-service/src/routes/chat.js. Kept verbatim so model behaviour is identical.
        private const string ConversationSystemSuffix = @"

You are now in conversation mode helping an account manager refine their account plan.

CRITICAL RESPONSE RULES - follow these exactly:

RULE 1 - WHEN THE MANAGER ASKS FOR A CHANGE TO THE PLAN:
Any request that modifies cadences, recommendations, actions, revenue targets, or any other plan field
MUST return a response in this exact format with no deviation:

[PLAN_UPDATE]
{complete updated JSON plan object}
[END_PLAN]

After [END_PLAN] you may add a brief plain English explanation (max 2 sentences) of what changed and any trade-offs.

RULE 2 - WHEN THE MANAGER ASKS A QUESTION OR REQUESTS REASONING:
Respond in plain conversational English only. No JSON. Max 150 words.
Always reference specific data from the account when explaining. Never answer generically.

RULE 3 - HOW TO IDENTIFY A CHANGE REQUEST vs A QUESTION:
Change requests use words like: change, update, modify, reduce, increase, remove, add, make it, set it, switch
Questions use words like: why, what, how, explain, tell me, is this, should we

RULE 4 - THE JSON IN A PLAN UPDATE:
Must be the complete plan object - not just the changed section.
Must follow the exact same schema as the original plan response.
Must preserve all unchanged fields from the current plan.

RULE 5 - WHITESPACE AND TERMINOLOGY:
When asked about whitespace, revenue potential, pipeline, or any business term,
always answer in context of THIS specific account's data.
Never give a generic textbook definition.";

        // Maximum total messages sent to the model including the system message
        private const int MessageHistoryCap = 20;

        public void Execute(IServiceProvider serviceProvider)
        {
            var context        = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracer         = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var svc            = serviceFactory.CreateOrganizationService(context.InitiatingUserId);

            try
            {
                Run(context, svc, tracer);
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracer?.Trace($"[AccordIn] RefinePlan unhandled exception: {ex}");
                throw new InvalidPluginExecutionException($"AccordIn RefinePlan failed: {ex.Message}", ex);
            }
        }

        private static void Run(
            IPluginExecutionContext context,
            IOrganizationService    svc,
            ITracingService         tracer)
        {
            // ------------------------------------------------------------------
            // 1. Input parameters
            // ------------------------------------------------------------------
            var planId      = ReadPlanId(context);
            var userMessage = ReadRequiredString(context, "UserMessage");

            tracer?.Trace($"[AccordIn] RefinePlan — plan {planId}");

            // ------------------------------------------------------------------
            // 2. Load plan header
            // ------------------------------------------------------------------
            var planRecord = LoadPlan(svc, planId);

            // ------------------------------------------------------------------
            // 3. Load system prompt from wrl_intentprompt, append conversation suffix
            // ------------------------------------------------------------------
            var systemPrompt = ReadSystemPrompt(svc, planRecord.PlanType) + ConversationSystemSuffix;

            // ------------------------------------------------------------------
            // 4. Load conversation history
            // ------------------------------------------------------------------
            var history     = LoadConversationHistory(svc, planId);
            var nextSeq     = history.Count > 0 ? history.Max(h => h.Sequence) + 1 : 1;

            tracer?.Trace($"[AccordIn] Loaded {history.Count} prior conversation messages");

            // ------------------------------------------------------------------
            // 5. Build capped message list for the model call
            //    System → plan payload (as assistant context) → history → new user turn
            // ------------------------------------------------------------------
            var messages = BuildMessages(systemPrompt, planRecord.Payload, history, userMessage);

            // ------------------------------------------------------------------
            // 6. Call model in chat mode (2500 tokens)
            // ------------------------------------------------------------------
            var aiClient = AzureOpenAIClient.FromEnvironmentVariables(svc, tracer);
            var rawText  = aiClient.Call(messages, maxTokens: 2500);

            // ------------------------------------------------------------------
            // 7. Detect and handle plan update
            // ------------------------------------------------------------------
            var isPlanUpdate    = rawText.Contains("[PLAN_UPDATE]");
            var responseMessage = rawText;

            if (isPlanUpdate)
            {
                tracer?.Trace("[AccordIn] Plan update detected — extracting JSON");
                responseMessage = ApplyPlanUpdate(svc, tracer, planId, planRecord.AccountId, rawText);

                // If extraction/parsing failed, ApplyPlanUpdate returns the raw text as message
                // and isPlanUpdate remains true so the UI knows to refresh
            }

            // ------------------------------------------------------------------
            // 8. Persist conversation turn to Dataverse
            //    Store raw model response (not just explanation) so future turns
            //    have full context of what the model said
            // ------------------------------------------------------------------
            SaveMessage(svc, planId, role: 1, content: userMessage,        sequence: nextSeq);
            SaveMessage(svc, planId, role: 2, content: rawText,            sequence: nextSeq + 1);

            tracer?.Trace($"[AccordIn] Saved conversation messages {nextSeq} and {nextSeq + 1}");

            // ------------------------------------------------------------------
            // 9. Output parameters
            // ------------------------------------------------------------------
            context.OutputParameters["ResponseMessage"] = responseMessage;
            context.OutputParameters["IsPlanUpdate"]    = isPlanUpdate;
        }

        // -----------------------------------------------------------------------------------------
        // Plan update — extract JSON, post-process, persist
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Extracts the updated plan JSON from the model response, applies post-processing,
        /// and calls PlanSaver.UpdatePlan. Returns the plain-English explanation to show the AM.
        /// Returns the raw response on any failure so the turn is never silently dropped.
        /// </summary>
        private static string ApplyPlanUpdate(
            IOrganizationService svc,
            ITracingService      tracer,
            Guid                 planId,
            Guid                 accountId,
            string               rawText)
        {
            var extracted = ExtractPlanJson(rawText);
            if (extracted == null)
            {
                tracer?.Trace("[AccordIn] Could not extract JSON from plan update response");
                return rawText;
            }

            var parseResult = AzureOpenAIClient.ParseResponse(extracted.JsonText);
            if (!parseResult.Success)
            {
                tracer?.Trace($"[AccordIn] Plan update parse error: {parseResult.ParseError}");
                return rawText;
            }

            var updatedPlan = parseResult.Parsed;

            // Re-run pipeline calculator so revenue post-processing uses current Dataverse data
            var opportunities = LoadOpportunitiesForPipeline(svc, accountId);
            var pipeline      = new PipelineCalculator().Calculate(opportunities);

            PostProcessRevenue(updatedPlan, pipeline);
            PostProcessHasCadence(updatedPlan);

            var saver = new PlanSaver(svc, tracer);
            saver.UpdatePlan(planId, updatedPlan, pipeline);

            tracer?.Trace($"[AccordIn] Plan {planId} updated via chat refinement");

            return string.IsNullOrWhiteSpace(extracted.Explanation) ? "Plan updated." : extracted.Explanation;
        }

        // -----------------------------------------------------------------------------------------
        // Message list construction
        // -----------------------------------------------------------------------------------------

        private static IList<ChatMessage> BuildMessages(
            string                    systemPrompt,
            string                    planPayload,
            IList<ConversationRecord> history,
            string                    newUserMessage)
        {
            var all = new List<ChatMessage>
            {
                new ChatMessage { Role = "system", Content = systemPrompt },
                // Original plan JSON as the first assistant turn — gives the model full context
                // of what it generated without needing the original (very long) user message
                new ChatMessage { Role = "assistant", Content = planPayload },
            };

            foreach (var h in history)
                all.Add(new ChatMessage { Role = h.Role, Content = h.Content });

            all.Add(new ChatMessage { Role = "user", Content = newUserMessage });

            // Cap to MessageHistoryCap total, always keeping the system message at index 0
            if (all.Count > MessageHistoryCap)
            {
                var systemMessage = all[0];
                var trimmed       = all.Skip(all.Count - (MessageHistoryCap - 1)).ToList();
                trimmed.Insert(0, systemMessage);
                return trimmed;
            }

            return all;
        }

        // -----------------------------------------------------------------------------------------
        // [PLAN_UPDATE] extraction — port of extractPlanJson() in chat.js
        // -----------------------------------------------------------------------------------------

        private static ExtractedPlan ExtractPlanJson(string rawText)
        {
            // Strategy 1: [PLAN_UPDATE] ... [END_PLAN]
            var markerMatch = Regex.Match(rawText, @"\[PLAN_UPDATE\]([\s\S]*?)\[END_PLAN\]");
            if (markerMatch.Success)
            {
                var jsonText    = markerMatch.Groups[1].Value.Trim();
                var explanation = rawText.Replace(markerMatch.Value, string.Empty).Trim();
                return new ExtractedPlan { JsonText = jsonText, Explanation = explanation };
            }

            // Strategy 2: [PLAN_UPDATE] followed by JSON, no end marker
            var legacyIdx = rawText.IndexOf("[PLAN_UPDATE]", StringComparison.Ordinal);
            if (legacyIdx >= 0)
            {
                var remainder = rawText.Substring(legacyIdx + "[PLAN_UPDATE]".Length).Trim();
                var jsonStart = remainder.IndexOf('{');
                var jsonEnd   = remainder.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    return new ExtractedPlan
                    {
                        JsonText    = remainder.Substring(jsonStart, jsonEnd - jsonStart + 1),
                        Explanation = remainder.Substring(jsonEnd + 1).Trim(),
                    };
                }
            }

            // Strategy 3: last resort — any large JSON object in the response (>200 chars)
            var start = rawText.IndexOf('{');
            var end   = rawText.LastIndexOf('}');
            if (start >= 0 && end > start && end - start > 200)
            {
                return new ExtractedPlan
                {
                    JsonText    = rawText.Substring(start, end - start + 1),
                    Explanation = rawText.Substring(0, start).Replace("[PLAN_UPDATE]", string.Empty).Trim(),
                };
            }

            return null;
        }

        // -----------------------------------------------------------------------------------------
        // Post-processing — identical logic to GeneratePlan (CLAUDE.md §1, §3)
        // -----------------------------------------------------------------------------------------

        private static void PostProcessRevenue(PlanResponse plan, PipelineResult pipeline)
        {
            if (plan.RevenuePicture != null)
            {
                plan.RevenuePicture.PipelineValue = pipeline.ExactTotal;
                plan.RevenuePicture.TotalLow      = pipeline.TotalLow;
                plan.RevenuePicture.TotalMid      = pipeline.WeightedTotal;
                plan.RevenuePicture.TotalHigh     = pipeline.TotalHigh;
            }

            plan.RevenueTarget = pipeline.WeightedTotal;
        }

        private static void PostProcessHasCadence(PlanResponse plan)
        {
            if (plan.ContactEngagement == null || plan.Cadences == null) return;

            var cadenceTitles = plan.Cadences
                .Where(c => c?.ContactTitle != null)
                .Select(c => c.ContactTitle.ToLowerInvariant().Trim())
                .ToList();

            foreach (var ce in plan.ContactEngagement)
            {
                if (ce == null) continue;
                ce.HasCadence = cadenceTitles.Contains((ce.Title ?? string.Empty).ToLowerInvariant().Trim());
            }
        }

        // -----------------------------------------------------------------------------------------
        // Dataverse reads
        // -----------------------------------------------------------------------------------------

        private static PlanRecord LoadPlan(IOrganizationService svc, Guid planId)
        {
            var entity = svc.Retrieve("wrl_accountplan", planId,
                new ColumnSet("wrl_planpayload", "wrl_plantype", "wrl_account"));

            var accountRef = entity.GetAttributeValue<EntityReference>("wrl_account");
            if (accountRef == null)
                throw new InvalidPluginExecutionException(
                    $"AccordIn RefinePlan: plan {planId} has no linked account.");

            // wrl_plantype OptionSet int → string label for env var routing
            var planTypeInt = entity.GetAttributeValue<OptionSetValue>("wrl_plantype")?.Value ?? 1;
            var planType    = MapPlanTypeIntToString(planTypeInt);

            var payload = entity.GetAttributeValue<string>("wrl_planpayload");
            if (string.IsNullOrWhiteSpace(payload))
                throw new InvalidPluginExecutionException(
                    $"AccordIn RefinePlan: plan {planId} has no payload (wrl_planpayload is empty). " +
                    "The plan must be generated before it can be refined.");

            return new PlanRecord
            {
                AccountId = accountRef.Id,
                PlanType  = planType,
                Payload   = payload,
            };
        }

        private static IList<ConversationRecord> LoadConversationHistory(IOrganizationService svc, Guid planId)
        {
            var query = new QueryExpression("wrl_conversationmessage")
            {
                ColumnSet = new ColumnSet("wrl_messagecontent", "wrl_userrole", "wrl_messagesequence"),
                Criteria  = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wrl_accountplan", ConditionOperator.Equal, planId)
                    }
                },
                Orders = { new OrderExpression("wrl_messagesequence", OrderType.Ascending) }
            };

            return svc.RetrieveMultiple(query).Entities
                .Select(e =>
                {
                    var roleInt = e.GetAttributeValue<OptionSetValue>("wrl_userrole")?.Value ?? 1;
                    return new ConversationRecord
                    {
                        Sequence = e.GetAttributeValue<int?>("wrl_messagesequence") ?? 0,
                        Role     = roleInt == 2 ? "assistant" : "user",
                        Content  = e.GetAttributeValue<string>("wrl_messagecontent") ?? string.Empty,
                    };
                })
                .ToList();
        }

        /// <summary>
        /// Lightweight opportunity fetch for pipeline recalculation during a plan update.
        /// Only reads the fields PipelineCalculator needs — avoids a full DataCollector.Collect().
        /// </summary>
        private static IList<Opportunity> LoadOpportunitiesForPipeline(IOrganizationService svc, Guid accountId)
        {
            var query = new QueryExpression("opportunity")
            {
                ColumnSet = new ColumnSet("name", "stepname", "estimatedvalue", "estimatedclosedate", "statecode"),
                Criteria  = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("parentaccountid", ConditionOperator.Equal, accountId)
                    }
                }
            };

            return svc.RetrieveMultiple(query).Entities
                .Select(e =>
                {
                    var stateCode = e.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0;
                    var closeDate = e.GetAttributeValue<DateTime?>("estimatedclosedate");
                    return new Opportunity
                    {
                        Name      = e.GetAttributeValue<string>("name"),
                        Stage     = e.GetAttributeValue<string>("stepname") ?? "Discovery",
                        Value     = e.GetAttributeValue<Money>("estimatedvalue")?.Value ?? 0m,
                        CloseDate = closeDate.HasValue ? closeDate.Value.ToString("yyyy-MM-dd") : null,
                        Status    = stateCode == 1 ? "Won" : stateCode == 2 ? "Lost" : "Open",
                    };
                })
                .ToList();
        }

        private static void SaveMessage(
            IOrganizationService svc,
            Guid                 planId,
            int                  role,        // 1=User, 2=Assistant
            string               content,
            int                  sequence)
        {
            var entity = new Entity("wrl_conversationmessage")
            {
                ["wrl_AccountPlan"]      = new EntityReference("wrl_accountplan", planId),
                ["wrl_messagecontent"]   = content,
                ["wrl_userrole"]         = new OptionSetValue(role),
                ["wrl_messagetimestamp"] = DateTime.UtcNow,
                ["wrl_messagesequence"]  = sequence,
            };

            svc.Create(entity);
        }

        // -----------------------------------------------------------------------------------------
        // Input parameter helpers
        // -----------------------------------------------------------------------------------------

        private static Guid ReadPlanId(IPluginExecutionContext context)
        {
            if (context.InputParameters.Contains("PlanId"))
            {
                var raw = context.InputParameters["PlanId"];

                if (raw is EntityReference er)
                    return er.Id;

                if (raw is string s && Guid.TryParse(s, out var parsed))
                    return parsed;
            }

            if (context.PrimaryEntityName == "wrl_accountplan" && context.PrimaryEntityId != Guid.Empty)
                return context.PrimaryEntityId;

            throw new InvalidPluginExecutionException(
                "AccordIn RefinePlan: 'PlanId' input parameter is required (EntityReference or Guid string).");
        }

        private static string ReadRequiredString(IPluginExecutionContext context, string name)
        {
            if (context.InputParameters.Contains(name))
            {
                var value = context.InputParameters[name] as string;
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            throw new InvalidPluginExecutionException(
                $"AccordIn RefinePlan: '{name}' input parameter is required and must not be empty.");
        }

        // -----------------------------------------------------------------------------------------
        // System prompt — read from wrl_intentprompt table, keyed by wrl_intenttype
        // -----------------------------------------------------------------------------------------

        private static string ReadSystemPrompt(IOrganizationService svc, string planType)
        {
            var query = new QueryExpression("wrl_intentprompt")
            {
                ColumnSet = new ColumnSet("wrl_systemprompt"),
                Criteria  = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wrl_intenttype", ConditionOperator.Equal, planType)
                    }
                },
                TopCount = 1,
            };

            var results = svc.RetrieveMultiple(query).Entities;
            if (results.Count == 0)
                throw new InvalidPluginExecutionException(
                    $"AccordIn RefinePlan: no system prompt found for intent type '{planType}'. " +
                    "Create a wrl_intentprompt record with wrl_intenttype = '{planType}'.");

            var prompt = results[0].GetAttributeValue<string>("wrl_systemprompt")?.Trim();
            if (string.IsNullOrEmpty(prompt))
                throw new InvalidPluginExecutionException(
                    $"AccordIn RefinePlan: wrl_intentprompt record for '{planType}' has an empty wrl_systemprompt.");

            return prompt;
        }

        // -----------------------------------------------------------------------------------------
        // Plan type mapping — OptionSet integer → routing string
        // -----------------------------------------------------------------------------------------

        private static string MapPlanTypeIntToString(int value)
        {
            switch (value)
            {
                case 2:  return "upsell";
                case 3:  return "retention";
                case 4:  return "relationship";
                default: return "cross-sell";
            }
        }

        // -----------------------------------------------------------------------------------------
        // Supporting types (private to this class)
        // -----------------------------------------------------------------------------------------

        private class PlanRecord
        {
            public Guid   AccountId { get; set; }
            public string PlanType  { get; set; }
            public string Payload   { get; set; }
        }

        private class ConversationRecord
        {
            public int    Sequence { get; set; }
            public string Role     { get; set; }   // "user" | "assistant"
            public string Content  { get; set; }
        }

        private class ExtractedPlan
        {
            public string JsonText    { get; set; }
            public string Explanation { get; set; }
        }
    }
}
