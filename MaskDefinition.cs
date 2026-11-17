using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Serialization;

namespace MatrixBitmapCrop
{
    public enum MaskShapeKind
    {
        Rectangle,
        Polygon,
        Line
    }

    // 指定形状は表示倍率に依存しない元画像の座標で保存する。
    public sealed class MaskShape
    {
        public MaskShapeKind Kind;
        public float LineWidth = 20;
        public List<PointF> Points = new List<PointF>();

        public override string ToString()
        {
            return ToDisplayString(CropLanguage.Japanese);
        }

        public string ToDisplayString(CropLanguage language)
        {
            string name = Kind == MaskShapeKind.Rectangle ? "矩形" : Kind == MaskShapeKind.Polygon ? "多角形" : "幅付き線";
            return string.Format(CropLocalization.Text("{0} / {1}点", language),
                CropLocalization.Text(name, language), Points.Count);
        }

        public GraphicsPath CreatePath()
        {
            var path = new GraphicsPath();
            if (Kind == MaskShapeKind.Rectangle && Points.Count == 2)
            {
                path.AddRectangle(RectangleF.FromLTRB(
                    Math.Min(Points[0].X, Points[1].X), Math.Min(Points[0].Y, Points[1].Y),
                    Math.Max(Points[0].X, Points[1].X), Math.Max(Points[0].Y, Points[1].Y)));
            }
            else if (Kind == MaskShapeKind.Polygon && Points.Count >= 3)
            {
                path.AddPolygon(Points.ToArray());
            }
            else if (Kind == MaskShapeKind.Line && Points.Count == 2)
            {
                if (Points[0] == Points[1])
                    return path;
                path.AddLine(Points[0], Points[1]);
                using (var pen = new Pen(Color.White, LineWidth))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    path.Widen(pen);
                }
            }
            return path;
        }
    }

    public sealed class MaskDefinition
    {
        public int ImageWidth;
        public int ImageHeight;
        // falseは旧版の画素座標。trueではPoints/LineWidthをmmとして保存する。
        public bool CoordinatesInMillimeters;
        public double PhysicalWidthMm;
        public double PhysicalHeightMm;
        public int MatrixColumns;
        public int MatrixRows;
        public double CellWidthMm;
        public double CellHeightMm;
        public List<MaskShape> Shapes = new List<MaskShape>();

        public void Save(string path)
        {
            using (var stream = File.Create(path))
                new XmlSerializer(typeof(MaskDefinition)).Serialize(stream, this);
        }

        public static MaskDefinition Load(string path)
        {
            // 外部参照を禁止し、座標定義としてだけXMLを読み込む。
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 4000000
            };
            using (var reader = System.Xml.XmlReader.Create(path, settings))
            {
                var definition = (MaskDefinition)new XmlSerializer(typeof(MaskDefinition)).Deserialize(reader);
                definition.Validate();
                return definition;
            }
        }

        public void Validate()
        {
            if (ImageWidth < 1 || ImageHeight < 1 || (long)ImageWidth * ImageHeight > 100000000)
                throw new InvalidDataException("マスク画像の寸法が不正です。");
            if (Shapes == null || Shapes.Count > 10000)
                throw new InvalidDataException("マスク形状の数が不正です。");
            if (CoordinatesInMillimeters && (!(PhysicalWidthMm > 0) || !(PhysicalHeightMm > 0)
                || double.IsInfinity(PhysicalWidthMm) || double.IsInfinity(PhysicalHeightMm)))
                throw new InvalidDataException("マスクの実寸が不正です。");
            foreach (var shape in Shapes)
            {
                if (shape == null || shape.Points == null || !Enum.IsDefined(typeof(MaskShapeKind), shape.Kind)
                    || (shape.Kind == MaskShapeKind.Polygon ? shape.Points.Count < 3 : shape.Points.Count != 2)
                    || shape.Points.Count > 10000 || float.IsNaN(shape.LineWidth)
                    || float.IsInfinity(shape.LineWidth) || shape.LineWidth <= 0 || shape.LineWidth > 1000)
                    throw new InvalidDataException("マスク形状の指定が不正です。");
                foreach (var point in shape.Points)
                    if (float.IsNaN(point.X) || float.IsNaN(point.Y) || float.IsInfinity(point.X)
                        || float.IsInfinity(point.Y) || Math.Abs(point.X) > 1000000 || Math.Abs(point.Y) > 1000000)
                        throw new InvalidDataException("マスク座標が不正です。");
            }
        }

        // 複数形状を先に合成する。十字の交点や重なった部分も一つのマスクとして扱う。
        public bool[] Rasterize()
        {
            return Rasterize(ImageWidth, ImageHeight, PhysicalWidthMm, PhysicalHeightMm);
        }

        // 保存したmm座標を、各入力Matrixのセル境界へ換算して適用する。
        public bool[] Rasterize(int columns, int rows, double widthMm, double heightMm)
        {
            Validate();
            if (columns < 1 || rows < 1 || (long)columns * rows > 100000000)
                throw new InvalidDataException("Matrix寸法が不正です。");
            var mask = new bool[checked(columns * rows)];
            using (var bitmap = new Bitmap(columns, rows, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Black);
                    graphics.SmoothingMode = SmoothingMode.None;
                    foreach (var shape in Shapes)
                        using (var path = shape.CreatePath())
                        {
                            if (CoordinatesInMillimeters)
                            {
                                if (!(widthMm > 0) || !(heightMm > 0))
                                    throw new InvalidDataException("実寸ヘッダーが必要です。");
                                using (var transform = new Matrix())
                                {
                                    transform.Scale((float)(columns / widthMm), (float)(rows / heightMm));
                                    path.Transform(transform);
                                }
                            }
                            graphics.FillPath(Brushes.White, path);
                        }
                }
                var bits = bitmap.LockBits(new Rectangle(0, 0, columns, rows), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row = new byte[columns * 4];
                    for (int y = 0; y < rows; y++)
                    {
                        Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), row, 0, row.Length);
                        for (int x = 0; x < columns; x++)
                            mask[y * columns + x] = row[x * 4] != 0;
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bits);
                }
            }
            return mask;
        }
    }
}
