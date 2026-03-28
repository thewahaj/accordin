const BASE = 'http://localhost:3001/api';

export async function fetchIntents() {
  const r = await fetch(`${BASE}/intents`);
  if (!r.ok) throw new Error('Failed to load intents');
  return r.json();
}

export async function fetchScenarios(intentId) {
  const r = await fetch(`${BASE}/intents/${intentId}/scenarios`);
  if (!r.ok) throw new Error('Failed to load scenarios');
  return r.json();
}

export async function fetchScenarioData(intentId, scenarioId) {
  const r = await fetch(`${BASE}/intents/${intentId}/scenarios/${scenarioId}`);
  if (!r.ok) throw new Error('Scenario not found');
  return r.json();
}

export async function runTest(intentId, scenarioId, sessionId = null) {
  const r = await fetch(`${BASE}/run`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ intentId, scenarioId, sessionId })
  });
  const data = await r.json();
  if (!r.ok) throw new Error(data.error || 'Run failed');
  return data;
}

export async function sendChatMessage(sessionId, message) {
  const r = await fetch(`${BASE}/chat`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId, message })
  });
  const data = await r.json();
  if (!r.ok) throw new Error(data.error || 'Chat failed');
  return data;
}

export async function resetSession(sessionId) {
  await fetch(`${BASE}/chat/${sessionId}`, { method: 'DELETE' });
}

export async function checkHealth() {
  const r = await fetch(`${BASE}/health`);
  return r.json();
}
