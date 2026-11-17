using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MatrixBitmapCrop
{
    // WIDTH/HEIGHTはmm、Columns/Rowsはセル数。セル境界は0～実寸とする。
    public sealed class MatrixScale
    {
        public double WidthMm;
        public double HeightMm;
        public int Columns;
        public int Rows;
        public double PitchX
        {
            get
            {
                return WidthMm / Columns;
            }
        }
        public double PitchY
        {
            get
            {
                return HeightMm / Rows;
            }
        }

        public static MatrixScale Read(string path)
        {
            using (var reader = new StreamReader(path, Encoding.Default, true, 65536))
            {
                string header = reader.ReadLine() ?? "";
                var width = Regex.Match(header, @"(?:#|,\s*)WIDTH\s+([+\-\d.eE]+)", RegexOptions.IgnoreCase);
                var height = Regex.Match(header, @"(?:#|,\s*)HEIGHT\s+([+\-\d.eE]+)", RegexOptions.IgnoreCase);
                double w, h;
                if (!width.Success || !height.Success
                    || !double.TryParse(width.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out w)
                    || !double.TryParse(height.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out h)
                    || double.IsNaN(w) || double.IsInfinity(w) || double.IsNaN(h) || double.IsInfinity(h) || w <= 0 || h <= 0)
                    return null;
                int rows = 0, columns = 0;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (rows == 0)
                    {
                        columns = 1;
                        foreach (char c in line)
                            if (c == ',')
                                columns++;
                    }
                    rows++;
                }
                if (rows == 0)
                    throw new InvalidDataException("Matrixの数値データがありません。");
                return new MatrixScale { WidthMm = w, HeightMm = h, Columns = columns, Rows = rows };
            }
        }
    }
}
