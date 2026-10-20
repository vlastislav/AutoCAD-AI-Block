using System.Text.Json;

namespace BlockAI.Core;

public sealed record DesignResult(DesignPlan Plan,BlockSpec Block,int Attempts,bool VisuallyReviewed);
public sealed record VisualReview(bool Accepted,string[] Issues);

public sealed class DesignAgent(OllamaClient client)
{
    public const string DefaultModel="qwen3.5:4b";
    public async Task<DesignResult> GenerateAsync(string prompt,string model,string? reviewModel,
        Func<BlockSpec,byte[]>? render,IProgress<string>? progress,CancellationToken cancellation,DesignRecording? recording=null)
    {
        var currentAttempt=0;
        try
        {
        recording?.Add(0,"started","Local part-based generation started.");
        if(string.IsNullOrWhiteSpace(prompt)||prompt.Length>4000) throw new ArgumentException("Use a description of 1–4000 characters.");
        if(reviewModel is not null&&render is null) throw new ArgumentException("Visual review requires a preview renderer.");
        var feedback="";
        var previous="";
        for(var attempt=1;attempt<=3;attempt++)
        {
            currentAttempt=attempt;
            cancellation.ThrowIfCancellationRequested();
            recording?.Add(attempt,"request",feedback.Length==0?"Requesting a part-based plan.":"Required corrections: "+feedback);
            progress?.Report(attempt==1?"Designing parts and their arrangement…":$"Revising design ({attempt}/3): {feedback}");
            var input=prompt;
            if(previous.Length>0) input+="\nPrevious plan:\n"+previous+"\nRequired corrections:\n"+feedback;
            previous=await client.ToolJsonAsync(model,DesignPlan.Instructions+"\n"+DesignPlan.WorkedExample,input,DesignPlan.Schema,null,cancellation);
            recording?.Add(attempt,"response","Designer returned JSON tool arguments.",raw:previous);
            DesignPlan plan;
            BlockSpec block;
            try
            {
                plan=DesignPlan.Parse(previous);
                recording?.Add(attempt,"proposal","AI-proposed parts and their requested parameters. Bounding boxes are proposals, not constructed outlines.",plan:plan);
                progress?.Report("Building geometry and checking dimensions and constraints…");
                block=plan.Build(prompt,recording is null?null:frame=>recording.Add(attempt,frame.Stage,frame.Message,frame:frame));
            }
            catch(Exception ex) when(ex is JsonException or InvalidDataException)
            {feedback=ex.Message;recording?.Add(attempt,"validation-failed",feedback);continue;}
            if(reviewModel is not null)
            {
                recording?.Add(attempt,"review-start","Sending only the generated plan preview to the selected local reviewer. The image is not stored in this recording.");
                progress?.Report("Reviewing the rendered plan…");
                var image=render!(block);
                var reviewText=await client.ToolJsonAsync(reviewModel,ReviewInstructions,
                    "Requested design: "+prompt+"\nDesigner description: "+plan.Description+"\nActual footprint: "+block.Bounds().Width+" x "+block.Bounds().Height+" mm.\n"
                    +"Inspect the image. Judge recognition, plan view, proportions, visible negative requirements and disconnected/overlapping components. Do not demand legs hidden by a seat.",
                    ReviewSchema,image,cancellation);
                recording?.Add(attempt,"review-response","Reviewer returned structured feedback.",raw:reviewText);
                VisualReview review;
                try {review=JsonSerializer.Deserialize<VisualReview>(reviewText,BlockSpec.JsonOptions)??throw new JsonException("Empty visual review.");}
                catch(JsonException ex) {throw new InvalidDataException("The visual reviewer returned an invalid review.",ex);}
                if(review.Issues is null||review.Issues.Length>8||review.Issues.Any(i=>i is null||i.Length>600))
                    throw new InvalidDataException("The visual reviewer returned invalid feedback.");
                recording?.Add(attempt,"review",review.Accepted&&review.Issues.Length==0?"Reviewer approved the preview (subjective).":"Reviewer requested corrections.",review:review);
                if(!review.Accepted||review.Issues.Length>0)
                {feedback="Visual review: "+(review.Issues.Length==0?"Design not approved. Improve recognition and proportions.":string.Join("; ",review.Issues));continue;}
            }
            block=block with {Assumptions=block.Assumptions.Concat(new[]{
                $"Design attempts: {attempt}. "+(reviewModel is null?"Visual review disabled; appearance has not been reviewed by AI.":$"Preview reviewed by {reviewModel}. This is subjective feedback, not an accuracy guarantee.")}).ToArray()};
            if(reviewModel is null)recording?.Add(attempt,"review-skipped","Visual review disabled; appearance was not reviewed.");
            recording?.Finish("succeeded","Design offered for insertion.",attempt);
            return new(plan,block,attempt,reviewModel is not null);
        }
        throw new InvalidDataException("Design did not pass checks after 3 attempts. Last feedback: "+feedback);
        }
        catch(OperationCanceledException)
        {recording?.Finish("canceled","Generation canceled or timed out; no new design offered for insertion.",currentAttempt);throw;}
        catch(Exception ex)
        {recording?.Finish("failed",ex.Message,currentAttempt);throw;}
    }

    public const string ReviewInstructions="""
    You review a rendered 2D architectural furniture PLAN, not a photo or front elevation.
    Compare the image with the user's actual request. Reject unrecognizable shapes, major disconnected parts,
    seat/back overlap, wrong view, or visible features expressly forbidden by the request.
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
