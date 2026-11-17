using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace MatrixBitmapCrop.Correction
{
    public class TemperatureRange
    {
        public double MinTemp
        {
            get; set;
        }
        public double MaxTemp
        {
            get; set;
        }
        public double Step
        {
            get; set;
        }
    }

    public partial class MatrixCorrectionData
    {
        // 10℃以上の刻みは十の位へ、10℃未満は整数温度へそろえる。
        public static List<double> CreateTemperatureSchedule(double start, double end, double defaultStep,
            List<TemperatureRange> ranges, bool includeStart)
        {
            if (double.IsNaN(defaultStep) || double.IsInfinity(defaultStep) || defaultStep < 1 || defaultStep != Math.Truncate(defaultStep))
                throw new ArgumentException("通常の刻みは1℃以上の整数にしてください。");
            if (ranges != null)
                foreach (var r in ranges)
                    if (r == null || double.IsNaN(r.MinTemp) || double.IsInfinity(r.MinTemp)
                        || double.IsNaN(r.MaxTemp) || double.IsInfinity(r.MaxTemp)
                        || !(r.MinTemp < r.MaxTemp) || double.IsNaN(r.Step) || double.IsInfinity(r.Step)
                        || r.Step < 1 || r.Step != Math.Truncate(r.Step))
                        throw new ArgumentException("温度範囲を確認し、刻みは1℃以上の整数にしてください。");
            double low = Math.Min(start, end), high = Math.Max(start, end);
            var boundaries = new List<double> { low, high };
            if (ranges != null)
                foreach (var r in ranges)
                    foreach (double boundary in new[] { r.MinTemp, r.MaxTemp })
                        if (boundary > low && boundary < high) boundaries.Add(boundary);
            boundaries = boundaries.Distinct().OrderBy(t => t).ToList();
            bool rise = end > start;
            var temperatures = new SortedSet<double>();
            for (int i = 0; i + 1 < boundaries.Count; i++)
            {
                double left = boundaries[i], right = boundaries[i + 1];
                double middle = (left + right) / 2;
                double step = defaultStep;
                if (ranges != null)
                    foreach (var r in ranges)
                        if (middle >= r.MinTemp && middle <= r.MaxTemp) step = Math.Min(step, r.Step);
                double unit = step >= 10 ? 10 : 1;
                double first = rise ? Math.Ceiling(left / unit - 0.0000001) * unit
                    : Math.Floor(right / unit + 0.0000001) * unit;
                for (double t = first; rise ? t <= right + 0.0000001 : t >= left - 0.0000001; t += rise ? step : -step)
                {
                    if (!includeStart && Math.Abs(t - start) < 0.0000001) continue;
                    temperatures.Add(t);
                    if (temperatures.Count > 20000) throw new ArgumentException("作成温度が2万件を超えます。");
                }
            }
            return rise ? temperatures.ToList() : temperatures.Reverse().ToList();
        }
        // ピーク前後も同じ刻み設定に従い、ピーク温度は独立して1回保存する。
        public void CreateSelectedCorrectedData(List<MatData> sourceList, double riseStartTemp,
            double peakTemp, double fallEndTemp, double defaultStep, List<TemperatureRange> fineRanges)
        {
            var rising = CreateTemperatureSchedule(riseStartTemp, peakTemp, defaultStep, fineRanges, true)
                .Where(t => t < peakTemp - 0.000001).ToList();
            var falling = CreateTemperatureSchedule(peakTemp, fallEndTemp, defaultStep, fineRanges, false);
            int count = 0;
            Mat before = null;
            double beforeTemp = 0;
            using (Mat peak = CreatePeakMatrix(FindPeakDataPair(sourceList), sourceList, peakTemp))
            {
                try
                {
                    foreach (double temperature in rising)
                    {
                        ReportStatus("昇温補正: " + temperature);
                        Mat normal = CreateTemperatureMatrix(sourceList, temperature, true);
                        if (normal != null)
                        {
                            if (before != null)
                                before.Dispose();
                            before = normal;
                            beforeTemp = temperature;
                            SaveCorrectData(sourceList[0], normal, (float)temperature, ++count, true);
                        }
                        else if (before != null && temperature > beforeTemp)
                        {
                            using (Mat near = InterpolateMat(before, beforeTemp, peak, peakTemp, temperature))
                                SaveCorrectData(sourceList[0], near, (float)temperature, ++count, true);
                        }
                        else
                            log.AppendLine("補間元なし（昇温）: " + temperature);
                    }
                    SaveCorrectData(sourceList[0], peak, (float)peakTemp, ++count, true);
                    // 通常補間できる最初の降温データとピークの間を補間する。
                    Mat after = null;
                    int first = 0;
                    try
                    {
                        for (; first < falling.Count; first++)
                        {
                            after = CreateTemperatureMatrix(sourceList, falling[first], false);
                            if (after != null)
                                break;
                        }
                        for (int i = 0; i < falling.Count; i++)
                        {
                            double temperature = falling[i];
                            ReportStatus("降温補正: " + temperature);
                            if (i < first && after != null)
                            {
                                using (Mat near = InterpolateMat(peak, peakTemp, after, falling[first], temperature))
                                    SaveCorrectData(sourceList[0], near, (float)temperature, ++count, false);
                            }
                            else
                            {
                                using (Mat normal = CreateTemperatureMatrix(sourceList, temperature, false))
                                    if (normal != null)
                                        SaveCorrectData(sourceList[0], normal, (float)temperature, ++count, false);
                                    else
                                        log.AppendLine("補間元なし（降温）: " + temperature);
                            }
                        }
                    }
                    finally { if (after != null) after.Dispose(); }
                }
                finally { if (before != null) before.Dispose(); }
            }
        }
    }
}
