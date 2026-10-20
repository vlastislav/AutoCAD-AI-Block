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
Console.WriteLine($"{passed} checks passed. No real API calls made.");

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}
