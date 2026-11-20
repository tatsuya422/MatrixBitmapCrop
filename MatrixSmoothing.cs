using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace MatrixBitmapCrop
{
    internal enum SmoothingMethod { Median, Gaussian }

    internal static class MatrixSmoothing
    {
        internal const int MinimumSize = 3;
        internal const int MaximumSize = 21;
        private const int BufferSize = 65536;
        private static readonly Encoding HeaderBytes = Encoding.GetEncoding(28591);

        private sealed class SelectedRow
        {
            internal readonly int Start;
            internal readonly int End;
            internal int[] Values;

            internal SelectedRow(int start, int end)
            {
                Start = start;
                End = end;
            }
        }

        internal static BatchResult ExportBatch(string[] files, string[] attachments, RotatedSelection area, Size imageSize,
            MatrixScale referenceScale, SmoothingMethod method, int size, string destination, string logName, Action<int, int, string> report)
        {
            if (size < MinimumSize || size > MaximumSize || size % 2 == 0)
                throw new ArgumentOutOfRangeException(nameof(size));

            Directory.CreateDirectory(destination);
            var result = new BatchResult();
            int total = files.Length + attachments.Length;
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                report?.Invoke(i, total, name);
                try
                {
                    var rows = SelectRows(files[i], area, imageSize, referenceScale);
                    Save(files[i], Path.Combine(destination, name), rows, method, size);
                    result.SuccessCount++;
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                {
                    result.Errors.Add(name + ": " + ex.Message);
                }
            }

            FolderFiles.CopyAttachments(attachments, destination, result, files.Length, report);
            using (var log = new FileStream(Path.Combine(destination, logName), FileMode.CreateNew))
            using (var writer = new StreamWriter(log, new UTF8Encoding(true)))
            {
                writer.WriteLine("{0} {1} × {1}", method, size);
                writer.WriteLine("{0} / {1}", result.SuccessCount, files.Length);
                foreach (string error in result.Errors)
                    writer.WriteLine(error);
                writer.WriteLine("コピー成功: {0} / {1}", result.CopyCount, attachments.Length);
                foreach (string error in result.CopyErrors)
                    writer.WriteLine(error);
            }
            report?.Invoke(total, total, "平滑化完了");
            return result;
        }

        private static SelectedRow[] SelectRows(string input, RotatedSelection area, Size imageSize, MatrixScale referenceScale)
        {
            int width = imageSize.Width, height = imageSize.Height;
            double scaleX = 1, scaleY = 1;
            if (referenceScale != null)
            {
                var target = MatrixScale.Read(input);
                if (target == null)
                    throw new InvalidDataException("平滑化には対象TXTの実寸ヘッダーが必要です。");
                width = target.Columns;
                height = target.Rows;
                scaleX = referenceScale.PitchX / target.PitchX;
                scaleY = referenceScale.PitchY / target.PitchY;
            }

            if ((long)width * height > 100000000)
                throw new InvalidDataException("Matrixが1億要素を超えています。");

            // 表示される枠の辺を対象Matrixのセル座標へ写し、セル中心の包含を判定する。
            double x0 = area.Origin.X * scaleX, y0 = area.Origin.Y * scaleY;
            double ux = ((double)area.AxisEnd.X - area.Origin.X) * scaleX;
            double uy = ((double)area.AxisEnd.Y - area.Origin.Y) * scaleY;
            double vx = area.Vx * area.Height * scaleX, vy = area.Vy * area.Height * scaleY;
            double determinant = ux * vy - uy * vx;

            int left = (int)Math.Max(0, Math.Min(width, Math.Floor(x0 + Math.Min(0, ux) + Math.Min(0, vx))));
            int right = (int)Math.Max(0, Math.Min(width, Math.Ceiling(x0 + Math.Max(0, ux) + Math.Max(0, vx))));
            int top = (int)Math.Max(0, Math.Min(height, Math.Floor(y0 + Math.Min(0, uy) + Math.Min(0, vy))));
            int bottom = (int)Math.Max(0, Math.Min(height, Math.Ceiling(y0 + Math.Max(0, uy) + Math.Max(0, vy))));
            var rows = new SelectedRow[bottom];
            bool anySelected = false;
            for (int y = top; y < bottom; y++)
            {
                int start = right, end = right;
                double dy = y + .5 - y0;
                for (int x = left; x < right; x++)
                {
                    double dx = x + .5 - x0;
                    double u = (dx * vy - dy * vx) / determinant;
                    double v = (ux * dy - uy * dx) / determinant;
                    if (u >= 0 && u <= 1 && v >= 0 && v <= 1)
                    {
                        start = Math.Min(start, x);
                        end = x + 1;
                    }
                }
                if (end > start)
                {
                    rows[y] = new SelectedRow(start, end);
                    anySelected = true;
                }
            }
            if (!anySelected)
                throw new InvalidDataException("赤枠内に平滑化できるセルがありません。");
            return rows;
        }

        private static void Save(string input, string output, SelectedRow[] rows, SmoothingMethod method, int size)
        {
            string temporary = output + "." + Path.GetRandomFileName() + ".partial";
            using (var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (var reader = new StreamReader(stream, HeaderBytes, true, BufferSize, true))
                    ReadValues(reader, rows);

                stream.Position = 0;
                try
                {
                    using (var reader = new StreamReader(stream, HeaderBytes, true, BufferSize, true))
                    {
                        string header = reader.ReadLine();
                        using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                        using (var writer = new StreamWriter(target, reader.CurrentEncoding, BufferSize))
                        {
                            writer.WriteLine(header);
                            WriteRows(reader, writer, rows, method, size);
                        }
                    }
                    File.Move(temporary, output);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        private static void ReadValues(StreamReader reader, SelectedRow[] rows)
        {
            if (string.IsNullOrWhiteSpace(reader.ReadLine()))
                throw new InvalidDataException("Matrixヘッダーがありません。");

            for (int y = 0; y < rows.Length; y++)
            {
                string line = reader.ReadLine();
                var row = rows[y];
                if (row == null)
                    continue;
                string[] cells = line?.Split(',');
                if (cells == null || cells.Length < row.End)
                    throw new InvalidDataException(string.Format("赤枠内のMatrixデータが不足しています: 行{0}", y + 1));

                row.Values = new int[row.End - row.Start];
                for (int x = row.Start; x < row.End; x++)
                {
                    if (!int.TryParse(cells[x], NumberStyles.Integer, CultureInfo.InvariantCulture, out row.Values[x - row.Start]))
                        throw new InvalidDataException(string.Format("整数ではありません: 行{0} 列{1}", y + 1, x + 1));
                }
            }
        }

        private static void WriteRows(StreamReader reader, StreamWriter writer, SelectedRow[] rows, SmoothingMethod method, int size)
        {
            int radius = size / 2;
            double[] weights = method == SmoothingMethod.Gaussian ? GaussianWeights(radius) : null;
            int[] window = new int[size * size];
            string line;
            for (int y = 0; (line = reader.ReadLine()) != null; y++)
            {
                var row = y < rows.Length ? rows[y] : null;
                if (row == null)
                {
                    writer.WriteLine(line);
                    continue;
                }
                string[] cells = line.Split(',');
                for (int x = row.Start; x < row.End; x++)
                {
                    int value = SmoothCell(rows, x, y, radius, window, weights);
                    if (value != row.Values[x - row.Start])
                        cells[x] = value.ToString(CultureInfo.InvariantCulture);
                }
                writer.WriteLine(string.Join(",", cells));
            }
        }

        private static double[] GaussianWeights(int radius)
        {
            var weights = new double[radius + 1];
            double sigma = Math.Max(.8, radius / 2.0);
            for (int i = 0; i <= radius; i++)
                weights[i] = Math.Exp(-i * i / (2 * sigma * sigma));
            return weights;
        }

        private static int SmoothCell(SelectedRow[] rows, int x, int y, int radius, int[] window, double[] weights)
        {
            int count = 0;
            double weightedSum = 0, totalWeight = 0;
            for (int ny = Math.Max(0, y - radius); ny <= Math.Min(rows.Length - 1, y + radius); ny++)
            {
                var row = rows[ny];
                if (row == null)
                    continue;
                int left = Math.Max(row.Start, x - radius), right = Math.Min(row.End, x + radius + 1);
                for (int nx = left; nx < right; nx++)
                {
                    int value = row.Values[nx - row.Start];
                    if (weights == null)
                    {
                        window[count++] = value;
                    }
                    else
                    {
                        double weight = weights[Math.Abs(nx - x)] * weights[Math.Abs(ny - y)];
                        weightedSum += value * weight;
                        totalWeight += weight;
                    }
                }
            }
            if (weights != null)
                return (int)Math.Round(weightedSum / totalWeight, MidpointRounding.AwayFromZero);

            Array.Sort(window, 0, count);
            int middle = count / 2;
            if (count % 2 == 1)
                return window[middle];
            return (int)Math.Round(((long)window[middle - 1] + window[middle]) / 2.0, MidpointRounding.AwayFromZero);
        }
    }
}
