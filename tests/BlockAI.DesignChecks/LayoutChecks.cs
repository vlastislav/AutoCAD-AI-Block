using BlockAI.Core;
using BlockAI.AutoCAD;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class LayoutChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        DesignPart Part(string role,double x,double y,double w,double h)=>new(role,"rectangle",x,y,w,h,0,1,"none",0,false,"none",0,[]);
        var sofa=new DesignPlan("Corner","Two joined perpendicular seating runs",2400,1800,
            [Part("seat_long",0,.6,1,.4),Part("seat_return",.6,0,.4,.65),Part("back",0,.96,1,.04)]);
        check(sofa.Build("L shaped sofa plan").Bounds().Width==2400,"joined concave sofa footprint passes without a furniture template");
        Reject(()=>(sofa with {Parts=[sofa.Parts[0],sofa.Parts[1] with {X=1.2,H=.4},sofa.Parts[2]]}).Build("L shaped sofa plan"),"detached seating run is rejected before visual approval",check);
        Reject(()=>(sofa with {Parts=[Part("base",0,0,1,1),Part("seat",.1,.1,.8,.8),Part("back",0,.9,1,.1)]}).Build("L shaped sofa plan"),"rectangular base cannot pass as an L-shaped sofa",check);
        Reject(()=>(sofa with {Parts=[Part("seat",0,.6,1,.4),Part("base_return",.6,0,.4,.65),Part("back",0,.96,1,.04)]}).Build("L shaped sofa plan"),"L-shaped base alone cannot substitute for a perpendicular seating run",check);
        Reject(()=>(sofa with {Parts=[sofa.Parts[0],sofa.Parts[1] with {X=1.9,W=1.6}]}).Build("L shaped sofa plan"),"regression: outlying meter-like coordinates cannot be hidden by global fitting",check);
        var metric=ToMetric(sofa);var parsed=MetricDesign.Parse(metric);
        check(Math.Abs(parsed.Parts[1].Y-sofa.Parts[1].Y)<1e-9&&Math.Abs(parsed.Parts[1].W-.4)<1e-9,"millimeter draft boxes convert to the existing normalized geometry engine");
        var physical=parsed.Build("L shaped sofa plan",preserveScale:true);
        check(Math.Abs(physical.Entities[1].Points.Max(p=>p.X)-physical.Entities[1].Points.Min(p=>p.X)-960)<1e-6,"metric part width is preserved rather than stretched by fitting");
        Reject(()=>(new DesignPlan("Small","Undersized surface",2000,1000,[Part("surface",0,0,.8,.8),Part("detail",.1,.1,.1,.1)])).Build("panel",preserveScale:true),"metric footprint mismatch is returned for correction instead of silent stretching",check);
        using(var schema=JsonDocument.Parse(MetricDesign.SchemaFor(false)))
        {
            var props=schema.RootElement.GetProperty("properties").GetProperty("parts").GetProperty("items").GetProperty("properties");
            check(props.TryGetProperty("widthMm",out _)&&!props.TryGetProperty("w",out _)&&props.GetProperty("points").GetProperty("items").GetProperty("properties").TryGetProperty("x",out _),"metric schema separates millimeter boxes from local fractional polygon points");
        }
        var mixed=JsonNode.Parse(metric)!;mixed["parts"]![0]!["x"]=.2;
        Reject(()=>MetricDesign.Parse(mixed.ToJsonString()),"ambiguous mixed-unit part boxes are rejected",check);
        var adjustment="{\"replacements\":[],\"remove\":[],\"add\":[],\"adjustments\":[{\"index\":1,\"field\":\"depthMm\",\"value\":1200}]}";
        var adjusted=DesignRevision.Apply(sofa,adjustment,new HashSet<int>{1},false);
        check(Math.Abs(adjusted.Parts[1].H-1200d/1800)<1e-9&&ReferenceEquals(adjusted.Parts[0],sofa.Parts[0]),"focused millimeter resize preserves every other part");
        Reject(()=>DesignRevision.Apply(sofa,adjustment,new HashSet<int>{0},false),"millimeter adjustments cannot bypass correct-part locks",check);
        var same="{\"replacements\":[],\"remove\":[],\"add\":[],\"adjustments\":[{\"index\":1,\"field\":\"depthMm\",\"value\":1170}]}";
        Reject(()=>DesignRevision.Apply(sofa,same),"resubmitting unchanged dimensions is rejected as a no-op",check);
        var replacement=JsonNode.Parse(MetricDesign.ToJson(sofa))!["parts"]![1]!.DeepClone();replacement["widthMm"]=1000;
        var metricPatch=new JsonObject{["replacements"]=new JsonArray(new JsonObject{["index"]=1,["part"]=replacement}),["remove"]=new JsonArray(),["add"]=new JsonArray(),["adjustments"]=new JsonArray()};
        check(Math.Abs(DesignRevision.Apply(sofa,metricPatch.ToJsonString(),metricParts:true).Parts[1].W-1000d/2400)<1e-9,"full-part repairs use the same millimeter box contract as the first draft");
        using(var repairSchema=JsonDocument.Parse(DesignRevision.SchemaFor(false,true)))check(repairSchema.RootElement.GetProperty("properties").GetProperty("replacements").GetProperty("items").GetProperty("properties").GetProperty("part").GetProperty("properties").TryGetProperty("widthMm",out _),"repair schema cannot mix normalized whole-part boxes with millimeter edits");
        check(MetricDesign.Parse(MetricDesign.ToJson(sofa)).Parts.Length==sofa.Parts.Length,"saved assembly is shown to the editor in consistent millimeter units");
        var joined=ReferenceGeometry.Create([[new(0,0),new(1,0)],[new(1,0),new(1,1)],[new(1,1),new(0,1)],[new(0,1),new(0,0)]],joinStrokes:true);
        check(joined.Outlines.Length==1&&joined.Outlines[0].Closed,"unambiguous loose DWG strokes join into a measurable closed outline");
        var branched=ReferenceGeometry.Create([[new(0,0),new(1,1)],[new(1,1),new(2,0)],[new(1,1),new(1,2)]],joinStrokes:true);
        check(branched.Outlines.Length==3,"ambiguous stroke junctions are not invented into closed furniture parts");

        var first=sofa with {Parts=[sofa.Parts[0],sofa.Parts[1] with {H=.4},sofa.Parts[2]]};
        var calls=0;var recording=new DesignRecording{Prompt="L shaped sofa plan",Model="scripted",Demonstration=true};
        var agent=new DesignAgent(new OllamaClient(new HttpClient(new Handler(async(message,ct)=>
        {
            using var body=JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));var content=body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            calls++;
            if(calls==1)return Reply(ToMetric(first));
            if(calls==2)return Reply("{\"replacements\":[],\"remove\":[],\"add\":[],\"adjustments\":[{\"index\":1,\"field\":\"depthMm\",\"value\":720}]}" );
            check(content.Contains("disconnected")&&content.Contains("no parameter changes")&&content.Contains("CURRENT PART BOXES IN MILLIMETERS"),"no-op retry retains the original geometric error and measured correction context");
            return Reply("{\"replacements\":[],\"remove\":[],\"add\":[],\"adjustments\":[{\"index\":1,\"field\":\"xMm\",\"value\":1440},{\"index\":1,\"field\":\"depthMm\",\"value\":1170}]}" );
        }))));
        var result=await agent.GenerateAsync(recording.Prompt,"scripted",null,null,null,default,recording,targetedRevisions:true,metricDraft:true);
        check(result.Attempts==3&&recording.Events.Any(e=>e.Stage=="revision-stalled"),"stalled invalid geometry is recorded and a measured edit resolves it");
        check(recording.Events.Single(e=>e.Stage=="revision-stalled").Frame?.Geometry.Length>0,"no-op attempts replay the retained construction instead of inventing a new draft");
        File.WriteAllText("artifacts-design/viewer-placement.json",recording.ToJson());File.WriteAllText("artifacts-design/viewer-placement.html",recording.ToHtml());

        var study=new ReferenceLesson("study","private.dwg","isolated sofa",new string('a',64),"scripted",DateTimeOffset.UtcNow,
            new("L-shaped sofa","sofa","plan",[],["joined runs"],[],[],[],[],.8,null,null),true,Geometry:joined);
        var visionCalls=0;string? sourceImage=null;var imageRecording=new DesignRecording{Prompt="L shaped sofa plan",Model="scripted"};
        var visionAgent=new DesignAgent(new OllamaClient(new HttpClient(new Handler(async(message,ct)=>
        {
            visionCalls++;using var body=JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));var input=body.RootElement.GetProperty("messages")[1];
            if(visionCalls==1){sourceImage=input.GetProperty("images")[0].GetString();check(input.GetProperty("content").GetString()!.Contains("reference LINEWORK"),"designer receives an isolated geometric reference image with its meaning labeled");return Reply(ToMetric(sofa));}
            if(visionCalls==2)return Reply("{\"accepted\":false,\"issues\":[\"Adjust back depth\"],\"partIndices\":[2]}");
            if(visionCalls==3){check(input.GetProperty("images")[0].GetString()!=sourceImage&&input.GetProperty("content").GetString()!.Contains("CURRENT failed/rejected assembly"),"editor receives its current draft image rather than repeatedly redrawing the source");return Reply("{\"replacements\":[],\"remove\":[],\"add\":[],\"adjustments\":[{\"index\":2,\"field\":\"depthMm\",\"value\":50}]}" );}
            return Reply("{\"accepted\":true,\"issues\":[],\"partIndices\":[]}");
        }))));
        await visionAgent.GenerateAsync(imageRecording.Prompt,"scripted","scripted",PlanRenderer.Png,null,default,imageRecording,references:[study],targetedRevisions:true,metricDraft:true,renderReference:PlanRenderer.ReferencePng,visualEdits:true);
        check(!imageRecording.ToJson().Contains(sourceImage!,StringComparison.Ordinal),"source and current-draft images are not stored in replay JSON");
    }

    public static string ToMetric(DesignPlan plan)
    {
        var node=JsonSerializer.SerializeToNode(plan,BlockSpec.JsonOptions)!;
        foreach(var p in node["parts"]!.AsArray())foreach(var pair in new Dictionary<string,string>{{"x","xMm"},{"y","yMm"},{"w","widthMm"},{"h","depthMm"},{"gap","gapMm"}})
        {var factor=pair.Key is "y" or "h"?plan.Depth:pair.Key=="gap"&&p!["repeat"]!.GetValue<string>()=="column"?plan.Depth:plan.Width;p![pair.Value]=p[pair.Key]!.GetValue<double>()*factor;p.AsObject().Remove(pair.Key);}
        return node.ToJsonString();
    }
    private static void Reject(Action action,string name,Action<bool,string> check){try{action();}catch(InvalidDataException){check(true,name);return;}throw new Exception(name);}
    private static HttpResponseMessage Reply(string content)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{done=true,message=new{content}}))};
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handle):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>handle(request,token);}
}
