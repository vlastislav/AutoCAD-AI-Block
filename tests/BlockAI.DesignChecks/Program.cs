using BlockAI.Core;
using BlockAI.AutoCAD;
using System.Net;
using System.Text.Json;
using System.IO;

var passed=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
void Reject(Action action,string name){try{action();}catch(InvalidDataException){Check(true,name);return;}throw new Exception("Expected rejection: "+name);}
DesignPart Part(string role,string shape,double x,double y,double w,double h,double detail=0)=>new(role,shape,x,y,w,h,detail,1,"none",0,false,"none",0,[]);
var seat=Part("seat","trapezoid",.1,0,.8,.65,.12);
var back=Part("backrest","rounded",0,.85,1,.15,.15);
var support=Part("support","rectangle",.15,.6,.045,.3) with {Mirror=true};
var plan=new DesignPlan("CurvedDiningChair","Tapered solid seat and separate backrest",500,550,[seat,back,support]);
const string request="Dining chair, no cushion, 500 x 550 mm";
var block=plan.Build(request);
Check(block.Bounds().Width==500&&block.Bounds().Height==550,"exact requested footprint");
Check(block.Entities.Length>=4,"mirrored supports assembled and hidden edges clipped without a chair template");
Check(block.Entities[1].Points.Length>8,"rounded tool uses sampled arcs rather than chamfers");
Check(block.Assumptions[0].Contains("no furniture template"),"provenance identifies autonomous plan");
var imperial=plan.Build("dining chair 20 x 22 inches");
Check(Math.Abs(imperial.Bounds().Width-508)<.00001&&Math.Abs(imperial.Bounds().Height-558.8)<.00001,"explicit imperial dimensions override model guesses");
var encoded=JsonSerializer.Serialize(plan,BlockSpec.JsonOptions);
Check(DesignPlan.Parse(encoded).Build(request).Entities.Length==block.Entities.Length,"design DSL roundtrip");
Reject(()=> (plan with {Parts=[seat,back,Part("cushion","rounded",.2,.1,.6,.4,.1)]}).Build(request),"forbidden cushion rejected");
Reject(()=> (plan with {Parts=[seat with {Inset=.05},back]}).Build(request),"seat pad inset rejected for no cushion");
Reject(()=> (plan with {Parts=[seat,back,Part("armrest","rectangle",0,.2,.05,.4)]}).Build(request+", no arms"),"forbidden armrest rejected");
Reject(()=> (plan with {Parts=[seat]}).Build(request),"incomplete design rejected");
Reject(()=> (plan with {Parts=[seat,back with {Role="ornament"}]}).Build(request),"chair missing backrest rejected");
Reject(()=> (plan with {Parts=[seat,back with {Y=.4,H=.2}]}).Build(request),"seat/back overlap rejected");
Reject(()=> (plan with {Parts=[seat with {H=.1},back]}).Build(request),"front-elevation seat proportions rejected");
Reject(()=> (plan with {Parts=[seat,back]}).Build(request),"floating backrest rejected");
Reject(()=> (plan with {Parts=[seat,back,seat with {Role="frame"}]}).Build(request),"duplicate outlines rejected");
Reject(()=> (plan with {Parts=[seat with {X=double.NaN},back]}).Build(request),"NaN part coordinates rejected");
Reject(()=> (plan with {Parts=[seat with {X=.7},back]}).Build(request),"out-of-bounds part rejected");
Reject(()=> (plan with {Parts=[seat with {Shape="script"},back]}).Build(request),"unsupported tools cannot execute code");
Reject(()=> (plan with {Parts=[seat with {Count=8,Repeat="row",Gap=.2},back]}).Build(request),"impossible repetition rejected");
Reject(()=> (plan with {Parts=[seat with {Shape="polygon",Points=[new(0,0),new(.5,0),new(1,0)]},back]}).Build(request),"zero-area polygons rejected");
var overflow=plan with {Parts=[Part("surface","rectangle",-.2,0,1.4,.8),Part("rail","rectangle",-.2,.8,1.4,.4)]};
var fitted=overflow.Build("panel 600 x 900 mm");
Check(Math.Abs(fitted.Bounds().Width-600)<1e-6&&Math.Abs(fitted.Bounds().Height-900)<1e-6,"overflow assembly fitted to requested footprint");
Check(Math.Abs(fitted.Entities[0].Points[2].Y-fitted.Entities[1].Points[0].Y)<1e-6,"fitting preserves shared edges without clipping");
Check(Math.Abs(fitted.Entities[0].Points[2].Y-600)<1e-6,"fitting preserves relative part proportions");
Reject(()=>(overflow with {Parts=[overflow.Parts[0] with {W=500},overflow.Parts[1]]}).Build("panel"),"pathological coordinate range rejected");
var slats=back with {Shape="rectangle",Count=4,Repeat="row",Gap=.04,Detail=0};
Check((plan with {Parts=[seat,slats,support]}).Build("panel 500 x 550 mm").Entities.Length==7,"repeat tool creates four spaced parts");
Check((plan with {Parts=[seat,slats with {Mirror=true},support]}).Build("panel 500 x 550 mm").Entities.Length==7,"symmetric repeat plus mirror does not duplicate tool output");
var curve=back with {Shape="curve",Points=[new(0,0),new(.5,1),new(1,0)]};
Check((plan with {Parts=[seat,curve,support]}).Build(request).Entities[1].Points.Length==33,"curve tool generates quadratic geometry");
Check((plan with {Parts=[seat,curve with {Points=[new(0,0),new(.5,-.15),new(1,0)]},support]}).Build(request).Entities[1].Points.Length==33,"curve controls may bow beyond local box while actual geometry remains bounded");
var ellipse=seat with {Shape="ellipse"};
Check((plan with {Parts=[ellipse,back,support with {X=.3,Y=.5,H=.4}]}).Build(request).Entities[0].Points.Length==48,"ellipse tool available to designer");
Reject(()=>(plan with {Parts=[ellipse,back,support with {X=.1,Y=.65,H=.25}]}).Build(request),"ellipse bounding-box contact is not mistaken for physical connection");
var attached=plan with {Parts=[ellipse,back,support with {X=.05,Y=.9,H=.5,From="seat",To="backrest"}]};
Check(attached.Build(request).Entities.Length>2,"attachment tool computes actual curved-seat connections from poor coordinate hints");
Reject(()=>(attached with {Parts=[ellipse with {From="support",To="backrest"},back,support with {From="seat",To="backrest"}]}).Build(request),"cyclic attachment anchors rejected");
var centered=seat with {X=0,Align="center"};
Check((plan with {Parts=[centered,back,support]}).Build(request).Entities[0].Points[0].X>0,"center alignment applied mathematically");
var calls=0;
var client=new OllamaClient(new HttpClient(new Handler(async(message,ct)=>
{
    calls++;
    using var body=JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
    Check(message.Headers.Authorization is null,"no secret sent in autonomous loop");
    if(calls==1)
    {Check(body.RootElement.GetProperty("format").GetProperty("properties").TryGetProperty("parts",out _),"model asked for parts, not furniture category");return Reply(encoded);}
    Check(body.RootElement.GetProperty("messages")[1].GetProperty("images")[0].GetString()!.Length>20,"rendered preview sent to reviewer");
    Check(!body.RootElement.GetProperty("think").GetBoolean(),"thinking disabled for bounded review latency");
    return Reply("{\"accepted\":true,\"issues\":[]}");
})));
var result=await new DesignAgent(client).GenerateAsync(request,"qwen3.5:4b","qwen3.5:4b",PlanRenderer.Png,null,default);
Check(calls==2&&result.VisuallyReviewed&&result.Attempts==1,"design, construction and visual review complete");
var repairCalls=0;
var bad=JsonSerializer.Serialize(plan with {Parts=[seat,back with {Y=.4,H=.2}]},BlockSpec.JsonOptions);
var repair=new DesignAgent(new OllamaClient(new HttpClient(new Handler(async(message,ct)=>
{
    repairCalls++;
    if(repairCalls==2)
    {using var body=JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));Check(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("overlap"),"geometric failure fed back to model");}
    return Reply(repairCalls==1?bad:encoded);
}))));
Check((await repair.GenerateAsync(request,"test",null,null,null,default)).Attempts==2,"invalid design repaired without template fallback");
var reviewCalls=0;
var revised=new DesignAgent(new OllamaClient(new HttpClient(new Handler((_,_)=>Task.FromResult(++reviewCalls switch
{1=>Reply(encoded),2=>Reply("{\"accepted\":false,\"issues\":[\"Backrest is visually disconnected.\"]}"),3=>Reply(encoded),_=>Reply("{\"accepted\":true,\"issues\":[]}")})))));
Check((await revised.GenerateAsync(request,"test","vision",PlanRenderer.Png,null,default)).Attempts==2&&reviewCalls==4,"visual feedback triggers another complete design pass");
var endlessCalls=0;
var endless=new DesignAgent(new OllamaClient(new HttpClient(new Handler((_,_)=>{endlessCalls++;return Task.FromResult(Reply(bad));}))));
try{await endless.GenerateAsync(request,"test",null,null,null,default);throw new Exception("Unvalidated design returned");}
catch(InvalidDataException){Check(endlessCalls==3,"repair loop bounded; failed design not insertable");}
using(var cancel=new CancellationTokenSource())
{cancel.Cancel();try{await endless.GenerateAsync(request,"test",null,null,null,cancel.Token);throw new Exception("Canceled loop ran");}catch(OperationCanceledException){Check(true,"cancellation stops design loop");}}
Directory.CreateDirectory("artifacts-design");
File.WriteAllBytes("artifacts-design/toolkit-example.png",PlanRenderer.Png(block));
Console.WriteLine($"{passed} design-loop checks passed.");

if(args.Contains("--live"))
{
    using var http=new HttpClient(new RecordingHandler(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false})){Timeout=TimeSpan.FromMinutes(8)};
    var agent=new DesignAgent(new OllamaClient(http));
    var prompts=args.Contains("--variations")?new[]{request,"Dining chair, no cushion, 500 x 550 mm, with a curved backrest and an oval seat","Dining chair, no cushion, 500 x 550 mm, straight backrest with four slats and a tapered seat"}:new[]{request};
    for(var i=0;i<prompts.Length;i++)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var generated=await agent.GenerateAsync(prompts[i],args.Contains("--text-designer")?OllamaClient.DefaultModel:DesignAgent.DefaultModel,args.Contains("--no-review")?null:DesignAgent.DefaultModel,PlanRenderer.Png,new Progress<string>(Console.WriteLine),default);
        File.WriteAllText($"artifacts-design/design-{i+1}.json",JsonSerializer.Serialize(generated,BlockSpec.JsonOptions));
        File.WriteAllBytes($"artifacts-design/design-{i+1}.png",PlanRenderer.Png(generated.Block));
        Console.WriteLine($"LIVE {i+1}: {generated.Plan.Parts.Length} AI-designed parts, {generated.Block.Entities.Length} entities; {generated.Attempts} attempts; visual review={generated.VisuallyReviewed}; {watch.Elapsed.TotalSeconds:0.0}s");
    }
}

static HttpResponseMessage Reply(string text)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{done=true,response=text}))};
sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handler):HttpMessageHandler
{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken cancellation)=>handler(message,cancellation);}
sealed class RecordingHandler(HttpMessageHandler inner):DelegatingHandler(inner)
{
    private int count;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken cancellation)
    {
        var response=await base.SendAsync(message,cancellation);
        var body=await response.Content.ReadAsStringAsync(cancellation);
        File.WriteAllText($"artifacts-design/live-response-{++count}.json",body);
        return response;
    }
}
