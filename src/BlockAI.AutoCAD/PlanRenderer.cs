using BlockAI.Core;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace BlockAI.AutoCAD;

internal static class PlanRenderer
{
    public static byte[] Png(BlockSpec block)
    {
        using var bitmap=new Bitmap(720,720);
        using var g=Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        Draw(g,new Rectangle(0,0,720,720),block,Color.Black);
        using var output=new MemoryStream();
        bitmap.Save(output,ImageFormat.Png);
        return output.ToArray();
    }

    public static void Draw(Graphics g,Rectangle area,BlockSpec block,Color color)
    {
        g.SmoothingMode=SmoothingMode.AntiAlias;
        var b=block.Bounds();
        var scale=Math.Min((area.Width-40)/b.Width,(area.Height-40)/b.Height);
        var ox=area.Left+(area.Width-b.Width*scale)/2;
        var oy=area.Top+(area.Height-b.Height*scale)/2;
        PointF Map(Point2 p)=>new((float)(ox+(p.X-b.MinX)*scale),(float)(area.Bottom-oy-(p.Y-b.MinY)*scale));
        using var pen=new Pen(color,1.6f);
        foreach(var entity in block.Entities)
        {
            if(entity.Kind=="circle")
            {var c=Map(entity.Center);var r=(float)(entity.Radius*scale);g.DrawEllipse(pen,c.X-r,c.Y-r,2*r,2*r);}
            else
            {var points=entity.Points.Select(Map).ToArray();if(entity.Closed)g.DrawPolygon(pen,points);else g.DrawLines(pen,points);}
        }
    }
}
