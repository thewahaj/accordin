using System.Collections.Generic;
using Newtonsoft.Json;

namespace AccordIn.Plugin.Models
{
    /// <summary>
    /// Deserialised model output. Matches the JSON schema defined in the cross-sell system prompt (Section 8).
    /// Revenue fields (pipelineValue, totalLow, totalMid, totalHigh, revenueTarget) are OVERWRITTEN
    /// by PipelineCalculator after deserialisation - never trust the model's arithmetic.
    /// hasCadence on each ContactEngagementItem is OVERWRITTEN by post-processing in GeneratePlan
    /// based on the actual cadences list - the model self-reports this inaccurately.
    /// </summary>
    public class PlanResponse
    {
        /// <summary>
        /// Why this is the right moment to act. Must cite at least two specific data points.
        /// Maps to wrl_aiopeningstatement on wrl_accountplan.
        /// </summary>
        [JsonProperty("openingStatement")]
        public string OpeningStatement { get; set; }

        /// <summary>
        /// One-sentence account health summary.
        /// Maps to wrl_healthsummary on wrl_accountplan.
        /// </summary>
        [JsonProperty("healthSummary")]
        public string HealthSummary { get; set; }

        /// <summary>Specific positive signals from the account data. No invented items.</summary>
        [JsonProperty("positiveSignals")]
        public List<string> PositiveSignals { get; set; } = new List<string>();

        /// <summary>Risks, each citing the specific data point and business impact.</summary>
        [JsonProperty("watchouts")]
        public List<string> Watchouts { get; set; } = new List<string>();

        /// <summary>
        /// Strategic growth objectives narrative.
        /// Maps to wrl_growthobjectives on wrl_accountplan.
        /// </summary>
        [JsonProperty("growthObjectives")]
        public string GrowthObjectives { get; set; }

        /// <summary>
        /// All contacts from the input, each assigned a planRole.
        /// hasCadence is post-processed after deserialisation - do not rely on the raw model value.
        /// </summary>
        [JsonProperty("contactEngagement")]
        public List<ContactEngagementItem> ContactEngagement { get; set; } = new List<ContactEngagementItem>();

        [JsonProperty("revenuePicture")]
        public RevenuePicture RevenuePicture { get; set; }

        /// <summary>
        /// Stage-weighted pipeline total (totalMid). Post-processed with the pre-calculated value.
        /// Maps to wrl_revenuetarget on wrl_accountplan.
        /// </summary>
        [JsonProperty("revenueTarget")]
        public decimal RevenueTarget { get; set; }

        /// <summary>Explains the forecast reasoning. References the highest-stage opportunity by name.</summary>
        [JsonProperty("forecastNarrative")]
        public string ForecastNarrative { get; set; }

        /// <summary>Max 3 recommendations. Ordered by pipeline stage (Negotiation first).</summary>
        [JsonProperty("recommendations")]
        public List<Recommendation> Recommendations { get; set; } = new List<Recommendation>();

        /// <summary>Max 3 cadences. Cadence 1 always goes to the primary relationship contact.</summary>
        [JsonProperty("cadences")]
        public List<Cadence> Cadences { get; set; } = new List<Cadence>();

        /// <summary>Max 4 one-off actions.</summary>
        [JsonProperty("oneOffActions")]
        public List<OneOffAction> OneOffActions { get; set; } = new List<OneOffAction>();

        /// <summary>
        /// Data gaps identified by the model. Never empty - minimum entry is a confirmation of what
        /// data was available. Maps to dataLimitations in the plan payload.
        /// </summary>
        [JsonProperty("dataLimitations")]
        public List<string> DataLimitations { get; set; } = new List<string>();
    }

    public class ContactEngagementItem
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        /// <summary>High | Medium | Low | Unknown</summary>
        [JsonProperty("engagementLevel")]
        public string EngagementLevel { get; set; }

        /// <summary>ISO date string or "No activity recorded".</summary>
        [JsonProperty("lastActivity")]
        public string LastActivity { get; set; }

        /// <summary>primary-relationship | opportunity-owner | approval-risk | low-priority | no-data</summary>
        [JsonProperty("planRole")]
        public string PlanRole { get; set; }

        /// <summary>
        /// Post-processed after deserialisation: set to true if this contact's title appears
        /// in the cadences list. Do not use the raw model value.
        /// </summary>
        [JsonProperty("hasCadence")]
        public bool HasCadence { get; set; }

        /// <summary>One sentence citing last activity, engagement level, or specific risk/opportunity.</summary>
        [JsonProperty("strategicNote")]
        public string StrategicNote { get; set; }

        [JsonProperty("d365ContactId", NullValueHandling = NullValueHandling.Include)]
        public string D365ContactId { get; set; }
    }

    public class RevenuePicture
    {
        /// <summary>
        /// Raw pipeline sum (exactTotal). Post-processed with the pre-calculated value from PipelineCalculator.
        /// </summary>
        [JsonProperty("pipelineValue")]
        public decimal PipelineValue { get; set; }

        /// <summary>Each open opportunity listed by name and stage only (no values - the model never sees them).</summary>
        [JsonProperty("pipelineDetail")]
        public string PipelineDetail { get; set; }

        /// <summary>
        /// Estimated whitespace revenue from signals not already in the pipeline.
        /// Zero if no whitespace signals exist in the account data.
        /// </summary>
        [JsonProperty("whitespaceEstimate")]
        public decimal WhitespaceEstimate { get; set; }

        [JsonProperty("whitespaceDetail")]
        public string WhitespaceDetail { get; set; }

        /// <summary>
        /// Floor: highest-confidence stage opportunities x their stage weight.
        /// Post-processed with the pre-calculated value from PipelineCalculator.
        /// </summary>
        [JsonProperty("totalLow")]
        public decimal TotalLow { get; set; }

        /// <summary>
        /// All open opportunities x their stage weights (stage-weighted total).
        /// Post-processed with the pre-calculated value from PipelineCalculator.
        /// </summary>
        [JsonProperty("totalMid")]
        public decimal TotalMid { get; set; }

        /// <summary>
        /// Ceiling: full pipeline if all opportunities close (equals pipelineValue).
        /// Post-processed with the pre-calculated value from PipelineCalculator.
        /// </summary>
        [JsonProperty("totalHigh")]
        public decimal TotalHigh { get; set; }

        /// <summary>Narrative confidence band, e.g. "GBP18,200-GBP73,000".</summary>
        [JsonProperty("confidenceBand")]
        public string ConfidenceBand { get; set; }
    }

    public class Recommendation
    {
        [JsonProperty("d365Id", NullValueHandling = NullValueHandling.Include)]
        public string D365Id { get; set; }

        /// <summary>cross-sell | upsell | retention | relationship</summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        /// <summary>Null for relationship or retention recommendations with no specific product.</summary>
        [JsonProperty("productName")]
        public string ProductName { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        /// <summary>Must cite a specific date, contact name, activity, or signal - or state industry best practice basis.</summary>
        [JsonProperty("rationale")]
        public string Rationale { get; set; }

        /// <summary>Estimated value in GBP. Zero if discovery-phase only.</summary>
        [JsonProperty("estimatedValue")]
        public decimal EstimatedValue { get; set; }

        /// <summary>high | medium | low</summary>
        [JsonProperty("confidence")]
        public string Confidence { get; set; }

        [JsonProperty("confidenceReason")]
        public string ConfidenceReason { get; set; }
    }

    public class Cadence
    {
        [JsonProperty("d365Id", NullValueHandling = NullValueHandling.Include)]
        public string D365Id { get; set; }

        [JsonProperty("d365ContactId", NullValueHandling = NullValueHandling.Include)]
        public string D365ContactId { get; set; }

        /// <summary>Must reflect the specific business objective, e.g. "Strategic Expansion Review - CTO".</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("contactName", NullValueHandling = NullValueHandling.Include)]
        public string ContactName { get; set; }

        /// <summary>Contact title - used for hasCadence post-processing (matched case-insensitively).</summary>
        [JsonProperty("contactTitle")]
        public string ContactTitle { get; set; }

        /// <summary>weekly | biweekly | monthly | quarterly</summary>
        [JsonProperty("frequency")]
        public string Frequency { get; set; }

        /// <summary>phone | online-meeting | in-person | email</summary>
        [JsonProperty("channel")]
        public string Channel { get; set; }

        /// <summary>True if the contact is in a different city or country from the account manager.</summary>
        [JsonProperty("locationAware")]
        public bool LocationAware { get; set; }

        [JsonProperty("purpose")]
        public string Purpose { get; set; }

        /// <summary>Must cite last activity date, engagement level, strategic role, and frequency justification.</summary>
        [JsonProperty("rationale")]
        public string Rationale { get; set; }
    }

    public class OneOffAction
    {
        [JsonProperty("d365Id", NullValueHandling = NullValueHandling.Include)]
        public string D365Id { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        /// <summary>phone | online-meeting | in-person | email | other</summary>
        [JsonProperty("channel")]
        public string Channel { get; set; }

        /// <summary>high | medium | low</summary>
        [JsonProperty("priority")]
        public string Priority { get; set; }

        /// <summary>e.g. "Within 1 week", "Before end of Q2 2026"</summary>
        [JsonProperty("suggestedTiming")]
        public string SuggestedTiming { get; set; }

        /// <summary>Must cite a specific date, contact name, activity, or signal.</summary>
        [JsonProperty("rationale")]
        public string Rationale { get; set; }
    }
}
