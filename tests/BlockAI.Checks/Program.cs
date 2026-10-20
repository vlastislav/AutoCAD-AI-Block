using BlockAI.Core;
using System.Net;
using System.Text;
using System.Text.Json;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    passed++;
    Console.WriteLine("PASS " + message);
}
void Reject(Action action, string message)
{
    try { action(); } catch (InvalidDataException) { Check(true, message); return; }
    throw new Exception("Expected rejection: " + message);
}

var bed = BlockSpec.DemoKingBed();
bed.Validate();
Check(bed.Bounds().Width == 2200 && bed.Bounds().Height == 2290, "king-bed physical extents");
var encoded = JsonSerializer.Serialize(bed, BlockSpec.JsonOptions);
Check(BlockSpec.Parse(encoded).Entities.Length == bed.Entities.Length, "geometry JSON roundtrip");
Check(Math.Abs(BlockSpec.MillimetersToDrawingUnits("Inches") * 25.4 - 1) < 1e-10, "inch conversion");
Check(BlockSpec.MillimetersToDrawingUnits("Meters") * 1000 == 1, "meter conversion");
Reject(() => (bed with { Entities = [] }).Validate(), "empty block rejected");
Reject(() => (bed with { Entities = new Primitive[501] }).Validate(), "entity count limited");
Reject(() => (bed with { Entities = [new("circle", [], new(double.NaN, 0), 1, false)] }).Validate(), "nonfinite coordinate rejected");
Reject(() => (bed with { Entities = [new("circle", [], new(0, 0), -1, false)] }).Validate(), "negative circle radius rejected");
Reject(() => (bed with { Entities = [new("command", [], new(0, 0), 0, false)] }).Validate(), "commands cannot be geometry");
Reject(() => (bed with { Entities = [new("polyline", [new(0, 0), new(1, 1), new(2, 2)], new(0, 0), 0, true)] }).Validate(), "degenerate outline rejected");
Reject(() => (bed with { Entities = [new("polyline", [new(0, 0), new(0, 0)], new(0, 0), 0, false)] }).Validate(), "duplicate vertices rejected");
Reject(() => BlockSpec.Parse("{\"name\":\"test\"}"), "incomplete AI result rejected");

var circle = bed with { Entities = [new("circle", [], new(0, 0), 250, false)] };
circle.Validate();
Check(circle.Bounds().Width == 500, "circle bounds include radius");

var handler = new FakeHandler(async (request, ct) =>
{
    Check(request.RequestUri!.AbsoluteUri == "https://api.openai.com/v1/responses", "fixed HTTPS API endpoint");
    Check(request.Headers.Authorization?.Parameter == "test-key", "API authorization supplied");
    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
    Check(!body.RootElement.GetProperty("store").GetBoolean(), "API storage disabled");
    Check(body.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean(), "strict schema requested");
    return new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new[] { new { content = new[] { new { type = "output_text", text = encoded } } } }
        }), Encoding.UTF8, "application/json")
    };
});
var result = await new AiClient(new HttpClient(handler)).GenerateAsync("bed", "test-key", "test-model", default);
Check(result.Name == bed.Name, "Responses output parsed into validated geometry");
foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests })
{
    var client = new AiClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)))));
    try { await client.GenerateAsync("bed", "test", "test", default); throw new Exception("HTTP error accepted"); }
    catch (HttpRequestException) { Check(true, $"HTTP {(int)status} handled"); }
}
var unfinished = new AiClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{ Content = new StringContent("{\"status\":\"incomplete\"}") }))));
try { await unfinished.GenerateAsync("bed", "test", "test", default); throw new Exception("Incomplete output accepted"); }
catch (InvalidDataException) { Check(true, "incomplete generation rejected"); }
var localHandler = new FakeHandler(async (request, ct) =>
{
    Check(request.RequestUri!.AbsoluteUri == "http://127.0.0.1:11434/api/generate", "Ollama uses loopback only");
    Check(request.Headers.Authorization is null, "no API key sent to Ollama");
    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
    Check(!body.RootElement.GetProperty("stream").GetBoolean(), "Ollama requests complete JSON");
    Check(body.RootElement.GetProperty("format").GetProperty("type").GetString() == "object", "Ollama receives geometry schema");
    Check(body.RootElement.GetProperty("model").GetString() == OllamaClient.DefaultModel, "Ollama model selection");
    Check(body.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32() == 8192, "local context bounded for desktop GPU");
    Check(body.RootElement.GetProperty("format").GetProperty("properties").GetProperty("entities").GetProperty("maxItems").GetInt32() == 40, "local schema limits output complexity");
    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { done = true, done_reason = "stop", response = encoded })) };
});
var localResult = await new OllamaClient(new HttpClient(localHandler)).GenerateAsync("bed", OllamaClient.DefaultModel, default);
Check(localResult.Name == bed.Name, "Ollama JSON parsed and validated");
var retryCount = 0;
var repairClient = new OllamaClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(new { done = true, response = ++retryCount == 1 ? "{}" : encoded }))
}))));
await repairClient.GenerateAsync("bed", "test", default);
Check(retryCount == 2, "invalid local geometry repaired once");
var alwaysInvalid = new OllamaClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{ Content = new StringContent("{\"done\":true,\"response\":\"{}\"}") }))));
try { await alwaysInvalid.GenerateAsync("bed", "test", default); throw new Exception("Invalid local geometry accepted"); }
catch (InvalidDataException) { Check(true, "invalid local geometry rejected after bounded repair"); }
var tags = new OllamaClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{ Content = new StringContent("{\"models\":[{\"name\":\"qwen2.5:7b\"},{\"name\":\"test:cloud\"}]}") }))));
Check((await tags.ListModelsAsync(default)).SequenceEqual(new[] { "qwen2.5:7b" }), "model list excludes cloud models");
try { await tags.GenerateAsync("bed", "test:cloud", default); throw new Exception("Cloud model accepted"); }
catch (ArgumentException) { Check(true, "local mode rejects cloud model requests"); }
foreach (var code in new[] { HttpStatusCode.NotFound, HttpStatusCode.InternalServerError, HttpStatusCode.Redirect })
{
    var client = new OllamaClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(code)))));
    try { await client.GenerateAsync("bed", "test", default); throw new Exception("Local HTTP error accepted"); }
    catch (HttpRequestException) { Check(true, $"local HTTP {(int)code} handled"); }
}
var offline = new OllamaClient(new HttpClient(new FakeHandler((_, _) => throw new HttpRequestException("Connection refused"))));
try { await offline.ListModelsAsync(default); throw new Exception("Unavailable server accepted"); }
catch (HttpRequestException ex) { Check(ex.Message.Contains("Start local AI"), "offline error explains how to start Ollama"); }
var sofaDesign = new FurnitureDesign("sofa",2400,950,3,"modern","rectangular",[]);
var sofa = sofaDesign.Build();
Check(sofa.Bounds().Width == 2400 && sofa.Bounds().Height == 950, "sofa has exact overall dimensions");
Check(sofa.Entities.Length == 9, "three-seat sofa has footprint, two arms, three seats and three back cushions");
var cushionBounds = sofa.Entities.Skip(3).Where((_,i)=>i%2==0).Select(e => (MinX:e.Points.Min(p=>p.X),MaxX:e.Points.Max(p=>p.X),MinY:e.Points.Min(p=>p.Y),MaxY:e.Points.Max(p=>p.Y))).ToArray();
Check(cushionBounds.Length==3 && cushionBounds[0].MaxX < cushionBounds[1].MinX && cushionBounds[1].MaxX < cushionBounds[2].MinX, "sofa seat cushions separated without overlap");
Check(cushionBounds.All(b=>b.MinX>0 && b.MaxX<2400 && b.MinY>0 && b.MaxY<950), "sofa cushions stay inside footprint");
foreach(var kind in new[]{"sofa","bed","chair","table","desk","cabinet"})
{
    var design = kind switch { "bed"=>new FurnitureDesign(kind,2030,2230,1,"modern","rectangular",[]), "chair"=>new(kind,850,850,1,"classic","rectangular",[]), _=>new(kind,1600,900,kind=="sofa"?2:1,"modern","rectangular",[]) };
    var block = design.Build();
    Check(block.Bounds().Width==design.Width && block.Bounds().Height==design.Depth, $"{kind} template preserves bounds");
}
var explicitSize = sofaDesign.ApplyExplicitDimensions("modern 2-seat sofa 2.1m x 90cm");
Check(explicitSize.Width==2100 && explicitSize.Depth==900 && explicitSize.Seats==2, "explicit mixed units and seat count override AI guesses");
Check(sofaDesign.ApplyExplicitDimensions("2400 x 950 modern sofa").Depth==950, "style word is not interpreted as unit");
Check(sofaDesign.ApplyExplicitDimensions("sofa 84 by 36 inches").Width==2133.6, "shared imperial unit converted exactly");
var bedDesign = new FurnitureDesign("bed",2030,2230,1,"modern","rectangular",[]);
Check(bedDesign.ApplyExplicitDimensions("bed mattress 1930 x 2030 mm").Width==2030, "mattress dimensions receive frame allowance");
Check(bedDesign.ApplyExplicitDimensions("bed overall 2030 x 2230 mm with mattress").Width==2030, "overall bed dimensions are not expanded");
Reject(()=> (sofaDesign with {Width=1000,Seats=5}).Build(), "impossible seat width rejected");
Reject(()=> (sofaDesign with {Width=double.NaN}).Build(), "nonfinite furniture dimensions rejected");
var roundTable = new FurnitureDesign("table",1200,1200,1,"modern","round",[]).Build();
Check(roundTable.Entities.Length==1 && roundTable.Entities[0].Radius==600, "round table uses correct diameter");
var intentCalls = 0;
var intentClient = new OllamaClient(new HttpClient(new FakeHandler(async(request,ct)=>
{
    intentCalls++;
    using var requestJson=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
    Check(requestJson.RootElement.GetProperty("format").GetProperty("properties").TryGetProperty("width",out _), "furniture request asks AI for parameters");
    return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{done=true,response=JsonSerializer.Serialize(sofaDesign,BlockSpec.JsonOptions)}))};
})));
var designed = await intentClient.GenerateFurnitureAsync("3-seat sofa 2500 x 1000 mm","test",default);
Check(intentCalls==1 && designed.Bounds().Width==2500 && designed.Entities.Length==9, "one AI call builds exact requested sofa dimensions");
var fallbackCalls=0;
var fallbackClient=new OllamaClient(new HttpClient(new FakeHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{Content=new StringContent(JsonSerializer.Serialize(new{done=true,response=++fallbackCalls==1 ? "{\"kind\":\"other\"}" : encoded}))}))));
var fallback=await fallbackClient.GenerateFurnitureAsync("a bicycle","test",default);
Check(fallbackCalls==2 && fallback.Assumptions[0].StartsWith("Experimental"), "unsupported object uses labeled free-form fallback");
Console.WriteLine($"{passed} checks passed. No paid API calls made.");

if (args.Contains("--local-smoke"))
{
    using var localHttp = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    var client = new OllamaClient(localHttp);
    Console.WriteLine("LIVE local models: " + string.Join(", ", await client.ListModelsAsync(default)));
    var started = System.Diagnostics.Stopwatch.StartNew();
    Directory.CreateDirectory("artifacts-next");
    foreach(var item in new[]{(Name:"sofa",Prompt:"A modern 3-seat sofa, 2400 x 950 mm"),(Name:"bed",Prompt:"A king size wooden bed plan. Mattress 1930 x 2030 mm."),(Name:"table",Prompt:"A round dining table, diameter 1200 mm")})
    {
        started.Restart();
        var generated=await client.GenerateFurnitureAsync(item.Prompt,OllamaClient.DefaultModel,default);
        var bounds=generated.Bounds();
        Console.WriteLine($"LIVE {item.Name}: {generated.Entities.Length} entities, {bounds.Width} x {bounds.Height} mm in {started.Elapsed.TotalSeconds:0.0}s");
        Check(generated.Assumptions[0].StartsWith("Built from adjustable"), "live "+item.Name+" uses furniture generator");
        File.WriteAllText($"artifacts-next/local-{item.Name}-smoke.json",JsonSerializer.Serialize(generated,BlockSpec.JsonOptions));
    }
}

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}
