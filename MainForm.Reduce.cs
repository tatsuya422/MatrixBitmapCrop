using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    public sealed partial class MainForm
    {
        // 読み込み前でも情報の表示範囲が分かる、余白付きの枠を設ける。
        private readonly Label reductionInfo = new Label
        {
            Height = 155,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(6),
            TextAlign = ContentAlignment.TopLeft
        };

        private TabPage BuildReduceTab()
        {
            var page = new TabPage(T("データ縮小")) { BackColor = Color.Azure, AutoScroll = true };
            var layout = CreateSidebarLayout(page);
            AddSidebarButton(layout, T("入力フォルダーを開く"), () => ChooseFolder(this, EventArgs.Empty));
            AddSidebarLabel(layout, T("Y方向の行数を縮小します。\r\n実寸WIDTH・HEIGHTとX列数を保持。"), 50);
            AddSidebarControl(layout, reductionInfo);
            UpdateReductionInfo();
            AddSidebarLabel(layout, T("目標行数 = HEIGHT ÷ (WIDTH ÷ 列数)\r\n整数に丸め、元の行数を上限にします。"), 65);
            AddSidebarLabel(layout, T("方法：重なる行の加重平均\r\n0も数値として含め、整数へ丸めます。\r\n溝など細かい凹凸は平均化されます。"), 75);
            AddSidebarButton(layout, T("全TXTをY方向に縮小して保存"), ReduceAll);
            AddSidebarLabel(layout, T("入力フォルダー内のREDUCE_日時へ、\r\n元と同じファイル名で保存します。\r\n結果フォルダーを開くとTXTから画像を生成します。"), 85);
            return page;
        }

        private void UpdateReductionInfo()
        {
            var scale = referenceScale;
            reductionInfo.Text = scale == null ? T("フォルダーを選択してください。\r\n有効なWIDTH/HEIGHTが必要です。") : string.Format(
                T("参考データの縮小予定\r\n実寸 {0:0.###} × {1:0.###} mm\r\nX列数 {2}（変更なし）\r\nY行数 {3} → {4}\r\nX間隔 {5:0.######} mm\r\nY間隔 {6:0.######} → {7:0.######} mm\r\n各TXT自身の実寸と行列数で計算します。"),
                scale.WidthMm, scale.HeightMm, scale.Columns, scale.Rows, MatrixReducer.TargetRows(scale),
                scale.PitchX, scale.PitchY, scale.HeightMm / MatrixReducer.TargetRows(scale));
        }

        private void ReduceAll()
        {
            try
            {
                if (!Directory.Exists(folderPath.Text)) throw new InvalidDataException(T("入力フォルダーを指定してください。"));
                var files = FolderFiles.Find(folderPath.Text, ".txt");
                if (files.Length == 0) throw new InvalidDataException(T("Matrix TXTがありません。"));
                string name = "REDUCE_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss");
                string destination = Path.Combine(folderPath.Text, name);
                for (int i = 1; Directory.Exists(destination) || File.Exists(destination); i++)
                    destination = Path.Combine(folderPath.Text, name + "_" + i.ToString("D3"));
                using (var progress = new BatchProgressForm(T("全TXTのY方向縮小"), files.Length,
                    report => MatrixReducer.ExportBatch(files, destination, report)))
                {
                    progress.SetLanguage(Language);
                    progress.ShowDialog(this);
                    if (progress.Error != null) throw progress.Error;
                    CropMessages.Show(this, string.Format(T("縮小が終了しました。\n成功: {0}/{1}\n失敗: {2}\n\n{3}\n詳細: ReduceResult.log"),
                        progress.Result.SuccessCount, files.Length, progress.Result.Errors.Count, destination), T("処理結果"));
                }
            }
            catch (Exception ex) { CropMessages.Show(this, ex.Message, T("縮小エラー")); }
        }
    }
}
