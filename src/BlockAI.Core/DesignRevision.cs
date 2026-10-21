using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlockAI.Core;

/// <summary>Edits are applied atomically to the last parsed assembly; omitted parts are preserved.</summary>
public static class DesignRevision
{
    public static string SchemaFor(bool elevations,bool metricParts=false)
    {
        var part=JsonNode.Parse(metricParts?MetricDesign.SchemaFor(elevations):DesignPlan.SchemaFor(elevations))!["properties"]!["parts"]!["items"]!;
        return new JsonObject
        {
            ["type"]="object",["additionalProperties"]=false,
            ["required"]=new JsonArray("replacements","remove","add","adjustments"),
            ["properties"]=new JsonObject
            {
                ["replacements"]=new JsonObject{["type"]="array",["maxItems"]=metricParts?2:6,["items"]=new JsonObject
                { ["type"]="object",["additionalProperties"]=false,["required"]=new JsonArray("index","part"),["properties"]=new JsonObject
                    {["index"]=new JsonObject{["type"]="integer",["minimum"]=0,["maximum"]=23},["part"]=part.DeepClone()}}},
                ["remove"]=new JsonObject{["type"]="array",["maxItems"]=metricParts?2:6,["items"]=new JsonObject{["type"]="integer",["minimum"]=0,["maximum"]=23}},
                ["add"]=new JsonObject{["type"]="array",["maxItems"]=metricParts?2:4,["items"]=part.DeepClone()},
                ["adjustments"]=new JsonObject{["type"]="array",["maxItems"]=12,["items"]=new JsonObject
                {
                    ["type"]="object",["additionalProperties"]=false,["required"]=new JsonArray("index","field","value"),
                    ["properties"]=new JsonObject
                    {
                        ["index"]=new JsonObject{["type"]="integer",["minimum"]=0,["maximum"]=23},
                        ["field"]=new JsonObject{["type"]="string",["enum"]=new JsonArray("xMm","yMm","widthMm","depthMm","gapMm","detail","level","mirror","opaque","align","shape")},
                        ["value"]=new JsonObject{["anyOf"]=new JsonArray(new JsonObject{["type"]="number"},new JsonObject{["type"]="boolean"},new JsonObject{["type"]="string"})}
                    }
                }}
            }
        }.ToJsonString();
    }

    public static DesignPlan Apply(DesignPlan baseline,string json,IReadOnlySet<int>? editableParts=null,bool allowAdd=true,bool metricParts=false)
    {
        if(metricParts)
        {
            var node=JsonNode.Parse(json)??throw new InvalidDataException("No edits returned.");
            if(node["replacements"] is JsonArray replacements)foreach(var replacement in replacements)MetricDesign.NormalizePart(replacement?["part"],baseline.Width,baseline.Depth);
            if(node["add"] is JsonArray additions)foreach(var addition in additions)MetricDesign.NormalizePart(addition,baseline.Width,baseline.Depth);
            json=node.ToJsonString();
        }
        var patch=JsonSerializer.Deserialize<Patch>(json,BlockSpec.JsonOptions)??throw new InvalidDataException("No revision edits returned.");
        if(patch.Replacements is null||patch.Remove is null||patch.Add is null||patch.Replacements.Length>6||patch.Remove.Length>6||patch.Add.Length>4)
            throw new InvalidDataException("Return replacements, remove and add arrays within their limits, not a new full design.");
        if(patch.Replacements.Any(r=>r is null))throw new InvalidDataException("Every replacement needs an index and a complete part.");
        if(metricParts&&(patch.Replacements.Length>2||patch.Remove.Length>2||patch.Add.Length>2))throw new InvalidDataException("Replace/remove/add at most two implicated parts per edit; use focused millimeter adjustments for positions/sizes.");
        var adjustments=patch.Adjustments??[];
        if(adjustments.Length>12||adjustments.Any(a=>a is null))throw new InvalidDataException("Use at most 12 focused property adjustments.");
        var indices=patch.Replacements.Select(r=>r.Index).Concat(patch.Remove).ToArray();
        if(indices.Any(i=>i<0||i>=baseline.Parts.Length)||indices.Distinct().Count()!=indices.Length||patch.Replacements.Any(r=>r.Part is null)||patch.Add.Any(p=>p is null))
            throw new InvalidDataException("Revision indices must refer to the current assembly exactly once. Do not both remove and replace the same part.");
        if(adjustments.Any(a=>a.Index<0||a.Index>=baseline.Parts.Length||indices.Contains(a.Index))||adjustments.Select(a=>(a.Index,a.Field)).Distinct().Count()!=adjustments.Length)
            throw new InvalidDataException("Adjustment indices must exist, fields must be unique per part, and adjusted parts cannot also be replaced/removed.");
        if(indices.Length==0&&patch.Add.Length==0&&adjustments.Length==0)throw new InvalidDataException("Empty revision: edit at least one part to address the outstanding correction.");
        if(editableParts is not null&&indices.Concat(adjustments.Select(a=>a.Index)).Any(i=>!editableParts.Contains(i)))throw new InvalidDataException("Revision tried to change a locked part. Replace/remove/adjust only the explicitly editable indices; preserve all other parts.");
        if(!allowAdd&&patch.Add.Length>0)throw new InvalidDataException("This correction does not require new parts. Edit the implicated existing parts instead of adding a new design.");
        var parts=baseline.Parts.ToArray();foreach(var edit in patch.Replacements)parts[edit.Index]=edit.Part;
        foreach(var group in adjustments.GroupBy(a=>a.Index))
        {
            var part=parts[group.Key];var node=JsonSerializer.SerializeToNode(part,BlockSpec.JsonOptions)!;
            foreach(var a in group)
            {
                if(a.Field is "xMm" or "yMm" or "widthMm" or "depthMm" or "gapMm" or "level" or "detail")
                {
                    if(a.Value.ValueKind!=JsonValueKind.Number||!a.Value.TryGetDouble(out var number)||!double.IsFinite(number))throw new InvalidDataException("Numeric adjustments require finite numbers.");
                    var property=a.Field switch {"xMm"=>"x","yMm"=>"y","widthMm"=>"w","depthMm"=>"h","gapMm"=>"gap",var field=>field};
                    var divisor=a.Field switch {"xMm" or "widthMm"=>baseline.Width,"yMm" or "depthMm"=>baseline.Depth,"gapMm"=>part.Repeat=="column"?baseline.Depth:baseline.Width,_=>1};
                    if(!double.IsFinite(divisor)||divisor<=0)throw new InvalidDataException("Metric adjustments require a valid assembly size.");
                    if(a.Field=="level"&&number!=Math.Truncate(number))throw new InvalidDataException("Level must be an integer.");
                    node[property]=number/divisor;
                    if(a.Field=="xMm")node["align"]="none"; // An explicit X move takes precedence over old alignment.
                }
                else if(a.Field is "mirror" or "opaque")
                {if(a.Value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("Mirror and opaque require boolean values.");node[a.Field]=a.Value.GetBoolean();}
                else if(a.Field is "align" or "shape")
                {if(a.Value.ValueKind!=JsonValueKind.String)throw new InvalidDataException("Align and shape require text values.");node[a.Field]=a.Value.GetString();}
                else throw new InvalidDataException("Unknown adjustment field.");
            }
            parts[group.Key]=node.Deserialize<DesignPart>(BlockSpec.JsonOptions)!;
        }
        var revised=parts.Where((_,i)=>!patch.Remove.Contains(i)).Concat(patch.Add).ToArray();
        if(revised.Length is <2 or >24)throw new InvalidDataException("Edited assembly must contain 2–24 parts.");
        if(JsonSerializer.Serialize(revised,BlockSpec.JsonOptions)==JsonSerializer.Serialize(baseline.Parts,BlockSpec.JsonOptions))
            throw new InvalidDataException("Revision made no parameter changes. The previous assembly is retained. Change a measured position, size, shape or depth; resending the same part is not a correction.");
        return baseline with {Parts=revised};
    }

    private sealed record Replacement(int Index,DesignPart Part);
    private sealed record Adjustment(int Index,string Field,JsonElement Value);
    private sealed record Patch(Replacement[] Replacements,int[] Remove,DesignPart[] Add,Adjustment[]? Adjustments=null);

    public static string InstructionsFor(bool metricParts)=>metricParts?Instructions.Replace("Replacement parts still use normalized coordinates.","Replacement/add part boxes ALSO use explicit millimeter fields xMm,yMm,widthMm,depthMm,gapMm. Never put millimeter values in normalized x/y/w/h fields. Replace at most two parts at a time; preserve the rest."):Instructions;

    public static string Measurements(DesignPlan plan)=>"\nCURRENT PART BOXES IN MILLIMETERS before whole-assembly fitting (not source DWG units):\n"+
        string.Join("\n",plan.Parts.Select((p,i)=>FormattableString.Invariant($"[{i}] {p.Role}: x={(p.Align switch {"center"=>(1-p.W)/2,"left"=>0,"right"=>1-p.W,_=>p.X})*plan.Width:0.#}, y={p.Y*plan.Depth:0.#}, width={p.W*plan.Width:0.#}, depth={p.H*plan.Depth:0.#}; align={p.Align}, mirror={p.Mirror}, level={p.EffectiveLevel}")))+
        FormattableString.Invariant($"\nTarget footprint: {plan.Width:0.#} x {plan.Depth:0.#} mm. Plan boxes should occupy that footprint coherently; large outlying parts distort fitting. Aligned X is resolved by align, not the old x field.");

    public const string Instructions="""
    EDIT THE CURRENT ASSEMBLY. Return ONLY {"adjustments":[{"index":0,"field":"depthMm","value":650}],"replacements":[],"remove":[],"add":[]}.
    PREFER adjustments for placement/proportions: xMm,yMm,widthMm,depthMm,gapMm use MILLIMETERS, not normalized fractions or meters. The engine converts them.
    Other fields: detail (ratio), level (integer), mirror/opaque (booleans), align/shape (strings). An xMm move sets align=none automatically; use align=center for true centering.
    Use replacements only for a new outline requiring points, attachment links or other fields not covered by adjustments. Replacement parts still use normalized coordinates.
    Indices are ZERO BASED and refer to CURRENT parts before this patch. Unmentioned parts are retained exactly by the engine.
    Replace only parts implicated in the outstanding corrections. Use add/remove only when needed to satisfy those corrections.
    Keep all unrelated correct parts. Overall dimensions, name and description are locked; do not return a new full design.
    Each replacement includes a complete part with all tool fields. Do not rename parts as a substitute for editing geometry.
    Read every outstanding correction, repair the earliest blocking geometry error first, and preserve previously fixed features.
    """;
}
