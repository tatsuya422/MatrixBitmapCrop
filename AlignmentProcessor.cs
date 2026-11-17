using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OpenCvSharp;

namespace MatrixBitmapCrop
{
    public static class AlignmentProcessor
    {
        // 基準と対象は同じ格子で比較する。行列数の違いは無断で拡大縮小しない。
        private static List<int[]> Read(string path, bool zeroAdjust, out string header)
        {
            var rows = new List<int[]>();
            int width = 0, minimum = int.MaxValue;
            using (var reader = new StreamReader(path, Encoding.Default, true, 65536))
            {
                header = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(header))
                    throw new InvalidDataException("ヘッダーがありません。");
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var cells = line.Split(',');
                    if (rows.Count == 0)
                        width = cells.Length;
                    if (cells.Length != width)
                        throw new InvalidDataException("Matrixの列数が不揃いです。");
                    if ((long)width * (rows.Count + 1) > 100000000)
                        throw new InvalidDataException("Matrixが1億要素を超えています。");
                    var row = new int[width];
                    for (int x = 0; x < width; x++)
                    {
                        if (!int.TryParse(cells[x], NumberStyles.Integer, CultureInfo.InvariantCulture, out row[x]))
                            throw new InvalidDataException("Matrixに整数以外の値があります。");
                        minimum = Math.Min(minimum, row[x]);
                    }
                    rows.Add(row);
                }
            }
            if (rows.Count < 8 || width < 8)
                throw new InvalidDataException("位置合わせには8列・8行以上必要です。");
            // StartNormalizeと同じ最小値を0とする高さ補正。XYの位置合わせとは別の設定。
            if (zeroAdjust)
                foreach (var row in rows)
                    for (int x = 0; x < width; x++)
                    {
                        long value = (long)row[x] - minimum;
                        if (value > int.MaxValue)
                            throw new InvalidDataException("最小値補正後の値が整数の上限を超えます。");
                        row[x] = (int)value;
                    }
            return rows;
        }

        private static void Save(string path, string header, List<int[]> data)
        {
            string partial = path + ".partial";
            try
            {
                using (var writer = new StreamWriter(partial, false, new UTF8Encoding(false), 65536))
                {
                    // 元の実寸・スキャン温度・時刻情報をすべて保持する。
                    writer.WriteLine(header);
                    foreach (var row in data)
                    {
                        var line = new StringBuilder();
                        for (int x = 0; x < row.Length; x++)
                        {
                            if (x > 0)
                                line.Append(',');
                            line.Append(row[x].ToString(CultureInfo.InvariantCulture));
                        }
                        writer.WriteLine(line);
                    }
                }
                File.Move(partial, path);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }

        public static BatchResult ExportBatch(string[] files, string referencePath, bool zeroAdjust,
            string destination, Action<int, int, string> report)
        {
            string baseHeader;
            var reference = Read(referencePath, zeroAdjust, out baseHeader);
            Directory.CreateDirectory(destination);
            var result = new BatchResult();
            var log = new StringBuilder();
            log.AppendLine("基準: " + referencePath);
            log.AppendLine("最小値を0に補正: " + zeroAdjust);
            log.AppendLine("1/4縮小→中央50%で精密合わせ。移動を合成して1回の線形補間、範囲外は0。");
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                if (report != null)
                    report(i, files.Length, name);
                try
                {
                    string header;
                    var target = Read(files[i], zeroAdjust, out header);
                    if (target.Count != reference.Count || target[0].Length != reference[0].Length)
                        throw new InvalidDataException("基準と行列数が一致しません。");
                    double dx = 0, dy = 0, response = 1;
                    List<int[]> aligned;
                    if (string.Equals(Path.GetFullPath(files[i]), Path.GetFullPath(referencePath), StringComparison.OrdinalIgnoreCase))
                        aligned = target;
                    else
                        aligned = Alignment.MatrixAlignmentTwoStep.AnalyzeMatrixAlignment(reference, target, out dx, out dy, out response);
                    if (aligned == null || double.IsNaN(dx) || double.IsInfinity(dx) || double.IsNaN(dy) || double.IsInfinity(dy)
                        || double.IsNaN(response) || double.IsInfinity(response))
                        throw new InvalidDataException("位置ずれを計算できませんでした。");
                    Save(Path.Combine(destination, name), header, aligned);
                    result.SuccessCount++;
                    log.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}: 移動X={1:F4}px Y={2:F4}px 相関応答={3:F4}", name, dx, dy, response));
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException
                    || ex is UnauthorizedAccessException || ex is OpenCVException)
                {
                    result.Errors.Add(name + ": " + ex.Message);
                }
            }
            if (result.SuccessCount > 0)
            {
                // 基準データに対応する画像を作成する。元BMPは基準と一致する保証がないため生成する。
                string referenceOutput = Path.Combine(destination, Path.GetFileName(referencePath));
                if (File.Exists(referenceOutput))
                    using (var image = MatrixPreview.Create(referenceOutput))
                        image.Save(Path.Combine(destination, "AlignmentReference.bmp"), System.Drawing.Imaging.ImageFormat.Bmp);
                foreach (string path in Directory.GetFiles(Path.GetDirectoryName(referencePath)))
                {
                    string ext = Path.GetExtension(path);
                    if (!ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".3m", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        File.Copy(path, Path.Combine(destination, Path.GetFileName(path)), false);
                        result.CopyCount++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { result.CopyErrors.Add(Path.GetFileName(path) + ": " + ex.Message); }
                }
            }
            log.AppendLine("TXT成功: " + result.SuccessCount + " / " + files.Length);
            log.AppendLine(string.Join("\r\n", result.Errors));
            log.AppendLine(string.Join("\r\n", result.CopyErrors));
            File.WriteAllText(Path.Combine(destination, "AlignmentResult.log"), log.ToString(), new UTF8Encoding(true));
            if (report != null)
                report(files.Length, files.Length, "位置合わせ完了");
            return result;
        }
    }
}
