using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AccordIn.Plugin.Models;
using AccordIn.Plugin.Services;

namespace AccordIn.Plugin.Tests
{
    [TestClass]
    public class ContactEnricherTests
    {
        private readonly ContactEnricher _enricher = new ContactEnricher();

        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        private static Contact C(
            string name,
            string title,
            string engagement  = "Unknown",
            string lastActivity = "No activity recorded") =>
            new Contact
            {
                Name            = name,
                Title           = title,
                EngagementLevel = engagement,
                LastActivity    = lastActivity,
            };

        private static Opportunity Opp(string name) =>
            new Opportunity { Name = name, Stage = "Qualify", Value = 1000m, Status = "Open" };

        private static readonly List<Opportunity> NoOpps = new List<Opportunity>();

        // -----------------------------------------------------------------------------------------
        // Empty / null inputs
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void EmptyList_ReturnsEmpty()
        {
            var result = _enricher.Enrich(new List<Contact>(), NoOpps);
            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void NullList_ReturnsEmptyNotNull()
        {
            var result = _enricher.Enrich(null, NoOpps);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        // -----------------------------------------------------------------------------------------
        // primary-relationship
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void SingleContact_GetsPrimaryRelationship()
        {
            var contacts = new List<Contact> { C("Alice Smith", "Analyst") };

            _enricher.Enrich(contacts, NoOpps);

            Assert.AreEqual("primary-relationship", contacts[0].SuggestedPlanRole);
        }

        [TestMethod]
        public void MostSeniorTitle_WinsPrimaryRelationship()
        {
            // CEO (score 11) beats Manager (score 1) regardless of engagement
            var ceo     = C("Alice Smith", "Chief Executive Officer", "Low",  "2025-01-01");
            var manager = C("Bob Jones",   "Sales Manager",           "High", "2025-01-01");

            _enricher.Enrich(new List<Contact> { manager, ceo }, NoOpps);

            Assert.AreEqual("primary-relationship", ceo.SuggestedPlanRole);
            Assert.AreNotEqual("primary-relationship", manager.SuggestedPlanRole);
        }

        [TestMethod]
        public void HigherEngagement_BreaksSeniorityTie_ForPrimary()
        {
            // Same title → same seniority score; High engagement outranks Low
            var highEng = C("Alice Smith", "Sales Manager", "High",   "2025-01-01");
            var lowEng  = C("Bob Jones",   "Sales Manager", "Low",    "2025-01-01");

            _enricher.Enrich(new List<Contact> { lowEng, highEng }, NoOpps);

            Assert.AreEqual("primary-relationship", highEng.SuggestedPlanRole);
        }

        [TestMethod]
        public void MoreRecentActivity_BreaksTie_ForPrimary()
        {
            // Same title, same engagement; later ISO date string is lexicographically greater
            var recent = C("Alice Smith", "Director", "Medium", "2025-06-15");
            var older  = C("Bob Jones",   "Director", "Medium", "2025-01-01");

            _enricher.Enrich(new List<Contact> { older, recent }, NoOpps);

            Assert.AreEqual("primary-relationship", recent.SuggestedPlanRole);
        }

        [TestMethod]
        public void Primary_OverridesApprovalRiskRule_EvenWithFinanceTitleAndLowEngagement()
        {
            // CFO is most senior → primary-relationship wins over approval-risk rule
            var cfo     = C("Alice Smith", "Chief Financial Officer", "Low",  "2025-01-01");
            var manager = C("Bob Jones",   "Sales Manager",           "High", "2025-01-01");

            _enricher.Enrich(new List<Contact> { manager, cfo }, NoOpps);

            Assert.AreEqual("primary-relationship", cfo.SuggestedPlanRole);
        }

        // -----------------------------------------------------------------------------------------
        // approval-risk
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void FinanceTitle_LowEngagement_GetsApprovalRisk()
        {
            var primary  = C("Alice Smith", "Chief Executive Officer", "High", "2025-01-01");
            var finance  = C("Bob Jones",   "Finance Director",        "Low",  "2025-01-01");

            _enricher.Enrich(new List<Contact> { primary, finance }, NoOpps);

            Assert.AreEqual("approval-risk", finance.SuggestedPlanRole);
        }

        [TestMethod]
        public void LegalTitle_LowEngagement_GetsApprovalRisk()
        {
            var primary = C("Alice Smith", "Chief Executive Officer", "High", "2025-01-01");
            var legal   = C("Bob Jones",   "Legal Counsel",           "Low",  "2025-01-01");

            _enricher.Enrich(new List<Contact> { primary, legal }, NoOpps);

            Assert.AreEqual("approval-risk", legal.SuggestedPlanRole);
        }

        [TestMethod]
        public void ProcurementTitle_LowEngagement_GetsApprovalRisk()
        {
            var primary     = C("Alice Smith", "Chief Executive Officer", "High", "2025-01-01");
            var procurement = C("Bob Jones",   "Head of Procurement",     "Low",  "2025-01-01");

            _enricher.Enrich(new List<Contact> { primary, procurement }, NoOpps);

            Assert.AreEqual("approval-risk", procurement.SuggestedPlanRole);
        }

        [TestMethod]
        public void ApprovalTitle_HighEngagement_NotApprovalRisk()
        {
            // Finance title but High engagement → approval-risk rule requires Low engagement
            var primary = C("Alice Smith", "Chief Executive Officer", "High", "2025-01-01");
            var finance = C("Bob Jones",   "Finance Director",        "High", "2025-06-01");

            _enricher.Enrich(new List<Contact> { primary, finance }, NoOpps);

            Assert.AreNotEqual("approval-risk", finance.SuggestedPlanRole);
        }

        [TestMethod]
        public void ApprovalTitle_MediumEngagement_NotApprovalRisk()
        {
            var primary = C("Alice Smith", "Chief Executive Officer", "High",   "2025-01-01");
            var finance = C("Bob Jones",   "Finance Director",        "Medium", "2025-06-01");

            _enricher.Enrich(new List<Contact> { primary, finance }, NoOpps);

            Assert.AreNotEqual("approval-risk", finance.SuggestedPlanRole);
        }

        // -----------------------------------------------------------------------------------------
        // opportunity-owner
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void HighEngagement_WithRecentActivity_GetsOpportunityOwner()
        {
            var primary = C("Alice Smith", "Chief Executive Officer", "High",   "2025-01-01");
            var ae      = C("Bob Jones",   "Account Executive",       "High",   "2025-06-01");

            _enricher.Enrich(new List<Contact> { primary, ae }, NoOpps);

            Assert.AreEqual("opportunity-owner", ae.SuggestedPlanRole);
        }

        [TestMethod]
        public void OpportunityNameContainsFirstName_GetsOpportunityOwner()
        {
            // Bob has no activity and Low engagement — but owns an opportunity → opportunity-owner
            var primary = C("Alice Smith", "Chief Executive Officer", "High", "2025-01-01");
            var bob     = C("Bob Jones",   "Account Executive",       "Low",  "No activity recorded");
            var opps    = new List<Opportunity> { Opp("Bob - Microsoft 365 Upsell") };

            _enricher.Enrich(new List<Contact> { primary, bob }, opps);

            Assert.AreEqual("opportunity-owner", bob.SuggestedPlanRole);
        }

        [TestMethod]
        public void OpportunityMatch_IsCaseInsensitive()
        {
            var primary = C("Alice Smith", "Chief Executive Officer", "High", "2025-01-01");
            var carol   = C("Carol White", "Account Executive",       "Low",  "No activity recorded");
            var opps    = new List<Opportunity> { Opp("CAROL — Dynamics 365 Proposal") };

            _enricher.Enrich(new List<Contact> { primary, carol }, opps);

            Assert.AreEqual("opportunity-owner", carol.SuggestedPlanRole);
        }

        [TestMethod]
        public void MediumEngagement_WithActivity_NoOpp_GetsOpportunityOwner()
        {
            // Falls through all earlier rules — the else branch returns opportunity-owner
            var primary = C("Alice Smith", "Chief Executive Officer", "High",   "2025-01-01");
            var medium  = C("Bob Jones",   "Account Executive",       "Medium", "2025-03-10");

            _enricher.Enrich(new List<Contact> { primary, medium }, NoOpps);

            Assert.AreEqual("opportunity-owner", medium.SuggestedPlanRole);
        }

        // -----------------------------------------------------------------------------------------
        // no-data
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void NoActivityRecorded_GetsNoData()
        {
            var primary  = C("Alice Smith", "Chief Executive Officer", "High", "2025-01-01");
            var inactive = C("Bob Jones",   "Account Executive",       "Low",  "No activity recorded");

            _enricher.Enrich(new List<Contact> { primary, inactive }, NoOpps);

            Assert.AreEqual("no-data", inactive.SuggestedPlanRole);
        }

        [TestMethod]
        public void NullLastActivity_GetsNoData()
        {
            var primary  = C("Alice Smith", "Chief Executive Officer", "High", "2025-01-01");
            var inactive = new Contact
            {
                Name            = "Bob Jones",
                Title           = "Account Executive",
                EngagementLevel = "Low",
                LastActivity    = null,
            };

            _enricher.Enrich(new List<Contact> { primary, inactive }, NoOpps);

            Assert.AreEqual("no-data", inactive.SuggestedPlanRole);
        }

        // -----------------------------------------------------------------------------------------
        // low-priority
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void LowEngagement_HasActivity_NoOpp_GetsLowPriority()
        {
            var primary = C("Alice Smith", "Chief Executive Officer", "High", "2025-01-01");
            var low     = C("Bob Jones",   "Account Executive",       "Low",  "2024-09-01");

            _enricher.Enrich(new List<Contact> { primary, low }, NoOpps);

            Assert.AreEqual("low-priority", low.SuggestedPlanRole);
        }

        [TestMethod]
        public void UnknownEngagement_HasActivity_NoOpp_GetsLowPriority()
        {
            var primary  = C("Alice Smith", "Chief Executive Officer", "High",    "2025-01-01");
            var unknown  = C("Bob Jones",   "Account Executive",       "Unknown", "2024-09-01");

            _enricher.Enrich(new List<Contact> { primary, unknown }, NoOpps);

            Assert.AreEqual("low-priority", unknown.SuggestedPlanRole);
        }

        // -----------------------------------------------------------------------------------------
        // Mutation and return value
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void Enrich_MutatesContactInPlace()
        {
            var contact  = C("Alice Smith", "Director");
            var contacts = new List<Contact> { contact };

            _enricher.Enrich(contacts, NoOpps);

            // The contact object itself is mutated — not a new copy
            Assert.IsNotNull(contact.SuggestedPlanRole);
        }

        [TestMethod]
        public void Enrich_ReturnsTheSameListInstance()
        {
            var contacts = new List<Contact> { C("Alice Smith", "Director") };

            var returned = _enricher.Enrich(contacts, NoOpps);

            Assert.AreSame(contacts, returned);
        }

        [TestMethod]
        public void AllContacts_HaveSuggestedPlanRoleSet()
        {
            var contacts = new List<Contact>
            {
                C("Alice Smith", "Chief Executive Officer", "High",    "2025-01-01"),
                C("Bob Jones",   "Finance Director",        "Low",     "2025-01-01"),
                C("Carol White", "Account Executive",       "Medium",  "2024-06-01"),
                C("Dave Brown",  "IT Manager",              "Unknown", "No activity recorded"),
            };

            _enricher.Enrich(contacts, NoOpps);

            foreach (var c in contacts)
                Assert.IsNotNull(c.SuggestedPlanRole, $"{c.Name} has no SuggestedPlanRole");
        }
    }
}
