using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    public sealed partial class MainForm
    {
        private TabPage smoothingTab;
        private readonly ComboBox smoothingMethod = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox smoothingSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };

        private bool SmoothingActive { get { return smoothingTab != null && operationTabs?.SelectedTab == smoothingTab; } }
        private bool CropFrameActive { get { return operationTabs.SelectedIndex == 0 || SmoothingActive; } }

        private TabPage BuildSmoothingTab()
        {
            var page = new TabPage(T("平滑化フィルタ")) { BackColor = Color.Azure, AutoScroll = true };
            var layout = CreateSidebarLayout(page);
            AddSidebarButton(layout, T("入力フォルダーを開く"), () => ChooseFolder(this, EventArgs.Empty));
            AddSidebarLabel(layout, T("画像上で始点・幅方向の終点・高さ方向の点を順にクリックします。切り出しと枠を共用します。"), 72);
            AddSidebarButton(layout, T("指定をクリア"), ClearSelection);
            AddSidebarLabel(layout, T("平滑化方式"));
            smoothingMethod.Items.AddRange(new object[] { T("メディアンフィルタ"), T("ガウシアン平滑化") });
            smoothingMethod.SelectedIndex = 0;
            AddSidebarControl(layout, smoothingMethod);
            AddSidebarLabel(layout, T("フィルタサイズ（セル）"));
            for (int side = MatrixSmoothing.MinimumSize; side <= MatrixSmoothing.MaximumSize; side += 2)
                smoothingSize.Items.Add(string.Format("{0} × {0}", side));
            smoothingSize.SelectedIndex = 0;
            AddSidebarControl(layout, smoothingSize);
            AddSidebarLabel(layout, T("赤枠内のセルだけを使って、赤枠内を平滑化します。"), 52);
            AddSidebarButton(layout, T("全TXTに適用して保存"), ExportSmoothing);
            AddSidebarLabel(layout, T("結果フォルダーに平滑化済みTXTを保存し、付随ファイルもコピーします。"), 56);
            return page;
        }

        private void ExportSmoothing()
        {
            if (source == null || selection == null || !Directory.Exists(folderPath.Text))
            {
                CropMessages.Show(this, T("入力フォルダーを開き、赤枠を指定してください。"));
                return;
            }

            try
            {
                string[] files = FolderFiles.Find(folderPath.Text, ".txt");
                if (files.Length == 0)
                    throw new InvalidDataException(T("Matrix TXTがありません。"));
                string[] attachments = FolderFiles.FindNonMatrixFiles(folderPath.Text);
                string logName = FolderFiles.GetAvailableLogName(attachments, "SmoothingResult");

                var method = smoothingMethod.SelectedIndex == 0 ? SmoothingMethod.Median : SmoothingMethod.Gaussian;
                string prefix = method == SmoothingMethod.Median ? "MEDIAN_" : "GAUSSIAN_";
                string name = prefix + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture);
                string destination = Path.Combine(folderPath.Text, name);
                for (int i = 1; Directory.Exists(destination) || File.Exists(destination); i++)
                    destination = Path.Combine(folderPath.Text, name + "_" + i.ToString("D3", CultureInfo.InvariantCulture));

                var area = selection;
                var imageSize = source.Size;
                var scale = referenceScale;
                int size = MatrixSmoothing.MinimumSize + smoothingSize.SelectedIndex * 2;
                using (var progress = new BatchProgressForm(T("平滑化フィルタを適用中"), files.Length + attachments.Length,
                    report => MatrixSmoothing.ExportBatch(files, attachments, area, imageSize, scale, method, size, destination, logName, report)))
                {
                    progress.SetLanguage(Language);
                    progress.ShowDialog(this);
                    if (progress.Error != null)
                        throw progress.Error;
                    CropMessages.Show(this, string.Format(T("平滑化フィルタの処理が終了しました。\nTXT成功: {0}/{1}\n失敗: {2}\nコピー成功: {3}\nコピー失敗: {4}\n\n{5}\n詳細: {6}"),
                        progress.Result.SuccessCount, files.Length, progress.Result.Errors.Count,
                        progress.Result.CopyCount, progress.Result.CopyErrors.Count, destination, logName), T("処理結果"));
                }
            }
            catch (Exception ex)
            {
                CropMessages.Show(this, ex.Message, T("平滑化フィルタのエラー"));
            }
        }
    }
}
