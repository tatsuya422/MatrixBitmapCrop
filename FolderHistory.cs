using System;
using System.IO;
using System.Security;
using System.Text;

namespace MatrixBitmapCrop
{
    // ユーザーごとに保存し、アプリの再起動・更新後も最後のフォルダーを引き継ぐ。
    public sealed class FolderHistory
    {
        private readonly string settingsPath;

        public FolderHistory(string path = null)
        {
            settingsPath = path ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MatrixBitmapCrop", "LastFolder.txt");
        }

        public string GetInitialFolder(string currentFolder)
        {
            if (Directory.Exists(currentFolder))
                return currentFolder;
            try
            {
                if (File.Exists(settingsPath))
                {
                    string saved = File.ReadAllText(settingsPath, Encoding.UTF8).Trim();
                    if (Directory.Exists(saved))
                        return saved;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }

            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string preferred = Path.Combine(documents, "malcom", "SRS360");
            // 既定のフォルダーが存在しない環境ではマイドキュメントを表示する。
            return Directory.Exists(preferred) ? preferred : documents;
        }

        public void Save(string folder)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            File.WriteAllText(settingsPath, Path.GetFullPath(folder), new UTF8Encoding(false));
        }
    }
}
