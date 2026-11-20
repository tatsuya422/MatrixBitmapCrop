using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    public sealed partial class MainForm
    {
        private TabPage zeroBaselineTab;
        private readonly TextBox zeroInput = new TextBox { ReadOnly = true, Multiline = true, Height = 48, ScrollBars = ScrollBars.Vertical };
        private readonly ComboBox zeroBitmap = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 500 };
        private readonly ComboBox zeroTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FormattingEnabled = true };
        private readonly PictureBox zeroPreview = new PictureBox
        {
            Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.DimGray, Visible = false
        };
        private string zeroPreviewStatus;

        private bool ZeroBaselineActive { get { return zeroBaselineTab != null && operationTabs.SelectedTab == zeroBaselineTab; } }

        private TabPage BuildZeroBaselineTab()
        {
            var page = new TabPage(T("高さ補正")) { BackColor = Color.Azure, AutoScroll = true };
            var layout = CreateSidebarLayout(page);
            folderPath.TextChanged += (s, e) => zeroInput.Text = folderPath.Text;
            zeroInput.TextChanged += (s, e) => { if (ZeroBaselineActive) LoadZeroBitmapList(); };
            zeroBitmap.SelectedIndexChanged += (s, e) => LoadZeroPreview();
            zeroPreview.Disposed += (s, e) => zeroPreview.Image?.Dispose();
            AddSidebarLabel(layout, T("入力フォルダー"));
            AddSidebarControl(layout, zeroInput);
            AddSidebarButton(layout, T("入力フォルダーを開く"), () =>
            {
                using (var dialog = new FolderBrowserDialog
                {
                    Description = T("Matrix TXTのフォルダーを指定（BMPは省略可能）"),
                    SelectedPath = folderHistory.GetInitialFolder(zeroInput.Text)
                })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        if (zeroInput.Text == dialog.SelectedPath)
                            LoadZeroBitmapList();
                        else
                            zeroInput.Text = dialog.SelectedPath;
                    }
                }
            });
            AddSidebarLabel(layout, T("参考画像（BMP）"));
            AddSidebarControl(layout, zeroBitmap);
            AddSidebarLabel(layout, T("補正後の最小値（μm）"));
            zeroTarget.Format += (s, e) => e.Value = ((int)e.ListItem).ToString("+0;-0;0", CultureInfo.InvariantCulture);
            for (int target = -500; target <= 500; target += 100)
                zeroTarget.Items.Add(target);
            zeroTarget.SelectedItem = 0;
            AddSidebarControl(layout, zeroTarget);
            AddSidebarLabel(layout, T("各TXTの最小値が指定した高さになるよう、全セルを同じ量だけ補正します。"), 54);
            AddSidebarButton(layout, T("全TXTの高さを補正して保存"), () => SaveZeroBaseline(zeroInput.Text, (int)zeroTarget.SelectedItem));
            AddSidebarLabel(layout, T("入力フォルダー内のZERO_日時に、元と同じファイル名で保存します。"), 55);
            return page;
        }

        private void UpdateZeroPreviewVisibility()
        {
            canvas.Visible = !ZeroBaselineActive;
            zeroPreview.Visible = ZeroBaselineActive;
            if (ZeroBaselineActive)
                LoadZeroBitmapList();
            else
                SetZeroPreview(null);
        }

        private void LoadZeroBitmapList()
        {
            string previous = zeroBitmap.SelectedItem as string;
            zeroBitmap.Items.Clear();
            zeroBitmap.Enabled = false;
            try
            {
                if (Directory.Exists(zeroInput.Text))
                    zeroBitmap.Items.AddRange(FolderFiles.Find(zeroInput.Text, ".bmp").Select(Path.GetFileName).ToArray());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                SetZeroPreview(null);
                zeroPreviewStatus = T("フォルダー読み込みエラー") + " - " + ex.Message;
                UpdateStatus();
                return;
            }

            zeroBitmap.Enabled = zeroBitmap.Items.Count > 0;
            if (zeroBitmap.Enabled)
                zeroBitmap.SelectedIndex = Math.Max(0, previous == null ? -1 : zeroBitmap.Items.IndexOf(previous));
            else
                LoadZeroPreview();
        }

        private void LoadZeroPreview()
        {
            Bitmap preview = null;
            string name = zeroBitmap.SelectedItem as string;
            if (name == null)
                zeroPreviewStatus = T(string.IsNullOrEmpty(zeroInput.Text) ? "入力フォルダーを指定してください。" : "フォルダー内にBMPがありません。");
            else
            {
                try
                {
                    preview = ReadBitmap(Path.Combine(zeroInput.Text, name));
                    zeroPreviewStatus = name + "  " + preview.Width + " × " + preview.Height;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                    || ex is OutOfMemoryException || ex is System.Runtime.InteropServices.ExternalException)
                {
                    zeroPreviewStatus = T("画像読み込みエラー") + " - " + name;
                }
            }
            SetZeroPreview(preview);
            UpdateStatus();
        }

        private void SetZeroPreview(Bitmap preview)
        {
            var previous = zeroPreview.Image;
            zeroPreview.Image = preview;
            previous?.Dispose();
        }

        private void SaveZeroBaseline(string input, int targetMinimumUm)
        {
            try
            {
                if (!Directory.Exists(input))
                    throw new InvalidDataException(T("入力フォルダーを指定してください。"));
                var files = FolderFiles.Find(input, ".txt");
                if (files.Length == 0)
                    throw new InvalidDataException(T("Matrix TXTがありません。"));
                var attachments = FolderFiles.FindNonMatrixFiles(input);
                string logName = FolderFiles.GetAvailableLogName(attachments, "ZeroResult");
                string name = "ZERO_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss");
                string destination = Path.Combine(input, name);
                for (int i = 1; Directory.Exists(destination) || File.Exists(destination); i++)
                    destination = Path.Combine(input, name + "_" + i.ToString("D3"));
                using (var progress = new BatchProgressForm(T("全TXTの高さ補正"), files.Length + attachments.Length,
                    report => MatrixZeroBaseline.ExportBatch(files, attachments, destination, logName, targetMinimumUm, report)))
                {
                    progress.SetLanguage(Language);
                    progress.ShowDialog(this);
                    if (progress.Error != null)
                        throw progress.Error;
                    CropMessages.Show(this, string.Format(T("高さ補正が終了しました。\nTXT成功: {0}/{1}\n失敗: {2}\nコピー成功: {3}\nコピー失敗: {4}\n\n{5}\n詳細: {6}"),
                        progress.Result.SuccessCount, files.Length, progress.Result.Errors.Count,
                        progress.Result.CopyCount, progress.Result.CopyErrors.Count, destination, logName), T("処理結果"));
                }
            }
            catch (Exception ex) { CropMessages.Show(this, ex.Message, T("高さ補正エラー")); }
        }
    }
}
