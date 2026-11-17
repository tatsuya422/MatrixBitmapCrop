using System;
using System.Drawing;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    public sealed partial class MainForm
    {
        private readonly ComboBox correctionProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 650 };

        private readonly NumericUpDown normalTemperatureStep = new NumericUpDown { Minimum = 1M, Maximum = 1000M, DecimalPlaces = 0, Increment = 1M, Value = 10M };
        private readonly CheckBox useFineRanges = new CheckBox { Text = "指定範囲の温度刻みを有効にする", Checked = false, Height = 28 };
        private readonly DataGridView fineRangesGrid = new DataGridView
        {
            Height = 160,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false
        };

        // 編集値は実行直前に検証し、ワーカースレッドへ独立した設定として渡す。
        private List<Correction.TemperatureRange> ReadFineRanges()
        {
            if (!useFineRanges.Checked)
                return null;
            fineRangesGrid.EndEdit();
            var ranges = new List<Correction.TemperatureRange>();
            foreach (DataGridViewRow row in fineRangesGrid.Rows)
            {
                var values = new double[3];
                for (int i = 0; i < 3; i++)
                    if (!double.TryParse(Convert.ToString(row.Cells[i].Value), NumberStyles.Float,
                        CultureInfo.CurrentCulture, out values[i]) || double.IsNaN(values[i]) || double.IsInfinity(values[i]))
                        throw new InvalidDataException(T("温度範囲の") + (row.Index + 1) + T("行目に数値を入力してください。"));
                if (values[0] >= values[1] || values[2] < 1 || values[2] != Math.Truncate(values[2])
                    || Math.Abs(values[0] * 10 - Math.Round(values[0] * 10)) > 0.000001
                    || Math.Abs(values[1] * 10 - Math.Round(values[1] * 10)) > 0.000001)
                    throw new InvalidDataException(T("下限は上限より小さくし、数値は小数1桁まで、刻みは1℃以上の整数にしてください。"));
                ranges.Add(new Correction.TemperatureRange { MinTemp = values[0], MaxTemp = values[1], Step = values[2] });
            }
            return ranges;
        }

        private TabPage BuildCorrectionTab()
        {
            var page = new TabPage(T("温度補正")) { BackColor = Color.Azure, AutoScroll = true };
            var layout = CreateSidebarLayout(page);
            AddSidebarButton(layout, T("入力フォルダーを開く"), () => ChooseFolder(this, EventArgs.Empty));
            AddSidebarLabel(layout, T("温度プロファイル（TMP・第3列）"), 35);
            AddSidebarControl(layout, correctionProfile);
            AddSidebarLabel(layout, T("通常の温度刻み（℃）"));
            AddSidebarControl(layout, normalTemperatureStep);
            AddSidebarControl(layout, useFineRanges);
            fineRangesGrid.Columns.Add("MinTemp", T("下限℃"));
            fineRangesGrid.Columns.Add("MaxTemp", T("上限℃"));
            fineRangesGrid.Columns.Add("Step", T("刻み℃"));
            fineRangesGrid.Rows.Add(150.0, 180.0, 2.0);
            fineRangesGrid.Rows.Add(230.0, 244.0, 1.0);
            AddSidebarControl(layout, fineRangesGrid);
            AddSidebarButton(layout, T("温度範囲を追加"), () => fineRangesGrid.Rows.Add(150.0, 180.0, 2.0));
            AddSidebarButton(layout, T("選択した温度範囲を削除"), () =>
            {
                if (fineRangesGrid.CurrentRow != null)
                    fineRangesGrid.Rows.Remove(fineRangesGrid.CurrentRow);
            });

            useFineRanges.CheckedChanged += (s, e) => fineRangesGrid.Enabled = useFineRanges.Checked;
            AddSidebarLabel(layout, T("刻みは整数。10℃以上は十の位、10℃未満は整数温度にそろえます。重複範囲は小さい刻みを使用。通常設定より細かい刻みが適用されます。OFF時は通常の刻みのみ。ピーク温度は別途保存します。"), 95);
            AddSidebarButton(layout, T("Make Correct（温度補正を実行）"), MakeCorrect);
            AddSidebarLabel(layout, T("CORRECT_日時フォルダーへ新しい温度のTXTを保存します。元データは保持します。BMP・TMP・3mもコピーします。"), 85);
            return page;
        }

        private void UpdateCorrectionProfiles()
        {
            correctionProfile.Items.Clear();
            if (!Directory.Exists(folderPath.Text))
                return;
            correctionProfile.Items.AddRange(FolderFiles.Find(folderPath.Text, ".tmp"));
            if (correctionProfile.Items.Count > 0)
            {
                correctionProfile.SelectedIndex = 0;
                string preferred = correctionProfile.Items.Cast<string>().FirstOrDefault(p => Path.GetFileName(p).Equals("tempData.tmp", StringComparison.OrdinalIgnoreCase));
                if (preferred != null)
                    correctionProfile.SelectedItem = preferred;
            }
        }

        private void MakeCorrect()
        {
            try
            {
                if (!Directory.Exists(folderPath.Text) || correctionProfile.SelectedItem == null)
                    throw new InvalidDataException(T("TXTと温度プロファイルTMPのあるフォルダーを指定してください。"));
                var fineRanges = ReadFineRanges();
                double normalStep = (double)normalTemperatureStep.Value;
                string input = folderPath.Text;
                string profile = correctionProfile.Text;
                string name = "CORRECT_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss");
                string destination = Path.Combine(input, name);
                for (int i = 1; Directory.Exists(destination) || File.Exists(destination); i++)
                    destination = Path.Combine(input, name + "_" + i.ToString("D3"));
                using (var engine = new Correction.MatrixCorrectionData())
                using (var progress = new BatchProgressForm(T("Make Correct：温度補正"), 1,
                    report => engine.ConvertFolder(input, destination, profile, report, fineRanges, normalStep)))
                {
                    progress.SetLanguage(Language);
                    progress.ShowDialog(this);
                    if (progress.Error != null)
                        throw progress.Error;
                    CropMessages.Show(this, string.Format(T("温度補正が完了しました。\n作成TXT: {0}\n付随ファイル: {1}\n\n{2}"),
                        progress.Result.SuccessCount, progress.Result.CopyCount, destination), "Make Correct");
                }
            }
            catch (Exception ex) { CropMessages.Show(this, ex.Message, T("温度補正エラー")); }
        }
    }
}
