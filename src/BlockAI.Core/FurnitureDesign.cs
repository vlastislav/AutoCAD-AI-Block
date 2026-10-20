using System.Globalization;
using System.Text.RegularExpressions;

namespace BlockAI.Core;

public sealed record FurnitureDesign(string Kind, double Width, double Depth, int Seats, string Style, string Shape, string[] Assumptions)
{
    public FurnitureDesign ApplyExplicitDimensions(string prompt)
    {
        const string number = @"(?:\d+(?:\.\d+)?|\.\d+)";
        const string unit = @"(?:mm|cm|meters?|metres?|m|inches|inch|in|feet|foot|ft)\b";
        var match = Regex.Match(prompt, $@"(?<![\d.\-])(?<w>{number})\s*(?<wu>{unit})?\s*(?:x|×|\bby\b)\s*(?<d>{number})\s*(?<du>{unit})?(?![\d.])", RegexOptions.IgnoreCase);
        var design = this;
        if (match.Success)
        {
            var firstUnit = match.Groups["wu"].Value;
            var secondUnit = match.Groups["du"].Value;
            if (firstUnit.Length == 0) firstUnit = secondUnit;
            if (secondUnit.Length == 0) secondUnit = firstUnit;
            static double Factor(string units) => units.ToLowerInvariant() switch
            {
                "cm" => 10, "m" or "meter" or "meters" or "metre" or "metres" => 1000,
                "in" or "inch" or "inches" => 25.4, "ft" or "foot" or "feet" => 304.8, _ => 1
            };
            var width = double.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture) * Factor(firstUnit);
            var depth = double.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture) * Factor(secondUnit);
            // A mattress dimension is distinct from the outside frame size.
            var prefix = prompt[..match.Index];
            var mattressMention = prefix.LastIndexOf("mattress", StringComparison.OrdinalIgnoreCase);
            var overallMention = prefix.LastIndexOf("overall", StringComparison.OrdinalIgnoreCase);
            var suffix = prompt[(match.Index + match.Length)..].TrimStart();
            if (Kind == "bed" && (mattressMention > overallMention || (overallMention < 0 && suffix.StartsWith("mattress", StringComparison.OrdinalIgnoreCase))))
            { width += 100; depth += 200; }
            design = design with { Width = width, Depth = depth };
        }
        var seats = Regex.Match(prompt, @"\b(?<n>[1-8])\s*[- ]?\s*seat(?:er|s)?\b", RegexOptions.IgnoreCase);
        if (seats.Success && Kind == "sofa") design = design with { Seats = int.Parse(seats.Groups["n"].Value) };
        return design;
    }

    public BlockSpec Build()
    {
        var (minW, maxW, minD, maxD) = Kind switch
        {
            "sofa" => (900, 6000, 500, 1800), "bed" => (700, 4000, 1200, 3500),
            "chair" => (300, 1500, 300, 1500), "table" => (300, 6000, 300, 3000),
            "desk" => (500, 4000, 350, 2000), "cabinet" => (200, 6000, 150, 2000),
            _ => throw new InvalidDataException("Unsupported furniture category.")
        };
        if (!double.IsFinite(Width) || !double.IsFinite(Depth) || Width < minW || Width > maxW || Depth < minD || Depth > maxD)
            throw new InvalidDataException($"{Kind} dimensions are outside the supported range. Include dimensions in mm, cm, m, inches or feet.");
        if (Seats is < 1 or > 8 || Style is not ("modern" or "classic" or "minimal") || Shape is not ("rectangular" or "round"))
            throw new InvalidDataException("Invalid furniture design parameters.");
        if (Shape == "round" && (Kind != "table" || Math.Abs(Width - Depth) > 0.01))
            throw new InvalidDataException("A round table needs equal width and depth. Specify its diameter.");
        if (Assumptions is null || Assumptions.Length > 8 || Assumptions.Any(a => a is null || a.Length > 500))
            throw new InvalidDataException("Invalid design assumptions.");
        var entities = new List<Primitive>();
        void Rect(double x, double y, double width, double depth, double corner = 0)
        {
            var r = Math.Min(corner, Math.Min(width, depth) / 4);
            Point2[] points = r <= 0 ? [new(x,y), new(x+width,y), new(x+width,y+depth), new(x,y+depth)]
                : [new(x+r,y),new(x+width-r,y),new(x+width,y+r),new(x+width,y+depth-r),new(x+width-r,y+depth),new(x+r,y+depth),new(x,y+depth-r),new(x,y+r)];
            entities.Add(new("polyline", points, new(0,0), 0, true));
        }
        void Line(double x1, double y1, double x2, double y2) => entities.Add(new("polyline", [new(x1,y1), new(x2,y2)], new(0,0),0,false));
        var w = Width;
        var d = Depth;
        var corner = Style == "classic" ? Math.Min(w,d)*0.035 : Style == "modern" ? Math.Min(w,d)*0.015 : 0;
        var notes = new List<string> { "Built from adjustable furniture geometry; AI interprets the description, not individual lines.", $"Overall size: {w:0.##} × {d:0.##} mm. Style: {Style}." };
        switch (Kind)
        {
            case "sofa":
            case "chair":
                var count = Kind == "chair" ? 1 : Seats;
                var arm = Math.Clamp(w * 0.065, 70, 190);
                var back = d * 0.22;
                var front = d * 0.06;
                var gap = Math.Clamp(w * 0.006, 8, 18);
                var seatWidth = (w - 2*arm - gap*(count+1))/count;
                if (seatWidth < 260) throw new InvalidDataException("Too many seats for this width. Increase width or reduce seat count.");
                Rect(0,0,w,d,corner);
                Rect(gap,front,arm-gap,d-front-gap,corner);
                Rect(w-arm,front,arm-gap,d-front-gap,corner);
                for(var i=0;i<count;i++)
                {
                    var x = arm + gap + i*(seatWidth+gap);
                    Rect(x,front,seatWidth,d-back-front-gap,corner);
                    Rect(x,d-back,seatWidth,back-gap,corner);
                }
                notes.Add($"{count} seat cushion(s), matching back cushions, and two arms. Back is at the top of the plan.");
                break;
            case "bed":
                Rect(0,0,w,d,corner);
                Rect(0,d-100,w,100,corner);
                Rect(50,100,w-100,d-200,corner);
                var pillowGap = 35.0;
                var pillowWidth = Math.Min(750, (w-160-pillowGap)/2);
                var pillowDepth = Math.Min(400,(d-200)*0.2);
                var pillowX = (w-2*pillowWidth-pillowGap)/2;
                Rect(pillowX,d-125-pillowDepth,pillowWidth,pillowDepth,20);
                Rect(pillowX+pillowWidth+pillowGap,d-125-pillowDepth,pillowWidth,pillowDepth,20);
                Line(50,d*0.65,w-50,d*0.65);
                Line(50,d*0.65-45,w-50,d*0.65-45);
                notes.Add($"Mattress: {w-100:0.##} × {d-200:0.##} mm; headboard at the top, two pillows and blanket fold.");
                break;
            case "table":
                if (Shape == "round") entities.Add(new("circle", [], new(w/2,d/2),w/2,false));
                else { Rect(0,0,w,d,corner); if(Style != "minimal") Rect(25,25,w-50,d-50,corner); }
                notes.Add("Tabletop plan; legs below the tabletop are omitted.");
                break;
            case "desk":
                Rect(0,0,w,d,corner);
                Rect(w*0.72,30,w*0.25,d-60);
                Line(w*0.72, d*0.35, w*0.97, d*0.35);
                Line(w*0.72, d*0.65, w*0.97, d*0.65);
                notes.Add("Desktop and one drawer pedestal indicated in plan.");
                break;
            case "cabinet":
                Rect(0,0,w,d);
                var doors = Math.Clamp((int)Math.Round(w/600),1,8);
                var doorWidth = w/doors;
                Line(0,25,w,25);
                for(var i=0;i<doors;i++)
                {
                    if(i>0) Line(i*doorWidth,25,i*doorWidth,d);
                    Line(i*doorWidth+doorWidth*0.7,12,(i+1)*doorWidth-15,12);
                }
                notes.Add("Cabinet footprint, front edge, compartments and door handles; no elevation or door swings.");
                break;
        }
        notes.AddRange(Assumptions);
        var spec = new BlockSpec($"{Kind}_{w:0}_{d:0}", $"{Style} {Kind} · plan view · {w:0.##} × {d:0.##} mm", notes.ToArray(), entities.ToArray());
        spec.Validate();
        return spec;
    }

    public const string Instructions = """
        Interpret a SINGLE furniture request as parameters for a 2D plan drawing. Return compact JSON only.
        Supported kinds: sofa (straight, 1-8 seats, no chaise), bed, chair (armchair), table, desk, cabinet.
        For other objects, multiple furniture items, L/U-shaped sofas, sectional/chaise sofas, armless seating,
        oval tables, asymmetric designs or unsupported features, return kind other. Do not silently replace them.
        width and depth are overall horizontal size in MILLIMETERS. Convert explicit units correctly.
        Default sofa 3 seats 2400 x 950; chair 850 x 850; desk 1400 x 700; cabinet 1200 x 500;
        rectangular table 1600 x 900; round table diameter 1000 (width=depth).
        Default bed: king overall 2030 x 2230, queen 1620 x 2230, double 1470 x 2100, single 1060 x 2100.
        Bed mattress is overall width minus 100, depth minus 200. If given mattress dimensions add 100 and 200.
        If given overall bed dimensions, use them directly. King defaults to North American sizing.
        seats is sofa seat count (default 3); for other kinds use 1. Shape is rectangular except round tables.
        style is modern, classic or minimal, matching the request; default modern.
        Assumptions must list dimensions/details you assumed, not contradict explicit requests.
        These are simplified furniture plans; do not promise materials, ornate details, 3D or moving parts.
        For kind other set width=0, depth=0, seats=1, style=modern, shape=rectangular, assumptions=[].
        """;

    public const string Schema = """
    {"type":"object","additionalProperties":false,"required":["kind","width","depth","seats","style","shape","assumptions"],"properties":{
    "kind":{"type":"string","enum":["sofa","bed","chair","table","desk","cabinet","other"]},
    "width":{"type":"number"},"depth":{"type":"number"},"seats":{"type":"integer","minimum":1,"maximum":8},
    "style":{"type":"string","enum":["modern","classic","minimal"]},"shape":{"type":"string","enum":["rectangular","round"]},
    "assumptions":{"type":"array","maxItems":6,"items":{"type":"string"}}}}
    """;
}
