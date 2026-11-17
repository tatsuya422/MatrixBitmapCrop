using System;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    public sealed partial class MainForm
    {
        private readonly NumericUpDown rotationAngle = new NumericUpDown
        {
            Minimum = -180,
            Maximum = 180,
            DecimalPlaces = 2,
            Increment = 0.1M, // 上で右へ、下で左へ、常に0.1°ずつ回転する。
            Value = 0
        };
        private RotatedSelection rotationReference;
        private RotatedSelection cropDragRotationReference;
        private decimal appliedRotationAngle;
        private bool updatingRotation;

        private void BuildRotationAngle(TableLayoutPanel layout)
        {
            rotationAngle.Font = new System.Drawing.Font(Font.FontFamily, 14F);
            AddSidebarLabel(layout, T("回転角度（°：▲ 右／▼ 左・0.1°刻み）"));
            AddSidebarControl(layout, rotationAngle);
            rotationAngle.ValueChanged += (s, e) => ApplyRotationAngle();
            rotationAngle.KeyDown += RotationAngleKeyDown;
            rotationAngle.Enabled = false;
        }

        private void RotationAngleKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            // Valueを参照して、編集中の文字列を確定してから適用する。
            decimal value = rotationAngle.Value;
            ApplyRotationAngle();
        }

        private void ResetRotationReference()
        {
            rotationReference = selection;
            appliedRotationAngle = 0;
            updatingRotation = true;
            try
            {
                rotationAngle.Value = 0;
            }
            finally { updatingRotation = false; }
            rotationAngle.Enabled = selection != null;
        }

        // サイズ変更後は、新しい枠を現在の角度の基準として保持する。
        private void RebaseRotationReference()
        {
            if (selection == null || source == null)
                return;
            rotationReference = selection.Rotate(-(double)appliedRotationAngle, CropPitchX, CropPitchY);
        }

        private void ApplyRotationAngle()
        {
            if (updatingRotation || selection == null || source == null)
                return;
            if (rotationReference == null)
                RebaseRotationReference();
            decimal angle = rotationAngle.Value;
            if (angle == appliedRotationAngle)
                return;
            CancelCropDrag();
            // 毎回同じ基準枠から計算し、連打・再Enterで角度や丸め誤差を積み重ねない。
            selection = rotationReference.Rotate((double)angle, CropPitchX, CropPitchY);
            appliedRotationAngle = angle;
            UpdateCropMarks();
        }
    }
}
