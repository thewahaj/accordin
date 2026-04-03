# AccordIn — Brief 3: External Signals + Bidirectional Linking

## Step 0 — Commit first

```bash
git add -A
git commit -m "checkpoint: before external signals and bidirectional linking"
git push origin main
```

---

## Overview

This brief covers two things:

1. Populate the wrl_businesssignal table with realistic external signals for the Apple
   Global Logistics demo account — a mix of internal CRM signals and external risk/news
   signals. These power both the health-check scenario and the demo narrative about
   external data sources.

2. Add bidirectional linking to the hub — clicking a watchout or signal on the left panel
   highlights the related plan items on the right panel, and vice versa (the existing
   insightRef already handles right-to-left; this adds left-to-right).

The Dataverse MCP is available. Use it to create the signal records.

---

## Part A — Create demo signals via Dataverse MCP

Use the Dataverse MCP to find the Apple Global Logistics account record.
Query accounts where name = "Apple Global Logistics". Get the accountid GUID.

Then create the following wrl_businesssignal records. All records link to that accountid
via wrl_account lookup.

### Signal 1 — External risk signal (post-approval, for health check demo)

```
wrl_signalsummary: "Lloyd's Market Intelligence Q1 2026 report flags elevated cargo theft risk on Asia-to-Middle East corridor, with incidents up 23% versus Q4 2025. Key route: Singapore to Dubai."
wrl_signalcategory: 3  (Risk)
wrl_sentimentstatus: 3  (Risk)
wrl_sourcesystem: 2  (check the actual option set value for "External" — use whatever value maps to external/third-party)
wrl_signaltimestamp: (today's date or a date after the plan was approved — use DateTime.UtcNow or equivalent)
wrl_recordcreatedon: (same as above)
wrl_Account@odata.bind: /accounts({accountid})
```

### Signal 2 — External news signal (post-approval)

```
wrl_signalsummary: "Reuters: DP World announces capacity expansion at Jebel Ali Port, increasing container throughput by 15% from Q3 2026. Positive for logistics partners with existing Jebel Ali relationships."
wrl_signalcategory: 2  (Expansion — check actual value)
wrl_sentimentstatus: 1  (Positive)
wrl_sourcesystem: 2  (External)
wrl_signaltimestamp: (post-approval date)
wrl_recordcreatedon: (same)
wrl_Account@odata.bind: /accounts({accountid})
```

### Signal 3 — Internal CRM signal (post-approval)

```
wrl_signalsummary: "Laura Chen forwarded the Supply Chain Visibility Platform brochure to three internal stakeholders including CFO office, indicating internal evaluation has progressed."
wrl_signalcategory: 4  (Engagement — check actual value)
wrl_sentimentstatus: 1  (Positive)
wrl_sourcesystem: 1  (CRM — check actual value)
wrl_signaltimestamp: (post-approval date)
wrl_recordcreatedon: (same)
wrl_Account@odata.bind: /accounts({accountid})
```

### Signal 4 — Risk signal (post-approval, competitor activity)

```
wrl_signalsummary: "Intel from field team: Maersk Sales contacted Sophia Martinez directly regarding freight forwarding services on the Asia-Middle East corridor. Competitor approach confirmed."
wrl_signalcategory: 3  (Risk)
wrl_sentimentstatus: 3  (Risk)
wrl_sourcesystem: 1  (CRM)
wrl_signaltimestamp: (post-approval date)
wrl_recordcreatedon: (same)
wrl_Account@odata.bind: /accounts({accountid})
```

**Note:** Before creating these records, query the wrl_signalcategory and wrl_sourcesystem
option set values in Dataverse to get the correct integer values for Risk, Expansion,
Engagement, CRM, and External. Do not guess — use the MCP to retrieve the metadata.

---

## Part B — Bidirectional signal linking in the hub

File: `power-platform/web-resources/accordin-hub/account-planning-hub-v3.html`

Read the full file before making any changes.

### Background

The existing `insightRef` system handles right-to-left linking:
- clicking ℹ on a plan card → scrolls left panel to the linked signal/watchout

This change adds left-to-right linking:
- clicking a signal or watchout on the left panel → highlights related plan cards on the right

### B1 — Add CSS for reverse highlight

Add these rules after the existing `.insight-highlight` animation:

```css
/* Bidirectional — plan card highlight when signal is clicked from left panel */
@keyframes cardPulse {
  0%, 100% { box-shadow: var(--xs); }
  30%       { box-shadow: 0 0 0 2px var(--blue), var(--sm); border-color: var(--blue-lt); }
  70%       { box-shadow: 0 0 0 2px var(--blue), var(--sm); border-color: var(--blue-lt); }
}
.card-highlight {
  animation: cardPulse 1.2s ease 2;
}
```

### B2 — Build reverse index when plan renders

Add this function before `renderPlan()`:

```javascript
// Maps insightRef values to the card element IDs that reference them
// Built once when the plan renders, used by left-panel click handlers
let insightToCards = {}; // e.g. { "watchout:0": ["pop-rec-0", "pop-cad-2"] }

function buildInsightIndex(plan) {
  insightToCards = {};

  function index(items, prefix) {
    (items || []).forEach((item, i) => {
      if (!item.insightRef) return;
      const ref = item.insightRef;
      if (!insightToCards[ref]) insightToCards[ref] = [];
      insightToCards[ref].push(`${prefix}-${i}`);
    });
  }

  index(plan.recommendations, 'rec');
  index(plan.cadences,        'cad');
  index(plan.oneOffActions,   'act');
}
```

Call `buildInsightIndex(plan)` at the start of `renderPlan()`, right after
`plan = enrichPlanWithInsightRefs(plan)`.

### B3 — Add click handler to left panel signals and watchouts

In `renderPlan()`, find where signals and watchouts are rendered with their IDs.

Update the signal rendering to add a click handler:

```javascript
document.getElementById('posSigs').innerHTML = (plan.positiveSignals || []).map((s, i) =>
  `<div class="sig-item" id="signal-${i}" onclick="highlightLinkedCards('signal:${i}')"
        style="cursor:pointer" title="Click to highlight linked plan items">
    <div class="sig-dot"></div>
    <div class="sig-text">${s}</div>
  </div>`
).join('');
```

Update the watchout rendering to add a click handler:

```javascript
document.getElementById('watchouts').innerHTML = (plan.watchouts || []).map((w, i) =>
  `<div class="wo-item" id="watchout-${i}" onclick="highlightLinkedCards('watchout:${i}')"
        style="cursor:pointer" title="Click to highlight linked plan items">${w}</div>`
).join('');
```

### B4 — Add highlightLinkedCards function

Add this function after `scrollToInsight()`:

```javascript
function highlightLinkedCards(insightRef) {
  // First highlight the source signal/watchout itself
  const sourceId = insightRef.startsWith('watchout:')
    ? `watchout-${insightRef.split(':')[1]}`
    : insightRef.startsWith('signal:')
      ? `signal-${insightRef.split(':')[1]}`
      : null;

  if (sourceId) {
    const sourceEl = document.getElementById(sourceId);
    if (sourceEl) {
      sourceEl.classList.remove('insight-highlight');
      void sourceEl.offsetWidth;
      sourceEl.classList.add('insight-highlight');
      setTimeout(() => sourceEl.classList.remove('insight-highlight'), 2000);
    }
  }

  // Find all plan cards linked to this insight
  const cardIds = insightToCards[insightRef] || [];
  if (cardIds.length === 0) return;

  // Scroll the middle panel to the first linked card
  const firstCardId = cardIds[0].replace('pop-', ''); // "rec-0", "cad-1" etc
  const firstCard = document.getElementById(firstCardId);
  if (firstCard) {
    firstCard.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }

  // Highlight all linked cards
  cardIds.forEach(popId => {
    const cardId = popId.replace('pop-', '');
    const card = document.getElementById(cardId);
    if (card) {
      card.classList.remove('card-highlight');
      void card.offsetWidth;
      card.classList.add('card-highlight');
      setTimeout(() => card.classList.remove('card-highlight'), 2500);
    }
  });
}
```

### B5 — Add subtle visual hint that signals are clickable

In the CSS, make the signal and watchout items show a hover state to indicate they are
interactive. Add after the existing `.sig-item` and `.wo-item` rules:

```css
.sig-item:hover .sig-text { color: var(--blue); }
.sig-item:hover .sig-dot  { background: var(--blue); }
.wo-item:hover { border-left-color: #991b1b; background: #fee2e2; cursor: pointer; }
```

---

## Verification checklist

1. In D365, the wrl_businesssignal table has 4 new records linked to Apple Global Logistics
   with dates after the plan approval timestamp
2. Open the approved Apple Global Logistics plan and type "anything new since approval?" —
   the copilot returns a summary of the 4 new signals including the two risk signals, and
   recommends reviewing the plan
3. In the hub, click a watchout on the left panel — the related plan cards on the right
   briefly pulse with a blue ring
4. Click a positive signal on the left panel — any linked cards pulse
5. Signals and watchouts show a hover state (cursor changes) to indicate they are clickable
6. The existing right-to-left behaviour still works — clicking ℹ on a plan card still
   scrolls and highlights the left panel element

---

## Final commit

```bash
git add -A
git commit -m "feat: external demo signals, bidirectional signal-to-card linking"
git push origin main
```
