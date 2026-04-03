# AccordIn Hub — Copilot Open State and Reasoning Trace Fix

## Step 0 — Commit first

```bash
git add -A
git commit -m "checkpoint: before copilot open state and reasoning trace fix"
git push origin main
```

---

## File to change

`power-platform/web-resources/accordin-hub/account-planning-hub-v3.html`

Read the full file before making any changes.

---

## Background

Two related bugs:

1. The copilot panel stays collapsed after a plan loads. The reasoning trace message is being
   added to chatHistory correctly but the user cannot see it because the panel is collapsed.

2. The generating state message ("Analysing account data from Dataverse...") survives into
   the loaded plan view because the preserveChat flag keeps it. This means the reasoning
   trace never gets added — setCopilotState('ready') is skipped when chatHistory is non-empty.
   The generating message should be cleared on successful plan load, not preserved.

Only refinement conversation messages should survive a re-render. Generation state messages
should always be cleared when the plan loads.

---

## Change 1 — Clear generation messages on plan load

### 1a — Mark generation messages so they can be identified

Find `setCopilotState()`. In the `'generating'` branch, add a `generated: true` flag to the
message that gets pushed to chatHistory:

```javascript
} else if (state === 'generating') {
  chatHistory = [];
  chatHistory.push({
    role: 'ai',
    text: 'Analysing account data from Dataverse — reading opportunities, contacts, signals and activities. This takes 15–30 seconds.',
    time: 'Just now',
    generated: true   // ← add this flag
  });
  if (status) status.classList.add('pulse');
  if (sub) sub.textContent = 'Generating plan...';
  document.getElementById('qPrompts').innerHTML = '';
}
```

### 1b — Strip generation messages before the ready state check

In `setCopilotState()`, in the `'ready'` branch, before checking `chatHistory.length === 0`,
filter out any messages that were marked as `generated: true`:

```javascript
} else if (state === 'ready' && plan) {
  // Remove generation-phase messages — they should not persist into the plan view
  chatHistory = chatHistory.filter(m => !m.generated);

  if (chatHistory.length === 0) {
    const trace = buildReasoningTrace(plan, accountName);
    chatHistory.push({ role: 'ai', text: trace, time: 'Just now' });
  }
  if (status) status.classList.remove('pulse');
  if (sub) sub.textContent = 'Refine this plan';
}
renderChatHistory();
```

This means: on first plan load, generation messages are cleared and the reasoning trace is
pushed. On re-render after a refinement (preserveChat path), generation messages are also
cleared but the user's actual conversation survives because those messages do not have
`generated: true`.

---

## Change 2 — Force copilot open when plan loads

### 2a — Find where copilotOpen is set in renderPlan()

In `renderPlan()`, find this section near the end:

```javascript
copilotOpen = true; applyCollapseState();
```

Replace it with a version that forces the panel open regardless of screen width or previous
collapse state:

```javascript
// Always open the copilot when a plan loads so the reasoning trace is visible
copilotOpen = true;
const copilotEl = document.getElementById('copilotPanel');
const collapseBtn = document.getElementById('collapseBtn');
if (copilotEl) {
  copilotEl.classList.remove('collapsed');
  copilotEl.classList.add('user-open');
}
if (collapseBtn) collapseBtn.textContent = '›';
```

### 2b — Fix applyCollapseState() to respect user-open class

Find `applyCollapseState()`. It currently applies the responsive breakpoint logic which can
override `copilotOpen = true`. Update it so that when `copilotOpen` is true, the panel is
always opened regardless of screen width:

```javascript
function applyCollapseState() {
  const panel = document.getElementById('copilotPanel');
  const btn   = document.getElementById('collapseBtn');
  if (!panel) return;

  if (copilotOpen) {
    panel.classList.remove('collapsed');
    panel.classList.add('user-open');
    if (btn) btn.textContent = '›';
  } else {
    panel.classList.add('collapsed');
    panel.classList.remove('user-open');
    if (btn) btn.textContent = '‹';
  }
}
```

Remove any breakpoint-based auto-collapse logic from inside `applyCollapseState()` if it
exists. The only place auto-collapse should happen is in `init()` for the initial page load
before any plan has been opened.

---

## Change 3 — Preserve only conversation messages across re-renders

Find `renderPlan()`. It has a `preserveChat` option that is passed when re-rendering after
a refinement. The current logic skips `setCopilotState('ready')` entirely when
`preserveChat && chatHistory.length`. This needs to still call `setCopilotState('ready')`
so that generation messages are cleaned up, but the reasoning trace should only be added
if there are no non-generated messages.

Replace the copilot section at the end of `renderPlan()`:

```javascript
// Open copilot
copilotOpen = true;
const copilotEl = document.getElementById('copilotPanel');
const collapseBtn = document.getElementById('collapseBtn');
if (copilotEl) {
  copilotEl.classList.remove('collapsed');
  copilotEl.classList.add('user-open');
}
if (collapseBtn) collapseBtn.textContent = '›';

// Always call setCopilotState('ready') — it handles both first load and re-render correctly
// On first load: clears generation messages, adds reasoning trace
// On re-render after refinement: clears generation messages, skips reasoning trace (chatHistory has real messages)
setCopilotState('ready', acc?.name || currentAccount?.name, plan);
generateQuickPrompts(plan);
setTimeout(triggerStaggeredReveal, 50);
```

Remove the old `if (preserveChat && chatHistory.length) { ... } else { setCopilotState(...) }`
block entirely. The `setCopilotState('ready')` call now handles both cases correctly because
it filters `generated` messages and only adds the reasoning trace when no real conversation
exists.

---

## Verification checklist

1. Open the hub and click Generate Plan on any mock account
2. While generating, the copilot shows the generating message
3. When the plan loads, the copilot panel opens automatically (not collapsed)
4. The generating message is gone — the copilot shows the reasoning trace as the first message
5. The reasoning trace is specific — it names a contact (Sophia Martinez for Apple Global
   Logistics) and states a dependency, not a generic "Plan loaded" message
6. Type a message in the copilot input — the conversation history shows your message and
   the AI response
7. If the plan re-renders (after a refinement), the conversation history is preserved —
   the reasoning trace does not reappear, only the actual messages remain
8. Closing the panel and opening a different account starts a fresh conversation with that
   account's reasoning trace

---

## Final commit

```bash
git add -A
git commit -m "fix: copilot opens on plan load, reasoning trace shows correctly, generation messages cleared"
git push origin main
```
