using System;
using System.Collections.Generic;
using System.IO;

namespace MatrixBitmapCrop
{
    // 周囲の傾斜面を推定し、その残差を滑らかに補間する。
    // 画像端は設定により正常値をそのまま延長する。存在しない画素を0として使わない。
    internal static class MaskInterpolator
    {
        public static double[] Fill(int[][] data, bool[][] valid, bool[] mask, int width, int height, bool ignoreZero,
            bool flatEdges = false, int flatPercent = 100)
        {
            if (flatPercent < 0 || flatPercent > 100)
                throw new ArgumentOutOfRangeException(nameof(flatPercent), "平坦化率は0～100％で指定してください。");
            var result = new double[mask.Length];
            var visited = new bool[mask.Length];
            var queue = new Queue<int>();

            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || visited[start])
                    continue;

                // 接続している範囲はまとめて解く。十字の交点を参照値に混ぜない。
                var component = new List<int>();
                visited[start] = true;
                queue.Enqueue(start);
                int left = width, right = 0, top = height, bottom = 0;
                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    component.Add(index);
                    int x = index % width, y = index / width;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                    if (x > 0)
                        Enqueue(index - 1, mask, visited, queue);
                    if (x + 1 < width)
                        Enqueue(index + 1, mask, visited, queue);
                    if (y > 0)
                        Enqueue(index - width, mask, visited, queue);
                    if (y + 1 < height)
                        Enqueue(index + width, mask, visited, queue);
                }

                int componentLeft = left, componentRight = right, componentTop = top, componentBottom = bottom;
                left = Math.Max(0, left - 8);
                top = Math.Max(0, top - 8);
                right = Math.Min(width - 1, right + 8);
                bottom = Math.Min(height - 1, bottom + 8);
                int w = right - left + 1, h = bottom - top + 1;
                double centerX = (left + right) * .5, centerY = (top + bottom) * .5;
                double scale = Math.Max(w, h);
                var sums = new double[3, 4];
                int donors = 0;

                // 参照点は全マスクの外だけから集める。未計測を表す0は任意で除外する。
                for (int y = top; y <= bottom; y++)
                    for (int x = left; x <= right; x++)
                        if (IsDonor(x, y, data, valid, mask, width, ignoreZero))
                        {
                            double u = (x - centerX) / scale, v = (y - centerY) / scale;
                            double z = data[y][x];
                            sums[0, 0]++;
                            sums[0, 1] += u;
                            sums[0, 2] += v;
                            sums[0, 3] += z;
                            sums[1, 0] += u;
                            sums[1, 1] += u * u;
                            sums[1, 2] += u * v;
                            sums[1, 3] += u * z;
                            sums[2, 0] += v;
                            sums[2, 1] += u * v;
                            sums[2, 2] += v * v;
                            sums[2, 3] += v * z;
                            donors++;
                        }
                if (donors < 3)
                    throw new InvalidDataException("指定範囲の周囲に補間可能なデータが3点以上ありません。範囲または0除外設定を確認してください。");

                double mean = sums[0, 3] / donors;
                double[] plane = SolvePlane(sums, mean);
                // 端の広い領域を1枚の平面で外挿すると、反りの曲率を誤って横の傾きへ変換してしまう。
                // 端に接する成分では、近傍の行・列で求めた局所傾斜を距離に応じて合成する。
                bool atLeft = componentLeft == 0, atRight = componentRight == width - 1;
                bool atTop = componentTop == 0, atBottom = componentBottom == height - 1;
                var surface = atLeft || atRight || atTop || atBottom
                    ? FitEdgeSurface(data, valid, mask, width, height, ignoreZero, componentLeft, componentRight, componentTop, componentBottom) : null;
                var fromLeft = atRight ? new EdgeTrend[h] : null;
                var fromRight = atLeft ? new EdgeTrend[h] : null;
                var fromTop = atBottom ? new EdgeTrend[w] : null;
                var fromBottom = atTop ? new EdgeTrend[w] : null;
                if (surface == null && (atLeft || atRight || atTop || atBottom))
                {
                    var firstX = new int[h];
                    var lastX = new int[h];
                    var firstY = new int[w];
                    var lastY = new int[w];
                    for (int i = 0; i < h; i++)
                    {
                        firstX[i] = width;
                        lastX[i] = -1;
                    }
                    for (int i = 0; i < w; i++)
                    {
                        firstY[i] = height;
                        lastY[i] = -1;
                    }
                    foreach (int index in component)
                    {
                        int x = index % width, y = index / width;
                        firstX[y - top] = Math.Min(firstX[y - top], x);
                        lastX[y - top] = Math.Max(lastX[y - top], x);
                        firstY[x - left] = Math.Min(firstY[x - left], y);
                        lastY[x - left] = Math.Max(lastY[x - left], y);
                    }
                    for (int y = top; y <= bottom; y++)
                    {
                        int first = firstX[y - top] == width ? componentLeft : firstX[y - top];
                        int last = lastX[y - top] < 0 ? componentRight : lastX[y - top];
                        if (atRight)
                            fromLeft[y - top] = FitEdge(data, valid, mask, width, height, ignoreZero, first - 1, y, true, -1);
                        if (atLeft)
                            fromRight[y - top] = FitEdge(data, valid, mask, width, height, ignoreZero, last + 1, y, true, 1);
                    }
                    for (int x = left; x <= right; x++)
                    {
                        int first = firstY[x - left] == height ? componentTop : firstY[x - left];
                        int last = lastY[x - left] < 0 ? componentBottom : lastY[x - left];
                        if (atBottom)
                            fromTop[x - left] = FitEdge(data, valid, mask, width, height, ignoreZero, x, first - 1, false, -1);
                        if (atTop)
                            fromBottom[x - left] = FitEdge(data, valid, mask, width, height, ignoreZero, x, last + 1, false, 1);
                    }
                }
                Func<int, int, double> trend = (x, y) =>
                {
                    if (surface != null)
                        return surface.Value(x, y);
                    double sum = 0, weight = 0;
                    AddEdge(fromLeft == null ? null : fromLeft[y - top], x, y, true, ref sum, ref weight);
                    AddEdge(fromRight == null ? null : fromRight[y - top], x, y, true, ref sum, ref weight);
                    AddEdge(fromTop == null ? null : fromTop[x - left], x, y, false, ref sum, ref weight);
                    AddEdge(fromBottom == null ? null : fromBottom[x - left], x, y, false, ref sum, ref weight);
                    return weight > 0 ? sum / weight : PlaneValue(plane, x, y, centerX, centerY, scale);
                };
                var values = new double[w * h];
                var known = new bool[values.Length];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int sx = x + left, sy = y + top, i = y * w + x;
                        if (IsDonor(sx, sy, data, valid, mask, width, ignoreZero)
                            && (surface == null || Math.Abs(data[sy][sx] - surface.Value(sx, sy)) <= surface.Tolerance))
                        {
                            known[i] = true;
                            values[i] = data[sy][sx] - trend(sx, sy);
                        }
                    }

                // 粗い格子から順に解き、広い領域でも内部へ滑らかに境界値を伝える。
                SolveResidual(values, known, w, h);
                var edgeValues = flatEdges && flatPercent > 0 ? MakeFlatEdges(component, data, valid, mask, width, height,
                    ignoreZero, atLeft, atRight, atTop, atBottom, surface) : null;
                foreach (int index in component)
                {
                    int x = index % width, y = index / width;
                    double value = trend(x, y) + values[(y - top) * w + x - left];
                    double edgeValue;
                    if (edgeValues != null && edgeValues.TryGetValue(index, out edgeValue))
                        // 0％は従来の反り補間、100％は近傍値の延長。丸める前の値を混ぜる。
                        value += (edgeValue - value) * (flatPercent / 100.0);
                    if (double.IsNaN(value) || double.IsInfinity(value) || value < int.MinValue || value > int.MaxValue)
                        throw new InvalidDataException("補間値が整数データの範囲を超えました。");
                    result[index] = value;
                }
            }
            return result;
        }

        // 端の欠測部は傾きを外挿せず、同じ行（左右端）または列（上下端）の正常値を延長する。
        // 四隅では横方向を優先し、参照できない行だけ縦方向の値を使う。
        private static Dictionary<int, double> MakeFlatEdges(List<int> component, int[][] data, bool[][] valid,
            bool[] mask, int width, int height, bool ignoreZero, bool atLeft, bool atRight,
            bool atTop, bool atBottom, EdgeSurface surface)
        {
            var result = new Dictionary<int, double>();
            if (!atLeft && !atRight && !atTop && !atBottom)
                return result;
            var firstX = new int[height];
            var lastX = new int[height];
            var firstY = new int[width];
            var lastY = new int[width];
            for (int y = 0; y < height; y++) { firstX[y] = width; lastX[y] = -1; }
            for (int x = 0; x < width; x++) { firstY[x] = height; lastY[x] = -1; }
            foreach (int index in component)
            {
                int x = index % width, y = index / width;
                firstX[y] = Math.Min(firstX[y], x);
                lastX[y] = Math.Max(lastX[y], x);
                firstY[x] = Math.Min(firstY[x], y);
                lastY[x] = Math.Max(lastY[x], y);
            }
            var rowValues = new double[height];
            var columnValues = new double[width];
            for (int y = 0; y < height; y++)
                rowValues[y] = firstX[y] < width && atLeft != atRight
                    ? FindNormalValue(data, valid, mask, width, height, ignoreZero,
                        atRight ? firstX[y] - 1 : lastX[y] + 1, y, atRight ? -1 : 1, 0, surface)
                    : double.NaN;
            for (int x = 0; x < width; x++)
                columnValues[x] = firstY[x] < height && atTop != atBottom
                    ? FindNormalValue(data, valid, mask, width, height, ignoreZero,
                        x, atBottom ? firstY[x] - 1 : lastY[x] + 1, 0, atBottom ? -1 : 1, surface)
                    : double.NaN;
            foreach (int index in component)
            {
                int x = index % width, y = index / width;
                double value = double.IsNaN(rowValues[y]) ? columnValues[x] : rowValues[y];
                if (!double.IsNaN(value))
                    result[index] = value;
            }
            return result;
        }

        private static double FindNormalValue(int[][] data, bool[][] valid, bool[] mask, int width, int height,
            bool ignoreZero, int x, int y, int dx, int dy, EdgeSurface surface)
        {
            while (x >= 0 && x < width && y >= 0 && y < height)
            {
                if (IsDonor(x, y, data, valid, mask, width, ignoreZero)
                    && (surface == null || Math.Abs(data[y][x] - surface.Value(x, y)) <= surface.Tolerance))
                    return data[y][x];
                x += dx;
                y += dy;
            }
            return double.NaN;
        }

        // 曲がった正常面を推定し、端に残った部品・突起を境界条件へ混ぜない。
        private sealed class EdgeSurface
        {
            public double X, Y, Scale, Tolerance;
            public double[] Coefficients;
            public double Value(int x, int y)
            {
                double u = (x - X) / Scale, v = (y - Y) / Scale;
                return Coefficients[0] + Coefficients[1] * u + Coefficients[2] * v
                    + Coefficients[3] * u * u + Coefficients[4] * u * v + Coefficients[5] * v * v;
            }
        }

        private static EdgeSurface FitEdgeSurface(int[][] data, bool[][] valid, bool[] mask, int width, int height,
            bool ignoreZero, int left, int right, int top, int bottom)
        {
            left = Math.Max(0, left - 64);
            right = Math.Min(width - 1, right + 64);
            top = Math.Max(0, top - 64);
            bottom = Math.Min(height - 1, bottom + 64);
            var surface = new EdgeSurface
            {
                X = (left + right) * .5,
                Y = (top + bottom) * .5,
                Scale = Math.Max(right - left + 1, bottom - top + 1)
            };
            int stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)(right - left + 1) * (bottom - top + 1) / 6000)));
            var samples = new List<int>();
            for (int y = top; y <= bottom; y += stride)
                for (int x = left; x <= right; x += stride)
                    if (IsDonor(x, y, data, valid, mask, width, ignoreZero))
                        samples.Add(y * width + x);
            if (samples.Count < 30)
                return null;
            double limit = double.PositiveInfinity;
            for (int pass = 0; pass < 7; pass++)
            {
                if (surface.Coefficients != null)
                {
                    var errors = new double[samples.Count];
                    for (int i = 0; i < samples.Count; i++)
                    {
                        int x = samples[i] % width, y = samples[i] / width;
                        errors[i] = Math.Abs(data[y][x] - surface.Value(x, y));
                    }
                    Array.Sort(errors);
                    limit = Math.Max(5, errors[errors.Length / 2] * 4.5);
                }
                var sums = new double[6, 7];
                foreach (int index in samples)
                {
                    int x = index % width, y = index / width;
                    double u = (x - surface.X) / surface.Scale, v = (y - surface.Y) / surface.Scale;
                    double z = data[y][x];
                    double error = surface.Coefficients == null ? 0 : Math.Abs(z - surface.Value(x, y));
                    // 大きな外れ値を徐々に弱め、正常面の曲率を残す。
                    double weight = error > limit ? Math.Pow(limit / error, 2) : 1;
                    double[] basis = { 1, u, v, u * u, u * v, v * v };
                    for (int row = 0; row < 6; row++)
                    {
                        for (int col = 0; col < 6; col++)
                            sums[row, col] += weight * basis[row] * basis[col];
                        sums[row, 6] += weight * basis[row] * z;
                    }
                }
                var solution = SolveQuadratic(sums);
                if (solution == null)
                    return null;
                surface.Coefficients = solution;
            }
            surface.Tolerance = limit;
            return surface;
        }

        private static double[] SolveQuadratic(double[,] a)
        {
            for (int col = 0; col < 6; col++)
            {
                int pivot = col;
                for (int row = col + 1; row < 6; row++)
                    if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col]))
                        pivot = row;
                if (Math.Abs(a[pivot, col]) < 1e-10)
                    return null;
                for (int j = col; j < 7; j++)
                {
                    double swap = a[col, j];
                    a[col, j] = a[pivot, j];
                    a[pivot, j] = swap;
                }
                double divisor = a[col, col];
                for (int j = col; j < 7; j++)
                    a[col, j] /= divisor;
                for (int row = 0; row < 6; row++)
                    if (row != col)
                    {
                        double factor = a[row, col];
                        for (int j = col; j < 7; j++)
                            a[row, j] -= factor * a[col, j];
                    }
            }
            var result = new double[6];
            for (int i = 0; i < 6; i++)
            {
                result[i] = a[i, 6];
                if (double.IsNaN(result[i]) || double.IsInfinity(result[i]))
                    return null;
            }
            return result;
        }

        private sealed class EdgeTrend
        {
            public int X, Y;
            public double[] Plane;
            public double Value(int x, int y)
            {
                return PlaneValue(Plane, x, y, X, Y, 32);
            }
        }

        private static void AddEdge(EdgeTrend model, int x, int y, bool horizontal, ref double sum, ref double total)
        {
            if (model == null)
                return;
            double distance = Math.Max(1, horizontal ? Math.Abs(x - model.X) : Math.Abs(y - model.Y));
            // 遠くの一辺が近傍の傾きを上書きしないよう、距離の4乗で重み付けする。
            double weight = 1 / (distance * distance * distance * distance);
            sum += weight * model.Value(x, y);
            total += weight;
        }

        private static EdgeTrend FitEdge(int[][] data, bool[][] valid, bool[] mask, int width, int height,
            bool ignoreZero, int anchorX, int anchorY, bool horizontal, int direction)
        {
            if (anchorX < 0 || anchorX >= width || anchorY < 0 || anchorY >= height)
                return null;
            var samples = new List<int>();
            for (int along = 0; along < 64; along++)
                for (int across = -3; across <= 3; across++)
                {
                    int x = anchorX + (horizontal ? direction * along : across);
                    int y = anchorY + (horizontal ? across : direction * along);
                    if (x >= 0 && x < width && y >= 0 && y < height && IsDonor(x, y, data, valid, mask, width, ignoreZero))
                        samples.Add(y * width + x);
                }
            if (samples.Count < 6)
                return null;
            double[] fitted = null;
            // 周囲に残った少数の異常値の影響を抑える反復重み付き最小二乗。
            for (int pass = 0; pass < 3; pass++)
            {
                double limit = double.PositiveInfinity;
                if (fitted != null)
                {
                    var deviations = new double[samples.Count];
                    for (int i = 0; i < samples.Count; i++)
                    {
                        int x = samples[i] % width, y = samples[i] / width;
                        deviations[i] = Math.Abs(data[y][x] - PlaneValue(fitted, x, y, anchorX, anchorY, 32));
                    }
                    Array.Sort(deviations);
                    limit = Math.Max(1, deviations[deviations.Length / 2] * 4.5);
                }
                var sums = new double[3, 4];
                foreach (int index in samples)
                {
                    int x = index % width, y = index / width;
                    double u = (x - anchorX) / 32.0, v = (y - anchorY) / 32.0, z = data[y][x];
                    double residual = fitted == null ? 0 : Math.Abs(z - PlaneValue(fitted, x, y, anchorX, anchorY, 32));
                    double weight = residual > limit ? limit / residual : 1;
                    double[] basis = { 1, u, v };
                    for (int row = 0; row < 3; row++)
                    {
                        for (int col = 0; col < 3; col++)
                            sums[row, col] += weight * basis[row] * basis[col];
                        sums[row, 3] += weight * basis[row] * z;
                    }
                }
                fitted = SolvePlane(sums, sums[0, 3] / sums[0, 0]);
            }
            return new EdgeTrend { X = anchorX, Y = anchorY, Plane = fitted };
        }

        private static bool IsDonor(int x, int y, int[][] data, bool[][] valid, bool[] mask, int width, bool ignoreZero)
        {
            return !mask[y * width + x] && y < data.Length && x < data[y].Length
                && valid[y][x] && (!ignoreZero || data[y][x] != 0);
        }

        private static void Enqueue(int i, bool[] mask, bool[] visited, Queue<int> queue)
        {
            if (mask[i] && !visited[i])
            {
                visited[i] = true;
                queue.Enqueue(i);
            }
        }

        private static double PlaneValue(double[] p, int x, int y, double cx, double cy, double scale)
        {
            return p[0] + p[1] * (x - cx) / scale + p[2] * (y - cy) / scale;
        }

        // 3元連立方程式を部分ピボット付きで解く。傾斜を決められない配置は平均値へ戻す。
        private static double[] SolvePlane(double[,] a, double mean)
        {
            for (int col = 0; col < 3; col++)
            {
                int pivot = col;
                for (int row = col + 1; row < 3; row++)
                    if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col]))
                        pivot = row;
                if (Math.Abs(a[pivot, col]) < 1e-10)
                    return new[] { mean, 0.0, 0.0 };
                for (int j = col; j < 4; j++)
                {
                    double temp = a[col, j];
                    a[col, j] = a[pivot, j];
                    a[pivot, j] = temp;
                }
                double divisor = a[col, col];
                for (int j = col; j < 4; j++)
                    a[col, j] /= divisor;
                for (int row = 0; row < 3; row++)
                {
                    if (row == col)
                        continue;
                    double factor = a[row, col];
                    for (int j = col; j < 4; j++)
                        a[row, j] -= factor * a[col, j];
                }
            }
            return new[] { a[0, 3], a[1, 3], a[2, 3] };
        }

        private static void SolveResidual(double[] values, bool[] known, int width, int height)
        {
            if (width > 16 || height > 16)
            {
                int cw = (width + 1) / 2, ch = (height + 1) / 2;
                var coarse = new double[cw * ch];
                var fixedCoarse = new bool[coarse.Length];
                for (int cy = 0; cy < ch; cy++)
                    for (int cx = 0; cx < cw; cx++)
                    {
                        int count = 0;
                        double sum = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int x = cx * 2 + dx, y = cy * 2 + dy;
                                if (x < width && y < height && known[y * width + x])
                                {
                                    count++;
                                    sum += values[y * width + x];
                                }
                            }
                        if (count > 0)
                        {
                            fixedCoarse[cy * cw + cx] = true;
                            coarse[cy * cw + cx] = sum / count;
                        }
                    }
                SolveResidual(coarse, fixedCoarse, cw, ch);
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        if (!known[y * width + x])
                        {
                            double fx = Math.Max(0, (x - .5) / 2), fy = Math.Max(0, (y - .5) / 2);
                            int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(cw - 1, x0 + 1), y1 = Math.Min(ch - 1, y0 + 1);
                            double tx = fx - x0, ty = fy - y0;
                            values[y * width + x] = (1 - ty) * ((1 - tx) * coarse[y0 * cw + x0] + tx * coarse[y0 * cw + x1])
                                + ty * ((1 - tx) * coarse[y1 * cw + x0] + tx * coarse[y1 * cw + x1]);
                        }
            }

            for (int iteration = 0; iteration < 120; iteration++)
            {
                double maxChange = 0;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * width + x;
                        if (known[i])
                            continue;
                        double sum = 0;
                        int count = 0;
                        if (x > 0)
                        {
                            sum += values[i - 1];
                            count++;
                        }
                        if (x + 1 < width)
                        {
                            sum += values[i + 1];
                            count++;
                        }
                        if (y > 0)
                        {
                            sum += values[i - width];
                            count++;
                        }
                        if (y + 1 < height)
                        {
                            sum += values[i + width];
                            count++;
                        }
                        if (count == 0)
                            continue;
                        double next = sum / count;
                        maxChange = Math.Max(maxChange, Math.Abs(next - values[i]));
                        values[i] = next;
                    }
                if (maxChange < .01)
                    break;
            }
        }
    }
}
