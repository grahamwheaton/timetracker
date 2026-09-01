using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace TimelineApp;

internal sealed record TimelineEntry(
    DateTime Timestamp,
    string Day,
    string Time,
    string Program,
    string FileName,
    string WindowTitle,
    string? Executable,
    string Note,
    string Screenshot);

internal sealed record WindowDetails(string Program, string FileName, string WindowTitle, string? Executable);

internal static class InputActivity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        internal uint Size;
        internal uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    internal static double IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        var elapsedMilliseconds = unchecked((uint)Environment.TickCount - info.Time);
        return elapsedMilliseconds / 1000d;
    }
}

internal sealed class DayTimelineView : UserControl
{
    private List<TimelineEntry> entries = [];

    internal DayTimelineView()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
    }

    internal void SetEntries(IEnumerable<TimelineEntry> dayEntries)
    {
        entries = dayEntries.OrderBy(e => e.Timestamp).ToList();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var chartLeft = 58;
        var chartTop = 8;
        var chartRight = Math.Max(chartLeft + 100, ClientSize.Width - 10);
        const int rowHeight = 30;
        const int firstHour = 7;
        const int lastHour = 19;
        var chartWidth = chartRight - chartLeft;

        using var hourFont = new Font("Segoe UI", 8.5f);
        using var segmentFont = new Font("Segoe UI Semibold", 7.5f);
        using var gridPen = new Pen(Color.FromArgb(220, 224, 228));
        using var quarterPen = new Pen(Color.FromArgb(238, 240, 242)) { DashStyle = DashStyle.Dot };

        for (var hour = firstHour; hour < lastHour; hour++)
        {
            var y = chartTop + (hour - firstHour) * rowHeight;
            if ((hour - firstHour) % 2 == 0)
                e.Graphics.FillRectangle(Brushes.WhiteSmoke, chartLeft, y, chartWidth, rowHeight);
            e.Graphics.DrawString($"{hour:00}:00", hourFont, Brushes.DimGray, 7, y + 7);
            e.Graphics.DrawLine(gridPen, chartLeft, y + rowHeight, chartRight, y + rowHeight);
            for (var quarter = 1; quarter < 4; quarter++)
            {
                var x = chartLeft + chartWidth * quarter / 4f;
                e.Graphics.DrawLine(quarterPen, x, y, x, y + rowHeight);
            }
        }

        var segments = BuildSegments();
        foreach (var segment in segments)
        {
            var cursor = segment.Start;
            while (cursor < segment.End)
            {
                var hourStart = new DateTime(cursor.Year, cursor.Month, cursor.Day, cursor.Hour, 0, 0);
                var hourEnd = hourStart.AddHours(1);
                var pieceEnd = segment.End < hourEnd ? segment.End : hourEnd;
                if (cursor.Hour >= firstHour && cursor.Hour < lastHour)
                {
                    var y = chartTop + (cursor.Hour - firstHour) * rowHeight + 3;
                    var x = chartLeft + (float)cursor.Minute / 60 * chartWidth;
                    var width = Math.Max(2, (float)(pieceEnd - cursor).TotalMinutes / 60 * chartWidth);
                    var color = ActivityColor(segment.Key);
                    using var brush = new SolidBrush(color);
                    e.Graphics.FillRectangle(brush, x, y, width, rowHeight - 6);
                    if (width > 92)
                    {
                        var label = string.IsNullOrWhiteSpace(segment.FileName) ? segment.Program : $"{segment.Program}: {segment.FileName}";
                        e.Graphics.SetClip(new RectangleF(x + 3, y, width - 6, rowHeight - 6));
                        e.Graphics.DrawString(label, segmentFont, Brushes.White, x + 4, y + 5);
                        e.Graphics.ResetClip();
                    }
                }
                cursor = pieceEnd;
            }
        }

        var legendY = chartTop + (lastHour - firstHour) * rowHeight + 12;
        var totals = segments.GroupBy(s => s.Key)
            .Select(g => new { Segment = g.First(), Minutes = g.Sum(s => (s.End - s.Start).TotalMinutes) })
            .OrderByDescending(x => x.Minutes).Take(6).ToList();
        var legendX = 10;
        foreach (var total in totals)
        {
            var label = $"{total.Segment.Program} — {total.Segment.FileName} ({FormatMinutes(total.Minutes)})";
            var size = e.Graphics.MeasureString(label, hourFont);
            if (legendX + size.Width + 28 > ClientSize.Width) { legendX = 10; legendY += 24; }
            using var brush = new SolidBrush(ActivityColor(total.Segment.Key));
            e.Graphics.FillRectangle(brush, legendX, legendY + 3, 13, 13);
            e.Graphics.DrawString(label, hourFont, Brushes.Black, legendX + 18, legendY);
            legendX += (int)size.Width + 34;
        }
    }

    private List<ActivitySegment> BuildSegments()
    {
        var result = new List<ActivitySegment>();
        foreach (var entry in entries)
        {
            var start = entry.Timestamp;
            if (start.Hour < 7 || start.Hour >= 19) continue;
            var end = start.AddMinutes(5);
            var dayEnd = start.Date.AddHours(19);
            if (end > dayEnd) end = dayEnd;
            var key = entry.Program + "|" + entry.FileName;
            if (result.Count > 0)
            {
                var previous = result[^1];
                if (previous.Key == key && start <= previous.End.AddMinutes(1))
                {
                    result[^1] = previous with { End = end > previous.End ? end : previous.End };
                    continue;
                }
            }
            result.Add(new ActivitySegment(start, end, key, entry.Program, entry.FileName));
        }
        return result;
    }

    private static string FormatMinutes(double minutes) => minutes >= 60 ? $"{minutes / 60:0.#}h" : $"{minutes:0}m";

    private static Color ActivityColor(string value)
    {
        if (value.StartsWith("Idle / uncertain|", StringComparison.Ordinal))
            return Color.FromArgb(145, 151, 158);
        uint hash = 2166136261;
        foreach (var character in value) hash = (hash ^ character) * 16777619;
        var hue = hash % 360;
        return FromHsv(hue, 0.58, 0.72);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x)
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    private sealed record ActivitySegment(DateTime Start, DateTime End, string Key, string Program, string FileName);
}

internal sealed class MainForm : Form
{
    private readonly string dataRoot = Path.Combine(AppContext.BaseDirectory, "Timeline Data");
    private readonly string screenshotRoot;
    private readonly string logPath;
    private readonly Label status = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly ListView list = new() { View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly TextBox note = new();
    private readonly DayTimelineView timeline = new();
    private readonly Label dayHeading = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 16) };
    private readonly bool autoCapture;
    private readonly System.Windows.Forms.Timer captureTimer = new() { Interval = 5 * 60 * 1000 };
    private bool captureInProgress;
    private DateTime selectedDay = DateTime.Today;

    internal MainForm(bool autoCapture)
    {
        this.autoCapture = autoCapture;
        screenshotRoot = Path.Combine(dataRoot, "Screenshots");
        logPath = Path.Combine(dataRoot, "timeline.jsonl");
        Directory.CreateDirectory(screenshotRoot);
        ConvertExistingScreenshots();
        CleanupOldData();

        Text = "Timeline";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Timeline.ico");
        if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 780);
        MinimumSize = new Size(760, 680);
        Font = new Font("Segoe UI", 9);

        dayHeading.Location = new Point(16, 14);
        status.Text = "Ready";
        status.Location = new Point(18, 49);

        var previousDay = new Button { Text = "‹ Previous", Location = new Point(635, 15), Size = new Size(82, 29), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        var today = new Button { Text = "Today", Location = new Point(723, 15), Size = new Size(70, 29), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        var nextDay = new Button { Text = "Next ›", Location = new Point(799, 15), Size = new Size(82, 29), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        previousDay.Click += (_, _) => { selectedDay = selectedDay.AddDays(-1); RefreshPage(); };
        today.Click += (_, _) => { selectedDay = DateTime.Today; RefreshPage(); };
        nextDay.Click += (_, _) => { if (selectedDay.Date < DateTime.Today) selectedDay = selectedDay.AddDays(1); RefreshPage(); };

        timeline.Location = new Point(18, 75);
        timeline.Size = new Size(864, 425);
        timeline.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        timeline.BorderStyle = BorderStyle.FixedSingle;

        list.Location = new Point(18, 512);
        list.Size = new Size(864, 188);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.Columns.Add("Time", 80);
        list.Columns.Add("Program", 130);
        list.Columns.Add("File / window", 300);
        list.Columns.Add("Note", 155);

        var noteLabel = new Label { Text = "Optional note", AutoSize = true, Location = new Point(18, 709), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        note.Location = new Point(18, 731);
        note.Size = new Size(516, 25);
        note.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        var capture = MakeButton("Capture now", 545, 730, 100);
        var summary = MakeButton("Open summary", 652, 730, 106);
        var folder = MakeButton("Data folder", 765, 730, 117);

        capture.Click += async (_, _) => await CaptureAsync(false);
        summary.Click += (_, _) => OpenPath(ExportDailySummary(selectedDay.ToString("yyyy-MM-dd")));
        folder.Click += (_, _) => OpenPath(dataRoot);
        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is string paths)
                foreach (var path in paths.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    if (File.Exists(path)) OpenPath(path);
        };

        Controls.AddRange([dayHeading, status, previousDay, today, nextDay, timeline, list, noteLabel, note, capture, summary, folder]);
        captureTimer.Tick += async (_, _) => await CaptureAsync(true);
        Shown += async (_, _) =>
        {
            RefreshPage();
            if (this.autoCapture)
            {
                await CaptureAsync(false);
                WindowState = FormWindowState.Minimized;
            }
        };
    }

    private Button MakeButton(string text, int x, int y, int width) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 30),
        Anchor = AnchorStyles.Bottom | AnchorStyles.Right
    };

    private async Task CaptureAsync(bool scheduled)
    {
        if (captureInProgress) return;
        captureInProgress = true;
        captureTimer.Stop();
        status.Text = "Capturing in 1 second...";
        var wasVisible = Visible && WindowState != FormWindowState.Minimized;
        if (wasVisible) Hide();
        await Task.Delay(1000);

        try
        {
            var details = GetForegroundDetails();
            var idleSeconds = InputActivity.IdleSeconds();
            if (idleSeconds >= 120)
                details = new WindowDetails(
                    "Idle / uncertain",
                    $"{details.Program}: {details.FileName}",
                    details.WindowTitle,
                    details.Executable);
            var paths = SaveScreenshots(DateTime.Now);
            var now = DateTime.Now;
            var entry = new TimelineEntry(
                now, now.ToString("yyyy-MM-dd"), now.ToString("HH:mm:ss"), details.Program,
                details.FileName, details.WindowTitle, details.Executable, note.Text.Trim(), string.Join('|', paths));
            File.AppendAllText(logPath, JsonSerializer.Serialize(entry) + Environment.NewLine, new UTF8Encoding(false));
            CleanupOldData();
            ExportDailySummary(entry.Day);
            note.Clear();
            status.Text = idleSeconds >= 120
                ? $"Captured as idle / uncertain ({idleSeconds / 60:0} minutes without input) — next capture in 5 minutes"
                : $"Captured {details.Program}: {details.FileName} — next automatic capture in 5 minutes";
        }
        catch (Exception ex)
        {
            status.Text = $"Capture failed: {ex.Message}";
        }
        finally
        {
            if (wasVisible)
            {
                Show();
                if (!scheduled) Activate();
            }
            RefreshPage();
            captureInProgress = false;
            if (autoCapture) captureTimer.Start();
        }
    }

    private static WindowDetails GetForegroundDetails()
    {
        var focused = AutomationElement.FocusedElement;
        var window = focused;
        while (window is not null && window.Current.ControlType != ControlType.Window)
            window = TreeWalker.ControlViewWalker.GetParent(window);

        var source = window ?? focused;
        var title = source?.Current.Name?.Trim() ?? string.Empty;
        var processId = source?.Current.ProcessId ?? 0;
        string program = "Unknown";
        string? executable = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            program = process.ProcessName;
            try { executable = process.MainModule?.FileName; } catch { }
        }
        catch { }

        var fileName = title;
        foreach (var separator in new[] { " - ", " – ", " — " })
        {
            var index = title.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                fileName = title[..index].Trim();
                break;
            }
        }
        return new WindowDetails(program, fileName, title, executable);
    }

    private List<string> SaveScreenshots(DateTime timestamp)
    {
        var paths = new List<string>();
        var screens = Screen.AllScreens;
        for (var index = 0; index < screens.Length; index++)
        {
            var screen = screens[index];
            var primary = screen.Primary ? "-Primary" : string.Empty;
            var path = Path.Combine(screenshotRoot, $"{timestamp:yyyy-MM-dd_HH-mm-ss}_Monitor-{index + 1}{primary}.jpg");
            using var bitmap = new Bitmap(screen.Bounds.Width, screen.Bounds.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(screen.Bounds.Left, screen.Bounds.Top, 0, 0, screen.Bounds.Size);
            SaveCompressedJpeg(bitmap, path);
            paths.Add(path);
        }
        return paths;
    }

    private static void SaveCompressedJpeg(Image source, string path)
    {
        const int maxDimension = 1280;
        var scale = Math.Min(1d, (double)maxDimension / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, 0, 0, width, height);
        }
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 55L);
        resized.Save(path, codec, parameters);
    }

    private void ConvertExistingScreenshots()
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var oldPath in Directory.EnumerateFiles(screenshotRoot, "*.png"))
        {
            var newPath = Path.ChangeExtension(oldPath, ".jpg");
            try
            {
                using (var image = Image.FromFile(oldPath))
                    SaveCompressedJpeg(image, newPath);
                replacements[oldPath] = newPath;
                File.Delete(oldPath);
            }
            catch { }
        }

        if (replacements.Count == 0 || !File.Exists(logPath)) return;
        var updatedLines = new List<string>();
        foreach (var line in File.ReadLines(logPath))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<TimelineEntry>(line);
                if (entry is null) continue;
                var paths = entry.Screenshot.Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => replacements.TryGetValue(path, out var replacement) ? replacement : path);
                updatedLines.Add(JsonSerializer.Serialize(entry with { Screenshot = string.Join('|', paths) }));
            }
            catch { }
        }
        File.WriteAllLines(logPath, updatedLines, new UTF8Encoding(false));
    }

    private void CleanupOldData()
    {
        // Today plus the previous 13 calendar days gives a 14-day retained window.
        var cutoff = DateTime.Today.AddDays(-13);

        if (File.Exists(logPath))
        {
            var keptLines = new List<string>();
            foreach (var line in File.ReadLines(logPath))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<TimelineEntry>(line);
                    if (entry is not null && entry.Timestamp.Date >= cutoff) keptLines.Add(line);
                }
                catch { }
            }
            File.WriteAllLines(logPath, keptLines, new UTF8Encoding(false));
        }

        foreach (var pattern in new[] { "*.png", "*.jpg" })
            foreach (var path in Directory.EnumerateFiles(screenshotRoot, pattern))
                if (File.GetLastWriteTime(path).Date < cutoff) File.Delete(path);

        foreach (var path in Directory.EnumerateFiles(dataRoot, "Summary ????-??-??.txt"))
        {
            var dateText = Path.GetFileNameWithoutExtension(path).Replace("Summary ", "", StringComparison.Ordinal);
            if (DateTime.TryParseExact(dateText, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date) && date.Date < cutoff)
                File.Delete(path);
        }
    }

    private List<TimelineEntry> ReadEntries()
    {
        if (!File.Exists(logPath)) return [];
        var entries = new List<TimelineEntry>();
        foreach (var line in File.ReadLines(logPath))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<TimelineEntry>(line);
                if (entry is not null) entries.Add(entry);
            }
            catch { }
        }
        return entries;
    }

    private void RefreshPage()
    {
        var dayText = selectedDay.ToString("yyyy-MM-dd");
        var dayEntries = ReadEntries().Where(e => e.Day == dayText).OrderBy(e => e.Timestamp).ToList();
        dayHeading.Text = selectedDay.Date == DateTime.Today
            ? $"Today — {selectedDay:dddd d MMMM}"
            : selectedDay.ToString("dddd d MMMM yyyy");
        timeline.SetEntries(dayEntries);
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var entry in dayEntries.OrderByDescending(e => e.Timestamp))
        {
            var item = new ListViewItem(entry.Time);
            item.SubItems.Add(entry.Program);
            item.SubItems.Add(entry.FileName);
            item.SubItems.Add(entry.Note);
            item.Tag = entry.Screenshot;
            list.Items.Add(item);
        }
        list.EndUpdate();
    }

    private string ExportDailySummary(string day)
    {
        var entries = ReadEntries().Where(e => e.Day == day).OrderBy(e => e.Timestamp).ToList();
        var path = Path.Combine(dataRoot, $"Summary {day}.txt");
        var lines = new List<string> { $"Timeline summary - {day}", "", "Work summary" };
        if (entries.Count == 0) lines.Add("No activity recorded.");
        else
        {
            foreach (var group in entries.Where(e => e.Program != "Idle / uncertain").GroupBy(e => new { e.Program, e.FileName }).OrderByDescending(g => g.Count()))
            {
                var estimatedMinutes = group.Count() * 5;
                var duration = estimatedMinutes >= 60 ? $"{estimatedMinutes / 60d:0.#} hours" : $"{estimatedMinutes} minutes";
                lines.Add($"- {group.Key.Program} - {(string.IsNullOrWhiteSpace(group.Key.FileName) ? "(no document title)" : group.Key.FileName)} (about {duration})");
            }
            lines.Add("");
            lines.Add("Timeline");
            foreach (var entry in entries)
                lines.Add($"{entry.Time}  {entry.Program} - {entry.FileName}{(string.IsNullOrWhiteSpace(entry.Note) ? "" : " - " + entry.Note)}");
        }
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return path;
    }

    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(!args.Contains("--no-auto-capture", StringComparer.OrdinalIgnoreCase)));
    }
}
