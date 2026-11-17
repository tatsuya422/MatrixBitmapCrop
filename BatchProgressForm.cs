using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    // 別スレッドで一括処理し、現在のファイル名を表示するポップアップ。
    public sealed class BatchProgressForm : Form
    {
        private readonly Label currentFile;
        private readonly ProgressBar progressBar;
        private bool running = true;
        private readonly string originalTitle;
        public CropLanguage Language { get; private set; } = CropLanguage.English;

        public void SetLanguage(CropLanguage language)
        {
            if (!Enum.IsDefined(typeof(CropLanguage), language))
                throw new ArgumentOutOfRangeException(nameof(language));
            Language = language;
            Text = T(originalTitle);
            currentFile.Text = T("処理を準備しています…");
        }

        private string T(string text) { return CropLocalization.Text(text, Language); }

        public BatchResult Result
        {
            get; private set;
        }
        public Exception Error
        {
            get; private set;
        }

        public BatchProgressForm(string[] files, Bitmap source, RotatedSelection area, string destination)
            : this("Matrix データを一括切り出し中", files.Length,
                  report => MatrixCropper.ExportBatch(files, source, area, destination, report))
        {
        }

        // 切り出しとマスク補間で同じ進捗ポップアップを使用する。
        public BatchProgressForm(string title, int count, Func<Action<int, int, string>, BatchResult> worker)
        {
            originalTitle = title;
            Text = T(title);
            ClientSize = new Size(620, 135);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ControlBox = false;
            ShowInTaskbar = false;

            currentFile = new Label
            {
                Location = new Point(20, 15),
                Size = new Size(580, 60),
                AutoEllipsis = true,
                Text = T("処理を準備しています…")
            };
            progressBar = new ProgressBar
            {
                Location = new Point(20, 85),
                Size = new Size(580, 25),
                Maximum = Math.Max(1, count)
            };
            Controls.Add(currentFile);
            Controls.Add(progressBar);
            LegacyAppearance.ApplyDialog(this);

            Shown += async (sender, e) =>
            {
                // UIスレッドで作成したProgressを介して表示を更新する。
                var progress = new Progress<Tuple<int, int, string>>(value =>
                {
                    if (IsDisposed)
                        return;

                    progressBar.Value = value.Item1;
                    currentFile.Text = string.Format(
                        T("{0} / {1} ファイル\r\n{2}"),
                        Math.Min(value.Item1 + 1, value.Item2), value.Item2, T(value.Item3));
                });

                try
                {
                    Result = await Task.Run(() => worker(
                        (index, total, name) => ((IProgress<Tuple<int, int, string>>)progress)
                            .Report(Tuple.Create(index, total, name))));
                }
                catch (Exception ex)
                {
                    Error = ex;
                }
                finally
                {
                    running = false;
                    Close();
                }
            };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 保存中に閉じる操作で処理が中断されることを防ぐ。
            if (running)
                e.Cancel = true;

            base.OnFormClosing(e);
        }
    }
}
