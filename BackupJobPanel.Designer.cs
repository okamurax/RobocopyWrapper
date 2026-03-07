using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RobocopyWrapper;

partial class BackupJobPanel
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopFlushTimer();
            ForceStop();
            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        lblSource = new Label();
        txtSource = new TextBox();
        btnBrowseSource = new Button();
        lblDest = new Label();
        txtDest = new TextBox();
        btnBrowseDest = new Button();
        lblOptions = new Label();
        txtOptions = new TextBox();
        btnExecute = new Button();
        btnPause = new Button();
        btnStop = new Button();
        chkSchedule = new CheckBox();
        nudScheduleHours = new NumericUpDown();
        lblScheduleUnit = new Label();
        lblNextRun = new Label();
        splitContainer = new SplitContainer();
        splitContainerInner = new SplitContainer();
        lblProgress = new Label();
        txtProgress = new TextBox();
        lblCopyResult = new Label();
        btnClearCopyResult = new Button();
        txtCopyResult = new TextBox();
        lblErrorLog = new Label();
        btnClearLog = new Button();
        txtErrorLog = new TextBox();
        btnVerify = new Button();
        btnVerifyStop = new Button();
        ((System.ComponentModel.ISupportInitialize)nudScheduleHours).BeginInit();
        ((System.ComponentModel.ISupportInitialize)splitContainer).BeginInit();
        splitContainer.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)splitContainerInner).BeginInit();
        splitContainerInner.SuspendLayout();
        SuspendLayout();

        // lblSource
        lblSource.AutoSize = true;
        lblSource.Location = new Point(12, 15);
        lblSource.Text = "コピー元:";

        // txtSource
        txtSource.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtSource.Location = new Point(80, 12);
        txtSource.Size = new Size(600, 23);
        txtSource.AllowDrop = true;

        // btnBrowseSource
        btnBrowseSource.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnBrowseSource.Location = new Point(686, 11);
        btnBrowseSource.Size = new Size(86, 25);
        btnBrowseSource.Text = "参照...";

        // lblDest
        lblDest.AutoSize = true;
        lblDest.Location = new Point(12, 47);
        lblDest.Text = "コピー先:";

        // txtDest
        txtDest.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtDest.Location = new Point(80, 44);
        txtDest.Size = new Size(600, 23);
        txtDest.AllowDrop = true;

        // btnBrowseDest
        btnBrowseDest.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnBrowseDest.Location = new Point(686, 43);
        btnBrowseDest.Size = new Size(86, 25);
        btnBrowseDest.Text = "参照...";

        // lblOptions
        lblOptions.AutoSize = true;
        lblOptions.Location = new Point(12, 79);
        lblOptions.Text = "オプション:";

        // txtOptions
        txtOptions.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtOptions.Location = new Point(80, 76);
        txtOptions.Size = new Size(600, 23);

        // btnExecute
        btnExecute.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnExecute.Location = new Point(686, 75);
        btnExecute.Size = new Size(86, 25);
        btnExecute.Text = "実行";

        // btnPause
        btnPause.Location = new Point(12, 108);
        btnPause.Size = new Size(90, 25);
        btnPause.Text = "一時停止";
        btnPause.Enabled = false;

        // btnStop
        btnStop.Location = new Point(108, 108);
        btnStop.Size = new Size(90, 25);
        btnStop.Text = "中止";
        btnStop.Enabled = false;

        // chkSchedule
        chkSchedule.AutoSize = true;
        chkSchedule.Location = new Point(220, 111);
        chkSchedule.Text = "定期実行";

        // nudScheduleHours
        nudScheduleHours.Location = new Point(305, 108);
        nudScheduleHours.Size = new Size(55, 25);
        nudScheduleHours.Minimum = 1;
        nudScheduleHours.Maximum = 24;
        nudScheduleHours.Value = 1;
        nudScheduleHours.Enabled = false;

        // lblScheduleUnit
        lblScheduleUnit.AutoSize = true;
        lblScheduleUnit.Location = new Point(365, 111);
        lblScheduleUnit.Text = "時間ごと";

        // lblNextRun
        lblNextRun.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        lblNextRun.TextAlign = ContentAlignment.MiddleRight;
        lblNextRun.Location = new Point(380, 111);
        lblNextRun.Size = new Size(300, 16);
        lblNextRun.Text = "";

        // btnVerify
        btnVerify.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnVerify.Location = new Point(686, 108);
        btnVerify.Size = new Size(86, 25);
        btnVerify.Text = "検証";

        // btnVerifyStop
        btnVerifyStop.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnVerifyStop.Location = new Point(686, 108);
        btnVerifyStop.Size = new Size(86, 25);
        btnVerifyStop.Text = "検証中止";
        btnVerifyStop.Visible = false;

        // splitContainer
        splitContainer.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        splitContainer.Location = new Point(12, 140);
        splitContainer.Size = new Size(760, 320);
        splitContainer.Orientation = Orientation.Horizontal;
        splitContainer.SplitterDistance = 180;
        splitContainer.SplitterWidth = 6;

        // --- Panel1: 進捗ログ ---
        lblProgress.AutoSize = true;
        lblProgress.Text = "進捗ログ:";
        lblProgress.Location = new Point(0, 0);
        lblProgress.Dock = DockStyle.Top;

        txtProgress.Multiline = true;
        txtProgress.ReadOnly = true;
        txtProgress.ScrollBars = ScrollBars.Both;
        txtProgress.WordWrap = false;
        txtProgress.BackColor = Color.FromArgb(20, 20, 30);
        txtProgress.ForeColor = Color.FromArgb(180, 180, 180);
        txtProgress.Font = GetMonoFont(9F);
        txtProgress.Dock = DockStyle.Fill;

        splitContainer.Panel1.Controls.Add(txtProgress);
        splitContainer.Panel1.Controls.Add(lblProgress);

        // --- Panel2: コピー結果 + エラーログ ---
        splitContainerInner.Dock = DockStyle.Fill;
        splitContainerInner.Orientation = Orientation.Horizontal;
        splitContainerInner.SplitterDistance = 80;
        splitContainerInner.SplitterWidth = 6;
        splitContainerInner.Panel1MinSize = 50;
        splitContainerInner.Panel2MinSize = 50;

        // -- コピー結果ログ --
        var pnlCopyResultHeader = new Panel();
        pnlCopyResultHeader.Dock = DockStyle.Top;
        pnlCopyResultHeader.Height = 23;

        lblCopyResult.AutoSize = true;
        lblCopyResult.Text = "操作結果:";
        lblCopyResult.Location = new Point(0, 4);

        btnClearCopyResult.Size = new Size(75, 23);
        btnClearCopyResult.Text = "クリア";
        btnClearCopyResult.Dock = DockStyle.Right;

        pnlCopyResultHeader.Controls.Add(lblCopyResult);
        pnlCopyResultHeader.Controls.Add(btnClearCopyResult);

        txtCopyResult.Multiline = true;
        txtCopyResult.ReadOnly = true;
        txtCopyResult.ScrollBars = ScrollBars.Both;
        txtCopyResult.WordWrap = false;
        txtCopyResult.BackColor = Color.FromArgb(20, 30, 20);
        txtCopyResult.ForeColor = Color.FromArgb(80, 220, 120);
        txtCopyResult.Font = GetMonoFont(9F);
        txtCopyResult.Dock = DockStyle.Fill;

        splitContainerInner.Panel1.Controls.Add(txtCopyResult);
        splitContainerInner.Panel1.Controls.Add(pnlCopyResultHeader);

        // -- エラーログ --
        var pnlErrorHeader = new Panel();
        pnlErrorHeader.Dock = DockStyle.Top;
        pnlErrorHeader.Height = 23;

        lblErrorLog.AutoSize = true;
        lblErrorLog.Text = "エラーログ:";
        lblErrorLog.Location = new Point(0, 4);

        btnClearLog.Size = new Size(75, 23);
        btnClearLog.Text = "クリア";
        btnClearLog.Dock = DockStyle.Right;

        pnlErrorHeader.Controls.Add(lblErrorLog);
        pnlErrorHeader.Controls.Add(btnClearLog);

        txtErrorLog.Multiline = true;
        txtErrorLog.ReadOnly = true;
        txtErrorLog.ScrollBars = ScrollBars.Both;
        txtErrorLog.WordWrap = false;
        txtErrorLog.BackColor = Color.FromArgb(30, 20, 20);
        txtErrorLog.ForeColor = Color.FromArgb(255, 80, 80);
        txtErrorLog.Font = GetMonoFont(9F);
        txtErrorLog.Dock = DockStyle.Fill;

        splitContainerInner.Panel2.Controls.Add(txtErrorLog);
        splitContainerInner.Panel2.Controls.Add(pnlErrorHeader);

        splitContainer.Panel2.Controls.Add(splitContainerInner);

        // BackupJobPanel
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Controls.Add(lblSource);
        Controls.Add(txtSource);
        Controls.Add(btnBrowseSource);
        Controls.Add(lblDest);
        Controls.Add(txtDest);
        Controls.Add(btnBrowseDest);
        Controls.Add(lblOptions);
        Controls.Add(txtOptions);
        Controls.Add(btnExecute);
        Controls.Add(btnPause);
        Controls.Add(btnStop);
        Controls.Add(chkSchedule);
        Controls.Add(nudScheduleHours);
        Controls.Add(lblScheduleUnit);
        Controls.Add(lblNextRun);
        Controls.Add(btnVerify);
        Controls.Add(btnVerifyStop);
        Controls.Add(splitContainer);
        ((System.ComponentModel.ISupportInitialize)nudScheduleHours).EndInit();
        splitContainerInner.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)splitContainerInner).EndInit();
        splitContainer.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)splitContainer).EndInit();
        ResumeLayout(false);
        PerformLayout();
    }

    private static Font GetMonoFont(float size)
    {
        string[] candidates = ["BIZ UDGothic", "Cascadia Mono", "MS Gothic", "Consolas"];
        var installed = FontFamily.Families;
        foreach (var name in candidates)
        {
            if (installed.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return new Font(name, size);
        }
        return new Font(FontFamily.GenericMonospace, size);
    }

    private Label lblSource;
    private TextBox txtSource;
    private Button btnBrowseSource;
    private Label lblDest;
    private TextBox txtDest;
    private Button btnBrowseDest;
    private Label lblOptions;
    private TextBox txtOptions;
    private Button btnExecute;
    private Button btnPause;
    private Button btnStop;
    private CheckBox chkSchedule;
    private NumericUpDown nudScheduleHours;
    private Label lblScheduleUnit;
    private Label lblNextRun;
    private SplitContainer splitContainer;
    private SplitContainer splitContainerInner;
    private Label lblProgress;
    private TextBox txtProgress;
    private Label lblCopyResult;
    private Button btnClearCopyResult;
    private TextBox txtCopyResult;
    private Label lblErrorLog;
    private Button btnClearLog;
    private TextBox txtErrorLog;
    private Button btnVerify;
    private Button btnVerifyStop;
}
