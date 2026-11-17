using System;
using System.Drawing;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MatrixBitmapCrop
{
    // 斜めの切り出し範囲と、元画像へ戻すための方向ベクトルを保持する。
    public sealed class RotatedSelection
    {
        public PointF Origin;
        public PointF AxisEnd;
        public int Width;
        public int Height;
        public int MarginPixels;
        public bool UsesPhysicalGeometry;
        private RotatedSelection originalSelection;

        // サイズ変更は中心と方向を保持する。画素数は整数、実寸はベクトルで調整する。
        public RotatedSelection Resize(double widthFactor, double heightFactor)
        {
            int width = Math.Max(1, checked((int)Math.Round(Width * widthFactor)));
            int height = Math.Max(1, checked((int)Math.Round(Height * heightFactor)));
            double ux = Ux * Width * widthFactor / width, uy = Uy * Width * widthFactor / width;
            double vx = Vx * Height * heightFactor / height, vy = Vy * Height * heightFactor / height;
            double cx = (Origin.X + FourthCorner.X) / 2.0, cy = (Origin.Y + FourthCorner.Y) / 2.0;
            var point = new PointF((float)(cx - (ux * width + vx * height) / 2), (float)(cy - (uy * width + vy * height) / 2));
            return new RotatedSelection
            {
                Origin = point,
                AxisEnd = new PointF((float)(point.X + ux * width), (float)(point.Y + uy * width)),
                Width = width,
                Height = height,
                Ux = ux,
                Uy = uy,
                Vx = vx,
                Vy = vy,
                UsesPhysicalGeometry = UsesPhysicalGeometry
            };
        }

        public RotatedSelection Move(double dx, double dy)
        {
            return new RotatedSelection
            {
                Origin = new PointF((float)(Origin.X + dx), (float)(Origin.Y + dy)),
                AxisEnd = new PointF((float)(AxisEnd.X + dx), (float)(AxisEnd.Y + dy)),
                Width = Width,
                Height = Height,
                Ux = Ux,
                Uy = Uy,
                Vx = Vx,
                Vy = Vy,
                UsesPhysicalGeometry = UsesPhysicalGeometry
            };
        }

        // Uは幅方向、Vは高さ方向の単位ベクトル。
        public double Ux;
        public double Uy;
        public double Vx;
        public double Vy;

        public PointF ThirdCorner
        {
            get
            {
                return new PointF((float)(Origin.X + Vx * Height), (float)(Origin.Y + Vy * Height));
            }
        }

        public PointF FourthCorner
        {
            get
            {
                return new PointF((float)(AxisEnd.X + Vx * Height), (float)(AxisEnd.Y + Vy * Height));
            }
        }

        // 出力画素の中心を元画像の座標へ逆変換する。
        public PointF SourcePoint(int x, int y)
        {
            // 元のサンプリング座標を使い、内側指定で丸め誤差が増えるのを防ぐ。
            if (originalSelection != null)
                return originalSelection.SourcePoint(x + MarginPixels, y + MarginPixels);

            return new PointF(
                (float)(Origin.X + Ux * (x + .5) + Vx * (y + .5)),
                (float)(Origin.Y + Uy * (x + .5) + Vy * (y + .5)));
        }

        // 幅・高さ方向の両端から同じピクセル数だけ除外する。
        // 実寸空間で枠の中心を固定して回転する。画面のY軸は下向きなので正角度が右回転。
        public RotatedSelection Rotate(double degrees, double pitchX, double pitchY)
        {
            double radians = degrees * Math.PI / 180;
            double c = Math.Cos(radians), s = Math.Sin(radians);
            double centerX = (Origin.X + FourthCorner.X) * 0.5;
            double centerY = (Origin.Y + FourthCorner.Y) * 0.5;
            Func<PointF, PointF> rotatePoint = p => new PointF(
                (float)(centerX + c * (p.X - centerX) - s * (p.Y - centerY) * pitchY / pitchX),
                (float)(centerY + s * (p.X - centerX) * pitchX / pitchY + c * (p.Y - centerY)));
            return new RotatedSelection
            {
                Origin = rotatePoint(Origin),
                AxisEnd = rotatePoint(AxisEnd),
                Width = Width,
                Height = Height,
                UsesPhysicalGeometry = UsesPhysicalGeometry,
                Ux = c * Ux - s * Uy * pitchY / pitchX,
                Uy = s * Ux * pitchX / pitchY + c * Uy,
                Vx = c * Vx - s * Vy * pitchY / pitchX,
                Vy = s * Vx * pitchX / pitchY + c * Vy
            };
        }

        // 幅・高さ方向の両端から同じピクセル数だけ除外する。
        public RotatedSelection Inset(int pixels)
        {
            if (pixels < 1 || pixels > 10)
                throw new ArgumentOutOfRangeException("pixels", "内側の指定は1～10ピクセルです。");
            if (Width <= pixels * 2 || Height <= pixels * 2)
                throw new ArgumentException("内側に縮めると保存範囲がなくなります。範囲を広げるか、ピクセル数を減らしてください。");

            var innerOrigin = new PointF(
                (float)(Origin.X + (Ux + Vx) * pixels),
                (float)(Origin.Y + (Uy + Vy) * pixels));
            return new RotatedSelection
            {
                Origin = innerOrigin,
                AxisEnd = new PointF(
                    (float)(innerOrigin.X + Ux * (Width - pixels * 2)),
                    (float)(innerOrigin.Y + Uy * (Width - pixels * 2))),
                Width = Width - pixels * 2,
                Height = Height - pixels * 2,
                Ux = Ux,
                Uy = Uy,
                Vx = Vx,
                Vy = Vy,
                MarginPixels = pixels,
                UsesPhysicalGeometry = UsesPhysicalGeometry,
                originalSelection = this
            };
        }

        // 1・2点目から幅と角度を求め、3点目の垂直方向の距離を高さとする。
        public static RotatedSelection Create(PointF origin, PointF axisEnd, PointF heightPoint)
        {
            double dx = axisEnd.X - origin.X;
            double dy = axisEnd.Y - origin.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1)
                throw new ArgumentException("幅方向の2点を離して指定してください。");
            double ux = dx / length;
            double uy = dy / length;
            double vx = -uy;
            double vy = ux;
            double signedHeight = (heightPoint.X - origin.X) * vx + (heightPoint.Y - origin.Y) * vy;
            if (Math.Abs(signedHeight) < 1)
                throw new ArgumentException("高さ方向の点を離して指定してください。");
            if (signedHeight < 0)
            {
                vx = -vx;
                vy = -vy;
            }
            return new RotatedSelection
            {
                Origin = origin,
                AxisEnd = axisEnd,
                Width = (int)Math.Round(length),
                Height = (int)Math.Round(Math.Abs(signedHeight)),
                Ux = ux,
                Uy = uy,
                Vx = vx,
                Vy = vy
            };
        }

        // 実寸空間で直角を求めてから画素座標へ戻す。縦横の画素寸法が違っても枠が傾かない。
        public static RotatedSelection CreatePhysical(PointF origin, PointF axisEnd, PointF heightPoint,
            double pitchX, double pitchY)
        {
            double dx = (axisEnd.X - origin.X) * pitchX;
            double dy = (axisEnd.Y - origin.Y) * pitchY;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 0)
                throw new ArgumentException("幅方向の2点を離して指定してください。");
            double nx = -dy / length;
            double ny = dx / length;
            double distance = (heightPoint.X - origin.X) * pitchX * nx
                + (heightPoint.Y - origin.Y) * pitchY * ny;
            double hx = nx * distance / pitchX;
            double hy = ny * distance / pitchY;
            double width = Math.Sqrt(Math.Pow(axisEnd.X - origin.X, 2) + Math.Pow(axisEnd.Y - origin.Y, 2));
            double height = Math.Sqrt(hx * hx + hy * hy);
            if (width < 1 || height < 1)
                throw new ArgumentException("幅・高さ方向の点を離して指定してください。");
            return new RotatedSelection
            {
                Origin = origin,
                AxisEnd = axisEnd,
                Width = (int)Math.Round(width),
                Height = (int)Math.Round(height),
                Ux = (axisEnd.X - origin.X) / width,
                Uy = (axisEnd.Y - origin.Y) / width,
                Vx = hx / height,
                Vy = hy / height,
                UsesPhysicalGeometry = true
            };
        }
    }

    public static class MatrixCropper
    {
        // 単一ファイルを切り出す場合の入口。一括処理では同じCropPlanを再利用する。
        public static void Export(string matrixPath, Bitmap source, RotatedSelection area, string outputDirectory, bool saveReference = true)
        {
            ExportWithPlan(matrixPath, source, area, outputDirectory, saveReference, new CropPlan(source.Size, area));
        }

        private static void ExportWithPlan(string matrixPath, Bitmap source, RotatedSelection area,
            string outputDirectory, bool saveReference, CropPlan plan)
        {
            Directory.CreateDirectory(outputDirectory);
            string header = plan.ReadMatrix(matrixPath);
            string matrixOutput = Path.Combine(outputDirectory, Path.GetFileName(matrixPath));
            string temporaryOutput = matrixOutput + ".partial";
            plan.WriteMatrix(temporaryOutput, CropHeader(header, source.Size, area));
            // TXTを先に確定し、共通画像のエラーから独立させる。
            File.Move(temporaryOutput, matrixOutput);
            if (saveReference)
                SaveReference(source, area, outputDirectory, plan);
        }

        private static void SaveReference(Bitmap source, RotatedSelection area, string outputDirectory, CropPlan plan)
        {
            plan.WriteImage(source, Path.Combine(outputDirectory, "Selected_Image.bmp"));
            File.WriteAllText(Path.Combine(outputDirectory, "Selection.info"),
                string.Format(CultureInfo.InvariantCulture,
                    "OriginX={0}\r\nOriginY={1}\r\nAxisEndX={2}\r\nAxisEndY={3}\r\nWidth={4}\r\nHeight={5}\r\nMarginPixels={6}\r\nOutsideImage=0 and black\r\nSampling=nearest pixel\r\n",
                    area.Origin.X, area.Origin.Y, area.AxisEnd.X, area.AxisEnd.Y, area.Width, area.Height, area.MarginPixels),
                new UTF8Encoding(false));
        }

        // 全TXTに同じ範囲を適用する。読み込み失敗は記録し、次のファイルへ進む。
        public static BatchResult ExportBatch(
            string[] files,
            Bitmap source,
            RotatedSelection area,
            string outputDirectory,
            Action<int, int, string> report)
        {
            var result = new BatchResult();
            Directory.CreateDirectory(outputDirectory);
            // 準備中もポップアップを更新して、座標計算が進行中であることを示す。
            if (report != null)
                report(0, files.Length, "切り出し座標を準備しています…");
            string logPath = Path.Combine(outputDirectory, "BatchResult.log");
            File.WriteAllText(logPath, "切り出し準備中", new UTF8Encoding(false));
            CropPlan plan;
            try
            {
                plan = new CropPlan(source.Size, area);
            }
            catch (Exception ex)
            {
                File.WriteAllText(logPath, "座標準備で中断: " + ex.Message, new UTF8Encoding(false));
                throw;
            }

            for (int index = 0; index < files.Length; index++)
            {
                string name = Path.GetFileName(files[index]);
                if (report != null)
                    report(index, files.Length, name);

                try
                {
                    ExportWithPlan(files[index], source, area, outputDirectory, false, plan);
                    result.SuccessCount++;
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Runtime.InteropServices.ExternalException)
                {
                    result.Errors.Add(name + ": " + ex.Message);
                }
            }

            // 共通画像はTXT保存後に一度だけ出力し、失敗してもTXT成功件数を保持する。
            if (result.SuccessCount > 0)
            {
                try
                {
                    SaveReference(source, area, outputDirectory, plan);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                    || ex is System.Runtime.InteropServices.ExternalException || ex is OutOfMemoryException)
                {
                    result.ReferenceErrors.Add("参考画像・座標情報: " + ex.Message);
                }
            }

            // 入力フォルダー直下の付随ファイルを、そのまま同名でコピーする。
            // TXTの成否とは別に件数とエラーを記録する。
            if (files.Length > 0)
            {
                string inputDirectory = Path.GetDirectoryName(Path.GetFullPath(files[0]));
                foreach (string path in Directory.GetFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    string extension = Path.GetExtension(path);
                    if (!string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(extension, ".3m", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (report != null)
                        report(files.Length, files.Length, "コピー中: " + Path.GetFileName(path));
                    try
                    {
                        File.Copy(path, Path.Combine(outputDirectory, Path.GetFileName(path)), false);
                        result.CopyCount++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        result.CopyErrors.Add(Path.GetFileName(path) + ": " + ex.Message);
                    }
                }
            }

            File.WriteAllText(
                Path.Combine(outputDirectory, "BatchResult.log"),
                string.Format("TXT成功: {0} / {1}\r\n付随ファイルコピー成功: {2}\r\nコピー失敗: {3}\r\n",
                    result.SuccessCount, files.Length, result.CopyCount, result.CopyErrors.Count)
                    + string.Join("\r\n", result.Errors) + "\r\n" + string.Join("\r\n", result.CopyErrors) + "\r\n" + string.Join("\r\n", result.ReferenceErrors),
                new UTF8Encoding(false));

            if (report != null)
                report(files.Length, files.Length, "処理完了");

            return result;
        }

        // ヘッダーのWIDTHとHEIGHTを出力画素数の比率で更新する。
        private static string CropHeader(string header, Size original, RotatedSelection area)
        {
            var match = Regex.Match(header, @"^#WIDTH\s+(?<width>[\d.]+),\s*HEIGHT\s+(?<height>[\d.]+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                return header;
            double physicalWidth, physicalHeight;
            if (!double.TryParse(match.Groups["width"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out physicalWidth) ||
                !double.TryParse(match.Groups["height"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out physicalHeight))
                return header;
            double pitchX = physicalWidth / original.Width;
            double pitchY = physicalHeight / original.Height;
            double width = physicalWidth * area.Width / original.Width;
            double height = physicalHeight * area.Height / original.Height;
            if (area.UsesPhysicalGeometry)
            {
                width = area.Width * Math.Sqrt(Math.Pow(area.Ux * pitchX, 2) + Math.Pow(area.Uy * pitchY, 2));
                height = area.Height * Math.Sqrt(Math.Pow(area.Vx * pitchX, 2) + Math.Pow(area.Vy * pitchY, 2));
            }
            string replacement = string.Format(CultureInfo.InvariantCulture, "#WIDTH {0:F1}, HEIGHT {1:F1}", width, height);
            return replacement + header.Substring(match.Length);
        }
    }

    public sealed class BatchResult
    {
        public int SuccessCount;
        public int CopyCount;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> CopyErrors = new List<string>();
        public readonly List<string> ReferenceErrors = new List<string>();
    }
}
