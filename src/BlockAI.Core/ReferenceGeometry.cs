using System.Text.Json;

namespace BlockAI.Core;

public sealed record ReferenceOutline(int Id,Point2[] Points,bool Closed,double X,double Y,double W,double H);
public sealed record ReferenceRelation(int A,int B,string Kind);
public sealed record ReferenceGeometry(double AspectRatio,ReferenceOutline[] Outlines,ReferenceRelation[] Relations)
{
    public void Validate()
    {
        if(!double.IsFinite(AspectRatio)||AspectRatio<=0||AspectRatio>100||Outlines is null||Outlines.Length is <1 or >64||Relations is null||Relations.Length>80)
            throw new InvalidDataException("Invalid reference geometry limits.");
        if(Outlines.Select(o=>o.Id).Distinct().Count()!=Outlines.Length)throw new InvalidDataException("Duplicate outline IDs.");
        foreach(var o in Outlines)
            if(o.Points is null||o.Points.Length is <2 or >40||o.Points.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)||p.X<0||p.X>1||p.Y<0||p.Y>1)
                ||new[]{o.X,o.Y,o.W,o.H}.Any(n=>!double.IsFinite(n)||n<0||n>1))throw new InvalidDataException("Reference outlines must be normalized and bounded.");
        if(Outlines.Sum(o=>o.Points.Length)>400||Relations.Any(r=>r.A==r.B||!Outlines.Any(o=>o.Id==r.A)||!Outlines.Any(o=>o.Id==r.B)||r.Kind is not("bounds-contained" or "same-size")))
            throw new InvalidDataException("Invalid reference relationships.");
    }

    public static ReferenceGeometry Create(IEnumerable<Point2[]> source,bool joinStrokes=false)
    {
        var paths=source.ToArray();
        if(joinStrokes)paths=JoinStrokes(paths);
        var all=paths.SelectMany(p=>p).ToArray();
        if(all.Length==0||all.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)))throw new InvalidDataException("No finite reference geometry.");
        var minX=all.Min(p=>p.X);var minY=all.Min(p=>p.Y);var w=all.Max(p=>p.X)-minX;var h=all.Max(p=>p.Y)-minY;
        if(w<=0||h<=0||paths.Length>64)throw new InvalidDataException("Isolate one object with at most 64 outlines.");
        var outlines=paths.Select((path,id)=>
        {
            var points=Simplify(path.Select(p=>new Point2(Math.Round((p.X-minX)/w,4),Math.Round((p.Y-minY)/h,4))).ToArray(),.002);
            var x=points.Min(p=>p.X);var y=points.Min(p=>p.Y);
            return new ReferenceOutline(id,points,Distance(points[0],points[^1])<.003,x,y,points.Max(p=>p.X)-x,points.Max(p=>p.Y)-y);
        }).ToArray();
        var relations=new List<ReferenceRelation>();
        foreach(var a in outlines)foreach(var b in outlines.Where(b=>b.Id!=a.Id))
        {
            if(a.Closed&&a.W*a.H>b.W*b.H+.001&&b.X>=a.X-.002&&b.Y>=a.Y-.002&&b.X+b.W<=a.X+a.W+.002&&b.Y+b.H<=a.Y+a.H+.002)
                relations.Add(new(a.Id,b.Id,"bounds-contained"));
            if(a.Id<b.Id&&Math.Abs(a.W-b.W)<.005&&Math.Abs(a.H-b.H)<.005)relations.Add(new(a.Id,b.Id,"same-size"));
        }
        var result=new ReferenceGeometry(Math.Round(w/h,4),outlines,relations.Take(80).ToArray());result.Validate();return result;
    }

    private static double Distance(Point2 a,Point2 b)=>Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    private static Point2[][] JoinStrokes(Point2[][] source)
    {
        if(source.Length>512||source.Any(p=>p.Length<2||p.Any(v=>!double.IsFinite(v.X)||!double.IsFinite(v.Y))))throw new InvalidDataException("Isolate finite strokes from a single object before joining.");
        var all=source.SelectMany(p=>p).ToArray();if(all.Length==0)return source;
        var tolerance=Math.Max(all.Max(p=>p.X)-all.Min(p=>p.X),all.Max(p=>p.Y)-all.Min(p=>p.Y))*1e-6;
        var paths=source.Select(p=>p.ToArray()).ToList();
        while(true)
        {
            var endpoints=paths.SelectMany((p,i)=>Distance(p[0],p[^1])<=tolerance?Array.Empty<(int Index,bool End,Point2 Point)>():new (int Index,bool End,Point2 Point)[]{(i,false,p[0]),(i,true,p[^1])}).ToArray();
            var merged=false;
            foreach(var endpoint in endpoints)
            {
                var candidates=endpoints.Where(e=>e.Index!=endpoint.Index&&Distance(e.Point,endpoint.Point)<=tolerance).ToArray();
                if(candidates.Length!=1)continue;var partner=candidates[0];
                if(endpoints.Count(e=>e.Index!=partner.Index&&Distance(e.Point,partner.Point)<=tolerance)!=1)continue;
                var a=endpoint.End?paths[endpoint.Index]:paths[endpoint.Index].Reverse().ToArray();
                var b=partner.End?paths[partner.Index].Reverse().ToArray():paths[partner.Index];
                var joined=a.Concat(b.Skip(1)).ToArray();
                paths[endpoint.Index]=joined;paths.RemoveAt(partner.Index);merged=true;break;
            }
            if(!merged)return paths.ToArray();
        }
    }
    private static Point2[] Simplify(Point2[] points,double tolerance)
    {
        if(points.Length<=2)return points;
        var first=points[0];var last=points[^1];var dx=last.X-first.X;var dy=last.Y-first.Y;var length=dx*dx+dy*dy;
        var maximum=0d;var index=0;
        for(var i=1;i<points.Length-1;i++)
        {
            var t=length==0?0:Math.Clamp(((points[i].X-first.X)*dx+(points[i].Y-first.Y)*dy)/length,0,1);
            var distance=Distance(points[i],new(first.X+t*dx,first.Y+t*dy));
            if(distance>maximum){maximum=distance;index=i;}
        }
        return maximum<=tolerance?[first,last]:Simplify(points[..(index+1)],tolerance)[..^1].Concat(Simplify(points[index..],tolerance)).ToArray();
    }

    public static ReferenceGeometry? Load(string directory,string id)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(id,@"^[a-zA-Z0-9_-]{1,64}$"))return null;
        try
        {
            var file=Path.Combine(directory,id+".geometry.json");if(!File.Exists(file)||new FileInfo(file).Length>64000)return null;
            var geometry=JsonSerializer.Deserialize<ReferenceGeometry>(File.ReadAllText(file),BlockSpec.JsonOptions);geometry?.Validate();return geometry;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException){return null;}
    }
}
