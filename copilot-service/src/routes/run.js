const express = require('express');
const router = express.Router();
const { callModel, parseModelResponse } = require('../azureClient');
const { loadScenario, getSystemPrompt } = require('../scenarioService');

const sessions = {};

const STAGE_WEIGHTS = {
  'negotiation': 0.85,
  'propose':     0.65,
  'qualify':     0.40,
  'discovery':   0.20,
};

function calculatePipeline(opportunities = []) {
  const open = opportunities.filter(o => o.status === 'Open');

  const exactTotal = open.reduce((sum, o) => sum + (o.value || 0), 0);

  const weightedTotal = open.reduce((sum, o) => {
    const weight = STAGE_WEIGHTS[(o.stage || '').toLowerCase()] || 0.20;
    return sum + (o.value || 0) * weight;
  }, 0);

  // totalLow: only the highest-confidence stage (Negotiation only, or highest present)
  // This is the floor — what closes even if everything else stalls
  const stageRank = { negotiation: 4, propose: 3, qualify: 2, discovery: 1 };
  let highestStageKey = null;
  let highestRank = 0;
  open.forEach(o => {
    const key = (o.stage || '').toLowerCase();
    const rank = stageRank[key] || 0;
    if (rank > highestRank) { highestRank = rank; highestStageKey = key; }
  });

  const totalLow = open
    .filter(o => (o.stage || '').toLowerCase() === highestStageKey)
    .reduce((sum, o) => sum + (o.value || 0) * (STAGE_WEIGHTS[highestStageKey] || 0.20), 0);

  const byStage = open.reduce((acc, o) => {
    const stage = o.stage || 'Unknown';
    if (!acc[stage]) acc[stage] = { count: 0, total: 0, weight: STAGE_WEIGHTS[stage.toLowerCase()] || 0.20 };
    acc[stage].count++;
    acc[stage].total += o.value || 0;
    return acc;
  }, {});

  return {
    exactTotal: Math.round(exactTotal),
    weightedTotal: Math.round(weightedTotal),
    totalLow: Math.round(totalLow),
    totalHigh: Math.round(exactTotal),
    highestStage: highestStageKey,
    byStage,
    opportunityCount: open.length
  };
}

// Derive planRole from contact data — model should confirm/refine but this gives it a strong signal
// Rules:
//   primary-relationship: most senior title + highest engagement + most recent activity (top 1)
//   approval-risk: finance/legal/procurement title + Low engagement
//   opportunity-owner: owns a named opportunity OR high engagement with recent activity
//   low-priority: Low or Unknown engagement with no named opportunity
//   no-data: no lastActivity recorded
function enrichContacts(contacts = [], opportunities = []) {
  if (!contacts.length) return contacts;

  const SENIORITY = ['chief', 'ceo', 'cfo', 'cto', 'coo', 'cio', 'president', 'vp ', 'vice president', 'director', 'head of', 'manager'];
  const APPROVAL_TITLES = ['finance', 'legal', 'procurement', 'counsel', 'controller', 'compliance'];

  function seniorityScore(title) {
    const t = (title || '').toLowerCase();
    for (let i = 0; i < SENIORITY.length; i++) {
      if (t.includes(SENIORITY[i])) return SENIORITY.length - i;
    }
    return 0;
  }

  function isApprovalRole(title) {
    const t = (title || '').toLowerCase();
    return APPROVAL_TITLES.some(k => t.includes(k));
  }

  // Find primary relationship: sort by seniority, then engagement, then recency
  const ENG_RANK = { 'High': 3, 'Medium': 2, 'Low': 1, 'Unknown': 0 };
  const sorted = [...contacts].sort((a, b) => {
    const sd = seniorityScore(b.title) - seniorityScore(a.title);
    if (sd !== 0) return sd;
    const ed = (ENG_RANK[b.engagementLevel] || 0) - (ENG_RANK[a.engagementLevel] || 0);
    if (ed !== 0) return ed;
    return (b.lastActivity || '').localeCompare(a.lastActivity || '');
  });
  const primaryName = sorted[0]?.name;

  return contacts.map(c => {
    let suggestedRole;
    const eng = c.engagementLevel || 'Unknown';
    const hasActivity = c.lastActivity && c.lastActivity !== 'No activity recorded';
    const ownsOpp = opportunities.some(o =>
      o.name && c.name && o.name.toLowerCase().includes(c.name.toLowerCase().split(' ')[0].toLowerCase())
    );

    if (c.name === primaryName) {
      suggestedRole = 'primary-relationship';
    } else if (isApprovalRole(c.title) && eng === 'Low') {
      suggestedRole = 'approval-risk';
    } else if (ownsOpp || (eng === 'High' && hasActivity)) {
      suggestedRole = 'opportunity-owner';
    } else if (!hasActivity) {
      suggestedRole = 'no-data';
    } else if (eng === 'Low' || eng === 'Unknown') {
      suggestedRole = 'low-priority';
    } else {
      suggestedRole = 'opportunity-owner';
    }

    return { ...c, suggestedPlanRole: suggestedRole };
  });
}

router.post('/', async (req, res) => {
  const { intentId, scenarioId, sessionId } = req.body;

  if (!intentId || !scenarioId) {
    return res.status(400).json({ error: 'intentId and scenarioId are required' });
  }

  try {
    const { meta, data } = loadScenario(intentId, scenarioId);
    const systemPrompt = getSystemPrompt(intentId);

    const pipeline = calculatePipeline(data.opportunities);
    const enrichedContacts = enrichContacts(data.contacts, data.opportunities);

    const dataForLLM = {
      ...data,
      contacts: enrichedContacts,
      opportunities: (data.opportunities || []).map(o => ({
        name: o.name,
        stage: o.stage,
        closeDate: o.closeDate,
        status: o.status
      }))
    };

    const userMessage = `Plan intent: ${data.planIntent || 'Analyse this account and suggest a strategy.'}

PRE-CALCULATED PIPELINE FACTS (verified - do not recalculate):
- Exact pipeline total: £${pipeline.exactTotal.toLocaleString()}
- Stage-weighted pipeline total (totalMid): £${pipeline.weightedTotal.toLocaleString()}
- totalLow (${pipeline.highestStage} stage only at ${Math.round((STAGE_WEIGHTS[pipeline.highestStage] || 0.20) * 100)}% confidence): £${pipeline.totalLow.toLocaleString()}
- totalHigh (full pipeline if all opportunities close): £${pipeline.totalHigh.toLocaleString()}
- Opportunity count: ${pipeline.opportunityCount}
- Stage breakdown:
${Object.entries(pipeline.byStage).map(([stage, s]) =>
  `  ${stage}: ${s.count} opportunity(s), £${s.total.toLocaleString()} total, ${Math.round(s.weight * 100)}% confidence weight`
).join('\n')}

Use these values directly:
- revenuePicture.pipelineValue = ${pipeline.exactTotal}
- revenuePicture.totalLow = ${pipeline.totalLow}
- revenuePicture.totalMid = ${pipeline.weightedTotal}
- revenuePicture.totalHigh = ${pipeline.totalHigh}
- revenueTarget = ${pipeline.weightedTotal}

Contact planRole guidance (use suggestedPlanRole as your starting point, override only if the data clearly justifies it):
${enrichedContacts.map(c => `  ${c.name} (${c.title}): suggested planRole = ${c.suggestedPlanRole}`).join('\n')}

Account data:
${JSON.stringify(dataForLLM, null, 2)}`;

    const messages = [
      { role: 'system', content: systemPrompt },
      { role: 'user', content: userMessage }
    ];

    const rawText = await callModel(messages, 4000);
    const { parsed, parseError } = parseModelResponse(rawText);

    // Post-process: override totalLow, totalMid, totalHigh with verified values
    // These are facts, not model output — we never trust the model to compute them
    if (parsed) {
      if (parsed.revenuePicture) {
        parsed.revenuePicture.pipelineValue = pipeline.exactTotal;
        parsed.revenuePicture.totalLow      = pipeline.totalLow;
        parsed.revenuePicture.totalMid      = pipeline.weightedTotal;
        parsed.revenuePicture.totalHigh     = pipeline.totalHigh;
      }
      parsed.revenueTarget = pipeline.weightedTotal;

      // Post-process: correct hasCadence based on actual cadences array
      if (parsed.contactEngagement && parsed.cadences) {
        const cadenceTitles = parsed.cadences.map(c => c.contactTitle.toLowerCase().trim());
        parsed.contactEngagement = parsed.contactEngagement.map(ce => ({
          ...ce,
          hasCadence: cadenceTitles.includes(ce.title.toLowerCase().trim())
        }));
      }
    }

    const sid = sessionId || `session_${Date.now()}`;
    sessions[sid] = {
      messages: [...messages, { role: 'assistant', content: rawText }],
      intentId,
      scenarioId,
      lastParsed: parsed,
      pipeline
    };

    res.json({
      sessionId: sid,
      parsed,
      raw: rawText,
      parseError,
      meta,
      scenarioData: data,
      pipeline
    });
  } catch (e) {
    console.error('Run error:', e);
    res.status(500).json({ error: e.message });
  }
});

module.exports = router;
module.exports.sessions = sessions;