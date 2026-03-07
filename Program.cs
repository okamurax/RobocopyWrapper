using System;
using System.Threading;
using System.Windows.Forms;

namespace RobocopyWrapper;

static class Program
{
    private static Mutex? _mutex;

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        _mutex = new Mutex(true, "RobocopyWrapper_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Robocopy Wrapper は既に起動しています。", "Robocopy Wrapper",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Form1());
    }
}
