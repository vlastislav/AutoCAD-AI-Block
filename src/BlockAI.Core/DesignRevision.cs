using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlockAI.Core;

/// <summary>Edits are applied atomically to the last parsed assembly; omitted parts are preserved.</summary>
public static class DesignRevision
{
    public static string SchemaFor(bool elevations)
    {
        var part=JsonNode.Parse(DesignPlan.SchemaFor(elevations))!["properties"]!["parts"]!["items"]!;
        return new JsonObject
        {
            ["type"]="object",["additionalProperties"]=false,
            ["required"]=new JsonArray("replacements","remove","add"),
            ["properties"]=new JsonObject
            {
                ["replacements"]=new JsonObject{["type"]="array",["maxItems"]=6,["items"]=new JsonObject
                { ["type"]="object",["additionalProperties"]=false,["required"]=new JsonArray("index","part"),["properties"]=new JsonObject
                    {["index"]=new JsonObject{["type"]="integer",["minimum"]=0,["maximum"]=23},["part"]=part.DeepClone()}}},
                ["remove"]=new JsonObject{["type"]="array",["maxItems"]=6,["items"]=new JsonObject{["type"]="integer",["minimum"]=0,["maximum"]=23}},
                ["add"]=new JsonObject{["type"]="array",["maxItems"]=4,["items"]=part.DeepClone()}
            }
        }.ToJsonString();
    }

    public static DesignPlan Apply(DesignPlan baseline,string json,IReadOnlySet<int>? editableParts=null,bool allowAdd=true)
    {
        var patch=JsonSerializer.Deserialize<Patch>(json,BlockSpec.JsonOptions)??throw new InvalidDataException("No revision edits returned.");
        if(patch.Replacements is null||patch.Remove is null||patch.Add is null||patch.Replacements.Length>6||patch.Remove.Length>6||patch.Add.Length>4)
            throw new InvalidDataException("Return replacements, remove and add arrays within their limits, not a new full design.");
        if(patch.Replacements.Any(r=>r is null))throw new InvalidDataException("Every replacement needs an index and a complete part.");
        var indices=patch.Replacements.Select(r=>r.Index).Concat(patch.Remove).ToArray();
        if(indices.Any(i=>i<0||i>=baseline.Parts.Length)||indices.Distinct().Count()!=indices.Length||patch.Replacements.Any(r=>r.Part is null)||patch.Add.Any(p=>p is null))
            throw new InvalidDataException("Revision indices must refer to the current assembly exactly once. Do not both remove and replace the same part.");
        if(indices.Length==0&&patch.Add.Length==0)throw new InvalidDataException("Empty revision: edit at least one part to address the outstanding correction.");
        if(editableParts is not null&&indices.Any(i=>!editableParts.Contains(i)))throw new InvalidDataException("Revision tried to change a locked part. Replace/remove only the explicitly editable indices; preserve all other parts.");
        if(!allowAdd&&patch.Add.Length>0)throw new InvalidDataException("This correction does not require new parts. Edit the implicated existing parts instead of adding a new design.");
        var parts=baseline.Parts.ToArray();foreach(var edit in patch.Replacements)parts[edit.Index]=edit.Part;
        var revised=parts.Where((_,i)=>!patch.Remove.Contains(i)).Concat(patch.Add).ToArray();
        if(revised.Length is <2 or >24)throw new InvalidDataException("Edited assembly must contain 2–24 parts.");
        return baseline with {Parts=revised};
    }

    private sealed record Replacement(int Index,DesignPart Part);
    private sealed record Patch(Replacement[] Replacements,int[] Remove,DesignPart[] Add);

    public const string Instructions="""
    EDIT THE CURRENT ASSEMBLY. Return ONLY {"replacements":[{"index":0,"part":{...complete part...}}],"remove":[],"add":[]}.
    Indices are ZERO BASED and refer to CURRENT parts before this patch. Unmentioned parts are retained exactly by the engine.
    Replace only parts implicated in the outstanding corrections. Use add/remove only when needed to satisfy those corrections.
    Keep all unrelated correct parts. Overall dimensions, name and description are locked; do not return a new full design.
    Each replacement includes a complete part with all tool fields. Do not rename parts as a substitute for editing geometry.
    Read every outstanding correction, repair the earliest blocking geometry error first, and preserve previously fixed features.
    """;
}
