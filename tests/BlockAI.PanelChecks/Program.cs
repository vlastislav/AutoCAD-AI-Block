using BlockAI.Core;
using System.Reflection;
using System.IO;

namespace BlockAI.AutoCAD;

internal static class Plugin
{
    internal static BlockSpec? Inserted;
    internal static void QueueInsert(BlockSpec spec, string units) { Inserted = spec; }
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
                Check(Field<TextBox>("model").Text == OllamaClient.DefaultModel, "local model default");
                Check(!Field<TextBox>("apiKey").Visible, "no key field in local mode");
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
                using var image = new Bitmap(panel.Width, panel.Height);
                panel.DrawToBitmap(image, panel.ClientRectangle);
                image.Save("artifacts-next/panel-local-preview.png");
                Field<Button>("checkLocal").PerformClick();
                for (var i = 0; i < 100 && !Field<Button>("checkLocal").Enabled; i++) await Task.Delay(100);
                Check(Field<Label>("status").Text.StartsWith("Available local models:") || Field<Label>("status").Text.Contains("has no local models"), "live Ollama discovery updates UI");
                Console.WriteLine("Panel smoke passed (AutoCAD bridge stubbed). Screenshot: artifacts-next/panel-local-preview.png");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { form.Close(); }
        };
        Application.Run(form);
    }
}
