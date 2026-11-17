using System;
using System.Drawing;
using System.Windows.Forms;
using MatrixBitmapCrop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new HostForm());
    }
}

internal sealed class HostForm : Form
{
    private readonly TextBox folder = new TextBox { Width = 560 };
    private readonly ComboBox language = new ComboBox
    {
        Width = 200,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private MainForm cropWindow;

    public HostForm()
    {
        Text = "Matrix crop DLL host example";
        ClientSize = new Size(620, 340);
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(16)
        };
        panel.Controls.Add(new Label { AutoSize = true, Text = "Data folder (optional) / データフォルダー（任意）" });
        panel.Controls.Add(folder);
        AddButton(panel, "Browse / フォルダー選択", () =>
        {
            using (var dialog = new FolderBrowserDialog())
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    folder.Text = dialog.SelectedPath;
        });
        language.Items.AddRange(new object[] { "English", "日本語" });
        language.SelectedIndex = 0;
        panel.Controls.Add(language);
        AddButton(panel, "Open window / 別ウィンドウで開く", () =>
        {
            if (cropWindow != null && !cropWindow.IsDisposed)
            {
                cropWindow.Activate();
                return;
            }
            var options = Options();
            options.AllowUserClose = false;
            cropWindow = CropLauncher.ShowWindow(this, options);
            ObserveLoad(cropWindow);
        });
        AddButton(panel, "Close from host / 呼出元から終了", () =>
        {
            if (cropWindow != null && !cropWindow.IsDisposed)
                cropWindow.CloseFromHost();
        });
        AddButton(panel, "Open dialog / ダイアログで開く", () => CropLauncher.ShowDialog(this, Options()));
        Controls.Add(panel);
    }

    private CropOptions Options()
    {
        return new CropOptions
        {
            FolderPath = folder.Text,
            Language = (CropLanguage)language.SelectedIndex,
            StartMaximized = true
        };
    }

    private async void ObserveLoad(MainForm window)
    {
        try { await window.InitialLoadTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed) MessageBox.Show(this, ex.Message);
        }
    }

    private void AddButton(Control parent, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32 };
        button.Click += (s, e) =>
        {
            try { action(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message); }
        };
        parent.Controls.Add(button);
    }
}
