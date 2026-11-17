using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MatrixBitmapCrop
{
    // 翻訳表はDLLへ埋め込み、ウィンドウごとの言語で参照する。
    // CurrentCultureなど呼出元アプリの共通設定は変更しない。
    internal static class CropLocalization
    {
        private static readonly Dictionary<string, string> English = Read();
        private static readonly Regex FormatToken = new Regex(@"\{(\d+)(?:[^{}]*)\}");
        private static readonly List<KeyValuePair<Regex, string>> NumericMessages = BuildNumericMessages();
        private static readonly Regex FileNames = new Regex(
            @"(?:[A-Za-z]:\\|\\\\)[^\r\n<>""|]*|[^\s\\/:""<>|]+\.(?:txt|bmp|tmp|3m|xml|png)\b", RegexOptions.IgnoreCase);
        private static readonly Regex Fragments = new Regex(string.Join("|", English.Keys
            .Where(k => (k.Length >= 4 || k == " 列") && !k.Contains("{"))
            .OrderByDescending(k => k.Length).Select(Regex.Escape)), RegexOptions.Compiled);

        private static Dictionary<string, string> Read()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var stream = typeof(CropLocalization).Assembly.GetManifestResourceStream("MatrixBitmapCrop.Localization.tsv"))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0 && !line.StartsWith("#", StringComparison.Ordinal))
                        result.Add(Unescape(line.Substring(0, tab)), Unescape(line.Substring(tab + 1)));
                }
            }
            return result;
        }

        private static string Unescape(string value)
        {
            return value.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t");
        }

        // 数値が既に埋め込まれた処理エラーも、行・列番号を保持して翻訳する。
        private static List<KeyValuePair<Regex, string>> BuildNumericMessages()
        {
            var result = new List<KeyValuePair<Regex, string>>();
            foreach (var pair in English)
            {
                var tokens = FormatToken.Matches(pair.Key);
                if (tokens.Count == 0) continue;
                int previous = 0;
                var expression = new StringBuilder();
                foreach (Match token in tokens)
                {
                    expression.Append(Regex.Escape(pair.Key.Substring(previous, token.Index - previous)));
                    expression.Append("(?<a" + token.Groups[1].Value + @">[-+0-9.,]+)");
                    previous = token.Index + token.Length;
                }
                expression.Append(Regex.Escape(pair.Key.Substring(previous)));
                result.Add(new KeyValuePair<Regex, string>(new Regex(expression.ToString()), pair.Value));
            }
            return result;
        }

        private static string TranslateMessage(string text)
        {
            foreach (var message in NumericMessages)
                text = message.Key.Replace(text, match => FormatToken.Replace(message.Value,
                    token => match.Groups["a" + token.Groups[1].Value].Value));
            return Fragments.Replace(text, match => English[match.Value]);
        }

        internal static string Text(string text, CropLanguage language)
        {
            if (language == CropLanguage.Japanese || string.IsNullOrEmpty(text)) return text;
            string value;
            if (English.TryGetValue(text, out value)) return value;
            // 日本語を含む保存先やファイル名は、表示するときも原文のまま保持する。
            var result = new StringBuilder();
            int previous = 0;
            foreach (Match file in FileNames.Matches(text))
            {
                result.Append(TranslateMessage(text.Substring(previous, file.Index - previous)));
                result.Append(file.Value);
                previous = file.Index + file.Length;
            }
            result.Append(TranslateMessage(text.Substring(previous)));
            return result.ToString();
        }

        internal static CropLanguage LanguageOf(Control control)
        {
            var form = control as Form ?? (control == null ? null : control.FindForm());
            var main = form as MainForm;
            if (main != null) return main.Language;
            var progress = form as BatchProgressForm;
            return progress == null ? CropLanguage.English : progress.Language;
        }
    }

    internal static class CropMessages
    {
        internal static DialogResult Show(Control owner, string message, string caption = "Matrix / Bitmap",
            MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
        {
            var language = CropLocalization.LanguageOf(owner);
            return MessageBox.Show(owner, CropLocalization.Text(message, language),
                CropLocalization.Text(caption, language), buttons, icon);
        }
    }
}
