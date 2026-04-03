using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using AccordIn.Plugin.Models;
using AccordIn.Plugin.Services;

namespace AccordIn.Plugin
{
    /// <summary>
    /// D365 plugin bound to the accordin_GeneratePlan custom action.
    ///
    /// Orchestration order mirrors run.js exactly:
    ///   1. DataCollector      - reads Account, Opportunities, Contacts, Activities, Signals
    ///   2. PipelineCalculator - pre-calculates exactTotal / weightedTotal / totalLow / totalHigh
    ///   3. ContactEnricher    - derives suggestedPlanRole for each contact
    ///   4. AzureOpenAIClient  - strips opportunity values, builds prompt, calls model
    ///   5. Post-processing    - overwrites revenue fields; fixes hasCadence
    ///   6. Contact matching   - injects contactName and d365ContactId into the plan
    ///   7. PlanSaver          - writes wrl_accountplan + all child records to Dataverse
    ///
    /// Input parameters (custom action):
    ///   AccountId    (String) - GUID of the target Account - required
    ///   PlanIntent   (String) - the AM's intent text - required
    ///   PlanType     (String) - "cross-sell" | "upsell" | "retention" | "relationship"
    ///                            optional, defaults to "cross-sell"
    ///
    /// Output parameters:
    ///   PlanId       (String) - GUID of the created wrl_accountplan record
    ///   PlanPayload  (String) - final saved plan payload including Dataverse IDs
    /// </summary>
    public class GeneratePlan : IPlugin
    {
        // System prompts are stored in wrl_intentprompt (one row per intent type).
        // This avoids the D365 environment variable 2000-char limit on plain text fields.

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracer = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            // Run as the initiating user so Dataverse security roles are respected
            var svc = serviceFactory.CreateOrganizationService(context.InitiatingUserId);

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
                tracer?.Trace($"[AccordIn] Unhandled exception: {ex}");
                throw new InvalidPluginExecutionException($"AccordIn GeneratePlan failed: {ex.Message}", ex);
            }
        }

        private static void Run(
            IPluginExecutionContext context,
            IOrganizationService svc,
            ITracingService tracer)
        {
            // ------------------------------------------------------------------
            // 1. Read and validate input parameters
            // ------------------------------------------------------------------
            var accountId = ReadAccountId(context);
            var planIntent = ReadRequiredString(context, "PlanIntent");
            var planType = ReadOptionalString(context, "PlanType", "cross-sell");

            tracer?.Trace($"[AccordIn] GeneratePlan - account {accountId}, type '{planType}'");

            // ------------------------------------------------------------------
            // 2. Load system prompt from wrl_intentprompt table
            // ------------------------------------------------------------------
            var systemPrompt = ReadSystemPrompt(svc, planType);

            // ------------------------------------------------------------------
            // 3. Collect account data from Dataverse
            // ------------------------------------------------------------------
            var collector = new DataCollector(svc);
            var data = collector.Collect(accountId, planIntent, planType);

            tracer?.Trace($"[AccordIn] Collected {data.Opportunities.Count} opportunities, {data.Contacts.Count} contacts");

            // ------------------------------------------------------------------
            // 4. Pre-calculate pipeline totals
            // ------------------------------------------------------------------
            var calculator = new PipelineCalculator();
            var pipeline = calculator.Calculate(data.Opportunities);

            tracer?.Trace($"[AccordIn] Pipeline - exact GBP{pipeline.ExactTotal:N0}, weighted GBP{pipeline.WeightedTotal:N0}, low GBP{pipeline.TotalLow:N0}");

            // ------------------------------------------------------------------
            // 5. Derive suggestedPlanRole for each contact
            // ------------------------------------------------------------------
            var enricher = new ContactEnricher();
            enricher.Enrich(data.Contacts, data.Opportunities);

            // ------------------------------------------------------------------
            // 6. Call the model
            // ------------------------------------------------------------------
            var aiClient = AzureOpenAIClient.FromEnvironmentVariables(svc, tracer);
            var result = aiClient.Generate(systemPrompt, data, pipeline);

            if (!result.Success)
            {
                tracer?.Trace($"[AccordIn] Parse error. Raw response:\n{result.Raw}");
                throw new InvalidPluginExecutionException(
                    $"AccordIn: model response could not be parsed - {result.ParseError}. " +
                    "Check the plugin trace log for the raw response.");
            }

            var plan = result.Parsed;

            // ------------------------------------------------------------------
            // 7. Post-process verified revenue fields
            // ------------------------------------------------------------------
            PostProcessRevenue(plan, pipeline);

            // ------------------------------------------------------------------
            // 8. Post-process hasCadence based on actual cadence list
            // ------------------------------------------------------------------
            PostProcessHasCadence(plan);

            // ------------------------------------------------------------------
            // 9. Inject contactName and d365ContactId before any save
            // ------------------------------------------------------------------
            enricher.InjectContactIds(plan, data.Contacts);

            tracer?.Trace($"[AccordIn] Post-processing complete - {plan.Cadences?.Count ?? 0} cadences, {plan.Recommendations?.Count ?? 0} recommendations");

            // ------------------------------------------------------------------
            // 10. Resolve plan owner email from the initiating user's systemuser record
            // ------------------------------------------------------------------
            var planOwnerEmail = ResolveUserEmail(svc, context.InitiatingUserId);

            // ------------------------------------------------------------------
            // 11. Save to Dataverse
            // ------------------------------------------------------------------
            var saver = new PlanSaver(svc, tracer);
            var planId = saver.Save(accountId, planOwnerEmail, data, plan, pipeline);

            tracer?.Trace($"[AccordIn] Plan saved - wrl_accountplan {planId}");

            // ------------------------------------------------------------------
            // 12. Return output parameters
            // ------------------------------------------------------------------
            context.OutputParameters["PlanId"] = planId.ToString();
            context.OutputParameters["PlanPayload"] = JsonConvert.SerializeObject(plan, Formatting.None);
        }

        // -----------------------------------------------------------------------------------------
        // Post-processing
        // -----------------------------------------------------------------------------------------

        private static void PostProcessRevenue(PlanResponse plan, PipelineResult pipeline)
        {
            if (plan.RevenuePicture != null)
            {
                plan.RevenuePicture.PipelineValue = pipeline.ExactTotal;
                plan.RevenuePicture.TotalLow = pipeline.TotalLow;
                plan.RevenuePicture.TotalMid = pipeline.WeightedTotal;
                plan.RevenuePicture.TotalHigh = pipeline.TotalHigh;
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
        // Input parameter helpers
        // -----------------------------------------------------------------------------------------

        private static Guid ReadAccountId(IPluginExecutionContext context)
        {
            if (context.InputParameters.Contains("AccountId"))
            {
                var raw = context.InputParameters["AccountId"];

                if (raw is EntityReference er)
                    return er.Id;

                if (raw is string s && Guid.TryParse(s, out var parsed))
                    return parsed;
            }

            if (context.PrimaryEntityName == "account" && context.PrimaryEntityId != Guid.Empty)
                return context.PrimaryEntityId;

            throw new InvalidPluginExecutionException(
                "AccordIn GeneratePlan: 'AccountId' input parameter is required (EntityReference or Guid string).");
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
                $"AccordIn GeneratePlan: '{name}' input parameter is required and must not be empty.");
        }

        private static string ReadOptionalString(IPluginExecutionContext context, string name, string defaultValue)
        {
            if (context.InputParameters.Contains(name))
            {
                var value = context.InputParameters[name] as string;
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim().ToLowerInvariant();
            }

            return defaultValue;
        }

        // -----------------------------------------------------------------------------------------
        // System prompt
        // -----------------------------------------------------------------------------------------

        private static string ReadSystemPrompt(IOrganizationService svc, string planType)
        {
            var query = new QueryExpression("wrl_intentprompt")
            {
                ColumnSet = new ColumnSet("wrl_systemprompt"),
                Criteria = new FilterExpression
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
                    $"AccordIn GeneratePlan: no system prompt found for intent type '{planType}'. " +
                    $"Create a wrl_intentprompt record with wrl_intenttype = '{planType}'.");

            var textPrompt = results[0].GetAttributeValue<string>("wrl_systemprompt");
            if (!string.IsNullOrWhiteSpace(textPrompt))
                return textPrompt.Trim();

            var recordRef = results[0].ToEntityReference();

            try
            {
                var initResponse = (InitializeFileBlocksDownloadResponse)svc.Execute(
                    new InitializeFileBlocksDownloadRequest
                    {
                        Target = recordRef,
                        FileAttributeName = "wrl_systempromptfile"
                    });

                var downloadResponse = (DownloadBlockResponse)svc.Execute(
                    new DownloadBlockRequest
                    {
                        FileContinuationToken = initResponse.FileContinuationToken,
                        Offset = 0,
                        BlockLength = initResponse.FileSizeInBytes
                    });

                var prompt = Encoding.UTF8.GetString(downloadResponse.Data).Trim();
                if (string.IsNullOrEmpty(prompt))
                    throw new InvalidPluginExecutionException(
                        $"AccordIn GeneratePlan: system prompt file for '{planType}' is empty.");

                return prompt;
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidPluginExecutionException(
                    $"AccordIn GeneratePlan: could not download system prompt file for intent type '{planType}'. " +
                    $"Ensure a .txt file is uploaded to wrl_SystemPromptFile on the wrl_intentprompt record. " +
                    $"Detail: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------------------------
        // Initiating user email
        // -----------------------------------------------------------------------------------------

        private static string ResolveUserEmail(IOrganizationService svc, Guid userId)
        {
            try
            {
                var user = svc.Retrieve("systemuser", userId, new ColumnSet("internalemailaddress"));
                return user.GetAttributeValue<string>("internalemailaddress");
            }
            catch
            {
                return null;
            }
        }
    }
}
