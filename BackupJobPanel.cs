using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RobocopyWrapper;

public partial class BackupJobPanel : UserControl
{
    // ジョブ名
    public string JobName { get; set; } = "バックアップ1";

    // 公開プロパティ
    public string SourcePath { get => txtSource.Text; set => txtSource.Text = value; }
    public string DestPath { get => txtDest.Text; set => txtDest.Text = value; }
    public string Options { get => txtOptions.Text; set => txtOptions.Text = value; }
    public bool ScheduleEnabled { get => chkSchedule.Checked; set => chkSchedule.Checked = value; }
    public int ScheduleIntervalHours
    {
        get => (int)nudScheduleHours.Value;
        set => nudScheduleHours.Value = Math.Max(1, Math.Min(value, 24));
    }
    public DateTime LastRunTime
    {
        get => _lastRunTime;
        set => _lastRunTime = value;
    }
    public DateTime NextScheduledTime => _nextScheduledTime;
    public bool IsBusy => _runningProcess != null || _isVerifying;

    public int SplitterDistance
    {
        get => splitContainer.SplitterDistance;
        set { try { splitContainer.SplitterDistance = value; } catch { } }
    }
    public int InnerSplitterDistance
    {
        get => splitContainerInner.SplitterDistance;
        set { try { splitContainerInner.SplitterDistance = value; } catch { } }
    }

    // Form1 が設定する排他チェック関数
    public Func<BackupJobPanel, bool>? CanExecute { get; set; }

    // イベント
    public event EventHandler? SettingsChanged;
    public event EventHandler? ExecutionStarting;
    public event EventHandler<JobCompletedEventArgs>? ExecutionCompleted;
    public event EventHandler? ScheduleChanged;
    public event EventHandler? SplitterMoved;

    // ジョブ単位の状態
    private Process? _runningProcess;
    private bool _isPaused;
    private bool _wasKilled;
    private bool _isVerifying;
    private CancellationTokenSource? _verifyCts;
    private readonly ConcurrentQueue<string> _progressQueue = new();
    private readonly ConcurrentQueue<string> _copyResultQueue = new();
    private readonly ConcurrentQueue<string> _errorQueue = new();
    private int _errorCount;
    private int _copyCount;
    private int _skipCount;
    private int _extraCount;
    private int _lastReportedTotal;
    private System.Windows.Forms.Timer? _flushTimer;
    private DateTime _nextScheduledTime = DateTime.MaxValue;
    private DateTime _lastRunTime = DateTime.MinValue;
    private decimal _nudValueOnEnter;

    // Regex パターン
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

    private static readonly Regex RobocopyFileLinePattern = new(
        @"^\s*(New File|Newer|Older|Same|Changed|Modified|\*EXTRA File|\*EXTRA Dir|New Dir|Extra Dir|MISMATCH|FAILED|" +
        @"新しいファイル|新しいディレクトリ|新しい|古い|同じ|変更済み|更新済み)?\s+(\d+(?:\.\d+\s*[kmgt])?)\t(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CopyingPattern = new(
        @"(New File|New Dir|Newer|新しいファイル|新しいディレクトリ|新しい)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SkippedPattern = new(
        @"(same|older|skip|同じ|古い|スキップ)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExtraPattern = new(
        @"\*EXTRA",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PathPattern = new(
        @"([A-Za-z]:\\[^\r\n*?""<>|]+|\\\\[^\r\n*?""<>|]+)",
        RegexOptions.Compiled);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    public BackupJobPanel()
    {
        InitializeComponent();
        _nudValueOnEnter = nudScheduleHours.Value;

        // イベント接続
        btnBrowseSource.Click += BtnBrowseSource_Click;
        btnBrowseDest.Click += BtnBrowseDest_Click;
        btnExecute.Click += BtnExecute_Click;
        btnPause.Click += BtnPause_Click;
        btnStop.Click += BtnStop_Click;
        btnVerify.Click += BtnVerify_Click;
        btnVerifyStop.Click += BtnVerifyStop_Click;
        btnClearLog.Click += (_, _) => txtErrorLog.Clear();
        btnClearCopyResult.Click += (_, _) => txtCopyResult.Clear();

        txtSource.DragEnter += TxtPath_DragEnter;
        txtSource.DragDrop += TxtSource_DragDrop;
        txtDest.DragEnter += TxtPath_DragEnter;
        txtDest.DragDrop += TxtDest_DragDrop;

        txtSource.Leave += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        txtDest.Leave += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);

        chkSchedule.CheckedChanged += ChkSchedule_CheckedChanged;
        nudScheduleHours.ValueChanged += NudScheduleHours_ValueChanged;
        nudScheduleHours.Leave += NudScheduleHours_Leave;
        nudScheduleHours.Enter += (_, _) => _nudValueOnEnter = nudScheduleHours.Value;
        nudScheduleHours.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
                NudScheduleHours_Leave(s, EventArgs.Empty);
        };

        splitContainer.DoubleClick += SplitContainer_DoubleClick;
        splitContainerInner.DoubleClick += SplitContainer_DoubleClick;
        splitContainer.SplitterMoved += (_, _) => SplitterMoved?.Invoke(this, EventArgs.Empty);
        splitContainerInner.SplitterMoved += (_, _) => SplitterMoved?.Invoke(this, EventArgs.Empty);

        txtErrorLog.DoubleClick += (_, _) => OpenPathFromLogLine(txtErrorLog);
        txtCopyResult.DoubleClick += (_, _) => OpenPathFromLogLine(txtCopyResult);
    }

    /// <summary>スケジュール設定を初期化（LoadSettings後に呼ぶ）</summary>
    public void InitializeSchedule()
    {
        _nudValueOnEnter = nudScheduleHours.Value;
        nudScheduleHours.Enabled = chkSchedule.Checked;
        if (chkSchedule.Checked)
        {
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
            UpdateNextScheduleLabel();
        }
    }

    /// <summary>スケジュール時刻を現在時刻より先に進める（スリープ復帰時）</summary>
    public void AdvanceSchedulePastNow()
    {
        var interval = TimeSpan.FromHours((double)nudScheduleHours.Value);
        while (_nextScheduledTime <= DateTime.Now)
            _nextScheduledTime = _nextScheduledTime.Add(interval);
        UpdateNextScheduleLabel();
    }

    /// <summary>カウントダウン表示を更新</summary>
    public void UpdateNextScheduleLabel()
    {
        if (!chkSchedule.Checked || _nextScheduledTime == DateTime.MaxValue)
        {
            lblNextRun.Text = FormatLastRunTime();
            return;
        }

        var remaining = _nextScheduledTime - DateTime.Now;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        var hours = (int)remaining.TotalHours;
        var minutes = remaining.Minutes;
        var lastStr = FormatLastRunTime();
        var prefix = lastStr.Length > 0 ? lastStr + "  " : "";
        lblNextRun.Text = $"{prefix}次回: {_nextScheduledTime:HH:mm} (残り {hours:D2}:{minutes:D2})";
    }

    /// <summary>スケジュール実行を試行（Form1のタイマーから呼ばれる）</summary>
    public async Task<bool> TryScheduledExecuteAsync()
    {
        if (IsBusy) return false;

        var source = txtSource.Text.Trim().Trim('"');
        var dest = txtDest.Text.Trim().Trim('"');
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest))
        {
            AppendProgressLine($"[{DateTime.Now:HH:mm:ss}] スケジュール実行をスキップ (コピー元/先が未設定)");
            return false;
        }

        ExecutionStarting?.Invoke(this, EventArgs.Empty);
        await ExecuteRobocopyAsync();
        return true;
    }

    /// <summary>プロセス強制終了（フォーム終了時）</summary>
    public void ForceStop()
    {
        _verifyCts?.Cancel();
        if (_runningProcess != null && !_runningProcess.HasExited)
        {
            try
            {
                if (_isPaused) NtResumeProcess(_runningProcess.Handle);
                _wasKilled = true;
                _runningProcess.Kill();
            }
            catch { }
        }
    }

    public void StopFlushTimer()
    {
        if (_flushTimer != null)
        {
            _flushTimer.Stop();
            _flushTimer.Dispose();
            _flushTimer = null;
        }
        FlushBuffers();
    }

    public void AppendProgressLine(string line)
    {
        txtProgress.AppendText(line + Environment.NewLine);
    }

    #region Execute / Pause / Stop

    private async void BtnExecute_Click(object? sender, EventArgs e)
    {
        if (_runningProcess != null || _isVerifying)
        {
            MessageBox.Show("既に実行中です。完了までお待ちください。", "実行中",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (CanExecute != null && !CanExecute(this))
        {
            MessageBox.Show("別のタブで実行中です。完了までお待ちください。", "実行中",
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

        ExecutionStarting?.Invoke(this, EventArgs.Empty);
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
        int exitCode = -1;

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
                    if (!SkippedPattern.IsMatch(status) &&
                        !status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) &&
                        !status.Equals("MISMATCH", StringComparison.OrdinalIgnoreCase) &&
                        status.Length > 0)
                        _copyResultQueue.Enqueue(FormatRobocopyLine(args.Data, dest));

                    if (CopyingPattern.IsMatch(status))
                        Interlocked.Increment(ref _copyCount);
                    else if (SkippedPattern.IsMatch(status))
                        Interlocked.Increment(ref _skipCount);
                    else if (ExtraPattern.IsMatch(status))
                        Interlocked.Increment(ref _extraCount);

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

            await Task.Run(() => _runningProcess.WaitForExit());
            _runningProcess.WaitForExit();
            StopFlushTimer();

            exitCode = _runningProcess.ExitCode;

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

            if (!_wasKilled)
            {
                _lastRunTime = DateTime.Now;
                SettingsChanged?.Invoke(this, EventArgs.Empty);
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
            if (!IsDisposed)
            {
                StopFlushTimer();
                _runningProcess?.Dispose();
                _runningProcess = null;
                _isPaused = false;
                var wasKilled = _wasKilled;
                _wasKilled = false;
                SetRunningState(false);
                ExecutionCompleted?.Invoke(this, new JobCompletedEventArgs
                {
                    WasKilled = wasKilled,
                    ExitCode = exitCode,
                    ErrorCount = _errorCount,
                });
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
            _runningProcess.Kill();
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
    }

    #endregion

    #region Checksum Verification

    private async void BtnVerify_Click(object? sender, EventArgs e)
    {
        if (_runningProcess != null || _isVerifying) return;

        if (CanExecute != null && !CanExecute(this))
        {
            MessageBox.Show("別のタブで実行中です。完了までお待ちください。", "実行中",
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

        if (!Directory.Exists(source))
        {
            MessageBox.Show($"コピー元が見つかりません:\n{source}", "エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _isVerifying = true;
        SetRunningState(false);
        ExecutionStarting?.Invoke(this, EventArgs.Empty);

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
                ExecutionCompleted?.Invoke(this, new JobCompletedEventArgs { WasVerification = true });
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

        _progressQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] ファイルを列挙中...");

        var sourceFiles = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(source.Length + 1))
                .ToList();
        }, ct);

        _progressQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] ソース: {sourceFiles.Count:#,0} ファイル");

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

        _progressQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] デスト側の余剰ファイルを確認中...");

        if (Directory.Exists(dest))
        {
            var destOnlyFiles = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var sourceSet = new HashSet<string>(sourceFiles, StringComparer.OrdinalIgnoreCase);
                return Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories)
                    .Select(f => f.Substring(dest.Length + 1))
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
        using (var sha = SHA256.Create())
            return sha.ComputeHash(stream);
    }

    #endregion

    #region Schedule

    private void ChkSchedule_CheckedChanged(object? sender, EventArgs e)
    {
        nudScheduleHours.Enabled = chkSchedule.Checked;

        if (chkSchedule.Checked)
        {
            _nextScheduledTime = DateTime.Now.AddHours((double)nudScheduleHours.Value);
            UpdateNextScheduleLabel();
        }
        else
        {
            _nextScheduledTime = DateTime.MaxValue;
            UpdateNextScheduleLabel();
        }

        ScheduleChanged?.Invoke(this, EventArgs.Empty);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NudScheduleHours_ValueChanged(object? sender, EventArgs e)
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NudScheduleHours_Leave(object? sender, EventArgs e)
    {
        if (chkSchedule.Checked && nudScheduleHours.Value != _nudValueOnEnter)
        {
            _nextScheduledTime = DateTime.Now.AddHours((double)nudScheduleHours.Value);
            UpdateNextScheduleLabel();
        }
        _nudValueOnEnter = nudScheduleHours.Value;
    }

    private string FormatLastRunTime()
    {
        if (_lastRunTime == DateTime.MinValue) return "";
        return _lastRunTime.Date == DateTime.Today
            ? $"前回: {_lastRunTime:HH:mm}"
            : $"前回: {_lastRunTime:M/d HH:mm}";
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
            if (text != null && !string.IsNullOrWhiteSpace(text))
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

    #region Output buffering

    private static string FormatFileSize(string rawSize)
    {
        rawSize = rawSize.Trim();

        if (rawSize.Length > 1 && char.IsLetter(rawSize[rawSize.Length - 1]))
        {
            var unit = char.ToUpper(rawSize[rawSize.Length - 1]);
            var numPart = rawSize.Substring(0, rawSize.Length - 1).Trim();
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
        _flushTimer.Tick += (_, _) => FlushBuffers();
        _flushTimer.Start();
    }

    #endregion

    #region Misc

    private void SplitContainer_DoubleClick(object? sender, EventArgs e)
    {
        var totalHeight = splitContainer.Height;
        var splitterWidths = splitContainer.SplitterWidth + splitContainerInner.SplitterWidth;
        var panelHeight = (totalHeight - splitterWidths) / 3;

        splitContainer.SplitterDistance = Math.Max(panelHeight, splitContainer.Panel1MinSize);
        var innerHeight = splitContainerInner.Height;
        var innerPanel = (innerHeight - splitContainerInner.SplitterWidth) / 2;
        splitContainerInner.SplitterDistance = Math.Max(innerPanel, splitContainerInner.Panel1MinSize);
        SplitterMoved?.Invoke(this, EventArgs.Empty);
    }

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

    #endregion
}

public class JobCompletedEventArgs : EventArgs
{
    public bool WasKilled { get; set; }
    public int ExitCode { get; set; }
    public int ErrorCount { get; set; }
    public bool WasVerification { get; set; }
}
