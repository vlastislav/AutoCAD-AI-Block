using System.Text.Json;

namespace BlockAI.Core;

public sealed record Point2(double X, double Y);
public sealed record Primitive(string Kind, Point2[] Points, Point2 Center, double Radius, bool Closed);
public sealed record BlockSpec(string Name, string Description, string[] Assumptions, Primitive[] Entities)
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static BlockSpec Parse(string json)
    {
        var spec = JsonSerializer.Deserialize<BlockSpec>(json, JsonOptions)
            ?? throw new InvalidDataException("The AI returned an empty block.");
        spec.Validate();
        return spec;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80 ||
            string.IsNullOrWhiteSpace(Description) || Description.Length > 2000)
            throw new InvalidDataException("Block name or description is missing or too long.");
        if (Assumptions is null || Assumptions.Length > 20 || Assumptions.Any(a => a is null || a.Length > 500))
            throw new InvalidDataException("Invalid design assumptions.");
        if (Entities is null || Entities.Length is < 1 or > 500)
            throw new InvalidDataException("A block must contain 1–500 entities.");
        foreach (var entity in Entities)
        {
            if (entity is null || entity.Points is null || entity.Center is null)
                throw new InvalidDataException("Incomplete geometry.");
            CheckPoint(entity.Center);
            if (!double.IsFinite(entity.Radius)) throw new InvalidDataException("Invalid radius.");
            if (entity.Kind == "circle")
            {
                if (entity.Radius <= 0 || entity.Radius > 50000 || entity.Points.Length != 0 || entity.Closed)
                    throw new InvalidDataException("Invalid circle.");
            }
            else if (entity.Kind == "polyline")
            {
                if (entity.Points.Length < (entity.Closed ? 3 : 2) || entity.Points.Length > 200 || entity.Radius != 0)
                    throw new InvalidDataException("Invalid polyline.");
                foreach (var point in entity.Points) CheckPoint(point);
                for (var i = 1; i < entity.Points.Length; i++)
                    if (entity.Points[i] == entity.Points[i - 1])
                        throw new InvalidDataException("A polyline has duplicate consecutive points.");
                if (entity.Closed)
                {
                    double area = 0;
                    for (var i = 0; i < entity.Points.Length; i++)
                    {
                        var a = entity.Points[i];
                        var b = entity.Points[(i + 1) % entity.Points.Length];
                        area += a.X * b.Y - b.X * a.Y;
                    }
                    if (Math.Abs(area) < 0.001) throw new InvalidDataException("A closed outline has no area.");
                }
            }
            else throw new InvalidDataException("Unsupported entity type.");
        }
        var bounds = Bounds();
        if (bounds.Width < 0.01 || bounds.Height < 0.01 || bounds.Width > 100000 || bounds.Height > 100000)
            throw new InvalidDataException("Block extents must be between 0.01 mm and 100 m on each axis.");
    }

    private static void CheckPoint(Point2? p)
    {
        if (p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > 100000 || Math.Abs(p.Y) > 100000)
            throw new InvalidDataException("Coordinates must be finite and within 100 m of the block origin.");
    }

    public (double MinX, double MinY, double Width, double Height) Bounds()
    {
        var points = Entities.SelectMany(e => e.Kind == "circle"
            ? new[] { new Point2(e.Center.X - e.Radius, e.Center.Y - e.Radius), new Point2(e.Center.X + e.Radius, e.Center.Y + e.Radius) }
            : e.Points).ToArray();
        var x = points.Min(p => p.X);
        var y = points.Min(p => p.Y);
        return (x, y, points.Max(p => p.X) - x, points.Max(p => p.Y) - y);
    }

    public static double MillimetersToDrawingUnits(string units) => units switch
    {
        "Millimeters" => 1, "Centimeters" => 0.1, "Meters" => 0.001,
        "Inches" => 1 / 25.4, "Feet" => 1 / 304.8,
        _ => throw new ArgumentException("Choose the drawing's units.")
    };

    public static BlockSpec DemoKingBed()
    {
        static Primitive Rect(double x, double y, double w, double h) => new("polyline",
            [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)], new(0, 0), 0, true);
        var shapes = new List<Primitive>
        {
            Rect(0, 0, 2050, 2200), Rect(-75, 2200, 2200, 90),
            Rect(60, 80, 1930, 2030), Rect(170, 1690, 760, 340), Rect(1120, 1690, 760, 340),
            new("polyline", [new(60, 1570), new(1990, 1570)], new(0, 0), 0, false),
            new("polyline", [new(60, 1490), new(1990, 1490)], new(0, 0), 0, false)
        };
        for (var x = 30; x < 2050; x += 170)
            shapes.Add(new("polyline", [new(x, 2215), new(x + 60, 2275)], new(0, 0), 0, false));
        return new("King_Wood_Bed", "King bed · wooden frame · plan view",
            ["Offline example; not generated from your prompt.", "Assumed North American king mattress: 1930 × 2030 mm (rounded).",
             "Frame: 2050 × 2200 mm; overall with headboard: 2200 × 2290 mm.",
             "Wood is suggested with headboard grain lines; no 3D material is assigned."], shapes.ToArray());
    }
}
