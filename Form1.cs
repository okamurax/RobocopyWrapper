using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobocopyWrapper;

public partial class Form1 : Form
{
    private static readonly string SettingsPath = Path.Combine(
        AppContext.BaseDirectory, "settings.json");

    private const int MaxProgressLines = 5000;

    private Process? _runningProcess;
    private bool _isPaused;
    private int _progressLineCount;

    // 出力バッファ (UIスレッドへの負荷軽減)
    private readonly record struct LogEntry(string Line, Color? OverrideColor, bool IsError);
    private readonly ConcurrentQueue<LogEntry> _progressQueue = new();
    private readonly ConcurrentQueue<string> _errorQueue = new();
    private System.Windows.Forms.Timer? _flushTimer;
    private int _errorCount;

    // robocopyの出力からエラー行を判定するパターン
    private static readonly Regex ErrorLinePattern = new(
        @"(ERROR\s|FAILED|エラー|^\s*\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2}\s+ERROR|" +
        @"Retry\s+limit\s+exceeded|The\s+process\s+cannot|Access\s+is\s+denied|" +
        @"ファイルが見つかりません|アクセスが拒否|パスが見つかりません|" +
        @"使用中のファイル|ネットワーク パスが見つかりません)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // robocopyのファイル/ディレクトリ操作行をパースするパターン
    // サイズ: 整数のみ or 小数+単位(1.5 g等)。[kmgt]はドットの後のみ(G:\のGに誤マッチ防止)
    private static readonly Regex RobocopyFileLinePattern = new(
        @"^\s*(New File|Newer|Older|Same|Changed|Modified|\*EXTRA File|\*EXTRA Dir|New Dir|Extra Dir|MISMATCH|FAILED|" +
        @"新しいファイル|新しいディレクトリ|更新|同じ|変更済み)?\s+(\d+(?:\.\d+\s*[kmgt])?)\t(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 行の種別判定用パターン
    private static readonly Regex CopyingPattern = new(
        @"(New File|New Dir|Newer|新しいファイル|新しいディレクトリ|更新)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SkippedPattern = new(
        @"(same|older|skip|同じ|スキップ)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExtraPattern = new(
        @"\*EXTRA",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SummaryPattern = new(
        @"^\s*(Dirs|Files|Bytes|Times|Speed|ディレクトリ|ファイル|バイト|時刻|速度)\s*:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SeparatorPattern = new(
        @"^-{5,}$",
        RegexOptions.Compiled);

    // 色定義
    private static readonly Color ColorDefault = Color.FromArgb(180, 180, 180);
    private static readonly Color ColorCopying = Color.FromArgb(80, 220, 120);
    private static readonly Color ColorSkipped = Color.FromArgb(100, 100, 100);
    private static readonly Color ColorExtra = Color.FromArgb(220, 180, 50);
    private static readonly Color ColorError = Color.FromArgb(255, 80, 80);
    private static readonly Color ColorSummary = Color.FromArgb(100, 180, 255);
    private static readonly Color ColorSeparator = Color.FromArgb(60, 60, 80);
    private static readonly Color ColorInfo = Color.FromArgb(140, 140, 160);

    // Win32 API
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int wMsg, IntPtr wParam, IntPtr lParam);

    private const int WM_VSCROLL = 0x0115;
    private static readonly IntPtr SB_BOTTOM = new(7);

    // 行の高さを固定するためのPARAFORMAT2
    private const int EM_SETPARAFORMAT = 0x0447;
    private const uint PFM_LINESPACING = 0x00000100;
    private const int LineSpacingTwips = 240; // 12pt (twips: 1pt=20)

    [StructLayout(LayoutKind.Sequential)]
    private struct PARAFORMAT2
    {
        public int cbSize;
        public uint dwMask;
        public short wNumbering;
        public short wReserved;
        public int dxStartIndent;
        public int dxRightIndent;
        public int dxOffset;
        public short wAlignment;
        public short cTabCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public int[] rgxTabs;
        public int dySpaceBefore;
        public int dySpaceAfter;
        public int dyLineSpacing;
        public short sStyle;
        public byte bLineSpacingRule;
        public byte bOutlineLevel;
        public short wShadingWeight;
        public short wShadingStyle;
        public short wNumberingStart;
        public short wNumberingStyle;
        public short wNumberingTab;
        public short wBorderSpace;
        public short wBorderWidth;
        public short wBorders;
    }

    private void SetFixedLineSpacing(RichTextBox rtb)
    {
        // 空の状態でカーソル位置に書式を設定 → 以降の追記が継承する
        rtb.SelectionStart = 0;
        rtb.SelectionLength = 0;
        var pf = new PARAFORMAT2
        {
            rgxTabs = new int[32],
            dwMask = PFM_LINESPACING,
            dyLineSpacing = LineSpacingTwips,
            bLineSpacingRule = 4, // 固定行間
        };
        pf.cbSize = Marshal.SizeOf(pf);

        var ptr = Marshal.AllocHGlobal(pf.cbSize);
        try
        {
            Marshal.StructureToPtr(pf, ptr, false);
            SendMessage(rtb.Handle, EM_SETPARAFORMAT, IntPtr.Zero, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // RTFバッチ挿入: 複数行を1回のSelectedRtfで一括追加
    private string BuildRtfBatch(List<LogEntry> entries)
    {
        var colors = new List<Color>();
        var colorMap = new Dictionary<int, int>();
        var lines = new List<(string text, int ci)>();

        foreach (var e in entries)
        {
            var fmt = FormatRobocopyLine(e.Line);
            var col = e.OverrideColor ?? ClassifyLine(e.Line);
            var argb = col.ToArgb();
            if (!colorMap.TryGetValue(argb, out var ci))
            {
                ci = colors.Count + 1;
                colors.Add(col);
                colorMap[argb] = ci;
            }
            lines.Add((fmt, ci));
        }

        var fontName = rtbProgress.Font.Name;
        var fs = (int)(rtbProgress.Font.SizeInPoints * 2); // RTFは半ポイント単位

        var sb = new StringBuilder(entries.Count * 120);
        sb.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0 ").Append(fontName).Append(@";}}{\colortbl ;");
        foreach (var c in colors)
            sb.Append($@"\red{c.R}\green{c.G}\blue{c.B};");
        sb.Append('}');

        foreach (var (text, ci) in lines)
        {
            sb.Append($@"\pard\sl-{LineSpacingTwips}\slmult0\cf{ci}\f0\fs{fs} ");
            AppendRtfEscaped(sb, text);
            sb.Append(@"\par");
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendRtfEscaped(StringBuilder sb, string text)
    {
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                default:
                    if (ch > 127)
                    {
                        int code = ch;
                        if (code > 32767) code -= 65536; // RTFはsigned 16bit
                        sb.Append(@"\u").Append(code).Append('?');
                    }
                    else
                        sb.Append(ch);
                    break;
            }
        }
    }

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    public Form1()
    {
        InitializeComponent();
        LoadSettings();
        FormClosing += Form1_FormClosing;
    }

    #region Drag & Drop

    private void TxtPath_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data == null) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && paths.Length > 0 && Directory.Exists(paths[0]))
            {
                e.Effect = DragDropEffects.Copy;
                return;
            }
        }

        if (e.Data.GetDataPresent(DataFormats.Text))
        {
            e.Effect = DragDropEffects.Copy;
            return;
        }

        e.Effect = DragDropEffects.None;
    }

    private void TxtSource_DragDrop(object? sender, DragEventArgs e)
    {
        var path = GetDroppedPath(e);
        if (path != null) txtSource.Text = path;
    }

    private void TxtDest_DragDrop(object? sender, DragEventArgs e)
    {
        var path = GetDroppedPath(e);
        if (path != null) txtDest.Text = path;
    }

    private static string? GetDroppedPath(DragEventArgs e)
    {
        if (e.Data == null) return null;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && paths.Length > 0)
            {
                var p = paths[0];
                return Directory.Exists(p) ? p : Path.GetDirectoryName(p);
            }
        }

        if (e.Data.GetDataPresent(DataFormats.Text))
        {
            var text = e.Data.GetData(DataFormats.Text) as string;
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim().Trim('"');
        }

        return null;
    }

    #endregion

    #region Browse buttons

    private void BtnBrowseSource_Click(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog();
        var current = txtSource.Text.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dlg.SelectedPath = current;
        if (dlg.ShowDialog() == DialogResult.OK)
            txtSource.Text = dlg.SelectedPath;
    }

    private void BtnBrowseDest_Click(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog();
        var current = txtDest.Text.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dlg.SelectedPath = current;
        if (dlg.ShowDialog() == DialogResult.OK)
            txtDest.Text = dlg.SelectedPath;
    }

    #endregion

    #region Progress log (RichTextBox with color + formatting)

    private Color ClassifyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return ColorDefault;
        if (ErrorLinePattern.IsMatch(line))
            return ColorError;
        if (ExtraPattern.IsMatch(line))
            return ColorExtra;
        if (CopyingPattern.IsMatch(line))
            return ColorCopying;
        if (SkippedPattern.IsMatch(line))
            return ColorSkipped;
        if (SummaryPattern.IsMatch(line))
            return ColorSummary;
        if (SeparatorPattern.IsMatch(line))
            return ColorSeparator;
        return ColorDefault;
    }

    private static string FormatFileSize(string rawSize)
    {
        rawSize = rawSize.Trim();

        if (rawSize.Length > 1 && char.IsLetter(rawSize[^1]))
        {
            var unit = char.ToUpper(rawSize[^1]);
            var numPart = rawSize[..^1].Trim();
            if (double.TryParse(numPart, out var val))
            {
                return unit switch
                {
                    'K' => $"{val:F1} KB",
                    'M' => $"{val:F1} MB",
                    'G' => $"{val:F1} GB",
                    'T' => $"{val:F1} TB",
                    _ => rawSize
                };
            }
            return rawSize;
        }

        if (long.TryParse(rawSize.Replace(",", "").Replace(".", ""), out var bytes))
        {
            return bytes switch
            {
                < 1024L => $"{bytes} B",
                < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
                < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
                < 1024L * 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
                _ => $"{bytes / (1024.0 * 1024 * 1024 * 1024):F1} TB",
            };
        }

        return rawSize;
    }

    private static string FormatRobocopyLine(string line)
    {
        var match = RobocopyFileLinePattern.Match(line);
        if (match.Success)
        {
            var status = match.Groups[1].Value.Trim();
            var size = FormatFileSize(match.Groups[2].Value);
            var path = match.Groups[3].Value.Trim();

            if (string.IsNullOrEmpty(status))
                status = "";

            return $"  {status,-14} {size,10}  {path}";
        }

        // マッチしない行もタブを統一的に置換して列ズレを防ぐ
        return line.Replace("\t", "  ");
    }

    private void AppendProgressLineDirect(string line, Color? overrideColor = null)
    {
        var formatted = FormatRobocopyLine(line);
        var color = overrideColor ?? ClassifyLine(line);

        rtbProgress.SelectionStart = rtbProgress.TextLength;
        rtbProgress.SelectionLength = 0;
        rtbProgress.SelectionColor = color;
        rtbProgress.SelectionFont = rtbProgress.Font; // 行の高さを統一
        rtbProgress.AppendText(formatted + Environment.NewLine);
    }

    private void ScrollProgressToBottom()
    {
        SendMessage(rtbProgress.Handle, WM_VSCROLL, SB_BOTTOM, IntPtr.Zero);
    }

    private void TrimProgressIfNeeded()
    {
        if (_progressLineCount > MaxProgressLines)
        {
            var cutIndex = rtbProgress.GetFirstCharIndexFromLine(MaxProgressLines / 4);
            if (cutIndex > 0)
            {
                rtbProgress.Select(0, cutIndex);
                rtbProgress.ReadOnly = false;
                rtbProgress.SelectedText = "";
                rtbProgress.ReadOnly = true;
                _progressLineCount -= MaxProgressLines / 4;
            }
        }
    }

    // タイマーで定期的にバッファをフラッシュ
    private void FlushTimer_Tick(object? sender, EventArgs e)
    {
        FlushBuffers();
    }

    private void FlushBuffers()
    {
        // 進捗ログのフラッシュ (RTF一括挿入)
        if (!_progressQueue.IsEmpty)
        {
            var entries = new List<LogEntry>();
            while (_progressQueue.TryDequeue(out var entry))
                entries.Add(entry);

            if (entries.Count > 0)
            {
                var rtf = BuildRtfBatch(entries);
                rtbProgress.SelectionStart = rtbProgress.TextLength;
                rtbProgress.SelectionLength = 0;
                rtbProgress.SelectedRtf = rtf;
                _progressLineCount += entries.Count;
                TrimProgressIfNeeded();
                ScrollProgressToBottom();
            }
        }

        // エラーログのフラッシュ
        if (!_errorQueue.IsEmpty)
        {
            var sb = new StringBuilder();
            while (_errorQueue.TryDequeue(out var line))
                sb.AppendLine(line);

            if (sb.Length > 0)
                txtErrorLog.AppendText(sb.ToString());
        }
    }

    private void StartFlushTimer()
    {
        _flushTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _flushTimer.Tick += FlushTimer_Tick;
        _flushTimer.Start();
    }

    private void StopFlushTimer()
    {
        if (_flushTimer != null)
        {
            _flushTimer.Stop();
            _flushTimer.Dispose();
            _flushTimer = null;
        }
        // 残りのバッファをフラッシュ
        FlushBuffers();
    }

    #endregion

    #region Execute / Pause / Stop

    private async void BtnExecute_Click(object? sender, EventArgs e)
    {
        if (_runningProcess != null)
        {
            MessageBox.Show("既に実行中です。完了までお待ちください。", "実行中",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var source = txtSource.Text.Trim().Trim('"');
        var dest = txtDest.Text.Trim().Trim('"');
        var options = txtOptions.Text.Trim();

        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest))
        {
            MessageBox.Show("コピー元とコピー先を指定してください。", "入力エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetRunningState(true);
        rtbProgress.Clear();
        SetFixedLineSpacing(rtbProgress);
        _progressLineCount = 0;
        _errorCount = 0;

        var arguments = $"\"{source}\" \"{dest}\"";
        if (!string.IsNullOrEmpty(options))
            arguments += " " + options;

        AppendProgressLineDirect($"[{DateTime.Now:HH:mm:ss}] robocopy {arguments}", ColorInfo);
        AppendProgressLineDirect(new string('─', 70), ColorSeparator);
        ScrollProgressToBottom();

        StartFlushTimer();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "robocopy",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(932),
                StandardErrorEncoding = Encoding.GetEncoding(932),
            };

            _runningProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

            _runningProcess.OutputDataReceived += (s, args) =>
            {
                if (args.Data == null) return;

                // バッファに追加 (UIスレッドを使わない)
                _progressQueue.Enqueue(new LogEntry(args.Data, null, false));

                if (ErrorLinePattern.IsMatch(args.Data))
                {
                    Interlocked.Increment(ref _errorCount);
                    _errorQueue.Enqueue(args.Data);
                }
            };

            _runningProcess.ErrorDataReceived += (s, args) =>
            {
                if (args.Data == null) return;
                Interlocked.Increment(ref _errorCount);
                _progressQueue.Enqueue(new LogEntry("[STDERR] " + args.Data, ColorError, true));
                _errorQueue.Enqueue("[STDERR] " + args.Data);
            };

            _runningProcess.Start();
            _runningProcess.BeginOutputReadLine();
            _runningProcess.BeginErrorReadLine();

            await _runningProcess.WaitForExitAsync();

            var exitCode = _runningProcess.ExitCode;

            StopFlushTimer();

            if (exitCode >= 8)
            {
                var exitMsg = exitCode switch
                {
                    >= 16 => $"[致命的エラー] 終了コード: {exitCode} - 致命的なエラーが発生しました。",
                    >= 8 => $"[コピー失敗] 終了コード: {exitCode} - 一部のファイルのコピーに失敗しました。",
                    _ => $"[エラー] 終了コード: {exitCode}"
                };
                AppendProgressLineDirect(exitMsg, ColorError);
                txtErrorLog.AppendText(Environment.NewLine + exitMsg + Environment.NewLine);
            }

            var summary = exitCode < 8
                ? $"完了 (終了コード: {exitCode}, エラー: {_errorCount}件)"
                : $"完了 (終了コード: {exitCode}, エラー: {_errorCount}件) ※エラーあり";

            var finishLine = $"── {DateTime.Now:yyyy/MM/dd HH:mm:ss} {summary} ──";
            AppendProgressLineDirect(finishLine, exitCode < 8 ? ColorCopying : ColorError);
            ScrollProgressToBottom();
            txtErrorLog.AppendText(Environment.NewLine + finishLine + Environment.NewLine);
        }
        catch (Exception ex)
        {
            StopFlushTimer();
            var msg = $"[例外] {ex.Message}";
            AppendProgressLineDirect(msg, ColorError);
            ScrollProgressToBottom();
            txtErrorLog.AppendText(msg + Environment.NewLine);
        }
        finally
        {
            StopFlushTimer();
            _runningProcess?.Dispose();
            _runningProcess = null;
            _isPaused = false;
            SetRunningState(false);
        }
    }

    private void BtnPause_Click(object? sender, EventArgs e)
    {
        if (_runningProcess == null || _runningProcess.HasExited) return;

        try
        {
            if (_isPaused)
            {
                NtResumeProcess(_runningProcess.Handle);
                _isPaused = false;
                btnPause.Text = "一時停止";
                _progressQueue.Enqueue(new LogEntry($"[{DateTime.Now:HH:mm:ss}] 再開しました", ColorInfo, false));
            }
            else
            {
                NtSuspendProcess(_runningProcess.Handle);
                _isPaused = true;
                btnPause.Text = "再開";
                _progressQueue.Enqueue(new LogEntry($"[{DateTime.Now:HH:mm:ss}] 一時停止しました", ColorInfo, false));
            }
        }
        catch (Exception ex)
        {
            _errorQueue.Enqueue($"[例外] 一時停止/再開に失敗: {ex.Message}");
        }
    }

    private void BtnStop_Click(object? sender, EventArgs e)
    {
        if (_runningProcess == null || _runningProcess.HasExited) return;

        var result = MessageBox.Show("実行中のrobocopyを中止しますか？", "中止確認",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes) return;

        try
        {
            if (_isPaused)
            {
                NtResumeProcess(_runningProcess.Handle);
                _isPaused = false;
            }
            _runningProcess.Kill(entireProcessTree: true);
            _progressQueue.Enqueue(new LogEntry($"[{DateTime.Now:HH:mm:ss}] 中止しました", ColorError, false));
            _errorQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] 中止しました");
        }
        catch (Exception ex)
        {
            _errorQueue.Enqueue($"[例外] 中止に失敗: {ex.Message}");
        }
    }

    private void SetRunningState(bool running)
    {
        btnExecute.Enabled = !running;
        btnExecute.Text = running ? "実行中..." : "実行";
        btnPause.Enabled = running;
        btnStop.Enabled = running;
        btnPause.Text = "一時停止";
        txtSource.ReadOnly = running;
        txtDest.ReadOnly = running;
        txtOptions.ReadOnly = running;
    }

    #endregion

    private void BtnClearLog_Click(object? sender, EventArgs e)
    {
        txtErrorLog.Clear();
    }

    // エラーログからパスを抽出するパターン (ドライブレター or UNCパス)
    private static readonly Regex PathPattern = new(
        @"([A-Za-z]:\\[^\r\n*?""<>|]+|\\\\[^\r\n*?""<>|]+)",
        RegexOptions.Compiled);

    private void TxtErrorLog_DoubleClick(object? sender, EventArgs e)
    {
        var charIndex = txtErrorLog.GetCharIndexFromPosition(
            txtErrorLog.PointToClient(Cursor.Position));
        var lineIndex = txtErrorLog.GetLineFromCharIndex(charIndex);
        if (lineIndex < 0 || lineIndex >= txtErrorLog.Lines.Length) return;

        var line = txtErrorLog.Lines[lineIndex];
        var match = PathPattern.Match(line);
        if (!match.Success) return;

        var path = match.Value.TrimEnd(' ', '\t', '\\');

        try
        {
            if (File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (Directory.Exists(path))
            {
                Process.Start("explorer.exe", $"\"{path}\"");
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir))
                    Process.Start("explorer.exe", $"\"{dir}\"");
                else
                    MessageBox.Show($"パスが見つかりません:\n{path}", "エラー",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エクスプローラの起動に失敗:\n{ex.Message}", "エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    #region Settings persistence

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<AppSettings>(json);
            if (s == null) return;

            if (s.Width > 0 && s.Height > 0)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(s.X, s.Y);
                Size = new Size(s.Width, s.Height);

                var screen = Screen.FromPoint(Location);
                if (!screen.WorkingArea.IntersectsWith(Bounds))
                {
                    StartPosition = FormStartPosition.WindowsDefaultLocation;
                    Size = new Size(800, 500);
                }
            }

            if (s.WindowState == "Maximized")
                WindowState = FormWindowState.Maximized;

            txtSource.Text = s.Source ?? "";
            txtDest.Text = s.Dest ?? "";
            txtOptions.Text = s.Options ?? "";
        }
        catch
        {
        }
    }

    private void SaveSettings()
    {
        try
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var s = new AppSettings
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                WindowState = WindowState == FormWindowState.Maximized ? "Maximized" : "Normal",
                Source = txtSource.Text,
                Dest = txtDest.Text,
                Options = txtOptions.Text,
            };
            var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
        }
    }

    private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_runningProcess != null && !_runningProcess.HasExited)
        {
            var result = MessageBox.Show("robocopyが実行中です。終了しますか？", "確認",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            try
            {
                if (_isPaused) NtResumeProcess(_runningProcess.Handle);
                _runningProcess.Kill(entireProcessTree: true);
            }
            catch { }
        }
        StopFlushTimer();
        SaveSettings();
    }

    private class AppSettings
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string WindowState { get; set; } = "Normal";
        public string? Source { get; set; }
        public string? Dest { get; set; }
        public string? Options { get; set; }
    }

    #endregion
}
