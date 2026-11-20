using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    public sealed partial class MainForm : Form
    {
        // 画像表示と入力ファイルの選択に使用するコントロール。
        private readonly PictureBox picture = new PictureBox();
        private readonly Panel canvas = new Panel();
        private readonly Label status = new Label();
        private readonly NumericUpDown marginPixels = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 10,
            Value = 1,
            Width = 65
        };
        private readonly TextBox folderPath = new TextBox { ReadOnly = true };
        private readonly FolderHistory folderHistory = new FolderHistory();
        private readonly ComboBox imagePath = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox matrixPath = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private bool loadingFolder;
        private Bitmap source;
        private MatrixScale referenceScale;
        private readonly Label physicalInfo = new Label { AutoSize = true, Visible = false };
        private readonly Label maskPhysicalInfo = new Label
        {
            Height = 90,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(6),
            TextAlign = ContentAlignment.TopLeft
        };
        private double zoom = 1;
        private readonly ComboBox pointMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
        private readonly Label zoomLabel = new Label { AutoSize = true, Padding = new Padding(4, 8, 4, 0) };
        private bool fitMode = true;
        // 指定点は表示倍率によらない元画像の座標で保持する。null は未指定。
        private PointF? origin;
        private PointF? axisEnd;
        private PointF? heightPoint;
        private RotatedSelection selection;

        // 画面を構成し、ボタンやマウスのイベントを登録する。
        public MainForm() : this(CropLanguage.English) { }

        public MainForm(CropLanguage language)
        {
            if (!Enum.IsDefined(typeof(CropLanguage), language))
                throw new ArgumentOutOfRangeException(nameof(language));
            Language = language;
            Text = T("Matrix / Bitmap 斜め範囲切り出し");
            // DLLに埋め込んだアイコンを使い、呼出元アプリのアイコンに依存しない。
            using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("MatrixBitmapCrop.AppIcon.ico"))
            using (var icon = new Icon(iconStream))
                Icon = (Icon)icon.Clone();
            Width = 1250;
            Height = 850;
            MinimumSize = new Size(800, 550);
            var sidebar = BuildSidebar();

            // 参考画像を切り替えたら、対応するMatrix候補も選択する。
            imagePath.SelectedIndexChanged += (s, e) =>
            {
                if (loadingFolder || !imagePath.Enabled || imagePath.SelectedItem == null)
                    return;
                try
                {
                    LoadImage(imagePath.Text);
                    matrixPath.SelectedItem = FolderFiles.PreferredMatrix(imagePath.Text, matrixPath.Items.Cast<string>().ToArray());
                    SetPhysicalScale(MatrixScale.Read(matrixPath.Text));
                }
                catch (Exception ex)
                {
                    picture.Image = null;
                    if (source != null)
                        source.Dispose();
                    source = null;
                    ClearSelection();
                    CropMessages.Show(this, ex.Message, T("画像読み込みエラー"));
                }
            };

            status.Dock = DockStyle.Bottom;
            status.Height = 28;
            status.Text = T("入力フォルダーを指定してください。");
            canvas.Dock = DockStyle.Fill;
            canvas.AutoScroll = true;
            canvas.BackColor = Color.DimGray;
            picture.SizeMode = PictureBoxSizeMode.StretchImage;
            picture.Paint += PaintSelection;
            picture.MouseClick += ClickPoint;
            picture.MouseDown += MaskMouseDown;
            picture.MouseMove += MaskMouseMove;
            picture.MouseUp += MaskMouseUp;
            picture.MouseDown += CropMoveDown;
            picture.MouseMove += CropMoveMouse;
            picture.MouseUp += CropMoveUp;
            picture.MouseLeave += (s, e) => { if (!cropDragging && cropHandleIndex < 0) picture.Cursor = Cursors.Default; };
            picture.MouseCaptureChanged += (s, e) =>
            {
                if (!picture.Capture)
                {
                    cropDragging = false;
                    cropDragOriginal = null;
                    cropHandleIndex = -1;
                    cropDragRotationReference = null;
                    picture.Cursor = Cursors.Default;
                }
            };
            picture.MouseEnter += (s, e) => canvas.Focus();
            canvas.MouseWheel += (s, e) =>
            {
                if (source == null)
                    return;
                var handled = e as HandledMouseEventArgs;
                if (handled != null)
                    handled.Handled = true;
                ChangeZoom(zoom * (e.Delta > 0 ? 1.25 : .8), canvas.PointToClient(Cursor.Position));
            };
            canvas.Controls.Add(picture);
            Controls.Add(canvas);
            Controls.Add(zeroPreview);
            Controls.Add(sidebar);
            Controls.Add(status);
            canvas.Resize += (s, e) =>
            {
                if (fitMode)
                    Fit();
                else
                    CenterPicture();
            };
            LocalizeInitialTexts(this);
            ArrangeSidebarPairs(sidebar);
            CompactCropControls(operationTabs.TabPages[0]);
            LegacyAppearance.Apply(sidebar);
            LegacyAppearance.ApplyTabs(operationTabs);
            status.BackColor = LegacyAppearance.HeaderBlue;
        }

        // 左側に操作を集約する。機能が増えた場合もタブを追加して整理できる。
        private Control BuildSidebar()
        {
            var sidebar = new Panel
            {
                Dock = DockStyle.Left,
                Width = 330,
                Padding = new Padding(6),
                BackColor = Color.Azure
            };
            var tabs = new TabControl { Dock = DockStyle.Fill, Name = "operationTabs", Multiline = true };
            var cropTab = new TabPage(T("切り出し")) { BackColor = Color.Azure, AutoScroll = true };
            tabs.TabPages.Add(cropTab);
            tabs.TabPages.Add(BuildMaskTab());
            tabs.TabPages.Add(BuildReduceTab());
            tabs.TabPages.Add(BuildCorrectionTab());
            tabs.TabPages.Add(BuildAlignmentTab());
            zeroBaselineTab = BuildZeroBaselineTab();
            tabs.TabPages.Add(zeroBaselineTab);
            smoothingTab = BuildSmoothingTab();
            tabs.TabPages.Add(smoothingTab);
            operationTabs = tabs;
            tabs.SelectedIndexChanged += (s, e) =>
            {
                CancelCropDrag();
                maskDragging = false;
                picture.Capture = false;
                picture.Invalidate();
                UpdateZeroPreviewVisibility();
                UpdateStatus();
            };
            sidebar.Controls.Add(tabs);
            // 全タブ共通の終了ボタン。通常のボタン（高さ30）より大きくする。
            var exitPanel = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(7, 7, 7, 3) };
            var exitButton = new Button
            {
                Name = "exitButton",
                Text = T("終了"),
                Dock = DockStyle.Fill,
                UseVisualStyleBackColor = true,
            };
            exitButton.Click += (s, e) => Close();
            exitPanel.Controls.Add(exitButton);
            sidebar.Controls.Add(exitPanel);

            var cropTools = CreateSidebarLayout(cropTab);
            AddSidebarLabel(cropTools, T("入力フォルダー"));
            folderPath.Multiline = true;
            folderPath.Height = 48;
            folderPath.ScrollBars = ScrollBars.Vertical;
            AddSidebarControl(cropTools, folderPath);
            AddSidebarButton(cropTools, T("フォルダーを開く"), () => ChooseFolder(this, EventArgs.Empty));
            AddSidebarLabel(cropTools, T("参考画像（BMP / TXTから自動生成）"));
            imagePath.DropDownWidth = 700;
            AddSidebarControl(cropTools, imagePath);
            AddSidebarLabel(cropTools, T("対象TXT（一覧の全件を保存）"));
            matrixPath.DropDownWidth = 700;
            AddSidebarControl(cropTools, matrixPath);

            AddSidebarLabel(cropTools, T("指定する点"));
            pointMode.Items.AddRange(new object[] { T("① 始点"), T("② 幅方向の終点"), T("③ 高さ方向の点") });
            pointMode.SelectedIndex = 0;
            pointMode.SelectedIndexChanged += (s, e) => UpdateStatus();
            AddSidebarControl(cropTools, pointMode);
            AddSidebarButton(cropTools, T("指定をクリア"), ClearSelection);
            // 入力フォルダー～点指定は、ラベルと入力欄の間隔を小さくまとめる。
            foreach (Control control in cropTools.Controls)
            {
                control.Margin = new Padding(0, 1, 0, 3);
                if (control is Label)
                    control.Height = 20;
            }
            BuildSelectionEdit(cropTools);
            BuildRotationAngle(cropTools);

            // 内側指定は初期表示のタブに置き、スクロールで必ず到達できるようにする。
            AddSidebarLabel(cropTools, T("四辺から内側へ（1～10ピクセル）"));
            marginPixels.BackColor = Color.Honeydew;
            AddSidebarControl(cropTools, marginPixels);
            marginPixels.ValueChanged += (s, e) =>
            {
                picture.Invalidate();
                UpdateStatus();
            };
            AddSidebarLabel(cropTools, T("赤枠：指定範囲\r\n緑枠：実際に保存する内側の範囲"));
            AddSidebarButton(cropTools, T("全TXTを切り出して保存"), () => Export(this, EventArgs.Empty));
            AddSidebarLabel(cropTools, T("指定した3点は、タブを切り替えても保持されます。"), 40);

            // 表示操作も起動時の切り出しタブにまとめる。
            /*
            AddSidebarLabel(cropTools, "画像の表示倍率");
            AddSidebarButton(cropTools, "拡大 ＋", () => ChangeZoom(zoom * 1.25));
            AddSidebarButton(cropTools, "縮小 −", () => ChangeZoom(zoom / 1.25));
            */
            //AddSidebarButton(cropTools, "100%（X方向等倍）", () => ChangeZoom(1));
            AddSidebarButton(cropTools, T("全体表示"), () =>
            {
                fitMode = true;
                Fit();
            });
            zoomLabel.Text = T("画像を読み込むと倍率を表示します。");
            AddSidebarControl(cropTools, zoomLabel);
            AddSidebarControl(cropTools, physicalInfo);
            return sidebar;
        }

        // 切り出しだけ余白を詰める。説明・実寸欄は文字数に応じた高さとし、空白を予約しない。
        private static void CompactCropControls(TabPage page)
        {
            var layout = (TableLayoutPanel)page.Controls[0];
            layout.SuspendLayout();
            try
            {
                layout.Padding = new Padding(7, 5, 7, 5);
                foreach (Control control in layout.Controls)
                {
                    control.Margin = new Padding(control.Margin.Left, 1, control.Margin.Right, 4);
                    var label = control as Label;
                    if (label != null)
                    {
                        label.AutoSize = true;
                        label.TextAlign = ContentAlignment.TopLeft;
                        int row = layout.GetRow(label);
                        Control next = row + 1 < layout.RowCount ? layout.GetControlFromPosition(0, row + 1) : null;
                        bool caption = next is ComboBox || next is NumericUpDown || next is TextBoxBase;
                        label.Margin = new Padding(label.Margin.Left, 3, label.Margin.Right, caption ? 0 : 3);
                    }
                    if (control is Button) control.Height = 28;
                    if (control is CheckBox) control.Height = Math.Max(24, control.GetPreferredSize(new Size(280, 0)).Height);
                }
            }
            finally { layout.ResumeLayout(true); }
        }

        // 高さは内容に合わせ、画面が低い場合はTabPageのスクロールで表示する。
        private static void ArrangeSidebarPairs(Control parent)
        {
            var layout = parent as TableLayoutPanel;
            if (layout != null && layout.ColumnCount == 1)
            {
                for (int row = 0; row + 1 < layout.RowCount; row++)
                {
                    var title = layout.GetControlFromPosition(0, row) as Label;
                    Control editor = layout.GetControlFromPosition(0, row + 1);
                    bool input = editor is ComboBox || editor is NumericUpDown || editor is TextBoxBase
                        || editor is ListBox || editor is DataGridView;
                    if (title == null || !input) continue;

                    // 見出し直下は1px、次の項目までは約12px空けて対応関係を見やすくする。
                    title.AutoSize = true;
                    title.TextAlign = ContentAlignment.BottomLeft;
                    title.Margin = new Padding(title.Margin.Left, 4, title.Margin.Right, 0);
                    editor.Margin = new Padding(editor.Margin.Left, 1, editor.Margin.Right, 8);
                }
            }
            foreach (Control child in parent.Controls)
                ArrangeSidebarPairs(child);
        }

        // 高さは内容に合わせ、画面が低い場合はTabPageのスクロールで表示する。
        private static TableLayoutPanel CreateSidebarLayout(TabPage page)
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 0,
                Padding = new Padding(7)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(layout);
            return layout;
        }

        private static void AddSidebarControl(TableLayoutPanel layout, Control control)
        {
            int row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Dock = DockStyle.Top;
            control.Margin = new Padding(0, 2, 0, 6);
            layout.Controls.Add(control, 0, row);
        }

        private static void AddSidebarLabel(TableLayoutPanel layout, string text, int height = 30)
        {
            AddSidebarControl(layout, new Label
            {
                Text = text,
                Height = height,
                TextAlign = ContentAlignment.MiddleLeft
            });
        }

        private static void AddSidebarButton(TableLayoutPanel layout, string text, Action action)
        {
            var button = new Button { Text = text, Height = 30 };
            button.Click += (s, e) => action();
            AddSidebarControl(layout, button);
        }

        // フォルダー選択ダイアログを表示して自動読み込みを開始する。
        private void ChooseFolder(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = T("Matrix TXTのフォルダーを指定（BMPは省略可能）"), SelectedPath = folderHistory.GetInitialFolder(folderPath.Text) })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    Cursor = Cursors.WaitCursor;
                    LoadFolder(dialog.SelectedPath);
                    // 読み込み成功時のみ記憶する。キャンセルや読み込み失敗では更新しない。
                    try
                    {
                        folderHistory.Save(dialog.SelectedPath);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                    {
                        CropMessages.Show(this, T("フォルダーは開きましたが、履歴を保存できませんでした。\n") + ex.Message, T("フォルダー履歴"));
                    }
                }
                catch (Exception ex)
                {
                    CropMessages.Show(this, ex.Message, T("フォルダー読み込みエラー"));
                }
                finally { Cursor = Cursors.Default; }
            }
        }

        // フォルダー直下のBMPとTXTを検出し、一覧と初期選択を設定する。
        private void LoadFolder(string directory)
        {
            var images = FolderFiles.Find(directory, ".bmp");
            var matrices = FolderFiles.Find(directory, ".txt");
            if (matrices.Length == 0)
                throw new InvalidDataException(T("指定フォルダー直下に Matrix の *.txt が必要です。"));
            // 先頭に番号のあるファイルは番号順で最初を選ぶ（1, 2, …, 10）。
            string firstMatrix = matrices.OrderBy(p =>
            {
                long number;
                string prefix = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(p), @"^\d+").Value;
                return long.TryParse(prefix, out number) ? number : long.MaxValue;
            }).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).First();
            if (images.Length > 0)
                LoadImage(images[0]);
            else
                SetReferenceImage(MatrixPreview.Create(firstMatrix));
            loadingFolder = true;
            try
            {
                folderPath.Text = Path.GetFullPath(directory);
                imagePath.Items.Clear();
                imagePath.Enabled = images.Length > 0;
                if (images.Length > 0)
                    imagePath.Items.AddRange(images);
                else
                    imagePath.Items.Add(T("[TXTから自動生成] ") + Path.GetFileName(firstMatrix));
                imagePath.SelectedIndex = 0;
                matrixPath.Items.Clear();
                matrixPath.Items.AddRange(matrices);
                matrixPath.SelectedItem = images.Length > 0 ? FolderFiles.PreferredMatrix(images[0], matrices) : firstMatrix;
            }
            finally
            {
                loadingFolder = false;
            }
            SetPhysicalScale(MatrixScale.Read(matrixPath.Text));
            UpdateCorrectionProfiles();
            UpdateAlignmentFiles();
            UpdateStatus();
        }

        // 画像を複製して読み込み、元ファイルをロックし続けないようにする。
        private void LoadImage(string path)
        {
            SetReferenceImage(ReadBitmap(path));
        }

        private static Bitmap ReadBitmap(string path)
        {
            using (var loaded = new Bitmap(path))
                return new Bitmap(loaded);
        }

        // 実画像とMatrixからの生成画像を、同じ座標指定・マスク処理に使用する。
        private void SetReferenceImage(Bitmap replacement)
        {
            referenceScale = null;
            picture.Image = null;
            if (source != null)
                source.Dispose();
            source = replacement;
            picture.Image = source;
            ClearMasks();
            ClearSelection();
            fitMode = true;
            Fit();
        }

        // 表示の縦横比は実寸に合わせる。画面上のmm長はズームとモニターに依存する。
        private void SetPhysicalScale(MatrixScale scale)
        {
            // フィルター等でMatrixだけ縮小されたデータも、表示・指定・保存を同じ格子にそろえる。
            // 画像全体はヘッダーのWIDTH/HEIGHTに対応するため、実寸の縦横比は変わらない。
            if (scale != null && source != null && (source.Width != scale.Columns || source.Height != scale.Rows))
            {
                if (scale.Columns < 1 || scale.Rows < 1 || (long)scale.Columns * scale.Rows > 100000000)
                    throw new InvalidDataException(T("参考画像を対応させるMatrixの寸法が不正です。"));
                var aligned = new Bitmap(scale.Columns, scale.Rows, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    using (var graphics = Graphics.FromImage(aligned))
                    {
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        graphics.DrawImage(source, new Rectangle(0, 0, aligned.Width, aligned.Height),
                            new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
                    }
                }
                catch { aligned.Dispose(); throw; }
                SetReferenceImage(aligned);
            }
            referenceScale = scale;
            string info = scale == null ? T("実寸ヘッダーなし：画素座標で表示") : string.Format(
                T("実寸 {0:0.###} × {1:0.###} mm\r\n列 {2} ／ 行 {3}\r\n1画素 X={4:0.######} mm\r\n         Y={5:0.######} mm"),
                scale.WidthMm, scale.HeightMm, scale.Columns, scale.Rows, scale.PitchX, scale.PitchY);
            physicalInfo.Text = maskPhysicalInfo.Text = info;
            physicalInfo.Visible = true;
            ConfigureMaskUnits();
            SyncCropSize();
            UpdateReductionInfo();
            Fit();
        }

        private double DisplayBaseHeight
        {
            get
            {
                return referenceScale == null ? source.Height : source.Width * referenceScale.HeightMm / referenceScale.WidthMm;
            }
        }

        // 画像全体が表示領域に収まる倍率を計算する。
        private void Fit()
        {
            if (source == null || canvas.ClientSize.Width < 20 || canvas.ClientSize.Height < 20)
                return;
            zoom = Math.Min((double)(canvas.ClientSize.Width - 20) / source.Width, (canvas.ClientSize.Height - 20) / DisplayBaseHeight);
            zoom = Math.Min(1, Math.Max(.01, zoom));
            picture.Size = new Size(Math.Max(1, (int)Math.Round(source.Width * zoom)), Math.Max(1, (int)Math.Round(DisplayBaseHeight * zoom)));
            canvas.AutoScrollPosition = Point.Empty;
            picture.Location = Point.Empty;
            CenterPicture();
            picture.Invalidate();
            zoomLabel.Text = string.Format("{0:0.#}%", zoom * 100);
        }

        private static void AddZoomButton(FlowLayoutPanel panel, string text, Action action)
        {
            var button = new Button { Text = text, Width = 80 };
            button.Click += (s, e) => action();
            panel.Controls.Add(button);
        }

        // 余白がある方向は中央へ配置し、大きい方向はスクロール位置を維持する。
        // 指定点はPictureBox内の座標なので、中央配置でも切り出し・マスクの計算は共通。
        private void CenterPicture()
        {
            if (source == null)
                return;
            picture.Location = new Point(
                Math.Max(0, (canvas.ClientSize.Width - picture.Width) / 2) + canvas.AutoScrollPosition.X,
                Math.Max(0, (canvas.ClientSize.Height - picture.Height) / 2) + canvas.AutoScrollPosition.Y);
        }

        private void ChangeZoom(double value)
        {
            ChangeZoom(value, new Point(canvas.ClientSize.Width / 2, canvas.ClientSize.Height / 2));
        }

        // マウス位置（または画面中央）を基準に拡大縮小し、表示位置を調整する。
        private void ChangeZoom(double value, Point anchor)
        {
            if (source == null)
                return;
            double sx = (anchor.X - picture.Left) * (double)source.Width / picture.Width;
            double sy = (anchor.Y - picture.Top) * (double)source.Height / picture.Height;
            fitMode = false;
            zoom = Math.Max(.01, Math.Min(8, value));
            canvas.AutoScrollPosition = Point.Empty;
            picture.Location = Point.Empty;
            picture.Size = new Size(Math.Max(1, (int)Math.Round(source.Width * zoom)), Math.Max(1, (int)Math.Round(DisplayBaseHeight * zoom)));
            canvas.AutoScrollPosition = new Point(Math.Max(0, (int)Math.Round(sx * picture.Width / source.Width - anchor.X)),
                Math.Max(0, (int)Math.Round(sy * picture.Height / source.Height - anchor.Y)));
            CenterPicture();
            zoomLabel.Text = string.Format("{0:0.#}%", zoom * 100);
            picture.Invalidate();
        }

        // 3点と選択範囲を消し、始点の指定に戻す。
        private void ClearSelection()
        {
            CancelCropDrag();
            moveCrop.Checked = false;
            origin = axisEnd = heightPoint = null;
            selection = null;
            ResetRotationReference();
            pointMode.SelectedIndex = 0;
            SyncCropSize();
            picture.Invalidate();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (ZeroBaselineActive)
            {
                status.Text = zeroPreviewStatus;
                return;
            }
            if (MaskModeActive && source != null)
            {
                status.Text = string.Format(T("マスク {0}箇所 ／ {1}"), maskShapes.Count, MaskInstruction());
                return;
            }
            if (SmoothingActive && selection != null)
            {
                status.Text = T("赤枠内が平滑化の対象です。");
                return;
            }
            status.Text = source == null ? T("入力フォルダーを指定してください。") :
                string.Format(T("画像 {0}×{1} ／ {2} をクリックしてください。{3}"), source.Width, source.Height, pointMode.Text,
                    selection == null ? "" : GetOutputSizeText());
        }

        private string GetOutputSizeText()
        {
            int width = selection.Width - (int)marginPixels.Value * 2;
            int height = selection.Height - (int)marginPixels.Value * 2;
            return width > 0 && height > 0
                ? string.Format(T(" 保存 {0}×{1}画素（各辺から{2}px内側）"), width, height, marginPixels.Value)
                : T(" 内側指定が大きすぎるため保存できません。");
        }

        // クリック位置を元画像の座標へ変換し、選択中の点だけを更新する。
        private void ClickPoint(object sender, MouseEventArgs e)
        {
            if (suppressCropClick) { suppressCropClick = false; return; }
            if (MaskModeActive)
            {
                MaskClick(e);
                return;
            }
            if (moveCrop.Checked && selection != null) return;
            if (source == null || e.Button != MouseButtons.Left)
                return;
            var point = new PointF(Math.Max(0, Math.Min(source.Width - 1, e.X * (float)source.Width / picture.Width)),
                Math.Max(0, Math.Min(source.Height - 1, e.Y * (float)source.Height / picture.Height)));
            if (pointMode.SelectedIndex == 0)
                origin = point;
            else if (pointMode.SelectedIndex == 1)
                axisEnd = point;
            else
                heightPoint = point;
            selection = null;
            if (origin.HasValue && axisEnd.HasValue && heightPoint.HasValue)
            {
                try
                {
                    selection = referenceScale == null
                        ? RotatedSelection.Create(origin.Value, axisEnd.Value, heightPoint.Value)
                        : RotatedSelection.CreatePhysical(origin.Value, axisEnd.Value, heightPoint.Value,
                            referenceScale.WidthMm / source.Width, referenceScale.HeightMm / source.Height);
                    SyncCropSize();
                    moveCrop.Checked = true;
                    ResetRotationReference();
                }
                catch (ArgumentException ex)
                {
                    CropMessages.Show(this, ex.Message);
                }
            }
            else
                pointMode.SelectedIndex = !origin.HasValue ? 0 : !axisEnd.HasValue ? 1 : 2;
            picture.Invalidate();
            UpdateStatus();
        }

        // 元画像の座標を、現在の表示倍率に合わせた描画座標へ変換する。
        private PointF Display(PointF point)
        {
            return new PointF(point.X * picture.Width / source.Width, point.Y * picture.Height / source.Height);
        }

        // 選択した矩形と、番号付きの指定点を画像上に重ねて描画する。
        private void PaintSelection(object sender, PaintEventArgs e)
        {
            if (MaskModeActive)
            {
                PaintMasks(e.Graphics);
                return;
            }
            using (var pen = new Pen(Color.Red, 2))
            {
                if (origin.HasValue && axisEnd.HasValue)
                    e.Graphics.DrawLine(pen, Display(origin.Value), Display(axisEnd.Value));
                if (selection != null)
                {
                    e.Graphics.DrawPolygon(pen, new[]
                    {
                        Display(selection.Origin),
                        Display(selection.AxisEnd),
                        Display(selection.FourthCorner),
                        Display(selection.ThirdCorner)
                    });
                }
            }
            if (!SmoothingActive && selection != null && selection.Width > marginPixels.Value * 2 && selection.Height > marginPixels.Value * 2)
            {
                var inner = selection.Inset((int)marginPixels.Value);
                using (var pen = new Pen(Color.Lime, 2))
                {
                    e.Graphics.DrawPolygon(pen, new[]
                    {
                        Display(inner.Origin),
                        Display(inner.AxisEnd),
                        Display(inner.FourthCorner),
                        Display(inner.ThirdCorner)
                    });
                }
            }
            DrawPoint(e.Graphics, origin, "1", Color.Yellow);
            DrawPoint(e.Graphics, axisEnd, "2", Color.Lime);
            DrawPoint(e.Graphics, selection == null ? heightPoint : selection.ThirdCorner, "3", Color.Cyan);
            if (selection != null) DrawPoint(e.Graphics, selection.FourthCorner, "4", Color.White);
        }

        private void DrawPoint(Graphics graphics, PointF? point, string label, Color color)
        {
            if (!point.HasValue || source == null)
                return;
            var p = Display(point.Value);
            using (var brush = new SolidBrush(color))
            {
                graphics.FillEllipse(brush, p.X - 5, p.Y - 5, 10, 10);
                graphics.DrawEllipse(Pens.Black, p.X - 5, p.Y - 5, 10, 10);
                graphics.FillRectangle(Brushes.Black, p.X + 5, p.Y + 5, 18, 20);
                graphics.DrawString(label, Font, brush, p.X + 6, p.Y + 6);
            }
        }

        // 入力フォルダーの下に日時名の保存先を決め、画像とMatrixを出力する。
        private void Export(object sender, EventArgs e)
        {
            if (source == null || selection == null || !Directory.Exists(folderPath.Text))
            {
                CropMessages.Show(this, T("入力フォルダーと3点の選択範囲を指定してください。"));
                return;
            }
            try
            {
                Cursor = Cursors.WaitCursor;
                var outputArea = selection.Inset((int)marginPixels.Value);
                string destination = FolderFiles.NewOutputDirectory(folderPath.Text, outputArea.Width, outputArea.Height);
                // 保存開始時に対象を確定する。一覧で選んだ1件だけでなく全件を処理する。
                var files = FolderFiles.Find(folderPath.Text, ".txt");
                if (files.Length == 0)
                    throw new InvalidDataException(T("対象のMatrix TXTがありません。"));

                // 表示中の画像とワーカースレッドが同じBitmapを操作しないよう複製する。
                using (var reference = new Bitmap(source))
                using (var progress = new BatchProgressForm(files, reference, outputArea, destination))
                {
                    progress.SetLanguage(Language);
                    progress.ShowDialog(this);
                    if (progress.Error != null)
                        throw progress.Error;

                    var result = progress.Result;
                    string failures = string.Join("\n", result.Errors.Concat(result.ReferenceErrors).Concat(result.CopyErrors).Take(5));
                    CropMessages.Show(this,
                        string.Format(T("一括処理が終了しました。\nTXT成功: {0} / {1}\nTXT失敗: {2}\nコピー成功: {3}\nコピー失敗: {4}\n\n保存先: {5}\n\n詳細: BatchResult.log"),
                            result.SuccessCount, files.Length, result.Errors.Count, result.CopyCount, result.CopyErrors.Count, destination)
                        + (failures.Length == 0 ? "" : T("\n\n失敗理由（先頭5件）:\n") + failures),
                        T("一括切り出し結果"));
                    // OKで結果を確認した後、保存したTXTと同じ範囲の参考画像へ切り替える。
                    // 全件失敗の場合は、元のフォルダーと選択範囲を残す。
                    if (result.SuccessCount > 0)
                    {
                        try
                        {
                            LoadFolder(destination);
                            folderHistory.Save(destination);
                        }
                        catch (Exception ex)
                        {
                            CropMessages.Show(this, T("切り出しの保存は完了していますが、保存先の読み込みまたは履歴保存に失敗しました。\n")
                                + destination + "\n" + ex.Message, T("保存先の読み込み"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CropMessages.Show(this, ex.Message, T("切り出しエラー"));
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        // 終了時に画像のメモリを解放する。
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            picture.Image = null;
            if (source != null)
            {
                source.Dispose();
                source = null;
            }
            base.OnFormClosed(e);
        }
    }

    public static class FolderFiles
    {
        // 指定された拡張子のファイルを、子フォルダーを含めず名前順に取得する。
        public static string[] Find(string directory, string extension)
        {
            return Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(p => string.Equals(Path.GetExtension(p), extension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static string[] FindNonMatrixFiles(string directory)
        {
            return Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        internal static string GetAvailableLogName(string[] attachments, string stem)
        {
            var names = new HashSet<string>(attachments.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            string name = stem + ".log";
            for (int i = 1; names.Contains(name); i++)
                name = stem + "_" + i.ToString("D3", CultureInfo.InvariantCulture) + ".log";
            return name;
        }

        internal static void CopyAttachments(string[] attachments, string destination, BatchResult result,
            int completed, Action<int, int, string> report)
        {
            for (int i = 0; i < attachments.Length; i++)
            {
                string name = Path.GetFileName(attachments[i]);
                report?.Invoke(completed + i, completed + attachments.Length, name);
                try
                {
                    CopyAttachment(attachments[i], Path.Combine(destination, name));
                    result.CopyCount++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.CopyErrors.Add(name + ": " + ex.Message);
                }
            }
        }

        private static void CopyAttachment(string input, string output)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(output), Path.GetRandomFileName() + ".partial");
            using (var source = File.OpenRead(input))
            {
                var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
                try
                {
                    using (target)
                        source.CopyTo(target);
                    File.Move(temporary, output);
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
        }

        // 画像名に対応するTXT、次にMeasureを含むTXTを優先して選択する。
        public static string PreferredMatrix(string image, string[] matrices)
        {
            string token = Path.GetFileNameWithoutExtension(image).Split('_')[0];
            return matrices.OrderByDescending(p => Path.GetFileNameWithoutExtension(p).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenByDescending(p => Path.GetFileNameWithoutExtension(p).IndexOf("Measure", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        // 既存の出力を上書きしないよう、同名があるときは連番を付ける。
        public static string NewOutputDirectory(string directory, int width, int height)
        {
            if (width < 1 || height < 1)
                throw new ArgumentOutOfRangeException("width", "切り出しサイズが不正です。");
            string name = "RECT_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss", System.Globalization.CultureInfo.InvariantCulture);
            string result = Path.Combine(directory, name);
            for (int i = 1; Directory.Exists(result) || File.Exists(result); i++)
                result = Path.Combine(directory, name + "_" + i.ToString("D3"));
            return result;
        }
    }
}
