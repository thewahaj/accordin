# AccordIn Hub — Loading Icons and Reasoning Trace Fix

## Step 0 — Commit current state first

```bash
git add -A
git commit -m "checkpoint: before loading icons and reasoning trace fix"
git push origin main
```

Do not proceed until the push succeeds.

---

## File to change

`power-platform/web-resources/accordin-hub/account-planning-hub-v3.html`

Read the full file before making any changes.

---

## Change 1 — Replace spinner with icon-driven loading experience

### 1a — Add CSS for the icon loading state

Find the `.spin` CSS rule. Add these new rules directly after it:

```css
/* Icon-driven generation loading */
.gen-stage {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  height: 100%;
  gap: 20px;
  padding: 40px;
}
.gen-icon-wrap {
  width: 64px;
  height: 64px;
  border-radius: 16px;
  background: var(--navy);
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 28px;
  transition: all .4s ease;
  box-shadow: 0 4px 20px rgba(14,30,53,.2);
}
.gen-icon-wrap.pulse-in {
  animation: iconPulse .4s ease;
}
@keyframes iconPulse {
  0%   { transform: scale(.85); opacity: .6; }
  60%  { transform: scale(1.06); opacity: 1; }
  100% { transform: scale(1);   opacity: 1; }
}
.gen-message {
  font-size: 13px;
  color: var(--text-mid);
  text-align: center;
  max-width: 300px;
  line-height: 1.65;
  font-weight: 500;
  letter-spacing: -.01em;
  min-height: 44px;
  transition: opacity .25s ease;
}
.gen-sub {
  font-size: 11px;
  color: var(--slate-lt);
  text-align: center;
  max-width: 240px;
  letter-spacing: .01em;
}
.gen-dots {
  display: flex;
  gap: 5px;
  margin-top: 4px;
}
.gen-dot {
  width: 5px;
  height: 5px;
  border-radius: 50%;
  background: var(--navy-border, #263f60);
  animation: dotPulse 1.4s ease infinite;
}
.gen-dot:nth-child(2) { animation-delay: .2s; }
.gen-dot:nth-child(3) { animation-delay: .4s; }
@keyframes dotPulse {
  0%, 80%, 100% { transform: scale(.7); opacity: .4; }
  40%            { transform: scale(1);  opacity: 1;  }
}
```

### 1b — Replace startGenerationMessages()

Find the existing `startGenerationMessages()` function and replace it entirely with this version:

```javascript
function startGenerationMessages() {
  const stages = [
    { icon: '📋', text: 'Reading account history and open pipeline...' },
    { icon: '👥', text: 'Checking contact engagement across the last 90 days...' },
    { icon: '📡', text: 'Identifying signals and commercial triggers...' },
    { icon: '📊', text: 'Sizing the pipeline by stage confidence...' },
    { icon: '🔗', text: 'Mapping approval dependencies across the buying group...' },
    { icon: '🔍', text: 'Looking for whitespace beyond current opportunities...' },
    { icon: '🧭', text: 'Weighing the primary relationship thread...' },
    { icon: '⚖️',  text: 'Assessing execution risk against pipeline stage...' },
    { icon: '💷', text: 'Building the revenue case...' },
    { icon: '📝', text: 'Structuring the plan...' },
    { icon: '✓',  text: 'Finalising cadence priorities...' },
    { icon: '◈',  text: 'Almost there...' }
  ];

  let idx = 0;
  const iconEl = document.getElementById('genIcon');
  const msgEl  = document.getElementById('genMessage');
  if (!iconEl || !msgEl) return null;

  // Set initial state
  iconEl.textContent = stages[0].icon;
  msgEl.textContent  = stages[0].text;

  const interval = setInterval(() => {
    idx++;
    if (idx >= stages.length) {
      idx = stages.length - 1; // stay on last stage
      clearInterval(interval);
    }

    // Fade message out, swap, fade back in
    if (msgEl) {
      msgEl.style.opacity = '0';
      setTimeout(() => {
        if (msgEl) msgEl.textContent = stages[idx].text;
        if (msgEl) msgEl.style.opacity = '1';
      }, 200);
    }

    // Pulse the icon and swap
    if (iconEl) {
      iconEl.classList.remove('pulse-in');
      void iconEl.offsetWidth; // force reflow to restart animation
      iconEl.classList.add('pulse-in');
      setTimeout(() => {
        if (iconEl) iconEl.textContent = stages[idx].icon;
      }, 100);
    }
  }, 2800);

  return interval;
}
```

### 1c — Replace the loading innerHTML in generatePlan()

Find where `pMid` is set to the loading state inside `generatePlan()`. Replace it with this:

```javascript
document.getElementById('pMid').innerHTML = `
  <div class="gen-stage">
    <div class="gen-icon-wrap" id="genIcon">📋</div>
    <div class="gen-message" id="genMessage"></div>
    <div class="gen-sub">Calling the AccordIn planning copilot</div>
    <div class="gen-dots">
      <div class="gen-dot"></div>
      <div class="gen-dot"></div>
      <div class="gen-dot"></div>
    </div>
  </div>`;
let msgInterval = startGenerationMessages();
```

Declare `msgInterval` with `let` before the try/catch block so it is in scope for both success and error paths. Call `if (msgInterval) clearInterval(msgInterval)` in both the success path (before renderPlan) and the catch block.

---

## Change 2 — Fix reasoning trace not appearing

The reasoning trace should appear as the copilot's first message when a plan finishes loading. It is likely not appearing because `setCopilotState('ready', accountName, plan)` is being called without the `plan` argument, or `buildReasoningTrace` was not added.

### 2a — Verify buildReasoningTrace exists

Search the file for `function buildReasoningTrace`. If it does not exist, add it before `setCopilotState()`:

```javascript
function buildReasoningTrace(plan, accountName) {
  const contacts  = plan.contactEngagement || [];
  const recs      = plan.recommendations   || [];
  const pipeline  = plan.revenuePicture?.pipelineValue || 0;

  const approvalRisk = contacts.find(c =>
    c.planRole === 'approval-risk' && !c.hasCadence
  );
  const primaryRel = contacts.find(c =>
    c.planRole === 'primary-relationship'
  );
  const topRec = recs[0];

  function fmtVal(n) {
    if (!n || n === 0) return '';
    if (n >= 1000000) return ' at £' + (n / 1000000).toFixed(1) + 'M';
    if (n >= 1000)    return ' at £' + Math.round(n / 1000) + 'K';
    return ' at £' + n;
  }
  function shortName(str) {
    return (str || '').split('–')[0].split('—')[0].trim().substring(0, 45);
  }

  // Case 1 — approval risk contact with no cadence and real pipeline
  if (approvalRisk && pipeline > 0) {
    const first   = approvalRisk.name.split(' ')[0];
    const role    = approvalRisk.title.split('–')[0].split('-')[0].trim();
    const recName = topRec?.productName
      ? shortName(topRec.productName)
      : 'the top opportunity';
    return `I prioritised re-engaging ${first} (${role}) before pushing on ${recName}. ` +
           `${first} controls budget approval across this account — without alignment there, ` +
           `even a Negotiation-stage deal can stall at signature. ` +
           `That dependency is the single highest-leverage point in this plan.`;
  }

  // Case 2 — primary relationship contact disengaged or no activity
  if (primaryRel && (
    primaryRel.lastActivity === 'No activity recorded' ||
    primaryRel.engagementLevel === 'Low'
  )) {
    const first   = primaryRel.name.split(' ')[0];
    const recName = topRec?.productName
      ? shortName(topRec.productName)
      : 'the primary opportunity';
    return `${first} is the strategic anchor on this account but engagement has gone quiet. ` +
           `I have kept ${recName} as the lead recommendation, but the cadence plan is built ` +
           `around re-establishing that relationship first. ` +
           `Commercial momentum without executive alignment rarely closes.`;
  }

  // Case 3 — strong pipeline, no major gaps
  if (topRec && primaryRel) {
    const recName = topRec.productName
      ? shortName(topRec.productName)
      : 'the top opportunity';
    const val   = fmtVal(topRec.estimatedValue);
    const first = primaryRel.name.split(' ')[0];
    return `The data points toward ${recName}${val} as the primary commercial thread. ` +
           `${first} is the right relationship anchor — I have put the strategic cadence with them first. ` +
           `The plan is structured to move pipeline forward without losing the executive thread.`;
  }

  // Fallback
  return `This plan is built on limited data for ${accountName || 'this account'}. ` +
         `I have applied industry best practices where account-specific signals were not available. ` +
         `Treat the recommendations as a discovery framework and validate through direct engagement.`;
}
```

### 2b — Fix setCopilotState() ready branch

Find `setCopilotState()`. In the `'ready'` branch, ensure it calls `buildReasoningTrace` and that the function signature accepts the `plan` parameter. The ready branch should look exactly like this:

```javascript
} else if (state === 'ready' && plan) {
  if (chatHistory.length === 0) {
    const trace = buildReasoningTrace(plan, accountName);
    chatHistory.push({ role: 'ai', text: trace, time: 'Just now' });
  }
  if (status) status.classList.remove('pulse');
  if (sub) sub.textContent = 'Refine this plan';
}
renderChatHistory();
```

### 2c — Fix all call sites of setCopilotState('ready')

Search the file for every call to `setCopilotState('ready'`. There will be at least two — one in `renderPlan()` and one possibly elsewhere.

Every call must pass all three arguments:

```javascript
// Correct — all three arguments
setCopilotState('ready', acc?.name || currentAccount?.name, plan);
```

If any call is missing the `plan` argument, the `state === 'ready' && plan` condition will be false and the reasoning trace will never appear. Fix every call site.

### 2d — Verify setCopilotState function signature

The function declaration must accept three parameters:

```javascript
function setCopilotState(state, accountName, plan) {
```

If it only has two parameters (`state, accountName`), add `plan` as the third.

---

## Verification checklist

1. Click Generate Plan on a mock account — the loading screen shows the icon (no spinner ring), the icon pulses and changes every 2.8 seconds, the message fades out and fades back in with each new stage
2. The three animated dots pulse below the message
3. When the plan finishes loading, the copilot panel shows the reasoning trace as the first message — it should name a specific contact and state a specific dependency or priority decision
4. The reasoning trace is in the dark copilot panel on the right, not in the Strategic Assessment section on the left
5. In mock mode, open Apple Global Logistics — the reasoning trace should reference Sophia Martinez and her approval risk since she has no cadence
6. The loading screen ends cleanly when the plan renders — no leftover spinner or loading state visible

---

## Final commit

```bash
git add -A
git commit -m "feat: icon-driven generation loading, reasoning trace fix"
git push origin main
```
