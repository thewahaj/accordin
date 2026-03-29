using System;
using System.Collections.Generic;
using System.Linq;
using AccordIn.Plugin.Models;

namespace AccordIn.Plugin.Services
{
    /// <summary>
    /// Port of calculatePipeline() from copilot-service/src/routes/run.js.
    ///
    /// Pre-calculates pipeline totals before calling the model. These are injected as
    /// verified facts into the user message and post-applied to overwrite any figures
    /// the model produces — the model must never compute arithmetic.
    /// </summary>
    internal class PipelineCalculator
    {
        // Stage weights — never change these. Must match run.js exactly.
        private static readonly Dictionary<string, double> StageWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "negotiation", 0.85 },
            { "propose",     0.65 },
            { "qualify",     0.40 },
            { "discovery",   0.20 },
        };

        // Stage rank for determining totalLow floor (highest rank = most advanced stage)
        private static readonly Dictionary<string, int> StageRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "negotiation", 4 },
            { "propose",     3 },
            { "qualify",     2 },
            { "discovery",   1 },
        };

        public PipelineResult Calculate(IEnumerable<Opportunity> opportunities)
        {
            var open = (opportunities ?? Enumerable.Empty<Opportunity>())
                .Where(o => string.Equals(o.Status, "Open", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var exactTotal = open.Sum(o => o.Value);

            var weightedTotal = open.Sum(o =>
            {
                var weight = StageWeights.TryGetValue(o.Stage ?? string.Empty, out var w) ? w : 0.20;
                return o.Value * (decimal)weight;
            });

            // totalLow: floor — only opportunities in the highest-ranked stage present, at that stage's weight
            string highestStageKey = null;
            int highestRank = 0;
            foreach (var o in open)
            {
                var key = o.Stage ?? string.Empty;
                if (StageRank.TryGetValue(key, out var rank) && rank > highestRank)
                {
                    highestRank = rank;
                    highestStageKey = key.ToLowerInvariant();
                }
            }

            var highestStageWeight = highestStageKey != null && StageWeights.TryGetValue(highestStageKey, out var hsw) ? hsw : 0.20;

            var totalLow = highestStageKey != null
                ? open
                    .Where(o => string.Equals(o.Stage, highestStageKey, StringComparison.OrdinalIgnoreCase))
                    .Sum(o => o.Value * (decimal)highestStageWeight)
                : 0m;

            // byStage: group open opportunities by stage
            var byStage = open
                .GroupBy(o => o.Stage ?? "Unknown")
                .ToDictionary(
                    g => g.Key,
                    g => new StageBreakdown
                    {
                        Count  = g.Count(),
                        Total  = g.Sum(o => o.Value),
                        Weight = StageWeights.TryGetValue(g.Key, out var gw) ? gw : 0.20,
                    });

            return new PipelineResult
            {
                ExactTotal       = (int)Math.Round(exactTotal),
                WeightedTotal    = (int)Math.Round(weightedTotal),
                TotalLow         = (int)Math.Round(totalLow),
                TotalHigh        = (int)Math.Round(exactTotal),   // same as exactTotal — full close scenario
                HighestStage     = highestStageKey,
                ByStage          = byStage,
                OpportunityCount = open.Count,
            };
        }
    }

    internal class PipelineResult
    {
        /// <summary>Raw sum of all open opportunity values.</summary>
        public int ExactTotal { get; set; }

        /// <summary>Stage-weighted sum — used as totalMid / revenueTarget.</summary>
        public int WeightedTotal { get; set; }

        /// <summary>
        /// Conservative floor: only highest-stage opportunities × their weight.
        /// What closes even if everything else stalls.
        /// </summary>
        public int TotalLow { get; set; }

        /// <summary>Optimistic ceiling: same as ExactTotal (all opportunities close).</summary>
        public int TotalHigh { get; set; }

        /// <summary>Lowercase stage key of the most advanced stage present (e.g. "negotiation").</summary>
        public string HighestStage { get; set; }

        /// <summary>Per-stage breakdown keyed by stage label (e.g. "Negotiation").</summary>
        public Dictionary<string, StageBreakdown> ByStage { get; set; }

        /// <summary>Count of Open opportunities.</summary>
        public int OpportunityCount { get; set; }

        /// <summary>Confidence weight for HighestStage as a percentage integer (e.g. 85).</summary>
        public int HighestStageWeightPct =>
            HighestStage != null
                ? (int)Math.Round(GetWeight(HighestStage) * 100)
                : 20;

        private static readonly Dictionary<string, double> Weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "negotiation", 0.85 }, { "propose", 0.65 }, { "qualify", 0.40 }, { "discovery", 0.20 }
        };

        private static double GetWeight(string stage) =>
            Weights.TryGetValue(stage ?? string.Empty, out var w) ? w : 0.20;
    }

    internal class StageBreakdown
    {
        public int Count { get; set; }

        /// <summary>Raw sum of opportunity values in this stage (GBP).</summary>
        public decimal Total { get; set; }

        /// <summary>Stage confidence weight (0.0–1.0).</summary>
        public double Weight { get; set; }
    }
}
