using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MatrixBitmapCrop
{
    // 一括処理で共通の座標対応表を一度だけ作り、全ファイルで再利用する。
    internal sealed class CropPlan
    {
        public readonly int[] SourceIndexes;
        private readonly bool[] required;
        private readonly int[] lastColumn;
        private readonly int[] data;
        private readonly Size sourceSize;
        private readonly RotatedSelection area;
        private int lastRow = -1;

        public CropPlan(Size size, RotatedSelection selection)
        {
            if (selection.Width < 1 || selection.Height < 1)
                throw new ArgumentException("選択範囲がありません。");
            if ((long)selection.Width * selection.Height > 30000000)
                throw new ArgumentException("選択範囲が大きすぎます。3000万画素以下にしてください。");

            sourceSize = size;
            area = selection;
            SourceIndexes = new int[checked(area.Width * area.Height)];
            required = new bool[checked(size.Width * size.Height)];
            data = new int[required.Length];
            lastColumn = new int[size.Height];
            for (int row = 0; row < lastColumn.Length; row++)
                lastColumn[row] = -1;

            for (int y = 0; y < area.Height; y++)
            {
                for (int x = 0; x < area.Width; x++)
                {
                    // 従来と同じ丸め方を使い、出力値を変えずに高速化する。
                    PointF point = area.SourcePoint(x, y);
                    int sx = (int)Math.Floor(point.X);
                    int sy = (int)Math.Floor(point.Y);
                    int index = -1;
                    if (sx >= 0 && sy >= 0 && sx < size.Width && sy < size.Height)
                    {
                        index = sy * size.Width + sx;
                        required[index] = true;
                        lastColumn[sy] = Math.Max(lastColumn[sy], sx);
                        lastRow = Math.Max(lastRow, sy);
                    }
                    SourceIndexes[y * area.Width + x] = index;
                }
            }
        }

        // Splitによる大量の文字列生成を避け、選択範囲の数値だけを読み取る。
        public string ReadMatrix(string path)
        {
            using (var reader = new StreamReader(path, Encoding.Default, true, 65536))
            {
                string header = reader.ReadLine();
                if (header == null)
                    throw new InvalidDataException("Matrix のヘッダーがありません。");

                for (int y = 0; y <= lastRow; y++)
                {
                    string line = reader.ReadLine();
                    if (line == null)
                        throw new InvalidDataException(string.Format(
                            "選択範囲に必要な行がありません。Matrixは{0}行、必要な最終行は{1}行目です。", y, lastRow + 1));

                    int start = 0;
                    int rowOffset = y * sourceSize.Width;
                    for (int x = 0; x <= lastColumn[y]; x++)
                    {
                        if (start > line.Length)
                            throw new InvalidDataException(string.Format(
                                "選択範囲のデータが不足しています。{0}行目に必要な{1}列目がありません。", y + 1, lastColumn[y] + 1));

                        int end = line.IndexOf(',', start);
                        if (end < 0)
                            end = line.Length;

                        if (required[rowOffset + x])
                        {
                            int value;
                            if (!TryReadInteger(line, start, end, out value))
                                throw new InvalidDataException(string.Format(
                                    "選択範囲の{0}行目・{1}列目に整数以外の値があります。", y + 1, x + 1));
                            data[rowOffset + x] = value;
                        }
                        start = end + 1;
                    }
                }
                return header;
            }
        }

        // 空白と符号付きの32bit整数を、部分文字列を作らず解析する。
        private static bool TryReadInteger(string text, int start, int end, out int value)
        {
            value = 0;
            while (start < end && char.IsWhiteSpace(text[start]))
                start++;
            while (end > start && char.IsWhiteSpace(text[end - 1]))
                end--;

            bool negative = start < end && text[start] == '-';
            if (start < end && (text[start] == '-' || text[start] == '+'))
                start++;
            if (start == end)
                return false;

            long number = 0;
            long limit = negative ? 2147483648L : int.MaxValue;
            for (int index = start; index < end; index++)
            {
                int digit = text[index] - '0';
                if (digit < 0 || digit > 9)
                    return false;
                number = number * 10 + digit;
                if (number > limit)
                    return false;
            }
            value = (int)(negative ? -number : number);
            return true;
        }

        // 1行ずつまとめて保存し、頻出する数値の文字列変換も再利用する。
        public void WriteMatrix(string path, string header)
        {
            var numberText = new Dictionary<int, string>();
            var row = new StringBuilder(area.Width * 6);
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false), 65536))
            {
                writer.WriteLine(header);
                for (int y = 0; y < area.Height; y++)
                {
                    row.Clear();
                    for (int x = 0; x < area.Width; x++)
                    {
                        if (x > 0)
                            row.Append(',');
                        int sourceIndex = SourceIndexes[y * area.Width + x];
                        // 元データは保持し、切り出しTXTへ保存する値だけ0以下を0にそろえる。
                        int value = sourceIndex < 0 ? 0 : Math.Max(0, data[sourceIndex]);
                        string text;
                        if (!numberText.TryGetValue(value, out text))
                        {
                            text = value.ToString(CultureInfo.InvariantCulture);
                            // 値の種類が多いファイルでもキャッシュを増やしすぎない。
                            if (numberText.Count < 65536)
                                numberText.Add(value, text);
                        }
                        row.Append(text);
                    }
                    writer.WriteLine(row.ToString());
                }
            }
        }

        // GetPixel/SetPixelを使わず、画像メモリから直接画素をコピーする。
        public void WriteImage(Bitmap source, string path)
        {
            using (var normalized = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
            using (var output = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(normalized))
                {
                    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    graphics.DrawImageUnscaled(source, 0, 0);
                }

                byte[] pixels = new byte[checked(source.Width * source.Height * 4)];
                var sourceBits = normalized.LockBits(new Rectangle(Point.Empty, normalized.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < source.Height; y++)
                        Marshal.Copy(IntPtr.Add(sourceBits.Scan0, y * sourceBits.Stride), pixels, y * source.Width * 4, source.Width * 4);
                }
                finally
                {
                    normalized.UnlockBits(sourceBits);
                }

                var outputBits = output.LockBits(new Rectangle(Point.Empty, output.Size), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte[] row = new byte[Math.Abs(outputBits.Stride)];
                    for (int y = 0; y < area.Height; y++)
                    {
                        Array.Clear(row, 0, row.Length);
                        for (int x = 0; x < area.Width; x++)
                        {
                            int index = SourceIndexes[y * area.Width + x];
                            if (index < 0)
                                continue;
                            row[x * 3] = pixels[index * 4];
                            row[x * 3 + 1] = pixels[index * 4 + 1];
                            row[x * 3 + 2] = pixels[index * 4 + 2];
                        }
                        Marshal.Copy(row, 0, IntPtr.Add(outputBits.Scan0, y * outputBits.Stride), row.Length);
                    }
                }
                finally
                {
                    output.UnlockBits(outputBits);
                }
                output.Save(path, ImageFormat.Bmp);
            }
        }
    }
}
