using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using AccordIn.Plugin.Models;
using Microsoft.Xrm.Sdk.Query;

namespace AccordIn.Plugin.Services
{
    /// <summary>
    /// Writes a generated plan to Dataverse in four steps:
    ///   1. wrl_accountplan        — header + full JSON payload (source of truth)
    ///   2. wrl_engagementcadence  — one record per cadence (max 3)
    ///   3. wrl_actionplan         — one record per one-off action (max 4)
    ///   4. wrl_planrecommendation — one record per recommendation (max 3)
    ///
    /// All child records link back to the plan via wrl_AccountPlan (exact casing per CLAUDE.md).
    /// Revenue fields on the plan header come from PipelineResult — never from the raw model output.
    /// </summary>
    internal class PlanSaver
    {
        private readonly IOrganizationService _svc;
        private readonly ITracingService      _tracer;

        public PlanSaver(IOrganizationService service, ITracingService tracer = null)
        {
            _svc    = service ?? throw new ArgumentNullException(nameof(service));
            _tracer = tracer;
        }

        // -----------------------------------------------------------------------------------------
        // Public entry point
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Persists the plan and all child records. Returns the new plan ID.
        /// Called after PipelineCalculator has overwritten revenue fields and
        /// hasCadence post-processing is complete — the PlanResponse passed here is final.
        /// </summary>
        public Guid Save(
            Guid            accountId,
            string          planOwnerEmail,
            AccountPlanData data,
            PlanResponse    plan,
            PipelineResult  pipeline)
        {
            var planId = CreateAccountPlan(accountId, planOwnerEmail, data, plan, pipeline);
            _tracer?.Trace($"[AccordIn] Created wrl_accountplan {planId}");

            CreateCadences(planId, plan.Cadences, managerAdjusted: false);
            CreateActions(planId, plan.OneOffActions);
            CreateRecommendations(planId, plan.Recommendations);

            return planId;
        }

        /// <summary>
        /// Applies an AM-directed plan update from a chat refinement turn.
        /// Updates the plan header, then deletes and recreates all child records from the
        /// new plan. Cadences are flagged wrl_manageradjustment=true because the AM directed the change.
        /// Revenue figures come from <paramref name="pipeline"/>, not the model output.
        /// </summary>
        public void UpdatePlan(Guid planId, PlanResponse plan, PipelineResult pipeline)
        {
            var payload = JsonConvert.SerializeObject(plan, Formatting.None);

            var update = new Entity("wrl_accountplan", planId)
            {
                ["wrl_planpayload"]        = payload,
                ["wrl_aiopeningstatement"] = plan.OpeningStatement,
                ["wrl_healthsummary"]      = plan.HealthSummary,
                ["wrl_growthobjectives"]   = plan.GrowthObjectives,
                ["wrl_revenuetarget"]      = new Money((decimal)pipeline.WeightedTotal),
            };
            _svc.Update(update);
            _tracer?.Trace($"[AccordIn] Updated wrl_accountplan {planId}");

            // Delete then recreate — child records are always replaced wholesale on a plan update
            DeleteChildRecords(planId, "wrl_engagementcadence");
            DeleteChildRecords(planId, "wrl_actionplan");
            DeleteChildRecords(planId, "wrl_planrecommendation");

            CreateCadences(planId, plan.Cadences, managerAdjusted: true);
            CreateActions(planId, plan.OneOffActions);
            CreateRecommendations(planId, plan.Recommendations);
        }

        /// <summary>
        /// Deletes all child records of <paramref name="entityLogicalName"/> linked to the given plan.
        /// Uses the lowercased logical name of the wrl_AccountPlan lookup for the query filter.
        /// </summary>
        private void DeleteChildRecords(Guid planId, string entityLogicalName)
        {
            var query = new QueryExpression(entityLogicalName)
            {
                ColumnSet = new ColumnSet(false),   // IDs only
                Criteria  = new FilterExpression
                {
                    Conditions =
                    {
                        // wrl_accountplan = logical name of the wrl_AccountPlan lookup field
                        new ConditionExpression("wrl_accountplan", ConditionOperator.Equal, planId)
                    }
                }
            };

            foreach (var e in _svc.RetrieveMultiple(query).Entities)
            {
                _svc.Delete(entityLogicalName, e.Id);
                _tracer?.Trace($"[AccordIn] Deleted {entityLogicalName} {e.Id}");
            }
        }

        // -----------------------------------------------------------------------------------------
        // wrl_accountplan
        // -----------------------------------------------------------------------------------------

        private Guid CreateAccountPlan(
            Guid            accountId,
            string          planOwnerEmail,
            AccountPlanData data,
            PlanResponse    plan,
            PipelineResult  pipeline)
        {
            var planName = BuildPlanName(data.Account?.Name, data.PlanType);

            // Full model output stored as the source-of-truth payload so the UI can always
            // reconstruct itself from this single field (CLAUDE.md §4).
            var payload = JsonConvert.SerializeObject(plan, Formatting.None);

            var entity = new Entity("wrl_accountplan")
            {
                ["wrl_planname"]           = planName,
                ["wrl_account"]            = new EntityReference("account", accountId),
                ["wrl_plantype"]           = new OptionSetValue(MapPlanType(data.PlanType)),
                ["wrl_planstatus"]         = new OptionSetValue(1),   // 1 = Draft
                ["wrl_planintent"]         = data.PlanIntent,
                ["wrl_planpayload"]        = payload,
                ["wrl_aiopeningstatement"] = plan.OpeningStatement,
                ["wrl_healthsummary"]      = plan.HealthSummary,
                ["wrl_growthobjectives"]   = plan.GrowthObjectives,
                // Revenue target uses the pre-calculated weighted total — not the raw model value
                ["wrl_revenuetarget"]      = new Money((decimal)pipeline.WeightedTotal),
                ["wrl_planowneremail"]     = planOwnerEmail,
                ["wrl_generatedtimestamp"] = DateTime.UtcNow,
            };

            return _svc.Create(entity);
        }

        private static string BuildPlanName(string accountName, string planType)
        {
            var typeLabel = PlanTypeLabel(planType);
            var datePart  = DateTime.UtcNow.ToString("MMM yyyy");
            return $"{accountName ?? "Account"} — {typeLabel} Plan — {datePart}";
        }

        // -----------------------------------------------------------------------------------------
        // wrl_engagementcadence
        // -----------------------------------------------------------------------------------------

        private void CreateCadences(Guid planId, IList<Cadence> cadences, bool managerAdjusted = false)
        {
            if (cadences == null || cadences.Count == 0) return;

            var planRef = new EntityReference("wrl_accountplan", planId);

            foreach (var cadence in cadences)
            {
                if (cadence == null) continue;

                var entity = new Entity("wrl_engagementcadence")
                {
                    // Attribute key = logical name (lowercase). Schema name wrl_AccountPlan ≠ logical name.
                    ["wrl_accountplan"]          = planRef,
                    ["wrl_cadencename"]          = cadence.Name,
                    ["wrl_contactname"]          = cadence.ContactTitle,
                    ["wrl_frequency"]            = new OptionSetValue(MapFrequency(cadence.Frequency)),
                    ["wrl_communicationchannel"] = new OptionSetValue(MapChannel(cadence.Channel)),
                    ["wrl_purpose"]              = cadence.Purpose,
                    ["wrl_rationale"]            = cadence.Rationale,
                    ["wrl_locationawareness"]    = cadence.LocationAware,
                    ["wrl_status"]               = new OptionSetValue(1),     // 1 = Active
                    ["wrl_startdate"]            = DateTime.UtcNow,
                    ["wrl_manageradjustment"]    = managerAdjusted,
                };

                var cadenceId = _svc.Create(entity);
                _tracer?.Trace($"[AccordIn] Created wrl_engagementcadence {cadenceId} ({cadence.Name})");
            }
        }

        // -----------------------------------------------------------------------------------------
        // wrl_actionplan
        // -----------------------------------------------------------------------------------------

        private void CreateActions(Guid planId, IList<OneOffAction> actions)
        {
            if (actions == null || actions.Count == 0) return;

            var planRef = new EntityReference("wrl_accountplan", planId);

            foreach (var action in actions)
            {
                if (action == null) continue;

                var entity = new Entity("wrl_actionplan")
                {
                    ["wrl_accountplan"]          = planRef,
                    ["wrl_actiondescription"]    = action.Description,
                    ["wrl_prioritylevel"]        = new OptionSetValue(MapPriority(action.Priority)),
                    ["wrl_communicationchannel"] = new OptionSetValue(MapChannel(action.Channel)),
                    ["wrl_suggestedtiming"]      = action.SuggestedTiming,
                    ["wrl_rationale"]            = action.Rationale,
                    ["wrl_currentstatus"]        = new OptionSetValue(1),   // 1 = To Do
                };

                var actionId = _svc.Create(entity);
                _tracer?.Trace($"[AccordIn] Created wrl_actionplan {actionId}");
            }
        }

        // -----------------------------------------------------------------------------------------
        // wrl_planrecommendation
        // -----------------------------------------------------------------------------------------

        private void CreateRecommendations(Guid planId, IList<Recommendation> recommendations)
        {
            if (recommendations == null || recommendations.Count == 0) return;

            var planRef  = new EntityReference("wrl_accountplan", planId);
            var sortOrder = 1;

            foreach (var rec in recommendations)
            {
                if (rec == null) continue;

                var entity = new Entity("wrl_planrecommendation")
                {
                    ["wrl_accountplan"]          = planRef,
                    ["wrl_productname"]          = rec.ProductName,
                    ["wrl_recommendationtype"]   = new OptionSetValue(MapRecommendationType(rec.Type)),
                    ["wrl_description"]          = rec.Description,
                    ["wrl_rationale"]            = rec.Rationale,
                    ["wrl_confidence"]           = new OptionSetValue(MapConfidence(rec.Confidence)),
                    ["wrl_confidencereason"]     = rec.ConfidenceReason,
                    ["wrl_sortorder"]            = sortOrder++,
                    // wrl_Opportunity lookup is nullable — only set when the recommendation
                    // maps directly to an existing opportunity in the pipeline
                    // (caller would need to resolve this; left null by default)
                };

                // Only set estimatedvalue if non-zero — avoids creating a £0 currency field entry
                if (rec.EstimatedValue > 0)
                    entity["wrl_estimatedvalue"] = new Money(rec.EstimatedValue);

                var recId = _svc.Create(entity);
                _tracer?.Trace($"[AccordIn] Created wrl_planrecommendation {recId} (sort {sortOrder - 1})");
            }
        }

        // -----------------------------------------------------------------------------------------
        // Choice value mappings — string labels from PlanResponse → D365 option set integers
        // -----------------------------------------------------------------------------------------

        private static int MapPlanType(string planType)
        {
            switch ((planType ?? string.Empty).ToLowerInvariant())
            {
                case "cross-sell":    return 1;
                case "upsell":        return 2;
                case "retention":     return 3;
                case "relationship":  return 4;
                default:              return 1;   // Cross-sell
            }
        }

        private static string PlanTypeLabel(string planType)
        {
            switch ((planType ?? string.Empty).ToLowerInvariant())
            {
                case "upsell":       return "Upsell";
                case "retention":    return "Retention";
                case "relationship": return "Relationship";
                default:             return "Cross-Sell";
            }
        }

        /// <summary>wrl_frequency: 1=Weekly, 2=Biweekly, 3=Monthly, 4=Quarterly</summary>
        private static int MapFrequency(string frequency)
        {
            switch ((frequency ?? string.Empty).ToLowerInvariant())
            {
                case "weekly":    return 1;
                case "biweekly":  return 2;
                case "monthly":   return 3;
                case "quarterly": return 4;
                default:          return 3;   // Monthly
            }
        }

        /// <summary>wrl_communicationchannel: 1=Phone, 2=Online Meeting, 3=In-Person, 4=Email, 5=Other</summary>
        private static int MapChannel(string channel)
        {
            switch ((channel ?? string.Empty).ToLowerInvariant())
            {
                case "phone":          return 1;
                case "online-meeting": return 2;
                case "in-person":      return 3;
                case "email":          return 4;
                default:               return 5;   // Other
            }
        }

        /// <summary>wrl_prioritylevel: 1=High, 2=Medium, 3=Low</summary>
        private static int MapPriority(string priority)
        {
            switch ((priority ?? string.Empty).ToLowerInvariant())
            {
                case "high":   return 1;
                case "medium": return 2;
                case "low":    return 3;
                default:       return 2;   // Medium
            }
        }

        /// <summary>wrl_confidence: 1=High, 2=Medium, 3=Low</summary>
        private static int MapConfidence(string confidence)
        {
            switch ((confidence ?? string.Empty).ToLowerInvariant())
            {
                case "high":   return 1;
                case "medium": return 2;
                case "low":    return 3;
                default:       return 2;   // Medium
            }
        }

        /// <summary>wrl_recommendationtype: 1=Cross-sell, 2=Upsell, 3=Retention, 4=Relationship</summary>
        private static int MapRecommendationType(string type)
        {
            switch ((type ?? string.Empty).ToLowerInvariant())
            {
                case "cross-sell":    return 1;
                case "upsell":        return 2;
                case "retention":     return 3;
                case "relationship":  return 4;
                default:              return 1;   // Cross-sell
            }
        }
    }
}
