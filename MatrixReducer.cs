using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace MatrixBitmapCrop
{
    public static class MatrixReducer
    {
        // 実寸を維持し、Y間隔がX間隔に最も近くなる整数行数へ縮小する。
        public static int TargetRows(MatrixScale scale)
        {
            return (int)Math.Max(1, Math.Min(scale.Rows,
                Math.Round(scale.HeightMm / scale.PitchX, MidpointRounding.AwayFromZero)));
        }

        public static void Reduce(string input, string output)
        {
            if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("出力先は元ファイルと別の場所を指定してください。");
            var scale = MatrixScale.Read(input);
            if (scale == null)
                throw new InvalidDataException("有効なWIDTH/HEIGHTヘッダーが必要です。");
            int target = TargetRows(scale);
            string temporary = output + ".partial";
            try
            {
                using (var reader = new StreamReader(input, Encoding.Default, true, 65536))
                using (var writer = new StreamWriter(temporary, false, Encoding.Default, 65536))
                {
                    // WIDTH/HEIGHTを含めヘッダーは保持する。X列数も変更しない。
                    writer.WriteLine(reader.ReadLine());
                    var sums = new double[scale.Columns];
                    int outputRow = 0;
                    for (int y = 0; y < scale.Rows; y++)
                    {
                        string line = reader.ReadLine();
                        string[] cells = (line ?? "").Split(',');
                        if (cells.Length != scale.Columns)
                            throw new InvalidDataException("行の列数が一致しません: " + (y + 1));
                        var values = new int[scale.Columns];
                        for (int x = 0; x < values.Length; x++)
                            if (!int.TryParse(cells[x], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[x]))
                                throw new InvalidDataException(string.Format("整数ではありません: 行{0} 列{1}", y + 1, x + 1));
                        if (target == scale.Rows)
                        {
                            writer.WriteLine(line);
                            continue;
                        }

                        // 区間の重なりを整数で計算して加重平均する。端の行も漏れなく反映する。
                        long position = (long)y * target;
                        long end = (long)(y + 1) * target;
                        while (position < end)
                        {
                            long boundary = (long)(outputRow + 1) * scale.Rows;
                            long next = Math.Min(end, boundary);
                            long weight = next - position;
                            for (int x = 0; x < sums.Length; x++)
                                sums[x] += (double)values[x] * weight;
                            position = next;
                            if (position == boundary)
                            {
                                var row = new StringBuilder();
                                for (int x = 0; x < sums.Length; x++)
                                {
                                    if (x > 0) row.Append(',');
                                    row.Append(Math.Round(sums[x] / scale.Rows, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture));
                                    sums[x] = 0;
                                }
                                writer.WriteLine(row);
                                outputRow++;
                            }
                        }
                    }
                }
                File.Move(temporary, output);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public static BatchResult ExportBatch(string[] files, string destination, Action<int, int, string> report)
        {
            Directory.CreateDirectory(destination);
            var result = new BatchResult();
            var log = new StringBuilder("Y方向縮小: 面積加重平均、0も数値として平均、整数丸め。WIDTH/HEIGHTと列数は保持。\r\n");
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                if (report != null) report(i, files.Length, name);
                try
                {
                    var scale = MatrixScale.Read(files[i]);
                    if (scale == null) throw new InvalidDataException("有効なWIDTH/HEIGHTがありません。");
                    Reduce(files[i], Path.Combine(destination, name));
                    result.SuccessCount++;
                    log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}: 列={1}, 行={2} → {3}, X間隔={4}mm, Y間隔={5} → {6}mm",
                        name, scale.Columns, scale.Rows, TargetRows(scale), scale.PitchX, scale.PitchY, scale.HeightMm / TargetRows(scale)));
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException || ex is UnauthorizedAccessException)
                {
                    result.Errors.Add(name + ": " + ex.Message);
                }
            }
            log.AppendLine("成功: " + result.SuccessCount + " / " + files.Length);
            log.AppendLine(string.Join("\r\n", result.Errors));
            File.WriteAllText(Path.Combine(destination, "ReduceResult.log"), log.ToString(), new UTF8Encoding(true));
            if (report != null) report(files.Length, files.Length, "縮小完了");
            return result;
        }
    }
}
