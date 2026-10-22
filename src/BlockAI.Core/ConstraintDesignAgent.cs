using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlockAI.Core;

/// <summary>Experimental two-stage designer: relationships first, appearance inside locked boxes second.</summary>
public sealed class ConstraintDesignAgent(OllamaClient client)
{
    public async Task<DesignResult> GenerateAsync(string prompt,string model,string? reviewModel,
        Func<BlockSpec,byte[]>? render,IProgress<string>? progress,CancellationToken cancellation,
        DesignRecording? recording=null,IReadOnlyList<ReferenceLesson>? references=null,int maxAttempts=6,
        bool requireSymmetry=false,Func<ReferenceGeometry,byte[]>? renderReference=null,bool visualEdits=false)
    {
        var attempt=0;
        try
        {
            if(string.IsNullOrWhiteSpace(prompt)||prompt.Length>4000)throw new ArgumentException("Use a request of 1–4000 characters.");
            if(maxAttempts is <1 or >10)throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            if(reviewModel is not null&&render is null)throw new ArgumentException("Visual review needs a renderer.");
            var selected=references?.Where(r=>r.Approved).Take(2).ToArray()??[];
            foreach(var r in selected)r.Validate();
            recording?.Add(0,"started","Experimental relationship layout: rough boxes before details; no stored furniture blocks.");
            recording?.Add(0,"references","Reference studies supplied to layout designer.",references:selected);
            recording?.Add(0,"policy","Mathematical layout failures block construction. Visual reviews are subjective. Repairs preserve stable box IDs and omitted definitions; dependents follow changed anchors.");
            LayoutDraft? layout=null;LayoutStyle? style=null;HashSet<string>? editable=null;
            var feedback="";var numericError="";var visualFeedback="";byte[]? image=null;
            var study=selected.FirstOrDefault(r=>r.Geometry is not null);
            if(study is not null&&renderReference is not null)
                try{image=renderReference(study.Geometry!);}catch(Exception ex) when(ex is not OperationCanceledException){recording?.Add(0,"reference-preview-skipped",ex.Message);}
            string? rejected=null;
            for(attempt=1;attempt<=maxAttempts;attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                var editing=layout is not null;
                var changedThisAttempt=false;
                progress?.Report(editing?$"Repairing anchored layout ({attempt}/{maxAttempts})…":"Designing connected layout regions…");
                var input=prompt+(editing?"\nCURRENT stable-ID layout:\n"+JsonSerializer.Serialize(layout,BlockSpec.JsonOptions)+
                    "\nCURRENT detail definitions:\n"+JsonSerializer.Serialize(style,BlockSpec.JsonOptions)+"\nREQUIRED CORRECTIONS: "+feedback+
                    "\nEditable IDs: "+(editable is null?"all":string.Join(",",editable))+". Moving a target propagates to its dependents; do not individually move every child."
                    :ReferenceLibrary.Context(selected));
                if(editing)
                    try{input+=DesignRevision.Measurements(ConstraintLayout.Solve(layout!,prompt:prompt));}
                    catch(InvalidDataException){ /* The current error already identifies an unresolved relationship. */ }
                if(PlanSymmetry.Required(prompt,requireSymmetry))input+="\nRequire left/right geometric symmetry; centered anchors and equal sizes can express it. Intentional L-shaped/corner requests are exempt.";
                if(image is not null)input+=editing?"\nImage: current constructed design to repair.":"\nImage: measured reference linework to interpret, not copy. Source physical units unknown.";
                recording?.Add(attempt,"request",editing?"Requesting scalar relationship edits and optional appearance repairs: "+feedback:"Requesting a rough relationship layout, without decorative geometry.");
                try
                {
                    var raw=await client.ToolJsonAsync(model,ConstraintLayout.Instructions+(editing?RepairInstructions:""),input,editing?RepairSchema:ConstraintLayout.Schema,image,cancellation);
                    recording?.Add(attempt,"response","Observable layout tool response.",raw:raw);
                    if(!editing)
                    {
                        var candidate=ConstraintLayout.Parse(raw);ConstraintLayout.ValidateHeader(candidate,prompt);layout=candidate;changedThisAttempt=true;
                    }
                    else
                    {
                        var node=JsonNode.Parse(raw)??throw new InvalidDataException("Empty repairs.");
                        if(node is not JsonObject o||o.Count!=2||node["edits"] is not JsonArray edits||node["details"] is not JsonArray details||details.Count>3)
                            throw new InvalidDataException("Return edits and details arrays only (up to 3 detail replacements).");
                        var next=edits.Count==0?layout!:ConstraintLayout.Apply(layout!,new JsonObject{["edits"]=edits.DeepClone()}.ToJsonString(),editable);
                        var nextStyle=style;
                        if(details.Count>0)
                        {
                            var replacements=JsonSerializer.Deserialize<BoxAppearance[]>(details.ToJsonString(),BlockSpec.JsonOptions)??[];
                            if(replacements.Select(p=>p.Id).Distinct().Count()!=replacements.Length||replacements.Any(p=>!next.Boxes.Any(b=>b.Id==p.Id)||editable is not null&&!editable.Contains(p.Id)))
                                throw new InvalidDataException("Detail edit uses unknown, duplicate or locked IDs.");
                            var defaults=style?.Parts??next.Boxes.Select(b=>new BoxAppearance(b.Id,"rectangle",0,10,true,[])).ToArray();
                            nextStyle=new(defaults.Select(p=>replacements.FirstOrDefault(r=>r.Id==p.Id)??p).ToArray());
                        }
                        if(JsonSerializer.Serialize(next,BlockSpec.JsonOptions)==JsonSerializer.Serialize(layout,BlockSpec.JsonOptions)&&
                            JsonSerializer.Serialize(nextStyle,BlockSpec.JsonOptions)==JsonSerializer.Serialize(style,BlockSpec.JsonOptions))
                            throw new InvalidDataException("No parameters changed; prior layout retained.");
                        layout=next;style=nextStyle;changedThisAttempt=true;
                        recording?.Add(attempt,"revision-applied","Stable-ID edits applied. Omitted definitions preserved; linked positions/sizes recalculated.");
                    }
                    var rough=ConstraintLayout.Solve(layout!,prompt:prompt);
                    recording?.Add(attempt,"layout-solved","Engine solved anchors and sizes in millimeters. Boxes are rough layout regions, not final outlines.",plan:rough,layout:layout);
                    var roughBlock=rough.Build(prompt,f=>recording?.Add(attempt,f.Stage,"ROUGH LAYOUT: "+f.Message,frame:f),preserveScale:true,layoutOnly:true);
                    recording?.Add(attempt,"layout-passed","Rough layout passed numeric footprint, containment and assembly checks. This is not visual approval.");
                    // Appearance is requested only after the rough assembly passes. It cannot change any box dimensions or anchors.
                    if(style is null)
                    {
                        progress?.Report("Adding outlines inside the solved layout…");
                        var detailInput=prompt+"\nLOCKED layout:\n"+JsonSerializer.Serialize(layout,BlockSpec.JsonOptions)+DesignRevision.Measurements(rough)+
                            "\nReturn each ID exactly once. Do not add/remove parts. Rectangle/rounded usually needs points=[]. detail=corner radius fraction 0..0.45. level -100..100; higher opaque closed parts hide lower edges. Keep simple useful outlines; no decoration not requested. Polygon/path points are local 0..1 fractions; polygons must retain box extents. Never draw elevations.";
                        var rawStyle=await client.ToolJsonAsync(model,"Apply appearance ONLY within the locked top-view layout boxes. Geometry engine controls positions and sizes. Return compact JSON.",detailInput,ConstraintLayout.DetailSchema,visualEdits&&render is not null?SafeRender(roughBlock):null,cancellation);
                        recording?.Add(attempt,"detail-response","Appearance tool arguments for locked regions.",raw:rawStyle);
                        var candidate=JsonSerializer.Deserialize<LayoutStyle>(rawStyle,BlockSpec.JsonOptions)??throw new InvalidDataException("Missing appearance definitions.");
                        ConstraintLayout.Solve(layout!,candidate,prompt);style=candidate;
                    }
                    var plan=ConstraintLayout.Solve(layout!,style,prompt);
                    recording?.Add(attempt,"proposal","Appearance applied to engine-positioned regions.",plan:plan,layout:layout);
                    var block=plan.Build(prompt,f=>recording?.Add(attempt,f.Stage,f.Message,frame:f),preserveScale:true);
                    if(PlanSymmetry.Required(prompt,requireSymmetry))PlanSymmetry.Validate(block);
                    image=visualEdits&&render is not null?SafeRender(block):null;
                    var signature=JsonSerializer.Serialize(block.Entities,BlockSpec.JsonOptions);
                    if(signature==rejected)throw new InvalidDataException("Visible geometry is unchanged from the rejected design. Change the relevant relationship, size or appearance.");
                    numericError="";
                    if(reviewModel is not null)
                    {
                        progress?.Report("Reviewing final visible plan…");
                        var rawReview=await client.ToolJsonAsync(reviewModel,DesignAgent.ReviewInstructions,
                            "USER REQUEST: "+prompt+"\nEngine-measured footprint mm: "+block.Bounds().Width+" x "+block.Bounds().Height+
                            "\nPart indices and stable IDs: "+string.Join("; ",layout!.Boxes.Select((b,i)=>$"[{i}] {b.Id}: {b.Role}"))+
                            "\nEarlier feedback is unverified; discard complaints contradicted by this current image: "+feedback+
                            "\nPlan only. Judge visible geometry, not the designer prose. Intentional overlap is handled by hidden-line trimming, not an error by itself.",DesignAgent.ReviewSchema,render!(block),cancellation);
                        recording?.Add(attempt,"review-response","Subjective reviewer response.",raw:rawReview);
                        var review=JsonSerializer.Deserialize<VisualReview>(rawReview,BlockSpec.JsonOptions)??throw new InvalidDataException("Empty review.");
                        if(review.Issues is null||review.Issues.Length>8||review.Issues.Any(i=>i is null||i.Length>600)||review.PartIndices is null||review.PartIndices.Any(i=>i<0||i>=layout!.Boxes.Length))throw new InvalidDataException("Invalid reviewer feedback/part indices.");
                        recording?.Add(attempt,"review","Subjective visual assessment; separate from mathematical checks.",review:review);
                        if(!review.Accepted||review.Issues.Length>0)
                        {
                            visualFeedback=string.Join("; ",review.Issues);if(visualFeedback.Length==0)visualFeedback="Reviewer did not approve; inspect recognition and proportions.";
                            feedback=visualFeedback;
                            // Unlock only named boxes AND their dependency ancestors, since a child's position is defined by its target.
                            editable=review.PartIndices.Length==0?null:review.PartIndices.Select(i=>layout!.Boxes[i].Id).ToHashSet();
                            if(editable is not null)
                            {
                                foreach(var id in editable.ToArray())
                                {
                                    var b=layout!.Boxes.First(x=>x.Id==id);var seen=new HashSet<string>();
                                    while(b.Target!="footprint"&&seen.Add(b.Target)){editable.Add(b.Target);b=layout.Boxes.First(x=>x.Id==b.Target);}
                                }
                            }
                            rejected=signature;recording?.Add(attempt,"parts-locked","Editable stable IDs including target ancestors: "+(editable is null?"all (review did not identify parts)":string.Join(",",editable)));continue;
                        }
                    }
                    else recording?.Add(attempt,"review-skipped","No visual review; numeric checks do not guarantee appearance.");
                    block=block with {Assumptions=block.Assumptions.Concat(["Experimental relationship layout; mathematical constraints do not guarantee furniture accuracy.",reviewModel is null?"Visual review disabled.":"Subjective visual review by "+reviewModel]).ToArray()};
                    recording?.Finish("succeeded","Constraint design offered for insertion.",attempt);
                    return new(plan,block,attempt,reviewModel is not null);
                }
                catch(Exception ex) when(ex is JsonException or InvalidDataException)
                {
                    if(changedThisAttempt||numericError.Length==0)numericError=ex.Message;
                    feedback=(visualFeedback.Length==0?"":visualFeedback+"\n")+"Current numeric/tool error: "+numericError+
                        (numericError==ex.Message?"":" Latest edit error (original blocker retained): "+ex.Message);
                    recording?.Add(attempt,"validation-failed",feedback);
                    if(layout is not null)
                        try
                        {
                            var rough=ConstraintLayout.Solve(layout,style,prompt);
                            if(visualEdits&&render is not null)image=SafeRender(rough.Build("layout diagnostic",preserveScale:true,layoutOnly:true));
                        }
                        catch(Exception previewError) when(previewError is not OperationCanceledException){recording?.Add(attempt,"edit-preview-skipped",previewError.Message);image=null;}
                }
            }
            throw new InvalidDataException($"Constraint design did not pass after {maxAttempts} attempts. "+feedback);
        }
        catch(OperationCanceledException){recording?.Finish("canceled","Constraint generation canceled; no design offered.",attempt);throw;}
        catch(Exception ex){recording?.Finish("failed",ex.Message,Math.Min(attempt,maxAttempts));throw;}
        byte[]? SafeRender(BlockSpec block)
        {
            try{return render!(block);}catch(Exception ex) when(ex is not OperationCanceledException){recording?.Add(attempt,"edit-preview-skipped",ex.Message);return null;}
        }
    }
    public static string RepairSchema
    {
        get
        {
            var root=JsonNode.Parse(ConstraintLayout.EditSchema)!;root["properties"]!["edits"]!["minItems"]=0;
            root["properties"]!["details"]=JsonNode.Parse(ConstraintLayout.DetailSchema)!["properties"]!["parts"]!.DeepClone();
            root["properties"]!["details"]!["minItems"]=0;root["properties"]!["details"]!["maxItems"]=3;
            root["required"]!.AsArray().Add("details");return root.ToJsonString();
        }
    }
    private const string RepairInstructions="""

    Return {"edits":[{"id":"existingID","field":"offsetXmm","value":0}],"details":[]}.
    Edits: 0–8 scalar relationship field changes. details: 0–3 complete appearance replacements using existing IDs, shape,detail,level,opaque,points. Use details only when outline/depth, rather than placement, is wrong. Empty arrays only if another array makes a real change. All omitted boxes and appearances stay fixed. No role/name changes, no new IDs, no full redesign. Numeric constraints are authoritative; visual feedback is fallible.
    """;
}
