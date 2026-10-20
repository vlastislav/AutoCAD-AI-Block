using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BlockAI.Core;

public sealed class AiClient(HttpClient http)
{
    public const string Instructions = """
        You design clean, usable 2D architectural CAD blocks from user descriptions.
        Return geometry data only, following the schema. All coordinates and radii are in MILLIMETERS.
        Top/plan view only. Use polylines for straight open paths and closed outlines, and circles.
        Origin is near lower left of the object; +Y is up. Draw at true physical size, never page scale.
        Make furniture recognizable: include sensible frame outlines, cushions, seams, or other details.
        Honor explicit dimensions and units in the user request. Convert to millimeters.
        If dimensions or regional size are missing, pick reasonable dimensions and list them in assumptions.
        Explicitly describe overall width and depth in description. State uncertain design choices in assumptions.
        Wood can be indicated with a few subtle grain lines. No image, 3D model, labels, code or commands.
        Avoid excessive decorative lines and overlapping duplicates. Maximum 500 entities, 200 points each.
        Closed polylines must have at least 3 noncollinear points; do not repeat first point at end.
        For polylines set center to {x:0,y:0}, radius to 0. For circles set points to [], closed to false.
        Dimensions must be positive; coordinates within +/-100000 mm, overall size at most 100000 mm.
        """;

    public async Task<BlockSpec> GenerateAsync(string prompt, string key, string model, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 4000)
            throw new ArgumentException("Describe your block in 1–4000 characters.");
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Enter your OpenAI API key to generate a custom block.");
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Enter a model name.");
        using var schema = JsonDocument.Parse(Schema);
        var body = new
        {
            model = model.Trim(), store = false, instructions = Instructions, input = prompt,
            max_output_tokens = 12000,
            text = new { format = new { type = "json_schema", name = "cad_block", strict = true, schema = schema.RootElement } }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "API key was rejected. Check your OpenAI API key.",
                HttpStatusCode.TooManyRequests => "API quota or rate limit reached. Check your API billing and limits.",
                HttpStatusCode.BadRequest or HttpStatusCode.NotFound => "The API rejected this request. Check that your model supports Responses and Structured Outputs.",
                _ => $"AI service returned HTTP {(int)response.StatusCode}. Try again later."
            };
            throw new HttpRequestException(message);
        }
        // Bound response size before parsing or displaying untrusted model output.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellation)) > 0)
        {
            if (buffer.Length + count > 2_000_000) throw new InvalidDataException("The AI response was too large.");
            buffer.Write(chunk, 0, count);
        }
        using var json = JsonDocument.Parse(buffer.ToArray());
        var root = json.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed")
            throw new InvalidDataException("AI generation did not finish. Try a simpler description.");
        var output = new StringBuilder();
        foreach (var item in root.GetProperty("output").EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content)) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.GetProperty("type").GetString() == "refusal")
                    throw new InvalidDataException("The AI could not fulfill this request. Try another description.");
                if (part.GetProperty("type").GetString() == "output_text") output.Append(part.GetProperty("text").GetString());
            }
        }
        if (output.Length == 0) throw new InvalidDataException("The AI returned no geometry.");
        return BlockSpec.Parse(output.ToString());
    }

    public const string Schema = """
    {"type":"object","additionalProperties":false,"required":["name","description","assumptions","entities"],"properties":{
      "name":{"type":"string"},"description":{"type":"string"},"assumptions":{"type":"array","items":{"type":"string"}},
      "entities":{"type":"array","items":{"type":"object","additionalProperties":false,
        "required":["kind","points","center","radius","closed"],"properties":{
          "kind":{"type":"string","enum":["polyline","circle"]},
          "points":{"type":"array","items":{"$ref":"#/$defs/point"}},
          "center":{"$ref":"#/$defs/point"},"radius":{"type":"number"},"closed":{"type":"boolean"}
        }}}
    },"$defs":{"point":{"type":"object","additionalProperties":false,"required":["x","y"],"properties":{"x":{"type":"number"},"y":{"type":"number"}}}}}
    """;
}
