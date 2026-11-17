using System;
using System.Drawing;

namespace MatrixBitmapCrop
{
    // 角は時計回りに 0=始点、1=幅終点、2=対角、3=高さ終点。
    // 枠の方向を変えず、ドラッグした角の反対側を固定して伸縮する。
    public static class CropHandleGeometry
    {
        public static PointF Corner(RotatedSelection area, int index)
        {
            switch (index)
            {
                case 0: return area.Origin;
                case 1: return area.AxisEnd;
                case 2: return area.FourthCorner;
                case 3: return area.ThirdCorner;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public static RotatedSelection ResizeCorner(RotatedSelection area, int corner, PointF position, int minimumCells)
        {
            if (area == null || corner < 0 || corner > 3 || minimumCells < 1)
                throw new ArgumentException("Invalid crop handle.");
            double determinant = area.Ux * area.Vy - area.Uy * area.Vx;
            if (Math.Abs(determinant) < 1e-12) throw new ArgumentException("Invalid crop axes.");
            double dx = position.X - area.Origin.X, dy = position.Y - area.Origin.Y;
            double u = (dx * area.Vy - dy * area.Vx) / determinant;
            double v = (dy * area.Ux - dx * area.Uy) / determinant;
            // 初回の3点指定ではAxisEndと整数Widthに丸め差があるため、実際の角を基準にする。
            double axisX = area.AxisEnd.X - area.Origin.X, axisY = area.AxisEnd.Y - area.Origin.Y;
            double left = 0, top = 0, right = (axisX * area.Vy - axisY * area.Vx) / determinant, bottom = area.Height;
            if (corner == 0 || corner == 3) left = Math.Min(u, right - minimumCells);
            else right = Math.Max(u, left + minimumCells);
            if (corner == 0 || corner == 1) top = Math.Min(v, bottom - minimumCells);
            else bottom = Math.Max(v, top + minimumCells);
            double w = right - left, h = bottom - top;
            if (double.IsNaN(w) || double.IsInfinity(w) || double.IsNaN(h) || double.IsInfinity(h)
                || w > int.MaxValue || h > int.MaxValue)
                throw new ArgumentException("Invalid crop dimensions.");
            int width = Math.Max(minimumCells, checked((int)Math.Round(w)));
            int height = Math.Max(minimumCells, checked((int)Math.Round(h)));
            if ((long)width * height > 30000000) throw new ArgumentException("Crop exceeds 30000000 cells.");
            var origin = new PointF((float)(area.Origin.X + area.Ux * left + area.Vx * top),
                (float)(area.Origin.Y + area.Uy * left + area.Vy * top));
            return new RotatedSelection
            {
                Origin = origin,
                AxisEnd = new PointF((float)(origin.X + area.Ux * w), (float)(origin.Y + area.Uy * w)),
                Width = width, Height = height,
                Ux = area.Ux * w / width, Uy = area.Uy * w / width,
                Vx = area.Vx * h / height, Vy = area.Vy * h / height,
                UsesPhysicalGeometry = area.UsesPhysicalGeometry
            };
        }

        // 実際の画面上の対角線に近い方向のサイズ変更カーソルを選ぶ。
        public static int CursorAxis(PointF point, PointF opposite, double scaleX, double scaleY)
        {
            double angle = Math.Atan2((point.Y - opposite.Y) * scaleY, (point.X - opposite.X) * scaleX) * 180 / Math.PI;
            return ((int)Math.Round(angle / 45) % 4 + 4) % 4;
        }
    }
}
