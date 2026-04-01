using System;
using System.Linq;
using System.Text;
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
    ///   1. DataCollector      — reads Account, Opportunities, Contacts, Activities, Signals
    ///   2. PipelineCalculator — pre-calculates exactTotal / weightedTotal / totalLow / totalHigh
    ///   3. ContactEnricher    — derives suggestedPlanRole for each contact
    ///   4. AzureOpenAIClient  — strips opportunity values, builds prompt, calls model
    ///   5. Post-processing    — overwrites revenue fields; fixes hasCadence (CLAUDE.md §1, §3)
    ///   6. PlanSaver          — writes wrl_accountplan + all child records to Dataverse
    ///
    /// Input parameters (custom action):
    ///   AccountId    (String)  — GUID of the target Account — required
    ///   PlanIntent   (String)  — the AM's intent text — required
    ///   PlanType     (String)  — "cross-sell" | "upsell" | "retention" | "relationship"
    ///                            optional, defaults to "cross-sell"
    ///
    /// Output parameters:
    ///   PlanId       (String)  — GUID of the created wrl_accountplan record
    /// </summary>
    public class GeneratePlan : IPlugin
    {
        // System prompts are stored in wrl_intentprompt (one row per intent type).
        // This avoids the D365 environment variable 2000-char limit on plain text fields.

        public void Execute(IServiceProvider serviceProvider)
        {
            var context        = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracer         = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
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
            IPluginExecutionContext  context,
            IOrganizationService     svc,
            ITracingService          tracer)
        {
            // ------------------------------------------------------------------
            // 1. Read and validate input parameters
            // ------------------------------------------------------------------
            var accountId = ReadAccountId(context);
            var planIntent = ReadRequiredString(context, "PlanIntent");
            var planType   = ReadOptionalString(context, "PlanType", "cross-sell");

            tracer?.Trace($"[AccordIn] GeneratePlan — account {accountId}, type '{planType}'");

            // ------------------------------------------------------------------
            // 2. Load system prompt from wrl_intentprompt table
            // ------------------------------------------------------------------
            var systemPrompt = ReadSystemPrompt(svc, planType);

            // ------------------------------------------------------------------
            // 3. Collect account data from Dataverse
            // ------------------------------------------------------------------
            var collector = new DataCollector(svc);
            var data      = collector.Collect(accountId, planIntent, planType);

            tracer?.Trace($"[AccordIn] Collected {data.Opportunities.Count} opportunities, {data.Contacts.Count} contacts");

            // ------------------------------------------------------------------
            // 4. Pre-calculate pipeline totals — these are injected as facts and
            //    overwrite the model's output after the call (CLAUDE.md §1)
            // ------------------------------------------------------------------
            var calculator = new PipelineCalculator();
            var pipeline   = calculator.Calculate(data.Opportunities);

            tracer?.Trace($"[AccordIn] Pipeline — exact £{pipeline.ExactTotal:N0}, weighted £{pipeline.WeightedTotal:N0}, low £{pipeline.TotalLow:N0}");

            // ------------------------------------------------------------------
            // 5. Derive suggestedPlanRole for each contact (CLAUDE.md §2)
            // ------------------------------------------------------------------
            var enricher = new ContactEnricher();
            enricher.Enrich(data.Contacts, data.Opportunities);

            // ------------------------------------------------------------------
            // 6. Call the model — opportunity values are stripped inside Generate()
            // ------------------------------------------------------------------
            var aiClient = AzureOpenAIClient.FromEnvironmentVariables(svc, tracer);
            var result   = aiClient.Generate(systemPrompt, data, pipeline);

            if (!result.Success)
            {
                tracer?.Trace($"[AccordIn] Parse error. Raw response:\n{result.Raw}");
                throw new InvalidPluginExecutionException(
                    $"AccordIn: model response could not be parsed — {result.ParseError}. " +
                    "Check the plugin trace log for the raw response.");
            }

            var plan = result.Parsed;

            // ------------------------------------------------------------------
            // 7. Post-process: overwrite revenue figures with verified values (CLAUDE.md §1)
            // ------------------------------------------------------------------
            PostProcessRevenue(plan, pipeline);

            // ------------------------------------------------------------------
            // 8. Post-process: fix hasCadence from actual cadences list (CLAUDE.md §3)
            // ------------------------------------------------------------------
            PostProcessHasCadence(plan);

            tracer?.Trace($"[AccordIn] Post-processing complete — {plan.Cadences?.Count ?? 0} cadences, {plan.Recommendations?.Count ?? 0} recommendations");

            // ------------------------------------------------------------------
            // 9. Resolve plan owner email from the initiating user's systemuser record
            // ------------------------------------------------------------------
            var planOwnerEmail = ResolveUserEmail(svc, context.InitiatingUserId);

            // ------------------------------------------------------------------
            // 10. Save to Dataverse
            // ------------------------------------------------------------------
            var saver  = new PlanSaver(svc, tracer);
            var planId = saver.Save(accountId, planOwnerEmail, data, plan, pipeline);

            tracer?.Trace($"[AccordIn] Plan saved — wrl_accountplan {planId}");

            // ------------------------------------------------------------------
            // 11. Return output parameter
            // ------------------------------------------------------------------
            context.OutputParameters["PlanId"] = planId.ToString();
        }

        // -----------------------------------------------------------------------------------------
        // Post-processing — mirrors run.js lines 183-199
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Overwrites the model's revenue figures with the pre-calculated pipeline values.
        /// The model reasons about revenue but must never compute it — these are facts (CLAUDE.md §1).
        /// </summary>
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

        /// <summary>
        /// Sets hasCadence on each ContactEngagementItem by checking whether the contact's
        /// title appears in the cadences list. The model self-reports this inaccurately (CLAUDE.md §3).
        /// </summary>
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
            // Support both EntityReference and String (Guid) input parameter types
            if (context.InputParameters.Contains("AccountId"))
            {
                var raw = context.InputParameters["AccountId"];

                if (raw is EntityReference er)
                    return er.Id;

                if (raw is string s && Guid.TryParse(s, out var parsed))
                    return parsed;
            }

            // Bound action fallback: the target record IS the account
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
        // System prompt — read from wrl_intentprompt table, keyed by wrl_intenttype
        // -----------------------------------------------------------------------------------------

        private static string ReadSystemPrompt(IOrganizationService svc, string planType)
        {
            // Locate the wrl_intentprompt record for this intent type
            var query = new QueryExpression("wrl_intentprompt")
            {
                ColumnSet = new ColumnSet(false), // primary key only — file content downloaded separately
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
                    $"AccordIn GeneratePlan: no system prompt found for intent type '{planType}'. " +
                    $"Create a wrl_intentprompt record with wrl_intenttype = '{planType}'.");

            var recordId  = results[0].Id;
            var recordRef = new EntityReference("wrl_intentprompt", recordId);

            // Download the prompt .txt file from the wrl_SystemPromptFile file column.
            // Uses InitializeFileBlocksDownload + DownloadBlock (single-block; prompt files are well under 4 MB).
            try
            {
                var initResponse = (InitializeFileBlocksDownloadResponse)svc.Execute(
                    new InitializeFileBlocksDownloadRequest
                    {
                        Target            = recordRef,
                        FileAttributeName = "wrl_systempromptfile"
                    });

                var downloadResponse = (DownloadBlockResponse)svc.Execute(
                    new DownloadBlockRequest
                    {
                        FileContinuationToken = initResponse.FileContinuationToken,
                        Offset                = 0,
                        BlockLength           = initResponse.FileSizeInBytes
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
                // Non-critical — plan saves without an owner email rather than failing
                return null;
            }
        }
    }
}
