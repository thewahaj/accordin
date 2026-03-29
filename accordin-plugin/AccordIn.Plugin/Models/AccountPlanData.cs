using System.Collections.Generic;
using Newtonsoft.Json;

namespace AccordIn.Plugin.Models
{
    /// <summary>
    /// Full account data collected from Dataverse and assembled before calling the model.
    /// Matches the scenario JSON shape consumed by the copilot service (run.js).
    /// Individual opportunity values are stripped before the LLM call — see AzureOpenAIClient.
    /// </summary>
    public class AccountPlanData
    {
        [JsonProperty("planIntent")]
        public string PlanIntent { get; set; }

        [JsonProperty("planType")]
        public string PlanType { get; set; }

        [JsonProperty("account")]
        public AccountInfo Account { get; set; }

        [JsonProperty("productsOwned")]
        public List<ProductOwned> ProductsOwned { get; set; } = new List<ProductOwned>();

        [JsonProperty("opportunities")]
        public List<Opportunity> Opportunities { get; set; } = new List<Opportunity>();

        [JsonProperty("contacts")]
        public List<Contact> Contacts { get; set; } = new List<Contact>();

        [JsonProperty("activities")]
        public List<ActivityRecord> Activities { get; set; } = new List<ActivityRecord>();

        [JsonProperty("commercialSignals")]
        public List<CommercialSignal> CommercialSignals { get; set; } = new List<CommercialSignal>();

        [JsonProperty("marketingSignals")]
        public List<MarketingSignal> MarketingSignals { get; set; } = new List<MarketingSignal>();

        [JsonProperty("planHistory")]
        public List<object> PlanHistory { get; set; } = new List<object>();
    }

    public class AccountInfo
    {
        /// <summary>D365 account GUID — maps to accountid.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("industry")]
        public string Industry { get; set; }

        [JsonProperty("region")]
        public string Region { get; set; }

        /// <summary>Tier 1 / Tier 2 / Tier 3 — from wrl_accounttier choice label.</summary>
        [JsonProperty("tier")]
        public string Tier { get; set; }

        /// <summary>Annual revenue in GBP — from D365 revenue field.</summary>
        [JsonProperty("annualRevenue")]
        public decimal AnnualRevenue { get; set; }

        /// <summary>0–100 score — from wrl_relationshipscore.</summary>
        [JsonProperty("relationshipScore")]
        public int RelationshipScore { get; set; }
    }

    public class ProductOwned
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>ISO date string: yyyy-MM-dd</summary>
        [JsonProperty("startDate")]
        public string StartDate { get; set; }

        /// <summary>ISO date string: yyyy-MM-dd</summary>
        [JsonProperty("renewalDate")]
        public string RenewalDate { get; set; }

        /// <summary>Annual contract value in GBP.</summary>
        [JsonProperty("value")]
        public decimal Value { get; set; }
    }

    public class Opportunity
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Discovery | Qualify | Propose | Negotiation</summary>
        [JsonProperty("stage")]
        public string Stage { get; set; }

        /// <summary>
        /// Opportunity value in GBP. Stripped before the LLM call so the model cannot
        /// attempt arithmetic — pipeline totals are injected as pre-calculated facts instead.
        /// </summary>
        [JsonProperty("value")]
        public decimal Value { get; set; }

        /// <summary>ISO date string: yyyy-MM-dd</summary>
        [JsonProperty("closeDate")]
        public string CloseDate { get; set; }

        /// <summary>Open | Won | Lost</summary>
        [JsonProperty("status")]
        public string Status { get; set; }
    }

    public class Contact
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("city")]
        public string City { get; set; }

        /// <summary>ISO date string or "No activity recorded".</summary>
        [JsonProperty("lastActivity")]
        public string LastActivity { get; set; }

        /// <summary>High | Medium | Low | Unknown</summary>
        [JsonProperty("engagementLevel")]
        public string EngagementLevel { get; set; }

        /// <summary>
        /// Set by ContactEnricher before the LLM call.
        /// Values: primary-relationship | approval-risk | opportunity-owner | low-priority | no-data
        /// Omitted from serialization until enriched.
        /// </summary>
        [JsonProperty("suggestedPlanRole", NullValueHandling = NullValueHandling.Ignore)]
        public string SuggestedPlanRole { get; set; }
    }

    public class ActivityRecord
    {
        /// <summary>Meeting | Call | Email | Task</summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("subject")]
        public string Subject { get; set; }

        /// <summary>ISO date string: yyyy-MM-dd</summary>
        [JsonProperty("date")]
        public string Date { get; set; }

        /// <summary>Completed | Scheduled | Cancelled</summary>
        [JsonProperty("status")]
        public string Status { get; set; }
    }

    public class CommercialSignal
    {
        /// <summary>Invoice | Order | Contract | Support</summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        /// <summary>ISO date string: yyyy-MM-dd</summary>
        [JsonProperty("date")]
        public string Date { get; set; }

        /// <summary>Positive | Neutral | Negative — maps to wrl_sentimentstatus on wrl_businesssignal.</summary>
        [JsonProperty("sentiment")]
        public string Sentiment { get; set; }
    }

    public class MarketingSignal
    {
        /// <summary>Email Click | Webinar | Event | Content Download</summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        /// <summary>ISO date string: yyyy-MM-dd</summary>
        [JsonProperty("date")]
        public string Date { get; set; }

        /// <summary>High | Medium | Low — maps to wrl_engagementlevel on wrl_marketingtouchpoint.</summary>
        [JsonProperty("engagementLevel")]
        public string EngagementLevel { get; set; }
    }
}
