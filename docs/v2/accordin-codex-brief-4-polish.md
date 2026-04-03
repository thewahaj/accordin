# AccordIn — Brief 4: Demo Polish and Minor Fixes

## Step 0 — Commit first

```bash
git add -A
git commit -m "checkpoint: before demo polish"
git push origin main
```

---

## Overview

Final polish pass before demo and documentation. Small targeted fixes only.
No new features. No architecture changes.

---

## Fix 1 — productName truncation in reasoning trace

File: `power-platform/web-resources/accordin-hub/account-planning-hub-v3.html`

Find `buildReasoningTrace()`. The `shortName()` helper truncates at 45 characters causing
names like "Supply Chain Visibility Platform - Global Container Tracking" to appear as
"Supply Chain Visibility Platform - Global Con". Change the limit to 60:

```javascript
function shortName(str) {
  return (str || '').split('–')[0].split('—')[0].trim().substring(0, 60);
}
```

---

## Fix 2 — Recommendation type display capitalisation

File: `power-platform/web-resources/accordin-hub/account-planning-hub-v3.html`

Currently recommendation cards show the type in lowercase (e.g. "relationship",
"retention"). Find where `r.type` is rendered in the recommendation card template and
wrap it in a capitalise helper:

Add this utility function near the other utility functions:

```javascript
function capitalise(str) {
  if (!str) return '';
  return str.charAt(0).toUpperCase() + str.slice(1);
}
```

Then in the recommendation card template, change:

```javascript
<div class="rec-type">${r.type}</div>
```

to:

```javascript
<div class="rec-type">${capitalise(r.type)}</div>
```

---

## Fix 3 — Recommendation estimated value zero display

Currently recommendations with estimatedValue of 0 show "£0" which looks odd for
relationship and retention type recommendations where value is not applicable.

In the recommendation card template, change the rec-val line:

```javascript
// Before
<div class="rec-val">${fmt(r.estimatedValue)}</div>

// After
${r.estimatedValue > 0
  ? `<div class="rec-val">${fmt(r.estimatedValue)}</div>`
  : `<div class="rec-val" style="color:var(--text-muted);font-size:13px;font-weight:500">Value TBD</div>`}
```

---

## Fix 4 — Contact strip shows only 2 contacts when 3 are expected

File: `power-platform/web-resources/accordin-hub/account-planning-hub-v3.html`

In `renderContactStrip()`, the third contact (most recently active, not primary or risk)
may not be found if the most-recently-active contact IS the primary or risk contact.

Find the logic that builds the `featured` array. The current logic uses `.find()` which
stops at the first match. Update to ensure the third slot always finds a contact that
is not already in the featured array:

```javascript
const primary = contacts.find(c => c.planRole === 'primary-relationship');
const risk    = contacts.find(c => c.planRole === 'approval-risk');

// Third: most recently active contact not already featured
const featuredNames = new Set([primary?.name, risk?.name].filter(Boolean));
const recent = [...contacts]
  .filter(c => !featuredNames.has(c.name))
  .sort((a, b) => (b.lastActivity || '').localeCompare(a.lastActivity || ''))
  [0];

const featured = [];
if (primary) featured.push({ ...primary, stripRole: 'primary' });
if (risk)    featured.push({ ...risk,    stripRole: 'risk'    });
if (recent && !featuredNames.has(recent.name))
             featured.push({ ...recent,  stripRole: 'active'  });
```

---

## Fix 5 — Copilot sub-heading during generation

When the plan is generating, the copilot sub-heading should say "Analysing account..."
not "Generating plan..." since the user can see the generation messages in the middle panel.

In `setCopilotState()` in the `'generating'` branch, change:

```javascript
if (sub) sub.textContent = 'Analysing account...';
```

---

## Fix 6 — Quick prompt buttons wrap on narrow screens

The quick prompt buttons at the bottom of the copilot panel currently wrap to multiple
lines on narrower screens making the area look messy.

In the CSS, find `.q-btn` and add `flex-shrink:0`. Also find `.q-prompts` and change it
to allow horizontal scrolling rather than wrapping:

```css
.q-prompts {
  padding: 8px 12px;
  border-top: 1px solid var(--navy-bd);
  display: flex;
  gap: 5px;
  flex-wrap: nowrap;       /* change from wrap to nowrap */
  overflow-x: auto;
  flex-shrink: 0;
}
.q-prompts::-webkit-scrollbar { height: 2px; }
.q-prompts::-webkit-scrollbar-thumb { background: var(--navy-bd); border-radius: 1px; }
```

---

## Fix 7 — Panel header button wrapping on narrow screens

On screens below about 1100px the panel header buttons ("Export", "Approve") wrap to a
second line because the header is a flex row with no overflow handling.

In the CSS, find `.p-hdr-right` and ensure the buttons never wrap:

```css
.p-hdr-right {
  margin-left: auto;
  display: flex;
  gap: 6px;
  align-items: center;
  flex-shrink: 0;
  white-space: nowrap;
}
```

Also ensure `.btn` has `white-space:nowrap` — verify this is already set. If not, add it.

---

## Fix 8 — Data limitations always collapsed on load

This is already implemented but verify it is consistent. On plan load, data limitations
should start collapsed with "show" toggle. After clicking show, they expand. After
re-rendering (refinement), they should return to collapsed state.

In `renderPlan()`, verify this line exists after the dataLims innerHTML is set:

```javascript
document.getElementById('dataLims').classList.add('lim-collapsed');
```

If it is missing, add it.

---

## Verification checklist

1. Recommendation type shows "Relationship" not "relationship", "Retention" not "retention"
2. Relationship and retention recommendations with £0 value show "Value TBD" in muted text
   instead of "£0"
3. The reasoning trace no longer truncates product names — "Supply Chain Visibility
   Platform - Global Container Tracking" appears in full
4. Contact strip consistently shows 3 contacts when 3 are available
5. Quick prompt buttons scroll horizontally on narrow copilot width instead of wrapping
6. Panel header buttons never wrap on any screen width above 800px
7. Data limitations start collapsed on every plan load and re-render

---

## Final commit

```bash
git add -A
git commit -m "fix: demo polish - capitalisation, zero values, truncation, layout fixes"
git push origin main
```
