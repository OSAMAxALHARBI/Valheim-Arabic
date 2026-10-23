@echo off
rem Builds ValheimArabic.exe from the C# source in this folder with the C# compiler of the .NET Framework 4 that
rem ships with Windows (no SDK, no NuGet). Source of the engine: Taareeb Installer by Osama (iOsamai).
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /out:ValheimArabic.exe /win32icon:icon.ico /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:System.Xaml.dll /r:System.Web.Extensions.dll /resource:game.json,game.json /resource:Theme.xaml,Theme.xaml /resource:cover.jpg,cover.jpg /resource:logo.png,logo.png /resource:icon.ico,icon.ico /resource:ReadexPro-Regular.ttf,ReadexPro-Regular.ttf /resource:ReadexPro-SemiBold.ttf,ReadexPro-SemiBold.ttf FolderPicker.cs Kit.cs MainWindow.cs Program.cs Ps4Pkg.cs Spec.cs Ui.cs Updates.cs BuildInfo.cs
