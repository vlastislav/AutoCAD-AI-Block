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
Console.WriteLine($"{passed} checks passed. No paid API calls made.");

if (args.Contains("--local-smoke"))
{
    using var localHttp = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    var client = new OllamaClient(localHttp);
    Console.WriteLine("LIVE local models: " + string.Join(", ", await client.ListModelsAsync(default)));
    var started = System.Diagnostics.Stopwatch.StartNew();
    var generated = await client.GenerateAsync("A king size wooden bed plan. North American king mattress 1930 by 2030 mm. Show frame, headboard and two pillows. Use fewer than 20 entities.", OllamaClient.DefaultModel, default);
    var bounds = generated.Bounds();
    Console.WriteLine($"LIVE generated {generated.Name}: {generated.Entities.Length} entities, {bounds.Width} x {bounds.Height} mm in {started.Elapsed.TotalSeconds:0.0}s");
    Directory.CreateDirectory("artifacts-next");
    File.WriteAllText("artifacts-next/local-bed-smoke.json", JsonSerializer.Serialize(generated, BlockSpec.JsonOptions));
}

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}
