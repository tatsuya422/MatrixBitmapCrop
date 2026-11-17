using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenCvSharp;

namespace MatrixBitmapCrop.Correction
{ 
    public class MatDataItem
    {
        // Mat内でこのScanが始まるY行
        public int StartRow { get; set; }

        // このScanのY方向の行数
        public int RowCount { get; set; }

        // このScanを開始した温度
        public double StartTemp { get; set; }

        // このScanを終了した温度
        public double EndTemp { get; set; }

        public double StartTime { get; set; }
        public double EndTime { get; set; }

        public double AverageTemp
        {
            get
            {
                return (StartTemp + EndTemp) * 0.5;
            }
        }

        // 指定X列を取得したときの温度
        public double GetTemperatureAtX(
            int x,
            int columnCount)
        {
            if (columnCount <= 1)
                return StartTemp;

            double ratio =
                (double)x / (columnCount - 1);

            return StartTemp
                 + (EndTemp - StartTemp) * ratio;
        }
    }

    public class MatData
    {
        public string FileName { get; set; }

        // true：昇温、false：降温
        public bool IsRise { get; set; }

        // 最大5スキャンをY方向に連結した高さデータ
        public Mat Mat { get; set; }

        public float Width { get; set; }
        public float Height { get; set; }

        // 各スキャンの情報
        public List<MatDataItem> Items { get; set; }
            = new List<MatDataItem>();
    }

    public class ScanPair
    {
        public MatData Data1 { get; set; }
        public MatDataItem Item1 { get; set; }

        public MatData Data2 { get; set; }
        public MatDataItem Item2 { get; set; }
    }
    
    public class CorrectedMatData
    {
        // このMatrix全体が何℃相当か
        public double TargetTemp { get; set; }

        // true：昇温側から生成
        // false：降温側から生成
        public bool IsRise { get; set; }

        public Mat Mat { get; set; }
    }

    public partial class MatrixCorrectionData
    {

        List<MatData> _matrices = new List<MatData>();
        string _readFolder = string.Empty;

        public ScanPair FindScanPair( List<MatData> sourceList, int scanIndex, double targetTemp, bool isRise)
        {
            var candidates = sourceList
                .Where(data =>
                    data.IsRise == isRise &&
                    data.Items != null &&
                    scanIndex < data.Items.Count)
                .Select(data => new
                {
                    Data = data,
                    Item = data.Items[scanIndex]
                })
                .OrderBy(x => x.Item.AverageTemp)
                .ToList();

            for (int i = 0; i < candidates.Count - 1; i++)
            {
                double temp1 = candidates[i].Item.AverageTemp;
                double temp2 = candidates[i + 1].Item.AverageTemp;

                double minTemp = Math.Min(temp1, temp2);
                double maxTemp = Math.Max(temp1, temp2);

                if (targetTemp >= minTemp && targetTemp <= maxTemp)
                {
                    return new ScanPair
                    {
                        Data1 = candidates[i].Data,
                        Item1 = candidates[i].Item,

                        Data2 = candidates[i + 1].Data,
                        Item2 = candidates[i + 1].Item
                    };
                }
            }

            return null;
        }
        
        public Mat CorrectOneScan( ScanPair pair ,double targetTemp)
        {
            if (pair == null)
                throw new ArgumentNullException(nameof(pair));

            int cols = Math.Min( pair.Data1.Mat.Cols, pair.Data2.Mat.Cols);

            int rows = Math.Min( pair.Item1.RowCount, pair.Item2.RowCount);

            Rect rect1 = new Rect( 0, pair.Item1.StartRow, cols, rows);

            Rect rect2 = new Rect( 0, pair.Item2.StartRow, cols, rows);

            Mat src1 = pair.Data1.Mat[rect1];
            Mat src2 = pair.Data2.Mat[rect2];

            Mat result = new Mat( rows, cols, MatType.CV_64FC1);

            try
            {
                for (int x = 0; x < cols; x++)
                {
                    double temp1 = pair.Item1.GetTemperatureAtX( x, cols);

                    double temp2 = pair.Item2.GetTemperatureAtX( x, cols);

                    double dTemp = temp2 - temp1;

                    Mat src1Col = src1.Col(x);
                    Mat src2Col = src2.Col(x);
                    Mat resultCol = result.Col(x);

                    try
                    {
                        if (Math.Abs(dTemp) < 0.000001)
                        {
                            src1Col.CopyTo(resultCol);
                            continue;
                        }

                        double ratio = (targetTemp - temp1) / dTemp;

                        Mat difference = new Mat();
                        Mat correction = new Mat();

                        try
                        {
                            // difference = src2 - src1
                            Cv2.Subtract( src2Col, src1Col, difference);

                            // correction = difference × ratio
                            difference.ConvertTo( correction, MatType.CV_64FC1, ratio);

                            // result = src1 + correction
                            Cv2.Add( src1Col, correction, resultCol);
                        }
                        finally
                        {
                            difference.Dispose();
                            correction.Dispose();
                        }
                    }
                    finally
                    {
                        src1Col.Dispose();
                        src2Col.Dispose();
                        resultCol.Dispose();
                    }
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
            finally
            {
                src1.Dispose();
                src2.Dispose();
            }
        }

        public Mat CreateTemperatureMatrix( List<MatData> sourceList, double targetTemp, bool isRise)
        {
            if (sourceList == null || sourceList.Count == 0)
            {
                throw new ArgumentException("No measurement data.");
            }

            MatData firstData = sourceList
                    .FirstOrDefault(data =>
                    data.IsRise == isRise &&
                    data.Items != null &&
                    data.Items.Count > 0);

            if (firstData == null)
            {
                throw new InvalidOperationException(isRise ? "No rise data." : "No clooling data.");
            }

            // Items[0]～Items[scanCount - 1]をScanとして扱う
            int scanCount = firstData.Items.Count;

            List<Mat> scanResults = new List<Mat>();

            try
            {
                int totalRows = 0;
                int commonCols = int.MaxValue;

                for (int scanIndex = 0;scanIndex < scanCount;scanIndex++)
                {
                    ScanPair pair = FindScanPair( sourceList, scanIndex, targetTemp, isRise);

                    if (pair == null)
                    {
                        /*
                        throw new InvalidOperationException(
                            $"about Scan {scanIndex}," +
                            $"{targetTemp:F1}℃を挟む2データがありません。");
                        */
                        return null;
                    }

                    Mat correctedScan = CorrectOneScan( pair, targetTemp);

                    scanResults.Add(correctedScan);

                    totalRows += correctedScan.Rows;

                    commonCols = Math.Min( commonCols, correctedScan.Cols);
                }

                Mat result = new Mat( totalRows, commonCols, MatType.CV_64FC1);

                int destinationRow = 0;

                foreach (Mat scanMat in scanResults)
                {
                    Rect sourceRect = new Rect( 0, 0, commonCols, scanMat.Rows);

                    Rect destinationRect = new Rect( 0, destinationRow, commonCols, scanMat.Rows);

                    Mat sourceRoi = scanMat[sourceRect];

                    Mat destinationRoi = result[destinationRect];

                    try
                    {
                        sourceRoi.CopyTo(destinationRoi);
                    }
                    finally
                    {
                        sourceRoi.Dispose();
                        destinationRoi.Dispose();
                    }

                    destinationRow += scanMat.Rows;
                }

                return result;
            }
            finally
            {
                foreach (Mat scanMat in scanResults)
                {
                    scanMat.Dispose();
                }
            }
        }

        // ピーク前後の温度が交差しても高さの補間係数は発散しない。
        // tempData.tmp の時刻と測定ヘッダの時刻は原点が異なるため、
        // 両方に記録された温度を対応付けて時刻差を推定する。
        private double GetPeakMeasurementTime(MatData before, MatData after)
        {
            double fallback = (before.Items.Max(i => i.EndTime) + after.Items.Min(i => i.StartTime)) * 0.5;
            string path = temperatureProfilePath ?? Path.Combine(_readFolder ?? string.Empty, "tempData.tmp");
            if (!File.Exists(path))
                return fallback;

            var profile = new List<Tuple<double, double>>();
            DateTime first = DateTime.MinValue;
            foreach (string line in File.ReadLines(path))
            {
                string[] fields = line.Split(',');
                DateTime stamp;
                double temperature;
                if (fields.Length < 3 ||
                    !DateTime.TryParse(fields[1], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out stamp) ||
                    !double.TryParse(fields[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out temperature))
                    continue;
                if (first == DateTime.MinValue) first = stamp;
                profile.Add(Tuple.Create((stamp - first).TotalSeconds, temperature));
            }
            if (profile.Count < 3) return fallback;

            int peakIndex = 0;
            for (int i = 1; i < profile.Count; i++)
                if (profile[i].Item2 > profile[peakIndex].Item2) peakIndex = i;

            var offsets = new List<double>();
            AddTimeOffsets(offsets, before.Items, profile, 0, peakIndex + 1);
            AddTimeOffsets(offsets, after.Items, profile, peakIndex, profile.Count);
            if (offsets.Count < 4) return fallback;
            offsets.Sort();
            double offset = (offsets[(offsets.Count - 1) / 2] + offsets[offsets.Count / 2]) * 0.5;

            // 異なる温度履歴や別のログを誤って対応付けない。
            int close = offsets.Count(v => Math.Abs(v - offset) <= 3.0);
            if (close < offsets.Count * 0.75) return fallback;
            double peakTime = profile[peakIndex].Item1 + offset;
            if (peakTime < before.Items.Min(i => i.StartTime) ||
                peakTime > after.Items.Max(i => i.EndTime)) return fallback;
            return peakTime;
        }

        private static void AddTimeOffsets(List<double> offsets, List<MatDataItem> items,
            List<Tuple<double, double>> profile, int start, int end)
        {
            foreach (MatDataItem item in items)
            {
                AddTimeOffset(offsets, item.StartTemp, item.StartTime, profile, start, end);
                AddTimeOffset(offsets, item.EndTemp, item.EndTime, profile, start, end);
            }
        }

        private static void AddTimeOffset(List<double> offsets, double temperature, double measurementTime,
            List<Tuple<double, double>> profile, int start, int end)
        {
            int nearest = start;
            for (int i = start + 1; i < end; i++)
                if (Math.Abs(profile[i].Item2 - temperature) <
                    Math.Abs(profile[nearest].Item2 - temperature)) nearest = i;
            if (Math.Abs(profile[nearest].Item2 - temperature) <= 0.5)
                offsets.Add(measurementTime - profile[nearest].Item1);
        }

        public Mat CreatePeakMatrix(ScanPair pair, List<MatData> dataList, double peakTemp)
        {
            if (pair == null || pair.Data1 == null || pair.Data2 == null)
                throw new ArgumentNullException(nameof(pair));
            MatData before = pair.Data1;
            MatData after = pair.Data2;
            if (before.Mat == null || before.Mat.Empty() || after.Mat == null || after.Mat.Empty())
                throw new ArgumentException("ピーク前後のMatrixがありません。");
            if (before.Items == null || after.Items == null ||
                before.Items.Count == 0 || after.Items.Count == 0)
                throw new ArgumentException("ピーク前後のスキャン情報がありません。");

            int scanCount = Math.Min(before.Items.Count, after.Items.Count);
            int cols = Math.Min(before.Mat.Cols, after.Mat.Cols);
            int totalRows = 0;
            for (int i = 0; i < scanCount; i++)
                totalRows += Math.Min(before.Items[i].RowCount, after.Items[i].RowCount);

            double peakTime = GetPeakMeasurementTime(before, after);
            Mat result = new Mat(totalRows, cols, MatType.CV_64FC1);
            int writeRow = 0;
            try
            {
                for (int scan = 0; scan < scanCount; scan++)
                {
                    MatDataItem b = before.Items[scan];
                    MatDataItem a = after.Items[scan];
                    int rows = Math.Min(b.RowCount, a.RowCount);
                    using (Mat bScan = before.Mat[new Rect(0, b.StartRow, cols, rows)])
                    using (Mat aScan = after.Mat[new Rect(0, a.StartRow, cols, rows)])
                    using (Mat destination = result[new Rect(0, writeRow, cols, rows)])
                    {
                        for (int x = 0; x < cols; x++)
                        {
                            double fraction = cols <= 1 ? 0.0 : (double)x / (cols - 1);
                            double timeBefore = b.StartTime + (b.EndTime - b.StartTime) * fraction;
                            double timeAfter = a.StartTime + (a.EndTime - a.StartTime) * fraction;
                            double duration = timeAfter - timeBefore;
                            double ratio = duration > 0 ? (peakTime - timeBefore) / duration : 0.5;
                            ratio = Math.Max(0.0, Math.Min(1.0, ratio));
                            using (Mat bCol = bScan.Col(x))
                            using (Mat aCol = aScan.Col(x))
                            using (Mat targetCol = destination.Col(x))
                                Cv2.AddWeighted(bCol, 1.0 - ratio, aCol, ratio, 0.0, targetCol);
                        }
                    }
                    writeRow += rows;
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }


        public static ScanPair FindPeakDataPair(List<MatData> dataList)
        {
            if (dataList == null || dataList.Count < 2)
                throw new ArgumentException(
                    "ピーク前後を検索するには2個以上のデータが必要です。");

            for (int i = 0; i < dataList.Count - 1; i++)
            {
                MatData current = dataList[i];
                MatData next = dataList[i + 1];

                // trueからfalseへ変わった位置
                if (current.IsRise && !next.IsRise)
                {
                    return new ScanPair
                    {
                        Data1 = current,
                        Data2 = next
                    };
                }
            }

            throw new InvalidOperationException(
                "isRise が true から false に変わる位置が見つかりません。");
        }


    }
}