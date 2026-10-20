using BlockAI.Core;
using System.Net.Http;
using System.Diagnostics;
using System.IO;

namespace BlockAI.AutoCAD;

internal sealed class PromptPanel : UserControl
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly HttpClient LocalHttp = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ComboBox provider = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label providerNote = new() { AutoSize = true };
    private readonly Label modelLabel = new() { AutoSize = true };
    private readonly Button startLocal = new() { Text = "Start local AI", Height = 28 };
    private readonly Button checkLocal = new() { Text = "Check models", Height = 28 };
    private readonly TextBox prompt = new() { Multiline = true, Height = 82, MaxLength = 4000, Text = "A king size wooden bed plan" };
    private readonly TextBox apiKey = new() { UseSystemPasswordChar = true, PlaceholderText = "OpenAI API key (kept in memory only)" };
    private readonly TextBox model = new() { Text = OllamaClient.DefaultModel };
    private readonly ComboBox units = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button generate = new() { Text = "Generate block", Height = 36 };
    private readonly Button demo = new() { Text = "Load example king bed", Height = 30 };
    private readonly Button insert = new() { Text = "Insert into drawing…", Enabled = false, Height = 36 };
    private readonly Button cancel = new() { Text = "Cancel generation", Enabled = false, Height = 28 };
    private readonly Label status = new() { Text = "Describe a 2D block, or load the offline example.", AutoSize = true };
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, Height = 115, ScrollBars = ScrollBars.Vertical };
    private readonly BlockPreview preview = new() { Height = 220 };
    private BlockSpec? spec;
    private CancellationTokenSource? cancellation;
    private bool IsLocal => provider.SelectedIndex == 0;
    private string localModel = OllamaClient.DefaultModel;
    private string openAiModel = "gpt-4.1-mini";

    public PromptPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(242, 244, 247);
        Font = new Font("Segoe UI", 9);
        AutoScroll = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control control)
        {
            control.Dock = DockStyle.Top;
            control.Margin = new Padding(0, 0, 0, 8);
            layout.Controls.Add(control, 0, layout.RowCount++);
        }
        void Label(string text) => Add(new Label { Text = text, AutoSize = true });
        Add(new Label { Text = "BLOCK AI", Font = new Font("Segoe UI", 17, FontStyle.Bold), AutoSize = true });
        Label("v0.2.1 · 2D geometry from your description");
        Label("Describe your block. Include dimensions if you know them.");
        Add(prompt);
        Label("Generation provider");
        provider.Items.AddRange(["Local AI — Ollama (no API fees)", "OpenAI (paid API)"]);
        provider.SelectedIndex = 0;
        Add(provider);
        Add(providerNote);
        Add(startLocal);
        Add(checkLocal);
        Add(apiKey);
        apiKey.Text = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        Add(modelLabel);
        Add(model);
        Add(generate);
        Add(cancel);
        Add(status);
        Add(demo);
        Add(preview);
        Add(details);
        Label("Drawing units — match your current drawing");
        units.Items.AddRange(["Millimeters", "Centimeters", "Meters", "Inches", "Feet"]);
        units.SelectedIndex = 0;
        Add(units);
        Add(insert);
        // Keep progress and errors next to Generate, visible without scrolling to the bottom.
        layout.SizeChanged += (_, _) =>
        {
            var width = Math.Max(100, layout.ClientSize.Width - layout.Padding.Horizontal - 8);
            foreach (var label in layout.Controls.OfType<Label>())
                label.MaximumSize = new Size(width, 0);
        };
        Controls.Add(layout);
        void UpdateProvider(bool savePrevious = false)
        {
            if (IsLocal) { if (savePrevious) openAiModel = model.Text; model.Text = localModel; }
            else { if (savePrevious) localModel = model.Text; model.Text = openAiModel; }
            apiKey.Visible = !IsLocal;
            startLocal.Visible = checkLocal.Visible = IsLocal;
            providerNote.Text = IsLocal
                ? "Runs on this PC. No API key required. First generation can take a few minutes."
                : "Sends your description to OpenAI. API access and billing required.";
            modelLabel.Text = IsLocal ? "Downloaded Ollama model" : "OpenAI model";
            status.Text = IsLocal ? "Start local AI, check models, then describe your block." : "Enter an API key to generate with OpenAI.";
        }
        provider.SelectedIndexChanged += (_, _) => UpdateProvider(true);
        UpdateProvider();
        startLocal.Click += (_, _) =>
        {
            try
            {
                const string launcher = @"D:\Ollama\start-ollama.ps1";
                if (!File.Exists(launcher)) throw new FileNotFoundException("Install local AI first. Expected launcher: " + launcher);
                Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{launcher}\"")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                status.Text = "Starting local AI. Wait a few seconds, then click Check models.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        checkLocal.Click += async (_, _) =>
        {
            SetBusy(true);
            cancel.Enabled = false;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var models = await new OllamaClient(LocalHttp).ListModelsAsync(timeout.Token);
                status.Text = models.Length == 0 ? "Ollama is running but has no local models. Download " + OllamaClient.DefaultModel + "."
                    : "Available local models: " + string.Join(", ", models);
                if (models.Length > 0 && !models.Contains(model.Text)) model.Text = models[0];
            }
            catch (OperationCanceledException) { status.Text = "Ollama did not respond. Start local AI and try again."; }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { SetBusy(false); }
        };
        generate.Click += async (_, _) => await GenerateAsync();
        cancel.Click += (_, _) => cancellation?.Cancel();
        demo.Click += (_, _) => ShowSpec(BlockSpec.DemoKingBed(), "Offline example ready. Check units, then insert.");
        insert.Click += (_, _) =>
        {
            try
            {
                if (spec is null) return;
                Plugin.QueueInsert(spec, units.Text);
                status.Text = "Choose a point in the drawing. Esc cancels insertion.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
    }

    private async Task GenerateAsync()
    {
        if (!IsLocal && string.IsNullOrWhiteSpace(apiKey.Text))
        {
            status.Text = "Enter an OpenAI API key above for AI generation, or click Load example king bed to try it offline.";
            apiKey.Focus();
            return;
        }
        spec = null;
        preview.Spec = null;
        preview.Invalidate();
        details.Clear();
        SetBusy(true);
        var useLocal = IsLocal;
        status.Text = useLocal ? "Generating on this PC… First run loads the model. You can cancel below." : "Generating geometry…";
        cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(useLocal ? 10 : 3));
        try
        {
            var generated = useLocal
                ? await new OllamaClient(LocalHttp).GenerateAsync(prompt.Text, model.Text, cancellation.Token)
                : await new AiClient(Http).GenerateAsync(prompt.Text, apiKey.Text, model.Text, cancellation.Token);
            ShowSpec(generated, "Preview ready. Review dimensions, then click Insert into drawing and pick a point.");
        }
        catch (OperationCanceledException) { status.Text = "Generation canceled or timed out. You can try again."; }
        catch (Exception ex)
        {
            status.Text = $"Could not generate: {ex.Message}";
            details.Text = status.Text;
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        generate.Enabled = demo.Enabled = prompt.Enabled = apiKey.Enabled = model.Enabled = !busy;
        provider.Enabled = startLocal.Enabled = checkLocal.Enabled = !busy;
        cancel.Enabled = busy;
        insert.Enabled = !busy && spec is not null;
    }

    private void ShowSpec(BlockSpec generated, string message)
    {
        generated.Validate();
        spec = generated;
        preview.Spec = generated;
        preview.Invalidate();
        var b = generated.Bounds();
        details.Text = $"{generated.Description}\r\nOverall: {b.Width:0.##} × {b.Height:0.##} mm\r\n{generated.Entities.Length} editable entities\r\n\r\n"
            + string.Join("\r\n", generated.Assumptions.Select(a => "• " + a));
        status.Text = message;
        insert.Enabled = cancellation is null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) cancellation?.Cancel();
        base.Dispose(disposing);
    }
}

internal sealed class BlockPreview : Control
{
    public BlockSpec? Spec { get; set; }
    public BlockPreview() { DoubleBuffered = true; BackColor = Color.FromArgb(24, 31, 42); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (Spec is null)
        {
            TextRenderer.DrawText(g, "Your block preview", Font, ClientRectangle, Color.SlateGray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var b = Spec.Bounds();
        var scale = Math.Min(Math.Max(1, Width - 36) / b.Width, Math.Max(1, Height - 36) / b.Height);
        var ox = (Width - b.Width * scale) / 2;
        var oy = (Height - b.Height * scale) / 2;
        PointF Map(Point2 p) => new((float)(ox + (p.X - b.MinX) * scale), (float)(Height - oy - (p.Y - b.MinY) * scale));
        using var pen = new Pen(Color.FromArgb(113, 224, 197), 1.4f);
        foreach (var entity in Spec.Entities)
        {
            if (entity.Kind == "circle")
            {
                var center = Map(entity.Center);
                var r = (float)(entity.Radius * scale);
                g.DrawEllipse(pen, center.X - r, center.Y - r, 2 * r, 2 * r);
            }
            else
            {
                var points = entity.Points.Select(Map).ToArray();
                if (entity.Closed) g.DrawPolygon(pen, points);
                else g.DrawLines(pen, points);
            }
        }
    }
}
