import React, { useState } from 'react';
import ContactEngagement from './ContactEngagement';

function formatCurrency(val) {
  if (!val && val !== 0) return '-';
  return new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP', maximumFractionDigits: 0 }).format(val);
}

function SectionWrapper({ title, count, children, defaultOpen = true }) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div className="result-section">
      <div className="section-header" style={{ cursor: 'pointer' }} onClick={() => setOpen(o => !o)}>
        <span className="section-title">{title}</span>
        {count != null && <span className="section-count">{count}</span>}
        <span style={{ marginLeft: 'auto', fontSize: 10, color: '#605e5c' }}>{open ? '▲' : '▼'}</span>
      </div>
      {open && <div className="section-body">{children}</div>}
    </div>
  );
}

function ConfidenceBadge({ level }) {
  const cls = level === 'high' ? 'conf-high' : level === 'medium' ? 'conf-medium' : 'conf-low';
  return <span className={`confidence-badge ${cls}`}>{level?.toUpperCase()}</span>;
}

function TypeBadge({ type }) {
  const cls = type === 'cross-sell' ? 'badge-cross-sell'
    : type === 'upsell' ? 'badge-upsell'
    : type === 'retention' ? 'badge-retention'
    : 'badge-relationship';
  return <span className={`rec-type-badge ${cls}`}>{type}</span>;
}

function FreqBadge({ freq }) {
  const cls = freq === 'weekly' ? 'freq-weekly'
    : freq === 'biweekly' ? 'freq-biweekly'
    : freq === 'monthly' ? 'freq-monthly'
    : 'freq-quarterly';
  return <span className={`cadence-badge ${cls}`}>{freq}</span>;
}

function PriorityBar({ priority }) {
  const cls = priority === 'high' ? 'pri-high' : priority === 'medium' ? 'pri-medium' : 'pri-low';
  return <div className={`action-priority-bar ${cls}`} title={`Priority: ${priority}`} />;
}

export default function PlanResult({ plan, raw, parseError, showRaw }) {
  if (!plan && !raw) return null;

  if (parseError || (!plan && raw)) {
    return (
      <div>
        <div className="parse-warning">
          ⚠ The model response could not be parsed as JSON. Raw output shown below.
          {parseError && <span style={{ display: 'block', marginTop: 4 }}>Error: {parseError}</span>}
        </div>
        <pre className="raw-json">{raw}</pre>
      </div>
    );
  }

  return (
    <div className="result-grid">
      {/* KPIs */}
      <div className="kpi-row">
        <div className="kpi-card">
          <div className="kpi-label">Target (Mid)</div>
          <div className="kpi-value">{formatCurrency(plan.revenueTarget)}</div>
        </div>
        <div className="kpi-card">
          <div className="kpi-label">Pipeline Now</div>
          <div className="kpi-value" style={{ color: '#107c10' }}>
            {formatCurrency(plan.revenuePicture?.pipelineValue)}
          </div>
        </div>
        <div className="kpi-card">
          <div className="kpi-label">Whitespace Est.</div>
          <div className="kpi-value" style={{ color: '#e07d10' }}>
            {formatCurrency(plan.revenuePicture?.whitespaceEstimate)}
          </div>
        </div>
        <div className="kpi-card">
          <div className="kpi-label">High Scenario</div>
          <div className="kpi-value" style={{ color: '#0f6cbd' }}>
            {formatCurrency(plan.revenuePicture?.totalHigh)}
          </div>
        </div>
      </div>

      {/* Revenue Picture */}
      {plan.revenuePicture && (
        <SectionWrapper title="Revenue Picture">
          {/* Scenario bar */}
          <div style={{ display: 'flex', gap: 8, marginBottom: 12 }}>
            {[
              { label: 'Low', value: plan.revenuePicture.totalLow, color: '#a19f9d' },
              { label: 'Mid (Target)', value: plan.revenuePicture.totalMid, color: '#0f6cbd' },
              { label: 'High', value: plan.revenuePicture.totalHigh, color: '#107c10' },
            ].map(s => (
              <div key={s.label} style={{
                flex: 1, background: '#f3f2f1', border: '1px solid #e1dfdd',
                borderRadius: 2, padding: '8px 10px', textAlign: 'center'
              }}>
                <div style={{ fontSize: 10, color: '#605e5c', fontWeight: 600, textTransform: 'uppercase', letterSpacing: '0.04em' }}>{s.label}</div>
                <div style={{ fontSize: 16, fontWeight: 700, color: s.color, marginTop: 2 }}>{formatCurrency(s.value)}</div>
              </div>
            ))}
          </div>

          {/* Pipeline vs Whitespace breakdown */}
          <div style={{ display: 'flex', gap: 8, marginBottom: 10 }}>
            <div style={{ flex: 1, background: '#eaf5ea', border: '1px solid #c8e6c9', borderRadius: 2, padding: '8px 10px' }}>
              <div style={{ fontSize: 10, fontWeight: 700, color: '#107c10', textTransform: 'uppercase', letterSpacing: '0.04em', marginBottom: 4 }}>
                Pipeline — {formatCurrency(plan.revenuePicture.pipelineValue)}
              </div>
              <div style={{ fontSize: 11, color: '#323130', lineHeight: 1.5 }}>
                {plan.revenuePicture.pipelineDetail}
              </div>
            </div>
            <div style={{ flex: 1, background: '#fff4ce', border: '1px solid #ffe4a0', borderRadius: 2, padding: '8px 10px' }}>
              <div style={{ fontSize: 10, fontWeight: 700, color: '#7a5600', textTransform: 'uppercase', letterSpacing: '0.04em', marginBottom: 4 }}>
                Whitespace — {formatCurrency(plan.revenuePicture.whitespaceEstimate)}
              </div>
              <div style={{ fontSize: 11, color: '#323130', lineHeight: 1.5 }}>
                {plan.revenuePicture.whitespaceDetail}
              </div>
            </div>
          </div>

          {plan.revenuePicture.confidenceBand && (
            <div className="rationale-block">
              <div className="rationale-label">Confidence band reasoning</div>
              {plan.revenuePicture.confidenceBand}
            </div>
          )}
        </SectionWrapper>
      )}

      {/* Opening Statement */}
      {plan.openingStatement && (
        <SectionWrapper title="Opening Statement">
          <p className="statement-text">{plan.openingStatement}</p>
        </SectionWrapper>
      )}

      {/* Contact Engagement */}
      {plan.contactEngagement?.length > 0 && (
        <ContactEngagement contacts={plan.contactEngagement} />
      )}

      {/* Health + Signals */}
      <div className="two-col">
        {plan.healthSummary && (
          <SectionWrapper title="Health Summary">
            <p className="summary-text">{plan.healthSummary}</p>
          </SectionWrapper>
        )}
        {plan.growthObjectives && (
          <SectionWrapper title="Growth Objectives">
            <p className="summary-text">{plan.growthObjectives}</p>
          </SectionWrapper>
        )}
      </div>

      {/* Signals */}
      {((plan.positiveSignals?.length > 0) || (plan.watchouts?.length > 0)) && (
        <div className="two-col">
          {plan.positiveSignals?.length > 0 && (
            <SectionWrapper title="Positive Signals" count={plan.positiveSignals.length}>
              <div className="signal-list">
                {plan.positiveSignals.map((s, i) => (
                  <div key={i} className="signal-item">
                    <div className="signal-dot positive" />
                    {s}
                  </div>
                ))}
              </div>
            </SectionWrapper>
          )}
          {plan.watchouts?.length > 0 && (
            <SectionWrapper title="Watchouts" count={plan.watchouts.length}>
              <div className="signal-list">
                {plan.watchouts.map((w, i) => (
                  <div key={i} className="signal-item">
                    <div className="signal-dot watchout" />
                    {w}
                  </div>
                ))}
              </div>
            </SectionWrapper>
          )}
        </div>
      )}

      {/* Forecast */}
      {plan.forecastNarrative && (
        <SectionWrapper title="Forecast Narrative">
          <p className="summary-text">{plan.forecastNarrative}</p>
        </SectionWrapper>
      )}

      {/* Recommendations */}
      {plan.recommendations?.length > 0 && (
        <SectionWrapper title="Recommendations" count={plan.recommendations.length}>
          <div className="rec-cards">
            {plan.recommendations.map((rec, i) => (
              <div key={i} className="rec-card">
                <div className="rec-card-header">
                  <TypeBadge type={rec.type} />
                  {rec.productName && <span className="rec-product">{rec.productName}</span>}
                  {rec.estimatedValue > 0 && (
                    <span className="rec-value">{formatCurrency(rec.estimatedValue)}</span>
                  )}
                  <ConfidenceBadge level={rec.confidence} />
                </div>
                <div className="rec-card-body">
                  <p className="rec-desc">{rec.description}</p>
                  <div className="rationale-block">
                    <div className="rationale-label">Why recommended</div>
                    {rec.rationale}
                  </div>
                  {rec.confidenceReason && (
                    <p className="conf-reason">Confidence: {rec.confidenceReason}</p>
                  )}
                </div>
              </div>
            ))}
          </div>
        </SectionWrapper>
      )}

      {/* Cadences */}
      {plan.cadences?.length > 0 && (
        <SectionWrapper title="Engagement Cadences" count={plan.cadences.length}>
          <div className="cadence-cards">
            {plan.cadences.map((c, i) => (
              <div key={i} className="cadence-card">
                <div className="cadence-header">
                  <span className="cadence-name">{c.name}</span>
                  <FreqBadge freq={c.frequency} />
                  <span className="channel-badge">{c.channel}</span>
                  {c.locationAware && (
                    <span style={{ fontSize: 10, color: '#605e5c' }}>📍 location-aware</span>
                  )}
                </div>
                <div style={{ fontSize: 11, color: '#605e5c' }}>Contact: {c.contactTitle}</div>
                <p className="cadence-purpose">{c.purpose}</p>
                <div className="rationale-block">
                  <div className="rationale-label">Why this cadence</div>
                  {c.rationale}
                </div>
              </div>
            ))}
          </div>
        </SectionWrapper>
      )}

      {/* One-off Actions */}
      {plan.oneOffActions?.length > 0 && (
        <SectionWrapper title="One-off Actions" count={plan.oneOffActions.length}>
          <div className="action-rows">
            {plan.oneOffActions.map((a, i) => (
              <div key={i} className="action-row">
                <PriorityBar priority={a.priority} />
                <div className="action-content">
                  <p className="action-desc">{a.description}</p>
                  <div className="action-meta">
                    <span className="action-meta-item">📡 {a.channel}</span>
                    <span className="action-meta-item">🕐 {a.suggestedTiming}</span>
                    <span className="action-meta-item" style={{
                      color: a.priority === 'high' ? '#a4262c' : a.priority === 'medium' ? '#e07d10' : '#107c10',
                      fontWeight: 600
                    }}>{a.priority?.toUpperCase()}</span>
                  </div>
                  {a.rationale && (
                    <div className="rationale-block" style={{ marginTop: 2 }}>
                      <div className="rationale-label">Rationale</div>
                      {a.rationale}
                    </div>
                  )}
                </div>
              </div>
            ))}
          </div>
        </SectionWrapper>
      )}

      {/* Data Limitations */}
      {plan.dataLimitations?.length > 0 && (
        <SectionWrapper title="Data Limitations" count={plan.dataLimitations.length}>
          <div className="limitation-list">
            {plan.dataLimitations.map((l, i) => (
              <div key={i} className="limitation-item">
                <span>⚠</span> {l}
              </div>
            ))}
          </div>
        </SectionWrapper>
      )}

      {/* Raw JSON */}
      {showRaw && (
        <SectionWrapper title="Raw JSON Response" defaultOpen={false}>
          <pre className="raw-json">{JSON.stringify(plan, null, 2)}</pre>
        </SectionWrapper>
      )}
    </div>
  );
}