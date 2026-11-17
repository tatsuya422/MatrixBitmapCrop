using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenCvSharp;

namespace MatrixBitmapCrop.Correction
{
    public partial class MatrixCorrectionData
    {


        public static Mat InterpolateMat( Mat mat1, double temp1, Mat mat2, double temp2, double targetTemp)
        {
            if (mat1 == null || mat1.Empty()) throw new ArgumentException("mat1==null");

            if (mat2 == null || mat2.Empty()) throw new ArgumentException("mat2==null");

            double dTemp = temp2 - temp1;

            if (Math.Abs(dTemp) < 0.000001) throw new ArgumentException("temp1==temp2");

            double minTemp = Math.Min(temp1, temp2);
            double maxTemp = Math.Max(temp1, temp2);

            if (targetTemp < minTemp || targetTemp > maxTemp)
            {
                throw new ArgumentOutOfRangeException( nameof(targetTemp), "targetTemp < minTemp && maxTemp < targetTemp");
            }

            int rows = Math.Min( mat1.Rows, mat2.Rows);
            int cols = Math.Min( mat1.Cols, mat2.Cols);

            Rect roi = new Rect( 0, 0, cols, rows);

            Mat src1 = mat1[roi];
            Mat src2 = mat2[roi];

            Mat result = new Mat();

            try
            {
                double ratio = (targetTemp - temp1) / (temp2 - temp1);

                // result =
                // mat1 × (1-ratio)
                // + mat2 × ratio
                Cv2.AddWeighted( src1, 1.0 - ratio, src2, ratio, 0.0, result);
                return result;
            }
            finally
            {
                src1.Dispose();
                src2.Dispose();
            }
        }

        public int CreateRiseNearPeak( Mat beforePeakMat, double beforePeakTemp, Mat peakMat, double peakTemp, double step, int count)
        {
            if (step <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(step));

            for (double temp = beforePeakTemp + step;temp < peakTemp; temp += step)
            {
                Mat result = null;

                try
                {

                    result = InterpolateMat( beforePeakMat, beforePeakTemp, peakMat, peakTemp, temp);

                    if (null != result)
                    {
                        count++;
                        SaveCorrectData(_matrices[0], result, (float)temp, count, true);
                    }
                    
                }
                finally
                {
                    result?.Dispose();
                }
            }
            return count;
        }

        public int CreateFallNearPeak( Mat peakMat, double peakTemp, Mat afterPeakMat, double afterPeakTemp, double step, int count)
        {
            if (step <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(step));

            for (double temp = peakTemp - step;temp > afterPeakTemp; temp -= step)
            {
                Mat result = null;

                try
                {
                    result = InterpolateMat(peakMat, peakTemp, afterPeakMat, afterPeakTemp, temp);
                    if (null != result)
                    {
                        count++;
                        SaveCorrectData(_matrices[0], result, (float)temp, count, false);
                    }
                }
                finally
                {
                    result?.Dispose();
                }
            }

            return count;
        }

    }// End Class
}
