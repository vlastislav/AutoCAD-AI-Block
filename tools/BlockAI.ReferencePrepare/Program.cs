using BlockAI.Core;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Net;

string Option(string key,string fallback="") {var index=Array.IndexOf(args,key);return index>=0&&index+1<args.Length?args[index+1]:fallback;}
var installSource=Option("--install-from");
if(installSource.Length>0)
{
    var destination=Option("--destination",ReferenceLibrary.DefaultDirectory);
    var library=ReferenceLibrary.Load(installSource,out var warnings);
    if(library.Lessons.Length==0)throw new InvalidDataException("No valid analyzed reference lessons found.");
    Directory.CreateDirectory(destination);
    foreach(var lesson in library.Lessons)
    {
        if(args.Contains("--force")||!File.Exists(Path.Combine(destination,lesson.Id+".lesson.json")))ReferenceLibrary.Save(destination,lesson);
        foreach(var extension in new[]{".png",".initial-analysis.json",".geometry.json"})
        {var source=Path.Combine(installSource,lesson.Id+extension);var target=Path.Combine(destination,lesson.Id+extension);if(File.Exists(source)&&(args.Contains("--force")||!File.Exists(target)))File.Copy(source,target,true);}
    }
    Console.WriteLine($"Installed {library.Lessons.Length} named studies ({library.Lessons.Count(l=>l.Approved)} reviewed) to {destination}. Existing labels are preserved unless --force is supplied.");
    foreach(var warning in warnings)Console.WriteLine("Skipped: "+warning);
    return;
}
var manifestPath=Path.GetFullPath(Option("--manifest"));
var baseDirectory=Path.GetDirectoryName(manifestPath)!;
var candidates=JsonSerializer.Deserialize<Candidate[]>(File.ReadAllText(manifestPath),BlockSpec.JsonOptions)??throw new InvalidDataException("Empty manifest.");
var directory=Path.GetFullPath(Option("--library",Path.Combine(baseDirectory,"library")));
Directory.CreateDirectory(directory);
using var http=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(3)};
var analyzer=new ReferenceAnalyzer(new OllamaClient(http));
var model=Option("--model",DesignAgent.DefaultModel);
var report=new List<object>();var cards=new List<string>();var thumbnails=new List<(string Path,string Label)>();
for(var i=0;i<candidates.Length;i++)
{
    var c=candidates[i];var source=Path.GetFullPath(Path.Combine(baseDirectory,c.Source));
    try
    {
        byte[] png;ReferenceGeometry? measured=null;
        if(Path.GetExtension(source).Equals(".json",StringComparison.OrdinalIgnoreCase))
        {
            var drawing=JsonSerializer.Deserialize<ExportedDrawing>(File.ReadAllText(source),new JsonSerializerOptions(BlockSpec.JsonOptions){PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Empty drawing export.");
            var strokes=drawing.Strokes.Where(s=>s.Group==c.Group||s.Group.StartsWith(c.Group+" / ",StringComparison.Ordinal)).ToArray();
            if(strokes.Length==0)throw new InvalidDataException("No curves in the selected group.");
            var points=strokes.SelectMany(s=>s.Points).ToArray();var minX=points.Min(p=>p[0]);var maxX=points.Max(p=>p[0]);var minY=points.Min(p=>p[1]);var maxY=points.Max(p=>p[1]);
            if(Option("--geometry-group")==c.Group)
            {
                var crop=Option("--geometry-crop","0,0,1,1").Split(',').Select(n=>double.Parse(n,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if(crop.Length!=4||crop.Any(n=>!double.IsFinite(n)||n<0||n>1)||crop[2]<=0||crop[3]<=0||crop[0]+crop[2]>1||crop[1]+crop[3]>1)throw new InvalidDataException("Invalid normalized geometry crop.");
                var x0=minX+crop[0]*(maxX-minX);var x1=x0+crop[2]*(maxX-minX);var y0=minY+crop[1]*(maxY-minY);var y1=y0+crop[3]*(maxY-minY);
                var isolated=strokes.Where(s=>s.Points.All(p=>p[0]>=x0&&p[0]<=x1&&p[1]>=y0&&p[1]<=y1));
                measured=ReferenceGeometry.Create(isolated.Select(s=>s.Points.Select(p=>new Point2(p[0],p[1])).ToArray()));
            }
            var scale=Math.Min(840/Math.Max(.001,maxX-minX),840/Math.Max(.001,maxY-minY));
            using var image=new Bitmap(900,900);using var g=Graphics.FromImage(image);g.Clear(Color.White);g.SmoothingMode=SmoothingMode.AntiAlias;using var pen=new Pen(Color.FromArgb(25,35,45),1.5f);
            foreach(var stroke in strokes)
            {
                var path=stroke.Points.Select(p=>new PointF((float)(450+(p[0]-(maxX+minX)/2)*scale),(float)(450-(p[1]-(maxY+minY)/2)*scale))).ToArray();
                if(path.Length>=2)g.DrawLines(pen,path);
            }
            using var output=new MemoryStream();image.Save(output,ImageFormat.Png);png=output.ToArray();
            if(c.Crop is not null)
            {
                var b=c.Crop;
                if(b.Length!=4||b.Any(n=>!double.IsFinite(n))||b[0]<0||b[1]<0||b[2]<=0||b[3]<=0||b[0]+b[2]>1||b[1]+b[3]>1)throw new InvalidDataException("Invalid group-image crop.");
                using var cropped=new Bitmap(900,900);using var cg=Graphics.FromImage(cropped);cg.Clear(Color.White);cg.InterpolationMode=InterpolationMode.HighQualityBicubic;
                var rect=new RectangleF((float)(b[0]*900),(float)(b[1]*900),(float)(b[2]*900),(float)(b[3]*900));var factor=Math.Min(840/rect.Width,840/rect.Height);var w=rect.Width*factor;var h=rect.Height*factor;
                cg.DrawImage(image,new RectangleF((900-w)/2,(900-h)/2,w,h),rect,GraphicsUnit.Pixel);using var cropOutput=new MemoryStream();cropped.Save(cropOutput,ImageFormat.Png);png=cropOutput.ToArray();
            }
        }
        else
        {
            using var original=Image.FromFile(source);var b=c.Crop??[0,0,1,1];
            if(b.Length!=4||b.Any(n=>!double.IsFinite(n))||b[0]<0||b[1]<0||b[2]<=0||b[3]<=0||b[0]+b[2]>1||b[1]+b[3]>1)throw new InvalidDataException("Crop must be x,y,width,height within 0..1.");
            var rect=new RectangleF((float)(b[0]*original.Width),(float)(b[1]*original.Height),(float)(b[2]*original.Width),(float)(b[3]*original.Height));
            using var image=new Bitmap(900,900);using var g=Graphics.FromImage(image);g.Clear(Color.White);g.InterpolationMode=InterpolationMode.HighQualityBicubic;
            var scale=Math.Min(840/rect.Width,840/rect.Height);var width=rect.Width*scale;var height=rect.Height*scale;
            g.DrawImage(original,new RectangleF((900-width)/2,(900-height)/2,width,height),rect,GraphicsUnit.Pixel);
            using var output=new MemoryStream();image.Save(output,ImageFormat.Png);png=output.ToArray();
        }
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant();var id=hash[..20];
        File.WriteAllBytes(Path.Combine(directory,id+".png"),png);
        if(measured is not null)File.WriteAllText(Path.Combine(directory,id+".geometry.json"),JsonSerializer.Serialize(measured,BlockSpec.JsonOptions));
        thumbnails.Add((Path.Combine(directory,id+".png"),c.Group));
        ReferenceLesson? lesson=null;
        var lessonPath=Path.Combine(directory,id+".lesson.json");
        if(File.Exists(lessonPath)&&!args.Contains("--force"))lesson=JsonSerializer.Deserialize<ReferenceLesson>(File.ReadAllText(lessonPath),BlockSpec.JsonOptions);
        else if(args.Contains("--analyze"))
        {
            Console.WriteLine($"Analyzing {i+1}/{candidates.Length}: {c.Source} / {c.Group}");
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
            lesson=await analyzer.AnalyzeAsync(png,c.Source.Replace("-Model.json",".dwg").Replace("-preview.png",".dwg"),c.Group,model,timeout.Token);
            if(!string.IsNullOrWhiteSpace(c.Name))lesson=lesson with {Analysis=lesson.Analysis with {Name=c.Name}};
            ReferenceLibrary.Save(directory,lesson);
        }
        lesson?.Validate();
        var name=lesson?.Analysis.Name??c.Name??c.Group;
        var summary=lesson is null?"Prepared; not analyzed yet.":$"{lesson.Analysis.Category} / {lesson.Analysis.View} / confidence {lesson.Analysis.Confidence:0.00} / {(lesson.Approved?"reviewed":"not enabled for generation")}";
        report.Add(new{id,source=c.Source,group=c.Group,name,summary});
        cards.Add($"<article><img src=\"{id}.png\" alt=\"Reference crop\"><h2>{WebUtility.HtmlEncode(name)}</h2><p>{WebUtility.HtmlEncode(summary)}</p><small>{WebUtility.HtmlEncode(c.Source+" / "+c.Group)}</small>"+(lesson is null?"":$"<p>{WebUtility.HtmlEncode(lesson.CurationNote)}</p><pre>{WebUtility.HtmlEncode(JsonSerializer.Serialize(lesson.Analysis,new JsonSerializerOptions(BlockSpec.JsonOptions){WriteIndented=true}))}</pre><a href=\"{id}.lesson.json\">Analysis JSON</a>")+"</article>");
        Console.WriteLine($"Prepared {i+1}: {name} — {summary}");
    }
    catch(Exception ex){Console.Error.WriteLine($"FAILED {c.Source} / {c.Group}: {ex.Message}");report.Add(new{source=c.Source,group=c.Group,error=ex.Message});}
}
File.WriteAllText(Path.Combine(directory,"catalog.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions(BlockSpec.JsonOptions){WriteIndented=true}));
File.WriteAllText(Path.Combine(directory,"index.html"),"<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Local furniture reference studies</title><style>body{font:15px/1.5 Segoe UI,sans-serif;background:#eef2f6;color:#182739;margin:30px}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:22px}article{background:white;padding:18px;border-radius:12px;min-width:0}img{width:100%;height:300px;object-fit:contain}h2{font-size:19px}pre{font:12px/1.5 Consolas,monospace;white-space:pre-wrap;overflow-wrap:anywhere;max-height:350px;overflow:auto}small{overflow-wrap:anywhere}</style><h1>Local furniture reference studies</h1><p>AI-extracted observations, not verified construction facts or trained model weights. Source artwork stays local. Generation receives descriptive lessons, not these outlines.</p><main>"+string.Join("",cards)+"</main></html>");
Console.WriteLine("Catalog: "+Path.Combine(directory,"index.html"));
if(thumbnails.Count>0)
{
    using var sheet=new Bitmap(1800,350*((thumbnails.Count+3)/4));using var g=Graphics.FromImage(sheet);g.Clear(Color.White);using var font=new Font("Segoe UI",11);
    for(var i=0;i<thumbnails.Count;i++){using var image=Image.FromFile(thumbnails[i].Path);g.DrawImage(image,new Rectangle(i%4*450+65,i/4*350+10,300,300));g.DrawString((i+1)+". "+thumbnails[i].Label,font,Brushes.Black,new RectangleF(i%4*450+10,i/4*350+313,430,34));}
    sheet.Save(Path.Combine(directory,"catalog-preview.png"),ImageFormat.Png);
}

record Candidate(string Source,string Group,string? Name=null,double[]? Crop=null);
record Stroke(string Group,double[][] Points);
record ExportedDrawing(Stroke[] Strokes);
