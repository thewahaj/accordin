# AccordIn Hub — Copilot Experience Enhancement

## Step 0 — Commit and push current state first

Before making any code changes, run these commands in the terminal:

```bash
git add -A
git commit -m "feat: plan generation and refinement working in happy path - pre-experience-enhancement checkpoint"
git push origin main
```

Confirm the push succeeds before touching any files. If the push fails due to upstream changes, run `git pull --rebase origin main` first, then push again. Do not proceed with code changes until the commit is on the remote.

---

## File to change

`power-platform/web-resources/accordin-hub/account-planning-hub-v3.html`

Read the full file before making any changes.

---

## Change 1 — Generation loading messages

Find the `generatePlan()` function. Inside it there is a loading state that sets `pMid` innerHTML to a spinner with static text like "Collecting account data from Dataverse...".

Add this function **before** `generatePlan()`:

```javascript
function startGenerationMessages() {
  const messages = [
    "Reading account history and open pipeline...",
    "Checking contact engagement across the last 90 days...",
    "Identifying signals and commercial triggers...",
    "Sizing the pipeline by stage confidence...",
    "Mapping approval dependencies across the buying group...",
    "Looking for whitespace beyond current opportunities...",
    "Weighing the primary relationship thread...",
    "Assessing execution risk against pipeline stage...",
    "Building the revenue case...",
    "Structuring the plan...",
    "Finalising cadence priorities...",
    "Almost there..."
  ];

  let idx = 0;
  const el = document.getElementById('genMessage');
  if (!el) return null;

  el.textContent = messages[0];
  const interval = setInterval(() => {
    idx = (idx + 1) % messages.length;
    if (messages[idx] === "Almost there...") {
      clearInterval(interval);
    }
    if (el) el.textContent = messages[idx];
  }, 2800);

  return interval;
}
```

Replace the loading innerHTML inside `generatePlan()` with this — note the `id="genMessage"` span:

```javascript
document.getElementById('pMid').innerHTML = `
  <div style="display:flex;flex-direction:column;align-items:center;justify-content:center;height:100%;gap:18px;padding:40px;">
    <div class="spin"></div>
    <div id="genMessage" style="font-size:13px;color:var(--text-mid);text-align:center;max-width:280px;line-height:1.6;"></div>
    <div style="font-size:11px;color:var(--slate-lt);text-align:center;max-width:240px">Calling the AccordIn planning copilot</div>
  </div>`;
let msgInterval = startGenerationMessages();
```

Declare `msgInterval` with `let` before the try block so it is accessible in both the success and catch paths. After the plan renders (success path) and in the catch block (error path), clear it:

```javascript
if (msgInterval) clearInterval(msgInterval);
```

---

## Change 2 — Reasoning trace in copilot opening message

Add this function **before** `setCopilotState()`:

```javascript
function buildReasoningTrace(plan, accountName) {
  const contacts = plan.contactEngagement || [];
  const recs     = plan.recommendations   || [];
  const watchouts = plan.watchouts        || [];

  const approvalRisk = contacts.find(c =>
    c.planRole === 'approval-risk' && !c.hasCadence
  );
  const primaryRel = contacts.find(c =>
    c.planRole === 'primary-relationship'
  );
  const topRec = recs[0];
  const pipeline = plan.revenuePicture?.pipelineValue || 0;

  function fmtVal(n) {
    if (!n || n === 0) return '';
    if (n >= 1000000) return ' at £' + (n / 1000000).toFixed(1) + 'M';
    if (n >= 1000)    return ' at £' + Math.round(n / 1000) + 'K';
    return ' at £' + n;
  }

  function shortName(str) {
    return (str || '').split('–')[0].split('—')[0].trim().substring(0, 40);
  }

  // Case 1 — approval risk contact with no cadence and real pipeline
  if (approvalRisk && pipeline > 0) {
    const first   = approvalRisk.name.split(' ')[0];
    const role    = approvalRisk.title.split('–')[0].split('-')[0].trim();
    const recName = topRec?.productName ? shortName(topRec.productName) : 'the top opportunity';
    return `I prioritised re-engaging ${first} (${role}) before pushing on ${recName}. ` +
           `${first} controls budget approval across this account — without alignment there, ` +
           `even a Negotiation-stage deal can stall at signature. ` +
           `That dependency is the single highest-leverage point in this plan.`;
  }

  // Case 2 — primary relationship contact disengaged
  if (primaryRel && (
    primaryRel.lastActivity === 'No activity recorded' ||
    primaryRel.engagementLevel === 'Low'
  )) {
    const first   = primaryRel.name.split(' ')[0];
    const recName = topRec?.productName ? shortName(topRec.productName) : 'the primary opportunity';
    return `${first} is the strategic anchor on this account but engagement has gone quiet. ` +
           `I have kept ${recName} as the lead recommendation, but the cadence plan is built ` +
           `around re-establishing that relationship first. ` +
           `Commercial momentum without executive alignment rarely closes.`;
  }

  // Case 3 — strong pipeline, no major gaps
  if (topRec && primaryRel) {
    const recName = topRec.productName ? shortName(topRec.productName) : 'the top opportunity';
    const val     = fmtVal(topRec.estimatedValue);
    const first   = primaryRel.name.split(' ')[0];
    return `The data points toward ${recName}${val} as the primary commercial thread. ` +
           `${first} is the right relationship anchor — I have put the strategic cadence with them first. ` +
           `The plan is structured to move pipeline forward without losing the executive thread.`;
  }

  // Fallback — sparse data
  return `This plan is built on limited data for ${accountName || 'this account'}. ` +
         `I have applied industry best practices where account-specific signals were not available. ` +
         `Treat the recommendations as a discovery framework and validate through direct engagement.`;
}
```

In `setCopilotState()`, find the `'ready'` branch. Replace the current message construction block with a call to `buildReasoningTrace()`:

```javascript
} else if (state === 'ready' && plan) {
  if (chatHistory.length === 0) {
    const trace = buildReasoningTrace(plan, accountName);
    chatHistory.push({ role: 'ai', text: trace, time: 'Just now' });
  }
  if (status) status.classList.remove('pulse');
  if (sub) sub.textContent = 'Refine this plan';
}
```

---

## Change 3 — Staggered section reveal on plan load

Add these CSS rules in the `<style>` block, after the existing `.p-sec` rule:

```css
/* Staggered reveal animation */
@keyframes sectionReveal {
  from { opacity:0; transform:translateY(8px); }
  to   { opacity:1; transform:translateY(0); }
}
.p-sec { opacity:0; }
.p-sec.reveal { animation:sectionReveal .35s ease forwards; }
.p-sec.revealed { opacity:1; }

@keyframes cardReveal {
  from { opacity:0; transform:translateY(6px); }
  to   { opacity:1; transform:translateY(0); }
}
.rec, .cad, .act { opacity:0; }
.rec.reveal, .cad.reveal, .act.reveal { animation:cardReveal .3s ease forwards; }
.rec.revealed, .cad.revealed, .act.revealed { opacity:1; }
```

Add this function **before** `renderPlan()`:

```javascript
function triggerStaggeredReveal() {
  // Reveal left panel sections sequentially
  const sections = document.querySelectorAll('#pLeft .p-sec');
  sections.forEach((sec, i) => {
    setTimeout(() => {
      sec.classList.add('reveal');
      setTimeout(() => sec.classList.add('revealed'), 350);
    }, i * 120);
  });

  // Reveal plan cards after left panel completes
  const leftDelay = sections.length * 120 + 100;
  const cards = document.querySelectorAll('#pMid .rec, #pMid .cad, #pMid .act');
  cards.forEach((card, i) => {
    setTimeout(() => {
      card.classList.add('reveal');
      setTimeout(() => card.classList.add('revealed'), 300);
    }, leftDelay + i * 80);
  });
}
```

Call `triggerStaggeredReveal()` at the end of `renderPlan()`, after `renderPlanTab(plan)` and after `setCopilotState('ready', ...)`:

```javascript
setTimeout(triggerStaggeredReveal, 50);
```

Also call it in `switchMidTab()` after `renderPlanTab(currentPlan)`:

```javascript
if (tab === 'plan') {
  renderPlanTab(currentPlan);
  setTimeout(triggerStaggeredReveal, 50);
}
```

**Important constraint:** `triggerStaggeredReveal()` must degrade gracefully. If called when no `.p-sec` or card elements are present (create mode, history view), it does nothing. The `querySelectorAll` calls already handle this safely — an empty NodeList forEach is a no-op.

---

## Change 4 — Fix ℹ button popovers (position:relative)

Find these three CSS rules and add `position:relative` to each. They currently have no positioning set, which causes the absolute-positioned popover to render off-screen.

```css
.rec { position:relative; border:1px solid var(--border); ... }
.cad { position:relative; border:1px solid var(--border); ... }
.act { position:relative; border:1px solid var(--border); ... }
```

Only add `position:relative` — do not change anything else in these rules.

---

## Verification checklist

Open the file in a browser and verify:

1. Click a mock account and click Generate Plan — loading messages cycle through the strategic phrases every 2.8 seconds and stop on "Almost there..."
2. When the plan loads, left panel sections fade in one by one from top to bottom
3. Plan cards (recommendations, cadences, actions) fade in sequentially after the left panel completes
4. The copilot opening message is the reasoning trace — specific to the plan data, not the generic "Plan loaded" message
5. Clicking the ℹ button on any recommendation, cadence, or action shows the popover correctly positioned in the top-right corner of the card
6. Clicking "See linked insight" scrolls the left panel and highlights the correct element
7. Switching to the Contacts tab and back to Plan tab re-triggers the card reveal animation
8. Create mode and History view are unaffected — no broken animations or invisible content

---

## Final commit

```bash
git add -A
git commit -m "feat: copilot experience - reasoning trace, generation messages, staggered reveal, popover fix"
git push origin main
```
