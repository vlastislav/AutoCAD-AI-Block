using BlockAI.Core;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace BlockAI.AutoCAD;

internal sealed class ReferenceForm : Form
{
    private readonly ListView list=new(){Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false};
    private readonly PictureBox picture=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.White};
    private readonly TextBox notes=new(){Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
    private readonly Label status=new(){AutoSize=true,Dock=DockStyle.Bottom,Padding=new Padding(10)};
    private readonly Button add=new(){Text="Analyze reference image",AutoSize=true};
    private readonly Button edit=new(){Text="Edit name / tags",AutoSize=true};
    private readonly Button refresh=new(){Text="Refresh",AutoSize=true};
    private readonly string directory;
    private readonly string model;
    private readonly HttpClient http;
    private CancellationTokenSource? cancellation;

    public ReferenceForm(string directory,string model,HttpClient http)
    {
        this.directory=directory;this.model=string.IsNullOrWhiteSpace(model)?DesignAgent.DefaultModel:model;this.http=http;
        Text="Furniture reference studies";Size=new Size(1040,760);MinimumSize=new Size(780,550);StartPosition=FormStartPosition.CenterParent;
        Font=new Font("Segoe UI",9);BackColor=Color.FromArgb(242,244,247);
        var header=new Label{Text="Named reference analyses guide new designs. Names and tags help matching; the source geometry is not inserted or traced.",Dock=DockStyle.Top,Height=52,Padding=new Padding(12)};
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Top,Height=44,Padding=new Padding(8)};
        var open=new Button{Text="Open library folder",AutoSize=true};buttons.Controls.AddRange([add,edit,refresh,open]);
        var split=new SplitContainer{Dock=DockStyle.Fill,Width=1000,SplitterDistance=520,FixedPanel=FixedPanel.Panel1};
        list.Columns.Add("Name",240);list.Columns.Add("Category",80);list.Columns.Add("View",65);list.Columns.Add("Confidence",80);
        list.Columns.Add("Status",95);
        split.Panel1.Controls.Add(list);
        var right=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};right.RowStyles.Add(new RowStyle(SizeType.Percent,55));right.RowStyles.Add(new RowStyle(SizeType.Percent,45));
        right.Controls.Add(picture,0,0);right.Controls.Add(notes,0,1);split.Panel2.Controls.Add(right);
        Controls.Add(split);Controls.Add(buttons);Controls.Add(header);Controls.Add(status);
        list.SelectedIndexChanged+=(_,_)=>ShowSelection();refresh.Click+=(_,_)=>Reload();edit.Click+=(_,_)=>EditSelection();add.Click+=async(_,_)=>await AddImageAsync();
        open.Click+=(_,_)=>{try{Directory.CreateDirectory(directory);Process.Start(new ProcessStartInfo(directory){UseShellExecute=true});}catch(Exception ex){status.Text=ex.Message;}};
        Reload();
    }
    private ReferenceLesson? Selected=>list.SelectedItems.Count==0?null:list.SelectedItems[0].Tag as ReferenceLesson;
    private void Reload()
    {
        var library=ReferenceLibrary.Load(directory,out var warnings);list.Items.Clear();
        foreach(var lesson in library.Lessons)
        {
            var a=lesson.Analysis;var row=new ListViewItem([a.Name,a.Category,a.View,a.Confidence.ToString("0.00"),lesson.Approved?"Reviewed":"Needs review"]){Tag=lesson};list.Items.Add(row);
        }
        status.Text=$"{library.Lessons.Count(l=>l.Approved)} reviewed / {library.Lessons.Length} named references. Vision model: {model}. Analysis is cached; Generate does not reanalyze images."+(warnings.Length>0?$" {warnings.Length} invalid file(s) skipped.":"");
        if(list.Items.Count>0)list.Items[0].Selected=true;else{notes.Text="No reference lessons installed. Add a clear isolated plan or elevation image, or import analyzed DWG examples with the preparation tool.";picture.Image?.Dispose();picture.Image=null;}
    }
    private void ShowSelection()
    {
        if(Selected is not{} lesson)return;
        picture.Image?.Dispose();picture.Image=null;
        var path=Path.Combine(directory,lesson.Id+".png");
        try{if(File.Exists(path)){using var stream=File.OpenRead(path);using var original=Image.FromStream(stream);picture.Image=new Bitmap(original);}}catch(Exception ex){status.Text="Preview unavailable: "+ex.Message;}
        var a=lesson.Analysis;
        notes.Text=$"{a.Name}\r\n{a.Category} · {a.View} · confidence {a.Confidence:0.00}\r\nReviewed for generation: {lesson.Approved}\r\n{lesson.CurationNote}\r\nTags: {string.Join(", ",a.Tags)}\r\nSource: {lesson.SourceFile} / {lesson.SourceGroup}\r\nAnalyzed by {lesson.Model}\r\n\r\n"+
            "Parts:\r\n"+string.Join("\r\n",a.Parts)+"\r\n\r\nProportions:\r\n"+string.Join("\r\n",a.Proportions)+"\r\n\r\nConnections:\r\n"+string.Join("\r\n",a.Connections)+"\r\n\r\nVisibility:\r\n"+string.Join("\r\n",a.Visibility)+"\r\n\r\nUncertainty:\r\n"+string.Join("\r\n",a.Uncertainties)+$"\r\n\r\nCushioned: {a.Cushioned?.ToString()??"unknown"}; armrests: {a.Armrests?.ToString()??"unknown"}.";
    }
    private void EditSelection()
    {
        if(Selected is not{} lesson)return;
        using var dialog=new Form{Text="Reference labels and review",Size=new Size(530,400),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false};
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,Padding=new Padding(14),AutoSize=true};
        var name=new TextBox{Text=lesson.Analysis.Name,MaxLength=100,Dock=DockStyle.Top};
        var category=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Top};category.Items.AddRange(ReferenceLibrary.Categories);category.SelectedItem=lesson.Analysis.Category;
        var tags=new TextBox{Text=string.Join(", ",lesson.Analysis.Tags),Dock=DockStyle.Top};
        var approved=new CheckBox{Text="Reviewed and usable for generation",Checked=lesson.Approved,AutoSize=true};
        foreach(var c in new Control[]{new Label{Text="Meaningful name",AutoSize=true},name,new Label{Text="Category",AutoSize=true},category,new Label{Text="Search tags (comma separated, up to 8)",AutoSize=true},tags})layout.Controls.Add(c);
        layout.Controls.Add(approved);var save=new Button{Text="Save labels",DialogResult=DialogResult.OK,AutoSize=true};layout.Controls.Add(save);dialog.Controls.Add(layout);dialog.AcceptButton=save;
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try
        {
            var changed=lesson with {Approved=approved.Checked,CurationNote="Labels/review status edited locally on "+DateTimeOffset.Now.ToString("u"),Analysis=lesson.Analysis with {Name=name.Text.Trim(),Category=category.Text,Tags=tags.Text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)}};
            ReferenceLibrary.Save(directory,changed);Reload();
        }
        catch(Exception ex){status.Text="Could not save labels: "+ex.Message;}
    }
    private async Task AddImageAsync()
    {
        using var picker=new OpenFileDialog{Title="Select one isolated furniture plan or elevation",Filter="Reference images|*.png;*.jpg;*.jpeg",Multiselect=false};
        if(picker.ShowDialog(this)!=DialogResult.OK)return;
        add.Enabled=edit.Enabled=refresh.Enabled=false;cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            if(new FileInfo(picker.FileName).Length>8_000_000)throw new InvalidDataException("Choose an image smaller than 8 MB.");
            var bytes=await File.ReadAllBytesAsync(picker.FileName,cancellation.Token);
            using(var stream=new MemoryStream(bytes))using(var image=Image.FromStream(stream))if((long)image.Width*image.Height>16_000_000)throw new InvalidDataException("Choose an image under 16 megapixels.");
            status.Text="Analyzing observed parts, proportions and connections with local vision AI…";
            var lesson=await new ReferenceAnalyzer(new OllamaClient(http)).AnalyzeAsync(bytes,picker.FileName,"user-selected isolated image",model,cancellation.Token);
            ReferenceLibrary.Save(directory,lesson);
            // Store a standard PNG preview; analysis provenance retains the original image hash.
            using(var stream=new MemoryStream(bytes))using(var image=Image.FromStream(stream))image.Save(Path.Combine(directory,lesson.Id+".png"),System.Drawing.Imaging.ImageFormat.Png);
            if(!IsDisposed)Reload();
        }
        catch(OperationCanceledException){if(!IsDisposed)status.Text="Reference analysis canceled or timed out.";}
        catch(Exception ex){if(!IsDisposed)status.Text="Could not analyze reference: "+ex.Message;}
        finally{cancellation?.Dispose();cancellation=null;if(!IsDisposed)add.Enabled=edit.Enabled=refresh.Enabled=true;}
    }
    protected override void Dispose(bool disposing)
    {if(disposing){cancellation?.Cancel();picture.Image?.Dispose();}base.Dispose(disposing);}
}
