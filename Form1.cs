using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobocopyWrapper;

public partial class Form1 : Form
{
    private static readonly string SettingsPath = Path.Combine(
        AppContext.BaseDirectory, "settings.json");

    private Process? _runningProcess;
    private bool _isPaused;

    // 出力バッファ (UIスレッドへの負荷軽減)
    private readonly ConcurrentQueue<string> _progressQueue = new();
    private readonly ConcurrentQueue<string> _copyResultQueue = new();
    private readonly ConcurrentQueue<string> _errorQueue = new();
    private System.Windows.Forms.Timer? _flushTimer;
    private int _errorCount;
    private int _copyCount;
    private int _skipCount;
    private int _extraCount;
    private int _lastReportedTotal;

    // タスクトレイ
    private bool _isExiting;
    private bool _trayBalloonShown;

    // スケジューラー
    private System.Windows.Forms.Timer? _schedulerTimer;
    private DateTime _nextScheduledTime = DateTime.MaxValue;
    private DateTime _lastRunTime = DateTime.MinValue;

    // 実行制御
    // Kill/中止された場合は前回実行時刻を更新しない
    private bool _wasKilled;
    // チェックサム検証
    private CancellationTokenSource? _verifyCts;
    private bool _isVerifying;
    // フォーカス取得時のnudScheduleHoursの値（値未変更のLeaveでリセットしないため）
    private decimal _nudValueOnEnter;

    // robocopyの出力からエラー行を判定するパターン
    private static readonly Regex ErrorLinePattern = new(
        @"(ERROR[\s:]|FAILED|エラー|^\s*\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2}\s+ERROR|" +
        @"Retry\s+limit\s+exceeded|The\s+process\s+cannot|Access\s+is\s+denied|" +
        @"Insufficient\s+disk\s+space|filename\s+or\s+extension\s+is\s+too\s+long|" +
        @"Sharing\s+violation|cannot\s+find\s+the\s+path|cannot\s+find\s+the\s+file|" +
        @"network\s+name\s+cannot\s+be\s+found|Logon\s+failure|" +
        @"Cannot\s+create\s+a\s+file\s+when\s+that\s+file\s+already\s+exists|" +
        @"ファイルが見つかりません|アクセスが拒否|パスが見つかりません|" +
        @"使用中のファイル|ネットワーク パスが見つかりません|" +
        @"ディスクに空き領域がありません|ファイル名または拡張子が長すぎます|" +
        @"共有違反|指定されたパスが見つかりません|指定されたファイルが見つかりません)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // robocopyのファイル/ディレクトリ操作行をパースするパターン
    private static readonly Regex RobocopyFileLinePattern = new(
        @"^\s*(New File|Newer|Older|Same|Changed|Modified|\*EXTRA File|\*EXTRA Dir|New Dir|Extra Dir|MISMATCH|FAILED|" +
        @"新しいファイル|新しいディレクトリ|新しい|古い|同じ|変更済み|更新済み)?\s+(\d+(?:\.\d+\s*[kmgt])?)\t(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 行の種別判定用パターン
    private static readonly Regex CopyingPattern = new(
        @"(New File|New Dir|Newer|新しいファイル|新しいディレクトリ|新しい)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SkippedPattern = new(
        @"(same|older|skip|同じ|古い|スキップ)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExtraPattern = new(
        @"\*EXTRA",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // NtSuspend/Resume用 (プロセス一時停止)

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    public Form1()
    {
        InitializeComponent();
        LoadSettings();
        // LoadSettings後の実際の値で初期化（Enter未経由のLeaveで誤リセットしないため）
        _nudValueOnEnter = nudScheduleHours.Value;

        // パス変更時は即時保存（トレイ格納中の強制終了でも設定を保持）
        txtSource.Leave += (_, _) => SaveSettings();
        txtDest.Leave += (_, _) => SaveSettings();

        // タスクトレイ設定（全初期化完了後にアイコン表示）
        notifyIcon.Icon = this.Icon ?? SystemIcons.Application;
        trayMenuShow.Click += TrayMenuShow_Click;
        trayMenuExit.Click += TrayMenuExit_Click;
        notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
        // バルーンチップをクリックしてもウィンドウを表示
        notifyIcon.BalloonTipClicked += (_, _) => ShowForm();
        notifyIcon.Visible = true;

        // スケジュールイベントはLoadSettings後に接続（読み込み時の誤発火防止）
        chkSchedule.CheckedChanged += ChkSchedule_CheckedChanged;
        nudScheduleHours.ValueChanged += NudScheduleHours_ValueChanged;
        nudScheduleHours.Leave += NudScheduleHours_Leave;
        // フォーカス取得時の値を記録（値未変更のLeaveでリセットしないため）
        nudScheduleHours.Enter += (_, _) => _nudValueOnEnter = nudScheduleHours.Value;
        // Enterキーでもカウントダウンをリセット（Leaveが発火しないため）
        nudScheduleHours.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
                NudScheduleHours_Leave(s, EventArgs.Empty);
        };

        // ロード済み設定でスケジューラーを初期化
        nudScheduleHours.Enabled = chkSchedule.Checked;
        if (chkSchedule.Checked)
        {
            // 前回実行時刻が記録されていれば、そこからn時間後を次回時刻に設定
            // 前回からn時間以上経過している場合はリセット（今からn時間後）
            // ※ 期限切れタスクを起動時に即時実行しない設計: バックグラウンドで
            //   意図せず大量コピーが始まるリスクを避けるため意図的にスキップ
            if (_lastRunTime != DateTime.MinValue)
            {
                var nextFromLast = _lastRunTime.AddHours((double)nudScheduleHours.Value);
                _nextScheduledTime = nextFromLast > DateTime.Now
                    ? nextFromLast
                    : DateTime.Now.AddHours((double)nudScheduleHours.Value);
            }
            else
            {
                _nextScheduledTime = DateTime.Now.AddHours((double)nudScheduleHours.Value);
            }
            StartSchedulerTimer();
            UpdateNextScheduleLabel();
            this.Text = "Robocopy Wrapper [スケジュール待機中]";
        }

        // スプリッターダブルクリックで3パネル均等化
        splitContainer.DoubleClick += SplitContainer_DoubleClick;
        splitContainerInner.DoubleClick += SplitContainer_DoubleClick;

        FormClosing += Form1_FormClosing;
    }

    #region Task Tray

    private void ShowForm()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void TrayMenuShow_Click(object? sender, EventArgs e) => ShowForm();

    private void TrayMenuExit_Click(object? sender, EventArgs e)
    {
        _isExiting = true;
        Application.Exit();
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e) => ShowForm();

    #endregion

    #region Scheduler

    private void StartSchedulerTimer()
    {
        if (_schedulerTimer != null) return;
        _schedulerTimer = new System.Windows.Forms.Timer { Interval = 30_000 }; // 30秒ごとにチェック
        _schedulerTimer.Tick += SchedulerTimer_Tick;
        _schedulerTimer.Start();
    }

    private void StopSchedulerTimer()
    {
        if (_schedulerTimer != null)
        {
            _schedulerTimer.Stop();
            _schedulerTimer.Dispose();
            _schedulerTimer = null;
        }
    }

    private async void SchedulerTimer_Tick(object? sender, EventArgs e)
    {
        // フォーム破棄直後にTickが発火してもUIアクセスしない
        if (IsDisposed) return;
        try
        {
            UpdateNextScheduleLabel();

            if (DateTime.Now < _nextScheduledTime) return;

            // スリープ復帰後に複数スロットが未消化でも1回だけ実行
            var interval = TimeSpan.FromHours((double)nudScheduleHours.Value);
            while (_nextScheduledTime <= DateTime.Now)
                _nextScheduledTime = _nextScheduledTime.Add(interval);
            UpdateNextScheduleLabel();

            if (_runningProcess != null || _isVerifying)
            {
                // 手動実行中/検証中 → スキップ（次回は上記で計算済みの時刻）
                var reason = _isVerifying ? "検証中" : "実行中";
                AppendProgressLine(
                    $"[{DateTime.Now:HH:mm:ss}] スケジュール実行をスキップ ({reason})");
                return;
            }

            var source = txtSource.Text.Trim().Trim('"');
            var dest = txtDest.Text.Trim().Trim('"');

            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest))
            {
                AppendProgressLine(
                    $"[{DateTime.Now:HH:mm:ss}] スケジュール実行をスキップ (コピー元/先が未設定)");
                return;
            }

            // フォームが非表示の場合は開始をバルーンチップで通知
            if (!this.Visible)
                notifyIcon.ShowBalloonTip(2000, "Robocopy Wrapper",
                    $"スケジュール実行を開始しました ({DateTime.Now:HH:mm})", ToolTipIcon.Info);

            await ExecuteRobocopyAsync();
        }
        catch (Exception ex)
        {
            // フォームが破棄済みの場合はコントロール操作を試みない
            if (IsDisposed) return;
            AppendProgressLine(
                $"[{DateTime.Now:HH:mm:ss}] スケジューラーエラー: {ex.Message}");
        }
    }

    private void UpdateNextScheduleLabel()
    {
        if (!chkSchedule.Checked || _nextScheduledTime == DateTime.MaxValue)
        {
            lblNextRun.Text = FormatLastRunTime();
            notifyIcon.Text = "Robocopy Wrapper";
            return;
        }

        var remaining = _nextScheduledTime - DateTime.Now;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        var hours = (int)remaining.TotalHours;
        var minutes = remaining.Minutes;
        var lastStr = FormatLastRunTime();
        var prefix = lastStr.Length > 0 ? lastStr + "  " : "";
        lblNextRun.Text = $"{prefix}次回: {_nextScheduledTime:HH:mm} (残り {hours:D2}:{minutes:D2})";

        var trayText = $"Robocopy Wrapper - 次回: {_nextScheduledTime:HH:mm}";
        notifyIcon.Text = trayText.Length <= 63 ? trayText : trayText[..63];
    }

    // 前回実行時刻を表示用にフォーマット（日をまたぐ場合は日付を付加）
    private string FormatLastRunTime()
    {
        if (_lastRunTime == DateTime.MinValue) return "";
        return _lastRunTime.Date == DateTime.Today
            ? $"前回: {_lastRunTime:HH:mm}"
            : $"前回: {_lastRunTime:M/d HH:mm}";
    }

    private void ChkSchedule_CheckedChanged(object? sender, EventArgs e)
    {
        nudScheduleHours.Enabled = chkSchedule.Checked;

        if (chkSchedule.Checked)
        {
            _nextScheduledTime = DateTime.Now.AddHours((double)nudScheduleHours.Value);
            StartSchedulerTimer();
            UpdateNextScheduleLabel();
            this.Text = "Robocopy Wrapper [スケジュール待機中]";
        }
        else
        {
            StopSchedulerTimer();
            _nextScheduledTime = DateTime.MaxValue;
            this.Text = "Robocopy Wrapper";
            UpdateNextScheduleLabel(); // lblNextRun と notifyIcon.Text を一元更新
        }

        SaveSettings();
    }

    // 値変更時は設定保存のみ
    private void NudScheduleHours_ValueChanged(object? sender, EventArgs e)
    {
        SaveSettings();
    }

    // フォーカスを外したとき、値が変わっていた場合のみカウントダウンをリセット
    private void NudScheduleHours_Leave(object? sender, EventArgs e)
    {
        if (chkSchedule.Checked && nudScheduleHours.Value != _nudValueOnEnter)
        {
            _nextScheduledTime = DateTime.Now.AddHours((double)nudScheduleHours.Value);
            UpdateNextScheduleLabel();
        }
        _nudValueOnEnter = nudScheduleHours.Value;
    }

    #endregion

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

    #region Progress log & output buffering

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

    /// <summary>
    /// robocopyのファイル操作行をフォーマット（操作結果・エラーログ用）
    /// </summary>
    private static string FormatRobocopyLine(string line, string? basePath = null)
    {
        var match = RobocopyFileLinePattern.Match(line);
        if (match.Success)
        {
            var status = match.Groups[1].Value.Trim();
            var size = FormatFileSize(match.Groups[2].Value);
            var path = match.Groups[3].Value.Trim();
            if (basePath != null)
                path = Path.Combine(basePath, path);
            return $"  {status}\t{size}\t{path}";
        }

        return line.Replace("\t", "  ");
    }

    private void AppendProgressLine(string line)
    {
        txtProgress.AppendText(line + Environment.NewLine);
    }

    private void FlushTimer_Tick(object? sender, EventArgs e) => FlushBuffers();

    private void FlushBuffers()
    {
        if (!_progressQueue.IsEmpty)
        {
            var sb = new StringBuilder();
            while (_progressQueue.TryDequeue(out var line))
                sb.AppendLine(line);

            if (sb.Length > 0)
                txtProgress.AppendText(sb.ToString());
        }

        if (!_copyResultQueue.IsEmpty)
        {
            var sb = new StringBuilder();
            while (_copyResultQueue.TryDequeue(out var line))
                sb.AppendLine(line);

            if (sb.Length > 0)
                txtCopyResult.AppendText(sb.ToString());
        }

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
        FlushBuffers();
    }

    #endregion

    #region Execute / Pause / Stop

    private async void BtnExecute_Click(object? sender, EventArgs e)
    {
        if (_runningProcess != null || _isVerifying)
        {
            MessageBox.Show("既に実行中です。完了までお待ちください。", "実行中",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var source = txtSource.Text.Trim().Trim('"');
        var dest = txtDest.Text.Trim().Trim('"');

        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest))
        {
            MessageBox.Show("コピー元とコピー先を指定してください。", "入力エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await ExecuteRobocopyAsync();
    }

    private async Task ExecuteRobocopyAsync()
    {
        _wasKilled = false;
        SetRunningState(true);
        txtProgress.Clear();
        _errorCount = 0;
        _copyCount = 0;
        _skipCount = 0;
        _extraCount = 0;
        _lastReportedTotal = 0;
        txtCopyResult.Clear();

        var source = txtSource.Text.Trim().Trim('"');
        var dest = txtDest.Text.Trim().Trim('"');
        var options = txtOptions.Text.Trim();

        var arguments = $"\"{source}\" \"{dest}\"";
        if (!string.IsNullOrEmpty(options))
            arguments += " " + options;

        AppendProgressLine($"[{DateTime.Now:HH:mm:ss}] robocopy {arguments}");
        AppendProgressLine(new string('─', 70));

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
                // ファイル操作行はステータス部分だけで判定（ファイル名誤検知防止）
                var fm = RobocopyFileLinePattern.Match(args.Data);
                if (fm.Success)
                {
                    var status = fm.Groups[1].Value;
                    if (status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("MISMATCH", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref _errorCount);
                        _errorQueue.Enqueue(FormatRobocopyLine(args.Data));
                    }
                    // 操作結果ログ（コピー・EXTRA等、スキップ以外の実操作行）フルパス付き
                    if (!SkippedPattern.IsMatch(status) &&
                        !status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) &&
                        !status.Equals("MISMATCH", StringComparison.OrdinalIgnoreCase) &&
                        status.Length > 0)
                        _copyResultQueue.Enqueue(FormatRobocopyLine(args.Data, dest));

                    // カテゴリ別カウント
                    if (CopyingPattern.IsMatch(status))
                        Interlocked.Increment(ref _copyCount);
                    else if (SkippedPattern.IsMatch(status))
                        Interlocked.Increment(ref _skipCount);
                    else if (ExtraPattern.IsMatch(status))
                        Interlocked.Increment(ref _extraCount);

                    // 100ファイルごとに進捗サマリーを表示
                    var total = _copyCount + _skipCount + _extraCount + _errorCount;
                    if (total >= _lastReportedTotal + 100)
                    {
                        _lastReportedTotal = total;
                        _progressQueue.Enqueue(
                            $"[{DateTime.Now:HH:mm:ss}] 処理中... コピー: {_copyCount:#,0}, スキップ: {_skipCount:#,0}, EXTRA: {_extraCount:#,0}, エラー: {_errorCount:#,0}");
                    }
                }
                else if (ErrorLinePattern.IsMatch(args.Data))
                {
                    Interlocked.Increment(ref _errorCount);
                    _errorQueue.Enqueue(args.Data);
                }
            };

            _runningProcess.ErrorDataReceived += (s, args) =>
            {
                if (args.Data == null) return;
                Interlocked.Increment(ref _errorCount);
                _progressQueue.Enqueue($"[STDERR] {args.Data}");
                _errorQueue.Enqueue("[STDERR] " + args.Data);
            };

            _runningProcess.Start();
            _runningProcess.BeginOutputReadLine();
            _runningProcess.BeginErrorReadLine();

            await _runningProcess.WaitForExitAsync();
            // 非同期出力コールバックの完了を保証（WaitForExitAsyncだけでは不十分）
            _runningProcess.WaitForExit();
            // 残バッファをすべてフラッシュしてから完了メッセージを表示
            StopFlushTimer();

            var exitCode = _runningProcess.ExitCode;

            if (exitCode >= 8)
            {
                var exitMsg = exitCode switch
                {
                    >= 16 => $"[致命的エラー] 終了コード: {exitCode} - 致命的なエラーが発生しました。",
                    >= 8 => $"[コピー失敗] 終了コード: {exitCode} - 一部のファイルのコピーに失敗しました。",
                    _ => $"[エラー] 終了コード: {exitCode}"
                };
                AppendProgressLine(exitMsg);
                txtErrorLog.AppendText(Environment.NewLine + exitMsg + Environment.NewLine);
            }

            // 最終カウントサマリー
            AppendProgressLine(
                $"[{DateTime.Now:HH:mm:ss}] コピー: {_copyCount:#,0}, スキップ: {_skipCount:#,0}, EXTRA: {_extraCount:#,0}, エラー: {_errorCount:#,0}");

            var summary = exitCode < 8
                ? $"完了 (終了コード: {exitCode}, エラー: {_errorCount}件)"
                : $"完了 (終了コード: {exitCode}, エラー: {_errorCount}件) ※エラーあり";

            var finishLine = $"── {DateTime.Now:yyyy/MM/dd HH:mm:ss} {summary} ──";
            AppendProgressLine(finishLine);
            if (txtCopyResult.TextLength > 0)
                txtCopyResult.AppendText(Environment.NewLine);
            txtCopyResult.AppendText(finishLine + Environment.NewLine);
            txtErrorLog.AppendText(Environment.NewLine + finishLine + Environment.NewLine);

            // 正常終了（中止でない）場合のみ前回実行時刻を更新
            if (!_wasKilled)
            {
                _lastRunTime = DateTime.Now;
                SaveSettings();
            }

            // フォームが非表示かつ中止でない場合は完了をバルーンチップで通知
            if (!this.Visible && !_wasKilled)
            {
                notifyIcon.ShowBalloonTip(3000, "Robocopy Wrapper",
                    exitCode < 8
                        ? $"バックアップが完了しました ({DateTime.Now:HH:mm})"
                        : $"エラーが発生しました (終了コード: {exitCode})",
                    exitCode < 8 ? ToolTipIcon.Info : ToolTipIcon.Error);
            }
        }
        catch (Exception ex)
        {
            var msg = $"[例外] {ex.Message}";
            AppendProgressLine(msg);
            txtErrorLog.AppendText(msg + Environment.NewLine);
        }
        finally
        {
            // フォーム破棄後にasync継続が実行された場合はコントロール操作を試みない
            if (!IsDisposed)
            {
                StopFlushTimer();
                _runningProcess?.Dispose();
                _runningProcess = null;
                _isPaused = false;
                _wasKilled = false;
                SetRunningState(false); // タイトル・Tooltip更新 + UpdateNextScheduleLabel を内部で呼ぶ
            }
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
                _progressQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] 再開しました");
            }
            else
            {
                NtSuspendProcess(_runningProcess.Handle);
                _isPaused = true;
                btnPause.Text = "再開";
                _progressQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] 一時停止しました");
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
            _wasKilled = true;
            _runningProcess.Kill(entireProcessTree: true);
            _progressQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] 中止しました");
            _errorQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] 中止しました");
        }
        catch (Exception ex)
        {
            _errorQueue.Enqueue($"[例外] 中止に失敗: {ex.Message}");
        }
    }

    private void SetRunningState(bool running)
    {
        btnExecute.Enabled = !running && !_isVerifying;
        btnExecute.Text = running ? "実行中..." : "実行";
        btnPause.Enabled = running;
        btnStop.Enabled = running;
        btnPause.Text = "一時停止";
        btnVerify.Enabled = !running && !_isVerifying;
        btnVerify.Visible = !_isVerifying;
        btnVerifyStop.Visible = _isVerifying;
        txtSource.ReadOnly = running || _isVerifying;
        txtDest.ReadOnly = running || _isVerifying;
        txtOptions.ReadOnly = running;
        chkSchedule.Enabled = !running && !_isVerifying;
        nudScheduleHours.Enabled = !running && !_isVerifying && chkSchedule.Checked;

        if (running)
        {
            this.Text = "Robocopy Wrapper [実行中]";
            notifyIcon.Text = "Robocopy Wrapper - 実行中...";
        }
        else
        {
            this.Text = chkSchedule.Checked
                ? "Robocopy Wrapper [スケジュール待機中]"
                : "Robocopy Wrapper";
            UpdateNextScheduleLabel(); // トレイTooltipも更新
        }
    }

    #endregion

    #region Checksum Verification

    private async void BtnVerify_Click(object? sender, EventArgs e)
    {
        if (_runningProcess != null || _isVerifying) return;

        var source = txtSource.Text.Trim().Trim('"');
        var dest = txtDest.Text.Trim().Trim('"');

        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest))
        {
            MessageBox.Show("コピー元とコピー先を指定してください。", "入力エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!Directory.Exists(source))
        {
            MessageBox.Show($"コピー元が見つかりません:\n{source}", "エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _isVerifying = true;
        SetRunningState(false); // UI更新（btnVerify/btnVerifyStop切替、btnExecute無効化）
        this.Text = "Robocopy Wrapper [検証中]";
        notifyIcon.Text = "Robocopy Wrapper - 検証中...";

        txtProgress.Clear();
        txtCopyResult.Clear();

        StartFlushTimer();
        _verifyCts = new CancellationTokenSource();

        try
        {
            await VerifyChecksumsAsync(source, dest, _verifyCts.Token);
        }
        catch (OperationCanceledException)
        {
            _progressQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] 検証を中止しました");
        }
        catch (Exception ex)
        {
            _progressQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] 検証エラー: {ex.Message}");
        }
        finally
        {
            StopFlushTimer();
            _verifyCts?.Dispose();
            _verifyCts = null;
            _isVerifying = false;
            if (!IsDisposed)
            {
                SetRunningState(false);
            }
        }
    }

    private void BtnVerifyStop_Click(object? sender, EventArgs e)
    {
        _verifyCts?.Cancel();
    }

    private async Task VerifyChecksumsAsync(string source, string dest, CancellationToken ct)
    {
        _progressQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] チェックサム検証開始: {source} ↔ {dest}");
        _progressQueue.Enqueue(new string('─', 70));

        var sw = Stopwatch.StartNew();
        var mismatchCount = 0;
        var missingInDestCount = 0;
        var missingInSourceCount = 0;
        var matchCount = 0;
        var errorCount = 0;

        // ソース側のファイル列挙
        _progressQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] ファイルを列挙中...");

        var sourceFiles = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Select(f => f[(source.Length + 1)..]) // 相対パス
                .ToList();
        }, ct);

        _progressQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] ソース: {sourceFiles.Count:#,0} ファイル");

        // バックグラウンドでハッシュ比較
        var total = sourceFiles.Count;
        var processed = 0;

        await Task.Run(() =>
        {
            foreach (var relPath in sourceFiles)
            {
                ct.ThrowIfCancellationRequested();

                var srcPath = Path.Combine(source, relPath);
                var dstPath = Path.Combine(dest, relPath);

                processed++;
                if (processed % 100 == 0 || processed == total)
                {
                    _progressQueue.Enqueue(
                        $"[{DateTime.Now:HH:mm:ss}] 検証中... {processed:#,0}/{total:#,0} ({100.0 * processed / total:F1}%)");
                }

                if (!File.Exists(dstPath))
                {
                    missingInDestCount++;
                    _copyResultQueue.Enqueue($"[デスト欠落] {relPath}");
                    continue;
                }

                try
                {
                    var srcHash = ComputeFileHash(srcPath);
                    ct.ThrowIfCancellationRequested();
                    var dstHash = ComputeFileHash(dstPath);

                    if (!srcHash.SequenceEqual(dstHash))
                    {
                        mismatchCount++;
                        _copyResultQueue.Enqueue($"[不一致] {relPath}");
                    }
                    else
                    {
                        matchCount++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errorCount++;
                    _copyResultQueue.Enqueue($"[エラー] {relPath}: {ex.Message}");
                }
            }
        }, ct);

        // デスト側にしかないファイルを検出
        _progressQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] デスト側の余剰ファイルを確認中...");

        if (Directory.Exists(dest))
        {
            var destOnlyFiles = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var sourceSet = new HashSet<string>(sourceFiles, StringComparer.OrdinalIgnoreCase);
                return Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories)
                    .Select(f => f[(dest.Length + 1)..])
                    .Where(rel => !sourceSet.Contains(rel))
                    .ToList();
            }, ct);

            foreach (var rel in destOnlyFiles)
            {
                missingInSourceCount++;
                _copyResultQueue.Enqueue($"[ソース欠落] {rel}");
            }
        }

        sw.Stop();
        var elapsed = sw.Elapsed;
        var summary = $"── {DateTime.Now:yyyy/MM/dd HH:mm:ss} 検証完了 " +
            $"(一致: {matchCount:#,0}, 不一致: {mismatchCount:#,0}, " +
            $"デスト欠落: {missingInDestCount:#,0}, ソース欠落: {missingInSourceCount:#,0}, " +
            $"エラー: {errorCount:#,0}, " +
            $"所要時間: {elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}) ──";

        _progressQueue.Enqueue(summary);

        if (mismatchCount + missingInDestCount + missingInSourceCount + errorCount == 0)
            _copyResultQueue.Enqueue("すべてのファイルが一致しました。");
    }

    private static byte[] ComputeFileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        return SHA256.HashData(stream);
    }

    #endregion

    private void BtnClearLog_Click(object? sender, EventArgs e) => txtErrorLog.Clear();
    private void BtnClearCopyResult_Click(object? sender, EventArgs e) => txtCopyResult.Clear();

    /// <summary>
    /// スプリッターをダブルクリックしたとき、3パネルの高さを均等にする
    /// </summary>
    private void SplitContainer_DoubleClick(object? sender, EventArgs e)
    {
        // 全体の高さからスプリッター幅2本分を引いて3等分
        var totalHeight = splitContainer.Height;
        var splitterWidths = splitContainer.SplitterWidth + splitContainerInner.SplitterWidth;
        var panelHeight = (totalHeight - splitterWidths) / 3;

        // 外側: Panel1 = 1/3, Panel2 = 2/3（内側のスプリッター幅含む）
        splitContainer.SplitterDistance = Math.Max(panelHeight, splitContainer.Panel1MinSize);
        // 内側: Panel1 = Panel2 = 均等
        var innerHeight = splitContainerInner.Height;
        var innerPanel = (innerHeight - splitContainerInner.SplitterWidth) / 2;
        splitContainerInner.SplitterDistance = Math.Max(innerPanel, splitContainerInner.Panel1MinSize);
        SaveSettings();
    }

    // ログからパスを抽出するパターン (ドライブレター or UNCパス)
    private static readonly Regex PathPattern = new(
        @"([A-Za-z]:\\[^\r\n*?""<>|]+|\\\\[^\r\n*?""<>|]+)",
        RegexOptions.Compiled);

    private void TxtErrorLog_DoubleClick(object? sender, EventArgs e) => OpenPathFromLogLine(txtErrorLog);
    private void TxtCopyResult_DoubleClick(object? sender, EventArgs e) => OpenPathFromLogLine(txtCopyResult);

    private void OpenPathFromLogLine(TextBox textBox)
    {
        var charIndex = textBox.GetCharIndexFromPosition(
            textBox.PointToClient(Cursor.Position));
        var lineIndex = textBox.GetLineFromCharIndex(charIndex);
        if (lineIndex < 0 || lineIndex >= textBox.Lines.Length) return;

        var line = textBox.Lines[lineIndex];
        var match = PathPattern.Match(line);
        if (!match.Success) return;

        var path = match.Value.TrimEnd(' ', '\t', '\\');

        try
        {
            if (File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
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

            chkSchedule.Checked = s.ScheduleEnabled;
            nudScheduleHours.Value = Math.Clamp(s.ScheduleIntervalHours, 1, 24);

            if (s.LastRunTime.HasValue)
                _lastRunTime = s.LastRunTime.Value;

            _trayBalloonShown = s.TrayBalloonShown;

            // スプリッター位置はLoad後にコントロールサイズが確定してから適用
            if (s.SplitterDistance > 0)
                this.Load += (_, _) => { try { splitContainer.SplitterDistance = s.SplitterDistance; } catch { } };
            if (s.InnerSplitterDistance > 0)
                this.Load += (_, _) => { try { splitContainerInner.SplitterDistance = s.InnerSplitterDistance; } catch { } };
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
                ScheduleEnabled = chkSchedule.Checked,
                ScheduleIntervalHours = (int)nudScheduleHours.Value,
                LastRunTime = _lastRunTime == DateTime.MinValue ? null : _lastRunTime,
                TrayBalloonShown = _trayBalloonShown,
                SplitterDistance = splitContainer.SplitterDistance,
                InnerSplitterDistance = splitContainerInner.SplitterDistance,
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
        // ×ボタンによるクローズ → タスクトレイに格納
        if (!_isExiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            // 初回のみバルーンチップでトレイ格納を案内（右クリック操作も含めて案内）
            if (!_trayBalloonShown)
            {
                notifyIcon.ShowBalloonTip(3000, "Robocopy Wrapper",
                    "タスクトレイに格納されました。ダブルクリックで再表示、右クリックで終了できます。",
                    ToolTipIcon.Info);
                _trayBalloonShown = true;
                SaveSettings();
            }
            return;
        }

        // 実際の終了処理
        if (_runningProcess != null && !_runningProcess.HasExited)
        {
            // シャットダウン時は確認なしで強制終了（ダイアログでブロックしない）
            if (e.CloseReason == CloseReason.WindowsShutDown)
            {
                try
                {
                    if (_isPaused) NtResumeProcess(_runningProcess.Handle);
                    _wasKilled = true;
                    _runningProcess.Kill(entireProcessTree: true);
                }
                catch { }
            }
            else
            {
                var result = MessageBox.Show("robocopyが実行中です。終了しますか？", "確認",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    _isExiting = false;
                    return;
                }
                try
                {
                    if (_isPaused) NtResumeProcess(_runningProcess.Handle);
                    _wasKilled = true;
                    _runningProcess.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }

        notifyIcon.Visible = false;
        StopSchedulerTimer();
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
        public bool ScheduleEnabled { get; set; }
        public int ScheduleIntervalHours { get; set; } = 1;
        public DateTime? LastRunTime { get; set; }
        public bool TrayBalloonShown { get; set; }
        public int SplitterDistance { get; set; }
        public int InnerSplitterDistance { get; set; }
    }

    #endregion
}
