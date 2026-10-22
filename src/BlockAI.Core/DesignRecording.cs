using System.Diagnostics;
using System.Text.Json;

namespace BlockAI.Core;

public sealed record RecordedGeometry(int PartId,string Role,Primitive Geometry);
public sealed record CoordinateFit(double MinX,double MinY,double ScaleX,double ScaleY);
public sealed record ConstructionFrame(string Stage,string Message,string Units,RecordedGeometry[] Geometry,int? FocusPart=null,CoordinateFit? Fit=null,string View="plan");
public sealed record DesignEvent(double ElapsedMs,int Attempt,string Stage,string Message,
    DesignPlan? Plan=null,ConstructionFrame? Frame=null,VisualReview? Review=null,string? RawResponse=null,ReferenceLesson[]? References=null,LayoutDraft? Layout=null);

/// <summary>Observable requests and engine output only. No model reasoning, credentials or images.</summary>
public sealed class DesignRecording
{
    public int SchemaVersion { get; init; }=1;
    public string Id { get; init; }=Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; init; }=DateTimeOffset.UtcNow;
    public string Prompt { get; init; }="";
    public string Model { get; init; }="";
    public string? ReviewModel { get; init; }
    public bool Demonstration { get; init; }
    public string Status { get; private set; }="running";
    public List<DesignEvent> Events { get; init; }=[];
    private readonly Stopwatch clock=Stopwatch.StartNew();

    // Diagnostic observation must never change generation behavior.
    public void Add(int attempt,string stage,string message,DesignPlan? plan=null,
        ConstructionFrame? frame=null,VisualReview? review=null,string? raw=null,ReferenceLesson[]? references=null,LayoutDraft? layout=null)
    {
        try { Events.Add(new(clock.Elapsed.TotalMilliseconds,attempt,stage,message,plan,frame,review,raw,references,layout)); }
        catch { /* Recording is best effort. */ }
    }
    public void Finish(string status,string message,int attempt)
    {Status=status;Add(attempt,status,message);clock.Stop();}

    public string ToJson()=>JsonSerializer.Serialize(this,BlockSpec.JsonOptions);
    public string ToHtml()
    {
        using var stream=typeof(DesignRecording).Assembly.GetManifestResourceStream("BlockAI.DesignViewer.html")
            ??throw new InvalidOperationException("The embedded design viewer is missing.");
        using var reader=new StreamReader(stream);
        // Default System.Text.Json escaping prevents user content from closing the script element.
        const string slot="<script id=\"recording\" type=\"application/json\">null</script>";
        var template=reader.ReadToEnd();
        if(!template.Contains(slot,StringComparison.Ordinal))throw new InvalidOperationException("The viewer recording slot is missing.");
        return template.Replace(slot,"<script id=\"recording\" type=\"application/json\">"+ToJson()+"</script>",StringComparison.Ordinal);
    }

    public bool TrySave(string directory,out string? htmlPath,out string? error)
    {
        htmlPath=null;error=null;
        try
        {
            Directory.CreateDirectory(directory);
            var stem=Path.Combine(directory,$"{StartedAt:yyyyMMdd-HHmmss}-{Id}");
            File.WriteAllText(stem+".json",ToJson());
            var target=stem+".html";
            File.WriteAllText(target,ToHtml());
            htmlPath=target;
            return true;
        }
        catch(Exception ex) {error="Could not save design recording: "+ex.Message;return false;}
    }
}
