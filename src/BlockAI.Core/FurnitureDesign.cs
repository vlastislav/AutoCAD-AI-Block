using System.Globalization;
using System.Text.RegularExpressions;

namespace BlockAI.Core;

public sealed record FurnitureDesign(string Kind, double Width, double Depth, int Seats, string Style, string Shape, string[] Assumptions,
    bool Cushioned = true, bool Arms = true, string BackStyle = "solid")
{
    public FurnitureDesign ApplyChairConstraints(string prompt)
    {
        var design = this;
        if (Regex.IsMatch(prompt,@"\b(?:chair|armchair)\b",RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(prompt,@"\b(?:sofa|couch|table|desk|bed|cabinet)\b",RegexOptions.IgnoreCase))
        {
            var kind = Regex.IsMatch(prompt,@"\b(?:office|task|desk|swivel|gaming)\b",RegexOptions.IgnoreCase) ? "office_chair"
                : Regex.IsMatch(prompt,@"\b(?:lounge|armchair|club|accent)\b",RegexOptions.IgnoreCase) ? "lounge_chair" : "dining_chair";
            if (Kind != kind)
            {
                var (w,d)=kind switch {"office_chair"=>(680,680),"lounge_chair"=>(850,900),_=>(500,550)};
                design=design with {Kind=kind,Width=w,Depth=d,Seats=1,Shape="rectangular",Arms=kind!="dining_chair",Cushioned=kind!="dining_chair"};
            }
        }
        if (Regex.IsMatch(prompt,@"\b(?:no|without)\s+(?:(?:a|any)\s+)?(?:cushions?|cushioning|upholstery)|\b(?:uncushioned|unupholstered)\b",RegexOptions.IgnoreCase))
            design=design with {Cushioned=false};
        if (Regex.IsMatch(prompt,@"\barmless\b|\b(?:no|without)\s+(?:armrests?|arms)\b",RegexOptions.IgnoreCase))
            design=design with {Arms=false};
        if (Regex.IsMatch(prompt,@"\b(?:slatted|slat|spindle)\b",RegexOptions.IgnoreCase)) design=design with {BackStyle="slatted"};
        return design;
    }

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
            "chair" or "dining_chair" or "office_chair" or "lounge_chair" => (300, 1500, 300, 1500), "table" => (300, 6000, 300, 3000),
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
        if (BackStyle is not ("solid" or "slatted")) throw new InvalidDataException("Unsupported backrest style.");
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
            case "lounge_chair":
                var count = Kind == "lounge_chair" ? 1 : Seats;
                var arm = Math.Clamp(w * 0.065, 70, 190);
                var back = d * 0.22;
                var front = d * 0.06;
                var gap = Math.Clamp(w * 0.006, 8, 18);
                var seatWidth = (w - 2*arm - gap*(count+1))/count;
                if (seatWidth < 260) throw new InvalidDataException("Too many seats for this width. Increase width or reduce seat count.");
                Rect(0,0,w,d,corner);
                if (Arms)
                {
                    Rect(gap,front,arm-gap,d-front-gap,corner);
                    Rect(w-arm,front,arm-gap,d-front-gap,corner);
                }
                for(var i=0;i<count;i++)
                {
                    var x = arm + gap + i*(seatWidth+gap);
                    Rect(x,front,seatWidth,d-back-front-gap,corner);
                    if (Cushioned) Rect(x,d-back,seatWidth,back-gap,corner);
                }
                if (!Cushioned) Rect(arm,d-back,w-2*arm,back-gap,corner);
                notes.Add($"{count} seat section(s); {(Cushioned ? "cushioned" : "plain, no cushion seams")}; {(Arms ? "two arms" : "armless")}. Back is at the top of the plan.");
                break;
            case "chair":
            case "dining_chair":
                // A tapered seat and a separated back rail, not an upholstered box.
                entities.Add(new("polyline",[
                    new(w*.12,0),new(w*.88,0),new(w*.93,d*.06),new(w*.82,d*.68),
                    new(w*.76,d*.74),new(w*.24,d*.74),new(w*.18,d*.68),new(w*.07,d*.06)],new(0,0),0,true));
                Rect(w*.12,d*.64,w*.07,d*.24);
                Rect(w*.81,d*.64,w*.07,d*.24);
                entities.Add(new("polyline",[
                    new(0,d*.87),new(w*.1,d*.94),new(w*.3,d*.985),new(w*.5,d),new(w*.7,d*.985),new(w*.9,d*.94),new(w,d*.87),
                    new(w*.97,d*.8),new(w*.85,d*.85),new(w*.68,d*.89),new(w*.5,d*.905),new(w*.32,d*.89),new(w*.15,d*.85),new(w*.03,d*.8)],new(0,0),0,true));
                if (BackStyle=="slatted") for(var i=1;i<5;i++) Line(w*i/5,d*.9,w*i/5,d*.96);
                if (Cushioned) Rect(w*.22,d*.1,w*.56,d*.51,Math.Min(w,d)*.06);
                if (Arms) { Rect(0,d*.25,w*.09,d*.44,corner); Rect(w*.91,d*.25,w*.09,d*.44,corner); }
                notes.Add($"Dining chair: tapered seat, separate curved back rail and two back supports; {(Cushioned ? "one seat pad" : "no cushion or upholstery seams")}; {(Arms ? "with arms" : "armless")}.");
                break;
            case "office_chair":
                var wheelRadius = Math.Min(w,d)*.035;
                Point2[] wheels=[new(w/2,wheelRadius),new(w-wheelRadius,d*.23),new(w*.8,d*.68),new(w*.2,d*.68),new(wheelRadius,d*.23)];
                var hub=new Point2(w/2,d*.38);
                // Show only the exposed ends of the five-star base outside the seat.
                foreach(var wheel in wheels)
                {
                    var vx=wheel.X-hub.X; var vy=wheel.Y-hub.Y;
                    var t = Math.Min(Math.Abs(vx)<.001 ? double.PositiveInfinity : w*.30/Math.Abs(vx),
                        Math.Abs(vy)<.001 ? double.PositiveInfinity : (vy<0 ? d*.18 : d*.25)/Math.Abs(vy));
                    if(t<.95) Line(hub.X+vx*t,hub.Y+vy*t,wheel.X,wheel.Y);
                    entities.Add(new("circle",[],wheel,wheelRadius,false));
                }
                entities.Add(new("polyline",[new(w*.24,d*.2),new(w*.76,d*.2),new(w*.8,d*.27),new(w*.76,d*.63),new(w*.65,d*.69),new(w*.35,d*.69),new(w*.24,d*.63),new(w*.2,d*.27)],new(0,0),0,true));
                Rect(w*.17,d*.86,w*.66,d*.14,corner);
                Line(w*.43,d*.69,w*.43,d*.86); Line(w*.57,d*.69,w*.57,d*.86);
                if(Cushioned) Rect(w*.3,d*.29,w*.4,d*.29,corner);
                if(Arms) { Rect(w*.09,d*.4,w*.1,d*.3,corner);Rect(w*.81,d*.4,w*.1,d*.3,corner); }
                notes.Add($"Office chair: five-star caster base, shaped seat and backrest; {(Cushioned ? "seat pad indicated" : "plain seat")}; {(Arms ? "armrests" : "armless")}.");
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
        Supported kinds: sofa (straight, 1-8 seats, no chaise), bed, dining_chair, office_chair, lounge_chair, table, desk, cabinet.
        A plain 'chair' means dining_chair, NOT sofa or lounge_chair. Dining chairs have a tapered seat and back rail.
        Office/task/swivel/gaming chair means office_chair with caster base. Armchair/lounge/accent chair means lounge_chair.
        For other objects, multiple furniture items, L/U-shaped sofas, sectional/chaise sofas,
        oval tables, asymmetric designs or unsupported features, return kind other. Do not silently replace them.
        width and depth are overall horizontal size in MILLIMETERS. Convert explicit units correctly.
        Default sofa 3 seats 2400 x 950; dining_chair 500 x 550; office_chair 680 x 680; lounge_chair 850 x 900;
        desk 1400 x 700; cabinet 1200 x 500;
        rectangular table 1600 x 900; round table diameter 1000 (width=depth).
        Default bed: king overall 2030 x 2230, queen 1620 x 2230, double 1470 x 2100, single 1060 x 2100.
        Bed mattress is overall width minus 100, depth minus 200. If given mattress dimensions add 100 and 200.
        If given overall bed dimensions, use them directly. King defaults to North American sizing.
        seats is sofa seat count (default 3); for other kinds use 1. Shape is rectangular except round tables.
        style is modern, classic or minimal, matching the request; default modern.
        cushioned=false for 'no cushion', 'without cushions', unupholstered or plain wood seating. Dining defaults cushioned=false.
        arms=false for dining chairs by default; otherwise true unless armless/no arms/without armrests is requested.
        backStyle=slatted for slatted/spindle back, otherwise solid.
        Assumptions must list dimensions/details you assumed, not contradict explicit requests.
        These are simplified furniture plans; do not promise materials, ornate details, 3D or moving parts.
        For kind other set width=0, depth=0, seats=1, style=modern, shape=rectangular, assumptions=[], cushioned=false, arms=false, backStyle=solid.
        """;

    public const string Schema = """
    {"type":"object","additionalProperties":false,"required":["kind","width","depth","seats","style","shape","assumptions","cushioned","arms","backStyle"],"properties":{
    "kind":{"type":"string","enum":["sofa","bed","dining_chair","office_chair","lounge_chair","table","desk","cabinet","other"]},
    "width":{"type":"number"},"depth":{"type":"number"},"seats":{"type":"integer","minimum":1,"maximum":8},
    "style":{"type":"string","enum":["modern","classic","minimal"]},"shape":{"type":"string","enum":["rectangular","round"]},
    "cushioned":{"type":"boolean"},"arms":{"type":"boolean"},"backStyle":{"type":"string","enum":["solid","slatted"]},
    "assumptions":{"type":"array","maxItems":6,"items":{"type":"string"}}}}
    """;
}
