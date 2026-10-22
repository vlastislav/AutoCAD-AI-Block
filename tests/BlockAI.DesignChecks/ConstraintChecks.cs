using BlockAI.Core;
using BlockAI.AutoCAD;
using System.Net;
using System.Text.Json;

internal static class ConstraintChecks
{
    public static LayoutDraft Sofa(double width,double depth,bool right)
    {
        var run=depth*.4;
        return new("Anchored corner sofa","Demonstration of generic edge relationships",width,depth,
        [new("main","seat_long","footprint",1,run,"fraction","mm","left","top",0,0,true),
         new("return","seat_return","main",width*.35,depth-run,"mm","mm",right?"right":"left","below",0,0,false),
         new("back","back","main",1,80,"fraction","mm","left","top",0,0,true)]);
    }
    public static LayoutStyle Style=>new([new("main","rounded",.025,10,true,[]),new("return","rounded",.025,10,true,[]),new("back","rectangle",0,20,true,[])]);
    public static async Task Run(Action<bool,string> check)
    {
        void Reject(Action action,string name){try{action();check(false,name);}catch(InvalidDataException){check(true,name);}}
        foreach(var (w,d) in new[]{(2000d,1500d),(2400d,1800d),(3000d,2200d)})
        foreach(var right in new[]{false,true})
        {
            var layout=Sofa(w,d,right);var plan=ConstraintLayout.Solve(layout,Style);var block=plan.Build("L shaped sofa plan",preserveScale:true);
            var seat=plan.Parts[0];var ret=plan.Parts[1];
            check(Math.Abs((ret.Y+ret.H)-seat.Y)<1e-9&&Math.Abs((right?ret.X+ret.W:ret.X)-(right?seat.X+seat.W:seat.X))<1e-9,$"{w}x{d} {(right?"right":"left")} return remains attached by anchors");
            check(Math.Abs(block.Bounds().Width-w)<.01&&Math.Abs(block.Bounds().Height-d)<.01,"anchored extents preserve requested footprint without global stretching");
            File.WriteAllBytes($"artifacts-design/constraint-{w}-{d}-{(right?"right":"left")}.png",PlanRenderer.Png(block));
        }
        var baseline=Sofa(2400,1800,true);
        DesignPart Rect(string role,double y,double h)=>new(role,"rectangle",0,y,1,h,0,1,"none",0,false,"none",0,[]);
        var overlapping=new DesignPlan("Regions","Coincident rough regions with appearance still undecided",1000,600,[Rect("surface",0,1),Rect("frame",0,1)]);
        check(overlapping.Build("panel",preserveScale:true,layoutOnly:true).Entities.Length>0,"rough region overlap is allowed before depth/outline decisions");
        Reject(()=>overlapping.Build("panel",preserveScale:true),"final output still rejects conflicting duplicate outlines");
        var chair=new DesignPlan("Regions","Seat and rear back regions",500,550,[Rect("seat",0,.8),Rect("back",.8,.2)]);
        check(chair.Build("chair with oval seat",preserveScale:true,layoutOnly:true).Entities.Length>0,"rough rectangles do not prematurely fail a requested final curved outline");
        Reject(()=>chair.Build("chair with oval seat",preserveScale:true),"final oval-seat requirement remains enforced");
        using(var schema=JsonDocument.Parse(ConstraintLayout.Schema))
        {
            var targets=schema.RootElement.GetProperty("properties").GetProperty("boxes").GetProperty("items").GetProperty("properties").GetProperty("target").GetProperty("enum").EnumerateArray().Select(v=>v.GetString()).ToArray();
            check(targets.Contains("footprint")&&!targets.Contains("null")&&targets.Length==9,"model target tool permits footprint and stable part IDs, excluding invented null roots");
        }
        var resized=ConstraintLayout.Apply(baseline,"{\"edits\":[{\"id\":\"main\",\"field\":\"depth\",\"value\":800},{\"id\":\"return\",\"field\":\"depth\",\"value\":1000}]}");
        var solved=ConstraintLayout.Solve(resized);
        check(Math.Abs(solved.Parts[2].Y-solved.Parts[0].Y-solved.Parts[0].H+solved.Parts[2].H)<1e-9,"dependent back remains anchored after parent resize");
        // Serialization creates fresh records; definition equality, rather than object identity, is the lock guarantee.
        check(resized.Boxes[2]==baseline.Boxes[2],"repair preserves unmentioned box definition exactly");
        Reject(()=>ConstraintLayout.Apply(baseline,"{\"edits\":[{\"id\":\"back\",\"field\":\"depth\",\"value\":100}]}",new HashSet<string>{"main"}),"scalar edits enforce stable-ID locks");
        Reject(()=>ConstraintLayout.Apply(baseline,"{\"edits\":[{\"id\":\"back\",\"field\":\"depth\",\"value\":80}]}"),"identical relationship repairs are rejected");
        Reject(()=>ConstraintLayout.Solve(baseline with {Boxes=[baseline.Boxes[0] with {Target="return"},baseline.Boxes[1],baseline.Boxes[2]]}),"cyclic dependency graph rejected");
        Reject(()=>ConstraintLayout.Solve(baseline with {Boxes=[baseline.Boxes[0],baseline.Boxes[1] with {Target="missing"},baseline.Boxes[2]]}),"unknown anchor targets rejected");
        Reject(()=>ConstraintLayout.Solve(baseline with {Boxes=[baseline.Boxes[0],baseline.Boxes[1],baseline.Boxes[2] with {Depth=900}]}),"contained back cannot spill beyond seating region");
        Reject(()=>ConstraintLayout.Solve(baseline with {Boxes=[baseline.Boxes[0],baseline.Boxes[1] with {OffsetXmm=100},baseline.Boxes[2]]}),"out-of-footprint attachment rejected rather than clipped");
        Reject(()=>ConstraintLayout.Solve(baseline with {Boxes=[baseline.Boxes[0] with {WidthMode="unknown"},baseline.Boxes[1],baseline.Boxes[2]]}),"unknown size modes rejected");
        Reject(()=>ConstraintLayout.Solve(baseline,Style with {Parts=[Style.Parts[0] with {Id="unknown"},Style.Parts[1],Style.Parts[2]]}),"detail stage cannot add new parts outside locked layout");
        Reject(()=>ConstraintLayout.Solve(baseline,prompt:"sofa 3000 x 2200 mm"),"explicit request dimensions checked before details");

        var calls=0;var recording=new DesignRecording{Prompt="Right return L shaped sofa plan",Model="scripted",Demonstration=true};
        var agent=new DesignAgent(new OllamaClient(new HttpClient(new Handler(async(message,ct)=>
        {
            var body=await message.Content!.ReadAsStringAsync(ct);calls++;
            if(calls==1)return Reply(JsonSerializer.Serialize(baseline,BlockSpec.JsonOptions));
            if(calls==2){check(body.Contains("LOCKED layout"),"appearance stage receives solved layout and cannot move boxes");return Reply(JsonSerializer.Serialize(Style,BlockSpec.JsonOptions));}
            if(calls==3)return Reply("{\"accepted\":false,\"issues\":[\"Back is too shallow, increase to 100 mm\"],\"partIndices\":[2]}");
            if(calls==4)
            {
                check(body.Contains("CURRENT stable-ID layout")&&body.Contains("too shallow"),"review feedback is grounded in previous graph instead of a fresh design");
                return Reply("{\"edits\":[{\"id\":\"back\",\"field\":\"depth\",\"value\":100}],\"details\":[]}");
            }
            return Reply("{\"accepted\":true,\"issues\":[],\"partIndices\":[]}");
        }))));
        var result=await agent.GenerateAsync(recording.Prompt,"scripted","scripted",PlanRenderer.Png,null,default,recording,maxAttempts:3,constraintLayout:true);
        check(calls==5&&result.Attempts==2&&Math.Abs(result.Plan.Parts[2].H*1800-100)<.001,"review repairs the same anchored design without repeating detail generation");
        check(recording.Events.Any(e=>e.Stage=="layout-passed")&&recording.Events.Any(e=>e.Stage=="detail-response")&&recording.Status=="succeeded","replay separates layout construction, detail tools and subjective review");
        check(!recording.ToJson().Contains("base64")&&!recording.ToJson().Contains("images"),"constraint replay excludes rendered images");
        File.WriteAllText("artifacts-design/viewer-constraints.json",recording.ToJson());File.WriteAllText("artifacts-design/viewer-constraints.html",recording.ToHtml());
        File.WriteAllBytes("artifacts-design/constraint-demo.png",PlanRenderer.Png(result.Block));
        var invalid=baseline with {Boxes=[baseline.Boxes[0],baseline.Boxes[1] with {OffsetXmm=500},baseline.Boxes[2]]};
        var failed=new DesignRecording{Prompt="L shaped sofa plan",Model="scripted",Demonstration=true};
        var failAgent=new ConstraintDesignAgent(new OllamaClient(new HttpClient(new Handler((m,c)=>Task.FromResult(Reply(JsonSerializer.Serialize(invalid,BlockSpec.JsonOptions)))))));
        try{await failAgent.GenerateAsync(failed.Prompt,"scripted",null,null,null,default,failed,maxAttempts:1);check(false,"failed layout must not be offered for insertion");}
        catch(InvalidDataException){check(failed.Status=="failed"&&!failed.Events.Any(e=>e.Stage=="detail-response"),"failed rough layouts are recorded and never sent for detail/insertion");}
        var canceled=new DesignRecording{Prompt="sofa",Model="scripted"};using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{await failAgent.GenerateAsync("sofa","scripted",null,null,null,cancel.Token,canceled);check(false,"cancellation propagates");}
        catch(OperationCanceledException){check(canceled.Status=="canceled","canceled constraint attempt recorded");}
        var repairCalls=0;
        var feedbackAgent=new ConstraintDesignAgent(new OllamaClient(new HttpClient(new Handler(async(message,ct)=>
        {
            var body=await message.Content!.ReadAsStringAsync(ct);repairCalls++;
            if(repairCalls==1)return Reply(JsonSerializer.Serialize(invalid,BlockSpec.JsonOptions));
            if(repairCalls==2)return Reply("{\"edits\":[{\"id\":\"return\",\"field\":\"offsetXmm\",\"value\":0},{\"id\":\"main\",\"field\":\"widthMode\",\"value\":\"mm\"},{\"id\":\"main\",\"field\":\"width\",\"value\":2000}],\"details\":[]}");
            check(body.Contains("Metric assembly extents")&&!body.Contains("lies outside the footprint"),"resolved numeric failures are removed from later feedback");
            check(body.Contains("CURRENT PART BOXES IN MILLIMETERS"),"repair tools receive engine-resolved measurements, not only relationship definitions");
            return Reply("{\"edits\":[],\"details\":[]}");
        }))));
        try{await feedbackAgent.GenerateAsync("L shaped sofa plan","scripted",null,null,null,default,maxAttempts:3);check(false,"exhausted constraint repairs must fail");}
        catch(InvalidDataException){check(repairCalls==3,"numeric failure repair test remains bounded");}
    }
    private static HttpResponseMessage Reply(string text)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{done=true,response=text}))};
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handler):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken cancellation)=>handler(message,cancellation);}
}
