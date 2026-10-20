using BlockAI.Core;
using System.Net.Http;

namespace BlockAI.AutoCAD;

internal sealed class PromptPanel : UserControl
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly TextBox prompt = new() { Multiline = true, Height = 82, MaxLength = 4000, Text = "A king size wooden bed plan" };
    private readonly TextBox apiKey = new() { UseSystemPasswordChar = true, PlaceholderText = "OpenAI API key (kept in memory only)" };
    private readonly TextBox model = new() { Text = "gpt-4.1-mini" };
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
        Label("2D geometry from your description");
        Label("Describe your block. Include dimensions if you know them.");
        Add(prompt);
        Label("Custom generation sends this description to OpenAI.");
        Add(apiKey);
        apiKey.Text = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        Label("OpenAI model (API access and billing required)");
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
        if (string.IsNullOrWhiteSpace(apiKey.Text))
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
        status.Text = "Generating geometry…";
        cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var generated = await new AiClient(Http).GenerateAsync(prompt.Text, apiKey.Text, model.Text, cancellation.Token);
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
