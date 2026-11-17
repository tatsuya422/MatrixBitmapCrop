using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    public sealed partial class MainForm
    {
        private TabControl operationTabs;
        private TabPage maskTab;
        private readonly List<MaskShape> maskShapes = new List<MaskShape>();
        private readonly List<PointF> polygonPoints = new List<PointF>();
        private readonly ComboBox maskKind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly NumericUpDown maskLineWidth = new NumericUpDown { Minimum = 1, Maximum = 300, Value = 20 };
        private readonly CheckBox maskIgnoreZero = new CheckBox { Text = "0を周囲の参照値から除外", Checked = true, Height = 24 };
        private readonly CheckBox maskZeroAdjust = new CheckBox { Text = "最小値を0に補正して保存", Checked = true, Height = 28 };
        private readonly CheckBox maskFlatEdges = new CheckBox { Text = "画像端の傾斜を調整する", Checked = true, Height = 28 };
        private readonly NumericUpDown maskFlatPercent = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 50, Increment = 5, DecimalPlaces = 0 };
        private readonly ListBox maskList = new ListBox { Height = 80 };
        private readonly Label maskHelp = new Label { Height = 48 };
        private readonly Label maskWidthLabel = new Label { Height = 20, Text = "線の幅（元画像のピクセル数）" };
        private bool maskDragging;
        private PointF maskStart, maskEnd;

        private bool MaskModeActive
        {
            get
            {
                return operationTabs != null && operationTabs.SelectedTab == maskTab;
            }
        }

        private TabPage BuildMaskTab()
        {
            maskTab = new TabPage(T("マスク")) { BackColor = Color.Azure, AutoScroll = true };
            var tools = CreateSidebarLayout(maskTab);
            AddSidebarButton(tools, T("入力フォルダーを開く"), () => ChooseFolder(this, EventArgs.Empty));
            AddSidebarLabel(tools, T("参考画像は「切り出し」と共通です。\r\n指定範囲を周囲の値から補間します。"), 40);
            AddSidebarControl(tools, maskPhysicalInfo);
            AddSidebarLabel(tools, T("指定形状"), 20);
            maskKind.Items.AddRange(new object[] { T("矩形（ドラッグ）"), T("多角形（頂点をクリック）"), T("幅付き線・溝（ドラッグ）") });
            maskKind.SelectedIndex = 0;
            maskKind.SelectedIndexChanged += (s, e) =>
            {
                CancelMaskDrawing();
                maskHelp.Text = MaskInstruction();
                maskLineWidth.Enabled = maskKind.SelectedIndex == 2;
            };
            AddSidebarControl(tools, maskKind);
            AddSidebarControl(tools, maskWidthLabel);
            maskLineWidth.Enabled = false;
            AddSidebarControl(tools, maskLineWidth);
            maskHelp.Text = MaskInstruction();
            AddSidebarControl(tools, maskHelp);
            AddSidebarButton(tools, T("多角形を確定"), FinishPolygon);
            AddSidebarButton(tools, T("描画中の指定を取り消す"), CancelMaskDrawing);
            AddSidebarLabel(tools, T("登録済みの範囲（黄色で表示）"), 20);
            AddSidebarControl(tools, maskList);
            maskList.SelectedIndexChanged += (s, e) => picture.Invalidate();
            AddSidebarButton(tools, T("選択した範囲を削除"), () =>
            {
                int index = maskList.SelectedIndex;
                if (index >= 0)
                {
                    maskShapes.RemoveAt(index);
                    RefreshMasks();
                }
            });
            AddSidebarButton(tools, T("すべてのマスクをクリア"), ClearMasks);
            AddSidebarControl(tools, maskIgnoreZero);
            AddSidebarControl(tools, maskZeroAdjust);
            AddSidebarControl(tools, maskFlatEdges);
            AddSidebarLabel(tools, T("画像端の平坦化率（％）"), 20);
            AddSidebarControl(tools, maskFlatPercent);
            maskFlatEdges.CheckedChanged += (s, e) => maskFlatPercent.Enabled = maskFlatEdges.Checked;
            AddSidebarLabel(tools, T("0％：従来の反り補間\r\n50％：従来と平らな結果の中間\r\n100％：近傍の正常値で平らに埋める"), 60);
            AddSidebarLabel(tools, T("0点補正ON：補間後の各TXTの最小値を全セルから差し引きます。0も最小値の判定に含めます。"), 60);
            AddSidebarButton(tools, T("マスク設定を保存"), SaveMaskDefinition);
            AddSidebarButton(tools, T("マスク設定を読込"), LoadMaskDefinition);
            AddSidebarButton(tools, T("全TXTをマスク補間して保存"), ExportMasks);
            AddSidebarLabel(tools, T("端のはみ出しは画像内へ適用。十字溝は縦横の線を重ねて指定します。"), 45);
            return maskTab;
        }

        private string MaskInstruction()
        {
            if (maskKind.SelectedIndex == 1)
                return T("頂点を順にクリックし「多角形を確定」。右クリックで描画を取消。");
            if (maskKind.SelectedIndex == 2)
                return T("溝の中心線をドラッグ。交差する溝は線を重ねて登録。");
            return T("左ボタンで矩形をドラッグ。複数箇所を続けて指定できます。");
        }

        private PointF MaskPoint(Point point)
        {
            // ドラッグ中は画像外の座標も保持し、最終的に画像内だけへ適用する。
            return new PointF((float)(point.X * MaskWidth / picture.Width),
                (float)(point.Y * MaskHeight / picture.Height));
        }

        private double MaskWidth
        {
            get
            {
                return referenceScale == null ? source.Width : referenceScale.WidthMm;
            }
        }
        private double MaskHeight
        {
            get
            {
                return referenceScale == null ? source.Height : referenceScale.HeightMm;
            }
        }

        private PointF MaskDisplay(PointF point)
        {
            return new PointF((float)(point.X * picture.Width / MaskWidth), (float)(point.Y * picture.Height / MaskHeight));
        }

        private void ConfigureMaskUnits()
        {
            bool millimeters = referenceScale != null;
            maskLineWidth.DecimalPlaces = millimeters ? 3 : 0;
            maskLineWidth.Minimum = millimeters ? .001m : 1m;
            maskLineWidth.Maximum = millimeters ? 100m : 300m;
            maskLineWidth.Increment = millimeters ? .1m : 1m;
            maskLineWidth.Value = millimeters ? 1m : 20m;
            maskWidthLabel.Text = millimeters ? T("線の幅（mm）") : T("線の幅（元画像のピクセル数）");
        }

        private void MaskMouseDown(object sender, MouseEventArgs e)
        {
            if (!MaskModeActive || source == null || e.Button != MouseButtons.Left || maskKind.SelectedIndex == 1)
                return;
            maskStart = maskEnd = MaskPoint(e.Location);
            maskDragging = true;
            picture.Capture = true;
        }

        private void MaskMouseMove(object sender, MouseEventArgs e)
        {
            if (MaskModeActive && source != null)
            {
                PointF point = MaskPoint(e.Location);
                status.Text = string.Format(T("X={0:0.###}  Y={1:0.###} {2} ／ マスク {3}箇所"), point.X, point.Y,
                    referenceScale == null ? "px" : "mm", maskShapes.Count);
            }
            if (!maskDragging || source == null)
                return;
            maskEnd = MaskPoint(e.Location);
            picture.Invalidate();
        }

        private void MaskMouseUp(object sender, MouseEventArgs e)
        {
            if (!maskDragging || e.Button != MouseButtons.Left)
                return;
            maskEnd = MaskPoint(e.Location);
            maskDragging = false;
            picture.Capture = false;
            float dx = Math.Abs(maskEnd.X - maskStart.X), dy = Math.Abs(maskEnd.Y - maskStart.Y);
            double minimum = referenceScale == null ? 1 : Math.Min(referenceScale.PitchX, referenceScale.PitchY);
            if (maskKind.SelectedIndex == 0 ? dx >= minimum && dy >= minimum : dx * dx + dy * dy >= minimum * minimum)
            {
                maskShapes.Add(DraftDragShape());
                RefreshMasks();
            }
            picture.Invalidate();
        }

        private MaskShape DraftDragShape()
        {
            return new MaskShape
            {
                Kind = maskKind.SelectedIndex == 2 ? MaskShapeKind.Line : MaskShapeKind.Rectangle,
                LineWidth = (float)maskLineWidth.Value,
                Points = new List<PointF> { maskStart, maskEnd }
            };
        }

        private void MaskClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                CancelMaskDrawing();
                return;
            }
            if (source == null || e.Button != MouseButtons.Left || maskKind.SelectedIndex != 1)
                return;
            PointF point = MaskPoint(e.Location);
            if (polygonPoints.Count == 0 || polygonPoints[polygonPoints.Count - 1] != point)
                polygonPoints.Add(point);
            picture.Invalidate();
        }

        private void FinishPolygon()
        {
            if (polygonPoints.Count < 3)
            {
                CropMessages.Show(this, T("多角形は3点以上指定してください。"));
                return;
            }
            maskShapes.Add(new MaskShape { Kind = MaskShapeKind.Polygon, Points = new List<PointF>(polygonPoints) });
            polygonPoints.Clear();
            RefreshMasks();
        }

        private void CancelMaskDrawing()
        {
            maskDragging = false;
            picture.Capture = false;
            polygonPoints.Clear();
            picture.Invalidate();
        }

        private void ClearMasks()
        {
            CancelMaskDrawing();
            maskShapes.Clear();
            RefreshMasks();
        }

        private void RefreshMasks()
        {
            maskList.Items.Clear();
            for (int i = 0; i < maskShapes.Count; i++)
                maskList.Items.Add((i + 1) + ": " + maskShapes[i].ToDisplayString(Language));
            picture.Invalidate();
            UpdateStatus();
        }

        private void PaintMasks(Graphics graphics)
        {
            if (source == null)
                return;
            for (int i = 0; i < maskShapes.Count; i++)
                PaintMaskShape(graphics, maskShapes[i], maskList.SelectedIndex == i ? Color.OrangeRed : Color.Yellow);
            if (maskDragging)
                PaintMaskShape(graphics, DraftDragShape(), Color.Cyan);
            if (polygonPoints.Count > 0)
            {
                using (var pen = new Pen(Color.Cyan, 2))
                    if (polygonPoints.Count > 1)
                        graphics.DrawLines(pen, polygonPoints.Select(MaskDisplay).ToArray());
                foreach (var point in polygonPoints)
                {
                    var p = MaskDisplay(point);
                    graphics.FillEllipse(Brushes.Cyan, p.X - 3, p.Y - 3, 6, 6);
                }
            }
        }

        private void PaintMaskShape(Graphics graphics, MaskShape shape, Color color)
        {
            using (var path = shape.CreatePath())
            using (var transform = new Matrix())
            using (var brush = new SolidBrush(Color.FromArgb(65, color)))
            using (var pen = new Pen(color, 2))
            {
                if (path.PointCount == 0)
                    return;
                transform.Scale((float)(picture.Width / MaskWidth), (float)(picture.Height / MaskHeight));
                path.Transform(transform);
                graphics.FillPath(brush, path);
                graphics.DrawPath(pen, path);
            }
        }

        private MaskDefinition CurrentMask()
        {
            return new MaskDefinition
            {
                ImageWidth = source.Width,
                ImageHeight = source.Height,
                CoordinatesInMillimeters = referenceScale != null,
                PhysicalWidthMm = referenceScale == null ? 0 : referenceScale.WidthMm,
                PhysicalHeightMm = referenceScale == null ? 0 : referenceScale.HeightMm,
                MatrixColumns = referenceScale == null ? source.Width : referenceScale.Columns,
                MatrixRows = referenceScale == null ? source.Height : referenceScale.Rows,
                CellWidthMm = referenceScale == null ? 0 : referenceScale.PitchX,
                CellHeightMm = referenceScale == null ? 0 : referenceScale.PitchY,
                Shapes = new List<MaskShape>(maskShapes)
            };
        }

        private void SaveMaskDefinition()
        {
            if (source == null || maskShapes.Count == 0)
            {
                CropMessages.Show(this, T("画像とマスク範囲を指定してください。"));
                return;
            }
            using (var dialog = new SaveFileDialog { Filter = T("マスク定義|*.xml"), FileName = "MaskDefinition.xml" })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    try
                    {
                        CurrentMask().Save(dialog.FileName);
                    }
                    catch (Exception ex) { CropMessages.Show(this, ex.Message, T("保存エラー")); }
        }

        private void LoadMaskDefinition()
        {
            if (source == null)
            {
                CropMessages.Show(this, T("先に入力フォルダーと画像を読み込んでください。"));
                return;
            }
            using (var dialog = new OpenFileDialog { Filter = T("マスク定義|*.xml") })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    try
                    {
                        var definition = MaskDefinition.Load(dialog.FileName);
                        if (definition.CoordinatesInMillimeters)
                        {
                            if (referenceScale == null)
                                throw new InvalidDataException(T("mmマスクを使用するには実寸ヘッダーが必要です。"));
                        }
                        else
                        {
                            if (definition.ImageWidth != source.Width || definition.ImageHeight != source.Height)
                                throw new InvalidDataException(T("旧版の画素マスクは同じ画像寸法で読み込んでください。"));
                            // 旧版の画素指定は、現在の実寸を使って多角形へ変換する。
                            if (referenceScale != null)
                            {
                                var converted = new List<MaskShape>();
                                foreach (var shape in definition.Shapes)
                                    using (var path = shape.CreatePath())
                                    using (var transform = new Matrix())
                                    {
                                        path.Flatten();
                                        transform.Scale((float)(MaskWidth / source.Width), (float)(MaskHeight / source.Height));
                                        path.Transform(transform);
                                        converted.Add(new MaskShape { Kind = MaskShapeKind.Polygon, Points = path.PathPoints.ToList() });
                                    }
                                definition.Shapes = converted;
                            }
                        }
                        CancelMaskDrawing();
                        maskShapes.Clear();
                        maskShapes.AddRange(definition.Shapes);
                        RefreshMasks();
                    }
                    catch (Exception ex) { CropMessages.Show(this, ex.Message, T("読込エラー")); }
        }

        private void ExportMasks()
        {
            if (source == null || maskShapes.Count == 0 || !Directory.Exists(folderPath.Text))
            {
                CropMessages.Show(this, T("入力フォルダーとマスク範囲を指定してください。"));
                return;
            }
            if (polygonPoints.Count > 0 || maskDragging)
            {
                CropMessages.Show(this, T("描画中の範囲を確定するか、取り消してください。"));
                return;
            }
            try
            {
                var files = FolderFiles.Find(folderPath.Text, ".txt");
                if (files.Length == 0)
                    throw new InvalidDataException(T("Matrix TXTがありません。"));
                string name = "MASK_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss", System.Globalization.CultureInfo.InvariantCulture);
                string destination = Path.Combine(folderPath.Text, name);
                for (int i = 1; Directory.Exists(destination) || File.Exists(destination); i++)
                    destination = Path.Combine(folderPath.Text, name + "_" + i.ToString("D3"));
                var definition = CurrentMask();
                bool ignoreZero = maskIgnoreZero.Checked;
                bool zeroAdjust = maskZeroAdjust.Checked;
                bool flatEdges = maskFlatEdges.Checked;
                int flatPercent = (int)maskFlatPercent.Value;
                using (var reference = new Bitmap(source))
                using (var progress = new BatchProgressForm(T("全TXTのマスク補間"), files.Length,
                    report => MaskProcessor.ExportBatch(files, reference, definition, ignoreZero, destination, report, zeroAdjust, flatEdges, flatPercent)))
                {
                    progress.SetLanguage(Language);
                    progress.ShowDialog(this);
                    if (progress.Error != null)
                        throw progress.Error;
                    CropMessages.Show(this, string.Format(T("マスク補間が終了しました。\n成功: {0}/{1}\n失敗: {2}\nコピー成功: {3}\nコピー失敗: {4}\n\n{5}\n詳細: MaskResult.log"),
                        progress.Result.SuccessCount, files.Length, progress.Result.Errors.Count,
                        progress.Result.CopyCount, progress.Result.CopyErrors.Count, destination), T("処理結果"));
                }
            }
            catch (Exception ex) { CropMessages.Show(this, ex.Message, T("マスク処理エラー")); }
        }
    }
}
