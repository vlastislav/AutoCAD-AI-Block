using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using BlockAI.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(BlockAI.AutoCAD.Plugin))]

namespace BlockAI.AutoCAD;

public class Plugin
{
    private static PaletteSet? palette;
    private static PendingInsert? lastInsertion;
    private static bool inserting;
    private sealed record PendingInsert(Document Document, BlockSpec Spec, double Scale);

    [CommandMethod("AIBLOCK", CommandFlags.Modal)]
    public void Show()
    {
        if (palette is null)
        {
            palette = new PaletteSet("Block AI", new Guid("D38800DE-0579-4AA7-96B2-A5D2A15B858A"))
            {
                Size = new System.Drawing.Size(420, 800),
                MinimumSize = new System.Drawing.Size(340, 580),
                Style = PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowPropertiesMenu
            };
            palette.Add("Create a block", new PromptPanel());
        }
        palette.Visible = true;
    }

    internal static async Task<bool> InsertAsync(BlockSpec spec, string units)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("Open a drawing first.");
        if (inserting) throw new InvalidOperationException("Finish or cancel the active insertion first.");
        if (!string.IsNullOrEmpty(doc.CommandInProgress)) throw new InvalidOperationException("Press Esc to finish the current AutoCAD command, then click Insert again.");
        spec.Validate();
        var job=new PendingInsert(doc,spec,BlockSpec.MillimetersToDrawingUnits(units));
        lastInsertion=job;
        inserting=true;
        System.Exception? failure=null;
        var inserted=false;
        try
        {
            await AcApp.DocumentManager.ExecuteInCommandContextAsync(_=>
            {
                // Never let an exception escape this native command-context callback.
                try
                {
                    if(AcApp.DocumentManager.MdiActiveDocument!=doc) throw new InvalidOperationException("The active drawing changed. Click Insert again in the intended drawing.");
                    doc.Window.Focus();
                    inserted=InsertGeometry(job);
                }
                catch(System.Exception ex) {failure=ex;}
                return Task.CompletedTask;
            },null);
            if(failure is not null) throw new InvalidOperationException(failure.Message,failure);
            return inserted;
        }
        finally {inserting=false;}
    }

    [CommandMethod("AIBLOCKINSERT", CommandFlags.Modal)]
    public void Insert()
    {
        var job = lastInsertion;
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (job is null || doc is null || job.Document != doc)
        {doc?.Editor.WriteMessage("\nGenerate a preview and use Insert into drawing first.\n");return;}
        try {InsertGeometry(job);}
        catch(System.Exception ex) {doc.Editor.WriteMessage($"\nBlock AI insertion failed: {ex.Message}\n");}
    }

    private static bool InsertGeometry(PendingInsert job)
    {
        var doc=job.Document;
        try
        {
            var point = doc.Editor.GetPoint("\nChoose block insertion point (Esc to cancel): ");
            if (point.Status != PromptStatus.OK) return false;
            using var transaction = doc.Database.TransactionManager.StartTransaction();
            var table = (BlockTable)transaction.GetObject(doc.Database.BlockTableId, OpenMode.ForWrite);
            var safeName = new string(job.Spec.Name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
            var definition = new BlockTableRecord { Name = $"AI_{safeName}_{Guid.NewGuid():N}", Origin = Point3d.Origin, Units = UnitsValue.Undefined };
            table.Add(definition);
            transaction.AddNewlyCreatedDBObject(definition, true);
            foreach (var geometry in job.Spec.Entities)
            {
                Entity entity;
                if (geometry.Kind == "circle")
                    entity = new Circle(new Point3d(geometry.Center.X, geometry.Center.Y, 0), Vector3d.ZAxis, geometry.Radius);
                else
                {
                    var path = new Polyline();
                    for (var i = 0; i < geometry.Points.Length; i++)
                        path.AddVertexAt(i, new Point2d(geometry.Points[i].X, geometry.Points[i].Y), 0, 0, 0);
                    path.Closed = geometry.Closed;
                    entity = path;
                }
                entity.SetDatabaseDefaults(doc.Database);
                entity.Layer = "0";
                entity.ColorIndex = 0; // ByBlock; inherit the block reference's appearance.
                definition.AppendEntity(entity);
                transaction.AddNewlyCreatedDBObject(entity, true);
            }
            var space = (BlockTableRecord)transaction.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
            var reference = new BlockReference(Point3d.Origin, definition.ObjectId) { ScaleFactors = new Scale3d(job.Scale) };
            reference.SetDatabaseDefaults(doc.Database);
            reference.TransformBy(doc.Editor.CurrentUserCoordinateSystem);
            reference.Position = point.Value; // GetPoint returns WCS; orient geometry without moving the picked point.
            space.AppendEntity(reference);
            transaction.AddNewlyCreatedDBObject(reference, true);
            transaction.Commit();
            doc.Editor.WriteMessage($"\nInserted {job.Spec.Name}. Use U to undo.\n");
            return true;
        }
        catch (System.Exception ex) {doc.Editor.WriteMessage($"\nBlock AI: {ex.Message}\n");throw;}
    }
}
