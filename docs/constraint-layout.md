# Relationship layout experiment (v0.6.5)

This is a generic geometry experiment, not a furniture library. The AI still chooses parts, sizes, relationships and outlines. The engine calculates coordinates from those relationships. The experiment is available for local Ollama **plan views only**; the existing insertion bridge is unchanged.

## Loading

Close every AutoCAD instance, reopen AutoCAD 2026, and `NETLOAD` `artifacts-v065\BlockAI.bundle\Contents\Windows\BlockAI.dll`. Keep `BlockAI.Core.dll` beside it. Run `AIBLOCK` and check the v0.6.5 label. Enable **Use anchored layout tools (experimental)** to try this pipeline; disable it to use v0.6.4's millimeter-part workflow.

Start with a specific request such as `Right return L-shaped sofa plan, overall 2400 x 1800 mm`. This is a suggested test, not a guaranteed successful design. Keep the design viewer recording when comparing results.

## Two stages

1. **Rough layout:** the model declares stable box IDs, roles, physical or relative sizes, and relationships to the overall footprint or another part. Numeric checks run on simple rectangular regions before details are requested. Coincident rough boxes and requested oval/tapered outlines are deferred to the appearance stage; a region box is not a final outline or depth decision.
2. **Appearance:** the model chooses outlines and depth order inside the solved regions. It cannot change positions, dimensions, IDs or the number of parts in this stage. The existing geometry engine constructs and trims the final drawing. Numeric checks and optional subjective visual review then run.

The default six-attempt limit counts layout/repair cycles. The first cycle can contain separate layout, detail and review calls, so it may take longer than the previous direct draft. Reasoning mode remains disabled: a local trial exhausted its context/output allowance before returning any layout. The palette's cancellation and ten-minute total deadline still apply. Image input is enabled only for the same recognized local vision designer names as before.

## Available tools

`footprint` is an engine-provided, non-drawn target. Each actual part has a stable ID (`part1` through `part8` in the model schema), a role, and one target. No coordinates are requested.

| Property | Meaning |
| --- | --- |
| `width`, `widthMode` | Physical millimeters (`mm`) or a fraction of target width (`fraction`, >0 through 1) |
| `depth`, `depthMode` | Physical millimeters or a fraction of target depth |
| `xAlign` | `left`, `center`, `right` align within target; `before` / `after` attach outside its left/right edge |
| `yAlign` | `bottom`, `center`, `top` align within target; `below` / `above` attach outside its lower/upper edge |
| `offsetXmm`, `offsetYmm` | Deliberate inset/gap after alignment, normally zero |
| `contained` | Require the complete box inside its target, in addition to staying inside the overall footprint |

For example, a narrow strip can match its target's width using `widthMode="fraction", width=1` and sit inside its rear edge using `yAlign="top"`. A perpendicular surface can attach below another using `yAlign="below"`, with `contained=false`. These operations apply to any geometry; no category selects a prebuilt design.

Dependencies may reference later boxes, but unknown IDs, cycles, nonfinite sizes and out-of-footprint boxes are rejected. The engine does not stretch the assembly to conceal a size error. Unlike the older free-coordinate workflow, this experimental layout stage requires boxes wholly inside the overall footprint. Legacy modest-overflow handling is retained in the other workflow.

## Repairs and diagnostics

Repairs return up to eight scalar edits against existing IDs and up to three complete appearance replacements. Omitted definitions stay equal. A changed target propagates to its dependents; preserving a dependent definition does **not** freeze its resolved coordinates. The viewer's next solved proposal records the resulting positions. Reviewer-identified boxes and their target ancestors are editable; other definitions stay locked. When a reviewer supplies no affected indices, all IDs remain editable and that ambiguity is recorded.

Repairs receive the current engine-resolved box measurements whenever the relationship graph can be solved. A new construction failure replaces the previous numeric blocker; rejected/no-op edits retain the unresolved blocker. Visual feedback is tracked separately for rechecking instead of accumulating stale numeric errors.

Recordings retain the model's graph tool arguments, solved rough proposals, construction frames, appearance tool arguments, edits, numerical failures and visual feedback. The part inspector shows stable IDs and their relationships. They contain no rendered image bytes or inferred model reasoning. `constraint-demo.html` and its JSON/PNG are **scripted demonstrations**, including a review-driven repair, not live AI outputs.

## Limits

This is a directed anchor resolver, not a general simultaneous constraint solver or 3D modeler. A box currently uses one target for both sizing and alignment. There are no Boolean unions, holes, multi-target equations, automatic symmetry synthesis, or semantic links from reference outlines to generated parts. Detail curves can still be poorly chosen. Box contact does not guarantee actual curved-outline contact; final assembly checks can reject it.

The rough layout's topology is fixed once parsed. Repairs can change sizes, targets and relationships, but cannot add/delete/rename parts. A fundamentally wrong part decomposition requires a new generation. Rough rectangles can pass numeric checks while still looking unlike furniture. The visual reviewer remains fallible. References remain studies supplied as notes, bounded geometry summaries and optional images; this work does not train model weights.

## Verification

`tests/BlockAI.DesignChecks/ConstraintChecks.cs` checks left/right returns at three sizes, dependency propagation, containment, cycles, missing targets, stable-ID locks, unchanged repairs, separate layout/detail stages, review-driven repairs, stale-feedback removal, rough-versus-final outline checks, failed layouts, cancellation and offline replay data. These scripted tests verify tool behavior, not the model's furniture competence. The local trials have not established reliable autonomous sofa generation.

An optional real-model smoke test saves responses and recordings locally:

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/cli'
& ./.tools/dotnet/dotnet.exe run --project tests/BlockAI.DesignChecks -c Release -- --live --sofa --edits --metric --constraints
```

The test does not insert anything into AutoCAD. View resulting recordings under `artifacts-design/live-recordings`; successful previews are under `artifacts-design/design-1.png`. Do not confuse stale previews from earlier runs with a newly successful generation: check recording status and timestamps.
