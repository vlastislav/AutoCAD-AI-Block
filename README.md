# AutoCAD-AI-Block

Block AI for AutoCAD 2026.

Windows AutoCAD plugin prototype: describe an object, preview a plan and optional front/right-side elevations, and insert a selected view as an editable native block. Type `AIBLOCK` to open the dockable panel.

## v0.6.3: scoped edits, symmetry, and more attempts

After the first local draft, the palette requests indexed replacements/additions/removals instead of a whole new design. Unmentioned part parameters and assembly dimensions are preserved. Following a visual rejection, only implicated part indices may be replaced/removed; other existing parts are locked. Unneeded additions, empty patches, invalid indices and full-design responses are rejected. Locks are preserved when an edit fails validation. Corrections stay in the retry context and are rechecked by the reviewer, which can still make mistakes.

The default budget is six attempts, adjustable from one to ten; cancellation and the ten-minute local generation timeout still apply. Left/right symmetry is checked on visible linework by reflection within 2 mm. It is enabled by default, with exceptions for explicitly asymmetric/corner/chaise/offset/L-shaped requests. Disable it for intentionally uneven detail. This validates geometry rather than choosing a furniture template. It does not guarantee good designs, and stricter checks may exhaust the budget.

Restart AutoCAD and `NETLOAD` `artifacts-v063\BlockAI.bundle\Contents\Windows\BlockAI.dll`, then run `AIBLOCK` and confirm **v0.6.3**. Elevations remain hidden. The replay records attempt policy, applied edits, part locks and symmetry checks. Source DWGs stay private.

The local Ollama smoke test returned the new edit format but still exhausted six attempts on bed geometry checks. This verifies protocol use, not reliable furniture design. Scripted regression checks separately demonstrate a successful fourth attempt, unchanged correct parts, and rejection of an edit to a locked part.

## v0.6.2: focus on plan views

The palette now generates and reviews plan views only. Elevation controls are hidden, and matching uses plan references only. Elevation code and older multi-view recordings remain available for later development. Load `artifacts-v062\BlockAI.bundle\Contents\Windows\BlockAI.dll` after restarting AutoCAD and confirm **v0.6.2 · Plan views only** in `AIBLOCK`.

## v0.6.1: measured reference geometry prototype

The first bed study can now supply normalized DWG outlines, measured aspect ratio, repeated outline sizes and bounding-box containment alongside its notes. This is an experimental geometry input, not a trained model or a traced block. The palette labels studies with measured geometry; the replay renders that input separately from generated output. Only the isolated `bed053` study has been prepared this way locally. Other references still supply notes.

Bed requests no longer receive the worked chair assembly. Basic conventional-bed checks reject a sleeping surface occupying less than 55% of either footprint axis and a headboard shaped like a narrow central post. These checks do not establish visual quality. Original source units and unseen construction remain unknown.

The live geometry-guided queen-bed test still failed after three attempts: its first draft had a broad mattress but poor detail placement, the reviewer missed visible pillows and invented texture requirements, and later repairs exceeded output limits or duplicated frame outlines. This prototype is for inspection and further development, not a proven quality fix. Load the current local build from `artifacts-v061\BlockAI.bundle\Contents\Windows\BlockAI.dll` after restarting AutoCAD, then run `AIBLOCK` and confirm v0.6.1. With reference matching enabled, a normal bed request should list **Framed bed with narrow head rail, plan (measured geometry + notes)**.

## v0.6.0: reference studies and meaningful revisions

The local generator can now use up to two reviewed furniture studies selected by category, name and tags. Open **Reference library** to inspect analyses, correct names/tags, and approve isolated image analyses. Supplied DWGs were used to prepare a private starter collection: 13 studies, 10 reviewed and enabled. Source files and derived images are not bundled or published. See [reference preparation](docs/reference-workflow.md) for the workflow and limitations.

Visual review sends its corrections and the previous assembly back to Ollama. A retry with the same geometry fingerprint (coordinates quantized to 2 mm) is rejected even if names change; the replay identifies changed and unchanged revisions. There are at most three attempts. This prevents unchanged drafts being accepted as corrections, but does not guarantee the model can fix them. A live reference-guided three-view chair test exhausted its retries on duplicate/disconnected geometry; mocked success, failure and revision tests cover the control flow separately.

Depth visibility and three matching views remain available:

Local generation now defaults to **Generate plan, front and right-side views**. The AI supplies one shared assembly: normalized plan footprints, overall height, and each part's bottom `z` and vertical `height` in millimeters. The engine derives all three editable drawings from that assembly. Use the view selector above the preview; **Insert into drawing** inserts the currently selected view. Uncheck the three-view option for the earlier plan-only workflow. OpenAI mode remains plan-only.

Intentional projection overlap is accepted. A closed `opaque=true` surface hides lower parts; `opaque=false` is linework that does not mask them. Open curves never mask a surface. Plan-only mode uses `level` (-100..100, larger means above). Three-view mode uses actual top Z first, then level for coincident surfaces. Front looks from -Y and right side from +X; nearer opaque projection envelopes hide lines behind them, with level breaking ties. The viewer records depth order and before/after trimming. Same-level duplicate outlines and disconnected construction can still fail; overlap itself is not a blanket error.

Elevations are a first experimental implementation using **vertical extrusion envelopes**, not a complete 3D solid model. A curved seat footprint can have a rectangular side envelope; shaped/sloped legs and curved elevation profiles are not represented. Part ordering and dimensions can still be wrong, and the vision reviewer can miss them. Three-view review receives a labeled sheet at a shared drawing scale. Reference DWGs have been inventoried locally, but reference analysis/retrieval and model training are not connected to generation yet.

For an offline three-view example, open `artifacts-v060\three-view-demo.html` and choose Front or Right side in the viewer. `three-view-demo.png` shows the matching projections. This is a clearly labeled scripted fixture, not a live model result. A separate live chair test completed all three views and visual review in two attempts (50.9 seconds on this machine); its first attempt duplicated a leg pair. These observations are not a quality or latency guarantee.

## Standalone visual debugging viewer

v0.4.4 fixed the JSON placeholder error when opening the source viewer without embedded recording data. The empty viewer opens normally and lets you import a recording; generated HTML exports embed validated JSON. Browser checks cover both the demo and the empty source viewer.

Open `artifacts-v060\design-viewer.html` in a browser to explore the clearly labeled demonstration recording. It replays real engine construction around scripted designer/reviewer replies: a disconnected first draft fails validation, a second draft receives visual feedback, and a third draft succeeds. Demo timings are test execution times, not AI inference benchmarks.

In the updated plugin, generate a design and click **Open design viewer**. Success, exhausted retries, model/network errors, and cancellation are saved locally as paired JSON/HTML files in `%LOCALAPPDATA%\BlockAI\DesignRecordings`. The button opens the most recently saved recording; it also finds previous recordings after the panel is reopened. Save errors are shown separately and do not prevent insertion. Files are kept until you delete them; a hard process termination before the generation's `finally` block can prevent saving.

Use the step slider or Play to replay each attempt. Amber dashed boxes are the AI's original requested part bounds, not a second implementation of its drawing tools. Teal outlines are actual geometry emitted by the engine. Construction events expose resolved alignment, attachment coordinates, repetition and symmetry. Later steps show deduplication, hidden-edge trimming, exact fitting, validation failures and structured visual feedback. Click a part to inspect its parameters, visible bounds and requested anchor roles. Red borders mark failed stages; named/focused parts are highlighted where identifiable. The viewer does not invent a precise error location when the validator only supplies a general message.

Compare any two attempts using the same viewport and scale. A successful attempt's recorded fit is applied to its earlier stages, preserving the relationship between proposed boxes and constructed geometry. Unfitted failed attempts use estimated dimensions from their proposed plans, explicitly labeled in the view. The view extents stay fixed across attempts and playback. The comparison pane shows the other attempt's last recorded state; select that attempt on the left to replay its individual steps.

HTML files are self-contained and need no AutoCAD, Ollama, server, internet connection or third-party web libraries. Open or drop a recording JSON file to inspect another run. **Save JSON** and **Save HTML** export the loaded recording. Imports accept schema version 1 up to 25 MB. This records observable tool requests and engine operations, not private model reasoning. It contains typed requests, model names, part arguments, geometry, timings and feedback; no API key field, request headers, preview images, screen captures or unrelated drawing contents are recorded. Recordings can contain sensitive design descriptions, so review them before sharing.

The local part-based workflow has the full replay. Legacy OpenAI mode records its final validated geometry and errors, but cannot show part construction stages that it does not perform. Recordings from before v0.4.3 cannot recover missing historical stages, and older plan-only recordings cannot recover elevations. This viewer diagnoses design quality; it does not improve the model or certify a design.

### Build and verification

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -OutputDirectory artifacts-v060 -CheckViewer
```

The build runs 68 core checks and 87 construction/recording checks, and generates the portable demo. `-CheckViewer` additionally runs 27 real headless Edge checks at each of desktop and narrow widths for playback, comparison scale, part inspection, failed/canceled replay, local imports, exports and escaped text. Screenshots and browser reports are written to `artifacts-design`. To use another Chromium executable, run `scripts\check-viewer.ps1 -Browser 'path\to\browser.exe'` after the build. `tests/BlockAI.PanelChecks` separately checks the palette with a stubbed AutoCAD insertion bridge; it does not test native AutoCAD itself. A local `qwen3.5:4b` generation with visual review also completed and recorded successfully in 11.5 seconds on this machine.

Diagnostic snapshots retain up to 500 outlines per step, with an explicit notice if an invalid oversized assembly exceeds that limit. All original part arguments remain recorded, and the full assembly is still validated normally. This limit affects replay data, not accepted block geometry.

## Autonomous design experiment

v0.4.2 fixes repeated “exceeds the 0–1 design area” failures: reasonable coordinate overflow is fitted as a complete assembly to the requested dimensions. Individual parts are not clipped or moved separately. Nonfinite/pathological coordinates and construction errors still fail validation. Regression checks cover negative origins, oversized boxes, preserved joints/proportions and exact output size.

Local generation now asks AI to compose a **part-based design**, not select a furniture category/template. The toolkit includes rectangles, rounded outlines, trapezoids, ellipses, custom polygons/paths, quadratic curves, inner outlines, alignment, repetition, and mirrored parts. The model selects roles, shapes, proportions and arrangement. Toolkit curves are sampled into editable CAD polylines.

Try **Dining chair, no cushion, 500 x 550 mm**, then request an oval seat, curved backrest, or four back slats. The engine fits the overall footprint to explicit dimension pairs and checks finite/bounded geometry, same-depth duplicates, chair seat/back presence, actual outline contact, and declared forbidden cushions/arms. Intentional overlap is resolved by depth and opacity. A connector can name `from`/`to` anchor roles; the engine computes its placement against actual outlines rather than relying on guessed coordinates. It does not prove physical construction, hidden details or visual recognition. Negative-feature checks inspect part roles and seat insets, so a mislabeled feature can evade them.

One annotated worked plan is supplied in the prompt to teach normalized coordinates and attachment semantics. This is in-context guidance, not fine-tuning or a block selected by category. The model still chooses all parts and tool arguments. Its designs may remain simple, incorrect, or fail all three attempts. Do not treat this as a finished autonomous furniture designer.

The default model is `qwen3.5:4b`. With **Review the preview with vision AI** enabled, the rendered plan is sent to the selected local review model. Feedback or geometric errors trigger a revised plan, with at most three design attempts. A design that still fails is not offered for insertion. There is no silent template fallback. Visual review is subjective and can approve a poor design or reject a reasonable one. Uncheck it for faster geometry-only experiments; the result explicitly records that visual review was disabled.

The review sends a PNG rendered from the generated block, the typed request and its generated description to localhost only. It does not capture the drawing or screen. Cloud/OpenAI mode remains the older direct-geometry workflow and does not use this local loop.

### Install and test this build

1. Close every AutoCAD window, reopen AutoCAD and `NETLOAD` the new DLL. For the current prepared build use `artifacts-v060\BlockAI.bundle\Contents\Windows\BlockAI.dll`, keeping `BlockAI.Core.dll` beside it.
2. Run `AIBLOCK` and confirm the **v0.6.0** label. Use local model `qwen3.5:4b` for design/review.
3. Generate, review the preview and size, select the actual drawing units, then click **Insert into drawing** and pick a point. Esc cancels placement.

The insertion button now enters AutoCAD command context directly, focuses the drawing and reports success/cancellation/errors back to the palette. Finish any active AutoCAD command before inserting. `AIBLOCKINSERT` can retry the last insertion from the command line in the same drawing. The user confirmed insertion works in their AutoCAD session. Undo and non-default UCS behavior still need separate interactive checks; the off-screen UI tests stub the native bridge.

Run `tests/BlockAI.DesignChecks` for construction/constraint/revision tests. Pass `--live --variations` for three real AI chair designs with rendered review; outputs are in `artifacts-design`. Add `--no-review` to test geometry-only generation. The build script runs the deterministic checks without paid API calls. `tests/BlockAI.PanelChecks` also exercises insertion success/cancel/error UI states.

Local verification: v0.4.2 passed 68 core and 44 design-loop checks. The previous build also passed 12 panel checks; three live chair variations completed construction and visual review in 20.6, 6.7 and 8.3 seconds respectively. Their geometry differed and the requested oval/tapered seats and four-slat count were represented. Manual preview inspection still found awkward backrest proportions; passing review is not evidence of architectural-quality output. These timings are observations on this machine, not guarantees.

Example: **A king size wooden bed plan, North American king mattress, simple headboard.**

## Earlier v0.3 workflow (retained for regression checks)

The earlier local workflow asked AI for the furniture category, dimensions, seat count and style. Drawing routines then built the plan with consistent parts and exact overall bounds. It remains in the source for regression checks, but is not the v0.4 palette generation path.

Supported single objects: straight sofas, armchairs, beds, rectangular/round tables, desks and cabinets. Try **A modern 3-seat sofa, 2400 x 950 mm**. Sofas have separate seat/back cushions and arms. Bed dimensions refer to the outside frame; an explicit **mattress 1930 x 2030 mm** receives a 100 mm width and 200 mm depth frame allowance. Round tables use equal width/depth for their diameter.

Explicit dimension pairs (`2400 x 950 mm`, `2.4m by 95cm`, `84 x 36 inches`) override model size guesses. Without units, pairs are interpreted as millimeters. Modern, classic and minimal styles currently vary outline corner treatment, not ornamental detail. Simplified furniture plans do not represent exact manufacturer products, construction details, or materials.

Objects outside these categories, multi-object compositions and unsupported shapes use clearly labeled experimental free-form generation. L-shaped sofas, armless seating and ornate designs do not yet have dependable furniture routines. AI can still misclassify a request or miss a feature; review the description, dimensions and preview.

## Build

Requires full AutoCAD 2026 for Windows (not AutoCAD LT) and the .NET 8 SDK. The project references Autodesk assemblies from the local AutoCAD installation; Autodesk DLLs are not distributed with the plugin.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

For a different installation folder, pass `-AutoCADDir 'D:\Autodesk\AutoCAD 2026'`. The script prefers a project-local SDK at `.tools\dotnet`, if present, and runs dependency-free geometry/API checks before packaging.

AutoCAD locks loaded plugin DLLs until it closes. To build while it is open, pass `-OutputDirectory artifacts-next`. Restart AutoCAD before loading an updated DLL; `NETLOAD` does not replace the assembly already loaded in that process.

This machine's installed AutoCAD assemblies target .NET 8. Autodesk documents a .NET 10 transition for AutoCAD 2026 Update 1.2 and later. Rebuild and validate against that installation before distributing to updated machines; this prototype targets the local .NET 8 installation.

## Load and use

1. Build. The distributable folder is `artifacts\BlockAI.bundle`.
2. For a quick trial, open AutoCAD, run `NETLOAD`, and select `artifacts\BlockAI.bundle\Contents\Windows\BlockAI.dll`. Keep `BlockAI.Core.dll` beside it. If AutoCAD requests trust, review and trust this specific build folder in AutoCAD's Trusted Locations; keep secure loading enabled.
3. Run `AIBLOCK`. Click **Load example king bed** to preview an offline example without an API key.
4. Set **Drawing units** to match the current drawing, click **Insert into drawing**, then pick a point. Esc cancels. `U` undoes an insertion.
5. For custom generation, use **Local AI — Ollama** (the default), click **Start local AI**, then **Check models**. Once your model is available, enter a description and click **Generate block**. Review dimensions and assumptions, then click **Insert into drawing** and choose a point.
6. Alternatively select **OpenAI (paid API)** and enter your API key. This mode requires Responses + Structured Outputs support; its initial model is `gpt-4.1-mini`.

For automatic loading, copy the whole `BlockAI.bundle` folder into `%APPDATA%\Autodesk\ApplicationPlugins` and restart AutoCAD. Installation is manual; the build script only writes inside this project.

## Free local generation on D:

The local provider uses Ollama at `127.0.0.1:11434` with `qwen3.5:4b` by default for design and vision review. The older `qwen2.5:7b` remains usable as a text-only designer with a separate vision review model. Local inference has no API fees and needs no OpenAI key. Expect a large initial download, disk/RAM/GPU use, and slower generation than the old parameter/template workflow. Block quality still needs review.

```powershell
# Downloads official portable Ollama; verifies the published SHA256 before extracting.
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ollama.ps1 -InstallRoot D:\Ollama
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Ollama\start-ollama.ps1
# Wait a few seconds for the server to start, then download the model:
D:\Ollama\app\ollama.exe pull qwen2.5:7b
# v0.4 design + vision review model:
D:\Ollama\app\ollama.exe pull qwen3.5:4b
```

Application: `D:\Ollama\app`; models: `D:\Ollama\models`; logs: `D:\Ollama\logs`; archive: `D:\Ollama\downloads`. On a fresh setup the installer creates a directory junction from `%USERPROFILE%\.ollama` to `D:\Ollama\config`, keeping actual configuration data on D:. Existing configuration is preserved without moving it. Windows itself may still use system caches or temporary files on C:.

The launcher sets storage variables for its child process, disables Ollama cloud features, and binds only to loopback. It does not change global PATH, install a Windows service, or add a startup task. Click **Start local AI** after reboot. The panel's launcher expects `D:\Ollama\start-ollama.ps1`; custom installation locations can be started manually.

The design loop uses an 8192-token context and a bounded part-based response. It uses Ollama's chat API, with the toolkit contract also included in the user message for models whose installed prompt template ignores system messages. The legacy template and direct-geometry clients remain in the source for regression comparisons.

**Check models** lists installed local models. A missing model must be downloaded using `ollama.exe pull`. The client does not download models or silently fall back to paid cloud generation. The design loop allows at most three design attempts. Cancel is available; generation has a ten-minute timeout.

## Behavior and limits

- Produces actual polylines and circles inside a unique block definition, with layer 0 and ByBlock colors. Blocks are stored in the drawing and can be edited with AutoCAD's block editor.
- Geometry is designed in millimeters, then scaled to the explicitly selected drawing units. It inserts on the current UCS XY plane, in the current drawing space and current layer.
- The current local design loop preserves the explicit overall dimension pair by fitting the assembled footprint. Parts, proportions and requested details depend on model output and selected checks. The result remains conceptual drafting geometry; review it before production use.
- No 3D, dynamic-block parameters, arcs, hatches, text, dimension objects, DWG export, or manufacturing details yet. Wood is represented by linework, not a rendered material.
- The offline example is fixed, clearly labeled, and does not interpret the prompt. It assumes a rounded 1930 × 2030 mm North American king mattress; specify exact dimensions for regional alternatives.
- Validation limits geometry size, entity count, coordinates, and degenerate outlines. AI output is treated as data. No generated scripts or commands are executed.
- Local mode sends only your description and drafting instructions to Ollama on this PC; it does not send your API key. Cloud mode sends the description and instructions to OpenAI with `store: false`. Neither uploads drawing contents. OpenAI retention policies and API billing apply only to the OpenAI provider.
- Keys are password-masked, held in process memory, and never written to project files. Optional: set `OPENAI_API_KEY` before starting AutoCAD. Do not commit keys. This local prototype is intended for your own key; a distributable commercial product should use an authenticated backend rather than embed a shared key.

## Validation

`tests/BlockAI.Checks` checks dimensions, sofa cushion separation/containment, all furniture routines, explicit unit conversion, mattress allowances, impossible seat counts, parameter requests, experimental fallback, geometry validation, both providers, local repair, model discovery and HTTP errors. It makes no paid API requests. Pass `--local-smoke` to run actual local sofa, bed and round-table requests and save geometry to `artifacts-next/local-*-smoke.json`. A successful compilation verifies references to the installed AutoCAD API. Interactive loading, UCS placement, undo, and palette docking still require an AutoCAD smoke test.

`tests/BlockAI.PanelChecks` is an optional Windows UI smoke test. It exercises the actual panel off-screen, with the AutoCAD insertion bridge stubbed. It checks provider defaults, per-provider model retention, key-field visibility, preview/insertion state, and live Ollama model discovery. Start Ollama first and create `artifacts-next` before running it; it saves `artifacts-next/panel-local-preview.png`.

The earlier free-form workflow produced poor furniture proportions, motivating the v0.3 furniture routines. Web reference search, CAD catalog import and image tracing are not implemented.

Live v0.3 checks on the local RTX 3070 with the model warm and queue clear completed a 2400 × 950 mm three-seat sofa in 1.1 s, a king bed in 1.1 s and a 1200 mm round table in 0.8 s. First loading and other queued requests increase elapsed time; these are measurements, not latency guarantees.

## GitHub

Source, packaging manifest, build script and checks belong in Git. `.gitignore` excludes SDK downloads, generated binaries, build artifacts and local secrets. No Autodesk binaries or API keys should be uploaded.

## References

- [Autodesk managed .NET compatibility](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm)
- [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=responses)
- [Ollama Windows portable setup and storage](https://docs.ollama.com/windows)
- [Ollama structured output](https://docs.ollama.com/capabilities/structured-outputs)
