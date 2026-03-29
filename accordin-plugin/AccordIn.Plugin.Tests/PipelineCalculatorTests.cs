using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AccordIn.Plugin.Models;
using AccordIn.Plugin.Services;

namespace AccordIn.Plugin.Tests
{
    [TestClass]
    public class PipelineCalculatorTests
    {
        private readonly PipelineCalculator _calc = new PipelineCalculator();

        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        private static Opportunity O(string stage, decimal value, string status = "Open") =>
            new Opportunity { Name = "Test Opp", Stage = stage, Value = value, Status = status };

        // -----------------------------------------------------------------------------------------
        // Empty / null inputs
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void EmptyList_ReturnsAllZeros()
        {
            var result = _calc.Calculate(new List<Opportunity>());

            Assert.AreEqual(0, result.ExactTotal);
            Assert.AreEqual(0, result.WeightedTotal);
            Assert.AreEqual(0, result.TotalLow);
            Assert.AreEqual(0, result.TotalHigh);
            Assert.AreEqual(0, result.OpportunityCount);
            Assert.IsNull(result.HighestStage);
        }

        [TestMethod]
        public void NullList_ReturnsAllZeros()
        {
            var result = _calc.Calculate(null);

            Assert.AreEqual(0, result.ExactTotal);
            Assert.AreEqual(0, result.WeightedTotal);
            Assert.AreEqual(0, result.OpportunityCount);
        }

        [TestMethod]
        public void AllWonOrLost_ReturnsAllZeros()
        {
            var opps = new List<Opportunity>
            {
                O("Negotiation", 10000m, "Won"),
                O("Propose",     5000m,  "Lost"),
            };

            var result = _calc.Calculate(opps);

            Assert.AreEqual(0, result.ExactTotal);
            Assert.AreEqual(0, result.OpportunityCount);
        }

        // -----------------------------------------------------------------------------------------
        // Stage weights (CLAUDE.md §1 — never change these)
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void NegotiationStage_AppliesWeight085()
        {
            var result = _calc.Calculate(new List<Opportunity> { O("Negotiation", 10000m) });

            Assert.AreEqual(10000, result.ExactTotal);
            Assert.AreEqual(8500,  result.WeightedTotal);   // 10000 × 0.85
        }

        [TestMethod]
        public void ProposeStage_AppliesWeight065()
        {
            var result = _calc.Calculate(new List<Opportunity> { O("Propose", 10000m) });

            Assert.AreEqual(6500, result.WeightedTotal);    // 10000 × 0.65
        }

        [TestMethod]
        public void QualifyStage_AppliesWeight040()
        {
            var result = _calc.Calculate(new List<Opportunity> { O("Qualify", 10000m) });

            Assert.AreEqual(4000, result.WeightedTotal);    // 10000 × 0.40
        }

        [TestMethod]
        public void DiscoveryStage_AppliesWeight020()
        {
            var result = _calc.Calculate(new List<Opportunity> { O("Discovery", 10000m) });

            Assert.AreEqual(2000, result.WeightedTotal);    // 10000 × 0.20
        }

        [TestMethod]
        public void NullStage_DefaultsToDiscoveryWeight()
        {
            var result = _calc.Calculate(new List<Opportunity> { O(null, 10000m) });

            Assert.AreEqual(2000, result.WeightedTotal);    // 10000 × 0.20 default
        }

        [TestMethod]
        public void UnknownStage_DefaultsToDiscoveryWeight()
        {
            var result = _calc.Calculate(new List<Opportunity> { O("Pipeline", 10000m) });

            Assert.AreEqual(2000, result.WeightedTotal);
        }

        [TestMethod]
        public void StageComparison_IsCaseInsensitive()
        {
            var upper  = _calc.Calculate(new List<Opportunity> { O("NEGOTIATION", 10000m) });
            var mixed  = _calc.Calculate(new List<Opportunity> { O("Negotiation", 10000m) });
            var lower  = _calc.Calculate(new List<Opportunity> { O("negotiation", 10000m) });

            Assert.AreEqual(8500, upper.WeightedTotal);
            Assert.AreEqual(8500, mixed.WeightedTotal);
            Assert.AreEqual(8500, lower.WeightedTotal);
        }

        // -----------------------------------------------------------------------------------------
        // ExactTotal / TotalHigh
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void ExactTotal_IsSumOfAllOpenValues()
        {
            var opps = new List<Opportunity>
            {
                O("Negotiation", 10000m),
                O("Propose",      5000m),
                O("Discovery",    2000m, "Won"),    // excluded
            };

            var result = _calc.Calculate(opps);

            Assert.AreEqual(15000, result.ExactTotal);
        }

        [TestMethod]
        public void TotalHigh_AlwaysEqualsExactTotal()
        {
            var opps = new List<Opportunity>
            {
                O("Negotiation", 10000m),
                O("Qualify",      3000m),
            };

            var result = _calc.Calculate(opps);

            Assert.AreEqual(result.ExactTotal, result.TotalHigh);
        }

        // -----------------------------------------------------------------------------------------
        // WeightedTotal
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void WeightedTotal_IsCorrectAcrossMixedStages()
        {
            // 10000×0.85 + 8000×0.65 + 5000×0.40 + 2000×0.20 = 8500+5200+2000+400 = 16100
            var opps = new List<Opportunity>
            {
                O("Negotiation", 10000m),
                O("Propose",      8000m),
                O("Qualify",      5000m),
                O("Discovery",    2000m),
            };

            var result = _calc.Calculate(opps);

            Assert.AreEqual(16100, result.WeightedTotal);
        }

        [TestMethod]
        public void WeightedTotal_ExcludesWonAndLost()
        {
            var opps = new List<Opportunity>
            {
                O("Negotiation", 10000m),
                O("Negotiation", 99999m, "Won"),
                O("Propose",     99999m, "Lost"),
            };

            var result = _calc.Calculate(opps);

            Assert.AreEqual(8500, result.WeightedTotal);   // only the Open opp
        }

        // -----------------------------------------------------------------------------------------
        // TotalLow — floor forecast: only highest-stage opportunities
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void TotalLow_UsesOnlyHighestStageOpportunities()
        {
            // Negotiation is highest stage — totalLow = only negotiation opps × 0.85
            // 6000×0.85 = 5100; the propose opp is excluded from totalLow
            var opps = new List<Opportunity>
            {
                O("Negotiation", 6000m),
                O("Propose",     4000m),
            };

            var result = _calc.Calculate(opps);

            Assert.AreEqual(5100, result.TotalLow);        // 6000 × 0.85
        }

        [TestMethod]
        public void TotalLow_SumsAllOppsInHighestStage()
        {
            // Two negotiation opps — both included in totalLow
            var opps = new List<Opportunity>
            {
                O("Negotiation", 10000m),
                O("Negotiation",  5000m),
                O("Propose",      8000m),
            };

            var result = _calc.Calculate(opps);

            Assert.AreEqual(12750, result.TotalLow);       // (10000+5000) × 0.85
        }

        [TestMethod]
        public void TotalLow_IsZeroWhenNoKnownStage()
        {
            // Unknown stage → highestStageKey = null → totalLow = 0
            var result = _calc.Calculate(new List<Opportunity> { O(null, 5000m) });

            Assert.AreEqual(0, result.TotalLow);
        }

        // -----------------------------------------------------------------------------------------
        // HighestStage
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void HighestStage_IsLowercaseKeyOfMostAdvancedStage()
        {
            var opps = new List<Opportunity>
            {
                O("Discovery",   1000m),
                O("Propose",     2000m),
                O("Negotiation", 500m),
            };

            var result = _calc.Calculate(opps);

            Assert.AreEqual("negotiation", result.HighestStage);
        }

        [TestMethod]
        public void HighestStage_IsNullWhenNoKnownStage()
        {
            var result = _calc.Calculate(new List<Opportunity> { O("Pipeline", 5000m) });

            Assert.IsNull(result.HighestStage);
        }

        [TestMethod]
        public void HighestStageWeightPct_Returns20WhenNoKnownStage()
        {
            var result = _calc.Calculate(new List<Opportunity> { O(null, 5000m) });

            Assert.AreEqual(20, result.HighestStageWeightPct);
        }

        // -----------------------------------------------------------------------------------------
        // ByStage grouping
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void ByStage_GroupsByStageLabel()
        {
            var opps = new List<Opportunity>
            {
                O("Negotiation", 10000m),
                O("Negotiation",  5000m),
                O("Propose",      8000m),
            };

            var result = _calc.Calculate(opps);

            Assert.IsTrue(result.ByStage.ContainsKey("Negotiation"));
            Assert.IsTrue(result.ByStage.ContainsKey("Propose"));
            Assert.AreEqual(2,      result.ByStage["Negotiation"].Count);
            Assert.AreEqual(15000m, result.ByStage["Negotiation"].Total);
            Assert.AreEqual(0.85,   result.ByStage["Negotiation"].Weight, delta: 0.0001);
            Assert.AreEqual(1,      result.ByStage["Propose"].Count);
            Assert.AreEqual(8000m,  result.ByStage["Propose"].Total);
        }

        [TestMethod]
        public void ByStage_UsesUnknownKeyForUnrecognisedStage()
        {
            var result = _calc.Calculate(new List<Opportunity> { O("Pipeline", 5000m) });

            Assert.IsTrue(result.ByStage.ContainsKey("Pipeline"));
            Assert.AreEqual(0.20, result.ByStage["Pipeline"].Weight, delta: 0.0001);
        }

        // -----------------------------------------------------------------------------------------
        // OpportunityCount
        // -----------------------------------------------------------------------------------------

        [TestMethod]
        public void OpportunityCount_OnlyCountsOpenOpportunities()
        {
            var opps = new List<Opportunity>
            {
                O("Negotiation", 10000m),
                O("Propose",      5000m, "Won"),
                O("Qualify",      3000m, "Lost"),
            };

            var result = _calc.Calculate(opps);

            Assert.AreEqual(1, result.OpportunityCount);
        }
    }
}
