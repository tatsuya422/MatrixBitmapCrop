using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

namespace MatrixBitmapCrop
{
    public static class MaskProcessor
    {
        // 一括マスク補間。寸法・ヘッダーを保持し、0点補正OFFでは対象外の数値文字列も保持する。
        public static BatchResult ExportBatch(string[] files, Bitmap reference, MaskDefinition definition,
            bool ignoreZero, string destination, Action<int, int, string> report, bool zeroAdjust = false,
            bool flatEdges = false, int flatPercent = 100)
        {
            ValidateFlatPercent(flatPercent);
            definition.Validate();
            bool[] mask = definition.Rasterize();
            if (Array.IndexOf(mask, true) < 0)
                throw new InvalidDataException("画像内にマスク範囲がありません。");
            Directory.CreateDirectory(destination);
            definition.Save(Path.Combine(destination, "MaskDefinition.xml"));
            SaveReference(reference, definition, destination);
            var result = new BatchResult();
            var details = new StringBuilder();
            details.AppendLine("処理: 周囲の傾斜面 + 多段階の滑らかな残差補間（推定値を整数に丸める）");
            details.AppendLine("0を参照から除外: " + ignoreZero);
            details.AppendLine("補間後の最小値を0に補正: " + zeroAdjust);
            details.AppendLine("画像端の傾斜調整: " + flatEdges);
            details.AppendLine("画像端の平坦化率: " + (flatEdges ? flatPercent : 0) + "％（0％=反り補間、100％=近傍値の延長）");
            details.AppendLine("画像端: 反り補間と近傍値の延長を平坦化率で混合。延長値は左右端を同じ行、上下端を同じ列から参照（四隅は横優先）。異常値を除外し、参照できない箇所は反り補間を使用。");

            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                if (report != null)
                    report(i, files.Length, name + " : 読み込み・補間中");
                try
                {
                    var scale = definition.CoordinatesInMillimeters ? MatrixScale.Read(files[i]) : null;
                    if (definition.CoordinatesInMillimeters && scale == null)
                        throw new InvalidDataException("mm指定のマスクにはWIDTH/HEIGHTヘッダーが必要です。");
                    int columns = scale == null ? definition.ImageWidth : scale.Columns;
                    int rows = scale == null ? definition.ImageHeight : scale.Rows;
                    bool[] fileMask = scale == null ? mask : definition.Rasterize(columns, rows, scale.WidthMm, scale.HeightMm);
                    double minimum;
                    int count = ProcessFileCore(files[i], columns, rows, fileMask,
                        ignoreZero, Path.Combine(destination, name), zeroAdjust, flatEdges, flatPercent, out minimum);
                    result.SuccessCount++;
                    details.AppendLine(name + " : 補間 " + count + "画素");
                    if (zeroAdjust)
                        details.AppendLine("  全セルから差し引いた最小値: " + minimum.ToString("0", CultureInfo.InvariantCulture));
                    if (scale != null)
                        details.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  WIDTH={0}mm HEIGHT={1}mm Columns={2} Rows={3} PitchX={4}mm PitchY={5}mm",
                        scale.WidthMm, scale.HeightMm, scale.Columns, scale.Rows, scale.PitchX, scale.PitchY));
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException || ex is UnauthorizedAccessException)
                {
                    result.Errors.Add(name + ": " + ex.Message);
                }
            }

            // 付随する計測条件ファイルも同じ出力フォルダーへコピーする。
            if (files.Length > 0)
                foreach (string path in Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(files[0]))))
                {
                    string extension = Path.GetExtension(path);
                    if (!string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(extension, ".3m", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (report != null)
                        report(files.Length, files.Length, "コピー中: " + Path.GetFileName(path));
                    try
                    {
                        File.Copy(path, Path.Combine(destination, Path.GetFileName(path)), false);
                        result.CopyCount++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        result.CopyErrors.Add(Path.GetFileName(path) + ": " + ex.Message);
                    }
                }
            details.AppendFormat("成功: {0}/{1}\r\nコピー成功: {2}\r\n", result.SuccessCount, files.Length, result.CopyCount);
            details.AppendLine(string.Join("\r\n", result.Errors));
            details.AppendLine(string.Join("\r\n", result.CopyErrors));
            File.WriteAllText(Path.Combine(destination, "MaskResult.log"), details.ToString(), new UTF8Encoding(false));
            if (report != null)
                report(files.Length, files.Length, "マスク補間完了");
            return result;
        }

        public static int ProcessFile(string input, int width, int height, bool[] sharedMask, bool ignoreZero,
            string output, bool zeroAdjust = false, bool flatEdges = false, int flatPercent = 100)
        {
            ValidateFlatPercent(flatPercent);
            double minimum;
            return ProcessFileCore(input, width, height, sharedMask, ignoreZero, output, zeroAdjust, flatEdges, flatPercent, out minimum);
        }

        private static void ValidateFlatPercent(int flatPercent)
        {
            if (flatPercent < 0 || flatPercent > 100)
                throw new ArgumentOutOfRangeException(nameof(flatPercent), "平坦化率は0～100％で指定してください。");
        }

        private static int ProcessFileCore(string input, int width, int height, bool[] sharedMask, bool ignoreZero,
            string output, bool zeroAdjust, bool flatEdges, int flatPercent, out double minimum)
        {
            minimum = 0;
            if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("出力先は元ファイルと別の場所を指定してください。");
            if (sharedMask.Length != checked(width * height))
                throw new ArgumentException("マスク寸法が不正です。");
            var rows = new List<int[]>();
            var validRows = new List<bool[]>();
            string header;
            long total = 0;
            using (var reader = new StreamReader(input, Encoding.Default, true, 65536))
            {
                header = reader.ReadLine();
                if (header == null)
                    throw new InvalidDataException("Matrixヘッダーがありません。");
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] cells = line.Split(',');
                    total += cells.Length;
                    if (total > 100000000)
                        throw new InvalidDataException("Matrixが1億要素を超えています。");
                    var data = new int[cells.Length];
                    var valid = new bool[cells.Length];
                    for (int x = 0; x < cells.Length; x++)
                        valid[x] = int.TryParse(cells[x], NumberStyles.Integer, CultureInfo.InvariantCulture, out data[x]);
                    rows.Add(data);
                    validRows.Add(valid);
                }
            }
            if (rows.Count == 0)
                throw new InvalidDataException("Matrixデータがありません。");

            // 元画像やMatrixの端からはみ出す形状は、存在する画素だけへ適用する。
            bool[] mask = (bool[])sharedMask.Clone();
            bool[] changedRows = new bool[height];
            int count = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    if (!mask[index])
                        continue;
                    if (y >= rows.Count || x >= rows[y].Length)
                    {
                        mask[index] = false;
                        continue;
                    }
                    count++;
                    changedRows[y] = true;
                }
            if (count == 0)
                throw new InvalidDataException("Matrix内にマスク対象がありません。");
            double[] filled = MaskInterpolator.Fill(rows.ToArray(), validRows.ToArray(), mask, width, height,
                ignoreZero, flatEdges, flatPercent);

            // 補間して整数に丸めた結果の全セルから最小値を求め、ファイルごとに0点をそろえる。
            // 参照値から0を除外する設定は、この最小値判定には適用しない。
            if (zeroAdjust)
            {
                minimum = double.PositiveInfinity;
                double maximum = double.NegativeInfinity;
                for (int y = 0; y < rows.Count; y++)
                    for (int x = 0; x < rows[y].Length; x++)
                    {
                        bool masked = y < height && x < width && mask[y * width + x];
                        if (!masked && !validRows[y][x])
                            throw new InvalidDataException("0点補正には全セルが整数である必要があります。行" + (y + 1) + " 列" + (x + 1));
                        double value = masked ? Math.Round(filled[y * width + x], MidpointRounding.AwayFromZero) : rows[y][x];
                        if (double.IsNaN(value) || double.IsInfinity(value))
                            throw new InvalidDataException("補間結果に有効な数値がありません。");
                        minimum = Math.Min(minimum, value);
                        maximum = Math.Max(maximum, value);
                    }
                if (maximum - minimum > int.MaxValue)
                    throw new InvalidDataException("0点補正後の値が整数の保存範囲を超えます。");
            }

            // 元の文字列を再読込する。0点補正ONは全セル、OFFはマスク内だけを書き換える。
            // 書き込みが成功するまで.partialとし、既存ファイルは上書きしない。
            string temporary = output + ".partial";
            using (var reader = new StreamReader(input, Encoding.Default, true, 65536))
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false), 65536))
            {
                writer.WriteLine(reader.ReadLine());
                string line;
                int y = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if (zeroAdjust || (y < height && changedRows[y]))
                    {
                        string[] cells = line.Split(',');
                        for (int x = 0; x < cells.Length; x++)
                            if (zeroAdjust)
                            {
                                double value = y < height && x < width && mask[y * width + x]
                                    ? Math.Round(filled[y * width + x], MidpointRounding.AwayFromZero) : rows[y][x];
                                cells[x] = (value - minimum).ToString("0", CultureInfo.InvariantCulture);
                            }
                            else if (x < width && mask[y * width + x])
                                cells[x] = Math.Round(filled[y * width + x], MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
                        writer.WriteLine(string.Join(",", cells));
                    }
                    else
                        writer.WriteLine(line);
                    y++;
                }
            }
            File.Move(temporary, output);
            return count;
        }

        private static void SaveReference(Bitmap source, MaskDefinition definition, string destination)
        {
            // 元画像と領域図を分けて残す。領域図は補間後の計測画像ではない。
            source.Save(Path.Combine(destination, "MaskReference.bmp"), ImageFormat.Bmp);
            using (var overlay = new Bitmap(source))
            {
                using (var graphics = Graphics.FromImage(overlay))
                using (var brush = new SolidBrush(Color.FromArgb(80, Color.Yellow)))
                using (var pen = new Pen(Color.Yellow, 2))
                    foreach (var shape in definition.Shapes)
                        using (var path = shape.CreatePath())
                        {
                            if (definition.CoordinatesInMillimeters)
                                using (var transform = new System.Drawing.Drawing2D.Matrix())
                                {
                                    transform.Scale((float)(source.Width / definition.PhysicalWidthMm), (float)(source.Height / definition.PhysicalHeightMm));
                                    path.Transform(transform);
                                }
                            graphics.FillPath(brush, path);
                            graphics.DrawPath(pen, path);
                        }
                overlay.Save(Path.Combine(destination, "MaskRegions.png"), ImageFormat.Png);
            }
        }
    }
}
