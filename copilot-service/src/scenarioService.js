const fs = require('fs');
const path = require('path');

//const DATA_ROOT = path.join(__dirname, '../../test-data');
const DATA_ROOT = path.join(__dirname, '../test-data');

function getIntents() {
  if (!fs.existsSync(DATA_ROOT)) return [];
  return fs.readdirSync(DATA_ROOT)
    .filter(name => fs.statSync(path.join(DATA_ROOT, name)).isDirectory())
    .map(name => ({
      id: name,
      label: formatLabel(name),
      path: path.join(DATA_ROOT, name)
    }));
}

function getScenarios(intentId) {
  const scenariosDir = path.join(DATA_ROOT, intentId, 'scenarios');
  if (!fs.existsSync(scenariosDir)) return [];
  return fs.readdirSync(scenariosDir)
    .filter(f => f.endsWith('.json'))
    .map(filename => {
      const filePath = path.join(scenariosDir, filename);
      let meta = {};
      try {
        const content = JSON.parse(fs.readFileSync(filePath, 'utf8'));
        meta = {
          title: content._meta?.title || formatLabel(filename.replace('.json', '')),
          description: content._meta?.description || '',
          tags: content._meta?.tags || [],
          difficulty: content._meta?.difficulty || ''
        };
      } catch (_) {}
      return {
        id: filename.replace('.json', ''),
        filename,
        ...meta
      };
    });
}

function loadScenario(intentId, scenarioId) {
  const filePath = path.join(DATA_ROOT, intentId, 'scenarios', `${scenarioId}.json`);
  if (!fs.existsSync(filePath)) {
    throw new Error(`Scenario not found: ${intentId}/${scenarioId}`);
  }
  const content = JSON.parse(fs.readFileSync(filePath, 'utf8'));
  const { _meta, ...scenarioData } = content;
  return { meta: _meta || {}, data: scenarioData };
}

function getSystemPrompt(intentId) {
  const promptPath = path.join(DATA_ROOT, intentId, 'system-prompt.txt');
  if (fs.existsSync(promptPath)) {
    return fs.readFileSync(promptPath, 'utf8');
  }
  return getDefaultSystemPrompt();
}

function getDefaultSystemPrompt() {
  return `You are an expert B2B account strategist specialising in cross-sell and expansion.
You will receive structured CRM data for a customer account and a plan intent from the account manager.

Act as a planning copilot. Propose a plan grounded entirely in the data provided.
Never invent facts not present in the data. If data is insufficient, say so explicitly.

For cadences: if a contact is in a different city or country from the account location,
suggest online meetings by default. If the contact is local, in-person is appropriate for senior contacts.

Return ONLY valid JSON with this exact structure. No preamble, no explanation, no markdown, no code fences:

{
  "openingStatement": "2-3 sentence strategic assessment referencing the AM intent",
  "healthSummary": "string",
  "positiveSignals": ["string"],
  "watchouts": ["string"],
  "growthObjectives": "string",
  "revenueTarget": 0,
  "forecastNarrative": "string",
  "recommendations": [{
    "type": "cross-sell|upsell|retention|relationship",
    "productName": "string or null",
    "description": "string",
    "rationale": "cite specific data points from the account",
    "estimatedValue": 0,
    "confidence": "high|medium|low",
    "confidenceReason": "string"
  }],
  "cadences": [{
    "name": "descriptive engagement name",
    "contactTitle": "string",
    "frequency": "weekly|biweekly|monthly|quarterly",
    "channel": "phone|online-meeting|in-person|email",
    "locationAware": true,
    "purpose": "string",
    "rationale": "why this frequency and channel"
  }],
  "oneOffActions": [{
    "description": "string",
    "channel": "phone|online-meeting|in-person|email|other",
    "priority": "high|medium|low",
    "suggestedTiming": "string",
    "rationale": "string"
  }],
  "dataLimitations": ["only if gaps materially affect plan quality"]
}

Rules:
- Max 3 recommendations
- Max 3 cadences
- Max 4 one-off actions
- Revenue target in GBP
- Every rationale must cite a specific value, date, or fact from the account data
- openingStatement must state strategic assessment, not paraphrase the intent
- cadences.name must be descriptive, not the contact name
- Review all open opportunities and consider surfacing as recommendations
- dataLimitations only references gaps in the provided data`;
}

function formatLabel(str) {
  return str.replace(/-/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
}

module.exports = { getIntents, getScenarios, loadScenario, getSystemPrompt };
