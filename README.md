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

The local provider uses Ollama at `127.0.0.1:11434` with `qwen2.5:7b` by default. Local inference has no API fees and needs no OpenAI key. Expect a large initial download, disk/RAM/GPU use, and potentially slower generation than cloud inference. Block quality still needs review.

```powershell
# Downloads official portable Ollama; verifies the published SHA256 before extracting.
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ollama.ps1 -InstallRoot D:\Ollama
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Ollama\start-ollama.ps1
# Wait a few seconds for the server to start, then download the model:
D:\Ollama\app\ollama.exe pull qwen2.5:7b
```

Application: `D:\Ollama\app`; models: `D:\Ollama\models`; logs: `D:\Ollama\logs`; archive: `D:\Ollama\downloads`. On a fresh setup the installer creates a directory junction from `%USERPROFILE%\.ollama` to `D:\Ollama\config`, keeping actual configuration data on D:. Existing configuration is preserved without moving it. Windows itself may still use system caches or temporary files on C:.

The launcher sets storage variables for its child process, disables Ollama cloud features, and binds only to loopback. It does not change global PATH, install a Windows service, or add a startup task. Click **Start local AI** after reboot. The panel's launcher expects `D:\Ollama\start-ollama.ps1`; custom installation locations can be started manually.

Local requests use an 8192-token context and compact JSON, with a schema limit of 40 entities and 32 points per entity, to keep simple blocks practical on an 8 GB GPU.

**Check models** lists installed local models. A missing model must be downloaded using `ollama.exe pull`. The client does not download models or silently fall back to paid cloud generation. Geometry failing validation gets at most one local repair attempt. Cancel is available; generation has a ten-minute timeout.

## Behavior and limits

- Produces actual polylines and circles inside a unique block definition, with layer 0 and ByBlock colors. Blocks are stored in the drawing and can be edited with AutoCAD's block editor.
- Geometry is designed in millimeters, then scaled to the explicitly selected drawing units. It inserts on the current UCS XY plane, in the current drawing space and current layer.
- Supports arbitrary prompts within the simple 2D geometry vocabulary. Recognition and dimensional accuracy depend on model output. This is conceptual drafting geometry; review it before production use.
- No 3D, dynamic-block parameters, arcs, hatches, text, dimension objects, DWG export, or manufacturing details yet. Wood is represented by linework, not a rendered material.
- The offline example is fixed, clearly labeled, and does not interpret the prompt. It assumes a rounded 1930 × 2030 mm North American king mattress; specify exact dimensions for regional alternatives.
- Validation limits geometry size, entity count, coordinates, and degenerate outlines. AI output is treated as data. No generated scripts or commands are executed.
- Local mode sends only your description and drafting instructions to Ollama on this PC; it does not send your API key. Cloud mode sends the description and instructions to OpenAI with `store: false`. Neither uploads drawing contents. OpenAI retention policies and API billing apply only to the OpenAI provider.
- Keys are password-masked, held in process memory, and never written to project files. Optional: set `OPENAI_API_KEY` before starting AutoCAD. Do not commit keys. This local prototype is intended for your own key; a distributable commercial product should use an authenticated backend rather than embed a shared key.

## Validation

`tests/BlockAI.Checks` checks dimensions, serialization, unit conversion, invalid geometry rejection, both providers' requests/responses, local repair, cloud-model rejection, model discovery, and HTTP errors using fake HTTP handlers. It makes no paid API requests. Pass `--local-smoke` to run an actual local king-bed generation and save the validated result to `artifacts-next/local-bed-smoke.json`. A successful compilation verifies references to the installed AutoCAD API. Interactive loading, UCS placement, undo, and palette docking still require an AutoCAD smoke test.

`tests/BlockAI.PanelChecks` is an optional Windows UI smoke test. It exercises the actual panel off-screen, with the AutoCAD insertion bridge stubbed. It checks provider defaults, per-provider model retention, key-field visibility, preview/insertion state, and live Ollama model discovery. Start Ollama first and create `artifacts-next` before running it; it saves `artifacts-next/panel-local-preview.png`.

A live local bed test completed with parseable, validated geometry, but its dimensions and visual layout did not fully match the request. The current validator checks geometry integrity, not furniture recognition or compliance with the requested dimensions. Web reference search, CAD catalog import, image tracing, and reliable parametric furniture generation are not implemented yet.

## GitHub

Source, packaging manifest, build script and checks belong in Git. `.gitignore` excludes SDK downloads, generated binaries, build artifacts and local secrets. No Autodesk binaries or API keys should be uploaded.

## References

- [Autodesk managed .NET compatibility](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm)
- [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=responses)
- [Ollama Windows portable setup and storage](https://docs.ollama.com/windows)
- [Ollama structured output](https://docs.ollama.com/capabilities/structured-outputs)
