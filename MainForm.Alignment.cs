using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    public sealed partial class MainForm
    {
        private readonly ComboBox alignmentReference = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 700 };
        private readonly CheckBox alignmentZero = new CheckBox { Text = "各Matrixの最小値を0に補正", Checked = true, Height = 32 };

        private TabPage BuildAlignmentTab()
        {
            var page = new TabPage(T("位置合わせ")) { BackColor = Color.Azure, AutoScroll = true };
            var layout = CreateSidebarLayout(page);
            AddSidebarButton(layout, T("入力フォルダーを開く"), () => ChooseFolder(this, EventArgs.Empty));
            AddSidebarLabel(layout, T("基準Matrix TXT（初期：連番順の先頭）"), 35);
            AddSidebarControl(layout, alignmentReference);
            AddSidebarControl(layout, alignmentZero);
            AddSidebarLabel(layout, T("基準に対して全TXTのXY位置を合わせます。1/4縮小で大まかに検出し、中央50%で精密に合わせます。"), 75);
            AddSidebarLabel(layout, T("最小値補正ONでは各Matrixの最小値を引きます。OFFでは元の高さの基準を保持します。"), 65);
            AddSidebarButton(layout, T("全TXTの位置合わせを実行"), AlignAll);
            AddSidebarLabel(layout, T("ALIGN_日時フォルダーへ同じ名前で保存。ヘッダーを保持し、はみ出しは0で埋めます。行列数が異なるTXTはログに記録して次へ進みます。"), 95);
            return page;
        }

        private void UpdateAlignmentFiles()
        {
            alignmentReference.Items.Clear();
            if (!Directory.Exists(folderPath.Text))
                return;
            var files = FolderFiles.Find(folderPath.Text, ".txt").OrderBy(p =>
            {
                long number;
                return long.TryParse(Regex.Match(Path.GetFileName(p), @"^\d+").Value, out number) ? number : long.MaxValue;
            }).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            alignmentReference.Items.AddRange(files);
            if (files.Length > 0)
                alignmentReference.SelectedIndex = 0;
        }

        private void AlignAll()
        {
            try
            {
                if (!Directory.Exists(folderPath.Text) || alignmentReference.SelectedItem == null)
                    throw new InvalidDataException(T("入力フォルダーと基準Matrixを選択してください。"));
                var files = FolderFiles.Find(folderPath.Text, ".txt");
                string reference = alignmentReference.Text;
                bool zero = alignmentZero.Checked;
                string name = "ALIGN_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss");
                string destination = Path.Combine(folderPath.Text, name);
                for (int i = 1; Directory.Exists(destination) || File.Exists(destination); i++)
                    destination = Path.Combine(folderPath.Text, name + "_" + i.ToString("D3"));
                using (var progress = new BatchProgressForm(T("全TXTの位置合わせ"), files.Length,
                    report => AlignmentProcessor.ExportBatch(files, reference, zero, destination, report)))
                {
                    progress.SetLanguage(Language);
                    progress.ShowDialog(this);
                    if (progress.Error != null)
                        throw progress.Error;
                    CropMessages.Show(this, string.Format(T("位置合わせが終了しました。\nTXT成功: {0}/{1}\n失敗: {2}\nコピー成功: {3}\nコピー失敗: {4}\n\n{5}\n詳細: AlignmentResult.log"),
                        progress.Result.SuccessCount, files.Length, progress.Result.Errors.Count,
                        progress.Result.CopyCount, progress.Result.CopyErrors.Count, destination), T("位置合わせ結果"));
                }
            }
            catch (Exception ex) { CropMessages.Show(this, ex.Message, T("位置合わせエラー")); }
        }
    }
}
