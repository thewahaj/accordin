# Approved Plan Monitoring Scope

## Goal

Define a stronger flow for approved plans so users can:

- keep the familiar approved-plan UI on the right side
- see what changed on the left side
- understand whether the approved plan still looks healthy
- decide whether to keep the plan as-is or add new execution items

This is intended as a planning note only.

## Core Idea

When a user opens an approved plan:

- the right side stays largely unchanged, especially the existing plan sections for actions and cadences
- the left side becomes a live review area for:
  - new signals since plan approval
  - plan health / execution drift
  - recommended response

The system should not immediately rewrite the whole plan. It should first explain what changed and then let the user confirm whether any updates are needed.

## Two Attention Engines

### 1. New Signals Requiring Attention

We need to detect whether anything materially new has appeared since the approved plan baseline.

Recommended rule:

- a signal is "new" if it entered the account after the plan approval timestamp
- if approval timestamp is unavailable, use the plan generated timestamp as fallback

This means the plan needs a clear baseline date:

- preferred: `planApprovedAt`
- fallback: `planGeneratedAt`

### 2. Existing Plan Health

We also need to know whether the approved plan is being executed on time.

Examples:

- an action is overdue
- an action is still not completed after its due date
- a cadence has not been carried out by the expected touch date
- a cadence is stale for too long

This is important because "requires attention" should not only come from new account changes. It should also come from execution drift in the existing plan.

## Workflow Foundation Needed First

Before plan health can be trusted, actions and cadences need real dates.

### Actions

Current issue:

- actions currently use soft text timing such as `suggestedTiming`
- that is not enough for reliable health tracking

Recommended fields:

- `plannedDate` or `dueDate`
- `status`
- `completedDate`
- `owner`
- optional `lastTouchedDate`

### Cadences

Cadences already have a start date concept, but need stronger tracking for ongoing execution.

Recommended fields:

- `startDate`
- `nextPlannedTouchDate`
- `lastCompletedTouchDate`
- `status`
- optional `missedCount`

With these fields, plan health can support rules like:

- overdue action
- action not completed after due date
- cadence touch overdue
- cadence stale for X days

## How To Detect New Signals Since Plan Creation

We need 3 things:

### 1. Baseline Timestamp

Use one consistent comparison point:

- `planApprovedAt`
- fallback: `planGeneratedAt`

### 2. Structured Signals

Signals should not only exist as rendered narrative strings. Each signal should have structured event data.

Minimum recommended structure:

- `signalType`
- `signalDate`
- `source`
- `subject` or related entity
- stable id or dedupe key
- summary text

### 3. Comparison Rule

To identify new signals:

- fetch current account signals
- keep only signals where `signalDate > planApprovedAt`
- dedupe repeated events

Recommended dedupe:

- best: `signalType + sourceRecordId`
- fallback: `signalType + subject + signalDate + normalized summary`

## New Signals vs Signals Requiring Attention

Not every new signal should trigger plan updates.

We should separate:

- `New signals`
- `New signals requiring attention`

A signal should require attention when it materially changes execution, for example:

- buying intent increases
- risk increases
- a new approval dependency appears
- a key stakeholder changes
- an opportunity stage or value materially changes

## Proposed Approved Plan Experience

When an approved plan is opened:

- right side keeps the familiar approved-plan view
- left side becomes a focused review panel

Suggested left-side sections:

- `New Since Approval`
- `Plan Health`
- `Recommended Response`

Suggested response choices:

- `No plan changes needed`
- `Add action`
- `Add cadence`
- `Review full plan`

## Attention Banner

Suggested banner language:

`Account data changed since this plan was approved. Review what changed and confirm whether updates are needed.`

This can be adapted when the issue is plan health rather than new signals.

Example variant:

`This plan requires attention because execution is behind schedule. Review overdue items and confirm whether updates are needed.`

## Requires Attention Logic

The "requires attention" state should come from both engines:

- new signals requiring attention
- plan health drift

High-level rule:

- if either engine crosses a threshold, the plan is marked as requiring attention

## Background Monitoring Need

This feature likely needs a background process or scheduled monitoring job.

Reason:

- without monitoring, the system can only detect issues when the user manually opens the plan
- that is not enough if we want to confidently label a plan as `requires attention`

The monitoring job should periodically:

- evaluate new signals since plan approval
- evaluate plan health against due dates / cadence dates
- compute an attention state
- store the latest attention reason(s)
- support surfacing plan-level status in list views before the plan is opened

## Current Scope Direction For Tomorrow

Recommended scope discussion for next session:

1. Define the minimum data model changes for trackable actions and cadences.
2. Define the baseline timestamp and signal schema for "new since approval".
3. Define the exact attention rules and thresholds.
4. Decide whether monitoring is near-real-time, scheduled daily, or hybrid.
5. Finalize the approved-plan UI states for:
   - no changes
   - new signals only
   - plan health only
   - both

## Key Product Bet

This feature can stand out because it turns an approved plan from a static artifact into a monitored operating plan:

- it stays stable unless change is justified
- it shows exactly what changed
- it explains why attention is needed
- it helps the user decide the lightest next action
