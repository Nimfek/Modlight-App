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

using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.HistoryLib;
using ShareX.Localization;
using ShareX.Properties;
using ShareX.ScreenCaptureLib;
using ShareX.UploadersLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;

namespace ShareX
{
    internal static class SettingManager
    {
        private const string ApplicationConfigFileName = "ApplicationConfig.json";

        /// <summary>Bump when the default notification buttons change again.</summary>
        private const int CurrentToastButtonsVersion = 1;

        /// <summary>Bump when the window-memory defaults change again.</summary>
        private const int CurrentWindowMemoryVersion = 1;

        private static string ApplicationConfigFilePath
        {
            get
            {
                if (Program.Sandbox) return null;

                return Path.Combine(Program.PersonalFolder, ApplicationConfigFileName);
            }
        }

        private const string UploadersConfigFileNamePrefix = "UploadersConfig";
        private const string UploadersConfigFileNameExtension = "json";
        private const string UploadersConfigFileName = UploadersConfigFileNamePrefix + "." + UploadersConfigFileNameExtension;

        private static string UploadersConfigFilePath
        {
            get
            {
                if (Program.Sandbox) return null;

                string uploadersConfigFolder;

                if (Settings != null && !string.IsNullOrEmpty(Settings.CustomUploadersConfigPath))
                {
                    uploadersConfigFolder = FileHelpers.ExpandFolderVariables(Settings.CustomUploadersConfigPath);
                }
                else
                {
                    uploadersConfigFolder = Program.PersonalFolder;
                }

                string uploadersConfigFileName = GetUploadersConfigFileName(uploadersConfigFolder);

                return Path.Combine(uploadersConfigFolder, uploadersConfigFileName);
            }
        }

        private const string HotkeysConfigFileName = "HotkeysConfig.json";

        private static string HotkeysConfigFilePath
        {
            get
            {
                if (Program.Sandbox) return null;

                string hotkeysConfigFolder;

                if (Settings != null && !string.IsNullOrEmpty(Settings.CustomHotkeysConfigPath))
                {
                    hotkeysConfigFolder = FileHelpers.ExpandFolderVariables(Settings.CustomHotkeysConfigPath);
                }
                else
                {
                    hotkeysConfigFolder = Program.PersonalFolder;
                }

                return Path.Combine(hotkeysConfigFolder, HotkeysConfigFileName);
            }
        }

        public static string BackupFolder => Path.Combine(Program.PersonalFolder, "Backup");

        private static ApplicationConfig Settings { get => Program.Settings; set => Program.Settings = value; }
        private static TaskSettings DefaultTaskSettings { get => Program.DefaultTaskSettings; set => Program.DefaultTaskSettings = value; }
        private static UploadersConfig UploadersConfig { get => Program.UploadersConfig; set => Program.UploadersConfig = value; }
        private static HotkeysConfig HotkeysConfig { get => Program.HotkeysConfig; set => Program.HotkeysConfig = value; }

        private static ManualResetEvent uploadersConfigResetEvent = new ManualResetEvent(false);
        private static ManualResetEvent hotkeysConfigResetEvent = new ManualResetEvent(false);

        public static void LoadInitialSettings()
        {
            LoadApplicationConfig();

            Task.Run(() =>
            {
                LoadUploadersConfig();
                uploadersConfigResetEvent.Set();

                LoadHotkeysConfig();
                hotkeysConfigResetEvent.Set();

                // Started here rather than from Main because the first thing the sync does
                // is read BOTH documents, and reading a half-loaded one is how an
                // installation uploads an empty hotkey list over a full one. This is the
                // moment both are on the table.
                ModlightSettingsSync.Start();
            });
        }

        public static void WaitUploadersConfig()
        {
            if (UploadersConfig == null)
            {
                uploadersConfigResetEvent.WaitOne();
            }
        }

        public static void WaitHotkeysConfig()
        {
            if (HotkeysConfig == null)
            {
                hotkeysConfigResetEvent.WaitOne();
            }
        }

        public static void LoadApplicationConfig(bool fallbackSupport = true)
        {
            Settings = ApplicationConfig.Load(ApplicationConfigFilePath, BackupFolder, fallbackSupport);
            Settings.CreateBackup = true;
            Settings.CreateWeeklyBackup = true;
            Settings.SettingsSaveFailed += Settings_SettingsSaveFailed;
            DefaultTaskSettings = Settings.DefaultTaskSettings;
            ApplicationConfigBackwardCompatibilityTasks();
            Settings.ThemeOptions ??= new ApplicationThemeOptions();

            // Before the theme is applied, not after: a config carried over from
            // ShareX (the installer offers exactly that) arrives wearing ShareX's
            // accent, and applying it first would paint the window blue and then
            // repaint it. Runs once per installation - see BrandVersion.
            bool migrated = Settings.ThemeOptions.MigrateBrand();

            // Non-short-circuiting on purpose: both migrations have to run, and
            // they share the one save.
            migrated |= MigrateToastButtons();
            migrated |= MigrateWindowMemory();

            if (migrated)
            {
                Settings.SaveAsync(ApplicationConfigFilePath);
            }

            ThemeManager.Configure(Settings.ThemeOptions);
            MigrateHistoryFile();
            HistoryConnect();
        }

        /// <summary>
        /// Carries an existing config over to the notification action buttons
        /// this fork ships. Runs once, and only over a set nobody chose.
        /// </summary>
        /// <remarks>
        /// ToastWindowButtons is written on every save, so an install that has
        /// run even once would never see a new default - the same trap that has
        /// already cost this project two bugs, where a form handed back an
        /// effective value and quietly froze a setting.
        ///
        /// The equality check is the part that matters: the version marker alone
        /// would also throw away a set the user assembled by hand. So only the
        /// four ShareX shipped are replaced, and anything else is left exactly
        /// as it is - the marker still moves, so a user who later rebuilds the
        /// old four keeps them.
        /// </remarks>
        private static bool MigrateToastButtons()
        {
            if (Settings.ToastButtonsVersion >= CurrentToastButtonsVersion)
            {
                return false;
            }

            Settings.ToastButtonsVersion = CurrentToastButtonsVersion;

            if (!NotificationActionButton.IsInheritedShareXSet(DefaultTaskSettings.GeneralSettings.ToastWindowButtons))
            {
                return true;
            }

            DefaultTaskSettings.GeneralSettings.ToastWindowButtons = NotificationActionButton.CreateDefaultButtons();

            return true;
        }

        /// <summary>
        /// Turns on remembering the main window's size and position for a config that
        /// never chose either way. Runs once.
        /// </summary>
        /// <remarks>
        /// ShareX shipped both switches off and writes them on every save, so a changed
        /// default reaches nobody who has ever run the program - the trap that has cost
        /// this project three bugs now.
        ///
        /// The pair is checked together, and only the pair ShareX shipped is replaced.
        /// Either switch already on means somebody has been in that dialog and set them
        /// deliberately, and a migration that overrules a deliberate choice is worse than
        /// the stale default it is fixing. The marker moves either way, so this never
        /// runs a second time.
        /// </remarks>
        private static bool MigrateWindowMemory()
        {
            if (Settings.WindowMemoryVersion >= CurrentWindowMemoryVersion)
            {
                return false;
            }

            Settings.WindowMemoryVersion = CurrentWindowMemoryVersion;

            if (Settings.RememberMainFormPosition || Settings.RememberMainFormSize)
            {
                return true;
            }

            Settings.RememberMainFormPosition = true;
            Settings.RememberMainFormSize = true;

            return true;
        }

        private static void Settings_SettingsSaveFailed(Exception e)
        {
            string message;

            if (e is UnauthorizedAccessException || e is FileNotFoundException)
            {
                message = Strings.YourAntiVirusSoftwareOrTheControlledFolderAccessFeatureInWindowsCouldBeBlockingShareX;
            }
            else
            {
                message = e.Message;
            }

            TaskHelpers.ShowNotificationTip(message, "Modlight - " + Strings.FailedToSaveSettings, 5000);
        }

        public static void LoadUploadersConfig(bool fallbackSupport = true)
        {
            UploadersConfig = UploadersConfig.Load(UploadersConfigFilePath, BackupFolder, fallbackSupport);
            UploadersConfig.CreateBackup = true;
            UploadersConfig.CreateWeeklyBackup = true;
            UploadersConfig.SupportDPAPIEncryption = true;
        }

        public static void LoadHotkeysConfig(bool fallbackSupport = true)
        {
            HotkeysConfig = HotkeysConfig.Load(HotkeysConfigFilePath, BackupFolder, fallbackSupport);
            HotkeysConfig.CreateBackup = true;
            HotkeysConfig.CreateWeeklyBackup = true;
            HotkeysConfigBackwardCompatibilityTasks();
        }

        public static void LoadAllSettings()
        {
            LoadApplicationConfig();
            LoadUploadersConfig();
            LoadHotkeysConfig();
        }

        private static string GetUploadersConfigFileName(string destinationFolder)
        {
            if (string.IsNullOrEmpty(destinationFolder))
            {
                return UploadersConfigFileName;
            }

            if (Settings != null && Settings.UseMachineSpecificUploadersConfig)
            {
                string sanitizedMachineName = FileHelpers.SanitizeFileName(Environment.MachineName.ToLowerInvariant());

                if (!string.IsNullOrEmpty(sanitizedMachineName))
                {
                    string machineSpecificFileName = $"{UploadersConfigFileNamePrefix}-{sanitizedMachineName}.{UploadersConfigFileNameExtension}";
                    string machineSpecificPath = Path.Combine(destinationFolder, machineSpecificFileName);

                    if (!File.Exists(machineSpecificPath))
                    {
                        string defaultFilePath = Path.Combine(destinationFolder, UploadersConfigFileName);

                        if (File.Exists(defaultFilePath))
                        {
                            try
                            {
                                File.Copy(defaultFilePath, machineSpecificPath, false);
                            }
                            catch (IOException)
                            {
                                // Ignore copy issues; file may have been created in the meantime.
                            }
                        }
                    }

                    return machineSpecificFileName;
                }
            }

            return UploadersConfigFileName;
        }

        private static void ApplicationConfigBackwardCompatibilityTasks()
        {
            if (SystemOptions.DisableUpload)
            {
                DefaultTaskSettings.AfterCaptureJob = DefaultTaskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.UploadImageToHost);
            }

            if (Settings.IsUpgradeFrom("14.1.2"))
            {
                if (!Environment.Is64BitOperatingSystem && !string.IsNullOrEmpty(DefaultTaskSettings.CaptureSettings.FFmpegOptions.CLIPath))
                {
                    DefaultTaskSettings.CaptureSettings.FFmpegOptions.OverrideCLIPath = true;
                }
            }

            if (Settings.IsUpgradeFrom("15.0.1"))
            {
                DefaultTaskSettings.CaptureSettings.ScrollingCaptureOptions = new ScrollingCaptureOptions();
                DefaultTaskSettings.CaptureSettings.FFmpegOptions.FixSources();
            }

            if (Settings.IsUpgradeFrom("16.0.2"))
            {
                // The CheckPreReleaseUpdates -> UpdateChannel migration that used to be here
                // went with the setting it migrated: Modlight publishes one release channel,
                // so there is nothing to choose and nothing to carry forward.
                if (!DefaultTaskSettings.CaptureSettings.SurfaceOptions.UseDimming)
                {
                    DefaultTaskSettings.CaptureSettings.SurfaceOptions.BackgroundDimStrength = 0;
                }
            }
        }

        public static void HistoryConnect()
        {
            HistoryClose();
            Program.HistoryManager = new HistoryManagerSQLite(Program.HistoryFilePath);
        }

        public static void HistoryClose()
        {
            if (Program.HistoryManager != null)
            {
                Program.HistoryManager.Dispose();
                Program.HistoryManager = null;
            }
        }

        private static void MigrateHistoryFile()
        {
            if (File.Exists(Program.HistoryFilePathOld))
            {
                try
                {
                    if (!File.Exists(Program.HistoryFilePath))
                    {
                        DebugHelper.WriteLine($"Migrating JSON history file \"{Program.HistoryFilePathOld}\" to SQLite history file \"{Program.HistoryFilePath}\"");

                        using (HistoryManagerSQLite historyManager = new HistoryManagerSQLite(Program.HistoryFilePath))
                        {
                            historyManager.MigrateFromJSON(Program.HistoryFilePathOld);
                        }
                    }

                    FileHelpers.MoveFile(Program.HistoryFilePathOld, BackupFolder);
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                    e.ShowError();
                }
            }
        }

        private static void HotkeysConfigBackwardCompatibilityTasks()
        {
            if (SystemOptions.DisableUpload)
            {
                foreach (TaskSettings taskSettings in HotkeysConfig.Hotkeys.Select(x => x.TaskSettings))
                {
                    if (taskSettings != null)
                    {
                        taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.UploadImageToHost);
                    }
                }
            }

            if (Settings.IsUpgradeFrom("15.0.1"))
            {
                foreach (TaskSettings taskSettings in HotkeysConfig.Hotkeys.Select(x => x.TaskSettings))
                {
                    if (taskSettings != null && taskSettings.CaptureSettings != null)
                    {
                        taskSettings.CaptureSettings.ScrollingCaptureOptions = new ScrollingCaptureOptions();
                        taskSettings.CaptureSettings.FFmpegOptions.FixSources();
                    }
                }
            }
        }

        public static void CleanupHotkeysConfig()
        {
            foreach (TaskSettings taskSettings in HotkeysConfig.Hotkeys.Select(x => x.TaskSettings))
            {
                taskSettings.Cleanup();
            }
        }

        public static void SaveAllSettings()
        {
            if (Settings != null)
            {
                Settings.Save(ApplicationConfigFilePath);
            }

            if (UploadersConfig != null)
            {
                UploadersConfig.Save(UploadersConfigFilePath);
            }

            if (HotkeysConfig != null)
            {
                CleanupHotkeysConfig();
                HotkeysConfig.Save(HotkeysConfigFilePath);
            }
        }

        public static void SaveApplicationConfigAsync()
        {
            if (Settings != null)
            {
                Settings.SaveAsync(ApplicationConfigFilePath);

                // Where "the configuration actually changed" is observed. There is no
                // change event to hang this off - ApplicationConfig is a bag of public
                // fields - and a save is the closest thing this codebase has, which is
                // not a poor substitute: every path that changes a setting ends in one.
                // The nudge is debounced, so a settings window saving on every keystroke
                // still produces one upload.
                ModlightSettingsSync.Nudge();
            }
        }

        public static void SaveUploadersConfigAsync()
        {
            if (UploadersConfig != null)
            {
                UploadersConfig.SaveAsync(UploadersConfigFilePath);
            }
        }

        public static void SaveHotkeysConfigAsync()
        {
            if (HotkeysConfig != null)
            {
                CleanupHotkeysConfig();
                HotkeysConfig.SaveAsync(HotkeysConfigFilePath);
                ModlightSettingsSync.Nudge();
            }
        }

        public static void SaveAllSettingsAsync()
        {
            SaveApplicationConfigAsync();
            SaveUploadersConfigAsync();
            SaveHotkeysConfigAsync();
        }

        public static void ResetSettings()
        {
            if (File.Exists(ApplicationConfigFilePath)) File.Delete(ApplicationConfigFilePath);
            LoadApplicationConfig(false);

            if (File.Exists(UploadersConfigFilePath)) File.Delete(UploadersConfigFilePath);
            LoadUploadersConfig(false);

            if (File.Exists(HotkeysConfigFilePath)) File.Delete(HotkeysConfigFilePath);
            LoadHotkeysConfig(false);
        }

        public static bool Export(string archivePath, bool settings, bool history)
        {
            MemoryStream msApplicationConfig = null, msUploadersConfig = null, msHotkeysConfig = null;

            try
            {
                List<ZipEntryInfo> entries = new List<ZipEntryInfo>();

                if (settings)
                {
                    msApplicationConfig = Settings.SaveToMemoryStream(false);
                    entries.Add(new ZipEntryInfo(msApplicationConfig, ApplicationConfigFileName));

                    msUploadersConfig = UploadersConfig.SaveToMemoryStream(false);
                    entries.Add(new ZipEntryInfo(msUploadersConfig, UploadersConfigFileName));

                    msHotkeysConfig = HotkeysConfig.SaveToMemoryStream(false);
                    entries.Add(new ZipEntryInfo(msHotkeysConfig, HotkeysConfigFileName));
                }

                if (history)
                {
                    entries.Add(new ZipEntryInfo(Program.HistoryFilePath));
                    HistoryClose();
                }

                ZipManager.Compress(archivePath, entries);
                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                MessageBox.Show(string.Format(Strings.SettingManager_ExportBackupError, e), Strings.SettingManager_ErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                msApplicationConfig?.Dispose();
                msUploadersConfig?.Dispose();
                msHotkeysConfig?.Dispose();

                if (history)
                {
                    HistoryConnect();
                }
            }

            return false;
        }

        public static bool Import(string archivePath)
        {
            try
            {
                HistoryClose();

                ZipManager.Extract(archivePath, Program.PersonalFolder, true, entry =>
                {
                    return FileHelpers.CheckExtension(entry.Name, new string[] { "json", "xml" });
                }, 1_000_000_000);

                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                MessageBox.Show(string.Format(Strings.SettingManager_ImportBackupError, e), Strings.SettingManager_ErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                HistoryConnect();
            }

            return false;
        }
    }
}
