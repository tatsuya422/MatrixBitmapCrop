using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace MatrixBitmapCrop
{
    internal static class MatrixZeroBaseline
    {
        private const int BufferSize = 65536;

        // BOMなしのヘッダーをバイト単位で往復させ、UTF-8や旧来のコードページの内容を保持する。
        private static readonly Encoding HeaderBytes = Encoding.GetEncoding(28591);

        internal static BatchResult ExportBatch(string[] files, string[] attachments, string destination,
            string logName, int targetMinimumUm, Action<int, int, string> report)
        {
            Directory.CreateDirectory(destination);
            var result = new BatchResult();
            var log = new StringBuilder();
            log.AppendLine("補正後の最小値: " + targetMinimumUm.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " μm");
            int total = files.Length + attachments.Length;
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                report?.Invoke(i, total, name);
                try
                {
                    int minimum = Save(files[i], Path.Combine(destination, name), targetMinimumUm);
                    result.SuccessCount++;
                    log.AppendFormat(CultureInfo.InvariantCulture, "{0}: 補正前の最小値 = {1} μm, 補正量 = {2:+0;-0;0} μm\r\n",
                        name, minimum, (long)targetMinimumUm - minimum);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                {
                    result.Errors.Add(name + ": " + ex.Message);
                }
            }
            FolderFiles.CopyAttachments(attachments, destination, result, files.Length, report);
            log.AppendLine("成功: " + result.SuccessCount + " / " + files.Length);
            log.AppendLine(string.Join("\r\n", result.Errors));
            log.AppendLine("コピー成功: " + result.CopyCount + " / " + attachments.Length);
            log.AppendLine(string.Join("\r\n", result.CopyErrors));
            using (var stream = new FileStream(Path.Combine(destination, logName), FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                writer.Write(log.ToString());
            report?.Invoke(total, total, "高さ補正完了");
            return result;
        }

        private static int Save(string input, string output, int targetMinimumUm)
        {
            // 読み取り専用で開き、同じファイルを2回読んで1行ずつ処理する。
            using (var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int minimum = int.MaxValue;
                int maximum = int.MinValue;
                using (var reader = new StreamReader(stream, HeaderBytes, true, BufferSize, true))
                {
                    if (string.IsNullOrWhiteSpace(reader.ReadLine()))
                        throw new InvalidDataException("Matrixヘッダーがありません。");
                    int rows = 0, columns = 0;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var cells = line.Split(',');
                        if (rows == 0)
                            columns = cells.Length;
                        if (cells.Length != columns)
                            throw new InvalidDataException("Matrixの列数が不揃いです。");
                        rows++;
                        for (int x = 0; x < columns; x++)
                        {
                            int value;
                            if (!int.TryParse(cells[x], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                                throw new InvalidDataException(string.Format("整数ではありません: 行{0} 列{1}", rows, x + 1));
                            minimum = Math.Min(minimum, value);
                            maximum = Math.Max(maximum, value);
                        }
                    }
                    if (rows == 0)
                        throw new InvalidDataException("Matrixのデータ行がありません。");
                }
                // SRSの計測用Matrix TXTは高さを1 μm単位の整数に変換して保存する。
                long adjustment = (long)targetMinimumUm - minimum;
                if (maximum + adjustment > int.MaxValue)
                    throw new InvalidDataException("最小値補正後の値が整数の上限を超えます。");

                stream.Position = 0;
                string temporary = output + "." + Path.GetRandomFileName() + ".partial";
                using (var reader = new StreamReader(stream, HeaderBytes, true, BufferSize, true))
                {
                    string header = reader.ReadLine();
                    try
                    {
                        using (var writer = new StreamWriter(temporary, false, reader.CurrentEncoding, BufferSize))
                        {
                            writer.WriteLine(header);
                            var row = new StringBuilder();
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (adjustment == 0)
                                {
                                    writer.WriteLine(line);
                                    continue;
                                }
                                row.Clear();
                                var cells = line.Split(',');
                                for (int x = 0; x < cells.Length; x++)
                                {
                                    if (x > 0)
                                        row.Append(',');
                                    int value = int.Parse(cells[x], NumberStyles.Integer, CultureInfo.InvariantCulture);
                                    row.Append((value + adjustment).ToString(CultureInfo.InvariantCulture));
                                }
                                writer.WriteLine(row.ToString());
                            }
                        }
                        File.Move(temporary, output);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                return minimum;
            }
        }
    }
}
