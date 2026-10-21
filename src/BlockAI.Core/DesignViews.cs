namespace BlockAI.Core;

public sealed record DesignViews(BlockSpec Plan,BlockSpec Front,BlockSpec Side);

/// <summary>Orthographic envelopes of a shared assembly of vertically extruded plan parts.</summary>
public static class ElevationBuilder
{
    public static DesignViews Build(DesignPlan plan,string prompt,Action<ConstructionFrame>? observe=null)
    {
        if(!double.IsFinite(plan.Height)||plan.Height<100||plan.Height>5000)
            throw new InvalidDataException("Overall furniture height must be 100–5000 mm for elevations.");
        if(plan.Parts is null||plan.Parts.Length is <2 or >24)throw new InvalidDataException("Elevations need 2–24 parts.");
        foreach(var part in plan.Parts)
        {
            if(part is null||part.Z is not double z||part.Height is not double height||!double.IsFinite(z)||!double.IsFinite(height)||z<0||height<=0||z+height>plan.Height+.001)
                throw new InvalidDataException("Every elevation part needs z>=0 and height>0 in millimeters, with z+height <= overall height. Include actual vertical members, even when hidden in plan.");
        }
        var actualTop=plan.Parts.Max(p=>p.Z!.Value+p.Height!.Value);
        if(Math.Abs(actualTop-plan.Height)>.5)throw new InvalidDataException("At least one part must reach the stated overall furniture height.");
        if(plan.Parts.Min(p=>p.Z!.Value)>.5)throw new InvalidDataException("The assembly needs a part at floor level z=0.");
        ConstructionFrame? assembly=null;CoordinateFit? fit=null;
        void Capture(ConstructionFrame frame)
        {
            if(frame.Stage=="constraints-passed")assembly=frame;
            if(frame.Fit is not null)fit=frame.Fit;
            try{observe?.Invoke(frame);}catch{/* Optional diagnostic sink. */}
        }
        var top=plan.Build(prompt,Capture,useVerticalDepth:true);
        if(assembly is null||fit is null)throw new InvalidDataException("The shared assembly could not be projected.");
        var solids=assembly.Geometry.Select(item=>
        {
            var points=item.Geometry.Points.Select(p=>new Point2((p.X-fit.MinX)*fit.ScaleX,(p.Y-fit.MinY)*fit.ScaleY)).ToArray();
            return (Item:item,MinX:points.Min(p=>p.X),MaxX:points.Max(p=>p.X),MinY:points.Min(p=>p.Y),MaxY:points.Max(p=>p.Y));
        }).ToArray();

        BlockSpec Project(bool side)
        {
            var view=side?"side":"front";
            var projected=solids.Select(s=>
            {
                var part=plan.Parts[s.Item.PartId];
                var left=side?s.MinY:s.MinX;var right=side?s.MaxY:s.MaxX;
                var z=part.Z!.Value;var height=part.Height!.Value;
                Primitive g=new("polyline",[new(left,z),new(right,z),new(right,z+height),new(left,z+height)],new(0,0),0,true);
                return (Part:s.Item.PartId,s.Item.Role,Geometry:g,Depth:side?s.MaxX:-s.MinY,Level:part.EffectiveLevel,Opaque:part.Opaque&&s.Item.Geometry.Closed,Width:right-left);
            }).Where(p=>p.Width>1e-6).ToArray();
            if(projected.Length==0)throw new InvalidDataException("The assembly has no visible width in the "+view+" view.");
            var visible=new List<RecordedGeometry>();
            foreach(var item in projected)
            {
                var masks=projected.Where(p=>p.Opaque&&(p.Depth>item.Depth+1e-6||Math.Abs(p.Depth-item.Depth)<=1e-6&&p.Level>item.Level)).Select(p=>p.Geometry).ToArray();
                foreach(var g in DesignPlan.VisibleSegments(item.Geometry,masks))visible.Add(new(item.Part,item.Role,g));
            }
            var unique=visible.DistinctBy(g=>string.Join(";",g.Geometry.Points.Select(p=>FormattableString.Invariant($"{p.X:F6},{p.Y:F6}")).OrderBy(s=>s,StringComparer.Ordinal))).ToArray();
            var spec=new BlockSpec(plan.Name[..Math.Min(plan.Name.Length,74)]+(side?"_Side":"_Front"),plan.Description+(side?" — right side elevation":" — front elevation"),
                ["Projected from the same part positions and vertical dimensions as the plan.",
                 "Simplified vertical extrusions: front uses X/Z from -Y; right side uses Y/Z from +X. Curved or sloped elevation profiles are not modeled.",
                 "Higher/nearer opaque parts hide lines behind them. Review before inserting."],unique.Select(g=>g.Geometry).ToArray());
            spec.Validate();
            try{observe?.Invoke(new(view+"-elevation",$"{(side?"Right side":"Front")} elevation from the shared assembly. Horizontal axis={(side?"depth Y":"width X")}; vertical axis=height Z (mm). Hidden lines trimmed along the view direction.","mm",unique.Take(500).ToArray(),View:view));}catch{}
            return spec;
        }
        return new(top,Project(false),Project(true));
    }
}
