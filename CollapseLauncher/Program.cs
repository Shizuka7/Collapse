﻿using CollapseLauncher.Helper.Update;
using Hi3Helper;
using Hi3Helper.Http;
using Hi3Helper.Shared.ClassStruct;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Squirrel;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using WinRT;
using static CollapseLauncher.ArgumentParser;
using static CollapseLauncher.InnerLauncherConfig;
using static Hi3Helper.Locale;
using static Hi3Helper.Logger;
using static Hi3Helper.Shared.Region.LauncherConfig;

namespace CollapseLauncher;

public static class MainEntryPoint
{
    public static int InstanceCount;

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    public static void Main(params string[] args)
    {
    #if PREVIEW
        IsPreview = true;
    #endif

        try
        {
            StartSquirrelHook();

            AppCurrentArgument = args;

            // Extract icons from the executable file
            var mainModulePath = Process.GetCurrentProcess().MainModule?.FileName;
            var iconCount      = InvokeProp.ExtractIconEx(mainModulePath, -1, null, null, 0);
            if (iconCount > 0)
            {
                var largeIcons = new IntPtr[1];
                var smallIcons = new IntPtr[1];
                InvokeProp.ExtractIconEx(mainModulePath, 0, largeIcons, smallIcons, 1);
                AppIconLarge = largeIcons[0];
                AppIconSmall = smallIcons[0];
            }

            InitAppPreset();
            var logPath = AppGameLogsFolder;
            _log = IsConsoleEnabled
                ? new LoggerConsole(logPath, Encoding.UTF8)
                : new LoggerNull(logPath, Encoding.UTF8);
            if (Directory.GetCurrentDirectory() != AppFolder)
            {
                LogWriteLine($"Force changing the working directory from {Directory.GetCurrentDirectory()} to {AppFolder}!",
                             LogType.Warning, true);
                Directory.SetCurrentDirectory(AppFolder);
            }

            LogWriteLine(string.Format("Running Collapse Launcher [{0}], [{3}], under {1}, as {2}",
                                       LauncherUpdateHelper.LauncherCurrentVersionString,
                                       GetVersionString(),
                                       Environment.UserName,
                                       IsPreview ? "Preview" : "Stable"), LogType.Scheme, true);

            var winAppSDKVer = FileVersionInfo.GetVersionInfo("Microsoft.ui.xaml.dll");

            LogWriteLine($"Runtime: {RuntimeInformation.FrameworkDescription} - WindowsAppSDK {winAppSDKVer.ProductVersion}",
                         LogType.Scheme, true);
            LogWriteLine($"Built from repo {ThisAssembly.Git.RepositoryUrl}\r\n\t" +
                         $"Branch {ThisAssembly.Git.Branch} - Commit {ThisAssembly.Git.Commit} at {ThisAssembly.Git.CommitDate}",
                         LogType.Scheme, true);

            Process.GetCurrentProcess().PriorityBoostEnabled = true;

            ParseArguments(args);
            InitializeAppSettings();

            HttpLogInvoker.DownloadLog += HttpClientLogWatcher;

            switch (m_appMode)
            {
                case AppMode.Launcher:
                    if (!IsConsoleEnabled)
                    {
                        LoggerConsole.DisposeConsole();
                        _log = new LoggerNull(logPath, Encoding.UTF8);
                    }

                    break;
                case AppMode.ElevateUpdater:
                    RunElevateUpdate();
                    return;
                case AppMode.InvokerTakeOwnership:
                    new TakeOwnership().StartTakingOwnership(m_arguments.TakeOwnership.AppPath);
                    return;
                case AppMode.InvokerMigrate:
                    if (m_arguments.Migrate.IsBHI3L)
                        new Migrate().DoMigrationBHI3L(
                                                       m_arguments.Migrate.GameVer,
                                                       m_arguments.Migrate.RegLoc,
                                                       m_arguments.Migrate.InputPath,
                                                       m_arguments.Migrate.OutputPath);
                    else
                        new Migrate().DoMigration(
                                                  m_arguments.Migrate.InputPath,
                                                  m_arguments.Migrate.OutputPath);
                    return;
                case AppMode.InvokerMoveSteam:
                    new Migrate().DoMoveSteam(
                                              m_arguments.Migrate.InputPath,
                                              m_arguments.Migrate.OutputPath,
                                              m_arguments.Migrate.GameVer,
                                              m_arguments.Migrate.KeyName);
                    return;
            }

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            InstanceCount = InvokeProp.EnumerateInstances();

            AppActivation.Enable();
            if (!AppActivation.DecideRedirection())
            {
                XamlCheckProcessRequirements();

                ComWrappersSupport.InitializeComWrappers();
                Application.Start(_ =>
                                  {
                                      var context =
                                          new DispatcherQueueSynchronizationContext(DispatcherQueue
                                             .GetForCurrentThread());
                                      SynchronizationContext.SetSynchronizationContext(context);

                                      // ReSharper disable once ObjectCreationAsStatement
                                      new App();
                                  });
            }
        }
        catch (Exception ex)
        {
            LoggerConsole.AllocateConsole();
            Console.WriteLine($"FATAL ERROR ON APP MAIN() LEVEL!!!\r\n{ex}");
            Console.WriteLine("\r\nIf you are sure that this is not intended, " +
                              "please report it to: https://github.com/CollapseLauncher/Collapse/issues\r\n" +
                              "Press any key to exit...");
            Console.ReadLine();
        }
        finally
        {
            HttpLogInvoker.DownloadLog -= HttpClientLogWatcher;
        }
    }

    private static void HttpClientLogWatcher(object sender, DownloadLogEvent e)
    {
        var severity = e.Severity switch
                       {
                           DownloadLogSeverity.Warning => LogType.Warning,
                           DownloadLogSeverity.Error => LogType.Error,
                           _ => LogType.Default
                       };

        LogWriteLine(e.Message, severity, true);
    }

    private static void OnProcessExit(object sender, EventArgs e)
    {
        App.IsAppKilled = true;
    }

    private static void StartSquirrelHook()
    {
        // Add Squirrel Hooks
        SquirrelAwareApp.HandleEvents(
                                      // Add shortcut and uninstaller entry on first start-up
                                      // ReSharper disable UnusedParameter.Local
                                      (_, sqr) =>
                                      {
                                          Console
                                             .WriteLine("Please do not close this console window while Collapse is preparing the installation via Squirrel...");
                                      },
                                      (_, sqr) =>
                                      {
                                          Console
                                             .WriteLine("Please do not close this console window while Collapse is updating via Squirrel...");
                                      },
                                      onAppUninstall: (_, sqr) =>
                                      {
                                          Console
                                             .WriteLine("Uninstalling Collapse via Squirrel...\r\n" +
                                                        "Please do not close this console window while action is being performed!");
                                      },
                                      // ReSharper restore UnusedParameter.Local
                                      onEveryRun: (_, _, _) => { }
                                     );
    }

    public static string FindCollapseStubPath()
    {
        var collapseExecName = "CollapseLauncher.exe";
        var collapseMainPath = Process.GetCurrentProcess().MainModule!.FileName;
        var collapseStubPath = Path.Combine(Directory.GetParent(Path.GetDirectoryName(collapseMainPath)!)!.FullName,
                                            collapseExecName);
        if (File.Exists(collapseStubPath))
        {
            LogWriteLine($"Found stub at {collapseStubPath}", LogType.Default, true);
            return collapseStubPath;
        }

        LogWriteLine($"Collapse stub does not exist, returning current executable path!\r\n\t{collapseStubPath}",
                     LogType.Default, true);
        return collapseMainPath;
    }

    private static void InitializeAppSettings()
    {
        InitializeLocale();
        if (IsFirstInstall)
        {
            LoadLocale(CultureInfo.CurrentUICulture.Name);
            SetAppConfigValue("AppLanguage", Lang.LanguageID);
        }
        else
        {
            LoadLocale(GetAppConfigValue("AppLanguage").ToString());
        }

        var themeValue = GetAppConfigValue("ThemeMode").ToString();
        if (!Enum.TryParse(themeValue, true, out CurrentAppTheme))
        {
            CurrentAppTheme = AppThemeMode.Dark;
            LogWriteLine($"ThemeMode: {themeValue} is invalid! Falling back to Dark-mode (Valid values are: {string.Join(',', Enum.GetNames(typeof(AppThemeMode)))})",
                         LogType.Warning, true);
        }
    }

    private static void RunElevateUpdate()
    {
        var elevatedProc = new Process()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName         = UpdaterWindow.sourcePath,
                WorkingDirectory = UpdaterWindow.workingDir,
                Arguments =
                    $"update --input \"{m_arguments.Updater.AppPath}\" --channel {m_arguments.Updater.UpdateChannel}",
                UseShellExecute = true,
                Verb            = "runas"
            }
        };
        elevatedProc.Start();
    }

    public static string GetVersionString()
    {
        var version = Environment.OSVersion.Version;
        m_isWindows11 = version.Build >= 22000;
        if (m_isWindows11)
            return $"Windows 11 (build: {version.Build}.{version.Revision})";
        return $"Windows {version.Major} (build: {version.Build}.{version.Revision})";
    }
}