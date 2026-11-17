using System.Drawing;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    // 3D解析アプリと共通の配色・立体枠。DLLから全体のVisualStyleStateは変更しない。
    internal static class LegacyAppearance
    {
        internal static readonly Color PanelGreen = Color.FromArgb(144, 238, 144);
        internal static readonly Color HeaderBlue = Color.FromArgb(135, 206, 250);
        internal static readonly Color ButtonGray = Color.FromArgb(224, 224, 224);

        internal static void Apply(Control control)
        {
            Apply(control, PanelGreen);
        }

        internal static void ApplyDialog(Control control)
        {
            Apply(control, SystemColors.Control);
        }

        private static void Apply(Control control, Color background)
        {
            control.BackColor = background;
            control.ForeColor = Color.Black;

            // 枠付きの情報表示欄は、編集欄と同じ白地で表示範囲を明確にする。
            var label = control as Label;
            if (label != null && label.BorderStyle != BorderStyle.None)
            {
                label.BackColor = Color.White;
                return;
            }

            var button = control as Button;
            if (button != null)
            {
                button.UseVisualStyleBackColor = false;
                button.FlatStyle = FlatStyle.Standard;
                button.BackColor = ButtonGray;
            }
            var check = control as CheckBox;
            if (check != null)
            {
                check.UseVisualStyleBackColor = false;
                check.FlatStyle = FlatStyle.Standard;
            }
            var page = control as TabPage;
            if (page != null)
                page.UseVisualStyleBackColor = false;

            var combo = control as ComboBox;
            if (combo != null)
            {
                combo.FlatStyle = FlatStyle.Standard;
                combo.BackColor = Color.White;
                return;
            }
            var number = control as NumericUpDown;
            if (number != null)
            {
                number.BorderStyle = BorderStyle.Fixed3D;
                number.BackColor = Color.White;
                // 数値入力の右端にもパネル色を残さない。
                foreach (Control child in number.Controls)
                    if (child is TextBox) child.BackColor = Color.White;
                return;
            }
            var text = control as TextBoxBase;
            if (text != null)
            {
                text.BorderStyle = BorderStyle.Fixed3D;
                text.BackColor = Color.White;
                return;
            }
            var list = control as ListBox;
            if (list != null)
            {
                list.BorderStyle = BorderStyle.Fixed3D;
                list.BackColor = Color.White;
                return;
            }
            var grid = control as DataGridView;
            if (grid != null)
            {
                grid.EnableHeadersVisualStyles = false;
                grid.BorderStyle = BorderStyle.Fixed3D;
                grid.BackgroundColor = Color.White;
                grid.DefaultCellStyle.BackColor = Color.White;
                grid.DefaultCellStyle.ForeColor = Color.Black;
                grid.DefaultCellStyle.SelectionBackColor = Color.Blue;
                grid.DefaultCellStyle.SelectionForeColor = Color.White;
                grid.ColumnHeadersDefaultCellStyle.BackColor = ButtonGray;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;
                return;
            }
            var progress = control as ProgressBar;
            if (progress != null)
            {
                progress.BackColor = SystemColors.Control;
                progress.ForeColor = SystemColors.Highlight;
                return;
            }
            foreach (Control child in control.Controls)
                Apply(child, background);
        }

        internal static void ApplyTabs(TabControl tabs)
        {
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.ItemSize = new Size(96, 27);
            tabs.DrawItem += (sender, e) =>
            {
                if (e.Index < 0 || e.Index >= tabs.TabCount) return;
                bool selected = e.Index == tabs.SelectedIndex;
                using (var brush = new SolidBrush(selected ? Color.Blue : ButtonGray))
                    e.Graphics.FillRectangle(brush, e.Bounds);
                ControlPaint.DrawBorder3D(e.Graphics, e.Bounds,
                    selected ? Border3DStyle.Sunken : Border3DStyle.Raised);
                using (var font = new Font(tabs.Font, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, font, e.Bounds,
                        selected ? Color.White : Color.Black,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
        }
    }
}
