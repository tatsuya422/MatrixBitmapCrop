MatrixBitmapCrop - source distribution

Repository: https://github.com/tatsuya422/MatrixBitmapCrop

Current project layout
  MatrixBitmapCrop.sln             EXE and Controls DLL
  MatrixBitmapCrop.csproj          EXE (.NET Framework 4.7.2, x64)
  MatrixBitmapCrop.Controls.csproj Shared UI and processing DLL (4.7.2, x64)
  Examples/CropHost/               External DLL host example
  Localization.tsv                Embedded Japanese/English translation table

Build requirements
  Windows, Visual Studio 2017 15.9 or later with .NET desktop development,
  .NET Framework 4.7.2 Developer Pack, C# 7.3, and x64 runtime dependencies.
  See VS2017で開く.txt for the current project arrangement. Older sections
  of README.md and DLLと多言語化.md refer to previous 4.8.1 / VS2017 names;
  those alternate project files are not part of the current source.

Required external DLLs (not uploaded)
  Create lib/ beside MatrixBitmapCrop.sln and supply these dependencies
  before building. The current local lib/ is unchanged by this upload.

  OpenCvSharp.dll                  4.13.0.20260602-beta
  OpenCvSharpExtern.dll            Matching x64 native OpenCV runtime
  System.Buffers.dll               4.6.1
  System.Memory.dll                4.6.3
  System.Numerics.Vectors.dll      4.6.1
  System.Runtime.CompilerServices.Unsafe.dll  6.1.2

  Versions above are the product versions of the existing managed DLLs.
  Use a matching managed/native OpenCvSharp pair and retain dependency
  licenses. These projects reference lib/ directly; there is no automatic
  NuGet restore configuration in the current source distribution.

Build in a Visual Studio Developer Command Prompt:
  msbuild MatrixBitmapCrop.sln /m:1 /p:Configuration=Release
  msbuild Examples\CropHost\CropHost.csproj /m:1 /p:Configuration=Release

Output: bin/Release/MatrixBitmapCrop.exe and MatrixBitmapCrop.Controls.dll.
Keep OpenCvSharpExtern.dll and the other dependencies beside the EXE.
The host must be x64 and use the supplied App.config binding redirects.
Use --language ja for Japanese or --language en for English (default).

Deployment to C:\MyProgram\SRS360\DllCtrl\DataAnalyze is opt-in only:
  /p:DeployToSrs360=true
Ordinary builds do not run that deployment event.

Excluded: third-party binaries, compiled application files, measurement
data, videos, generated verification results, and Visual Studio user state.
The repository does include the application icon and source documentation.
