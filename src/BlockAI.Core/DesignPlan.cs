using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlockAI.Core;

public sealed record DesignPart(string Role, string Shape, double X, double Y, double W, double H,
    double Detail, int Count, string Repeat, double Gap, bool Mirror, string Align, double Inset, Point2[] Points,
    string From="",string To="",int? Level=null,bool Opaque=true,double? Z=null,double? Height=null)
{
    // Older recordings omitted levels. Keep their existing support-under-surface convention.
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveLevel=>Level??(Role.ToLowerInvariant() switch {"back" or "backrest"=>20,var r when r.Contains("support")||r.Contains("leg")=>0,_=>10});
}

public sealed record DesignPlan(string Name, string Description, double Width, double Depth, DesignPart[] Parts,double Height=800)
{
    public static DesignPlan Parse(string json) => JsonSerializer.Deserialize<DesignPlan>(json, BlockSpec.JsonOptions)
        ?? throw new InvalidDataException("The model returned no design plan.");

    public BlockSpec Build(string prompt,Action<ConstructionFrame>? observe=null,bool useVerticalDepth=false)
    {
        var origins=new Dictionary<Primitive,int>();
        var built=new List<(string Role,Primitive Geometry)>();
        var partIndex=-1;
        void Emit(string stage,string message,int? focus=null,string units="normalized")
        {
            if(observe is null)return;
            try {observe(new(stage,message+(built.Count>500?" Snapshot limited to the first 500 outlines; the full assembly still undergoes validation.":""),units,built.Take(500).Select(p=>new RecordedGeometry(origins.GetValueOrDefault(p.Geometry,-1),p.Role,p.Geometry)).ToArray(),focus));}
            catch { /* A diagnostic observer cannot interrupt construction. */ }
        }
        if(string.IsNullOrWhiteSpace(Name)||Name.Length>80||string.IsNullOrWhiteSpace(Description)||Description.Length>1000)
            throw new InvalidDataException("A design needs a short name and description.");
        // Reuse unit parsing only; no furniture geometry or category selection is used here.
        var dimensions = new FurnitureDesign("other",Width,Depth,1,"modern","rectangular",[]).ApplyExplicitDimensions(prompt);
        var width=dimensions.Width; var depth=dimensions.Depth;
        if(!double.IsFinite(width)||!double.IsFinite(depth)||width<100||depth<100||width>10000||depth>10000)
            throw new InvalidDataException("Specify an overall size between 100 and 10000 mm on each axis.");
        if(Parts is null||Parts.Length is <2 or >24) throw new InvalidDataException("Use 2–24 meaningful parts.");
        (double Surface,int Level) TopDepth(DesignPart p)=> (useVerticalDepth?(p.Z??0)+(p.Height??0):0,p.EffectiveLevel);
        var noCushions=Regex.IsMatch(prompt,@"\b(?:no|without)\s+(?:(?:a|any|seat)\s+)?(?:cushions?|cushioning|upholstery|padding)|\b(?:uncushioned|unupholstered)\b",RegexOptions.IgnoreCase);
        var noArms=Regex.IsMatch(prompt,@"\barmless\b|\b(?:no|without)\s+(?:arms|armrests?)\b",RegexOptions.IgnoreCase);
        var chair=Regex.IsMatch(prompt,@"\b(?:chair|armchair)\b",RegexOptions.IgnoreCase);
        foreach(var part in Parts)
        {
            partIndex++;
            Emit("part-start","Constructing part "+(partIndex+1)+": "+part?.Role,partIndex);
            var localOutlines=new HashSet<string>();
            if(part is null||string.IsNullOrWhiteSpace(part.Role)||part.Role.Length>60||part.Points is null)
                throw new InvalidDataException("Every part needs a role and a points array.");
            if(part.Level is < -100 or >100)throw new InvalidDataException("Part level must be between -100 and 100; higher levels are closer to the top-view camera.");
            var role=part.Role.ToLowerInvariant();
            if(noCushions&&Regex.IsMatch(role,"cushion|upholster|pad|pillow")) throw new InvalidDataException("The request forbids cushions/padding. Remove those parts.");
            if(noCushions&&role.Contains("seat")&&part.Inset>0) throw new InvalidDataException("No cushion means no inner seat pad/seam outline. Set seat inset=0.");
            if(noArms&&Regex.IsMatch(role,"armrest|^arm$|^arms$")) throw new InvalidDataException("The request forbids armrests.");
            double[] numbers=[part.X,part.Y,part.W,part.H,part.Detail,part.Gap,part.Inset];
            if(numbers.Any(v=>!double.IsFinite(v))||Math.Abs(part.X)>4||Math.Abs(part.Y)>4||part.W<=0||part.H<=0||part.W>4||part.H>4||part.Detail<0||part.Detail>.45||part.Inset<0||part.Inset>.2||part.Gap<0||part.Gap>.2)
                throw new InvalidDataException("Part boxes need finite positions between -4 and 4 and positive sizes <=4; detail <=0.45, inset/gap <=0.2.");
            if(part.Count is <1 or >8||part.Repeat is not("none" or "row" or "column")||part.Align is not("none" or "center" or "left" or "right"))
                throw new InvalidDataException("Invalid repetition/alignment. Use none/row/column and none/center/left/right.");
            if(part.Repeat=="none"&&part.Count!=1) throw new InvalidDataException("Non-repeated parts must have count=1.");
            var x=part.Align switch {"center"=>(1-part.W)/2,"left"=>0,"right"=>1-part.W,_=>part.X};
            var y=part.Y;var height=part.H;
            if(!string.IsNullOrWhiteSpace(part.From)||!string.IsNullOrWhiteSpace(part.To))
            {
                if(part.Repeat!="none"||part.Shape is not("rectangle" or "rounded")||part.From==part.To)
                    throw new InvalidDataException("Attachment links need different from/to roles, rectangle/rounded shape, and repeat=none. Mirror is supported.");
                Primitive Anchor(string role)
                {
                    var candidates=Parts.Where(p=>p is not null&&p.Role==role).ToArray();
                    if(candidates.Length!=1) throw new InvalidDataException($"Attachment anchor '{role}' must identify exactly one part.");
                    var a=candidates[0];
                    if(!string.IsNullOrEmpty(a.From)||!string.IsNullOrEmpty(a.To)||a.Repeat!="none"||a.Points is null||
                        new[]{a.X,a.Y,a.W,a.H,a.Detail}.Any(v=>!double.IsFinite(v))||a.W<=0||a.H<=0)
                        throw new InvalidDataException("Anchors must be finite, non-repeated, unlinked parts; cyclic links are not supported.");
                    var ax=a.Align switch {"center"=>(1-a.W)/2,"left"=>0,"right"=>1-a.W,_=>a.X};
                    return new("polyline",ShapePoints(a).Select(p=>new Point2(ax+p.X*a.W,a.Y+p.Y*a.H)).ToArray(),new(0,0),0,a.Shape is not("path" or "curve"));
                }
                var a=Anchor(part.From);var b=Anchor(part.To);
                var left=Math.Max(a.Points.Min(p=>p.X),b.Points.Min(p=>p.X))+part.W/2;
                var right=Math.Min(a.Points.Max(p=>p.X),b.Points.Max(p=>p.X))-part.W/2;
                if(left>right)throw new InvalidDataException("Attachment anchors have no shared horizontal span wide enough for this connector.");
                var center=right-left<2e-7?(left+right)/2:Math.Clamp(x+part.W/2,left+1e-7,right-1e-7);
                double[] Crossings(Primitive anchor)=>Edges(anchor).Select(e=>Intersection(new(center,-16),new(center,16),e.A,e.B))
                    .Where(t=>t is not null).Select(t=>-16+32*t!.Value).OrderBy(v=>v).ToArray();
                var ac=Crossings(a);var bc=Crossings(b);
                if(ac.Length==0||bc.Length==0)throw new InvalidDataException("The connector cannot intersect its requested anchors at this X position.");
                var start=ac.Max();var end=bc.Min();
                if(end<start)throw new InvalidDataException("Attachment 'to' must be behind 'from' (+Y) without overlapping its surface.");
                x=center-part.W/2;y=start-.015;height=end+.015-y;
            }
            // Fit the complete assembly below; clipping or moving individual parts breaks joints.
            var cellW=part.Repeat=="row"?(part.W-part.Gap*(part.Count-1))/part.Count:part.W;
            var cellH=part.Repeat=="column"?(height-part.Gap*(part.Count-1))/part.Count:height;
            if(cellW<.002||cellH<.002) throw new InvalidDataException("Repeated parts have no space. Reduce count or gap.");
            for(var i=0;i<part.Count;i++)
            {
                var px=x+(part.Repeat=="row"?i*(cellW+part.Gap):0);
                var py=y+(part.Repeat=="column"?i*(cellH+part.Gap):0);
                var points=ShapePoints(part);
                Primitive Map(double inset)
                {
                    var iw=cellW-inset*2; var ih=cellH-inset*2;
                    if(iw<=.001||ih<=.001) throw new InvalidDataException("Inset consumes the entire part.");
                    return new("polyline",points.Select(p=>new Point2(px+inset+p.X*iw,py+inset+p.Y*ih)).ToArray(),new(0,0),0,
                        part.Shape is not("path" or "curve"));
                }
                void Add(Primitive geometry)
                {
                    void Unique(Primitive outline)
                    {
                        if(outline.Points.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)||Math.Abs(p.X)>16||Math.Abs(p.Y)>16))
                            throw new InvalidDataException($"Actual outline of '{part.Role}' is nonfinite or exceeds the supported coordinate range (-16 to 16).");
                        var key=string.Join(";",outline.Points.Select(p=>FormattableString.Invariant($"{p.X:F6},{p.Y:F6}")).OrderBy(v=>v,StringComparer.Ordinal));
                        if(localOutlines.Add(key)) {built.Add((role,outline));origins[outline]=partIndex;}
                    }
                    Unique(geometry);
                    if(part.Mirror&&Math.Abs(px-(1-px-cellW))>.00001)
                        Unique(geometry with {Points=geometry.Points.Select(p=>new Point2(1-p.X,p.Y)).ToArray()});
                }
                Add(Map(0));
                if(part.Inset>0) Add(Map(part.Inset*Math.Min(cellW,cellH)));
            }
            Emit("part-built",FormattableString.Invariant($"{part.Role}: requested x={part.X:0.####}, y={part.Y:0.####}, w={part.W:0.####}, h={part.H:0.####}; resolved x={x:0.####}, y={y:0.####}, w={part.W:0.####}, h={height:0.####}. Alignment={part.Align}; repeat={part.Repeat} × {part.Count}; mirror={part.Mirror}; attachment={part.From} → {part.To}."),partIndex);
        }
        Emit("assembled","All parts constructed; constraint checks follow.");
        if(built.Count>160) throw new InvalidDataException("The assembled design has too many parts.");
        if(chair&&(!built.Any(p=>p.Role.Contains("seat"))||!built.Any(p=>p.Role.Contains("back"))))
            throw new InvalidDataException("A chair plan must include separate seat and backrest roles.");
        static (double X,double Y,double W,double H) Bounds(Primitive p)
        {var x=p.Points.Min(p=>p.X);var y=p.Points.Min(p=>p.Y);return(x,y,p.Points.Max(p=>p.X)-x,p.Points.Max(p=>p.Y)-y);}
        var signatures=new Dictionary<(string Outline,(double,int) Depth),string>();
        var uniqueBuilt=new List<(string Role,Primitive Geometry)>();
        foreach(var item in built)
        {
            var signature=string.Join(";",item.Geometry.Points.Select(p=>FormattableString.Invariant($"{p.X:F6},{p.Y:F6}")).OrderBy(v=>v,StringComparer.Ordinal));
            var key=(signature,TopDepth(Parts[origins[item.Geometry]]));
            if(!signatures.TryAdd(key,item.Role))
            {
                if(signatures[key]==item.Role) continue;
                throw new InvalidDataException($"Parts '{signatures[key]}' and '{item.Role}' duplicate the same outline at the same level. Remove one or assign the intended above/below levels.");
            }
            uniqueBuilt.Add(item);
        }
        built=uniqueBuilt;
        Emit("deduplicated","Removed identical repeated outlines with the same role; conflicting duplicates are rejected.");
        if(chair&&Regex.IsMatch(prompt,@"\b(?:oval|elliptical|round)\s+seat\b",RegexOptions.IgnoreCase)&&!Parts.Any(p=>p.Role.Contains("seat",StringComparison.OrdinalIgnoreCase)&&p.Shape=="ellipse"))
            throw new InvalidDataException("The request asks for an oval/round seat. Use the ellipse tool for the seat outline.");
        if(chair&&Regex.IsMatch(prompt,@"\btapered\s+seat\b",RegexOptions.IgnoreCase)&&!Parts.Any(p=>p.Role.Contains("seat",StringComparison.OrdinalIgnoreCase)&&p.Shape is "trapezoid" or "polygon"))
            throw new InvalidDataException("The request asks for a tapered seat. Use a trapezoid or a tapered custom polygon.");
        var slatRequest=Regex.Match(prompt,@"\b(?<n>[2-8]|two|three|four|five|six|seven|eight)\s+(?:back\s+)?slats\b",RegexOptions.IgnoreCase);
        if(slatRequest.Success)
        {
            var requested=slatRequest.Groups["n"].Value.ToLowerInvariant() switch {"two"=>2,"three"=>3,"four"=>4,"five"=>5,"six"=>6,"seven"=>7,"eight"=>8,var n=>int.Parse(n,CultureInfo.InvariantCulture)};
            if(built.Count(p=>p.Role.Contains("slat"))!=requested) throw new InvalidDataException($"The request needs exactly {requested} slats. Label their role back_slat and use repetition or individual parts.");
        }
        var seats=built.Where(p=>p.Role=="seat").ToArray();
        var backs=built.Where(p=>p.Role is "back" or "backrest").ToArray();
        if(chair&&seats.Length>0&&backs.Length>0)
        {
            var seatBox=Bounds(seats[0].Geometry);var backBox=Bounds(backs[0].Geometry);
            if(seatBox.H<.35||backBox.H>.4||backBox.Y+backBox.H/2<=seatBox.Y+seatBox.H/2)
                throw new InvalidDataException(FormattableString.Invariant($"Wrong plan-view proportions: seat y={seatBox.Y:0.###}, h={seatBox.H:0.###}; backrest y={backBox.Y:0.###}, h={backBox.H:0.###}. Set seat h>=0.35, backrest h<=0.4 and keep the backrest center behind the seat center. Intentional projection overlap is allowed. A seat is a broad horizontal surface in TOP view; do not copy front-elevation coordinates."));
        }
        if(chair&&seats.Length>0&&backs.Length>0)
        {
            var reachable=new HashSet<int>(Enumerable.Range(0,built.Count).Where(i=>built[i].Role=="seat"));
            var queue=new Queue<int>(reachable);
            while(queue.Count>0)
            {
                var current=queue.Dequeue();
                var a=Bounds(built[current].Geometry);
                for(var i=0;i<built.Count;i++)
                {
                    if(reachable.Contains(i))continue;
                    var b=Bounds(built[i].Geometry);
                    if(a.X<=b.X+b.W+.003&&a.X+a.W+.003>=b.X&&a.Y<=b.Y+b.H+.003&&a.Y+a.H+.003>=b.Y
                        &&Touches(built[current].Geometry,built[i].Geometry))
                    {reachable.Add(i);queue.Enqueue(i);}
                }
            }
            if(Enumerable.Range(0,built.Count).Any(i=>built[i].Role is "back" or "backrest"&&!reachable.Contains(i)))
            {
                var seatBox=Bounds(seats[0].Geometry);var backBox=Bounds(backs[0].Geometry);
                throw new InvalidDataException(FormattableString.Invariant($"Backrest is disconnected from the actual seat outline. Actual seat bounds: x={seatBox.X:0.###}, y={seatBox.Y:0.###}, w={seatBox.W:0.###}, h={seatBox.H:0.###}; backrest bounds: x={backBox.X:0.###}, y={backBox.Y:0.###}, w={backBox.W:0.###}, h={backBox.H:0.###}. Preserve the seat and backrest. Edit an existing rectangular rear support/post to use from=\"{seats[0].Role}\", to=\"{backs[0].Role}\", repeat=\"none\", align=\"none\", mirror=true. Choose x and w inside both anchors' horizontal span; the attachment tool computes the actual connecting y/h. Do not just move a tiny post closer or claim connection in the description. Supports must overlap the actual seat and backrest geometry, not just their boxes. For an oval seat, extend supports into the curved outline."));
            }
            var isolated=Enumerable.Range(0,built.Count).FirstOrDefault(i=>built[i].Role.Contains("support")&&!reachable.Contains(i),-1);
            if(isolated>=0)
            {
                var b=Bounds(built[isolated].Geometry);
                throw new InvalidDataException(FormattableString.Invariant($"Support at x={b.X:0.###}, y={b.Y:0.###}, w={b.W:0.###}, h={b.H:0.###} is disconnected. Move it to overlap the seat and backrest edges, or omit the unnecessary support."));
            }
        }
        var all=built.SelectMany(b=>b.Geometry.Points).ToArray();
        var minX=all.Min(p=>p.X);var minY=all.Min(p=>p.Y);var spanX=all.Max(p=>p.X)-minX;var spanY=all.Max(p=>p.Y)-minY;
        if(spanX<.1||spanY<.1) throw new InvalidDataException("The design has degenerate overall extents.");
        if(Regex.IsMatch(prompt,@"\bbeds?\b",RegexOptions.IgnoreCase)&&!Regex.IsMatch(prompt,@"\b(?:bunk|loft|folded|murphy|sofa)\b",RegexOptions.IgnoreCase))
        {
            var sleeping=built.Where(p=>Regex.IsMatch(p.Role,@"mattress|bedding|sleeping|duvet|quilt")).OrderByDescending(p=>{var b=Bounds(p.Geometry);return b.W*b.H;}).ToArray();
            if(sleeping.Length==0)throw new InvalidDataException("A bed plan needs a broad sleeping surface. Identify its role as mattress or bedding; do not substitute a chair seat or only a frame.");
            var b=Bounds(sleeping[0].Geometry);
            if(b.W/spanX<.55||b.H/spanY<.55)
                throw new InvalidDataException(FormattableString.Invariant($"Sleeping surface '{sleeping[0].Role}' occupies only {b.W/spanX:P0} of bed width and {b.H/spanY:P0} of depth. It must occupy most of BOTH axes (at least 55% for a conventional bed). Preserve the bed size and expand/reposition the actual sleeping surface; changing its description does not fix its geometry."));
            foreach(var head in built.Where(p=>Regex.IsMatch(p.Role,@"headboard|headrail|head_rail")))
            {
                var hb=Bounds(head.Geometry);
                if(hb.W<b.W*.65||hb.H>spanY*.35||hb.Y+hb.H/2<b.Y+b.H*.55)
                    throw new InvalidDataException("Bed headboard/headrail must span the head end of the sleeping surface toward +Y. It is a broad thin band in plan, not a narrow central post or a front-elevation panel. Preserve the mattress and change the head part's width, depth or position.");
            }
        }
        Emit("constraints-passed","Construction constraints passed. This does not prove visual accuracy.");
        {
            // Visibility is based on declared depth, never XY position or list order.
            var untrimmed=built.ToArray();
            Emit("visibility-order","Top-view depth (higher is closer): "+string.Join("; ",Parts.Select((p,i)=>$"{i+1} {p.Role}: "+(useVerticalDepth?$"top Z={(p.Z??0)+(p.Height??0):0.##} mm, ":"")+$"level {p.EffectiveLevel}, opaque={p.Opaque}")));
            var visible=new List<(string Role,Primitive Geometry)>();
            foreach(var item in built)
            {
                var id=origins[item.Geometry];var part=Parts[id];
                var opaque=untrimmed.Where(b=>origins[b.Geometry]!=id&&TopDepth(Parts[origins[b.Geometry]]).CompareTo(TopDepth(part))>0&&Parts[origins[b.Geometry]].Opaque&&b.Geometry.Closed).Select(b=>b.Geometry).ToArray();
                var result=opaque.Length==0?new[]{item.Geometry}:VisibleSegments(item.Geometry,opaque).ToArray();
                foreach(var g in result)
                {origins[g]=id;visible.Add((item.Role,g));}
            }
            built=visible;
            Emit("hidden-edges",$"Trimmed lower-level lines beneath higher opaque surfaces. {untrimmed.Length} original outlines → {built.Count} visible outlines. Equal levels do not hide each other; open paths and nonopaque parts do not mask surfaces.");
        }
        var geometry=built.Select(b=>b.Geometry with {Points=b.Geometry.Points.Select(p=>new Point2((p.X-minX)*width/spanX,(p.Y-minY)*depth/spanY)).ToArray()}).ToArray();
        var spec=new BlockSpec(Name,Description,["AI composed this design from geometric tools; no furniture template was selected.",
            $"Overall footprint fitted to {width:0.##} × {depth:0.##} mm. Parts: {string.Join(", ",Parts.Select(p=>p.Role).Distinct())}.",
            "Checked: finite geometry, footprint, same-level duplicates, declared negative constraints, connectivity and depth-based hidden lines. Visual recognition is a separate review."],geometry);
        spec.Validate();
        if(observe is not null)
        {
            try {observe(new("fitted",FormattableString.Invariant($"Translated origin ({minX:0.####}, {minY:0.####}); scaled X by {width/spanX:0.####} and Y by {depth/spanY:0.####} to {width:0.##} × {depth:0.##} mm. Individual parts were not moved separately."),"mm",
                geometry.Select((g,i)=>new RecordedGeometry(origins.GetValueOrDefault(built[i].Geometry,-1),built[i].Role,g)).ToArray(),Fit:new(minX,minY,width/spanX,depth/spanY)));}
            catch { /* Best effort diagnostics. */ }
        }
        return spec;
    }

    private static IEnumerable<(Point2 A,Point2 B)> Edges(Primitive p)
    {for(var i=0;i<p.Points.Length-(p.Closed?0:1);i++)yield return(p.Points[i],p.Points[(i+1)%p.Points.Length]);}
    private static double Cross(Point2 a,Point2 b)=>a.X*b.Y-a.Y*b.X;
    private static Point2 Sub(Point2 a,Point2 b)=>new(a.X-b.X,a.Y-b.Y);
    private static double? Intersection(Point2 a,Point2 b,Point2 c,Point2 d)
    {
        var r=Sub(b,a);var s=Sub(d,c);var denominator=Cross(r,s);
        if(Math.Abs(denominator)<1e-12)return null;
        var t=Cross(Sub(c,a),s)/denominator;var u=Cross(Sub(c,a),r)/denominator;
        return t>=-1e-9&&t<=1+1e-9&&u>=-1e-9&&u<=1+1e-9?Math.Clamp(t,0,1):null;
    }
    private static bool Inside(Point2 p,Primitive polygon)
    {
        if(!polygon.Closed)return false;
        var inside=false;
        foreach(var (a,b) in Edges(polygon))
            if((a.Y>p.Y)!=(b.Y>p.Y)&&p.X<(b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
        return inside;
    }
    private static double Distance(Point2 p,Point2 a,Point2 b)
    {
        var dx=b.X-a.X;var dy=b.Y-a.Y;var length=dx*dx+dy*dy;
        var t=length<1e-15?0:Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/length,0,1);
        return Math.Sqrt(Math.Pow(p.X-a.X-t*dx,2)+Math.Pow(p.Y-a.Y-t*dy,2));
    }
    private static bool Touches(Primitive a,Primitive b)
    {
        if(a.Points.Any(p=>Inside(p,b))||b.Points.Any(p=>Inside(p,a)))return true;
        foreach(var e in Edges(a))foreach(var f in Edges(b))
            if(Intersection(e.A,e.B,f.A,f.B) is not null||
                Math.Min(Math.Min(Distance(e.A,f.A,f.B),Distance(e.B,f.A,f.B)),Math.Min(Distance(f.A,e.A,e.B),Distance(f.B,e.A,e.B)))<.003)return true;
        return false;
    }
    internal static IEnumerable<Primitive> VisibleSegments(Primitive part,Primitive[] opaque)
    {
        var visible=new List<(Point2 A,Point2 B)>();
        var changed=false;
        foreach(var (a,b) in Edges(part))
        {
            var cuts=new List<double>{0,1};
            foreach(var surface in opaque)foreach(var edge in Edges(surface))
            {
                if(Intersection(a,b,edge.A,edge.B) is double t)cuts.Add(t);
                else
                {
                    // Split coincident boundaries too, so a higher surface owns their shared edge.
                    var delta=Sub(b,a);var length=delta.X*delta.X+delta.Y*delta.Y;
                    if(length>1e-16&&Math.Abs(Cross(delta,Sub(edge.A,a)))<1e-10&&Math.Abs(Cross(delta,Sub(edge.B,a)))<1e-10)
                        foreach(var p in new[]{edge.A,edge.B})cuts.Add(Math.Clamp(((p.X-a.X)*delta.X+(p.Y-a.Y)*delta.Y)/length,0,1));
                }
            }
            var sorted=cuts.Distinct().OrderBy(t=>t).ToArray();
            Point2 At(double t)=>new(a.X+(b.X-a.X)*t,a.Y+(b.Y-a.Y)*t);
            for(var i=1;i<sorted.Length;i++)
            {
                if(sorted[i]-sorted[i-1]<1e-8)continue;
                var midpoint=At((sorted[i]+sorted[i-1])/2);
                if(opaque.Any(p=>Inside(midpoint,p)||Edges(p).Any(e=>Distance(midpoint,e.A,e.B)<1e-9))) {changed=true;continue;}
                visible.Add((At(sorted[i-1]),At(sorted[i])));
            }
        }
        if(!changed){yield return part;yield break;}
        var chains=new List<List<Point2>>();
        bool Same(Point2 a,Point2 b)=>Math.Abs(a.X-b.X)<1e-9&&Math.Abs(a.Y-b.Y)<1e-9;
        foreach(var edge in visible)
        {
            if(chains.Count>0&&Same(chains[^1][^1],edge.A))chains[^1].Add(edge.B);
            else chains.Add([edge.A,edge.B]);
        }
        if(part.Closed&&chains.Count>1&&Same(chains[^1][^1],chains[0][0]))
        {var joined=chains[^1];joined.AddRange(chains[0].Skip(1));chains[0]=joined;chains.RemoveAt(chains.Count-1);}
        foreach(var points in chains)yield return new("polyline",points.ToArray(),new(0,0),0,false);
    }

    private static Point2[] ShapePoints(DesignPart p)
    {
        var r=p.Detail;
        switch(p.Shape)
        {
            case "rectangle": return [new(0,0),new(1,0),new(1,1),new(0,1)];
            case "trapezoid": return [new(0,0),new(1,0),new(1-r,1),new(r,1)];
            case "rounded":
                if(r<.001) return [new(0,0),new(1,0),new(1,1),new(0,1)];
                var outline=new List<Point2>();
                foreach(var arc in new[]{(X:1-r,Y:r,Angle:-90.0),(X:1-r,Y:1-r,Angle:0.0),(X:r,Y:1-r,Angle:90.0),(X:r,Y:r,Angle:180.0)})
                    for(var i=0;i<=6;i++){var a=(arc.Angle+i*15)*Math.PI/180;outline.Add(new(arc.X+r*Math.Cos(a),arc.Y+r*Math.Sin(a)));}
                return outline.ToArray();
            case "ellipse": return Enumerable.Range(0,48).Select(i=>new Point2(.5+.5*Math.Cos(i*Math.PI/24),.5+.5*Math.Sin(i*Math.PI/24))).ToArray();
            case "path": case "polygon": case "curve":
                var low=p.Shape=="curve"?-1:0;var high=p.Shape=="curve"?2:1;
                if(p.Points.Length<(p.Shape=="polygon"?3:2)||p.Points.Length>32||p.Points.Any(v=>v is null||!double.IsFinite(v.X)||!double.IsFinite(v.Y)||v.X<low||v.X>high||v.Y<low||v.Y>high))
                    throw new InvalidDataException("Custom shape points must stay within its local 0–1 box.");
                if(p.Shape!="curve") return p.Points;
                if(p.Points.Length!=3) throw new InvalidDataException("A quadratic curve needs exactly three control points.");
                return Enumerable.Range(0,33).Select(i=>{var t=i/32.0;var q=1-t;return new Point2(q*q*p.Points[0].X+2*q*t*p.Points[1].X+t*t*p.Points[2].X,q*q*p.Points[0].Y+2*q*t*p.Points[1].Y+t*t*p.Points[2].Y);}).ToArray();
            default: throw new InvalidDataException("Unsupported drawing tool. Use rectangle, rounded, trapezoid, ellipse, path, polygon or curve.");
        }
    }

    public static string InstructionsFor(string prompt)
    {
        if(Regex.IsMatch(prompt,@"\b(?:chair|armchair)\b",RegexOptions.IgnoreCase))return Instructions;
        var generic=Instructions[..Instructions.IndexOf("Keep a plain request plain.",StringComparison.Ordinal)];
        if(Regex.IsMatch(prompt,@"\bbeds?\b",RegexOptions.IgnoreCase))generic+="""

        BED PLAN: the sleeping surface (role mattress or bedding) occupies most of the bed width and length.
        A headboard/headrail, when present, spans the head end near +Y; it is a wide thin band in plan, not a narrow central post.
        The frame surrounds/supports the sleeping surface. Pillows, when appropriate and not forbidden, lie on the mattress near +Y, above bedding.
        Bedding seams are nonopaque linework; do not hide the whole mattress with a higher full-footprint frame or decorative surface.
        A furnished bed can show pillow outlines and a bedding fold; a bare bed/mattress must respect that request. Do not require or add accessories, bedside tables or a footboard unless appropriate to the request.
        Design the requested type, dimensions and proportions yourself. These are part relationships, not fixed coordinates or a bed block template.
        """;
        return generic+"\nRespect the actual requested object, dimensions and negative requirements. Return a complete revised plan when correcting feedback. Do not add chair parts to other objects.";
    }

    public const string Instructions="""
    You DESIGN 2D furniture plan views using a geometric toolkit. Do not select a furniture template.
    Choose the parts, shapes, proportions, positions and arrangement to satisfy the specific request.
    Return COMPACT JSON only. Name/description explain actual design choices. Overall width/depth are millimeters.
    Parts use NORMALIZED overall coordinates: x,y,w,h within 0..1. +Y is the back/top of the plan, +X right.
    Each part has role, shape, x,y,w,h,detail,count,repeat,gap,mirror,align,inset,points,from,to,level,opaque.
    TOP-VIEW VISIBILITY: level is a relative depth order (-100..100), not millimeters. Higher means closer to the overhead camera.
    opaque=true means a CLOSED outline is a filled surface which hides lower-level lines. opaque=false means linework only.
    Open paths/curves never mask geometry. Equal levels do not hide each other; use distinct levels when one surface covers another.
    Choose depth from the object's construction, not its XY position or the order parts appear in JSON.
    Example: supports/legs level=0, seat/tabletop level=10, back rail/armrests level=20, pad on seat level=30.
    Interior seams/engraving can use opaque=false at a higher level than their surface. A slat is its own solid surface; spaces between slats stay empty.
    INTENTIONAL OVERLAP IS NORMAL. Draw whole parts; the engine trims hidden lower-level lines at actual outline intersections.
    Do not separate parts just to avoid overlap. Do not pre-trim away construction that is needed to connect them.
    Roles describe physical purpose: seat, backrest, support, armrest, cushion, frame, tabletop, etc.
    Tools: rectangle; rounded (detail=corner fraction .02..0.3); trapezoid (detail=top inset fraction);
    ellipse; path (open local points); polygon (closed local points); curve (3 local points for quadratic Bezier).
    points=[] unless using path/polygon/curve. Points are local to that part's box, normally between 0 and 1.
    Curve control points may range from -1 to 2 to bow outside the local box. Modest overflow beyond the overall 0..1 box is fitted as one assembly to the requested footprint, preserving connections.
    count=1, repeat=none normally. row/column divides the part box into count equally spaced items; gap is normalized overall spacing.
    mirror=true makes a reflected copy across overall x=0.5; use for paired supports, not centered seats.
    align=none uses x; center centers the whole box; left/right align it to the overall bounds.
    IMPORTANT: align=center OVERRIDES x. To create two side supports use ONE off-center support,
    align=none, mirror=true. Do not center each side support or create a hidden stem inside the seat.
    inset=0 normally; >0 adds an inner outline offset by that fraction of the shorter part-box side.
    ATTACHMENT TOOL: a rectangular/rounded connector can set from="seat", to="backrest" (role names).
    The engine computes its exact y/h and shifts x as needed to intersect both actual outlines, including oval seats.
    Choose its w and hinted x; use mirror=true,align=none for a pair. This is strongly preferred for rear supports.
    All other parts set from="",to="". Anchors must have unique roles and may not themselves be linked or repeated.
    Use 3-10 purposeful parts, not decorative clutter. No duplicate outlines. Keep all parts inside the design box.
    Keep a plain request plain. Do not invent slats or extra exposed legs unless requested. A simple chair can use three part definitions: seat, backrest, mirrored support.
    In a chair plan, SEAT and BACKREST are separate parts. They may overlap in projection; declare which is above using level.
    Camera looks STRAIGHT DOWN. The seat is a broad shape taking 55-80% of depth. The backrest is a THIN band
    behind the seat toward +Y, typically 5-20% of depth, NOT a tall rectangular panel below the seat.
    Legs directly under an opaque seat are hidden in plan and should be omitted, not drawn as a front elevation.
    Back supports can connect the rear seat edge to the backrest. Prefer one mirrored support part for a pair.
    Backrests MUST connect to the seat directly or via supports that span the entire gap. No floating backrest.
    Supports must OVERLAP actual seat/backrest outlines. For an oval seat, start side supports forward inside
    the curved outline, not at empty corners of the ellipse bounding box. Hidden support edges are clipped by the engine.
    Dining chairs have a seat, a backrest, and supports; no arms unless requested. Default overall 500 x 550 mm.
    'No cushion' means no cushion/pad parts or upholstery seam/inset lines on the seat. Use one plain seat outline.
    Respect explicit dimensions, no arms, slatted backs, curved backs and other requested features.
    Build a coherent top view, not a front elevation. Slats across a thin backrest band can be repeated narrow parts.
    Label slats back_slat. If a slat count is requested, generate exactly that many visible slats.
    For an oval seat use ellipse; for a tapered seat use trapezoid or polygon. Do not substitute rectangles.
    Default other objects to plausible dimensions. Do not claim visual approval or manufacturing suitability.
    If repair feedback is provided, return a complete revised plan that resolves it. Preserve correct parts.
    """;

    public const string Schema="""
    {"type":"object","additionalProperties":false,"required":["name","description","width","depth","parts"],"properties":{
    "name":{"type":"string"},"description":{"type":"string"},"width":{"type":"number"},"depth":{"type":"number"},
    "parts":{"type":"array","minItems":2,"maxItems":24,"items":{"type":"object","additionalProperties":false,
    "required":["role","shape","x","y","w","h","detail","count","repeat","gap","mirror","align","inset","points","from","to","level","opaque"],"properties":{
    "role":{"type":"string"},"shape":{"type":"string","enum":["rectangle","rounded","trapezoid","ellipse","path","polygon","curve"]},
    "x":{"type":"number"},"y":{"type":"number"},"w":{"type":"number"},"h":{"type":"number"},"detail":{"type":"number"},
    "count":{"type":"integer","minimum":1,"maximum":8},"repeat":{"type":"string","enum":["none","row","column"]},
    "gap":{"type":"number"},"mirror":{"type":"boolean"},"align":{"type":"string","enum":["none","center","left","right"]},"inset":{"type":"number"},
    "from":{"type":"string"},"to":{"type":"string"},
    "level":{"type":"integer","minimum":-100,"maximum":100},"opaque":{"type":"boolean"},
    "points":{"type":"array","maxItems":32,"items":{"type":"object","required":["x","y"],"additionalProperties":false,"properties":{"x":{"type":"number"},"y":{"type":"number"}}}}
    }}}}}
    """;

    public static string SchemaFor(bool elevations)=>!elevations?Schema:Schema
        .Replace("\"maxItems\":24","\"maxItems\":12")
        .Replace("\"width\",\"depth\",\"parts\"","\"width\",\"depth\",\"height\",\"parts\"")
        .Replace("\"depth\":{\"type\":\"number\"},","\"depth\":{\"type\":\"number\"},\"height\":{\"type\":\"number\",\"minimum\":100,\"maximum\":5000},")
        .Replace("\"level\",\"opaque\"]","\"level\",\"opaque\",\"z\",\"height\"]")
        .Replace("\"opaque\":{\"type\":\"boolean\"},","\"opaque\":{\"type\":\"boolean\"},\"z\":{\"type\":\"number\",\"minimum\":0},\"height\":{\"type\":\"number\",\"exclusiveMinimum\":0},");

    public const string ElevationInstructions="""
    ALSO GENERATE MATCHING FRONT AND RIGHT-SIDE ELEVATIONS from ONE shared assembly.
    Overall width,depth,height are millimeters. The plan coordinates remain normalized X/Y.
    Every part MUST specify z (bottom above floor in mm) and height (vertical thickness/height in mm).
    z>=0, height>0, z+height<=overall height. At least one part reaches overall height and one starts at floor z=0.
    INCLUDE legs, posts, panels and bases needed for elevations, even when they are hidden under a seat in plan.
    Override the earlier top-view-only instruction to omit hidden legs. The renderer hides them in plan automatically.
    Use closed solid outlines. Each plan footprint is extruded vertically between z and z+height.
    Example chair: four legs z=0,height=430; seat z=430,height=40; rear posts z=0,height=800; back rail z=700,height=100; overall height=800.
    Decide real construction dimensions for THIS request, not a front elevation drawn into the normalized plan box.
    Front projects X/Z looking from -Y; right side projects Y/Z looking from +X. Elevations are derived, not independently invented.
    These tools make vertical extrusions; do not claim sloping legs or curved elevation profiles that they cannot represent.
    Top-view visibility uses actual top Z first and level as a tie-breaker. Equal depths do not hide each other.
    The worked example above only teaches plan tools. Add your own complete vertical dimensions and floor supports.
    Keep this request SIMPLE: 3–8 part definitions, never more than 12. Use mirror/repetition instead of many repeated definitions.
    Do not add decorative crossbars/slats unless explicitly requested. For a plain chair, a seat, thin rear rail, front leg pair and rear post pair are sufficient.
    A leg is slender in BOTH plan axes: normally w=.04..0.12, h=.04..0.12. Never make its plan h=.8: that is a rail, not a leg.
    Rear posts can touch both seat and rear rail in plan; rear posts extend to overall height, front legs stop at seat underside.
    """;

    public const string ElevationExample="""
    WORKED TOOL EXAMPLE ONLY: one coherent chair assembly, not a block to select. Adapt parts and proportions to the actual request.
    A broad seat, rear rail and two mirrored pairs of vertical members. Rear rail overlaps the seat in plan; its higher top surface hides the seat edge.
    {"name":"AssemblyExample","description":"Plain chair with a solid seat, rear rail and four posts","width":500,"depth":550,"height":800,"parts":[
    {"role":"seat","shape":"trapezoid","x":0.1,"y":0.08,"w":0.8,"h":0.72,"detail":0.06,"count":1,"repeat":"none","gap":0,"mirror":false,"align":"none","inset":0,"points":[],"from":"","to":"","level":10,"opaque":true,"z":430,"height":40},
    {"role":"backrest","shape":"rounded","x":0.05,"y":0.76,"w":0.9,"h":0.12,"detail":0.08,"count":1,"repeat":"none","gap":0,"mirror":false,"align":"none","inset":0,"points":[],"from":"","to":"","level":20,"opaque":true,"z":700,"height":100},
    {"role":"front_legs","shape":"rectangle","x":0.14,"y":0.13,"w":0.06,"h":0.06,"detail":0,"count":1,"repeat":"none","gap":0,"mirror":true,"align":"none","inset":0,"points":[],"from":"","to":"","level":0,"opaque":true,"z":0,"height":430},
    {"role":"rear_posts","shape":"rectangle","x":0.14,"y":0.76,"w":0.06,"h":0.06,"detail":0,"count":1,"repeat":"none","gap":0,"mirror":true,"align":"none","inset":0,"points":[],"from":"","to":"","level":0,"opaque":true,"z":0,"height":800}]}
    """;

    public const string WorkedExample="""
    WORKED DRAFTING EXAMPLE, not a template to select: this one top-view chair has a broad tapered seat,
    a narrow rear rail, and off-center supports spanning the gap. The mirrored support has align=none.
    The backrest y=.86 is behind seat ending at y=.68; supports span y=.62 to .92 to connect both.
    Change tools/parts/positions for the actual request: oval seat needs ellipse; slatted back needs repeated back_slat parts.
    Do not copy this description or claim this is the only chair design. It demonstrates coordinates and tool semantics.
    {"name":"ExampleOnly","description":"Tapered seat, plain rear rail and paired rear supports","width":500,"depth":550,"parts":[
    {"role":"seat","shape":"trapezoid","x":0.12,"y":0,"w":0.76,"h":0.68,"detail":0.12,"count":1,"repeat":"none","gap":0,"mirror":false,"align":"center","inset":0,"points":[],"from":"","to":"","level":10,"opaque":true},
    {"role":"backrest","shape":"rounded","x":0,"y":0.86,"w":1,"h":0.14,"detail":0.12,"count":1,"repeat":"none","gap":0,"mirror":false,"align":"center","inset":0,"points":[],"from":"","to":"","level":20,"opaque":true},
    {"role":"support","shape":"rectangle","x":0.2,"y":0.62,"w":0.045,"h":0.3,"detail":0,"count":1,"repeat":"none","gap":0,"mirror":true,"align":"none","inset":0,"points":[],"from":"seat","to":"backrest","level":0,"opaque":true}]}
    """;
}
