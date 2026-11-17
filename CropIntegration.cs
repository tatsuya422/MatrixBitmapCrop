using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    public enum CropLanguage
    {
        English = 0,
        Japanese = 1
    }

    public sealed class CropOptions
    {
        public string FolderPath { get; set; }
        public CropLanguage Language { get; set; } = CropLanguage.English;
        public bool AllowUserClose { get; set; } = true;
        public bool StartMaximized { get; set; } = true;
    }

    // 呼出元のSTA UIスレッドで使用する。DLL内ではApplication.RunやExitを呼ばない。
    public static class CropLauncher
    {
        public static MainForm CreateWindow(CropOptions options = null)
        {
            RequireSta();
            options = options ?? new CropOptions();
            if (!Enum.IsDefined(typeof(CropLanguage), options.Language))
                throw new ArgumentOutOfRangeException(nameof(options.Language));
            string folder = string.IsNullOrWhiteSpace(options.FolderPath) ? null : Path.GetFullPath(options.FolderPath);
            if (folder != null && !Directory.Exists(folder))
                throw new DirectoryNotFoundException(folder);
            var form = new MainForm(options.Language);
            try
            {
                form.ConfigureClosePolicy(options.AllowUserClose);
                form.WindowState = options.StartMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
                if (folder != null)
                {
                    var completion = new TaskCompletionSource<bool>();
                    form.InitialLoadTask = completion.Task;
                    form.FormClosed += (s, e) => completion.TrySetCanceled();
                    form.Shown += async (s, e) =>
                    {
                        try
                        {
                            await form.OpenDataFolderAsync(folder);
                            completion.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            form.SetHostStatus(ex.Message);
                            completion.TrySetException(ex);
                        }
                    };
                }
                return form;
            }
            catch
            {
                form.Dispose();
                throw;
            }
        }

        public static MainForm ShowWindow(IWin32Window owner = null, CropOptions options = null)
        {
            RequireOwner(owner);
            var form = CreateWindow(options);
            try
            {
                if (owner == null) form.Show(); else form.Show(owner);
                return form;
            }
            catch { form.Dispose(); throw; }
        }

        public static DialogResult ShowDialog(IWin32Window owner = null, CropOptions options = null)
        {
            RequireOwner(owner);
            using (var form = CreateWindow(options))
                return owner == null ? form.ShowDialog() : form.ShowDialog(owner);
        }

        internal static void RequireSta()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Call CropLauncher on a WinForms STA UI thread, not Task.Run.");
        }

        private static void RequireOwner(IWin32Window owner)
        {
            RequireSta();
            var control = owner as Control;
            if (control != null && control.InvokeRequired)
                throw new InvalidOperationException("Use the owner's UI thread.");
        }
    }

    public sealed partial class MainForm
    {
        private readonly int owningThreadId = Thread.CurrentThread.ManagedThreadId;
        private bool closingFromHost;
        public CropLanguage Language { get; private set; }
        public bool AllowUserClose { get; private set; } = true;
        public Task InitialLoadTask { get; internal set; } = Task.FromResult(true);
        public string StatusMessage { get { return status.Text; } }
        public string CurrentFolder { get { return folderPath.Text; } }

        private string T(string text) { return CropLocalization.Text(text, Language); }

        private void LocalizeInitialTexts(Control control)
        {
            if (control is Label || control is ButtonBase || control is TabPage)
                control.Text = T(control.Text);
            // 英文の説明が増えても、左パネルのスクロールで全文へ到達できる高さを確保する。
            if (control.Parent is TableLayoutPanel && (control is Label || control is CheckBox))
            {
                int width = Math.Max(180, control.Parent.ClientSize.Width - 20);
                control.Height = Math.Max(control.Height,
                    control.GetPreferredSize(new System.Drawing.Size(width, 0)).Height + 4);
            }
            foreach (Control child in control.Controls)
                LocalizeInitialTexts(child);
        }

        internal void SetHostStatus(string text) { if (!IsDisposed) status.Text = T(text); }

        internal void ConfigureClosePolicy(bool allow)
        {
            AllowUserClose = allow;
            foreach (Control exit in Controls.Find("exitButton", true))
            {
                exit.Visible = exit.Enabled = allow;
                // 組み込み時はボタンの入れ物も畳み、下に空の58pxを残さない。
                if (exit.Parent != null) exit.Parent.Visible = allow;
            }
        }

        public void CloseFromHost()
        {
            RequireUiThread();
            if (IsDisposed) return;
            closingFromHost = true;
            try { Close(); }
            finally { closingFromHost = false; }
        }

        private void RequireUiThread()
        {
            CropLauncher.RequireSta();
            if (owningThreadId != Thread.CurrentThread.ManagedThreadId)
                throw new InvalidOperationException("Use the UI thread that created this window.");
        }

        // 既存の読み込み経路を共用し、言語によってファイル名や座標の扱いを変えない。
        public Task OpenDataFolderAsync(string folder)
        {
            RequireUiThread();
            if (IsDisposed) throw new ObjectDisposedException(nameof(MainForm));
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Specify a folder.", nameof(folder));
            string path = Path.GetFullPath(folder);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            LoadFolder(path);
            return Task.FromResult(true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowUserClose && !closingFromHost && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && source != null)
            {
                picture.Image = null;
                source.Dispose();
                source = null;
            }
            base.Dispose(disposing);
        }
    }
}
