import React, { useState, useRef, useEffect } from 'react';
import { sendChatMessage } from '../api';

const QUICK_PROMPTS = [
  'Why did you recommend this?',
  'What assumptions are weak here?',
  'Reduce the cadence frequency.',
  'Make this more conservative.',
  'Which risk is highest priority?',
  'What is the whitespace for this account?',
];

export default function ChatPanel({ sessionId, onPlanUpdate, disabled }) {
  const [messages, setMessages] = useState([]);
  const [input, setInput] = useState('');
  const [sending, setSending] = useState(false);
  const bottomRef = useRef(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  // Reset messages when session changes (new test run)
  useEffect(() => {
    setMessages([]);
  }, [sessionId]);

  async function send(text) {
    const msg = text || input.trim();
    if (!msg || !sessionId || sending) return;

    setInput('');
    setMessages(prev => [...prev, { role: 'user', content: msg }]);
    setSending(true);

    try {
      const result = await sendChatMessage(sessionId, msg);

      setMessages(prev => [...prev, {
        role: 'assistant',
        content: result.message,
        isPlanUpdate: result.isPlanUpdate,
        parseError: result.parseError
      }]);

      if (result.isPlanUpdate && result.currentPlan) {
        onPlanUpdate(result.currentPlan);
      }
    } catch (e) {
      setMessages(prev => [...prev, {
        role: 'assistant',
        content: e.message,
        isError: true
      }]);
    } finally {
      setSending(false);
    }
  }

  function handleKeyDown(e) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  }

  return (
    <>
      <div className="panel-header">
        <div className="panel-header-title">Copilot Refinement</div>
      </div>

      {!sessionId && (
        <div style={{ padding: '16px 14px', fontSize: 12, color: '#a19f9d', lineHeight: 1.5 }}>
          Run a test to start the refinement conversation.
        </div>
      )}

      {sessionId && messages.length === 0 && (
        <div style={{ padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: 6 }}>
          <div style={{ fontSize: 11, color: '#605e5c', marginBottom: 4 }}>Quick prompts:</div>
          {QUICK_PROMPTS.map((q, i) => (
            <button
              key={i}
              onClick={() => send(q)}
              disabled={sending || disabled}
              style={{
                background: '#f3f2f1', border: '1px solid #e1dfdd',
                borderRadius: 2, padding: '5px 10px', fontSize: 11,
                color: '#323130', cursor: 'pointer', textAlign: 'left',
                transition: 'background 0.15s'
              }}
              onMouseOver={e => e.currentTarget.style.background = '#deecf9'}
              onMouseOut={e => e.currentTarget.style.background = '#f3f2f1'}
            >
              {q}
            </button>
          ))}
        </div>
      )}

      <div className="chat-messages">
        {messages.map((m, i) => (
          <div key={i} className="chat-message">
            <div className={`chat-role ${m.role}`}>
              {m.role === 'user' ? 'You' : 'Copilot'}
            </div>

            {m.isPlanUpdate ? (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                {/* Plan updated banner */}
                <div style={{
                  background: '#dff6dd', border: '1px solid #92d050',
                  borderRadius: 2, padding: '6px 10px',
                  display: 'flex', alignItems: 'center', gap: 6,
                  fontSize: 11, fontWeight: 600, color: '#107c10'
                }}>
                  <span style={{ fontSize: 14 }}>✓</span>
                  Plan updated — center panel refreshed
                </div>
                {/* Explanation if any */}
                {m.content && m.content.trim() && (
                  <div className="chat-bubble assistant">
                    {m.content}
                  </div>
                )}
                {m.parseError && (
                  <div style={{
                    background: '#fff4ce', border: '1px solid #ffe4a0',
                    borderRadius: 2, padding: '5px 8px',
                    fontSize: 11, color: '#7a5600'
                  }}>
                    ⚠ Parse issue: {m.parseError}
                  </div>
                )}
              </div>
            ) : (
              <div className={`chat-bubble ${m.role}${m.isError ? ' plan-update' : ''}`}
                style={m.isError ? { background: '#fde7e9', borderColor: '#f4b8bb', color: '#a4262c' } : {}}>
                {m.content}
              </div>
            )}
          </div>
        ))}

        {sending && (
          <div className="chat-message">
            <div className="chat-role assistant">Copilot</div>
            <div className="chat-bubble assistant" style={{ color: '#a19f9d', display: 'flex', alignItems: 'center', gap: 6 }}>
              <span className="spinner" style={{ width: 12, height: 12, display: 'inline-block', flexShrink: 0 }} />
              Thinking...
            </div>
          </div>
        )}
        <div ref={bottomRef} />
      </div>

      {sessionId && (
        <div className="chat-input-area">
          <textarea
            className="chat-input"
            value={input}
            onChange={e => setInput(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Ask about the plan or request changes... (Enter to send)"
            disabled={sending || disabled}
          />
          <div className="chat-actions">
            <button
              className="send-btn"
              onClick={() => send()}
              disabled={!input.trim() || sending || disabled}
            >
              Send
            </button>
            <button
              className="reset-btn"
              onClick={() => setMessages([])}
              title="Clear conversation history"
            >
              Clear
            </button>
          </div>
        </div>
      )}
    </>
  );
}
