using System;
using System.Drawing;
using System.Windows.Forms;

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
        components = new System.ComponentModel.Container();
        chkStartup = new CheckBox();
        btnAddTab = new Button();
        tabControl = new TabControl();
        trayContextMenu = new ContextMenuStrip(components);
        trayMenuShow = new ToolStripMenuItem();
        trayMenuExit = new ToolStripMenuItem();
        notifyIcon = new NotifyIcon(components);
        tabContextMenu = new ContextMenuStrip(components);
        tabMenuRename = new ToolStripMenuItem();
        tabMenuDelete = new ToolStripMenuItem();
        SuspendLayout();

        // chkStartup
        chkStartup.AutoSize = true;
        chkStartup.Location = new Point(12, 8);
        chkStartup.Text = "スタートアップに登録";

        // btnAddTab
        btnAddTab.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnAddTab.Location = new Point(734, 5);
        btnAddTab.Size = new Size(40, 25);
        btnAddTab.Text = "+";

        // tabControl
        tabControl.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        tabControl.Location = new Point(0, 34);
        tabControl.Size = new Size(784, 471);

        // trayContextMenu
        trayMenuShow.Text = "表示";
        trayMenuExit.Text = "終了";
        trayContextMenu.Items.AddRange(new ToolStripItem[]
        {
            trayMenuShow,
            new ToolStripSeparator(),
            trayMenuExit,
        });

        // notifyIcon
        notifyIcon.Text = "Robocopy Wrapper";
        notifyIcon.ContextMenuStrip = trayContextMenu;
        notifyIcon.Visible = false;

        // tabContextMenu
        tabMenuRename.Text = "名前の変更";
        tabMenuDelete.Text = "タブを削除";
        tabContextMenu.Items.AddRange(new ToolStripItem[]
        {
            tabMenuRename,
            tabMenuDelete,
        });

        // Form1
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(784, 505);
        Controls.Add(chkStartup);
        Controls.Add(btnAddTab);
        Controls.Add(tabControl);
        MinimumSize = new Size(500, 400);
        Text = "Robocopy Wrapper";
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private CheckBox chkStartup;
    private Button btnAddTab;
    private TabControl tabControl;
    private NotifyIcon notifyIcon;
    private ContextMenuStrip trayContextMenu;
    private ToolStripMenuItem trayMenuShow;
    private ToolStripMenuItem trayMenuExit;
    private ContextMenuStrip tabContextMenu;
    private ToolStripMenuItem tabMenuRename;
    private ToolStripMenuItem tabMenuDelete;
}
