using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using AccordIn.Plugin.Models;

namespace AccordIn.Plugin.Services
{
    /// <summary>
    /// Reads all account intelligence from Dataverse using IOrganizationService and assembles
    /// an <see cref="AccountPlanData"/> ready for pipeline pre-calculation and model injection.
    ///
    /// Query order matches the data dependencies in run.js:
    ///   Account → Opportunities → Contacts + activity enrichment → Activities → Signals → Touchpoints → Products
    /// </summary>
    internal class DataCollector
    {
        private readonly IOrganizationService _svc;

        // Caps — keeps the LLM context size predictable regardless of account size
        private const int MaxActivities  = 20;
        private const int MaxSignals     = 10;
        private const int MaxTouchpoints = 10;

        public DataCollector(IOrganizationService service)
        {
            _svc = service ?? throw new ArgumentNullException(nameof(service));
        }

        // -----------------------------------------------------------------------------------------
        // Public entry point
        // -----------------------------------------------------------------------------------------

        public AccountPlanData Collect(Guid accountId, string planIntent = null, string planType = null)
        {
            var account      = RetrieveAccount(accountId);
            var opportunities = RetrieveOpportunities(accountId);

            // Contacts are retrieved with entity IDs kept so we can batch-enrich lastActivity
            Dictionary<Guid, Contact> contactById;
            var contacts = RetrieveContacts(accountId, out contactById);
            EnrichContactActivity(contactById);

            var activities  = RetrieveActivities(accountId);
            var signals     = RetrieveBusinessSignals(accountId);
            var touchpoints = RetrieveMarketingTouchpoints(accountId);
            var products    = RetrieveProductsOwned(accountId);

            return new AccountPlanData
            {
                PlanIntent        = planIntent ?? string.Empty,
                PlanType          = planType   ?? "cross-sell",
                Account           = account,
                Opportunities     = opportunities,
                Contacts          = contacts,
                Activities        = activities,
                CommercialSignals = signals,
                MarketingSignals  = touchpoints,
                ProductsOwned     = products,
            };
        }

        // -----------------------------------------------------------------------------------------
        // Account
        // -----------------------------------------------------------------------------------------

        private AccountInfo RetrieveAccount(Guid accountId)
        {
            var entity = _svc.Retrieve("account", accountId, new ColumnSet(
                "name", "industrycode", "address1_country", "address1_stateorprovince",
                "revenue", "wrl_accounttier", "wrl_relationshipscore"));

            return new AccountInfo
            {
                Id   = accountId.ToString(),
                Name = entity.GetAttributeValue<string>("name"),
                Industry = entity.FormattedValues.ContainsKey("industrycode")
                    ? entity.FormattedValues["industrycode"] : null,
                Region = entity.GetAttributeValue<string>("address1_country")
                      ?? entity.GetAttributeValue<string>("address1_stateorprovince"),
                Tier = entity.FormattedValues.ContainsKey("wrl_accounttier")
                    ? entity.FormattedValues["wrl_accounttier"] : null,
                AnnualRevenue     = entity.GetAttributeValue<Money>("revenue")?.Value ?? 0m,
                RelationshipScore = entity.GetAttributeValue<int?>("wrl_relationshipscore") ?? 0,
            };
        }

        // -----------------------------------------------------------------------------------------
        // Opportunities
        // -----------------------------------------------------------------------------------------

        private List<Opportunity> RetrieveOpportunities(Guid accountId)
        {
            var query = new QueryExpression("opportunity")
            {
                ColumnSet = new ColumnSet(
                    "name", "stepname", "estimatedvalue", "estimatedclosedate", "statecode"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("parentaccountid", ConditionOperator.Equal, accountId)
                    }
                },
                Orders = { new OrderExpression("estimatedclosedate", OrderType.Ascending) }
            };

            return _svc.RetrieveMultiple(query).Entities
                .Select(MapOpportunity)
                .ToList();
        }

        private static Opportunity MapOpportunity(Entity e)
        {
            var stateCode  = e.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0;
            var status     = stateCode == 1 ? "Won" : stateCode == 2 ? "Lost" : "Open";
            var closeDate  = e.GetAttributeValue<DateTime?>("estimatedclosedate");

            return new Opportunity
            {
                Name      = e.GetAttributeValue<string>("name"),
                // stepname comes from the active Business Process Flow stage.
                // Falls back to "Discovery" so the pipeline calculator never receives a null stage.
                Stage     = e.GetAttributeValue<string>("stepname") ?? "Discovery",
                Value     = e.GetAttributeValue<Money>("estimatedvalue")?.Value ?? 0m,
                CloseDate = closeDate.HasValue ? closeDate.Value.ToString("yyyy-MM-dd") : null,
                Status    = status,
            };
        }

        // -----------------------------------------------------------------------------------------
        // Contacts
        // -----------------------------------------------------------------------------------------

        private List<Contact> RetrieveContacts(Guid accountId, out Dictionary<Guid, Contact> contactById)
        {
            var query = new QueryExpression("contact")
            {
                ColumnSet = new ColumnSet(
                    "fullname", "jobtitle", "address1_city", "wrl_engagementlevel"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("parentcustomerid", ConditionOperator.Equal, accountId)
                    }
                }
            };

            var entities = _svc.RetrieveMultiple(query).Entities;
            var contacts = new List<Contact>(entities.Count);
            contactById  = new Dictionary<Guid, Contact>(entities.Count);

            foreach (var e in entities)
            {
                var contact = MapContact(e);
                contacts.Add(contact);
                contactById[e.Id] = contact;
            }

            return contacts;
        }

        private static Contact MapContact(Entity e)
        {
            // wrl_engagementlevel is a custom Choice on Contact (High/Medium/Low/Unknown).
            // Falls back to Unknown so ContactEnricher always has a value to reason from.
            var engLevel = e.FormattedValues.ContainsKey("wrl_engagementlevel")
                ? e.FormattedValues["wrl_engagementlevel"]
                : "Unknown";

            return new Contact
            {
                Name            = e.GetAttributeValue<string>("fullname"),
                Title           = e.GetAttributeValue<string>("jobtitle"),
                City            = e.GetAttributeValue<string>("address1_city"),
                EngagementLevel = engLevel,
                LastActivity    = "No activity recorded",   // overwritten by EnrichContactActivity
            };
        }

        /// <summary>
        /// Single batch query for the most recent activity against every contact in <paramref name="contactById"/>.
        /// Results are ordered by createdon desc so the first record per contactId is the latest.
        /// </summary>
        private void EnrichContactActivity(Dictionary<Guid, Contact> contactById)
        {
            if (contactById.Count == 0) return;

            var query = new QueryExpression("activitypointer")
            {
                ColumnSet = new ColumnSet("regardingobjectid", "actualend", "scheduledend", "createdon"),
                Criteria = new FilterExpression(LogicalOperator.And),
                Orders   = { new OrderExpression("createdon", OrderType.Descending) },
                // Fetch up to 5 activities per contact so we always capture the latest even if
                // the ordering across contacts is interleaved
                PageInfo = new PagingInfo { Count = contactById.Count * 5, PageNumber = 1 }
            };

            // IN condition on the polymorphic regardingobjectid lookup
            var inCondition = new ConditionExpression("regardingobjectid", ConditionOperator.In);
            foreach (var id in contactById.Keys)
                inCondition.Values.Add(id);
            query.Criteria.Conditions.Add(inCondition);

            var results = _svc.RetrieveMultiple(query).Entities;

            // Keep only the first (most recent) activity seen per contactId
            var seen = new HashSet<Guid>();
            foreach (var e in results)
            {
                var regardingRef = e.GetAttributeValue<EntityReference>("regardingobjectid");
                if (regardingRef == null) continue;
                if (!contactById.ContainsKey(regardingRef.Id)) continue;
                if (!seen.Add(regardingRef.Id)) continue;   // already have latest for this contact

                var date = e.GetAttributeValue<DateTime?>("actualend")
                        ?? e.GetAttributeValue<DateTime?>("scheduledend")
                        ?? e.GetAttributeValue<DateTime?>("createdon");

                if (date.HasValue)
                    contactById[regardingRef.Id].LastActivity = date.Value.ToString("yyyy-MM-dd");
            }
        }

        // -----------------------------------------------------------------------------------------
        // Account-level activities
        // -----------------------------------------------------------------------------------------

        private List<ActivityRecord> RetrieveActivities(Guid accountId)
        {
            var query = new QueryExpression("activitypointer")
            {
                ColumnSet = new ColumnSet(
                    "activitytypecode", "subject", "actualend", "scheduledend", "createdon", "statecode"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("regardingobjectid", ConditionOperator.Equal, accountId)
                    }
                },
                Orders   = { new OrderExpression("createdon", OrderType.Descending) },
                PageInfo = new PagingInfo { Count = MaxActivities, PageNumber = 1 }
            };

            return _svc.RetrieveMultiple(query).Entities
                .Select(MapActivity)
                .ToList();
        }

        private static ActivityRecord MapActivity(Entity e)
        {
            var stateCode = e.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0;
            var status    = stateCode == 1 ? "Completed" : stateCode == 2 ? "Cancelled" : "Scheduled";
            var date      = e.GetAttributeValue<DateTime?>("actualend")
                         ?? e.GetAttributeValue<DateTime?>("scheduledend")
                         ?? e.GetAttributeValue<DateTime?>("createdon");

            return new ActivityRecord
            {
                Type    = MapActivityType(e.GetAttributeValue<string>("activitytypecode")),
                Subject = e.GetAttributeValue<string>("subject"),
                Date    = date.HasValue ? date.Value.ToString("yyyy-MM-dd") : null,
                Status  = status,
            };
        }

        private static string MapActivityType(string typeCode)
        {
            switch ((typeCode ?? string.Empty).ToLowerInvariant())
            {
                case "appointment": return "Meeting";
                case "phonecall":   return "Call";
                case "email":       return "Email";
                case "task":        return "Task";
                default:            return typeCode ?? "Activity";
            }
        }

        // -----------------------------------------------------------------------------------------
        // Business signals — wrl_businesssignal
        // -----------------------------------------------------------------------------------------

        private List<CommercialSignal> RetrieveBusinessSignals(Guid accountId)
        {
            var query = new QueryExpression("wrl_businesssignal")
            {
                ColumnSet = new ColumnSet(
                    "wrl_signalcategory", "wrl_signalsummary", "wrl_signaltimestamp", "wrl_sentimentstatus"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wrl_account", ConditionOperator.Equal, accountId)
                    }
                },
                Orders   = { new OrderExpression("wrl_signaltimestamp", OrderType.Descending) },
                PageInfo = new PagingInfo { Count = MaxSignals, PageNumber = 1 }
            };

            return _svc.RetrieveMultiple(query).Entities
                .Select(MapBusinessSignal)
                .ToList();
        }

        private static CommercialSignal MapBusinessSignal(Entity e)
        {
            // wrl_sentimentstatus: 1=Positive, 2=Neutral, 3=Negative/Risk (matches CLAUDE.md schema)
            var sentiment      = e.GetAttributeValue<OptionSetValue>("wrl_sentimentstatus")?.Value;
            var sentimentLabel = sentiment == 1 ? "Positive" : sentiment == 3 ? "Negative" : "Neutral";
            var ts             = e.GetAttributeValue<DateTime?>("wrl_signaltimestamp");

            return new CommercialSignal
            {
                Type      = e.FormattedValues.ContainsKey("wrl_signalcategory")
                    ? e.FormattedValues["wrl_signalcategory"] : null,
                Summary   = e.GetAttributeValue<string>("wrl_signalsummary"),
                Date      = ts.HasValue ? ts.Value.ToString("yyyy-MM-dd") : null,
                Sentiment = sentimentLabel,
            };
        }

        // -----------------------------------------------------------------------------------------
        // Marketing touchpoints — wrl_marketingtouchpoint
        // -----------------------------------------------------------------------------------------

        private List<MarketingSignal> RetrieveMarketingTouchpoints(Guid accountId)
        {
            var query = new QueryExpression("wrl_marketingtouchpoint")
            {
                ColumnSet = new ColumnSet(
                    "wrl_touchpointtype", "wrl_touchpointsummary", "wrl_touchpointdatetime", "wrl_engagementlevel"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wrl_account", ConditionOperator.Equal, accountId)
                    }
                },
                Orders   = { new OrderExpression("wrl_touchpointdatetime", OrderType.Descending) },
                PageInfo = new PagingInfo { Count = MaxTouchpoints, PageNumber = 1 }
            };

            return _svc.RetrieveMultiple(query).Entities
                .Select(MapMarketingTouchpoint)
                .ToList();
        }

        private static MarketingSignal MapMarketingTouchpoint(Entity e)
        {
            var ts = e.GetAttributeValue<DateTime?>("wrl_touchpointdatetime");

            return new MarketingSignal
            {
                Type           = e.FormattedValues.ContainsKey("wrl_touchpointtype")
                    ? e.FormattedValues["wrl_touchpointtype"] : null,
                Summary        = e.GetAttributeValue<string>("wrl_touchpointsummary"),
                Date           = ts.HasValue ? ts.Value.ToString("yyyy-MM-dd") : null,
                EngagementLevel = e.FormattedValues.ContainsKey("wrl_engagementlevel")
                    ? e.FormattedValues["wrl_engagementlevel"] : null,
            };
        }

        // -----------------------------------------------------------------------------------------
        // Products owned — D365 Contracts (active service/subscription agreements)
        // -----------------------------------------------------------------------------------------

        private List<ProductOwned> RetrieveProductsOwned(Guid accountId)
        {
            // contract statecode: 1=Invoiced, 2=Active — the states that represent a live product
            var stateCondition = new ConditionExpression("statecode", ConditionOperator.In);
            stateCondition.Values.Add(1);
            stateCondition.Values.Add(2);

            var query = new QueryExpression("contract")
            {
                ColumnSet = new ColumnSet("title", "activeon", "expireson", "totalprice"),
                Criteria = new FilterExpression(LogicalOperator.And)
                {
                    Conditions =
                    {
                        new ConditionExpression("customerid", ConditionOperator.Equal, accountId),
                        stateCondition
                    }
                },
                Orders = { new OrderExpression("activeon", OrderType.Descending) }
            };

            return _svc.RetrieveMultiple(query).Entities
                .Select(MapProductOwned)
                .ToList();
        }

        private static ProductOwned MapProductOwned(Entity e)
        {
            var start  = e.GetAttributeValue<DateTime?>("activeon");
            var expiry = e.GetAttributeValue<DateTime?>("expireson");

            return new ProductOwned
            {
                Name        = e.GetAttributeValue<string>("title"),
                StartDate   = start.HasValue  ? start.Value.ToString("yyyy-MM-dd")  : null,
                RenewalDate = expiry.HasValue ? expiry.Value.ToString("yyyy-MM-dd") : null,
                Value       = e.GetAttributeValue<Money>("totalprice")?.Value ?? 0m,
            };
        }
    }
}
