# Reference preparation

The initial reference task is an inventory, not model training. A whole DWG collection is not one furniture example. Before using it as guidance, isolate an individual object, identify plan/front/side, establish scale, and pair views only when their identity is known. Block names and sheet positions alone do not establish a match.

The analysis output is semantic: part roles, relative proportions, connections, which surfaces hide others, visible vs inferred facts, and uncertainty. The generation panel now retrieves up to two reviewed matching studies. It sends their notes, rather than source coordinates or images, to the designer. This supplies context without training the model or tracing a source block.

The local starter collection contains 13 named studies extracted from the supplied drawings. Ten have been visually curated and enabled; three ambiguous studies remain excluded. Initial model analyses sometimes misidentified the furniture, so approval is required before a new study is used. In **Reference library**, inspect the preview and notes, then use **Edit name / tags** to correct the name/category/tags and mark the study reviewed. Names aid retrieval but do not establish geometry or matching views.

## Prepare and install studies

`tools/BlockAI.ReferencePrepare` reads a private JSON manifest of candidates. Each candidate specifies `Source` (export JSON or PNG), `Group` (export group), optional `Name`, and optional normalized `Crop` rectangle `[x,y,width,height]`. Isolate one object before analysis. Run once with `--analyze` to create cached local vision analyses; inspect and correct their notes before approving them.

```powershell
.\.tools\dotnet\dotnet.exe run --project .\tools\BlockAI.ReferencePrepare -c Release -- --manifest artifacts-references/starter-manifest.json --library artifacts-references/prepared-library --analyze
.\.tools\dotnet\dotnet.exe run --project .\tools\BlockAI.ReferencePrepare -c Release -- --install-from artifacts-references/prepared-library
```

The manifest and third-party assets stay private and are not included in a repository checkout. The installed library lives in `%LOCALAPPDATA%\BlockAI\References`. The palette can also analyze an isolated PNG/JPG using the selected local review model. New analyses start unapproved. Existing user metadata is preserved during installation unless `--force` is supplied. Reanalysis is cached by image hash, not performed on each generation.

Enable **Use reference studies** in the palette. It displays selected study names or reports no match. Matching considers categories, names, tags, review status, confidence, and explicit no-cushion/no-arm constraints; it remains keyword based. Independent plan and elevation studies are not assumed to depict the same object. Replays retain selected semantic notes and provenance, not reference images. This small starter set cannot cover every request.

## Local DWG inventory tool

`tools/BlockAI.ReferenceExport` runs inside AutoCAD Core Console. It reads entity geometry and writes layout summaries, sampled-curve JSON and SVG overviews. It recursively explores block references and records group/layer information and readable text. It skips unsupported/degenerate entities and reports warnings. This is not a full DWG converter; hatches, dimensions and other objects may be absent.

Build the inspector with the same .NET 8 SDK used for the plugin:

```powershell
.\.tools\dotnet\dotnet.exe build .\tools\BlockAI.ReferenceExport\BlockAI.ReferenceExport.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\inspect-references.ps1 -Files 'C:\path\to\reference.dwg'
```

The script reads the named input and opens a copy in `artifacts-references/source-copies`, never the original drawing. Exported JSON/SVG and console logs are placed in `artifacts-references`. It temporarily trusts its specific loader folder in the headless AutoCAD process, restores the previous setting, and answers Yes to discarding drawing changes on exit. An ASCII temporary loader path avoids script encoding problems with Unicode workspace paths. It does not operate on the user's active AutoCAD document.

Keep third-party reference assets and derived exports local. The repository ignores `artifacts-*`; only the inspector code and this workflow are published. Preserve provenance and applicable permissions with any reference collection. A linked source category is useful for discovery, but it does not prove that a particular file may be redistributed or used for training.
