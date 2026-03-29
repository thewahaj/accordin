using System;
using System.Collections.Generic;
using System.Linq;
using AccordIn.Plugin.Models;

namespace AccordIn.Plugin.Services
{
    /// <summary>
    /// Port of enrichContacts() from copilot-service/src/routes/run.js.
    ///
    /// Derives a suggestedPlanRole for each contact using deterministic rules before
    /// calling the model. The model can override the suggestion but starts from a
    /// grounded signal rather than reasoning from scratch.
    ///
    /// Role values (matches system prompt vocabulary):
    ///   primary-relationship  — most senior + highest engagement + most recent activity (top 1)
    ///   approval-risk         — finance/legal/procurement title + Low engagement
    ///   opportunity-owner     — owns a named opportunity OR (High engagement + recent activity)
    ///   low-priority          — Low or Unknown engagement, no named opportunity
    ///   no-data               — no lastActivity recorded
    /// </summary>
    internal class ContactEnricher
    {
        // Ordered by descending seniority — index position drives the score (lower index = higher score)
        private static readonly string[] SeniorityKeywords =
        {
            "chief", "ceo", "cfo", "cto", "coo", "cio",
            "president", "vp ", "vice president",
            "director", "head of", "manager",
        };

        private static readonly string[] ApprovalTitleKeywords =
        {
            "finance", "legal", "procurement", "counsel", "controller", "compliance",
        };

        private static readonly Dictionary<string, int> EngagementRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "High",    3 },
            { "Medium",  2 },
            { "Low",     1 },
            { "Unknown", 0 },
        };

        /// <summary>
        /// Mutates each contact in-place by setting <see cref="Contact.SuggestedPlanRole"/>.
        /// Returns the same list for fluent chaining.
        /// </summary>
        public IList<Contact> Enrich(IList<Contact> contacts, IEnumerable<Opportunity> opportunities)
        {
            if (contacts == null || contacts.Count == 0)
                return contacts ?? new List<Contact>();

            var oppList = (opportunities ?? Enumerable.Empty<Opportunity>()).ToList();

            // Determine the primary-relationship contact: sort by seniority desc, then engagement desc,
            // then lastActivity desc (lexicographic ISO date comparison matches JS .localeCompare behaviour)
            var primaryName = contacts
                .OrderByDescending(c => SeniorityScore(c.Title))
                .ThenByDescending(c => EngagementRank.TryGetValue(c.EngagementLevel ?? string.Empty, out var r) ? r : 0)
                .ThenByDescending(c => c.LastActivity ?? string.Empty, StringComparer.Ordinal)
                .First()
                .Name;

            foreach (var contact in contacts)
                contact.SuggestedPlanRole = DeriveRole(contact, primaryName, oppList);

            return contacts;
        }

        // -----------------------------------------------------------------------------------------

        private static string DeriveRole(Contact c, string primaryName, IList<Opportunity> opportunities)
        {
            var eng        = c.EngagementLevel ?? "Unknown";
            var hasActivity = !string.IsNullOrEmpty(c.LastActivity)
                              && !string.Equals(c.LastActivity, "No activity recorded", StringComparison.OrdinalIgnoreCase);
            var ownsOpp    = OpportunityOwnerMatch(c.Name, opportunities);

            if (string.Equals(c.Name, primaryName, StringComparison.Ordinal))
                return "primary-relationship";

            if (IsApprovalRole(c.Title) && string.Equals(eng, "Low", StringComparison.OrdinalIgnoreCase))
                return "approval-risk";

            if (ownsOpp || (string.Equals(eng, "High", StringComparison.OrdinalIgnoreCase) && hasActivity))
                return "opportunity-owner";

            if (!hasActivity)
                return "no-data";

            if (string.Equals(eng, "Low",     StringComparison.OrdinalIgnoreCase) ||
                string.Equals(eng, "Unknown", StringComparison.OrdinalIgnoreCase))
                return "low-priority";

            // Medium engagement + has activity — treat as opportunity-owner (mirrors JS else branch)
            return "opportunity-owner";
        }

        /// <summary>
        /// Replicates the JS heuristic: opportunity name contains the contact's first name (case-insensitive).
        /// </summary>
        private static bool OpportunityOwnerMatch(string contactName, IList<Opportunity> opportunities)
        {
            if (string.IsNullOrEmpty(contactName) || opportunities.Count == 0)
                return false;

            var firstName = contactName.Split(' ')[0];
            return opportunities.Any(o =>
                !string.IsNullOrEmpty(o.Name) &&
                o.Name.IndexOf(firstName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Returns a score where a higher value means a more senior title.
        /// Mirrors the JS: score = SeniorityKeywords.Length - matchIndex.
        /// Returns 0 if no keyword matches.
        /// </summary>
        private static int SeniorityScore(string title)
        {
            var t = (title ?? string.Empty).ToLowerInvariant();
            for (int i = 0; i < SeniorityKeywords.Length; i++)
            {
                if (t.Contains(SeniorityKeywords[i]))
                    return SeniorityKeywords.Length - i;
            }
            return 0;
        }

        private static bool IsApprovalRole(string title)
        {
            var t = (title ?? string.Empty).ToLowerInvariant();
            return ApprovalTitleKeywords.Any(k => t.Contains(k));
        }
    }
}
