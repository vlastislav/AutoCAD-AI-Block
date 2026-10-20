using System.Text.Json;

namespace BlockAI.Core;

public sealed record DesignResult(DesignPlan Plan,BlockSpec Block,int Attempts,bool VisuallyReviewed);
public sealed record VisualReview(bool Accepted,string[] Issues);

public sealed class DesignAgent(OllamaClient client)
{
    public const string DefaultModel="qwen3.5:4b";
    public async Task<DesignResult> GenerateAsync(string prompt,string model,string? reviewModel,
        Func<BlockSpec,byte[]>? render,IProgress<string>? progress,CancellationToken cancellation)
    {
        if(string.IsNullOrWhiteSpace(prompt)||prompt.Length>4000) throw new ArgumentException("Use a description of 1–4000 characters.");
        if(reviewModel is not null&&render is null) throw new ArgumentException("Visual review requires a preview renderer.");
        var feedback="";
        var previous="";
        for(var attempt=1;attempt<=3;attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(attempt==1?"Designing parts and their arrangement…":$"Revising design ({attempt}/3): {feedback}");
            var input=prompt;
            if(previous.Length>0) input+="\nPrevious plan:\n"+previous+"\nRequired corrections:\n"+feedback;
            previous=await client.ToolJsonAsync(model,DesignPlan.Instructions+"\n"+DesignPlan.WorkedExample,input,DesignPlan.Schema,null,cancellation);
            DesignPlan plan;
            BlockSpec block;
            try
            {
                plan=DesignPlan.Parse(previous);
                progress?.Report("Building geometry and checking dimensions and constraints…");
                block=plan.Build(prompt);
            }
            catch(Exception ex) when(ex is JsonException or InvalidDataException)
            {feedback=ex.Message;continue;}
            if(reviewModel is not null)
            {
                progress?.Report("Reviewing the rendered plan…");
                var image=render!(block);
                var reviewText=await client.ToolJsonAsync(reviewModel,ReviewInstructions,
                    "Requested design: "+prompt+"\nDesigner description: "+plan.Description+"\nActual footprint: "+block.Bounds().Width+" x "+block.Bounds().Height+" mm.\n"
                    +"Inspect the image. Judge recognition, plan view, proportions, visible negative requirements and disconnected/overlapping components. Do not demand legs hidden by a seat.",
                    ReviewSchema,image,cancellation);
                VisualReview review;
                try {review=JsonSerializer.Deserialize<VisualReview>(reviewText,BlockSpec.JsonOptions)??throw new JsonException("Empty visual review.");}
                catch(JsonException ex) {throw new InvalidDataException("The visual reviewer returned an invalid review.",ex);}
                if(review.Issues is null||review.Issues.Length>8||review.Issues.Any(i=>i is null||i.Length>600))
                    throw new InvalidDataException("The visual reviewer returned invalid feedback.");
                if(!review.Accepted||review.Issues.Length>0)
                {feedback="Visual review: "+(review.Issues.Length==0?"Design not approved. Improve recognition and proportions.":string.Join("; ",review.Issues));continue;}
            }
            block=block with {Assumptions=block.Assumptions.Concat(new[]{
                $"Design attempts: {attempt}. "+(reviewModel is null?"Visual review disabled; appearance has not been reviewed by AI.":$"Preview reviewed by {reviewModel}. This is subjective feedback, not an accuracy guarantee.")}).ToArray()};
            return new(plan,block,attempt,reviewModel is not null);
        }
        throw new InvalidDataException("Design did not pass checks after 3 attempts. Last feedback: "+feedback);
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
