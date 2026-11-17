using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenCvSharp;

namespace MatrixBitmapCrop.Alignment
{

    public class MatrixAlignmentTwoStep
    {

        public static List<int[]> AnalyzeMatrixAlignment(List<int[]> baseArray, List<int[]> targetArray, out double offsetX, out double offsetY, out double response)
        {
            offsetX = offsetY = response = 0;
            List<int[]> alignArray = new List<int[]>();

            Debug.Assert(null != baseArray);
            Debug.Assert(null != targetArray);

            int sizeY1 = baseArray.Count;
            int sizeX1 = baseArray[0].Length;
            int sizeY2 = targetArray.Count;
            int sizeX2 = targetArray[0].Length;
            if (sizeY2 != sizeY1)
            {
                Debug.Assert(sizeY2 == sizeY1);
            }
            if (sizeX2 != sizeX1)
            {
                Debug.Assert(sizeX2 == sizeX1);
            }

            if (sizeY1 < 1)
            {
                return null;
            }
            if (sizeX1 < 1)
            {
                return null;
            }

            float[,] baseMatrix = new float[sizeY1, sizeX1];
            float[,] targetMatrix = new float[sizeY1, sizeX1];

            for (int y = 0; y < sizeY1; y++)
            {
                for (int x = 0; x < sizeX1; x++)
                {
                    baseMatrix[y, x] = baseArray[y][x];
                    targetMatrix[y, x] = targetArray[y][x];
                }
            }

            float[,] alignMatrix = AlignMatrix(baseMatrix, targetMatrix, out offsetX, out offsetY, out response);

            if (null == alignMatrix)
            {
                return null;
            }

            int sizeY3 = alignMatrix.GetLength(0);
            int sizeX3 = alignMatrix.GetLength(1);
            if (sizeY1 != sizeY3)
            {
                Debug.Assert(sizeY1 == sizeY3);
            }
            if (sizeX1 != sizeX3)
            {
                Debug.Assert(sizeX1 == sizeX3);
            }

            for (int y = 0; y < sizeY1; y++)
            {
                int[] arrayX = new int[sizeX1];
                for (int x = 0; x < sizeX1; x++)
                {
                    arrayX[x] = (int)alignMatrix[y, x];
                }

                alignArray.Add(arrayX);

            }

            return alignArray;
        }


        public static float[,] AlignMatrix(float[,] baseMatrix, float[,] targetMatrix, out double offsetX, out double offsetY, out double response)
        {
            int rows = baseMatrix.GetLength(0);
            int cols = baseMatrix.GetLength(1);

            // 元の全体データをMat形式に変換
            using (Mat matBaseFull = MatrixToMat(baseMatrix))
            using (Mat matTargetFull = MatrixToMat(targetMatrix))
            {
                // =================================================================
                // 【ステップ1】ラフ位置合わせ（大きなズレを大まかに合わせる）
                // =================================================================
                // データを 1/4 に縮小してノイズを平滑化する
                int smallCols = cols / 4;
                int smallRows = rows / 4;

                using (Mat matBaseSmall = new Mat())
                using (Mat matTargetSmall = new Mat())
                using (Mat windowSmall = new Mat(smallRows, smallCols, MatType.CV_32FC1))
                {
                    // エリア平均による縮小（ノイズ除去効果が高い手法です）
                    Cv2.Resize(matBaseFull, matBaseSmall, new Size(smallCols, smallRows), 0, 0, InterpolationFlags.Area);
                    Cv2.Resize(matTargetFull, matTargetSmall, new Size(smallCols, smallRows), 0, 0, InterpolationFlags.Area);

                    // 縮小サイズに合わせたガウシアン窓の作成
                    CreateGaussianWindow(windowSmall, smallRows, smallCols);

                    // 縮小データ同士でラフなズレ量を検出
                    Point2d shiftSmall = Cv2.PhaseCorrelate(matBaseSmall, matTargetSmall, windowSmall, out double respSmall);

                    // 検出したズレ量を「4倍」して元のスケールに戻す
                    float roughOffsetX = (float)(-shiftSmall.X * 4.0);
                    float roughOffsetY = (float)(-shiftSmall.Y * 4.0);

                    Console.WriteLine($"[1段階目:ラフ] 信頼度: {respSmall:F4} / 概算ズレ量 -> X: {roughOffsetX:F2}, Y: {roughOffsetY:F2}");

                    // ターゲットデータをまずは「大まかに移動させた中間Mat」を作る
                    using (Mat matTargetRoughAligned = new Mat())
                    using (Mat matTransformRough = CreateTransformMatrix(roughOffsetX, roughOffsetY))
                    {
                        Cv2.WarpAffine(matTargetFull, matTargetRoughAligned, matTransformRough, new Size(cols, rows),
                                       InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

                        // =================================================================
                        // 【ステップ2】精密位置合わせ（中央の狭いエリアで追い込む）
                        // =================================================================
                        // 大枠はもう合っているので、はみ出る心配はありません。
                        // 余白ノイズを完全に排除するため、中央の「狭い確定エリア（例: 内側50%）」を切り出します。
                        int borderX = (int)(cols * 0.25); // 左右から25%ずつ削る
                        int borderY = (int)(rows * 0.25); // 上下から25%ずつ削る
                        Rect narrowRoi = new Rect(borderX, borderY, cols - (borderX * 2), rows - (borderY * 2));

                        using (Mat matBaseRoi = new Mat(matBaseFull, narrowRoi))
                        using (Mat matTargetRoi = new Mat(matTargetRoughAligned, narrowRoi))
                        using (Mat windowNarrow = new Mat(matBaseRoi.Rows, matBaseRoi.Cols, MatType.CV_32FC1))
                        {
                            // 狭いエリアに合わせたガウシアン窓の作成
                            CreateGaussianWindow(windowNarrow, matBaseRoi.Rows, matBaseRoi.Cols);

                            // 微小な残りズレ（サブピクセル単位）を検出
                            Point2d shiftFine = Cv2.PhaseCorrelate(matBaseRoi, matTargetRoi, windowNarrow, out double respFine);

                            float fineOffsetX = (float)(-shiftFine.X);
                            float fineOffsetY = (float)(-shiftFine.Y);

                            Console.WriteLine($"[2段階目:精密] 信頼度: {respFine:F4} / 残りズレ量 -> X: {fineOffsetX:F2}, Y: {fineOffsetY:F2}");

                            // =================================================================
                            // 【最終合成】1回目のズレ量 ＋ 2回目のズレ量 を合わせて一発で全体を変換
                            // =================================================================
                            float finalOffsetX = roughOffsetX + fineOffsetX;
                            float finalOffsetY = roughOffsetY + fineOffsetY;
                            offsetX = finalOffsetX;
                            offsetY = finalOffsetY;
                            response = respFine;

                            Console.WriteLine($"[最終結果] トータル移動量 -> X: {finalOffsetX:F2} 画素, Y: {finalOffsetY:F2} 画素");

                            using (Mat matTransformFinal = CreateTransformMatrix(finalOffsetX, finalOffsetY))
                            using (Mat matFinalAligned = new Mat())
                            {
                                // 補間による画質劣化を最小限にするため、元の生のtargetMatrixから一気に平行移動させます
                                Cv2.WarpAffine(matTargetFull, matFinalAligned, matTransformFinal, new Size(cols, rows),
                                               InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

                                // C#の配列に戻して完了
                                return MatToMatrix(matFinalAligned, rows, cols);
                            }
                        }
                    }
                }
            }
        }

        // 補助関数: ガウシアン窓の作成
        private static void CreateGaussianWindow(Mat window, int rows, int cols)
        {
            int centerX = cols / 2;
            int centerY = rows / 2;
            double sigmaX = cols / 3.0;
            double sigmaY = rows / 3.0;

            unsafe
            {
                float* pWin = (float*)window.Data;
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        double dx = (c - centerX) / sigmaX;
                        double dy = (r - centerY) / sigmaY;
                        pWin[r * cols + c] = (float)Math.Exp(-(dx * dx + dy * dy) / 2.0);
                    }
                }
            }
        }

        // 補助関数: アフィン変換行列の作成
        private static Mat CreateTransformMatrix(float x, float y)
        {
            Mat mat = new Mat(2, 3, MatType.CV_32FC1);
            mat.Set<float>(0, 0, 1f);
            mat.Set<float>(0, 1, 0f);
            mat.Set<float>(0, 2, x);
            mat.Set<float>(1, 0, 0f);
            mat.Set<float>(1, 1, 1f);
            mat.Set<float>(1, 2, y);
            return mat;
        }

        // ※ MatrixToMat と MatToMatrix は以前のfixed句のままで動作します
        // 補助関数：float[,] から Mat へ変換
        private static Mat MatrixToMat(float[,] matrix)
        {
            int rows = matrix.GetLength(0);
            int cols = matrix.GetLength(1);

            Mat mat = null;

            try
            {
                mat = new Mat(rows, cols, MatType.CV_32FC1);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return null;
            }
            // 全体のバイトサイズを計算 (要素数 * floatのサイズ(4バイト))
            long byteSize = matrix.Length * sizeof(float);

            // fixed句で2次元配列のメモリ位置を固定し、ポインタ経由で一括コピー
            unsafe
            {
                fixed (float* pMatrix = matrix)
                {
                    // Buffer.MemoryCopy を使用して、配列から Mat.Data（ポインタ）へ超高速コピー
                    Buffer.MemoryCopy(pMatrix, (void*)mat.Data, byteSize, byteSize);
                }
            }
            return mat;
        }

        // 補助関数：Mat から float[,] へ変換
        private static float[,] MatToMatrix(Mat mat, int rows, int cols)
        {
            float[,] matrix = new float[rows, cols];
            long byteSize = matrix.Length * sizeof(float);

            unsafe
            {
                fixed (float* pMatrix = matrix)
                {
                    // mat.Dataポインタから配列へ直接一括コピー
                    Buffer.MemoryCopy((void*)mat.Data, pMatrix, byteSize, byteSize);
                }
            }
            return matrix;
        }

    }

}
