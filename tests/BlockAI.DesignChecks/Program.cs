using BlockAI.Core;
using BlockAI.AutoCAD;
using System.Net;
using System.Text.Json;
using System.IO;

var passed=0;
Directory.CreateDirectory("artifacts-design");
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
Check((plan with {Parts=[seat,back with {Y=.4,H=.2}]}).Build(request).Entities.Length>0,"intentional seat/back projection overlap allowed and trimmed");
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
var repeatFrames=new List<ConstructionFrame>();
var repeated=(plan with {Parts=[seat,slats,support]}).Build("panel 500 x 550 mm",repeatFrames.Add);
Check(repeatFrames.First(f=>f.Stage=="assembled").Geometry.Count(g=>g.PartId==1)==4,"repeat tool creates four spaced parts before visibility trimming");
var mirroredRepeatFrames=new List<ConstructionFrame>();
(plan with {Parts=[seat,slats with {Mirror=true},support]}).Build("panel 500 x 550 mm",mirroredRepeatFrames.Add);
Check(mirroredRepeatFrames.First(f=>f.Stage=="assembled").Geometry.Count(g=>g.PartId==1)==4,"symmetric repeat plus mirror does not duplicate tool output");
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

// Visibility is independent of furniture category and input list order.
var lower=Part("lower_frame","rectangle",0,0,1,1) with {Level=0};
var upper=Part("upper_panel","rectangle",.25,.25,.5,.5) with {Level=10};
var layered=new DesignPlan("LayerStudy","Overlapping panels",1000,1000,[lower,upper]);
var visibilityFrames=new List<ConstructionFrame>();
layered.Build("panel",visibilityFrames.Add);
Check(visibilityFrames.Any(f=>f.Stage=="visibility-order"),"depth order is recorded for all furniture categories");
var inner=Part("lower_detail","rectangle",.3,.3,.2,.2) with {Level=0};
var covered=new DesignPlan("CoverageStudy","Small part under a larger surface",1000,1000,[inner,upper]);
var coverageFrames=new List<ConstructionFrame>();covered.Build("panel",coverageFrames.Add);
Check(coverageFrames.Last(f=>f.Stage=="hidden-edges").Geometry.All(g=>g.Role!="lower_detail"),"fully covered lower part disappears");
var transparent=covered with {Parts=[inner,upper with {Opaque=false}]};
Check(transparent.Build("panel").Entities.Length==2,"nonopaque closed linework does not hide parts");
Check((covered with {Parts=[inner with {Level=10},upper]}).Build("panel").Entities.Length==2,"equal depth does not silently choose a foreground surface");
Check((covered with {Parts=[upper,inner]}).Build("panel").Entities.Length==covered.Build("panel").Entities.Length,"depth visibility does not depend on list order");
var crossing=Part("lower_bar","rectangle",0,.4,1,.2) with {Level=0};
var crossingFrames=new List<ConstructionFrame>();
(layered with {Parts=[crossing,upper]}).Build("panel",crossingFrames.Add);
var clippedBar=crossingFrames.Last(f=>f.Stage=="hidden-edges").Geometry.Where(g=>g.PartId==0).ToArray();
Check(clippedBar.Length==2&&clippedBar.SelectMany(g=>g.Geometry.Points).All(p=>p.X<=.25000001||p.X>=.74999999),"lower crossing edges trimmed at true surface intersections");
var identical=(covered with {Parts=[upper with {Role="base",Level=0},upper]});
Check(identical.Build("panel").Entities.Length==1,"identical footprints at different depths show only the upper outline");
Reject(()=>(covered with {Parts=[inner with {Level=101},upper]}).Build("panel"),"invalid depth order rejected");

var legs=Part("legs","rectangle",.08,.06,.08,.88) with {Mirror=true,Level=0,Z=0,Height=430};
var tableTop=Part("tabletop","rectangle",0,0,1,1) with {Level=10,Z=430,Height=40};
var table=new DesignPlan("SharedTable","Simple two-pedestal table",1200,700,[legs,tableTop],470);
var viewFrames=new List<ConstructionFrame>();
var tableViews=ElevationBuilder.Build(table,"table 1200 x 700 mm",viewFrames.Add);
Check(tableViews.Plan.Bounds().Width==1200&&tableViews.Plan.Bounds().Height==700,"plan footprint comes from shared assembly");
Check(tableViews.Front.Bounds().Width==1200&&tableViews.Front.Bounds().Height==470,"front uses shared width and overall height");
Check(tableViews.Side.Bounds().Width==700&&tableViews.Side.Bounds().Height==470,"side uses shared depth and overall height");
Check(tableViews.Plan.Entities.Length==1&&tableViews.Front.Entities.Length>1,"supports hidden in plan are retained in elevations");
Check(viewFrames.Any(f=>f.View=="front")&&viewFrames.Any(f=>f.View=="side"),"both elevations are stored as view-specific replay frames");
Reject(()=>ElevationBuilder.Build(table with {Parts=[legs with {Z=null},tableTop]},"table"),"missing vertical dimensions fail rather than inventing elevations");
Reject(()=>ElevationBuilder.Build(table with {Height=400},"table"),"parts above the declared overall height rejected");
var allViewPng=PlanRenderer.Png(tableViews);
Check(allViewPng.Length>1000,"review renderer creates a labeled three-view image");
File.WriteAllBytes("artifacts-design/shared-table-views.png",allViewPng);
using(var schema=JsonDocument.Parse(DesignPlan.SchemaFor(true)))
Check(schema.RootElement.GetProperty("properties").GetProperty("parts").GetProperty("items").GetProperty("required").EnumerateArray().Any(v=>v.GetString()=="z"),"elevation request schema requires shared vertical coordinates");
var multiRecording=new DesignRecording {Prompt="table 1200 x 700 mm",Model="scripted shared-assembly designer",Demonstration=true};
var multiAgent=new DesignAgent(new OllamaClient(new HttpClient(new Handler((_,_)=>Task.FromResult(Reply(JsonSerializer.Serialize(table,BlockSpec.JsonOptions)))))));
var multiResult=await multiAgent.GenerateAsync(multiRecording.Prompt,"scripted",null,null,null,default,multiRecording,true,PlanRenderer.Png);
Check(multiResult.Front is not null&&multiResult.Side is not null&&multiResult.Attempts==1,"one shared model response produces all three editable views");
File.WriteAllText("artifacts-design/viewer-views.json",multiRecording.ToJson());
File.WriteAllText("artifacts-design/viewer-views.html",multiRecording.ToHtml());
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
var bad=JsonSerializer.Serialize(plan with {Parts=[seat,back with {Y=.95,H=.05}]},BlockSpec.JsonOptions);
var repair=new DesignAgent(new OllamaClient(new HttpClient(new Handler(async(message,ct)=>
{
    repairCalls++;
    if(repairCalls==2)
    {using var body=JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));Check(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("disconnected"),"geometric failure fed back to model");}
    return Reply(repairCalls==1?bad:encoded);
}))));
Check((await repair.GenerateAsync(request,"test",null,null,null,default)).Attempts==2,"invalid design repaired without template fallback");
var incompleteCalls=0;
var incompleteAgent=new DesignAgent(new OllamaClient(new HttpClient(new Handler(async(message,ct)=>
{
    if(++incompleteCalls==1)return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"done\":true,\"done_reason\":\"length\",\"message\":{\"content\":\"{}\"}}")};
    using var body=JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
    Check(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("at most 6 meaningful"),"incomplete-response correction reaches the next model request");
    return Reply(encoded);
}))));
Check((await incompleteAgent.GenerateAsync(request,"test",null,null,null,default)).Attempts==2,"incomplete model output is repaired within the bounded attempt loop");
var reviewCalls=0;
var revised=new DesignAgent(new OllamaClient(new HttpClient(new Handler((_,_)=>Task.FromResult(++reviewCalls switch
{1=>Reply(encoded),2=>Reply("{\"accepted\":false,\"issues\":[\"Backrest is visually disconnected.\"]}"),3=>Reply(JsonSerializer.Serialize(plan with {Parts=[seat with {Shape="ellipse"},back,support with {X=.3,Y=.5,H=.4}]},BlockSpec.JsonOptions)),_=>Reply("{\"accepted\":true,\"issues\":[]}")})))));
Check((await revised.GenerateAsync(request,"test","vision",PlanRenderer.Png,null,default)).Attempts==2&&reviewCalls==4,"visual feedback triggers another complete design pass");
var duplicateCalls=0;
var duplicateRecording=new DesignRecording{Prompt=request,Model="scripted",ReviewModel="scripted"};
var duplicateAgent=new DesignAgent(new OllamaClient(new HttpClient(new Handler((_,_)=>Task.FromResult(++duplicateCalls==2?Reply("{\"accepted\":false,\"issues\":[\"Change the seat outline to an oval.\"]}"):Reply(JsonSerializer.Serialize(plan with {Name="RenamedDraft",Description="A different description"},BlockSpec.JsonOptions)))))));
try{await duplicateAgent.GenerateAsync(request,"test","vision",PlanRenderer.Png,null,default,duplicateRecording);throw new Exception("Unchanged rejected geometry accepted");}
catch(InvalidDataException){Check(duplicateCalls==4&&duplicateRecording.Events.Count(e=>e.Stage=="revision-unchanged")==2,"renaming a rejected draft does not count as editing visible geometry");}
File.WriteAllText("artifacts-design/viewer-unchanged.json",duplicateRecording.ToJson());
var endlessCalls=0;
var endless=new DesignAgent(new OllamaClient(new HttpClient(new Handler((_,_)=>{endlessCalls++;return Task.FromResult(Reply(bad));}))));
try{await endless.GenerateAsync(request,"test",null,null,null,default);throw new Exception("Unvalidated design returned");}
catch(InvalidDataException){Check(endlessCalls==3,"repair loop bounded; failed design not insertable");}
using(var cancel=new CancellationTokenSource())
{cancel.Cancel();try{await endless.GenerateAsync(request,"test",null,null,null,cancel.Token);throw new Exception("Canceled loop ran");}catch(OperationCanceledException){Check(true,"cancellation stops design loop");}}
Directory.CreateDirectory("artifacts-design");
File.WriteAllBytes("artifacts-design/toolkit-example.png",PlanRenderer.Png(block));

// Record actual engine work around scripted model/reviewer replies. This is explicitly a demo.
var demoRecording=new DesignRecording {Prompt=request,Model="scripted demonstration designer",ReviewModel="scripted demonstration reviewer",Demonstration=true};
var demoCalls=0;
var ovalPlan=plan with {Name="OvalDiningChair",Description="Oval seat with a thin rear rail and paired attachments",Parts=[ellipse,back,support with {X=.2,From="seat",To="backrest"}]};
var demoAgent=new DesignAgent(new OllamaClient(new HttpClient(new Handler((_,_)=>Task.FromResult(++demoCalls switch
{
    1=>Reply(bad),
    2=>Reply(encoded),
    3=>Reply("{\"accepted\":false,\"issues\":[\"Demonstration feedback: try an oval seat to compare the silhouette.\"]}"),
    4=>Reply(JsonSerializer.Serialize(ovalPlan,BlockSpec.JsonOptions)),
    _=>Reply("{\"accepted\":true,\"issues\":[]}")
})))));
var demoResult=await demoAgent.GenerateAsync(request,"scripted","scripted",PlanRenderer.Png,null,default,demoRecording);
Check(demoRecording.Status=="succeeded"&&demoResult.Attempts==3,"recording preserves three revision attempts through success");
Check(demoRecording.Events.Any(e=>e.Attempt==1&&e.Stage=="validation-failed"&&e.Message.Contains("disconnected")),"recording retains exact failed constraint feedback");
Check(demoRecording.Events.Any(e=>e.Attempt==1&&e.Frame?.Geometry.Length>0),"failed attempt retains inspectable geometry");
Check(demoRecording.Events.Count(e=>e.Plan is not null)==3&&demoRecording.Events.Count(e=>e.Stage=="response"&&e.RawResponse is not null)==3,"recording preserves proposed parts and original tool arguments");
Check(demoRecording.Events.Any(e=>e.Attempt==2&&e.Review is {Accepted:false})&&demoRecording.Events.Any(e=>e.Attempt==3&&e.Review is {Accepted:true}),"reviewer rejection and acceptance recorded separately");
Check(demoRecording.Events.Any(e=>e.Attempt==3&&e.Stage=="request"&&e.Message.Contains("oval seat")),"review correction is associated with the next request");
Check(demoRecording.Events.Zip(demoRecording.Events.Skip(1),(a,b)=>b.ElapsedMs>=a.ElapsedMs).All(v=>v),"event timings are monotonic");
var assembledFrame=demoRecording.Events.First(e=>e.Attempt==3&&e.Stage=="constraints-passed").Frame!;
var clippedFrame=demoRecording.Events.First(e=>e.Attempt==3&&e.Stage=="hidden-edges").Frame!;
Check(assembledFrame.Geometry.Any(g=>g.Role=="support"&&g.Geometry.Closed)&&clippedFrame.Geometry.Where(g=>g.Role=="support").All(g=>!g.Geometry.Closed),"recording distinguishes constructed supports from hidden-edge trimming");
var fitFrame=demoRecording.Events.First(e=>e.Attempt==3&&e.Stage=="fitted").Frame!;
Check(fitFrame.Units=="mm"&&fitFrame.Fit is not null&&fitFrame.Geometry.All(g=>g.PartId>=0),"final fitting transform and part identities recorded");
Check(assembledFrame.Geometry.Length==4,"earlier snapshots remain unchanged after clipping");
var tracedBlock=ovalPlan.Build(request,_=>throw new IOException("Simulated recording failure"));
Check(JsonSerializer.Serialize(tracedBlock,BlockSpec.JsonOptions)==JsonSerializer.Serialize(ovalPlan.Build(request),BlockSpec.JsonOptions),"observer failure cannot alter generated geometry");
var excessiveFrames=new List<ConstructionFrame>();
var excessive=plan with {Parts=Enumerable.Range(0,24).Select(i=>Part("panel"+i,"rectangle",.1,0,.7,1) with {Count=8,Repeat="row",Mirror=true,Inset=.1}).ToArray()};
Reject(()=>excessive.Build("panel",excessiveFrames.Add),"excessive assembly still fails normal geometry limits");
Check(excessiveFrames.All(f=>f.Geometry.Length<=500)&&excessiveFrames.Any(f=>f.Message.Contains("Snapshot limited")),"oversized failed assembly has a bounded replay with explicit truncation notice");
var failedRecording=new DesignRecording {Prompt=request,Model="scripted"};
try{await endless.GenerateAsync(request,"test",null,null,null,default,failedRecording);}catch(InvalidDataException){}
Check(failedRecording.Status=="failed"&&failedRecording.Events.Count(e=>e.Stage=="validation-failed")==3,"exhausted retries remain replayable as a failed run");
var cancelRecording=new DesignRecording {Prompt=request,Model="scripted"};
using(var cancelSource=new CancellationTokenSource())
{
    var interrupted=new DesignAgent(new OllamaClient(new HttpClient(new Handler(async(_,token)=>{cancelSource.Cancel();await Task.Delay(1000,token);return Reply(encoded);}))));
    try{await interrupted.GenerateAsync(request,"test",null,null,null,cancelSource.Token,cancelRecording);}catch(OperationCanceledException){}
}
Check(cancelRecording.Status=="canceled"&&cancelRecording.Events.Any(e=>e.Stage=="request")&&cancelRecording.Events.All(e=>e.Plan is null),"cancellation during model request recorded without fabricated geometry");
var jsonRecording=new DesignRecording {Prompt=request,Model="scripted"};
var malformed=new DesignAgent(new OllamaClient(new HttpClient(new Handler((_,_)=>Task.FromResult(Reply("{not json"))))));
try{await malformed.GenerateAsync(request,"test",null,null,null,default,jsonRecording);}catch(InvalidDataException){}
Check(jsonRecording.Status=="failed"&&jsonRecording.Events.Count(e=>e.RawResponse=="{not json")==3,"invalid model JSON retained for diagnosis");
var transportRecording=new DesignRecording {Prompt=request,Model="scripted"};
var offline=new DesignAgent(new OllamaClient(new HttpClient(new Handler((_,_)=>throw new HttpRequestException("Simulated offline server")))));
try{await offline.GenerateAsync(request,"test",null,null,null,default,transportRecording);}catch(Exception){}
Check(transportRecording.Status=="failed"&&transportRecording.Events.Any(e=>e.Stage=="request"),"transport failure recorded before any proposed plan");
Check(demoRecording.TrySave("artifacts-design/recordings",out var demoPath,out _),"recording saves portable HTML and JSON");
var savedRecording=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(demoPath!,".json")));
Check(savedRecording.RootElement.GetProperty("events").GetArrayLength()==demoRecording.Events.Count,"saved JSON retains all replay events");
Check(!demoRecording.TrySave(demoPath!,out _,out var saveError)&&saveError is not null&&demoRecording.Status=="succeeded","disk recording failure is reported without changing generation success");
var hostile=new DesignRecording {Prompt="</script><script>window.injected=true</script>",Model="scripted"};
hostile.Finish("failed","Test escaping",0);
Check(!hostile.ToHtml().Contains("</script><script>window.injected=true"),"HTML export escapes embedded user content");
var embeddedHtml=demoRecording.ToHtml();
const string dataOpen="<script id=\"recording\" type=\"application/json\">";
var dataStart=embeddedHtml.IndexOf(dataOpen,StringComparison.Ordinal)+dataOpen.Length;
var dataEnd=embeddedHtml.IndexOf("</script>",dataStart,StringComparison.Ordinal);
using(var embedded=JsonDocument.Parse(embeddedHtml[dataStart..dataEnd]))
    Check(embedded.RootElement.GetProperty("id").GetString()==demoRecording.Id,"HTML recording slot contains valid JSON rather than a template placeholder");
Check(!demoRecording.ToJson().Contains("data:image")&&!demoRecording.ToJson().Contains("Authorization"),"recording excludes rendered images and HTTP headers");
File.WriteAllText("artifacts-design/viewer-demo.html",demoRecording.ToHtml());
File.WriteAllText("artifacts-design/viewer-demo.json",demoRecording.ToJson());
File.WriteAllText("artifacts-design/viewer-failed.json",failedRecording.ToJson());
File.WriteAllText("artifacts-design/viewer-canceled.json",cancelRecording.ToJson());
Console.WriteLine($"{passed} design-loop checks passed.");

string PatchFor(int index,DesignPart part)=>JsonSerializer.Serialize(new{replacements=new[]{new{index,part}},remove=Array.Empty<int>(),add=Array.Empty<DesignPart>()},BlockSpec.JsonOptions);
var editedBack=back with {Shape="rectangle"};
var patched=DesignRevision.Apply(plan,PatchFor(1,editedBack),new HashSet<int>{1},false);
Check(ReferenceEquals(patched.Parts[0],plan.Parts[0])&&ReferenceEquals(patched.Parts[2],plan.Parts[2])&&patched.Width==plan.Width&&patched.Name==plan.Name,"indexed edits preserve untouched parts and assembly dimensions exactly");
Reject(()=>DesignRevision.Apply(plan,PatchFor(0,seat with {W=.6}),new HashSet<int>{1},false),"edits to locked correct parts are rejected");
Reject(()=>DesignRevision.Apply(plan,"{\"replacements\":[],\"remove\":[],\"add\":[]}"),"empty edits cannot count as repairs");
Reject(()=>DesignRevision.Apply(plan,encoded),"full regenerated designs are rejected by the edit protocol");
Reject(()=>DesignRevision.Apply(plan,PatchFor(22,editedBack)),"out of range edits rejected atomically");
PlanSymmetry.Validate(block);Check(true,"actual clipped mirrored plan passes symmetry checks");
var asymmetric=block with {Entities=block.Entities.Concat(new[]{new Primitive("polyline",[new(50,100),new(80,100)],new(0,0),0,false)}).ToArray()};
Reject(()=>PlanSymmetry.Validate(asymmetric),"unpaired visible detail fails geometric reflection check");
Check(!PlanSymmetry.Required("L-shaped desk",true)&&!PlanSymmetry.Required("asymmetric sofa",true)&&PlanSymmetry.Required("queen bed",true),"symmetry is required for enabled ordinary furniture but respects intentional asymmetric requests");
var editCalls=0;var editRecording=new DesignRecording{Prompt=request,Model="scripted",ReviewModel="scripted",Demonstration=true};
var editAgent=new DesignAgent(new OllamaClient(new HttpClient(new Handler(async(message,ct)=>
{
    var n=++editCalls;
    using var body=JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
    var content=body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
    if(n==3)Check(body.RootElement.GetProperty("format").GetProperty("properties").TryGetProperty("replacements",out _)&&content.Contains("only these zero-based part indices"),"repair transport uses edits and explicit part locks");
    if(n==6)Check(content.Contains("Widen supports")&&content.Contains("locked part"),"blocked edit retains outstanding review and the last assembly");
    return n switch
    {
        1=>Reply(encoded),
        2=>Reply("{\"accepted\":false,\"issues\":[\"Make the backrest rectangular\"],\"partIndices\":[1]}"),
        3=>Reply(PatchFor(1,editedBack)),
        4=>Reply("{\"accepted\":false,\"issues\":[\"Widen supports\"],\"partIndices\":[2]}"),
        5=>Reply(PatchFor(0,seat with {W=.6})), // Deliberate attempt to redesign a locked correct seat.
        6=>Reply(PatchFor(2,support with {W=.06})),
        _=>Reply("{\"accepted\":true,\"issues\":[],\"partIndices\":[]}")
    };
}))));
var editResult=await editAgent.GenerateAsync(request,"scripted","scripted",PlanRenderer.Png,null,default,editRecording,maxAttempts:6,targetedRevisions:true,requireSymmetry:true);
Check(editResult.Attempts==4&&editCalls==7,"fourth attempt succeeds with a six-attempt budget without accepting the locked-part edit");
Check(JsonSerializer.Serialize(editResult.Plan.Parts[0],BlockSpec.JsonOptions)==JsonSerializer.Serialize(seat,BlockSpec.JsonOptions)&&JsonSerializer.Serialize(editResult.Plan.Parts[1],BlockSpec.JsonOptions)==JsonSerializer.Serialize(editedBack,BlockSpec.JsonOptions)&&editResult.Plan.Parts[2].W==.06,"later repairs keep the correct seat and previously repaired backrest");
Check(editRecording.Events.Any(e=>e.Stage=="parts-locked")&&editRecording.Events.Count(e=>e.Stage=="revision-applied")==2,"replay records part locks and applied patches");
File.WriteAllText("artifacts-design/viewer-edits.json",editRecording.ToJson());
File.WriteAllText("artifacts-design/viewer-edits.html",editRecording.ToHtml());
Console.WriteLine($"{passed} checks including symmetry and scoped edits passed.");

if(Array.IndexOf(args,"--render-recording") is var renderIndex&&renderIndex>=0&&renderIndex+1<args.Length)
{
    using var saved=JsonDocument.Parse(File.ReadAllText(args[renderIndex+1]));
    var proposal=saved.RootElement.GetProperty("events").EnumerateArray().First(e=>e.GetProperty("stage").GetString()=="proposal").GetProperty("plan");
    var replayPlan=DesignPlan.Parse(proposal.GetRawText());
    File.WriteAllBytes("artifacts-design/reference-bed-draft.png",PlanRenderer.Png(replayPlan.Build(saved.RootElement.GetProperty("prompt").GetString()!)));
}

if(args.Contains("--live"))
{
    using var http=new HttpClient(new RecordingHandler(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false})){Timeout=TimeSpan.FromMinutes(8)};
    var agent=new DesignAgent(new OllamaClient(http));
    var prompts=args.Contains("--bed")?new[]{"Queen sized bed plan, overall 1700 x 2200 mm, with two pillows and a wooden headboard"}:args.Contains("--variations")?new[]{request,"Dining chair, no cushion, 500 x 550 mm, with a curved backrest and an oval seat","Dining chair, no cushion, 500 x 550 mm, straight backrest with four slats and a tapered seat"}:new[]{request};
    for(var i=0;i<prompts.Length;i++)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var liveModel=args.Contains("--text-designer")?OllamaClient.DefaultModel:DesignAgent.DefaultModel;
        var liveReviewer=args.Contains("--no-review")?null:DesignAgent.DefaultModel;
        var liveRecording=new DesignRecording {Prompt=prompts[i],Model=liveModel,ReviewModel=liveReviewer};
        DesignResult generated;
        var liveReferences=args.Contains("--references")?ReferenceLibrary.Load(ReferenceLibrary.DefaultDirectory,out _).Match(prompts[i],planOnly:!args.Contains("--elevations")):[];
        if(args.Contains("--references"))Console.WriteLine("References: "+string.Join("; ",liveReferences.Select(r=>r.Analysis.Name)));
        try {generated=await agent.GenerateAsync(prompts[i],liveModel,liveReviewer,PlanRenderer.Png,new Progress<string>(Console.WriteLine),default,liveRecording,args.Contains("--elevations"),PlanRenderer.Png,liveReferences,
            maxAttempts:args.Contains("--edits")?6:3,targetedRevisions:args.Contains("--edits"),requireSymmetry:args.Contains("--edits"));}
        finally {liveRecording.TrySave("artifacts-design/live-recordings",out _,out _);}
        File.WriteAllText($"artifacts-design/design-{i+1}.json",JsonSerializer.Serialize(generated,BlockSpec.JsonOptions));
        File.WriteAllBytes($"artifacts-design/design-{i+1}.png",PlanRenderer.Png(generated.Block));
        if(generated.Front is not null&&generated.Side is not null)File.WriteAllBytes($"artifacts-design/design-{i+1}-views.png",PlanRenderer.Png(new DesignViews(generated.Block,generated.Front,generated.Side)));
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
