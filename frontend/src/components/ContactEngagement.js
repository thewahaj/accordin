import React from 'react';

const ROLE_CONFIG = {
  'primary-relationship': {
    label: 'Primary Relationship',
    color: '#0f6cbd',
    bg: '#deecf9',
    border: '#c7e0f4',
    icon: '★'
  },
  'opportunity-owner': {
    label: 'Opportunity Owner',
    color: '#107c10',
    bg: '#dff6dd',
    border: '#92d050',
    icon: '◆'
  },
  'approval-risk': {
    label: 'Approval Risk',
    color: '#a4262c',
    bg: '#fde7e9',
    border: '#f4b8bb',
    icon: '!'
  },
  'low-priority': {
    label: 'Low Priority',
    color: '#605e5c',
    bg: '#f3f2f1',
    border: '#e1dfdd',
    icon: '·'
  },
  'no-data': {
    label: 'No Data',
    color: '#a19f9d',
    bg: '#faf9f8',
    border: '#e1dfdd',
    icon: '?'
  }
};

const ENGAGEMENT_CONFIG = {
  'High':    { color: '#107c10', bg: '#dff6dd' },
  'Medium':  { color: '#7a5600', bg: '#fff4ce' },
  'Low':     { color: '#a4262c', bg: '#fde7e9' },
  'Unknown': { color: '#a19f9d', bg: '#f3f2f1' }
};

function EngagementDot({ level }) {
  const cfg = ENGAGEMENT_CONFIG[level] || ENGAGEMENT_CONFIG['Unknown'];
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 4,
      fontSize: 11, fontWeight: 600, color: cfg.color,
      background: cfg.bg, padding: '2px 7px', borderRadius: 2
    }}>
      {level || 'Unknown'}
    </span>
  );
}

function RoleBadge({ role }) {
  const cfg = ROLE_CONFIG[role] || ROLE_CONFIG['low-priority'];
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 4,
      fontSize: 10, fontWeight: 700, color: cfg.color,
      background: cfg.bg, padding: '2px 7px', borderRadius: 2,
      textTransform: 'uppercase', letterSpacing: '0.04em',
      border: `1px solid ${cfg.border}`
    }}>
      <span style={{ fontSize: 11 }}>{cfg.icon}</span>
      {cfg.label}
    </span>
  );
}

export default function ContactEngagement({ contacts }) {
  if (!contacts || contacts.length === 0) return null;

  // Sort: primary-relationship first, then by role priority, then by engagement
  const ROLE_ORDER = {
    'primary-relationship': 0,
    'opportunity-owner': 1,
    'approval-risk': 2,
    'low-priority': 3,
    'no-data': 4
  };
  const ENG_ORDER = { 'High': 0, 'Medium': 1, 'Low': 2, 'Unknown': 3 };

  const sorted = [...contacts].sort((a, b) => {
    const roleDiff = (ROLE_ORDER[a.planRole] ?? 3) - (ROLE_ORDER[b.planRole] ?? 3);
    if (roleDiff !== 0) return roleDiff;
    return (ENG_ORDER[a.engagementLevel] ?? 3) - (ENG_ORDER[b.engagementLevel] ?? 3);
  });

  return (
    <div className="result-section">
      <div className="section-header">
        <span className="section-title">Contact Engagement</span>
        <span className="section-count">{contacts.length}</span>
        <span style={{ marginLeft: 'auto', fontSize: 10, color: '#605e5c' }}>
          All contacts assessed — not just those in cadences
        </span>
      </div>
      <div style={{ padding: '10px 14px', display: 'flex', flexDirection: 'column', gap: 6 }}>

        {/* Legend */}
        <div style={{
          display: 'flex', gap: 10, flexWrap: 'wrap',
          padding: '6px 10px', background: '#faf9f8',
          border: '1px solid #e1dfdd', borderRadius: 2,
          marginBottom: 4
        }}>
          {Object.entries(ROLE_CONFIG).map(([key, cfg]) => (
            <span key={key} style={{ fontSize: 10, color: cfg.color, display: 'flex', alignItems: 'center', gap: 3 }}>
              <span style={{ fontWeight: 700 }}>{cfg.icon}</span> {cfg.label}
            </span>
          ))}
        </div>

        {/* Contact rows */}
        {sorted.map((contact, i) => {
          const roleCfg = ROLE_CONFIG[contact.planRole] || ROLE_CONFIG['low-priority'];
          return (
            <div key={i} style={{
              display: 'flex', gap: 10, alignItems: 'flex-start',
              padding: '10px 12px',
              background: '#fff',
              border: `1px solid ${contact.planRole === 'approval-risk' ? '#f4b8bb' : '#e1dfdd'}`,
              borderLeft: `3px solid ${roleCfg.color}`,
              borderRadius: 2
            }}>
              {/* Left: identity */}
              <div style={{ flex: '0 0 220px', display: 'flex', flexDirection: 'column', gap: 4 }}>
                <div style={{ fontSize: 12, fontWeight: 600, color: '#201f1e' }}>
                  {contact.name}
                </div>
                <div style={{ fontSize: 11, color: '#605e5c', lineHeight: 1.3 }}>
                  {contact.title}
                </div>
                <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap', marginTop: 2 }}>
                  <RoleBadge role={contact.planRole} />
                </div>
              </div>

              {/* Middle: engagement */}
              <div style={{ flex: '0 0 140px', display: 'flex', flexDirection: 'column', gap: 4 }}>
                <EngagementDot level={contact.engagementLevel} />
                <div style={{ fontSize: 11, color: '#605e5c' }}>
                  Last: {contact.lastActivity || 'No activity'}
                </div>
              </div>

              {/* Cadence indicator */}
              <div style={{ flex: '0 0 80px', display: 'flex', alignItems: 'flex-start' }}>
                {contact.hasCadence ? (
                  <span style={{
                    fontSize: 10, fontWeight: 600, color: '#107c10',
                    background: '#dff6dd', padding: '2px 7px',
                    borderRadius: 2, border: '1px solid #92d050'
                  }}>
                    ✓ In plan
                  </span>
                ) : (
                  <span style={{
                    fontSize: 10, fontWeight: 600, color: '#605e5c',
                    background: '#f3f2f1', padding: '2px 7px',
                    borderRadius: 2, border: '1px solid #e1dfdd'
                  }}>
                    No cadence
                  </span>
                )}
              </div>

              {/* Right: strategic note */}
              <div style={{
                flex: 1,
                fontSize: 11, color: '#323130', lineHeight: 1.5,
                fontStyle: 'italic',
                padding: '2px 0'
              }}>
                {contact.strategicNote}
              </div>
            </div>
          );
        })}

        {/* Coverage gap warning */}
        {(() => {
          const atRisk = sorted.filter(c =>
            c.planRole === 'approval-risk' && !c.hasCadence
          );
          if (atRisk.length === 0) return null;
          return (
            <div style={{
              background: '#fde7e9', border: '1px solid #f4b8bb',
              borderRadius: 2, padding: '8px 12px',
              fontSize: 11, color: '#a4262c',
              display: 'flex', gap: 6, alignItems: 'flex-start'
            }}>
              <span style={{ fontSize: 13, flexShrink: 0 }}>⚠</span>
              <span>
                <strong>Coverage gap:</strong> {atRisk.map(c => c.name).join(', ')} {atRisk.length === 1 ? 'is' : 'are'} flagged as approval risk but {atRisk.length === 1 ? 'has' : 'have'} no cadence. Consider asking the copilot to add a re-engagement cadence.
              </span>
            </div>
          );
        })()}
      </div>
    </div>
  );
}