using System.Text.Json;

namespace BlockAI.Core;

public sealed record DesignResult(DesignPlan Plan,BlockSpec Block,int Attempts,bool VisuallyReviewed,BlockSpec? Front=null,BlockSpec? Side=null);
public sealed record VisualReview(bool Accepted,string[] Issues);

public sealed class DesignAgent(OllamaClient client)
{
    public const string DefaultModel="qwen3.5:4b";
    public async Task<DesignResult> GenerateAsync(string prompt,string model,string? reviewModel,
        Func<BlockSpec,byte[]>? render,IProgress<string>? progress,CancellationToken cancellation,DesignRecording? recording=null,
        bool elevations=false,Func<DesignViews,byte[]>? renderViews=null,IReadOnlyList<ReferenceLesson>? references=null)
    {
        var currentAttempt=0;
        try
        {
        recording?.Add(0,"started","Local part-based generation started.");
        if(string.IsNullOrWhiteSpace(prompt)||prompt.Length>4000) throw new ArgumentException("Use a description of 1–4000 characters.");
        if(reviewModel is not null&&render is null) throw new ArgumentException("Visual review requires a preview renderer.");
        var selected=references?.Where(r=>r.Approved).Take(2).ToArray()??[];
        foreach(var lesson in selected)lesson.Validate();
        recording?.Add(0,"references",selected.Length==0?"No matching reference lessons supplied; generation uses the request and drawing tools.":"Reference analysis used: "+string.Join("; ",selected.Select(r=>r.Analysis.Name)),references:selected);
        var referenceContext=ReferenceLibrary.Context(selected);
        var feedback="";
        var previous="";
        string? rejectedGeometry=null;
        var visualCorrections="";
        for(var attempt=1;attempt<=3;attempt++)
        {
            currentAttempt=attempt;
            cancellation.ThrowIfCancellationRequested();
            recording?.Add(attempt,"request",feedback.Length==0?"Requesting a part-based plan.":"Required corrections: "+feedback);
            progress?.Report(attempt==1?"Designing parts and their arrangement…":$"Revising design ({attempt}/3): {feedback}");
            var input=prompt+referenceContext;
            if(previous.Length>0) input+="\nPrevious plan:\n"+previous+"\nRequired corrections:\n"+feedback;
            else if(feedback.Length>0)input+="\nRequired corrections:\n"+feedback;
            try
            {
                var guidance=attempt==1?(elevations?DesignPlan.ElevationInstructions+"\n"+DesignPlan.ElevationExample:DesignPlan.WorkedExample)
                    :(elevations?DesignPlan.ElevationInstructions+"\n":"")+"REVISE the previous assembly using the specific feedback. Change the named geometry/placement/depth or dimensions; renaming or rewriting the description is not a revision. Preserve parts that already satisfy the request. Do not restart from the worked example.";
                previous=await client.ToolJsonAsync(model,DesignPlan.Instructions+"\n"+guidance,input,DesignPlan.SchemaFor(elevations),null,cancellation);
            }
            catch(InvalidDataException ex) when(ex.Message.Contains("incomplete",StringComparison.OrdinalIgnoreCase))
            {
                feedback="The previous response exceeded the output limit. Return a complete compact plan with at most 6 meaningful part definitions; use mirror/repetition and omit unrequested details. "+visualCorrections;
                recording?.Add(attempt,"validation-failed",ex.Message+" "+feedback);
                previous="";continue;
            }
            recording?.Add(attempt,"response","Designer returned JSON tool arguments.",raw:previous);
            DesignPlan plan;
            BlockSpec block;
            DesignViews? views=null;
            try
            {
                plan=DesignPlan.Parse(previous);
                recording?.Add(attempt,"proposal","AI-proposed parts and their requested parameters. Bounding boxes are proposals, not constructed outlines.",plan:plan);
                progress?.Report("Building geometry and checking dimensions and constraints…");
                Action<ConstructionFrame>? observe=recording is null?null:frame=>recording.Add(attempt,frame.Stage,frame.Message,frame:frame);
                if(elevations){views=ElevationBuilder.Build(plan,prompt,observe);block=views.Plan;}
                else block=plan.Build(prompt,observe);
            }
            catch(Exception ex) when(ex is JsonException or InvalidDataException)
            {feedback=ex.Message+" "+visualCorrections;recording?.Add(attempt,"validation-failed",feedback);continue;}
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
                    "Requested design: "+prompt+"\nDesigner description: "+plan.Description+"\nActual footprint: "+block.Bounds().Width+" x "+block.Bounds().Height+" mm.\n"
                    +"Declared top-view order (higher is closer): "+string.Join("; ",plan.Parts.Select(p=>$"{p.Role}: level={p.EffectiveLevel}, opaque={p.Opaque}"+(views is not null?$", z={p.Z}, height={p.Height} mm":"")))+".\n"
                    +(allViewsReviewed?"Image panels: PLAN at left, FRONT elevation at center, RIGHT SIDE elevation at right. Elevations share the assembly's dimensions and use vertical extrusion envelopes. Inspect each labeled view; do not confuse separate panels with overlapping furniture.\n":"Only the plan image is supplied; do not claim to have inspected elevations.\n")
                    +"Inspect the FINAL visible lines after depth trimming. Judge recognition, plan view, proportions, visible negative requirements and disconnected components. Intentional projection overlap is normal; do not reject it by itself. If hidden lines or depth are wrong, name the parts and suggest an above/below correction. Do not demand legs hidden by a seat.",
                    ReviewSchema,image,cancellation);
                recording?.Add(attempt,"review-response","Reviewer returned structured feedback.",raw:reviewText);
                VisualReview review;
                try {review=JsonSerializer.Deserialize<VisualReview>(reviewText,BlockSpec.JsonOptions)??throw new JsonException("Empty visual review.");}
                catch(JsonException ex) {throw new InvalidDataException("The visual reviewer returned an invalid review.",ex);}
                if(review.Issues is null||review.Issues.Length>8||review.Issues.Any(i=>i is null||i.Length>600))
                    throw new InvalidDataException("The visual reviewer returned invalid feedback.");
                recording?.Add(attempt,"review",review.Accepted&&review.Issues.Length==0?"Reviewer approved the preview (subjective).":"Reviewer requested corrections.",review:review);
                if(!review.Accepted||review.Issues.Length>0)
                {visualCorrections="Visual review: "+(review.Issues.Length==0?"Design not approved. Improve recognition and proportions.":string.Join("; ",review.Issues));feedback=visualCorrections;rejectedGeometry=geometryFingerprint;continue;}
            }
            block=block with {Assumptions=block.Assumptions.Concat(new[]{
                $"Design attempts: {attempt}. "+(reviewModel is null?"Visual review disabled; appearance has not been reviewed by AI.":$"Preview reviewed by {reviewModel}. This is subjective feedback, not an accuracy guarantee.")}).ToArray()};
            if(reviewModel is null)recording?.Add(attempt,"review-skipped","Visual review disabled; appearance was not reviewed.");
            recording?.Finish("succeeded","Design offered for insertion.",attempt);
            return new(plan,block,attempt,reviewModel is not null,views?.Front,views?.Side);
        }
        throw new InvalidDataException("Design did not pass checks after 3 attempts. Last feedback: "+feedback);
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
    Return compact JSON {"accepted":true,"issues":[]} or accepted=false with short actionable issues.
    If unsure whether it satisfies the request, set accepted=false and explain the uncertainty.
    """;
    public const string ReviewSchema="""
    {"type":"object","additionalProperties":false,"required":["accepted","issues"],"properties":{"accepted":{"type":"boolean"},"issues":{"type":"array","maxItems":8,"items":{"type":"string"}}}}
    """;
}
