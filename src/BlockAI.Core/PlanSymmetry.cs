using System.Text.RegularExpressions;

namespace BlockAI.Core;

public static class PlanSymmetry
{
    public static bool Required(string prompt,bool enabled)=>enabled&&!Regex.IsMatch(prompt,
        @"\basymmetr\w*\b|\b(?:L|U)[-\s]+shaped\b|\b(?:corner|chaise|offset|one[-\s]sided|left[-\s]hand|right[-\s]hand)\b",RegexOptions.IgnoreCase);

    public static void Validate(BlockSpec drawing,double tolerance=2)
    {
        var bounds=drawing.Bounds();var axis=bounds.MinX+bounds.Width/2;
        var edges=new List<(Point2 A,Point2 B)>();
        foreach(var e in drawing.Entities)
        {
            var points=e.Kind=="circle"?Enumerable.Range(0,96).Select(i=>new Point2(e.Center.X+e.Radius*Math.Cos(i*Math.PI/48),e.Center.Y+e.Radius*Math.Sin(i*Math.PI/48))).ToArray():e.Points;
            for(var i=0;i<points.Length-((e.Closed||e.Kind=="circle")?0:1);i++)edges.Add((points[i],points[(i+1)%points.Length]));
        }
        foreach(var edge in edges)for(var sample=0;sample<=4;sample++)
        {
            var t=sample/4d;var p=new Point2(2*axis-(edge.A.X+(edge.B.X-edge.A.X)*t),edge.A.Y+(edge.B.Y-edge.A.Y)*t);
            if(edges.All(e=>Distance(p,e.A,e.B)>tolerance))throw new InvalidDataException(FormattableString.Invariant(
                $"Left/right symmetry failed near x={p.X:0.#}, y={p.Y:0.#} mm, mirrored around x={axis:0.#} mm (tolerance {tolerance:0.#} mm). Center major surfaces with align=center; create paired parts with ONE off-center definition and mirror=true, align=none. Symmetric curves/polygons need mirrored local points. Correct the implicated outlines; do not redesign unrelated parts."));
        }
    }

    private static double Distance(Point2 p,Point2 a,Point2 b)
    {
        var dx=b.X-a.X;var dy=b.Y-a.Y;var length=dx*dx+dy*dy;
        var t=length==0?0:Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/length,0,1);
        return Math.Sqrt(Math.Pow(p.X-a.X-t*dx,2)+Math.Pow(p.Y-a.Y-t*dy,2));
    }
}
