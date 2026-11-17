# DLLの組み込みと日本語・英語表示

## 構成

MatrixView2Dと同様に、画面・処理を持つDLLと、起動用EXEを分離しました。

| ファイル | 用途 |
| --- | --- |
| MatrixBitmapCrop.Controls.dll | 全5タブの画面、切り出し・マスク・縮小・温度補正・位置合わせ |
| MatrixBitmapCrop.exe | .NET Framework 4.8.1の単体起動用EXE |
| MatrixBitmapCrop.VS2017.exe | .NET Framework 4.7.2の単体起動用EXE |
| Localization.tsv | 日本語と英語の翻訳表。ビルド時にDLLへ埋め込み |
| Examples/CropHost | DLLを別のWinFormsアプリから呼び出す例 |

配布ZIPのRelease481とReleaseVS2017には、対応するEXE・DLLと依存DLL一式を収録しています。
2種類のControls.dllは同名なので、使用する.NET版の一方を選んでください。

## 単体で起動

MatrixView2Dと同じく初期言語は英語です。各Releaseフォルダー内のStartJapanese.cmdで日本語起動、StartEnglish.cmdで英語起動できます。

```bat
MatrixBitmapCrop.exe --language ja
MatrixBitmapCrop.exe --language en --folder "C:\MeasurementData"
```

VS2017版ではEXE名をMatrixBitmapCrop.VS2017.exeに置き換えます。通常は最大化して起動します。

## 別ウィンドウで呼び出す

呼出元プロジェクトにMatrixBitmapCrop.Controls.dllを参照追加し、WinFormsのSTA UIスレッド（ボタンのClickイベントなど）で呼びます。Task.Run内から画面を開かないでください。

```csharp
using MatrixBitmapCrop;

private MainForm cropWindow;

private async void OpenCrop_Click(object sender, EventArgs e)
{
    if (cropWindow != null && !cropWindow.IsDisposed)
    {
        cropWindow.Activate();
        return;
    }

    cropWindow = CropLauncher.ShowWindow(this, new CropOptions
    {
        FolderPath = @"C:\MeasurementData", // nullなら未選択で開く
        Language = CropLanguage.Japanese,   // Englishも選択可
        AllowUserClose = true,
        StartMaximized = true
    });

    try
    {
        await cropWindow.InitialLoadTask;
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        MessageBox.Show(this, ex.Message);
    }
}
```

初期フォルダーの読み込み失敗はInitialLoadTaskで受け取ります。DLL側で追加のエラーダイアログを自動表示しません。FolderPathの不存在や言語指定の不正などは呼び出し時に例外になります。

## ダイアログ・呼出元による管理

```csharp
CropLauncher.ShowDialog(this, new CropOptions
{
    Language = CropLanguage.English
});

// Application.Runを管理する単体アプリなどで使用する。
using (var form = CropLauncher.CreateWindow(new CropOptions()))
    Application.Run(form);

// 作成した画面と同じUIスレッドから呼ぶ。
await cropWindow.OpenDataFolderAsync(@"C:\AnotherData");
string currentFolder = cropWindow.CurrentFolder;
string status = cropWindow.StatusMessage;
cropWindow.CloseFromHost();
```

AllowUserClose=falseでは終了ボタンを非表示にし、ユーザーの閉じる操作を抑止します。呼出元はCloseFromHostで終了できます。通常のダイアログ利用ではtrueにしてください。DLLはApplication.Run、Application.Exit、呼出元のCurrentCultureの変更を行いません。言語は画面作成時に指定し、日本語・英語の画面を同時に開けます。

## 配置とビルド

OpenCVを使用するため、呼出元もx64でビルドしてください。ReleaseフォルダーのControls.dll、OpenCvSharp.dll、OpenCvSharpExtern.dll、System.*.dllを呼出元EXEと同じフォルダーへ配置します。呼出元のAutoGenerateBindingRedirectsを有効にし、同梱App.configのSystem.MemoryとSystem.Runtime.CompilerServices.UnsafeのbindingRedirectを呼出元のApp.configへ統合してください。Examples/CropHostではこの構成を設定済みです。翻訳表とアプリアイコンはDLLに埋め込まれているため、実行時の外部ファイル配置は不要です。

- MatrixBitmapCrop.sln：.NET Framework 4.8.1。
- MatrixBitmapCrop.VS2017.sln：VS2017用 .NET Framework 4.7.2。
- VS2017で開く場合はVS2017用ソリューションを使用してください。

```bat
msbuild MatrixBitmapCrop.sln /m:1 /p:Configuration=Release
msbuild MatrixBitmapCrop.VS2017.sln /m:1 /p:Configuration=Release
msbuild Examples\CropHost\CropHost.csproj /m:1 /p:Configuration=Release
```

翻訳は各タブ、設定説明、状態表示、進捗、結果・エラーダイアログに適用します。ファイル名・ヘッダー・マスク座標・保存データの計算は言語で変更しません。診断ログは従来の原文形式を保持します。画像端の平坦化率50％など直前までの処理設定も保持しています。
