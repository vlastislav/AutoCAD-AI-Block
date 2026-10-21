using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security;
using App=Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace BlockAI.ReferenceExport;

public sealed record Stroke(string Layer,string Group,double[][] Points);
public sealed record DrawingText(string Text,double X,double Y);
public sealed class ReferenceExport
{
    [CommandMethod("AIREFEXPORT")]
    public static void Export()
    {
        var doc=App.DocumentManager.MdiActiveDocument;
        var directory=Environment.GetEnvironmentVariable("BLOCKAI_REFERENCE_OUTPUT")??throw new InvalidOperationException("Set BLOCKAI_REFERENCE_OUTPUT to a local export folder.");
        Directory.CreateDirectory(directory);
        var db=doc.Database;var stem=Path.GetFileNameWithoutExtension(db.Filename);
        using var tx=db.TransactionManager.StartTransaction();
        var layouts=(DBDictionary)tx.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);
        var summary=new List<object>();
        foreach(DBDictionaryEntry entry in layouts)
        {
            var layout=(Layout)tx.GetObject(entry.Value,OpenMode.ForRead);
            var strokes=new List<Stroke>();var texts=new List<DrawingText>();var warnings=new List<string>();
            void Read(Entity entity,string group,int depth)
            {
                if(depth>12||strokes.Count>=40000)return;
                try
                {
                    if(entity is BlockReference block)
                    {
                        var name=((BlockTableRecord)tx.GetObject(block.BlockTableRecord,OpenMode.ForRead)).Name;
                        using var objects=new DBObjectCollection();block.Explode(objects);
                        foreach(DBObject obj in objects)
                        {using(obj)if(obj is Entity child)Read(child,group.Length==0?name:group+" / "+name,depth+1);}
                    }
                    else if(entity is Curve curve)
                    {
                        var start=curve.StartParam;var end=curve.EndParam;
                        var steps=curve is Line?1:Math.Clamp((int)Math.Ceiling(Math.Abs(end-start)*12),24,400);
                        var points=Enumerable.Range(0,steps+1).Select(i=>curve.GetPointAtParameter(start+(end-start)*i/steps)).Select(p=>new[]{p.X,p.Y,p.Z}).ToArray();
                        if(points.All(p=>p.All(double.IsFinite)))strokes.Add(new(entity.Layer,group,points));
                    }
                    else if(entity is DBText text)texts.Add(new(text.TextString,text.Position.X,text.Position.Y));
                    else if(entity is MText mtext)texts.Add(new(mtext.Text,mtext.Location.X,mtext.Location.Y));
                }
                catch(System.Exception ex){if(warnings.Count<30)warnings.Add(entity.GetType().Name+": "+ex.Message);}
            }
            var record=(BlockTableRecord)tx.GetObject(layout.BlockTableRecordId,OpenMode.ForRead);
            foreach(ObjectId id in record)if(tx.GetObject(id,OpenMode.ForRead) is Entity entity)Read(entity,"",0);
            if(strokes.Count==0)continue;
            var points=strokes.SelectMany(s=>s.Points).ToArray();
            var minX=points.Min(p=>p[0]);var maxX=points.Max(p=>p[0]);var minY=points.Min(p=>p[1]);var maxY=points.Max(p=>p[1]);
            var width=Math.Max(1e-6,maxX-minX);var height=Math.Max(1e-6,maxY-minY);
            var safeLayout=string.Concat(layout.LayoutName.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
            var target=Path.Combine(directory,stem+"-"+safeLayout);
            var data=new{file=Path.GetFileName(db.Filename),layout=layout.LayoutName,units=db.Insunits.ToString(),bounds=new{minX,minY,maxX,maxY},
                blockGroups=strokes.GroupBy(s=>s.Group).Select(g=>new{name=g.Key,strokes=g.Count()}),strokes,texts,warnings};
            File.WriteAllText(target+".json",JsonSerializer.Serialize(data));
            string N(double value)=>value.ToString("0.######",CultureInfo.InvariantCulture);
            var svg=new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{N(minX-width*.03)} {N(-maxY-height*.03)} {N(width*1.06)} {N(height*1.06)}\"><rect x=\"{N(minX-width*.03)}\" y=\"{N(-maxY-height*.03)}\" width=\"{N(width*1.06)}\" height=\"{N(height*1.06)}\" fill=\"white\"/>");
            foreach(var stroke in strokes)
                svg.Append("<polyline fill=\"none\" stroke=\"#152235\" stroke-width=\"1\" vector-effect=\"non-scaling-stroke\" points=\"").Append(string.Join(" ",stroke.Points.Select(p=>N(p[0])+","+N(-p[1])))).Append("\"><title>").Append(SecurityElement.Escape(stroke.Group+" | "+stroke.Layer)).Append("</title></polyline>");
            foreach(var text in texts)svg.Append($"<text x=\"{N(text.X)}\" y=\"{N(-text.Y)}\" font-size=\"{N(height/180)}\" fill=\"#486077\">{SecurityElement.Escape(text.Text)}</text>");
            svg.Append("</svg>");File.WriteAllText(target+".svg",svg.ToString());
            summary.Add(new{layout=layout.LayoutName,curves=strokes.Count,texts=texts.Count,groups=strokes.Select(s=>s.Group).Distinct().Count(),width,height,warnings=warnings.Count});
        }
        File.WriteAllText(Path.Combine(directory,stem+"-summary.json"),JsonSerializer.Serialize(new{file=Path.GetFileName(db.Filename),units=db.Insunits.ToString(),layouts=summary}));
        doc.Editor.WriteMessage("\nReference export completed without saving the drawing.\n");
    }
}
