const express = require('express');
const router = express.Router();
const { callModel, parseModelResponse } = require('../azureClient');
const { sessions } = require('./run');

const CONVERSATION_SYSTEM_SUFFIX = `

You are now in conversation mode helping an account manager refine their account plan.

CRITICAL RESPONSE RULES - follow these exactly:

RULE 1 - WHEN THE MANAGER ASKS FOR A CHANGE TO THE PLAN:
Any request that modifies cadences, recommendations, actions, revenue targets, or any other plan field
MUST return a response in this exact format with no deviation:

[PLAN_UPDATE]
{complete updated JSON plan object}
[END_PLAN]

After [END_PLAN] you may add a brief plain English explanation (max 2 sentences) of what changed and any trade-offs.

RULE 2 - WHEN THE MANAGER ASKS A QUESTION OR REQUESTS REASONING:
Respond in plain conversational English only. No JSON. Max 150 words.
Always reference specific data from the account when explaining. Never answer generically.

RULE 3 - HOW TO IDENTIFY A CHANGE REQUEST vs A QUESTION:
Change requests use words like: change, update, modify, reduce, increase, remove, add, make it, set it, switch
Questions use words like: why, what, how, explain, tell me, is this, should we

RULE 4 - THE JSON IN A PLAN UPDATE:
Must be the complete plan object - not just the changed section.
Must follow the exact same schema as the original plan response.
Must preserve all unchanged fields from the current plan.

RULE 5 - WHITESPACE AND TERMINOLOGY:
When asked about whitespace, revenue potential, pipeline, or any business term,
always answer in context of THIS specific account's data.
Never give a generic textbook definition.`;

function extractPlanJson(rawText) {
  // Try [PLAN_UPDATE] ... [END_PLAN] format first
  const markerMatch = rawText.match(/\[PLAN_UPDATE\]([\s\S]*?)\[END_PLAN\]/);
  if (markerMatch) {
    return {
      jsonText: markerMatch[1].trim(),
      explanation: rawText.replace(markerMatch[0], '').trim()
    };
  }

  // Fallback: [PLAN_UPDATE] followed by JSON (no end marker)
  const legacyMatch = rawText.match(/\[PLAN_UPDATE\]([\s\S]*)/);
  if (legacyMatch) {
    const remainder = legacyMatch[1].trim();
    const jsonStart = remainder.indexOf('{');
    const jsonEnd = remainder.lastIndexOf('}');
    if (jsonStart !== -1 && jsonEnd !== -1) {
      return {
        jsonText: remainder.substring(jsonStart, jsonEnd + 1),
        explanation: remainder.substring(jsonEnd + 1).trim()
      };
    }
  }

  // Last resort: look for a large JSON object in the response
  const jsonStart = rawText.indexOf('{');
  const jsonEnd = rawText.lastIndexOf('}');
  if (jsonStart !== -1 && jsonEnd !== -1 && jsonEnd - jsonStart > 200) {
    return {
      jsonText: rawText.substring(jsonStart, jsonEnd + 1),
      explanation: rawText.substring(0, jsonStart).replace('[PLAN_UPDATE]', '').trim()
    };
  }

  return null;
}

router.post('/', async (req, res) => {
  const { sessionId, message } = req.body;

  if (!sessionId || !message) {
    return res.status(400).json({ error: 'sessionId and message are required' });
  }

  const session = sessions[sessionId];
  if (!session) {
    return res.status(404).json({ error: 'Session not found. Run a test first.' });
  }

  try {
    let messages = session.messages;

    // Inject conversation rules into system message if not already present
    if (messages[0].role === 'system' && !messages[0].content.includes('CONVERSATION_SYSTEM_SUFFIX_APPLIED')) {
      messages = [
        { role: 'system', content: messages[0].content + CONVERSATION_SYSTEM_SUFFIX + '\n\n<!-- CONVERSATION_SYSTEM_SUFFIX_APPLIED -->' },
        ...messages.slice(1)
      ];
    }

    // Cap history to last 20 messages
    const history = messages.slice(-20);
    const updatedMessages = [...history, { role: 'user', content: message }];

    const rawText = await callModel(updatedMessages, 2500);

    const isPlanUpdate = rawText.includes('[PLAN_UPDATE]');
    let parsed = null;
    let parseError = null;
    let explanation = rawText;

    if (isPlanUpdate) {
      const extracted = extractPlanJson(rawText);
      if (extracted) {
        const result = parseModelResponse(extracted.jsonText);
        parsed = result.parsed;
        parseError = result.parseError;
        explanation = extracted.explanation || 'Plan updated.';
        if (parsed) session.lastParsed = parsed;
      } else {
        parseError = 'Could not extract JSON from plan update response.';
      }
    }

    // Save to session history
    session.messages.push({ role: 'user', content: message });
    session.messages.push({ role: 'assistant', content: rawText });

    res.json({
      sessionId,
      message: explanation,
      isPlanUpdate,
      parsed,
      parseError,
      currentPlan: session.lastParsed
    });
  } catch (e) {
    console.error('Chat error:', e);
    res.status(500).json({ error: e.message });
  }
});

router.delete('/:sessionId', (req, res) => {
  delete sessions[req.params.sessionId];
  res.json({ message: 'Session cleared' });
});

module.exports = router;
