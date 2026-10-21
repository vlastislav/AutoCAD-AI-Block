using BlockAI.Core;
using System.Reflection;
using System.IO;

namespace BlockAI.AutoCAD;

internal static class Plugin
{
    internal static BlockSpec? Inserted;
    internal static bool NextResult=true;
    internal static Exception? Failure;
    internal static Task<bool> InsertAsync(BlockSpec spec, string units)
    {if(Failure is not null)return Task.FromException<bool>(Failure);Inserted=spec;return Task.FromResult(NextResult);}
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory("artifacts-next");
        using var panel = new PromptPanel();
        using var form = new Form { Size = new Size(470, 1200), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) };
        form.Controls.Add(panel);
        T Field<T>(string name) where T : class => (T)typeof(PromptPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS UI " + message); }
        form.Shown += async (_, _) =>
        {
            try
            {
                Check(Field<ComboBox>("provider").SelectedIndex == 0, "local provider default");
                Check(Field<TextBox>("model").Text == DesignAgent.DefaultModel, "local model default");
                Check(!Field<TextBox>("apiKey").Visible, "no key field in local mode");
                Check(!Field<CheckBox>("elevations").Checked&&!Field<CheckBox>("elevations").Visible&&!Field<ComboBox>("drawingView").Visible,"current palette exposes plan views only");
                Field<TextBox>("model").Text = "custom:local";
                Field<ComboBox>("provider").SelectedIndex = 1;
                Check(Field<TextBox>("model").Text == "gpt-4.1-mini", "OpenAI model retained independently");
                Check(Field<TextBox>("apiKey").Visible, "key field visible for OpenAI");
                Field<ComboBox>("provider").SelectedIndex = 0;
                Check(Field<TextBox>("model").Text == "custom:local", "local model restored on switch");
                Field<TextBox>("model").Text = OllamaClient.DefaultModel;
                Field<Button>("demo").PerformClick();
                Check(Field<Button>("insert").Enabled, "preview enables insertion");
                Field<Button>("insert").PerformClick();
                Check(Plugin.Inserted is not null, "insert passes validated geometry to AutoCAD bridge");
                Check(Field<Label>("status").Text.StartsWith("Block inserted")&&Field<Button>("insert").Enabled,"insertion success reported and button reusable");
                Plugin.NextResult=false;
                Field<Button>("insert").PerformClick();
                Check(Field<Label>("status").Text.Contains("canceled")&&Field<Button>("insert").Enabled,"canceled insertion can be retried");
                Plugin.Failure=new InvalidOperationException("Drawing is busy");
                Field<Button>("insert").PerformClick();
                Check(Field<Label>("status").Text.Contains("Drawing is busy")&&Field<Button>("insert").Enabled,"insertion error visible and button reusable");
                Plugin.Failure=null;Plugin.NextResult=true;
                var multi=ElevationBuilder.Build(new DesignPlan("PanelFixture","Table with a pedestal",1000,600,
                    [new("base","rectangle",.3,.3,.4,.4,0,1,"none",0,false,"none",0,[],Z:0,Height:400),
                     new("tabletop","rectangle",0,0,1,1,0,1,"none",0,false,"none",0,[],Z:400,Height:50)],450),"table");
                {
                    typeof(PromptPanel).GetField("generatedViews",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(panel,multi);
                    typeof(PromptPanel).GetMethod("SetBusy",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(panel,[false]);
                    Field<ComboBox>("drawingView").SelectedIndex=1;
                    Field<Button>("insert").PerformClick();
                    Check(ReferenceEquals(Plugin.Inserted,multi.Front),"front selector sends front geometry to insertion bridge");
                    Field<ComboBox>("drawingView").SelectedIndex=2;
                    Field<Button>("insert").PerformClick();
                    Check(ReferenceEquals(Plugin.Inserted,multi.Side),"side selector sends side geometry to insertion bridge");
                    Field<Button>("demo").PerformClick();
                    Check(!Field<ComboBox>("drawingView").Enabled&&Field<ComboBox>("drawingView").SelectedIndex==0,"offline example clears stale elevation selection");
                }
                using var image = new Bitmap(panel.Width, panel.Height);
                panel.DrawToBitmap(image, panel.ClientRectangle);
                image.Save("artifacts-next/panel-local-preview.png");
                using(var referenceHttp=new System.Net.Http.HttpClient())
                using(var references=new ReferenceForm(ReferenceLibrary.DefaultDirectory,DesignAgent.DefaultModel,referenceHttp)
                    {StartPosition=FormStartPosition.Manual,Location=new Point(-5000,-5000),ShowInTaskbar=false})
                {
                    references.Show();Application.DoEvents();
                    using var referenceImage=new Bitmap(references.Width,references.Height);
                    references.DrawToBitmap(referenceImage,references.ClientRectangle);
                    referenceImage.Save("artifacts-next/reference-library-preview.png");
                    Check(references.Controls.Count>0,"reference library opens independently of generation");
                    references.Close();
                }
                Field<Button>("checkLocal").PerformClick();
                for (var i = 0; i < 100 && !Field<Button>("checkLocal").Enabled; i++) await Task.Delay(100);
                Check(Field<Label>("status").Text.StartsWith("Available local models:") || Field<Label>("status").Text.Contains("has no local models"), "live Ollama discovery updates UI");
                {
                    var sofa = File.Exists("artifacts-next/local-sofa-smoke.json")
                        ? BlockSpec.Parse(File.ReadAllText("artifacts-next/local-sofa-smoke.json"))
                        : new FurnitureDesign("sofa",2400,950,3,"modern","rectangular",[]).Build();
                    typeof(PromptPanel).GetMethod("ShowSpec", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(panel, [sofa, "Furniture generator: sofa ready for insertion."]);
                    using var sofaImage = new Bitmap(panel.Width, panel.Height);
                    panel.DrawToBitmap(sofaImage, panel.ClientRectangle);
                    sofaImage.Save("artifacts-next/panel-generated-sofa.png");
                    var preview = Field<BlockPreview>("preview");
                    using var plan = new Bitmap(preview.Width,preview.Height);
                    preview.DrawToBitmap(plan,preview.ClientRectangle);
                    plan.Save("artifacts-next/generated-sofa-plan.png");
                }
                Console.WriteLine("Panel smoke passed (AutoCAD bridge stubbed). Screenshot: artifacts-next/panel-local-preview.png");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { form.Close(); }
        };
        Application.Run(form);
    }
}
