using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    public sealed partial class MainForm
    {
        private readonly NumericUpDown cropWidth = new NumericUpDown { Minimum = 0.001M, Maximum = 1000000, DecimalPlaces = 3, Increment = 1 };
        private readonly NumericUpDown cropHeight = new NumericUpDown { Minimum = 0.001M, Maximum = 1000000, DecimalPlaces = 3, Increment = 1 };
        private readonly Label cropSizeUnits = new Label { Height = 20 };
        private readonly Label cropWidthLabel = new Label { Height = 20 };
        private readonly Label cropHeightLabel = new Label { Height = 20 };
        private readonly CheckBox moveCrop = new CheckBox { Text = "枠を移動（枠内をドラッグ）", Height = 30 };
        private bool cropDragging;
        private PointF cropDragStart;
        private RotatedSelection cropDragOriginal;
        private bool syncingCropSize;
        private bool applyingCropSize;
        private int cropHandleIndex = -1;
        private bool suppressCropClick;
        private PointF cropDragHandlePoint;

        private void BuildSelectionEdit(TableLayoutPanel layout)
        {
            AddSidebarControl(layout, cropSizeUnits);
            cropSizeUnits.Margin = new Padding(0, 1, 0, 3);
            var sizeInputs = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 2,
                Padding = Padding.Empty
            };
            sizeInputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            sizeInputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            sizeInputs.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sizeInputs.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            // 1行目に幅・高さと単位、2行目に対応する数値スピン欄をそろえる。
            cropWidthLabel.Dock = cropHeightLabel.Dock = DockStyle.Top;
            cropWidthLabel.TextAlign = cropHeightLabel.TextAlign = ContentAlignment.MiddleLeft;
            cropWidthLabel.Margin = new Padding(0, 0, 4, 2);
            cropHeightLabel.Margin = new Padding(4, 0, 0, 2);
            sizeInputs.Controls.Add(cropWidthLabel, 0, 0);
            sizeInputs.Controls.Add(cropHeightLabel, 1, 0);
            cropWidth.Font = new Font(Font.FontFamily, 18F);
            cropHeight.Font = new Font(Font.FontFamily, 18F);
            cropWidth.Dock = cropHeight.Dock = DockStyle.Top;
            cropWidth.Margin = new Padding(0, 0, 4, 0);
            cropHeight.Margin = new Padding(4, 0, 0, 0);
            sizeInputs.Controls.Add(cropWidth, 0, 1);
            sizeInputs.Controls.Add(cropHeight, 1, 1);
            AddSidebarControl(layout, sizeInputs);
            cropWidth.ValueChanged += (s, e) => ApplyCropSize();
            cropHeight.ValueChanged += (s, e) => ApplyCropSize();
            cropWidth.KeyDown += CropSizeKeyDown;
            cropHeight.KeyDown += CropSizeKeyDown;
            AddSidebarButton(layout, T("サイズを適用（中心を保持）"), ApplyCropSize);
            AddSidebarControl(layout, moveCrop);
            SyncCropSize();
        }

        private double CropPitchX
        {
            get
            {
                return referenceScale == null ? 1 : referenceScale.WidthMm / source.Width;
            }
        }
        private double CropPitchY
        {
            get
            {
                return referenceScale == null ? 1 : referenceScale.HeightMm / source.Height;
            }
        }

        private double CropWidthValue()
        {
            return selection.Width * Math.Sqrt(Math.Pow(selection.Ux * CropPitchX, 2) + Math.Pow(selection.Uy * CropPitchY, 2));
        }

        private double CropHeightValue()
        {
            return selection.Height * Math.Sqrt(Math.Pow(selection.Vx * CropPitchX, 2) + Math.Pow(selection.Vy * CropPitchY, 2));
        }

        private void SyncCropSize()
        {
            syncingCropSize = true;
            try
            {
                bool ready = source != null && selection != null;
                cropWidth.Enabled = cropHeight.Enabled = moveCrop.Enabled = ready;
                bool mm = referenceScale != null;
                string suffix = mm ? " (mm)" : " (px)";
                cropWidthLabel.Text = T("枠の幅") + suffix;
                cropHeightLabel.Text = T("枠の高さ") + suffix;
                cropSizeUnits.Text = T("サイズ指定の単位：") + (mm ? T("mm（実寸）") : T("px（元画像）"));
                cropWidth.DecimalPlaces = cropHeight.DecimalPlaces = mm ? 3 : 0;
                cropWidth.Minimum = cropHeight.Minimum = mm ? 0.001M : 1M;
                if (ready)
                {
                    cropWidth.Value = Math.Max(cropWidth.Minimum, Math.Min(cropWidth.Maximum, (decimal)CropWidthValue()));
                    cropHeight.Value = Math.Max(cropHeight.Minimum, Math.Min(cropHeight.Maximum, (decimal)CropHeightValue()));
                }
            }
            finally { syncingCropSize = false; }
        }

        private void CropSizeKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            ApplyCropSize();
        }

        private void UpdateCropMarks()
        {
            origin = selection.Origin;
            axisEnd = selection.AxisEnd;
            heightPoint = selection.ThirdCorner;
            SyncCropSize();
            picture.Invalidate();
            UpdateStatus();
        }

        private void ApplyCropSize()
        {
            if (syncingCropSize || applyingCropSize || selection == null || source == null)
                return;
            applyingCropSize = true;
            try
            {
                // Valueの参照で編集中の文字列も確定する。確定イベントによる再入は防ぐ。
                decimal width = cropWidth.Value, height = cropHeight.Value;
                var resized = selection.Resize((double)width / CropWidthValue(), (double)height / CropHeightValue());
                if ((long)resized.Width * resized.Height > 30000000)
                    throw new ArgumentException(T("保存画素数が3000万画素を超えます。サイズを小さくしてください。"));
                resized.Inset((int)marginPixels.Value);
                selection = resized;
                RebaseRotationReference();
                moveCrop.Checked = true;
                UpdateCropMarks();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is OverflowException)
            {
                CropMessages.Show(this, ex.Message, T("サイズ変更"));
            }
            finally { applyingCropSize = false; }
        }

        private PointF CropMousePoint(Point point)
        {
            return new PointF(point.X * (float)source.Width / picture.Width, point.Y * (float)source.Height / picture.Height);
        }

        private void CropMoveDown(object sender, MouseEventArgs e)
        {
            suppressCropClick = false;
            if (operationTabs.SelectedIndex != 0 || source == null || e.Button != MouseButtons.Left)
                return;
            int handle = HitCropHandle(e.Location);
            if (handle >= 0)
            {
                cropHandleIndex = handle;
                cropDragOriginal = selection;
                cropDragStart = CropMousePoint(e.Location);
                cropDragHandlePoint = selection == null ? (handle == 0 ? origin.Value : axisEnd.Value) : CropHandleGeometry.Corner(selection, handle);
                suppressCropClick = true;
                picture.Capture = true;
                UpdateCropCursor(e.Location);
                return;
            }
            if (!moveCrop.Checked || selection == null) return;
            var point = CropMousePoint(e.Location);
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(new[] { selection.Origin, selection.AxisEnd, selection.FourthCorner, selection.ThirdCorner });
                if (!path.IsVisible(point))
                    return;
            }
            cropDragging = true;
            cropDragStart = point;
            cropDragOriginal = selection;
            cropDragRotationReference = rotationReference;
            suppressCropClick = true;
            picture.Capture = true;
            picture.Cursor = Cursors.SizeAll;
        }

        private void CropMoveMouse(object sender, MouseEventArgs e)
        {
            if (source == null)
                return;
            if (cropHandleIndex >= 0)
            {
                var handlePoint = CropMousePoint(e.Location);
                if (cropDragOriginal == null)
                {
                    var target = new PointF(cropDragHandlePoint.X + handlePoint.X - cropDragStart.X, cropDragHandlePoint.Y + handlePoint.Y - cropDragStart.Y);
                    if (cropHandleIndex == 0) origin = target;
                    else if (cropHandleIndex == 1) axisEnd = target;
                    picture.Invalidate();
                    UpdateStatus();
                }
                else
                {
                    try
                    {
                        // マウスを押した位置と丸印の中心の差を保ち、つかんだ瞬間の跳びを防ぐ。
                        var corner = CropHandleGeometry.Corner(cropDragOriginal, cropHandleIndex);
                        var target = new PointF(corner.X + handlePoint.X - cropDragStart.X, corner.Y + handlePoint.Y - cropDragStart.Y);
                        selection = CropHandleGeometry.ResizeCorner(cropDragOriginal, cropHandleIndex, target, (int)marginPixels.Value * 2 + 1);
                        RebaseRotationReference();
                        UpdateCropMarks();
                    }
                    catch (ArgumentException) { } // 大きすぎるドラッグでは直前の有効な枠を保持する。
                }
                return;
            }
            if (!cropDragging) { UpdateCropCursor(e.Location); return; }
            var point = CropMousePoint(e.Location);
            selection = cropDragOriginal.Move(point.X - cropDragStart.X, point.Y - cropDragStart.Y);
            if (cropDragRotationReference != null)
                rotationReference = cropDragRotationReference.Move(point.X - cropDragStart.X, point.Y - cropDragStart.Y);
            UpdateCropMarks();
        }

        private void CropMoveUp(object sender, MouseEventArgs e)
        {
            if ((!cropDragging && cropHandleIndex < 0) || e.Button != MouseButtons.Left)
                return;
            CropMoveMouse(sender, e);
            CancelCropDrag();
        }

        private void CancelCropDrag()
        {
            cropDragging = false;
            cropDragOriginal = null;
            cropDragRotationReference = null;
            cropHandleIndex = -1;
            picture.Capture = false;
            picture.Cursor = Cursors.Default;
        }

        private int HitCropHandle(Point location)
        {
            if (operationTabs.SelectedIndex != 0 || source == null) return -1;
            double radius = 10.0 * picture.DeviceDpi / 96;
            int nearest = -1;
            double best = radius * radius;
            for (int i = 0; i < (selection == null ? 2 : 4); i++)
            {
                PointF? point = selection == null ? (i == 0 ? origin : axisEnd) : CropHandleGeometry.Corner(selection, i);
                if (!point.HasValue) continue;
                var display = Display(point.Value);
                double distance = Math.Pow(display.X - location.X, 2) + Math.Pow(display.Y - location.Y, 2);
                if (distance <= best) { best = distance; nearest = i; }
            }
            return nearest;
        }

        private void UpdateCropCursor(Point location)
        {
            if (operationTabs.SelectedIndex != 0 || source == null) { picture.Cursor = Cursors.Default; return; }
            if (cropDragging) { picture.Cursor = Cursors.SizeAll; return; }
            int handle = cropHandleIndex >= 0 ? cropHandleIndex : HitCropHandle(location);
            if (handle >= 0)
            {
                if (selection == null) { picture.Cursor = Cursors.Hand; return; }
                int axis = CropHandleGeometry.CursorAxis(CropHandleGeometry.Corner(selection, handle),
                    CropHandleGeometry.Corner(selection, (handle + 2) % 4), (double)picture.Width / source.Width, (double)picture.Height / source.Height);
                picture.Cursor = new[] { Cursors.SizeWE, Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW }[axis];
                return;
            }
            if (moveCrop.Checked && selection != null)
            {
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(new[] { selection.Origin, selection.AxisEnd, selection.FourthCorner, selection.ThirdCorner });
                    if (path.IsVisible(CropMousePoint(location))) { picture.Cursor = Cursors.SizeAll; return; }
                }
            }
            picture.Cursor = Cursors.Cross;
        }
    }
}
