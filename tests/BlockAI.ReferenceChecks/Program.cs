using BlockAI.Core;
using System.Net;
using System.Text.Json;

var checks=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
ReferenceLesson Lesson(string id,string name,string category,string view="plan",bool? cushion=null,bool? arms=null)=>new(id,"fixture.dwg","one isolated object",new string('a',64),"scripted analyzer",DateTimeOffset.UtcNow,
    new(name,category,view,[category,name],["seat and back for chairs; observed surfaces for other objects"],["broad surface with a narrow rear component"],["supports join neighboring surfaces"],["upper surface hides lower support edges"],["physical dimensions and unseen details are unverified"],.8,cushion,arms),Approved:true);
var chair=Lesson("chair","Plain dining chair","chair");
var sofa=Lesson("sofa","Three seat couch","sofa",cushion:true,arms:true);
var bed=Lesson("bed","Double bed with a headboard","bed");
var table=Lesson("table","Round dining table","table");
var library=new ReferenceLibrary([sofa,table,chair,bed]);
Check(library.Match("dining chair no cushion").Single().Id=="chair","chair request does not retrieve sofa examples");
Check(library.Match("a couch").Single().Id=="sofa","couch synonym retrieves a sofa");
Check(library.Match("king size bed").Single().Id=="bed","bed request selects bed semantics");
Check(library.Match("round dining table").Single().Id=="table","table request selects a table");
Check(library.Match("draw a flower").Length==0,"unrelated prompts do not receive arbitrary furniture notes");
Check(new ReferenceLibrary([chair with {Approved=false}]).Match("chair").Length==0,"unreviewed model analysis cannot guide generation");
Check(new ReferenceLibrary([chair with {Analysis=chair.Analysis with {Confidence=.3}}]).Match("chair").Length==0,"low-confidence studies excluded");
Check(new ReferenceLibrary([chair with {Analysis=chair.Analysis with {View="collection"}}]).Match("chair").Length==0,"whole collections excluded from individual-object matching");
Check(new ReferenceLibrary([sofa]).Match("sofa no cushion").Length==0,"known cushions excluded when explicitly forbidden");
Check(new ReferenceLibrary([sofa]).Match("armless sofa").Length==0,"known arms excluded when explicitly forbidden");
Check(library.Match("chair",0).Length==0,"reference matching can be disabled");
Check(new ReferenceLibrary([chair,chair with {Id="second"},chair with {Id="third"}]).Match("chair",20).Length==2,"retrieval context is bounded to two examples");
var directory=Path.Combine("artifacts-reference-checks",Guid.NewGuid().ToString("N"));
ReferenceLibrary.Save(directory,chair);ReferenceLibrary.Save(directory,sofa);
File.WriteAllText(Path.Combine(directory,"bad.lesson.json"),"not JSON");
var loaded=ReferenceLibrary.Load(directory,out var warnings);
Check(loaded.Lessons.Length==2&&warnings.Length==1,"corrupt files are reported without losing valid reference studies");
Check(loaded.Lessons[0].Analysis.Name=="Plain dining chair","library is presented in meaningful name order");
ReferenceLibrary.Save(directory,chair with {Analysis=chair.Analysis with {Name="Wood chair study",Tags=["dining","wood"]}});
loaded=ReferenceLibrary.Load(directory,out _);
Check(loaded.Lessons.Length==2&&loaded.Match("wood dining chair").Single().Analysis.Name=="Wood chair study","renaming labels updates retrieval without duplicate records");
var context=ReferenceLibrary.Context([chair]);
Check(context.Contains("untrusted descriptive data")&&context.Contains("User dimensions and negative requirements take priority"),"reference context explicitly defers to the actual request");
Check(!context.Contains("\"points\"")&&!context.Contains("\"entities\"")&&!context.Contains("images"),"designer receives semantic notes rather than source geometry or pixels");
var analysisClient=new OllamaClient(new HttpClient(new Handler(async(message,ct)=>
{
    var body=JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
    Check(message.Headers.Authorization is null,"local reference analysis sends no API key");
    Check(body.RootElement.GetProperty("messages")[1].GetProperty("images")[0].GetString()!.Length>100,"reference analysis supplies its isolated image to vision");
    return Reply(JsonSerializer.Serialize(chair.Analysis,BlockSpec.JsonOptions));
})));
var analyzed=await new ReferenceAnalyzer(analysisClient).AnalyzeAsync(new byte[200],"reference.dwg","isolated chair","scripted",default);
Check(!analyzed.Approved&&analyzed.SourceFile=="reference.dwg"&&analyzed.ImageHash.Length==64,"new analysis retains provenance and requires curation");

var plan=DesignPlan.Parse(DesignPlan.WorkedExample[DesignPlan.WorkedExample.IndexOf("{\"name\"",StringComparison.Ordinal)..]);
var record=new DesignRecording{Prompt="Dining chair, no cushion, 600 x 650 mm",Model="scripted"};
var agent=new DesignAgent(new OllamaClient(new HttpClient(new Handler(async(message,ct)=>
{
    using var body=JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
    var input=body.RootElement.GetProperty("messages")[1];
    Check(input.GetProperty("content").GetString()!.Contains("Plain dining chair"),"selected analysis reaches the designer request");
    Check(!input.TryGetProperty("images",out _),"designer is not sent reference pixels to trace");
    return Reply(JsonSerializer.Serialize(plan,BlockSpec.JsonOptions));
}))));
var result=await agent.GenerateAsync(record.Prompt,"scripted",null,null,null,default,record,references:[chair]);
Check(result.Block.Bounds().Width==600&&result.Block.Bounds().Height==650,"requested dimensions retain priority over study notes");
Check(record.Events.Single(e=>e.Stage=="references").References!.Single().Id=="chair","replay identifies exactly which reference analysis was supplied");
File.WriteAllText(Path.Combine(directory,"recording.json"),record.ToJson());
Check(!record.ToJson().Contains("data:image"),"recording contains reference notes and provenance without image data");
Console.WriteLine($"{checks} reference checks passed.");

static HttpResponseMessage Reply(string text)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{done=true,message=new{content=text}}))};
sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handle):HttpMessageHandler
{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>handle(request,cancellationToken);}
