using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlockAI.Core;

/// <summary>Model-facing millimeters; the existing geometry engine keeps its normalized representation.</summary>
public static class MetricDesign
{
    private static readonly Dictionary<string,string> Fields=new(){["x"]="xMm",["y"]="yMm",["w"]="widthMm",["h"]="depthMm",["gap"]="gapMm"};
    public static string SchemaFor(bool elevations)
    {
        var schema=JsonNode.Parse(DesignPlan.SchemaFor(elevations))!;
        schema["properties"]!["parts"]!["maxItems"]=10;
        var part=schema["properties"]!["parts"]!["items"]!;var properties=part["properties"]!;
        foreach(var field in Fields){properties[field.Value]=properties[field.Key]!.DeepClone();properties.AsObject().Remove(field.Key);}
        var required=part["required"]!.AsArray();for(var i=0;i<required.Count;i++)if(Fields.TryGetValue(required[i]!.GetValue<string>(),out var name))required[i]=name;
        return schema.ToJsonString();
    }

    public static DesignPlan Parse(string json,string? prompt=null)
    {
        var root=JsonNode.Parse(json)??throw new InvalidDataException("Empty metric draft.");
        if(root["width"] is not JsonValue widthValue||!widthValue.TryGetValue<double>(out var width)||root["depth"] is not JsonValue depthValue||!depthValue.TryGetValue<double>(out var depth))throw new InvalidDataException("Overall metric width/depth must be numeric millimeters.");
        if(!double.IsFinite(width)||!double.IsFinite(depth)||width<100||depth<100||width>10000||depth>10000)throw new InvalidDataException("Metric draft width/depth must be 100–10000 mm.");
        if(prompt is not null)
        {
            var requested=new FurnitureDesign("other",width,depth,1,"modern","rectangular",[]).ApplyExplicitDimensions(prompt);
            if(Math.Abs(requested.Width-width)>2||Math.Abs(requested.Depth-depth)>2)throw new InvalidDataException(FormattableString.Invariant($"Overall metric dimensions contradict the request: use width={requested.Width:0.#}, depth={requested.Depth:0.#} mm and size the part boxes in those units."));
        }
        if(root["parts"] is not JsonArray parts||parts.Count is <2 or >24)throw new InvalidDataException("Metric draft needs 2–24 parts.");
        foreach(var part in parts)NormalizePart(part,width,depth);
        return DesignPlan.Parse(root.ToJsonString());
    }

    internal static void NormalizePart(JsonNode? part,double width,double depth)
    {
        if(part is not JsonObject)throw new InvalidDataException("Every metric part needs a box object.");
        foreach(var field in Fields)
        {
            if(part[field.Key] is not null||part[field.Value] is not JsonValue value||!value.TryGetValue<double>(out var number)||!double.IsFinite(number))
                throw new InvalidDataException("Use explicit xMm, yMm, widthMm, depthMm and gapMm fields with finite millimeter values, not x/y/w/h fractions.");
            var divisor=field.Key is "y" or "h"?depth:field.Key=="gap"&&part["repeat"]?.ToString()=="column"?depth:width;
            part[field.Key]=number/divisor;part.AsObject().Remove(field.Value);
        }
    }

    public static string ToJson(DesignPlan plan)
    {
        var root=JsonSerializer.SerializeToNode(plan,BlockSpec.JsonOptions)!;
        foreach(var part in root["parts"]!.AsArray())foreach(var field in Fields)
        {
            var factor=field.Key is "y" or "h"?plan.Depth:field.Key=="gap"&&part!["repeat"]?.ToString()=="column"?plan.Depth:plan.Width;
            part![field.Value]=part[field.Key]!.GetValue<double>()*factor;part.AsObject().Remove(field.Key);
        }
        return root.ToJsonString();
    }

    public static string InstructionsFor(string prompt)=>Instructions+(Regex.IsMatch(prompt,@"\bbeds?\b",RegexOptions.IgnoreCase)?"\nBed: the sleeping surface occupies most of both axes. A headboard is a wide thin rear strip, not a narrow central post. Pillows rest on the mattress near the head, not outside it. Bedding folds are open/nonopaque linework."
        :Regex.IsMatch(prompt,@"\b(?:chair|armchair)\b",RegexOptions.IgnoreCase)?"\nChair: broad seat and thin rear backrest band in TOP view; rear supports connect them. No cushion means a plain seat without cushion/pad/seam. Do not draw front-elevation heights into plan depth."
        :Regex.IsMatch(prompt,@"\b(?:sofa|couch|settee)\b",RegexOptions.IgnoreCase)?"\nSofa: seats lie inside or on a connected frame/base, with thin backs and arms at its edges. An L-shaped request needs perpendicular joined seating runs and a real empty inside corner. Use a polygon for a concave base if needed, or connected bars; a full rectangle is not an L. Do not detach one run or add slender chair-like legs in place of sofa seating.":"");

    public const string Instructions="""
    Design a NEW 2D top-down furniture plan using geometric tools. Do not select a furniture template or trace a reference.
    Return compact JSON. Overall width/depth are millimeters. Every part box uses xMm,yMm,widthMm,depthMm,gapMm IN MILLIMETERS.
    +X is right; +Y is the rear/head end. A part's box starts at its lower-left corner. Width is the X extent; depth is the Y extent, NOT elevation height.
    Use actual millimeter numbers: a 650 mm seat depth is depthMm=650, never 0.65 or 650000. Stay inside the overall footprint except modest intentional overhangs.
    The outer furniture extents must span the declared overall width/depth, without empty padding. Part sizes are preserved; the engine will NOT stretch the assembly to make it fit.
    Choose coherent real part dimensions and check that neighboring edges meet. Seats/cushions must lie on their frame/base. Do not merely spread labeled parts across the page.
    Tools: rectangle, rounded (detail=corner-radius fraction), trapezoid (detail=top inset fraction), ellipse, path, polygon, curve (quadratic Bezier).
    IMPORTANT: points stay LOCAL fractions of the part box, normally 0..1, even though the box itself is in millimeters. points=[] for rectangle/rounded/trapezoid/ellipse.
    A polygon uses its true concave outline; never replace an L-shaped body with its bounding rectangle. Curve has 3 local control points.
    To make a polygon's box equal its actual outline extents, its local vertices must reach x=0, x=1, y=0 and y=1; unused local margins make the actual shape smaller than the box.
    align=none uses xMm; center/left/right align to the overall footprint and override xMm. Use none for intentionally off-center parts.
    mirror=true reflects the entire part about overall width/2. Use ONE off-center mirrored definition for a left/right pair, not two separately mirrored definitions.
    count=1,repeat=none normally. row/column divides the given box into count items with gapMm between them. inset=0 normally; inset and detail are fractions, not millimeters.
    from/to are empty normally. A rectangular/rounded rear connector may name unique seat/backrest anchors; its y/depth is computed by the engine. This tool only bridges behind an anchor (+Y), not arbitrary side-by-side sofa bars.
    level is relative top-view depth (-100..100); higher is closer. opaque=true CLOSED surfaces hide lower lines. Open paths and opaque=false details do not mask anything.
    Intentional overlap is normal. Draw whole parts; the engine trims hidden edges. Do not separate connected furniture parts just to avoid overlap.
    Use 3–8 purposeful parts. Label actual roles. Keep the requested dimensions, object type, and negative constraints; do not add accessories or decorative clutter.
    Reference outlines are normalized study geometry, NOT millimeter design boxes. Interpret their relationships, then choose new part geometry for this request.
    """;
}
