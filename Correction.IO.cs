using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OpenCvSharp;

namespace MatrixBitmapCrop.Correction
{
    // 元の補間計算はCore/NearPeak/VariableStepへ移植し、画面と保存処理を分離する。
    public partial class MatrixCorrectionData : IDisposable
    {
        private string outputFolder;
        private string temperatureProfilePath;
        private Action<int, int, string> progress;
        private readonly StringBuilder log = new StringBuilder();
        private int savedCount;

        private void ReportStatus(string message)
        {
            if (progress != null)
                progress(0, 1, message);
        }

        private static double Number(string value)
        {
            double result;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                || double.IsNaN(result) || double.IsInfinity(result))
                throw new InvalidDataException("数値が不正です: " + value);
            return result;
        }

        private void ReadInput(string path)
        {
            var scale = MatrixScale.Read(path);
            if (scale == null)
                throw new InvalidDataException("WIDTH/HEIGHTが不正です: " + path);
            var data = new MatData { FileName = Path.GetFileName(path), Width = (float)scale.WidthMm, Height = (float)scale.HeightMm };
            var fileMatch = Regex.Match(data.FileName, @"^\d+_([RC])", RegexOptions.IgnoreCase);
            if (!fileMatch.Success)
                throw new InvalidDataException("昇温・降温を識別できません。連番_R / 連番_C が必要です: " + data.FileName);
            data.IsRise = fileMatch.Groups[1].Value.Equals("R", StringComparison.OrdinalIgnoreCase);
            using (var reader = new StreamReader(path, Encoding.Default, true, 65536))
            {
                string header = reader.ReadLine();
                var scans = Regex.Matches(header, @"TempS\s+([^,]+),\s*TempE\s+([^,]+),\s*TimeS\s+([^,]+),\s*TimeE\s+([^,]+)", RegexOptions.IgnoreCase);
                if (scans.Count == 0)
                    throw new InvalidDataException("TempS/TempE/TimeS/TimeEのスキャン情報が必要です: " + data.FileName);
                if (scale.Rows < scans.Count)
                    throw new InvalidDataException("スキャン数より行数が少なくなっています。");
                int block = scale.Rows / scans.Count;
                foreach (Match scan in scans)
                {
                    int index = data.Items.Count;
                    var item = new MatDataItem
                    {
                        StartTemp = Number(scan.Groups[1].Value),
                        EndTemp = Number(scan.Groups[2].Value),
                        StartTime = Number(scan.Groups[3].Value),
                        EndTime = Number(scan.Groups[4].Value),
                        StartRow = index * block,
                        RowCount = index == scans.Count - 1 ? scale.Rows - index * block : block
                    };
                    if (item.EndTime < item.StartTime)
                        throw new InvalidDataException("スキャンの終了時刻が開始時刻より前です。");
                    data.Items.Add(item);
                }
                data.Mat = new Mat(scale.Rows, scale.Columns, MatType.CV_64FC1);
                try
                {
                    for (int y = 0; y < scale.Rows; y++)
                    {
                        string[] cells = reader.ReadLine().Split(',');
                        if (cells.Length != scale.Columns)
                            throw new InvalidDataException("行の列数が不揃いです: " + data.FileName);
                        for (int x = 0; x < cells.Length; x++)
                        {
                            int value;
                            if (!int.TryParse(cells[x], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                                throw new InvalidDataException("Matrixに整数以外の値があります: " + data.FileName);
                            data.Mat.Set(y, x, (double)value);
                        }
                    }
                    if (_matrices.Count > 0 && data.Items.Count != _matrices[0].Items.Count)
                        throw new InvalidDataException("ファイル間でスキャン数が異なります。");
                    _matrices.Add(data);
                }
                catch { data.Mat.Dispose(); throw; }
            }
        }

        private bool SaveCorrectData(MatData data, Mat matrix, float temperature, int count, bool rise)
        {
            string name = string.Format(CultureInfo.InvariantCulture, "{0}_{1}{2:F1}_Measure.txt", count, rise ? "R" : "C", temperature);
            ReportStatus("保存中: " + name);
            string path = Path.Combine(outputFolder, name);
            string temporary = path + ".partial";
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false), 65536))
            {
                // 元ソフトと同じ補正済み温度ヘッダーを出力する。
                var header = new StringBuilder(string.Format(CultureInfo.InvariantCulture,
                    "#WIDTH {0:F1}, HEIGHT {1:F1}, SIZEX 1.0, SIZEY 10.0, SIZEZ 0.05,", data.Width, data.Height));
                foreach (var item in data.Items)
                    header.AppendFormat(CultureInfo.InvariantCulture, " TempS {0:F1}, TempE {0:F1}, TimeS {1:F1}, TimeE {2:F1},", temperature, item.StartTime, item.EndTime);
                writer.WriteLine(header);
                for (int y = 0; y < matrix.Rows; y++)
                {
                    var line = new StringBuilder();
                    for (int x = 0; x < matrix.Cols; x++)
                    {
                        if (x > 0)
                            line.Append(',');
                        double value = matrix.At<double>(y, x);
                        if (double.IsNaN(value) || double.IsInfinity(value) || value < int.MinValue || value > int.MaxValue)
                            throw new InvalidDataException("補正結果が整数の保存範囲を超えています。");
                        line.Append(value.ToString("F0", CultureInfo.InvariantCulture));
                    }
                    writer.WriteLine(line);
                }
            }
            File.Move(temporary, path);
            savedCount++;
            log.AppendLine(name + " : " + matrix.Cols + "列 × " + matrix.Rows + "行");
            return true;
        }

        public BatchResult ConvertFolder(string inputFolder, string destination, string profilePath,
            Action<int, int, string> report, List<TemperatureRange> fineRanges = null, double defaultStep = 10)
        {
            _readFolder = inputFolder;
            temperatureProfilePath = profilePath;
            outputFolder = destination;
            progress = report;
            // .tmpの第3列が計測温度。元ソフトと同じ温度チャンネルを使う。
            var temperatures = new List<double>();
            foreach (string line in File.ReadLines(profilePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var fields = line.Split(',');
                if (fields.Length < 3)
                    throw new InvalidDataException("温度プロファイルは3列以上必要です。");
                temperatures.Add(Number(fields[2]));
            }
            if (temperatures.Count == 0)
                throw new InvalidDataException("温度プロファイルが空です。");
            double peak = temperatures.Max();
            var files = FolderFiles.Find(inputFolder, ".txt")
                .OrderBy(p => Number(Regex.Match(Path.GetFileName(p), @"^\d+").Value)).ToArray();
            if (files.Length < 4)
                throw new InvalidDataException("昇温・降温それぞれ2ファイル以上が必要です。");
            foreach (string file in files)
            {
                ReportStatus("読み込み: " + Path.GetFileName(file));
                ReadInput(file);
            }
            if (_matrices.Count(m => m.IsRise) < 2 || _matrices.Count(m => !m.IsRise) < 2)
                throw new InvalidDataException("昇温・降温それぞれ2ファイル以上が必要です。");
            double[] limits = DetermineTemperatureLimits(_matrices, temperatures);
            double riseStart = limits[0];
            double fallEnd = limits[2];
            Directory.CreateDirectory(destination);
            log.AppendLine("開始・終了温度: 温度履歴と全スキャンの平均温度範囲の共通部分から自動決定");
            log.AppendLine(string.Format(CultureInfo.InvariantCulture, "昇温開始: {0:F1}℃ / 降温終了: {1:F1}℃", riseStart, fallEnd));
            log.AppendLine("ピーク温度: " + peak.ToString(CultureInfo.InvariantCulture));
            log.AppendLine("温度プロファイル: " + profilePath);
            log.AppendLine("通常刻み: " + defaultStep + "℃ / 細分設定: " + (fineRanges == null ? "OFF" : "ON"));
            try
            {
                if (fineRanges != null)
                    foreach (var range in fineRanges)
                        log.AppendLine(string.Format(CultureInfo.InvariantCulture, "細分範囲: {0}～{1}℃ / {2}℃刻み", range.MinTemp, range.MaxTemp, range.Step));
                CreateSelectedCorrectedData(_matrices, riseStart, peak, fallEnd, defaultStep, fineRanges);
                var result = new BatchResult { SuccessCount = savedCount };
                foreach (string file in Directory.GetFiles(inputFolder))
                {
                    string extension = Path.GetExtension(file).ToLowerInvariant();
                    if (extension != ".bmp" && extension != ".tmp" && extension != ".3m")
                        continue;
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
                    result.CopyCount++;
                }
                return result;
            }
            catch (Exception ex) { log.AppendLine("処理中断: " + ex.Message); throw; }
            finally { File.WriteAllText(Path.Combine(destination, "CorrectionResult.log"), log.ToString(), new UTF8Encoding(true)); }
        }

        // FindScanPairと同じスキャン平均温度を基準に、全スキャン共通の補間範囲を求める。
        public static double[] DetermineTemperatureLimits(List<MatData> matrices, List<double> profile)
        {
            if (matrices == null || matrices.Count == 0 || profile == null || profile.Count < 3)
                throw new InvalidDataException("温度範囲の判定に必要なデータがありません。");
            double peak = profile.Max();
            int firstPeak = profile.IndexOf(peak);
            int lastPeak = profile.LastIndexOf(peak);
            double[] limits = new double[3];
            limits[1] = peak;
            for (int branch = 0; branch < 2; branch++)
            {
                bool rise = branch == 0;
                var files = matrices.Where(m => m.IsRise == rise).ToList();
                if (files.Count < 2 || files[0].Items.Count == 0)
                    throw new InvalidDataException("昇温・降温それぞれ2件以上のスキャン情報が必要です。");
                int scans = files[0].Items.Count;
                if (files.Any(m => m.Items.Count != scans))
                    throw new InvalidDataException("スキャン数が一致しません。");
                double lower = rise ? profile.Take(firstPeak + 1).Min() : profile.Skip(lastPeak).Min();
                double upper = peak;
                for (int scan = 0; scan < scans; scan++)
                {
                    lower = Math.Max(lower, files.Min(m => m.Items[scan].AverageTemp));
                    upper = Math.Min(upper, files.Max(m => m.Items[scan].AverageTemp));
                }
                // 保存温度は小数1桁。範囲外へ出ない方向に丸める。
                lower = Math.Ceiling(lower * 10 - 0.000001) / 10;
                if (lower > upper + 0.000001 || lower >= peak)
                    throw new InvalidDataException((rise ? "昇温" : "降温") + "の温度履歴とMatrixに共通する補間範囲がありません。");
                limits[rise ? 0 : 2] = lower;
            }
            return limits;
        }

        public void Dispose()
        {
            foreach (var data in _matrices)
                data.Mat.Dispose();
            _matrices.Clear();
        }
    }
}
