namespace RobocopyWrapper;

partial class Form1
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
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
        splitContainer = new SplitContainer();
        lblProgress = new Label();
        rtbProgress = new RichTextBox();
        lblErrorLog = new Label();
        btnClearLog = new Button();
        txtErrorLog = new TextBox();
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
        txtSource.DragEnter += TxtPath_DragEnter;
        txtSource.DragDrop += TxtSource_DragDrop;

        // btnBrowseSource
        btnBrowseSource.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnBrowseSource.Location = new Point(686, 11);
        btnBrowseSource.Size = new Size(86, 25);
        btnBrowseSource.Text = "参照...";
        btnBrowseSource.Click += BtnBrowseSource_Click;

        // lblDest
        lblDest.AutoSize = true;
        lblDest.Location = new Point(12, 47);
        lblDest.Text = "コピー先:";

        // txtDest
        txtDest.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtDest.Location = new Point(80, 44);
        txtDest.Size = new Size(600, 23);
        txtDest.AllowDrop = true;
        txtDest.DragEnter += TxtPath_DragEnter;
        txtDest.DragDrop += TxtDest_DragDrop;

        // btnBrowseDest
        btnBrowseDest.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnBrowseDest.Location = new Point(686, 43);
        btnBrowseDest.Size = new Size(86, 25);
        btnBrowseDest.Text = "参照...";
        btnBrowseDest.Click += BtnBrowseDest_Click;

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
        btnExecute.Click += BtnExecute_Click;

        // btnPause
        btnPause.Location = new Point(12, 108);
        btnPause.Size = new Size(90, 25);
        btnPause.Text = "一時停止";
        btnPause.Enabled = false;
        btnPause.Click += BtnPause_Click;

        // btnStop
        btnStop.Location = new Point(108, 108);
        btnStop.Size = new Size(90, 25);
        btnStop.Text = "中止";
        btnStop.Enabled = false;
        btnStop.Click += BtnStop_Click;

        // splitContainer
        splitContainer.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        splitContainer.Location = new Point(12, 140);
        splitContainer.Size = new Size(760, 350);
        splitContainer.Orientation = Orientation.Horizontal;
        splitContainer.SplitterDistance = 200;
        splitContainer.SplitterWidth = 6;

        // --- Panel1: 進捗ログ ---
        lblProgress = new Label();
        lblProgress.AutoSize = true;
        lblProgress.Text = "進捗ログ:";
        lblProgress.Location = new Point(0, 0);
        lblProgress.Dock = DockStyle.Top;

        rtbProgress = new RichTextBox();
        rtbProgress.ReadOnly = true;
        rtbProgress.WordWrap = false;
        rtbProgress.BackColor = System.Drawing.Color.FromArgb(20, 20, 30);
        rtbProgress.ForeColor = System.Drawing.Color.FromArgb(180, 180, 180);
        rtbProgress.Font = GetMonoFont(9F);
        rtbProgress.Dock = DockStyle.Fill;
        rtbProgress.DetectUrls = false;

        splitContainer.Panel1.Controls.Add(rtbProgress);
        splitContainer.Panel1.Controls.Add(lblProgress);

        // --- Panel2: エラーログ ---
        lblErrorLog = new Label();
        lblErrorLog.AutoSize = true;
        lblErrorLog.Text = "エラーログ:";
        lblErrorLog.Dock = DockStyle.Top;

        btnClearLog = new Button();
        btnClearLog.Size = new Size(60, 20);
        btnClearLog.Text = "クリア";
        btnClearLog.Dock = DockStyle.Top;
        btnClearLog.Click += BtnClearLog_Click;

        txtErrorLog = new TextBox();
        txtErrorLog.Multiline = true;
        txtErrorLog.ReadOnly = true;
        txtErrorLog.ScrollBars = ScrollBars.Both;
        txtErrorLog.WordWrap = false;
        txtErrorLog.BackColor = System.Drawing.Color.FromArgb(30, 20, 20);
        txtErrorLog.ForeColor = System.Drawing.Color.FromArgb(255, 80, 80);
        txtErrorLog.Font = GetMonoFont(9F);
        txtErrorLog.Dock = DockStyle.Fill;
        txtErrorLog.DoubleClick += TxtErrorLog_DoubleClick;

        splitContainer.Panel2.Controls.Add(txtErrorLog);
        splitContainer.Panel2.Controls.Add(btnClearLog);
        splitContainer.Panel2.Controls.Add(lblErrorLog);

        // Form1
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(784, 505);
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
        Controls.Add(splitContainer);
        MinimumSize = new Size(500, 400);
        Text = "Robocopy Wrapper";
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private static Font GetMonoFont(float size)
    {
        // 日本語グリフを持つ等幅フォントを優先的に使用
        string[] candidates = ["BIZ UDGothic", "Cascadia Mono", "MS Gothic", "Consolas"];
        var installed = System.Drawing.FontFamily.Families;
        foreach (var name in candidates)
        {
            if (installed.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return new Font(name, size);
        }
        return new Font(System.Drawing.FontFamily.GenericMonospace, size);
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
    private SplitContainer splitContainer;
    private Label lblProgress;
    private RichTextBox rtbProgress;
    private Label lblErrorLog;
    private Button btnClearLog;
    private TextBox txtErrorLog;
}
