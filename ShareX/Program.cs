#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.Localization;
using ShareX.AvaloniaUI.Integration;
using ShareX.HelpersLib;
using ShareX.HistoryLib;
using ShareX.ImageEditor.Integration;
using ShareX.Properties;
using ShareX.UploadersLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;

namespace ShareX
{
    internal static class Program
    {
        public const string AppName = "Modlight";

        // Deliberately NOT the upstream ShareX GUID (82E6AC09-0FEF-4390-AD9F-0DD3F5561EFC).
        // The mutex is what single-instance mode checks: sharing it with ShareX would make
        // Modlight refuse to start (or forward its command line into ShareX) whenever both
        // are installed, which is exactly the situation a migrating user is in.
        public const string MutexName = "3030612E-160A-4C79-8811-E6E709671DD8";
        public static readonly string PipeName = $"{Environment.MachineName}-{Environment.UserName}-{AppName}";

        public const ShareXBuild Build =
#if RELEASE
            ShareXBuild.Release;
#elif DEBUG
            ShareXBuild.Debug;
#else
            ShareXBuild.Unknown;
#endif

        public static string VersionText
        {
            get
            {
                StringBuilder sbVersionText = new StringBuilder();
                Version version = Version.Parse(Application.ProductVersion);
                sbVersionText.Append(version.Major + "." + version.Minor);
                if (version.Build > 0 || version.Revision > 0) sbVersionText.Append("." + version.Build);
                if (version.Revision > 0) sbVersionText.Append("." + version.Revision);
                if (Dev) sbVersionText.Append(" Dev");
                if (Portable) sbVersionText.Append(" Portable");
                return sbVersionText.ToString();
            }
        }

        public static string Title
        {
            get
            {
                string title = $"{AppName} {VersionText}";

                if (Settings != null && Settings.DevMode)
                {
                    string info = Build.ToString();

                    if (IsAdmin)
                    {
                        info += ", Admin";
                    }

                    title += $" ({info})";
                }

                return title;
            }
        }

        public static string TitleShort
        {
            get
            {
                if (Settings != null && Settings.DevMode)
                {
                    return Title;
                }

                return AppName;
            }
        }

        public static bool Dev { get; } = true;
        public static bool MultiInstance { get; private set; }
        public static bool Portable { get; private set; }
        public static bool SilentRun { get; private set; }
        public static bool Sandbox { get; private set; }
        public static bool IsAdmin { get; private set; }
        public static bool IgnoreHotkeyWarning { get; private set; }

        internal static ApplicationConfig Settings { get; set; }
        internal static TaskSettings DefaultTaskSettings { get; set; }
        internal static UploadersConfig UploadersConfig { get; set; }
        internal static HotkeysConfig HotkeysConfig { get; set; }
        internal static HistoryManagerSQLite HistoryManager { get; set; }

        internal static MainForm MainForm { get; private set; }
        internal static Stopwatch StartTimer { get; private set; }
        internal static HotkeyManager HotkeyManager { get; set; }
        internal static WatchFolderManager WatchFolderManager { get; set; }
        internal static ShareXUpdateManager UpdateManager { get; private set; }
        internal static ShareXCLIManager CLI { get; private set; }

        #region Paths

        private const string PersonalPathConfigFileName = "PersonalPath.cfg";

        // Where the user's settings, history and screenshots live. Kept as its own constant
        // rather than reusing AppName directly: renaming the product must never silently
        // relocate user data. Modlight starts from a clean "Documents\Modlight" - an existing
        // "Documents\ShareX" profile is NOT read. Importing one is an explicit, user-confirmed
        // step handled by the installer (see docs/decyzje-modlight-app.md, item 41).
        public const string PersonalFolderName = AppName;

        public static readonly string DefaultPersonalFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), PersonalFolderName);
        public static readonly string PortablePersonalFolder = FileHelpers.GetAbsolutePath(PersonalFolderName);

        private static string PersonalPathConfigFilePath
        {
            get
            {
                string relativePath = FileHelpers.GetAbsolutePath(PersonalPathConfigFileName);

                if (File.Exists(relativePath))
                {
                    return relativePath;
                }

                return CurrentPersonalPathConfigFilePath;
            }
        }

        private static readonly string CurrentPersonalPathConfigFilePath = Path.Combine(DefaultPersonalFolder, PersonalPathConfigFileName);

        private static readonly string PreviousPersonalPathConfigFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            PersonalFolderName, PersonalPathConfigFileName);

        private static readonly string PortableCheckFilePath = FileHelpers.GetAbsolutePath("Portable");

        private static string CustomPersonalPath { get; set; }

        public static string PersonalFolder
        {
            get
            {
                if (!string.IsNullOrEmpty(CustomPersonalPath))
                {
                    return FileHelpers.ExpandFolderVariables(CustomPersonalPath);
                }

                return DefaultPersonalFolder;
            }
        }

        public const string HistoryFileName = "History.db";

        public static string HistoryFilePath
        {
            get
            {
                if (Sandbox) return null;

                return Path.Combine(PersonalFolder, HistoryFileName);
            }
        }

        public const string HistoryFileNameOld = "History.json";

        public static string HistoryFilePathOld
        {
            get
            {
                if (Sandbox) return null;

                return Path.Combine(PersonalFolder, HistoryFileNameOld);
            }
        }

        public const string LogsFolderName = "Logs";

        public static string LogsFolder => Path.Combine(PersonalFolder, LogsFolderName);

        public static string LogsFilePath
        {
            get
            {
                if (SystemOptions.DisableLogging)
                {
                    return null;
                }

                string fileName = string.Format("Modlight-Log-{0:yyyy-MM}.txt", DateTime.Now);
                return Path.Combine(LogsFolder, fileName);
            }
        }

        public static string ScreenshotsParentFolder
        {
            get
            {
                if (Settings != null && Settings.UseCustomScreenshotsPath)
                {
                    string path = Settings.CustomScreenshotsPath;
                    string path2 = Settings.CustomScreenshotsPath2;

                    if (!string.IsNullOrEmpty(path))
                    {
                        path = FileHelpers.ExpandFolderVariables(path);

                        if (string.IsNullOrEmpty(path2) || Directory.Exists(path))
                        {
                            return path;
                        }
                    }

                    if (!string.IsNullOrEmpty(path2))
                    {
                        path2 = FileHelpers.ExpandFolderVariables(path2);

                        if (Directory.Exists(path2))
                        {
                            return path2;
                        }
                    }
                }

                return Path.Combine(PersonalFolder, "Screenshots");
            }
        }

        public static string ImageEffectsFolder => Path.Combine(PersonalFolder, "ImageEffects");
        public static string ModelsFolder => Path.Combine(PersonalFolder, "Models");

        /// <summary>
        /// Where the external components that are executables live.
        /// </summary>
        /// <remarks>
        /// Not the installation directory: on a default install that is under Program Files
        /// and a standard user cannot write to it, which would make "install this now" from
        /// inside the running application impossible. This folder is also already the second
        /// place YtDlpTool looks, so a component put here is found by the code that was
        /// written before this folder had a name.
        /// </remarks>
        public static string ToolsFolder => Path.Combine(PersonalFolder, "Tools");

        private static string PersonalPathDetectionMethod;

        #endregion Paths

        private static bool closeSequenceStarted, restartRequested, restartAsAdmin;

        [STAThread]
        private static void Main(string[] args)
        {
            StartupWork();

            StartTimer = Stopwatch.StartNew();

            CLI = new ShareXCLIManager(args);
            CLI.ParseCommands();

            SystemOptions.UpdateSystemOptions();
            UpdatePersonalPath();

            DebugHelper.Init(LogsFilePath);

            IsAdmin = Helpers.IsAdministrator();
            MultiInstance = CLI.IsCommandExist("multi", "m");

            using (SingleInstanceManager singleInstanceManager = new SingleInstanceManager(MutexName, PipeName, !MultiInstance, args))
            {
                if (!singleInstanceManager.IsSingleInstance || singleInstanceManager.IsFirstInstance)
                {
                    singleInstanceManager.ArgumentsReceived += SingleInstanceManager_ArgumentsReceived;

                    using (TimerResolutionManager timerResolutionManager = new TimerResolutionManager())
                    {
                        Run(args);
                    }

                    if (restartRequested)
                    {
                        DebugHelper.WriteLine("Modlight restarting.");

                        if (restartAsAdmin)
                        {
                            TaskHelpers.RunShareXAsAdmin("-silent");
                        }
                        else
                        {
                            Process.Start(Application.ExecutablePath);
                        }
                    }
                }
            }

            DebugHelper.Flush();
        }

        private static void Run(string[] args)
        {
            ApplicationConfiguration.Initialize();

            DebugHelper.WriteLine("Modlight starting.");
            DebugHelper.WriteLine("Version: " + VersionText);
            DebugHelper.WriteLine("Build: " + Build);
            DebugHelper.WriteLine("Command line: " + Environment.CommandLine);
            DebugHelper.WriteLine("Personal path: " + PersonalFolder);
            if (!string.IsNullOrEmpty(PersonalPathDetectionMethod))
            {
                DebugHelper.WriteLine("Personal path detection method: " + PersonalPathDetectionMethod);
            }
            DebugHelper.WriteLine("Operating system: " + Helpers.GetOperatingSystemProductName(true));
            DebugHelper.WriteLine(".NET version: " + Environment.Version);
            DebugHelper.WriteLine("Running as elevated process: " + IsAdmin);

            SilentRun = CLI.IsCommandExist("silent", "s");

            IgnoreHotkeyWarning = CLI.IsCommandExist("NoHotkeys");

            CreateParentFolders();
            RegisterExtensions();
            DebugWriteFlags();

            DebugHelper.WriteLine("Avalonia application initializing.");
            AvaloniaBootstrapper.Initialize(args, StartApplication, StopApplication);

            // How HelpersLib ends this application. It cannot see Program, and the one call it
            // used instead - System.Windows.Forms.Application.Exit() - does nothing here: this
            // build has no WinForms message loop and MainForm never enters
            // Application.OpenForms. Exit() below marshals onto the thread that owns MainForm
            // and runs the real close sequence, which is what releases the single-instance
            // mutex that the installer's AppMutex waits on.
            ApplicationExit.Handler = Exit;

            SettingManager.LoadInitialSettings();

            // IsPortable here rather than at the first check: the About window can create a
            // checker before MainForm has configured anything, and a portable copy asked to
            // update itself with a setup executable installs a second Modlight beside it.
            UpdateManager = new ShareXUpdateManager { IsPortable = Portable };
            LanguageHelper.ChangeLanguage(Settings.Language);
            CleanupManager.CleanupAsync();

            DebugHelper.WriteLine("Avalonia application starting.");
            AvaloniaBootstrapper.Run();

            CloseSequence();
        }

        private static async Task StartApplication()
        {
            ImageEditorIntegration.Initialize();

            DebugHelper.WriteLine("MainForm host init started.");
            MainForm = new MainForm();
            await MainForm.InitializeAsync();
            DebugHelper.WriteLine("MainForm host init finished.");
        }

        private static void StopApplication()
        {
            if (MainForm is { IsDisposed: false })
            {
                MainForm.ExitApplication();
            }
        }

        public static void CloseSequence()
        {
            if (!closeSequenceStarted)
            {
                closeSequenceStarted = true;

                DebugHelper.WriteLine("Modlight closing.");

                WatchFolderManager?.Dispose();
                SettingManager.HistoryClose();
                SettingManager.SaveAllSettings();

                DebugHelper.WriteLine("Modlight closed.");
            }
        }

        public static void Restart(bool asAdmin = false)
        {
            restartRequested = true;
            restartAsAdmin = asAdmin;
            Exit();
        }

        public static void Exit()
        {
            if (MainForm is { IsDisposed: false } mainForm)
            {
                mainForm.InvokeSafe(mainForm.ExitApplication);
            }
            else
            {
                AvaloniaBootstrapper.Shutdown();
            }
        }

        private static void SingleInstanceManager_ArgumentsReceived(string[] arguments)
        {
            string message = "Arguments received: ";

            if (arguments == null)
            {
                message += "null";
            }
            else
            {
                message += "\"" + string.Join(" ", arguments) + "\"";
            }

            DebugHelper.WriteLine(message);

            if (WaitFormLoad(5000))
            {
                MainForm.InvokeSafe(async () =>
                {
                    await UseCommandLineArgs(arguments);
                });
            }
        }

        private static bool WaitFormLoad(int wait)
        {
            Stopwatch timer = Stopwatch.StartNew();

            while (timer.ElapsedMilliseconds < wait)
            {
                if (MainForm != null && MainForm.IsReady) return true;

                Thread.Sleep(10);
            }

            return false;
        }

        /// <summary>
        /// Flags that say HOW to run, not WHAT to do.
        /// </summary>
        /// <remarks>
        /// These ride on every launch of a portable or startup copy, so treating
        /// them as a request is what made running the app a second time do
        /// nothing at all: the shortcut passes -portable, the check below saw a
        /// non-empty argument list, decided the user had asked for something
        /// specific, and never opened the window. Clicking the icon of an app
        /// that is already in the tray has to show the app.
        ///
        /// -silent is NOT one of them, and that is not an oversight. It is the
        /// only flag in the family that is itself an instruction about the
        /// window: it means "do not show me". Listing it here made
        /// <see cref="UseCommandLineArgs"/> read it as "no request was made" and
        /// force the window open - the precise opposite of what it asks for, on
        /// the two launches that actually carry it. The startup shortcut passes
        /// -silent (see StartupManager), and so does the elevated relaunch after
        /// a restart, so a startup that raced a running instance popped a window
        /// in the user's face at every login.
        /// </remarks>
        private static readonly string[] LaunchModeFlags =
        {
            "portable", "p", "multi", "m", "sandbox"
        };

        private static bool IsOnlyLaunchModeFlags(string[] args)
        {
            foreach (string arg in args)
            {
                string name = arg.TrimStart('-', '/');

                if (Array.FindIndex(LaunchModeFlags,
                        flag => string.Equals(flag, name, StringComparison.OrdinalIgnoreCase)) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static async Task UseCommandLineArgs(string[] args)
        {
            if (args == null || args.Length < 1 || IsOnlyLaunchModeFlags(args))
            {
                if (Program.Settings.ShowTray)
                {
                    // Workaround for Windows startup tray icon bug
                    MainWindowIntegration.SetTrayVisible(false);
                    MainWindowIntegration.SetTrayVisible(true);
                }

                MainForm.ForceActivate();
            }
            else if (MainForm.Visible)
            {
                MainForm.ForceActivate();
            }

            CLIManager cli = new CLIManager(args);
            cli.ParseCommands();

            await CLI.UseCommandLineArgs(cli.Commands);
        }

        private static void UpdatePersonalPath()
        {
            Sandbox = CLI.IsCommandExist("sandbox");

            if (!Sandbox)
            {
                if (CLI.IsCommandExist("portable", "p"))
                {
                    Portable = true;
                    CustomPersonalPath = PortablePersonalFolder;
                    PersonalPathDetectionMethod = "Portable CLI flag";
                }
                else if (File.Exists(PortableCheckFilePath))
                {
                    Portable = true;
                    CustomPersonalPath = PortablePersonalFolder;
                    PersonalPathDetectionMethod = $"Portable file ({PortableCheckFilePath})";
                }
                else if (!string.IsNullOrEmpty(SystemOptions.PersonalPath))
                {
                    CustomPersonalPath = SystemOptions.PersonalPath;
                    PersonalPathDetectionMethod = "Registry";
                }
                else
                {
                    MigratePersonalPathConfig();

                    string customPersonalPath = ReadPersonalPathConfig();

                    if (!string.IsNullOrEmpty(customPersonalPath))
                    {
                        CustomPersonalPath = FileHelpers.GetAbsolutePath(customPersonalPath);
                        PersonalPathDetectionMethod = $"PersonalPath.cfg file ({PersonalPathConfigFilePath})";
                    }
                }

                if (!Directory.Exists(PersonalFolder))
                {
                    try
                    {
                        Directory.CreateDirectory(PersonalFolder);
                    }
                    catch (Exception e)
                    {
                        StringBuilder sb = new StringBuilder();

                        sb.AppendFormat("{0} \"{1}\"", Strings.Program_Run_Unable_to_create_folder_, PersonalFolder);
                        sb.AppendLine();

                        if (!string.IsNullOrEmpty(PersonalPathDetectionMethod))
                        {
                            sb.AppendLine("Personal path detection method: " + PersonalPathDetectionMethod);
                        }

                        sb.AppendLine();
                        sb.Append(e);

                        MessageBox.Show(sb.ToString(), "Modlight - " + Strings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        CustomPersonalPath = "";
                    }
                }
            }
        }

        private static void CreateParentFolders()
        {
            if (!Sandbox && Directory.Exists(PersonalFolder))
            {
                FileHelpers.CreateDirectory(SettingManager.BackupFolder);
                FileHelpers.CreateDirectory(ImageEffectsFolder);
                FileHelpers.CreateDirectory(ScreenshotsParentFolder);
            }
        }

        private static void RegisterExtensions()
        {
            if (!Portable)
            {
                IntegrationHelpers.RemoveCustomUploaderExtension();

                if (!IntegrationHelpers.CheckImageEffectExtension())
                {
                    IntegrationHelpers.CreateImageEffectExtension(true);
                }
            }
        }

        public static void UpdateHelpersSpecialFolders()
        {
            Dictionary<string, string> specialFolders = new Dictionary<string, string>();
            specialFolders.Add("ShareXImageEffects", ImageEffectsFolder);
            HelpersOptions.ShareXSpecialFolders = specialFolders;

            // Where ScreenCaptureLib and Tools look for ffmpeg.exe and exiftool.exe when
            // there is no copy beside the executable. Neither of them can see this project,
            // so the path is pushed at them the same way the proxy and the special folders
            // above already are.
            ExternalTools.ManagedFolder = ToolsFolder;
        }

        private static void MigratePersonalPathConfig()
        {
            if (File.Exists(PreviousPersonalPathConfigFilePath))
            {
                try
                {
                    if (!File.Exists(CurrentPersonalPathConfigFilePath))
                    {
                        FileHelpers.CreateDirectoryFromFilePath(CurrentPersonalPathConfigFilePath);
                        File.Move(PreviousPersonalPathConfigFilePath, CurrentPersonalPathConfigFilePath);
                    }

                    File.Delete(PreviousPersonalPathConfigFilePath);
                    Directory.Delete(Path.GetDirectoryName(PreviousPersonalPathConfigFilePath));
                }
                catch (Exception e)
                {
                    e.ShowError();
                }
            }
        }

        public static string ReadPersonalPathConfig()
        {
            if (File.Exists(PersonalPathConfigFilePath))
            {
                return File.ReadAllText(PersonalPathConfigFilePath, Encoding.UTF8).Trim();
            }

            return "";
        }

        public static bool WritePersonalPathConfig(string path)
        {
            if (path == null)
            {
                path = "";
            }
            else
            {
                path = path.Trim();
            }

            bool isDefaultPath = string.IsNullOrEmpty(path) && !File.Exists(PersonalPathConfigFilePath);

            if (!isDefaultPath)
            {
                string currentPath = ReadPersonalPathConfig();

                if (!path.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        FileHelpers.CreateDirectoryFromFilePath(PersonalPathConfigFilePath);
                        File.WriteAllText(PersonalPathConfigFilePath, path, Encoding.UTF8);
                        return true;
                    }
                    catch (UnauthorizedAccessException e)
                    {
                        DebugHelper.WriteException(e);
                        MessageBox.Show(string.Format(Strings.Program_WritePersonalPathConfig_Cant_access_to_file, PersonalPathConfigFilePath),
                            "Modlight", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    catch (Exception e)
                    {
                        DebugHelper.WriteException(e);
                        e.ShowError();
                    }
                }
            }

            return false;
        }

        private static void StartupWork()
        {
            AssemblyLoadContext.Default.Resolving += (ctx, asmName) =>
            {
                if (string.IsNullOrEmpty(asmName.CultureName) || !asmName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string baseDir = AppContext.BaseDirectory;
                string path = Path.Combine(baseDir, "Languages", asmName.CultureName, asmName.Name + ".dll");
                return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
            };

#if DEBUG
            if (Debugger.IsAttached)
            {
                return;
            }
#endif

            // Add the event handler for handling UI thread exceptions to the event
            Application.ThreadException += Application_ThreadException;

            // Set the unhandled exception mode to force all Windows Forms errors to go through our handler
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // Add the event handler for handling non-UI thread exceptions to the event
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            OnError(e.Exception);
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            OnError((Exception)e.ExceptionObject);
        }

        private static void OnError(Exception e)
        {
            ErrorWindowIntegration.Show(e.Message, $"{e}\r\n\r\n{Title}", LogsFilePath, Links.GitHubIssues);
        }

        private static void DebugWriteFlags()
        {
            List<string> flags = new List<string>();

            if (Dev) flags.Add(nameof(Dev));
            if (MultiInstance) flags.Add(nameof(MultiInstance));
            if (Portable) flags.Add(nameof(Portable));
            if (SilentRun) flags.Add(nameof(SilentRun));
            if (Sandbox) flags.Add(nameof(Sandbox));
            if (IgnoreHotkeyWarning) flags.Add(nameof(IgnoreHotkeyWarning));
            if (SystemOptions.DisableUpdateCheck) flags.Add(nameof(SystemOptions.DisableUpdateCheck));
            if (SystemOptions.DisableUpload) flags.Add(nameof(SystemOptions.DisableUpload));
            if (SystemOptions.DisableLogging) flags.Add(nameof(SystemOptions.DisableLogging));

            string output = string.Join(", ", flags);

            if (!string.IsNullOrEmpty(output))
            {
                DebugHelper.WriteLine("Flags: " + output);
            }
        }
    }
}
