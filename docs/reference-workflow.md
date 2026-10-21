# Reference preparation

The initial reference task is an inventory, not model training. A whole DWG collection is not one furniture example. Before using it as guidance, isolate an individual object, identify plan/front/side, establish scale, and pair views only when their identity is known. Block names and sheet positions alone do not establish a match.

The desired later analysis output is semantic: part roles, relative proportions, connections, which surfaces hide others, visible vs inferred facts, and uncertainty. The generator should receive a few relevant design lessons and produce new geometry for the user's request, rather than inserting or tracing a source block. Reference retrieval/analysis is not yet integrated into the generation panel.

## Local DWG inventory tool

`tools/BlockAI.ReferenceExport` runs inside AutoCAD Core Console. It reads entity geometry and writes layout summaries, sampled-curve JSON and SVG overviews. It recursively explores block references and records group/layer information and readable text. It skips unsupported/degenerate entities and reports warnings. This is not a full DWG converter; hatches, dimensions and other objects may be absent.

Build the inspector with the same .NET 8 SDK used for the plugin:

```powershell
.\.tools\dotnet\dotnet.exe build .\tools\BlockAI.ReferenceExport\BlockAI.ReferenceExport.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\inspect-references.ps1 -Files 'C:\path\to\reference.dwg'
```

The script reads the named input and opens a copy in `artifacts-references/source-copies`, never the original drawing. Exported JSON/SVG and console logs are placed in `artifacts-references`. It temporarily trusts its specific loader folder in the headless AutoCAD process, restores the previous setting, and answers Yes to discarding drawing changes on exit. An ASCII temporary loader path avoids script encoding problems with Unicode workspace paths. It does not operate on the user's active AutoCAD document.

Keep third-party reference assets and derived exports local. The repository ignores `artifacts-*`; only the inspector code and this workflow are published. Preserve provenance and applicable permissions with any reference collection. A linked source category is useful for discovery, but it does not prove that a particular file may be redistributed or used for training.
