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

    public static byte[] Png(DesignViews views)
    {
        using var bitmap=new Bitmap(1440,720);
        using var g=Graphics.FromImage(bitmap);g.Clear(Color.White);
        using var font=new Font("Segoe UI",16,FontStyle.Bold);
        using var small=new Font("Segoe UI",10);
        var panels=new[]{("PLAN",views.Plan),("FRONT",views.Front),("RIGHT SIDE",views.Side)};
        var scale=Math.Min(440/panels.Max(p=>p.Item2.Bounds().Width),600/panels.Max(p=>p.Item2.Bounds().Height));
        for(var i=0;i<panels.Length;i++)
        {var b=panels[i].Item2.Bounds();g.DrawString(panels[i].Item1,font,Brushes.Black,i*480+20,12);g.DrawString($"{b.Width:0.#} × {b.Height:0.#} mm · shared scale",small,Brushes.Black,i*480+20,39);Draw(g,new Rectangle(i*480,65,480,630),panels[i].Item2,Color.Black,scale);}
        using var output=new MemoryStream();bitmap.Save(output,ImageFormat.Png);return output.ToArray();
    }

    public static void Draw(Graphics g,Rectangle area,BlockSpec block,Color color,double? fixedScale=null)
    {
        g.SmoothingMode=SmoothingMode.AntiAlias;
        var b=block.Bounds();
        var scale=fixedScale??Math.Min((area.Width-40)/b.Width,(area.Height-40)/b.Height);
        var ox=area.Left+(area.Width-b.Width*scale)/2;
        var oy=area.Top+(area.Height-b.Height*scale)/2;
        PointF Map(Point2 p)=>new((float)(ox+(p.X-b.MinX)*scale),(float)(oy+b.Height*scale-(p.Y-b.MinY)*scale));
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
