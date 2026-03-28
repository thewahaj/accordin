import React, { useState, useEffect, useCallback } from 'react';
import PlanResult from './components/PlanResult';
import ChatPanel from './components/ChatPanel';
import { fetchIntents, fetchScenarios, fetchScenarioData, runTest, resetSession } from './api';

export default function App() {
  const [intents, setIntents] = useState([]);
  const [scenarios, setScenarios] = useState([]);
  const [selectedIntent, setSelectedIntent] = useState('');
  const [selectedScenario, setSelectedScenario] = useState('');
  const [scenarioMeta, setScenarioMeta] = useState(null);
  const [scenarioJson, setScenarioJson] = useState(null);
  const [showJsonPreview, setShowJsonPreview] = useState(false);
  const [showRawOutput, setShowRawOutput] = useState(false);

  const [running, setRunning] = useState(false);
  const [result, setResult] = useState(null);
  const [currentPlan, setCurrentPlan] = useState(null);
  const [sessionId, setSessionId] = useState(null);
  const [error, setError] = useState(null);

  const [verdict, setVerdict] = useState(null);
  const [rating, setRating] = useState(0);
  const [notes, setNotes] = useState('');
  const [planUpdatedAt, setPlanUpdatedAt] = useState(null);

  useEffect(() => {
    fetchIntents()
      .then(data => {
        setIntents(data);
        if (data.length > 0) setSelectedIntent(data[0].id);
      })
      .catch(() => setError('Could not load intents. Is the backend running on port 3001?'));
  }, []);

  useEffect(() => {
    if (!selectedIntent) return;
    setSelectedScenario('');
    setScenarioMeta(null);
    setScenarioJson(null);
    fetchScenarios(selectedIntent)
      .then(data => {
        setScenarios(data);
        if (data.length > 0) setSelectedScenario(data[0].id);
      })
      .catch(e => setError(e.message));
  }, [selectedIntent]);

  useEffect(() => {
    if (!selectedIntent || !selectedScenario) return;
    fetchScenarioData(selectedIntent, selectedScenario)
      .then(data => {
        setScenarioMeta(data.meta);
        setScenarioJson(data.data);
      })
      .catch(e => setError(e.message));
  }, [selectedIntent, selectedScenario]);

  const handleRun = useCallback(async () => {
    if (!selectedIntent || !selectedScenario) return;
    setRunning(true);
    setError(null);
    setResult(null);
    setCurrentPlan(null);
    setVerdict(null);
    setRating(0);
    setNotes('');

    if (sessionId) {
      await resetSession(sessionId).catch(() => {});
      setSessionId(null);
    }

    try {
      const data = await runTest(selectedIntent, selectedScenario);
      setResult(data);
      setCurrentPlan(data.parsed);
      setSessionId(data.sessionId);
    } catch (e) {
      setError(e.message);
    } finally {
      setRunning(false);
    }
  }, [selectedIntent, selectedScenario, sessionId]);

  function handlePlanUpdate(updatedPlan) {
    setCurrentPlan(updatedPlan);
    setPlanUpdatedAt(new Date());
  }

  function handleReset() {
    if (sessionId) resetSession(sessionId).catch(() => {});
    setSessionId(null);
    setResult(null);
    setCurrentPlan(null);
    setVerdict(null);
    setRating(0);
    setNotes('');
    setError(null);
  }

  const difficultyClass = scenarioMeta?.difficulty
    ? `difficulty-${scenarioMeta.difficulty.toLowerCase().replace(/\s/g, '-')}`
    : '';

  return (
    <div id="root" style={{ display: 'flex', flexDirection: 'column', height: '100vh' }}>

      {/* HEADER */}
      <div className="app-header">
        <svg width="20" height="20" viewBox="0 0 20 20" fill="none">
          <rect x="2" y="2" width="16" height="16" rx="3" fill="rgba(255,255,255,0.2)" />
          <path d="M6 10h8M10 6v8" stroke="white" strokeWidth="1.5" strokeLinecap="round" />
        </svg>
        <span className="app-header-title">Account Planning Copilot</span>
        <span className="app-header-subtitle">Prompt Evaluation Workbench</span>
        <span className="app-header-badge">DEV / TESTING</span>
      </div>

      <div className="app-body">

        {/* LEFT PANEL */}
        <div className="left-panel">
          <div className="panel-header">
            <div className="panel-header-title">Test Controls</div>
          </div>

          <div className="panel-body">

            {/* Intent */}
            <div className="field-group">
              <label className="field-label">Intent</label>
              <select
                value={selectedIntent}
                onChange={e => setSelectedIntent(e.target.value)}
                style={{
                  width: '100%', height: 30, border: '1px solid #c8c6c4',
                  borderRadius: 2, padding: '0 8px', fontSize: 13,
                  background: '#fff', color: '#201f1e'
                }}
              >
                {intents.map(i => <option key={i.id} value={i.id}>{i.label}</option>)}
              </select>
            </div>

            {/* Scenario */}
            <div className="field-group">
              <label className="field-label">Scenario</label>
              <select
                value={selectedScenario}
                onChange={e => setSelectedScenario(e.target.value)}
                style={{
                  width: '100%', height: 30, border: '1px solid #c8c6c4',
                  borderRadius: 2, padding: '0 8px', fontSize: 13,
                  background: '#fff', color: '#201f1e'
                }}
              >
                {scenarios.map(s => <option key={s.id} value={s.id}>{s.title || s.id}</option>)}
              </select>
            </div>

            {/* Scenario Meta */}
            {scenarioMeta && (
              <div className="scenario-meta">
                <div className="scenario-meta-title">{scenarioMeta.title || selectedScenario}</div>
                {scenarioMeta.description && (
                  <div className="scenario-meta-desc">{scenarioMeta.description}</div>
                )}
                <div className="tag-row">
                  {scenarioMeta.difficulty && (
                    <span className={`tag ${difficultyClass}`}>{scenarioMeta.difficulty}</span>
                  )}
                  {scenarioMeta.tags?.map((t, i) => (
                    <span key={i} className="tag">{t}</span>
                  ))}
                </div>
              </div>
            )}

            {/* JSON Preview toggle */}
            {scenarioJson && (
              <div className="field-group">
                <button
                  className={`toggle-btn ${showJsonPreview ? 'active' : ''}`}
                  onClick={() => setShowJsonPreview(v => !v)}
                  style={{ width: '100%', height: 28 }}
                >
                  {showJsonPreview ? 'Hide' : 'Show'} Scenario JSON
                </button>
                {showJsonPreview && (
                  <div className="json-preview">{JSON.stringify(scenarioJson, null, 2)}</div>
                )}
              </div>
            )}

            {/* Run Button */}
            <button
              className="run-btn"
              onClick={handleRun}
              disabled={running || !selectedIntent || !selectedScenario}
            >
              {running
                ? <><span className="spinner" style={{ width: 14, height: 14 }} /> Running...</>
                : '▶  Run Test'}
            </button>

            {result && (
              <button
                onClick={handleReset}
                style={{
                  width: '100%', height: 28, background: '#fff',
                  border: '1px solid #c8c6c4', borderRadius: 2,
                  fontSize: 12, color: '#605e5c', cursor: 'pointer'
                }}
              >
                ↺ Reset Session
              </button>
            )}

          </div>

          {/* Evaluation Section */}
          {result && (
            <div className="eval-section">
              <div className="eval-label">Evaluation</div>

              <div className="eval-verdict">
                <button
                  className={`verdict-btn pass ${verdict === 'pass' ? 'active' : ''}`}
                  onClick={() => setVerdict(v => v === 'pass' ? null : 'pass')}
                >✓ Pass</button>
                <button
                  className={`verdict-btn fail ${verdict === 'fail' ? 'active' : ''}`}
                  onClick={() => setVerdict(v => v === 'fail' ? null : 'fail')}
                >✗ Fail</button>
              </div>

              <div className="field-group">
                <div className="field-label" style={{ fontSize: 11 }}>Quality Rating</div>
                <div className="rating-row">
                  {[1, 2, 3, 4, 5].map(n => (
                    <button
                      key={n}
                      className={`rating-dot ${rating >= n ? 'active' : ''}`}
                      onClick={() => setRating(r => r === n ? 0 : n)}
                    >{n}</button>
                  ))}
                </div>
              </div>

              <div className="field-group">
                <div className="field-label" style={{ fontSize: 11 }}>Evaluator Notes</div>
                <textarea
                  className="notes-area"
                  value={notes}
                  onChange={e => setNotes(e.target.value)}
                  placeholder="What worked? What broke? Hallucination risk? Acceptable output?"
                />
              </div>
            </div>
          )}
        </div>

        {/* CENTER PANEL */}
        <div className="center-panel">
          <div className="result-toolbar">
            <span className="toolbar-label">
              {result
                ? `Result: ${scenarioMeta?.title || selectedScenario} — ${selectedIntent}`
                : 'No result yet'}
            </span>
            {planUpdatedAt && (
              <span style={{
                fontSize: 11, color: '#107c10', fontWeight: 600,
                background: '#dff6dd', border: '1px solid #92d050',
                borderRadius: 2, padding: '2px 8px',
                display: 'flex', alignItems: 'center', gap: 4
              }}>
                ✓ Updated {planUpdatedAt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}
              </span>
            )}
            {result && (
              <button
                className={`toggle-btn ${showRawOutput ? 'active' : ''}`}
                onClick={() => setShowRawOutput(v => !v)}
              >
                {showRawOutput ? 'Hide' : 'Show'} Raw JSON
              </button>
            )}
          </div>

          <div className="result-body">
            {error && (
              <div className="error-bar">
                ⚠ {error}
              </div>
            )}

            {running && (
              <div className="loading-overlay">
                <div className="spinner" />
                <div>Calling Azure OpenAI...</div>
                <div style={{ fontSize: 11, color: '#a19f9d' }}>This usually takes 5–15 seconds</div>
              </div>
            )}

            {!running && !result && !error && (
              <div className="empty-state">
                <div className="empty-state-icon">📋</div>
                <div className="empty-state-text">Select an intent and scenario, then click Run Test</div>
              </div>
            )}

            {!running && result && (
              <PlanResult
                plan={currentPlan}
                raw={result.raw}
                parseError={result.parseError}
                showRaw={showRawOutput}
              />
            )}
          </div>
        </div>

        {/* RIGHT PANEL */}
        <div className="right-panel">
          <ChatPanel
            sessionId={sessionId}
            onPlanUpdate={handlePlanUpdate}
            disabled={running}
          />
        </div>

      </div>
    </div>
  );
}
