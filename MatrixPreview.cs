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
    public static class MatrixPreview
    {
        // Matrixの1セルを1画素へ変換する。数値そのものは変更しない。
        public static Bitmap Create(string path)
        {
            var rows = new List<int[]>();
            int width = 0;
            int minimum = int.MaxValue, maximum = int.MinValue;
            using (var reader = new StreamReader(path, Encoding.Default, true, 65536))
            {
                if (reader.ReadLine() == null)
                    throw new InvalidDataException("Matrixのヘッダーがありません。");
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] cells = line.Split(',');
                    if (rows.Count == 0)
                        width = cells.Length;
                    if (cells.Length != width)
                        throw new InvalidDataException(string.Format("参考画像を生成できません。{0}行目の列数が一致しません。", rows.Count + 1));
                    if ((long)width * (rows.Count + 1) > 100000000)
                        throw new InvalidDataException("参考画像の生成は1億画素以下のMatrixに対応しています。");
                    var values = new int[width];
                    for (int x = 0; x < width; x++)
                    {
                        if (!int.TryParse(cells[x], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[x]))
                            throw new InvalidDataException(string.Format("参考画像を生成できません。{0}行目・{1}列目が整数ではありません。", rows.Count + 1, x + 1));
                        if (values[x] != 0)
                        {
                            minimum = Math.Min(minimum, values[x]);
                            maximum = Math.Max(maximum, values[x]);
                        }
                    }
                    rows.Add(values);
                }
            }
            if (rows.Count == 0)
                throw new InvalidDataException("Matrixの数値データがありません。");

            var bitmap = new Bitmap(width, rows.Count, PixelFormat.Format24bppRgb);
            try
            {
                var bits = bitmap.LockBits(new Rectangle(0, 0, width, rows.Count), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte[] buffer = new byte[Math.Abs(bits.Stride)];
                    for (int y = 0; y < rows.Count; y++)
                    {
                        Array.Clear(buffer, 0, buffer.Length);
                        for (int x = 0; x < width; x++)
                        {
                            int value = rows[y][x];
                            // 0は黒。その他は32～255の濃淡、一定値の場合は中間色で表示する。
                            byte shade = value == 0 ? (byte)0 : maximum == minimum ? (byte)160
                                : (byte)(32 + Math.Round(223.0 * ((double)value - minimum) / ((double)maximum - minimum)));
                            buffer[x * 3] = buffer[x * 3 + 1] = buffer[x * 3 + 2] = shade;
                        }
                        Marshal.Copy(buffer, 0, IntPtr.Add(bits.Scan0, y * bits.Stride), buffer.Length);
                    }
                }
                finally { bitmap.UnlockBits(bits); }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
    }
}
