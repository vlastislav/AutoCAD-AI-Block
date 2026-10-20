using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlockAI.Core;

/// <summary>Local inference only. No API key, redirects or configurable remote endpoint.</summary>
public sealed class OllamaClient(HttpClient http)
{
    public const string DefaultModel = "qwen2.5:7b";
    private const string Endpoint = "http://127.0.0.1:11434";

    public async Task<string[]> ListModelsAsync(CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + "/api/tags");
        using var json = await SendAsync(request, cancellation);
        return json.RootElement.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString()!)
            .Where(m => !m.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public async Task<BlockSpec> GenerateAsync(string prompt, string model, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 4000)
            throw new ArgumentException("Describe your block in 1–4000 characters.");
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Enter a downloaded Ollama model name.");
        model = model.Trim();
        if (model.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a local model; cloud models are disabled in this mode.");
        var schema = JsonNode.Parse(AiClient.Schema)!;
        schema["properties"]!["entities"]!["maxItems"] = 40;
        schema["properties"]!["entities"]!["items"]!["properties"]!["points"]!["maxItems"] = 32;
        schema["properties"]!["assumptions"]!["maxItems"] = 8;
        var input = prompt;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var body = new
            {
                model, prompt = input,
                system = AiClient.Instructions
                    + "\nUse 6–12 simple entities when possible. No more than 40 entities, 32 points per entity."
                    + "\nReturn compact JSON. Do not add whitespace after the final closing brace."
                    + "\nExample closed rectangle primitive: {\"kind\":\"polyline\",\"points\":[{\"x\":0,\"y\":0},{\"x\":100,\"y\":0},{\"x\":100,\"y\":200},{\"x\":0,\"y\":200}],\"center\":{\"x\":0,\"y\":0},\"radius\":0,\"closed\":true}."
                    + "\nUse this JSON schema exactly:\n" + schema.ToJsonString(),
                format = schema, stream = false, keep_alive = "5m",
                options = new { temperature = 0, num_ctx = 8192, num_predict = 4096 }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + "/api/generate")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            using var json = await SendAsync(request, cancellation);
            var root = json.RootElement;
            if (!root.TryGetProperty("done", out var done) || !done.GetBoolean() ||
                (root.TryGetProperty("done_reason", out var reason) && reason.GetString() == "length"))
                throw new InvalidDataException("Local generation did not finish. Request a simpler block.");
            if (!root.TryGetProperty("response", out var response) || string.IsNullOrWhiteSpace(response.GetString()))
                throw new InvalidDataException("Ollama returned no geometry.");
            var geometry = response.GetString()!;
            try { return BlockSpec.Parse(geometry); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                if (attempt == 1) throw new InvalidDataException("Local AI returned invalid geometry: " + ex.Message + " Try a simpler description.", ex);
                input = prompt + "\nYour previous geometry failed validation: " + ex.Message
                    + "\nReturn the entire corrected block JSON. Previous result:\n" + geometry;
            }
        }
        throw new InvalidOperationException("Generation failed.");
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException("Cannot reach local Ollama. Click Start local AI, then Check models.", ex);
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                throw new HttpRequestException("Unexpected redirect from local Ollama.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new HttpRequestException("Local model not found. Download it with D:\\Ollama\\app\\ollama.exe pull " + DefaultModel + ", then Check models.");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Ollama returned HTTP {(int)response.StatusCode}. Check D:\\Ollama\\logs\\server-error.log; it may need more available memory.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, cancellation)) > 0)
            {
                if (buffer.Length + count > 2_000_000) throw new InvalidDataException("Local AI response was too large.");
                buffer.Write(chunk, 0, count);
            }
            return JsonDocument.Parse(buffer.ToArray());
        }
    }
}
