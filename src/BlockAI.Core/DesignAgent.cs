using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlockAI.Core;

public sealed record DesignResult(DesignPlan Plan,BlockSpec Block,int Attempts,bool VisuallyReviewed,BlockSpec? Front=null,BlockSpec? Side=null);
public sealed record VisualReview(bool Accepted,string[] Issues,int[]? PartIndices=null);

public sealed class DesignAgent(OllamaClient client)
{
    public const string DefaultModel="qwen3.5:4b";
    public async Task<DesignResult> GenerateAsync(string prompt,string model,string? reviewModel,
        Func<BlockSpec,byte[]>? render,IProgress<string>? progress,CancellationToken cancellation,DesignRecording? recording=null,
        bool elevations=false,Func<DesignViews,byte[]>? renderViews=null,IReadOnlyList<ReferenceLesson>? references=null,
        int maxAttempts=3,bool targetedRevisions=false,bool requireSymmetry=false,bool metricDraft=false,
        Func<ReferenceGeometry,byte[]>? renderReference=null,bool visualEdits=false,bool constraintLayout=false)
    {
        if(constraintLayout)
        {
            if(elevations)throw new ArgumentException("Relationship layouts currently support plan views only.");
            return await new ConstraintDesignAgent(client).GenerateAsync(prompt,model,reviewModel,render,progress,cancellation,
                recording,references,maxAttempts,requireSymmetry,renderReference,visualEdits);
        }
        var currentAttempt=0;
        try
        {
        recording?.Add(0,"started","Local part-based generation started.");
        if(string.IsNullOrWhiteSpace(prompt)||prompt.Length>4000) throw new ArgumentException("Use a description of 1–4000 characters.");
        if(reviewModel is not null&&render is null) throw new ArgumentException("Visual review requires a preview renderer.");
        if(maxAttempts is <1 or >10)throw new ArgumentOutOfRangeException(nameof(maxAttempts),"Use 1–10 attempts.");
        var symmetry=PlanSymmetry.Required(prompt,requireSymmetry);
        recording?.Add(0,"policy",$"Attempt limit: {maxAttempts}. Revisions: {(targetedRevisions?"part edits; unchanged parts preserved":"complete assemblies")}. Symmetry: {(symmetry?"left/right checked within 2 mm":"not required") }.");
        var selected=references?.Where(r=>r.Approved).Take(2).ToArray()??[];
        foreach(var lesson in selected)lesson.Validate();
        recording?.Add(0,"references",selected.Length==0?"No matching reference lessons supplied; generation uses the request and drawing tools.":"Reference analysis used: "+string.Join("; ",selected.Select(r=>r.Analysis.Name+(r.Geometry is null?" (notes)":$" ({r.Geometry.Outlines.Length} measured outlines + notes)"))),references:selected);
        var referenceContext=ReferenceLibrary.Context(selected);
        var feedback="";
        var previous="";
        string? rejectedGeometry=null;
        var visualCorrections="";
        var outstanding=new List<string>();
        DesignPlan? currentPlan=null;
        HashSet<int>? editableParts=null;
        var allowAdd=true;
        var blockingError="";
        var failedShapes=new Dictionary<string,int>();
        ConstructionFrame? retainedAssembly=null;DesignPlan? retainedBuiltPlan=null;
        byte[]? currentPreview=null;byte[]? referencePreview=null;
        var imageStudy=selected.FirstOrDefault(r=>r.Geometry is not null);
        if(imageStudy is not null&&renderReference is not null)
        {
            try{referencePreview=renderReference(imageStudy.Geometry!);recording?.Add(0,"reference-preview","Prepared generated reference-linework preview for study "+imageStudy.Id+". Image bytes are not recorded.");}
            catch(Exception ex) when(ex is not OperationCanceledException){recording?.Add(0,"reference-preview-skipped","Reference preview unavailable: "+ex.Message);}
        }
        for(var attempt=1;attempt<=maxAttempts;attempt++)
        {
            currentAttempt=attempt;
            cancellation.ThrowIfCancellationRequested();
            recording?.Add(attempt,"request",feedback.Length==0?"Requesting a part-based plan.":"Required corrections: "+feedback);
            progress?.Report(attempt==1?"Designing parts and their arrangement…":$"Editing design ({attempt}/{maxAttempts}): {feedback}");
            var editing=targetedRevisions&&currentPlan?.Parts is {Length:>=2 and <=24};
            var input=prompt+(editing?ReferenceLibrary.Context(selected,includeGeometry:false):referenceContext);
            if(symmetry)input+="\nREQUIRED: left/right geometric symmetry. Center major surfaces; use one mirrored definition for paired parts. Actual visible outlines will be checked within 2 mm. Do not invent extra parts merely to balance an incorrect layout.";
            if(editing)previous=metricDraft?MetricDesign.ToJson(currentPlan!):JsonSerializer.Serialize(currentPlan,BlockSpec.JsonOptions);
            if(editing)input+=DesignRevision.Measurements(currentPlan!);
            var toolImage=editing&&visualEdits?currentPreview:!editing?referencePreview:null;
            if(toolImage is not null)input+=editing?"\nThe image shows the CURRENT failed/rejected assembly to edit. Inspect its actual part placement before proposing adjustments; do not start again from a reference image."
                :"\nThe image shows normalized reference LINEWORK for study "+imageStudy!.Id+" (not the proposed design). Interpret its silhouette, connected seating runs, corners and negative space. It is guidance, not an outline to trace or a proof of physical dimensions.";
            if(editing&&editableParts is not null)input+="\nLOCKED ASSEMBLY: only these zero-based part indices may be replaced or removed: ["+string.Join(",",editableParts.Order())+"]. All other existing parts are locked. New parts "+(allowAdd?"may be added only to resolve the reported missing/connection/symmetry issue.":"are not allowed for this correction.");
            if(previous.Length>0) input+="\nPrevious plan:\n"+previous+"\nRequired corrections:\n"+feedback;
            else if(feedback.Length>0)input+="\nRequired corrections:\n"+feedback;
            try
            {
                var chairRequest=Regex.IsMatch(prompt,@"\b(?:chair|armchair)\b",RegexOptions.IgnoreCase);
                var vertical=chairRequest?DesignPlan.ElevationInstructions:DesignPlan.ElevationInstructions[..DesignPlan.ElevationInstructions.IndexOf("Example chair:",StringComparison.Ordinal)]+"\nFront projects X/Z looking from -Y; right side projects Y/Z looking from +X. Use the requested furniture's own construction; do not add chair parts.";
                var guidance=attempt==1?(elevations?vertical+"\n"+(chairRequest?DesignPlan.ElevationExample:""):chairRequest?DesignPlan.WorkedExample:"")
                    :(elevations?vertical+"\n":"")+"REVISE the previous assembly using the specific feedback. Change the named geometry/placement/depth or dimensions; renaming or rewriting the description is not a revision. Preserve parts that already satisfy the request. Do not restart from the worked example.";
                if(editing)guidance=(elevations?vertical+"\n":"")+DesignRevision.InstructionsFor(metricDraft);
                var system=metricDraft?MetricDesign.InstructionsFor(prompt)+(editing?"\n"+DesignRevision.InstructionsFor(true):""):DesignPlan.InstructionsFor(prompt)+"\n"+guidance;
                previous=await client.ToolJsonAsync(model,system,input,
                    editing?DesignRevision.SchemaFor(elevations,metricDraft):metricDraft?MetricDesign.SchemaFor(elevations):DesignPlan.SchemaFor(elevations),toolImage,cancellation);
            }
            catch(InvalidDataException ex) when(ex.Message.Contains("incomplete",StringComparison.OrdinalIgnoreCase))
            {
                feedback=editing?"The edit response exceeded the output limit. Return one or two compact replacements for the earliest blocking correction, with empty remove/add unless needed. The current assembly is preserved. "+visualCorrections
                    :"The previous response exceeded the output limit. Return a complete compact plan with at most 6 meaningful part definitions; use mirror/repetition and omit unrequested details. "+visualCorrections;
                recording?.Add(attempt,"validation-failed",ex.Message+" "+feedback);
                previous="";continue;
            }
            recording?.Add(attempt,"response","Designer returned JSON tool arguments.",raw:previous);
            DesignPlan plan;
            BlockSpec block;
            DesignViews? views=null;
            ConstructionFrame? lastAssembly=null;
            ConstructionFrame? lastFitted=null;
            var applied=false;
            try
            {
                var lockedParts=editing&&editableParts is not null?currentPlan!.Parts.Where((_,i)=>!editableParts.Contains(i)).ToArray():null;
                plan=editing?DesignRevision.Apply(currentPlan!,previous,editableParts,allowAdd,metricDraft):metricDraft?MetricDesign.Parse(previous,prompt):DesignPlan.Parse(previous);
                if(metricDraft&&!editing)recording?.Add(attempt,"coordinate-conversion","Converted explicit millimeter part boxes to normalized engine coordinates. Raw tool arguments retain the original millimeter fields.");
                currentPlan=plan;
                applied=true;
                if(lockedParts is not null)editableParts=plan.Parts.Select((p,i)=>(p,i)).Where(x=>!lockedParts.Any(p=>ReferenceEquals(p,x.p))).Select(x=>x.i).ToHashSet();
                if(editing)recording?.Add(attempt,"revision-applied","Applied indexed part edits to the previous assembly. Unmentioned parts and overall dimensions were preserved.");
                recording?.Add(attempt,"proposal","AI-proposed parts and their requested parameters. Bounding boxes are proposals, not constructed outlines.",plan:plan);
                progress?.Report("Building geometry and checking dimensions and constraints…");
                Action<ConstructionFrame> observe=frame=>{if(frame.Stage=="assembled")lastAssembly=frame;if(frame.Stage=="fitted"&&frame.View=="plan")lastFitted=frame;recording?.Add(attempt,frame.Stage,frame.Message,frame:frame);};
                if(elevations){views=ElevationBuilder.Build(plan,prompt,observe);block=views.Plan;}
                else block=plan.Build(prompt,observe,preserveScale:metricDraft);
                retainedAssembly=lastFitted??lastAssembly;retainedBuiltPlan=currentPlan;
                if(visualEdits&&render is not null)
                {try{currentPreview=render(block);}catch(Exception previewError) when(previewError is not OperationCanceledException){recording?.Add(attempt,"edit-preview-skipped",previewError.Message);}}
                if(symmetry)
                {
                    PlanSymmetry.Validate(block);
                    recording?.Add(attempt,"symmetry-passed","Visible plan outlines pass left/right reflection checks within 2 mm.");
                }
            }
            catch(Exception ex) when(ex is JsonException or InvalidDataException)
            {
                if(applied||blockingError.Length==0)blockingError=ex.Message;
                if(metricDraft&&targetedRevisions&&applied&&ex.Data["editableIndices"] is int[] affected&&affected.Length>0)
                {
                    var scope=affected.ToHashSet();if(editableParts is not null)scope.IntersectWith(editableParts);
                    if(scope.Count>0)editableParts=scope;
                    recording?.Add(attempt,"parts-locked","Geometry failure scoped to indices ["+string.Join(",",(editableParts??new HashSet<int>()).Order())+"]. Other existing parts remain locked.");
                }
                var stalled=editing&&!applied;
                if(lastAssembly is not null)
                {
                    retainedAssembly=lastFitted??lastAssembly;retainedBuiltPlan=currentPlan;
                    if(visualEdits&&render is not null)
                    {
                        try{currentPreview=render(new BlockSpec("Failed draft","Current constructed geometry",[],lastFitted?.Geometry.Select(g=>g.Geometry).ToArray()??lastAssembly.Geometry.Select(g=>g.Geometry with {Points=g.Geometry.Points.Select(p=>new Point2(p.X*currentPlan!.Width,p.Y*currentPlan.Depth)).ToArray()}).ToArray()));}
                        catch(Exception previewError) when(previewError is not OperationCanceledException){recording?.Add(attempt,"edit-preview-skipped",previewError.Message);}
                    }
                    var signature=GeometryFingerprint(new BlockSpec("Diagnostic","Constructed outlines",[],lastAssembly.Geometry.Select(g=>g.Geometry with {Points=g.Geometry.Points.Select(p=>new Point2(p.X*1000,p.Y*1000)).ToArray()}).ToArray()));
                    failedShapes.TryGetValue(signature,out var repeats);failedShapes[signature]=repeats+1;stalled=repeats>0;
                }
                feedback=blockingError+(ex.Message==blockingError?"":" Latest edit error: "+ex.Message)+" "+visualCorrections;
                if(stalled)
                {
                    feedback+=" The failed construction is unchanged or the edit was rejected. Use a focused millimeter adjustment against the current part measurements; do not resend identical parts. The original blocking error above is still unresolved.";
                    recording?.Add(attempt,"revision-stalled","No progress on failed construction; previous constructed outlines retained where available. "+feedback,
                        frame:retainedAssembly is not null&&ReferenceEquals(retainedBuiltPlan,currentPlan)?retainedAssembly with {Stage="revision-stalled",Message="Retained outlines from the last constructed assembly; the rejected patch produced no new geometry."}:null);
                }
                recording?.Add(attempt,"validation-failed",feedback);continue;
            }
            blockingError="";
            var geometryFingerprint=GeometryFingerprint(block,views?.Front,views?.Side);
            if(rejectedGeometry==geometryFingerprint)
            {
                feedback=visualCorrections+" The revised output has the same visible geometry (within 2 mm) as the rejected draft. Change the requested parts, shape, proportions, placement or depth. Changing names, descriptions or part order does not count.";
                recording?.Add(attempt,"revision-unchanged",feedback);continue;
            }
            if(rejectedGeometry is not null)recording?.Add(attempt,"revision-changed","Visible geometry differs from the previously rejected draft; the new drawing will be reviewed again.");
            if(reviewModel is not null)
            {
                var allViewsReviewed=views is not null&&renderViews is not null;
                recording?.Add(attempt,"review-start",allViewsReviewed?"Sending the generated plan/front/right-side sheet to the local reviewer. No images are stored in this recording.":"Sending only the generated plan preview to the local reviewer. No image is stored in this recording.");
                progress?.Report(allViewsReviewed?"Reviewing plan, front and side views…":"Reviewing the rendered plan…");
                var image=allViewsReviewed?renderViews!(views!):render!(block);
                var reviewText=await client.ToolJsonAsync(reviewModel,ReviewInstructions,
                    "Requested design (only this defines requirements): "+prompt+"\nDesigner description (unverified, not additional requirements): "+plan.Description+"\nActual footprint (engine measured, do not override by pixel guesses): "+block.Bounds().Width+" x "+block.Bounds().Height+" mm.\n"
                    +"Outstanding corrections to recheck against the CURRENT preview: "+(outstanding.Count==0?"none":string.Join("; ",outstanding))+". A previously reported issue can be mistaken; discard it if the preview or actual user request contradicts it. Do not require texture or other details the user did not request.\n"
                    +"Declared top-view order (higher is closer), with ZERO-BASED part indices: "+string.Join("; ",plan.Parts.Select((p,i)=>$"[{i}] {p.Role}: level={p.EffectiveLevel}, opaque={p.Opaque}"+(views is not null?$", z={p.Z}, height={p.Height} mm":"")))+".\n"
                    +(allViewsReviewed?"Image panels: PLAN at left, FRONT elevation at center, RIGHT SIDE elevation at right. Elevations share the assembly's dimensions and use vertical extrusion envelopes. Inspect each labeled view; do not confuse separate panels with overlapping furniture.\n":"Only the plan image is supplied; do not claim to have inspected elevations.\n")
                    +"Inspect the FINAL visible lines after depth trimming. Judge recognition, plan view, proportions, visible negative requirements and disconnected components. Intentional projection overlap is normal; do not reject it by itself. If hidden lines or depth are wrong, name the parts and suggest an above/below correction. Do not demand legs hidden by a seat.",
                    ReviewSchema,image,cancellation);
                recording?.Add(attempt,"review-response","Reviewer returned structured feedback.",raw:reviewText);
                VisualReview review;
                try {review=JsonSerializer.Deserialize<VisualReview>(reviewText,BlockSpec.JsonOptions)??throw new JsonException("Empty visual review.");}
                catch(JsonException ex) {throw new InvalidDataException("The visual reviewer returned an invalid review.",ex);}
                if(review.Issues is null||review.Issues.Length>8||review.Issues.Any(i=>i is null||i.Length>600))
                    throw new InvalidDataException("The visual reviewer returned invalid feedback.");
                if(review.PartIndices is not null&&(review.PartIndices.Length>24||review.PartIndices.Any(i=>i<0||i>=plan.Parts.Length)))throw new InvalidDataException("Reviewer named an invalid part index.");
                recording?.Add(attempt,"review",review.Accepted&&review.Issues.Length==0?"Reviewer approved the preview (subjective).":"Reviewer requested corrections.",review:review);
                if(!review.Accepted||review.Issues.Length>0)
                {
                    foreach(var issue in review.Issues.Length==0?new[]{"Design not approved. Improve recognition and proportions."}:review.Issues)
                        if(!outstanding.Contains(issue,StringComparer.OrdinalIgnoreCase))outstanding.Add(issue);
                    if(outstanding.Count>8)outstanding.RemoveRange(0,outstanding.Count-8);
                    visualCorrections="Outstanding visual corrections (must be rechecked before approval): "+string.Join("; ",outstanding.Select((s,i)=>$"{i+1}. {s}"));
                    if(targetedRevisions)
                    {
                        editableParts=review.PartIndices is {Length:>0}?review.PartIndices.ToHashSet():plan.Parts.Select((p,i)=>(p,i)).Where(x=>review.Issues.Any(s=>s.Contains(x.p.Role,StringComparison.OrdinalIgnoreCase))).Select(x=>x.i).ToHashSet();
                        allowAdd=Regex.IsMatch(string.Join(" ",review.Issues),@"missing|add\b|needs?\b|include|connect|symmetr|pair",RegexOptions.IgnoreCase);
                        recording?.Add(attempt,"parts-locked","Existing parts locked except indices ["+string.Join(",",editableParts.Order())+"]. "+(allowAdd?"Necessary additions allowed.":"Additions disabled for this correction."));
                    }
                    feedback=visualCorrections;rejectedGeometry=geometryFingerprint;continue;
                }
            }
            block=block with {Assumptions=block.Assumptions.Concat(new[]{
                $"Design attempts: {attempt}. "+(reviewModel is null?"Visual review disabled; appearance has not been reviewed by AI.":$"Preview reviewed by {reviewModel}. This is subjective feedback, not an accuracy guarantee.")}).ToArray()};
            if(reviewModel is null)recording?.Add(attempt,"review-skipped","Visual review disabled; appearance was not reviewed.");
            recording?.Finish("succeeded","Design offered for insertion.",attempt);
            return new(plan,block,attempt,reviewModel is not null,views?.Front,views?.Side);
        }
        throw new InvalidDataException($"Design did not pass checks after {maxAttempts} attempts. Last feedback: "+feedback);
        }
        catch(OperationCanceledException)
        {recording?.Finish("canceled","Generation canceled or timed out; no new design offered for insertion.",currentAttempt);throw;}
        catch(Exception ex)
        {recording?.Finish("failed",ex.Message,currentAttempt);throw;}
    }

    // Compare visible edges, independent of descriptions, entity order and polyline direction.
    private static string GeometryFingerprint(params BlockSpec?[] drawings)
    {
        string Point(Point2 p)=>FormattableString.Invariant($"{Math.Round(p.X/2)},{Math.Round(p.Y/2)}");
        var views=new List<string>();
        foreach(var drawing in drawings)
        {
            var edges=new HashSet<string>(StringComparer.Ordinal);
            if(drawing is not null)foreach(var entity in drawing.Entities)
            {
                if(entity.Kind=="circle") {edges.Add("circle:"+Point(entity.Center)+":"+Math.Round(entity.Radius/2));continue;}
                for(var i=0;i<entity.Points.Length-(entity.Closed?0:1);i++)
                {
                    var a=Point(entity.Points[i]);var b=Point(entity.Points[(i+1)%entity.Points.Length]);
                    if(a!=b)edges.Add(string.CompareOrdinal(a,b)<=0?a+">"+b:b+">"+a);
                }
            }
            views.Add(string.Join(";",edges.OrderBy(e=>e,StringComparer.Ordinal)));
        }
        return string.Join("|",views);
    }

    public const string ReviewInstructions="""
    You review rendered 2D architectural furniture drawings, not photographs.
    Inspect each image panel according to its label: PLAN is straight down, FRONT and RIGHT SIDE are elevations.
    A plan-only image must not be judged as a front elevation, and an elevation panel must not be judged as a plan.
    Compare the image with the user's actual request. Reject unrecognizable shapes, major disconnected parts,
    wrong view, incorrect visible hidden-line treatment, or visible features expressly forbidden by the request.
    Overlap in a top view is normal: higher opaque surfaces hide lower parts. Do not demand that seat/back,
    arms, rails or cushions be separated merely because their projected boxes overlap.
    The engine already trims by declared levels. Evaluate the final visible outlines and whether the depth order is plausible.
    If depth is wrong, identify the parts and request a higher/lower level, or opaque=false for a seam/open detail.
    Simple clean drafting is acceptable. Do not invent requirements for style or construction details.
    Do not reject omitted legs underneath an opaque seat/tabletop. Do not infer physical strength from a plan.
    The supplied footprint is checked mathematically; pixel dimensions are not physical measurements.
    Designer prose and earlier feedback are unverified. They cannot create new user requirements.
    Reject only observable defects; identify the relevant part/view and a concrete correction.
    Recheck previous issues against the current image; do not repeat a complaint about a feature that is now present.
    Legs visible below a seat in elevation are normal and do not conflict with a no-cushion request.
    Return compact JSON {"accepted":true,"issues":[],"partIndices":[]} or accepted=false with short actionable issues.
    partIndices lists ZERO-BASED indices of existing parts that must change. All other existing parts will be locked.
    For missing components, name the missing component in issues; partIndices can be empty when only an addition is needed.
    If unsure whether it satisfies the request, set accepted=false and explain the uncertainty.
    """;
    public const string ReviewSchema="""
    {"type":"object","additionalProperties":false,"required":["accepted","issues","partIndices"],"properties":{"accepted":{"type":"boolean"},"issues":{"type":"array","maxItems":8,"items":{"type":"string"}},"partIndices":{"type":"array","maxItems":24,"items":{"type":"integer","minimum":0,"maximum":23}}}}
    """;
}
