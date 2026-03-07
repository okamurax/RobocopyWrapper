using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RobocopyWrapper;

public partial class Form1 : Form
{
    private static readonly string SettingsPath = Path.Combine(
        AppContext.BaseDirectory, "settings.json");

    private bool _isExiting;
    private bool _trayBalloonShown;
    private System.Windows.Forms.Timer? _schedulerTimer;
    private TabPage? _rightClickedTab;

    public Form1()
    {
        InitializeComponent();
        LoadSettings();

        // スタートアップ登録状態をレジストリから復元
        chkStartup.Checked = IsStartupRegistered();
        chkStartup.CheckedChanged += ChkStartup_CheckedChanged;

        // タスクトレイ設定
        notifyIcon.Icon = this.Icon ?? SystemIcons.Application;
        trayMenuShow.Click += (_, _) => ShowForm();
        trayMenuExit.Click += TrayMenuExit_Click;
        notifyIcon.DoubleClick += (_, _) => ShowForm();
        notifyIcon.BalloonTipClicked += (_, _) => ShowForm();
        notifyIcon.Visible = true;

        // タブ管理
        btnAddTab.Click += BtnAddTab_Click;
        btnRemoveTab.Click += BtnRemoveTab_Click;
        tabControl.MouseClick += TabControl_MouseClick;
        tabMenuRename.Click += TabMenuRename_Click;
        tabMenuDelete.Click += TabMenuDelete_Click;

        // タブが0なら初期タブを追加
        if (tabControl.TabCount == 0)
            AddNewTab("バックアップ1");

        // スケジューラー開始（いずれかのタブでスケジュールが有効なら）
        if (GetAllPanels().Any(p => p.ScheduleEnabled))
            StartSchedulerTimer();

        UpdateTitleAndTray();
        FormClosing += Form1_FormClosing;
    }

    #region Tab Management

    private BackupJobPanel AddNewTab(string name, JobSettings? settings = null)
    {
        var panel = new BackupJobPanel
        {
            Dock = DockStyle.Fill,
            JobName = name,
        };

        if (settings != null)
        {
            panel.SourcePath = settings.Source ?? "";
            panel.DestPath = settings.Dest ?? "";
            panel.Options = settings.Options ?? "";
            panel.ScheduleEnabled = settings.ScheduleEnabled;
            panel.ScheduleIntervalHours = settings.ScheduleIntervalHours;
            if (settings.LastRunTime.HasValue)
                panel.LastRunTime = settings.LastRunTime.Value;
            panel.InitializeSchedule();
        }

        // パス競合チェック設定
        panel.CanExecute = (requestor) => CheckPathConflict(requestor);

        // イベント接続
        panel.SettingsChanged += (_, _) => SaveSettings();
        panel.ExecutionStarting += (_, _) => UpdateTitleAndTray();
        panel.ExecutionCompleted += Panel_ExecutionCompleted;
        panel.ScheduleChanged += Panel_ScheduleChanged;
        panel.SplitterMoved += (_, _) => SaveSettings();

        var tabPage = new TabPage(name) { Tag = panel };
        tabPage.Controls.Add(panel);
        tabControl.TabPages.Add(tabPage);

        UpdateRemoveButtonState();
        return panel;
    }

    private void BtnAddTab_Click(object? sender, EventArgs e)
    {
        if (tabControl.TabCount >= 10)
        {
            MessageBox.Show("タブの上限(10)に達しています。", "上限",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var name = GenerateTabName();
        var panel = AddNewTab(name);
        tabControl.SelectedIndex = tabControl.TabCount - 1;
        // 新規タブはスプリッタを均等化
        panel.EqualizeSplitters();
        SaveSettings();
    }

    private void BtnRemoveTab_Click(object? sender, EventArgs e)
    {
        RemoveTab(tabControl.SelectedIndex);
    }

    private void RemoveTab(int index)
    {
        if (tabControl.TabCount <= 1)
        {
            MessageBox.Show("最低1つのタブが必要です。", "削除不可",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (index < 0 || index >= tabControl.TabCount) return;

        var panel = GetPanel(index);
        if (panel != null && panel.IsBusy)
        {
            var result = MessageBox.Show("このタブのジョブは実行中です。中止してタブを削除しますか？",
                "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;
            panel.ForceStop();
        }

        var tabPage = tabControl.TabPages[index];
        tabControl.TabPages.RemoveAt(index);
        panel?.Dispose();
        tabPage.Dispose();
        UpdateRemoveButtonState();
        SaveSettings();
    }

    private void TabControl_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        for (int i = 0; i < tabControl.TabCount; i++)
        {
            if (tabControl.GetTabRect(i).Contains(e.Location))
            {
                _rightClickedTab = tabControl.TabPages[i];
                tabContextMenu.Show(tabControl, e.Location);
                break;
            }
        }
    }

    private void TabMenuRename_Click(object? sender, EventArgs e)
    {
        if (_rightClickedTab == null || !tabControl.TabPages.Contains(_rightClickedTab)) return;

        var panel = _rightClickedTab.Tag as BackupJobPanel;
        if (panel == null) return;

        var currentName = panel.JobName;
        var input = ShowInputDialog("タブ名の変更", "新しい名前:", currentName);
        if (input != null && input.Trim().Length > 0)
        {
            var newName = input.Trim();
            panel.JobName = newName;
            _rightClickedTab.Text = newName;
            SaveSettings();
        }
    }

    private void TabMenuDelete_Click(object? sender, EventArgs e)
    {
        if (_rightClickedTab == null || !tabControl.TabPages.Contains(_rightClickedTab)) return;
        RemoveTab(tabControl.TabPages.IndexOf(_rightClickedTab));
    }

    private void UpdateRemoveButtonState()
    {
        btnRemoveTab.Enabled = tabControl.TabCount > 1;
    }

    private string GenerateTabName()
    {
        var existing = GetAllPanels().Select(p => p.JobName).ToHashSet();
        for (int i = 1; ; i++)
        {
            var name = $"バックアップ{i}";
            if (!existing.Contains(name)) return name;
        }
    }

    private IEnumerable<BackupJobPanel> GetAllPanels()
    {
        foreach (TabPage page in tabControl.TabPages)
        {
            if (page.Tag is BackupJobPanel panel)
                yield return panel;
        }
    }

    private BackupJobPanel? GetPanel(int index)
    {
        if (index < 0 || index >= tabControl.TabCount) return null;
        return tabControl.TabPages[index].Tag as BackupJobPanel;
    }

    /// <summary>実行中ジョブとのパス競合をチェック（null=OK、文字列=競合理由）</summary>
    /// <remarks>
    /// コピー元同士の重複は読み取りのみなので許可。
    /// コピー先が絡む重複（先vs先、先vs元、元vs先）のみブロック。
    /// </remarks>
    private string? CheckPathConflict(BackupJobPanel requestor)
    {
        var reqSource = requestor.SourcePath.Trim().Trim('"');
        var reqDest = requestor.DestPath.Trim().Trim('"');

        foreach (var other in GetAllPanels())
        {
            if (other == requestor || !other.IsBusy) continue;

            var otherSource = other.SourcePath.Trim().Trim('"');
            var otherDest = other.DestPath.Trim().Trim('"');

            // コピー先 vs コピー先
            if (!string.IsNullOrWhiteSpace(reqDest) && !string.IsNullOrWhiteSpace(otherDest)
                && PathsOverlap(reqDest, otherDest))
                return $"コピー先が競合しています:\n  {reqDest}\n  ↔ {otherDest}\n({other.JobName} が実行中)";

            // 自分のコピー先 vs 相手のコピー元
            if (!string.IsNullOrWhiteSpace(reqDest) && !string.IsNullOrWhiteSpace(otherSource)
                && PathsOverlap(reqDest, otherSource))
                return $"コピー先が他ジョブのコピー元と競合しています:\n  {reqDest}\n  ↔ {otherSource}\n({other.JobName} が実行中)";

            // 自分のコピー元 vs 相手のコピー先
            if (!string.IsNullOrWhiteSpace(reqSource) && !string.IsNullOrWhiteSpace(otherDest)
                && PathsOverlap(reqSource, otherDest))
                return $"コピー元が他ジョブのコピー先と競合しています:\n  {reqSource}\n  ↔ {otherDest}\n({other.JobName} が実行中)";
        }
        return null;
    }

    /// <summary>2つのパスが同一または親子関係にあるか判定</summary>
    private static bool PathsOverlap(string path1, string path2)
    {
        try
        {
            var p1 = Path.GetFullPath(path1).TrimEnd('\\') + "\\";
            var p2 = Path.GetFullPath(path2).TrimEnd('\\') + "\\";
            return p1.StartsWith(p2, StringComparison.OrdinalIgnoreCase) ||
                   p2.StartsWith(p1, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 無効なパスの場合は文字列完全一致で判定
            return string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? ShowInputDialog(string title, string prompt, string defaultValue)
    {
        using var form = new Form
        {
            Text = title,
            Size = new Size(350, 150),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
        };

        var lbl = new Label { Text = prompt, Location = new Point(12, 15), AutoSize = true };
        var txt = new TextBox { Text = defaultValue, Location = new Point(12, 40), Size = new Size(310, 23) };
        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(166, 75), Size = new Size(75, 25) };
        var btnCancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Location = new Point(247, 75), Size = new Size(75, 25) };

        form.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        return form.ShowDialog() == DialogResult.OK ? txt.Text : null;
    }

    #endregion

    #region Event Handlers (from panels)

    private void Panel_ExecutionCompleted(object? sender, JobCompletedEventArgs args)
    {
        if (sender is not BackupJobPanel panel) return;

        if (!this.Visible && !args.WasKilled)
        {
            var icon = args.ExitCode < 8 ? ToolTipIcon.Info : ToolTipIcon.Error;
            string msg;
            if (args.WasVerification)
                msg = $"{panel.JobName}: 検証完了 ({DateTime.Now:HH:mm})";
            else
                msg = args.ExitCode < 8
                    ? $"{panel.JobName}: バックアップ完了 ({DateTime.Now:HH:mm})"
                    : $"{panel.JobName}: エラー発生 (終了コード: {args.ExitCode})";

            notifyIcon.ShowBalloonTip(3000, "Robocopy Wrapper", msg, icon);
        }

        UpdateTitleAndTray();
        SaveSettings();
    }

    private void Panel_ScheduleChanged(object? sender, EventArgs e)
    {
        var anyScheduled = GetAllPanels().Any(p => p.ScheduleEnabled);
        if (anyScheduled)
            StartSchedulerTimer();
        else
            StopSchedulerTimer();
        UpdateTitleAndTray();
    }

    #endregion

    #region Task Tray

    private void ShowForm()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void TrayMenuExit_Click(object? sender, EventArgs e)
    {
        _isExiting = true;
        Application.Exit();
    }

    #endregion

    #region Scheduler

    private void StartSchedulerTimer()
    {
        if (_schedulerTimer != null) return;
        _schedulerTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
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

    private bool _schedulerRunning;

    private async void SchedulerTimer_Tick(object? sender, EventArgs e)
    {
        if (IsDisposed || _schedulerRunning) return;
        _schedulerRunning = true;
        try
        {
            var panels = GetAllPanels().ToList();

            // 全タブのカウントダウン更新
            foreach (var panel in panels)
                panel.UpdateNextScheduleLabel();

            // スケジュール実行チェック
            foreach (var panel in panels)
            {
                if (!panel.ScheduleEnabled) continue;
                if (panel.NextScheduledTime > DateTime.Now) continue;

                panel.AdvanceSchedulePastNow();

                var conflict = CheckPathConflict(panel);
                if (conflict != null)
                {
                    panel.AppendProgressLine(
                        $"[{DateTime.Now:HH:mm:ss}] スケジュール実行をスキップ (パス競合: 別ジョブ実行中)");
                    continue;
                }

                // フォームが非表示の場合は開始をバルーンチップで通知
                if (!this.Visible)
                    notifyIcon.ShowBalloonTip(2000, "Robocopy Wrapper",
                        $"{panel.JobName}: スケジュール実行を開始 ({DateTime.Now:HH:mm})", ToolTipIcon.Info);

                await panel.TryScheduledExecuteAsync();
            }

            UpdateTitleAndTray();
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            // エラーを表示可能なパネルに書く
            var activePanel = GetPanel(tabControl.SelectedIndex);
            activePanel?.AppendProgressLine(
                $"[{DateTime.Now:HH:mm:ss}] スケジューラーエラー: {ex.Message}");
        }
        finally
        {
            _schedulerRunning = false;
        }
    }

    #endregion

    #region Title & Tray

    private void UpdateTitleAndTray()
    {
        var panels = GetAllPanels().ToList();
        var running = panels.FirstOrDefault(p => p.IsBusy);

        if (running != null)
        {
            this.Text = $"Robocopy Wrapper [{running.JobName}: 実行中]";
            var trayText = $"Robocopy Wrapper - {running.JobName}: 実行中";
            notifyIcon.Text = trayText.Length <= 63 ? trayText : trayText.Substring(0, 63);
        }
        else if (panels.Any(p => p.ScheduleEnabled))
        {
            var nearest = panels
                .Where(p => p.ScheduleEnabled && p.NextScheduledTime < DateTime.MaxValue)
                .OrderBy(p => p.NextScheduledTime)
                .FirstOrDefault();
            if (nearest != null)
            {
                this.Text = "Robocopy Wrapper [スケジュール待機中]";
                var trayText = $"Robocopy Wrapper - 次回: {nearest.NextScheduledTime:HH:mm} ({nearest.JobName})";
                notifyIcon.Text = trayText.Length <= 63 ? trayText : trayText.Substring(0, 63);
            }
            else
            {
                this.Text = "Robocopy Wrapper [スケジュール待機中]";
                notifyIcon.Text = "Robocopy Wrapper";
            }
        }
        else
        {
            this.Text = "Robocopy Wrapper";
            notifyIcon.Text = "Robocopy Wrapper";
        }
    }

    #endregion

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

            _trayBalloonShown = s.TrayBalloonShown;

            // Jobs 配列がある場合はそこからタブ生成
            var jobs = s.Jobs ?? new List<JobSettings>();

            // 旧フォーマット移行: Jobs が空で旧フィールドがある場合
            if (jobs.Count == 0 && (s.Source != null || s.Dest != null))
            {
                jobs.Add(new JobSettings
                {
                    Name = "バックアップ1",
                    Source = s.Source,
                    Dest = s.Dest,
                    Options = s.Options,
                    ScheduleEnabled = s.ScheduleEnabled,
                    ScheduleIntervalHours = s.ScheduleIntervalHours,
                    LastRunTime = s.LastRunTime,
                });
            }

            // タブを作成
            foreach (var job in jobs)
            {
                var panel = AddNewTab(job.Name ?? "バックアップ", job);

                // スプリッター位置はLoad後に適用（ジョブごと、旧フォーマットはグローバル値を使用）
                var splDist = job.SplitterDistance > 0 ? job.SplitterDistance : s.SplitterDistance;
                var innerDist = job.InnerSplitterDistance > 0 ? job.InnerSplitterDistance : s.InnerSplitterDistance;
                if (splDist > 0 || innerDist > 0)
                {
                    this.Load += (_, _) =>
                    {
                        try { if (splDist > 0) panel.SplitterDistance = splDist; } catch { }
                        try { if (innerDist > 0) panel.InnerSplitterDistance = innerDist; } catch { }
                    };
                }
            }

            // 選択タブ復元
            if (s.SelectedTabIndex >= 0 && s.SelectedTabIndex < tabControl.TabCount)
                tabControl.SelectedIndex = s.SelectedTabIndex;
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
            var panels = GetAllPanels().ToList();

            var s = new AppSettings
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                WindowState = WindowState == FormWindowState.Maximized ? "Maximized" : "Normal",
                TrayBalloonShown = _trayBalloonShown,
                SelectedTabIndex = tabControl.SelectedIndex,
                Jobs = panels.Select(p => new JobSettings
                {
                    Name = p.JobName,
                    Source = p.SourcePath,
                    Dest = p.DestPath,
                    Options = p.Options,
                    ScheduleEnabled = p.ScheduleEnabled,
                    ScheduleIntervalHours = p.ScheduleIntervalHours,
                    LastRunTime = p.LastRunTime == DateTime.MinValue ? null : (DateTime?)p.LastRunTime,
                    SplitterDistance = p.SplitterDistance,
                    InnerSplitterDistance = p.InnerSplitterDistance,
                }).ToList(),
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
        // ×ボタン → タスクトレイに格納
        if (!_isExiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
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

        // 実行中チェック
        var busyPanels = GetAllPanels().Where(p => p.IsBusy).ToList();
        if (busyPanels.Count > 0)
        {
            if (e.CloseReason == CloseReason.WindowsShutDown)
            {
                foreach (var p in busyPanels) p.ForceStop();
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
                foreach (var p in busyPanels) p.ForceStop();
            }
        }

        notifyIcon.Visible = false;
        StopSchedulerTimer();
        foreach (var p in GetAllPanels()) p.StopFlushTimer();
        SaveSettings();
    }

    #endregion

    #region Startup Registration

    private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "RobocopyWrapper";

    private void ChkStartup_CheckedChanged(object? sender, EventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
            if (key == null) return;

            if (chkStartup.Checked)
            {
                var exePath = $"\"{Application.ExecutablePath}\"";
                key.SetValue(StartupValueName, exePath);
            }
            else
            {
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
        }
    }

    private static bool IsStartupRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey);
            return key?.GetValue(StartupValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Settings Classes

    private class AppSettings
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string WindowState { get; set; } = "Normal";
        public bool TrayBalloonShown { get; set; }
        public int SplitterDistance { get; set; }
        public int InnerSplitterDistance { get; set; }
        public int SelectedTabIndex { get; set; }
        public List<JobSettings>? Jobs { get; set; }

        // 旧フォーマット互換
        public string? Source { get; set; }
        public string? Dest { get; set; }
        public string? Options { get; set; }
        public bool ScheduleEnabled { get; set; }
        public int ScheduleIntervalHours { get; set; } = 1;
        public DateTime? LastRunTime { get; set; }
    }

    private class JobSettings
    {
        public string? Name { get; set; }
        public string? Source { get; set; }
        public string? Dest { get; set; }
        public string? Options { get; set; }
        public bool ScheduleEnabled { get; set; }
        public int ScheduleIntervalHours { get; set; } = 1;
        public DateTime? LastRunTime { get; set; }
        public int SplitterDistance { get; set; }
        public int InnerSplitterDistance { get; set; }
    }

    #endregion
}
