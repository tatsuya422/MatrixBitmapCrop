using System;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace MatrixBitmapCrop
{
    static class Program
    {
        // Windows Formsの表示設定を行い、メイン画面を起動する。
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 3D解析アプリと同じく、タイトルバーのテーマは維持して操作部をクラシック表示する。
            Application.VisualStyleState = VisualStyleState.NonClientAreaEnabled;
            try
            {
                var options = new CropOptions();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--language" && i + 1 < args.Length)
                    {
                        string language = args[++i].ToLowerInvariant();
                        if (language != "en" && language != "ja")
                            throw new ArgumentException("Use --language en or --language ja.");
                        options.Language = language == "ja" ? CropLanguage.Japanese : CropLanguage.English;
                    }
                    else if (args[i] == "--folder" && i + 1 < args.Length)
                        options.FolderPath = args[++i];
                    else
                        throw new ArgumentException("Usage: MatrixBitmapCrop.exe [--language en|ja] [--folder path]");
                }
                using (var form = CropLauncher.CreateWindow(options))
                    Application.Run(form);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Matrix / Bitmap", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
