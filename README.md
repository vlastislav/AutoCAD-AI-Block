# AutoCAD-AI-Block

Block AI for AutoCAD 2026.

Windows AutoCAD plugin prototype: describe an object, preview a 2D plan, and insert it as an editable native block. Type `AIBLOCK` to open the dockable panel.

Example: **A king size wooden bed plan, North American king mattress, simple headboard.**

## Build

Requires full AutoCAD 2026 for Windows (not AutoCAD LT) and the .NET 8 SDK. The project references Autodesk assemblies from the local AutoCAD installation; Autodesk DLLs are not distributed with the plugin.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

For a different installation folder, pass `-AutoCADDir 'D:\Autodesk\AutoCAD 2026'`. The script prefers a project-local SDK at `.tools\dotnet`, if present, and runs dependency-free geometry/API checks before packaging.

This machine's installed AutoCAD assemblies target .NET 8. Autodesk documents a .NET 10 transition for AutoCAD 2026 Update 1.2 and later. Rebuild and validate against that installation before distributing to updated machines; this prototype targets the local .NET 8 installation.

## Load and use

1. Build. The distributable folder is `artifacts\BlockAI.bundle`.
2. For a quick trial, open AutoCAD, run `NETLOAD`, and select `artifacts\BlockAI.bundle\Contents\Windows\BlockAI.dll`. Keep `BlockAI.Core.dll` beside it. If AutoCAD requests trust, review and trust this specific build folder in AutoCAD's Trusted Locations; keep secure loading enabled.
3. Run `AIBLOCK`. Click **Load example king bed** to preview an offline example without an API key.
4. Set **Drawing units** to match the current drawing, click **Insert into drawing**, then pick a point. Esc cancels. `U` undoes an insertion.
5. For custom generation, enter an OpenAI API key and a model with Responses + Structured Outputs support, then click **Generate block**. The initial model field is `gpt-4.1-mini`; use a supported model your API account can access. Review dimensions and assumptions before inserting.

For automatic loading, copy the whole `BlockAI.bundle` folder into `%APPDATA%\Autodesk\ApplicationPlugins` and restart AutoCAD. Installation is manual; the build script only writes inside this project.

## Behavior and limits

- Produces actual polylines and circles inside a unique block definition, with layer 0 and ByBlock colors. Blocks are stored in the drawing and can be edited with AutoCAD's block editor.
- Geometry is designed in millimeters, then scaled to the explicitly selected drawing units. It inserts on the current UCS XY plane, in the current drawing space and current layer.
- Supports arbitrary prompts within the simple 2D geometry vocabulary. Recognition and dimensional accuracy depend on model output. This is conceptual drafting geometry; review it before production use.
- No 3D, dynamic-block parameters, arcs, hatches, text, dimension objects, DWG export, or manufacturing details yet. Wood is represented by linework, not a rendered material.
- The offline example is fixed, clearly labeled, and does not interpret the prompt. It assumes a rounded 1930 × 2030 mm North American king mattress; specify exact dimensions for regional alternatives.
- Validation limits geometry size, entity count, coordinates, and degenerate outlines. AI output is treated as data. No generated scripts or commands are executed.
- Generation sends only your typed description and drafting instructions to OpenAI, with `store: false`. It does not upload drawing contents. Provider retention policies still apply. API usage may incur charges on your account, separate from ChatGPT subscriptions.
- Keys are password-masked, held in process memory, and never written to project files. Optional: set `OPENAI_API_KEY` before starting AutoCAD. Do not commit keys. This local prototype is intended for your own key; a distributable commercial product should use an authenticated backend rather than embed a shared key.

## Validation

`tests/BlockAI.Checks` checks dimensions, serialization, unit conversion, invalid geometry rejection, request construction, response parsing, and HTTP errors using a fake HTTP handler. It makes no paid API requests. A successful compilation verifies references to the installed AutoCAD API. Interactive loading, UCS placement, undo, palette docking, and real model output still require an AutoCAD smoke test.

## GitHub

Source, packaging manifest, build script and checks belong in Git. `.gitignore` excludes SDK downloads, generated binaries, build artifacts and local secrets. No Autodesk binaries or API keys should be uploaded.

## References

- [Autodesk managed .NET compatibility](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm)
- [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=responses)
