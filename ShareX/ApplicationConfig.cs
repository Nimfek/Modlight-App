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
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;

namespace ShareX
{
    public class ApplicationConfig : SettingsBase<ApplicationConfig>
    {
        public TaskSettings DefaultTaskSettings = new TaskSettings();

        public DateTime FirstTimeRunDate = DateTime.Now;
        public string FileUploadDefaultDirectory = "";
        public int NameParserAutoIncrementNumber = 0;
        public List<QuickTaskInfo> QuickTaskPresets = QuickTaskInfo.DefaultPresets;

        /// <summary>
        /// How far this config has been carried forward for the notification
        /// action buttons. 0 is a config written before the fork changed them.
        /// </summary>
        /// <remarks>
        /// A changed default only reaches an installation that has never saved
        /// the setting, and DefaultTaskSettings.GeneralSettings.ToastWindowButtons
        /// is saved on every run - so the new set needs a marker to be applied
        /// once and then left alone. Same reason as ApplicationThemeOptions'
        /// BrandVersion; kept here rather than there because this is a task
        /// setting, not a theme one. See SettingManager.MigrateToastButtons.
        /// </remarks>
        public int ToastButtonsVersion = 0;

        /// <summary>
        /// The external components the user said no to. Everything else is wanted.
        /// </summary>
        /// <remarks>
        /// The declined list rather than the accepted one, so that "all ticked by default"
        /// keeps working: a component added in a later release is in nobody's list, so it
        /// starts out wanted for everybody, exactly as a new checkbox on the installer page
        /// would. An accepted list would have made every existing installation silently opt
        /// out of everything added after it.
        /// </remarks>
        public List<string> ExternalComponentsDeclined = new List<string>();

        /// <summary>
        /// How far this config has been carried forward for external components.
        /// See ModlightComponents.MigrateSettings.
        /// </summary>
        public int ExternalComponentsVersion = 0;

        /// <summary>
        /// Whether a newer published build of a component may be installed unasked.
        /// </summary>
        /// <remarks>
        /// True by default, and only yt-dlp follows a publisher at all - see
        /// ModlightComponentUpdates for why an eighteen megabyte download with the
        /// project's own digest behind it is worth taking without asking, and why a stale
        /// copy of that particular tool is the more expensive of the two options.
        /// </remarks>
        public bool ExternalComponentsAutoUpdate = true;

        /// <summary>
        /// When a component update check last COMPLETED, in UTC. Never stamped by one that
        /// failed, so an afternoon offline does not spend the day's check.
        /// </summary>
        public DateTime ExternalComponentsLastUpdateCheck = DateTime.MinValue;

        // Main window
        public bool FirstTimeMinimizeToTray = true;

        public ApplicationConfig()
        {
            this.ApplyDefaultPropertyValues();
        }

        #region Settings Form

        #region General

        public SupportedLanguage Language = SupportedLanguage.Automatic;
        public bool ShowTray = true;
        public bool SilentRun = false;
        public bool TrayIconProgressEnabled = true;
        public bool TaskbarProgressEnabled = true;
        public bool UseWhiteShareXIcon = false;
        /// <summary>
        /// How far this config has been carried forward for the window remembering
        /// where it was. 0 is a config written before the fork turned that on.
        /// </summary>
        /// <remarks>
        /// ShareX shipped both switches off, so an installation that has run even once
        /// carries "off" in its file and would never see the new default. Same marker
        /// pattern, and the same reason, as ToastButtonsVersion above - see
        /// SettingManager.MigrateWindowMemory for the part that keeps a deliberate
        /// choice deliberate.
        /// </remarks>
        public int WindowMemoryVersion = 0;

        public bool RememberMainFormPosition = true;
        public Point MainFormPosition = Point.Empty;
        public bool RememberMainFormSize = true;
        public Size MainFormSize = Size.Empty;

        /// <summary>
        /// Whether the window was left maximised. Kept apart from the size on purpose:
        /// a maximised window recorded as a size comes back as a screen-sized ordinary
        /// window, and "restore down" then has nothing smaller to hand back.
        /// </summary>
        public bool MainFormMaximized = false;

        public HotkeyType TrayLeftClickAction = HotkeyType.RectangleRegion;
        public HotkeyType TrayLeftDoubleClickAction = HotkeyType.OpenMainWindow;
        public HotkeyType TrayMiddleClickAction = HotkeyType.ClipboardUploadWithContentViewer;

        public bool AutoCheckUpdate = true;

        #endregion General

        #region Theme

        public ApplicationThemeOptions ThemeOptions = new ApplicationThemeOptions();

        #endregion

        #region Cloud settings

        /// <summary>
        /// What the account's settings row on the server last confirmed about this
        /// installation. Written only by <see cref="ModlightSettingsSync"/>.
        /// </summary>
        /// <remarks>
        /// Not synced, and it must never be: this records THIS machine's conversation with
        /// the server, so a copy of it on another machine would claim a confirmation that
        /// machine never received. <see cref="ModlightSettingsDocument"/> lists what
        /// travels, and this is deliberately absent from it.
        /// </remarks>
        public ModlightCloudSettings CloudSettings = new ModlightCloudSettings();

        #endregion Cloud settings

        #region Paths

        public bool UseCustomScreenshotsPath = false;
        public string CustomScreenshotsPath = "";

        public string SaveImageSubFolderPattern = "%y-%mo";
        public string SaveImageSubFolderPatternWindow = "";

        #endregion Paths

        #region Settings

        public bool AutoCleanupBackupFiles = false;
        public bool AutoCleanupLogFiles = false;
        public int CleanupKeepFileCount = 10;

        #endregion

        #region Main window

        public bool ShowThumbnailTitle = true;
        public ThumbnailTitleLocation ThumbnailTitleLocation = ThumbnailTitleLocation.Top;
        public Size ThumbnailSize = new Size(200, 150);
        public ThumbnailViewClickAction ThumbnailClickAction = ThumbnailViewClickAction.Default;

        /// <summary>
        /// The folder My space was last looking at, or empty for the top of the library.
        /// </summary>
        /// <remarks>
        /// This window is open for two seconds at a time. Landing on the top of the
        /// library every launch costs a navigation on every one of them, so it remembers -
        /// like a file manager, and for the same reason.
        ///
        /// An id that no longer names a folder needs no cleaning up here: the shelf checks
        /// every id against the folder list the server has just sent, and falls back to
        /// the top of the library when it cannot find one.
        /// </remarks>
        public string ModlightLastFolderId = "";

        /// <summary>
        /// Whether the library panel down the left of My space is open.
        /// </summary>
        /// <remarks>
        /// Remembered because the owner asked for it to be. An earlier round deliberately
        /// did NOT remember it, on the grounds that the folder list was an extra rather
        /// than the layout - but he has since asked for the whole of the web panel's
        /// sidebar in this window, and a navigation surface that closes itself on every
        /// launch is one somebody has to reopen on every launch.
        /// </remarks>
        public bool ModlightLibraryPanel = false;

        /// <summary>
        /// How many files one page of My space holds.
        /// </summary>
        /// <remarks>
        /// One of the three sizes the web panel offers, and the same three: 30, 60 or 120.
        /// Whatever is in here is put through <c>LibraryPager.ClampPageSize</c> before it is
        /// asked for, so a settings file written by an older build - or edited by hand - can
        /// only ever produce a size this build has a button for.
        ///
        /// The default is 120 rather than the panel's 30, which is the number the owner named:
        /// this window IS the grid, where the panel's sits under a page header and a filter
        /// bar. There is no migration to write for it because there is no old default to
        /// mistake for a choice - the field is new, so a config that predates it has no value
        /// here and takes the initialiser.
        /// </remarks>
        public int ModlightPageSize = 120;

        /// <summary>
        /// Whether the Folders and Files sections of My space are expanded.
        /// </summary>
        /// <remarks>
        /// The owner asked for the two headers to fold and for the fold to survive a
        /// restart, in those words. Open is the default because a library whose two
        /// sections are both shut on a fresh install looks like a library that failed
        /// to load: the collapse has to be something the user did, and therefore
        /// something the user can undo by doing it again.
        ///
        /// Two flags rather than one enum, because they are independent - somebody who
        /// folds the folder shelf away to get at a long file list is asking for exactly
        /// that and nothing else.
        /// </remarks>
        public bool ModlightFoldersSection = true;

        /// <inheritdoc cref="ModlightFoldersSection"/>
        public bool ModlightFilesSection = true;

        #endregion Main window

        #region Clipboard formats

        public List<ClipboardFormat> ClipboardContentFormats = new List<ClipboardFormat>();

        #endregion

        #region Upload

        public int UploadLimit = 0;
        public int BufferSizePower = 5;
        public int MaxUploadFailRetry = 1;

        #endregion Upload

        #region History

        public bool HistorySaveTasks = true;
        public bool HistoryCheckURL = false;

        public RecentTask[] RecentTasks = null;
        public bool RecentTasksSave = false;
        public int RecentTasksMaxCount = 10;
        public bool RecentTasksShowInMainWindow = true;
        public bool RecentTasksShowInTrayMenu = true;
        public bool RecentTasksTrayMenuMostRecentFirst = false;

        public HistorySettings HistorySettings = new HistorySettings();
        public ImageHistorySettings ImageHistorySettings = new ImageHistorySettings();

        #endregion History

        #region Print

        public bool DontShowPrintSettingsDialog = false;
        public PrintSettings PrintSettings = new PrintSettings();

        #endregion Print

        #region Proxy

        public ProxyInfo ProxySettings = new ProxyInfo();

        #endregion Proxy

        #region Advanced

        [Category("Application"), DefaultValue(false), Description("Calculate and show file sizes in binary units (KiB, MiB etc.)")]
        public bool BinaryUnits { get; set; }

        [Category("Application"), DefaultValue(false), Description("Show most recent task first in main window.")]
        public bool ShowMostRecentTaskFirst { get; set; }

        [Category("Application"), DefaultValue(false), Description("Show only customized tasks in main window workflows.")]
        public bool WorkflowsOnlyShowEdited { get; set; }

        [Category("Application"), DefaultValue(false), Description("Automatically expand capture menu when you open the tray menu.")]
        public bool TrayAutoExpandCaptureMenu { get; set; }

        [Category("Application"), DefaultValue(""), Description("URLs will open using this path instead of default browser. Example path: chrome.exe")]
        [Editor(typeof(ExeFileNameEditor), typeof(UITypeEditor))]
        public string BrowserPath { get; set; }

        [Category("Application"), DefaultValue(false), Description("Save settings after task completed but only if there are no other active tasks.")]
        public bool SaveSettingsAfterTaskCompleted { get; set; }

        [Category("Application"), DefaultValue(false), Description("")]
        public bool DevMode { get; set; }

        [Category("Hotkey"), DefaultValue(false), Description("Disables hotkeys.")]
        public bool DisableHotkeys { get; set; }

        [Category("Hotkey"), DefaultValue(false), Description("If active window is fullscreen then hotkeys won't be executed.")]
        public bool DisableHotkeysOnFullscreen { get; set; }

        private int hotkeyRepeatLimit;

        [Category("Hotkey"), DefaultValue(500), Description("If you hold hotkeys then it will only trigger every this milliseconds.")]
        public int HotkeyRepeatLimit
        {
            get
            {
                return hotkeyRepeatLimit;
            }
            set
            {
                hotkeyRepeatLimit = Math.Max(value, 200);
            }
        }

        [Category("Clipboard"), DefaultValue(true), Description("Show clipboard content viewer when using clipboard upload in main window.")]
        public bool ShowClipboardContentViewer { get; set; }

        [Category("Clipboard"), DefaultValue(true), Description("Because default .NET image copying not supports alpha channel, background of image will be black. This option will fill background white.")]
        public bool DefaultClipboardCopyImageFillBackground { get; set; }

        [Category("Clipboard"), DefaultValue(false), Description("Default .NET method can't copy image with alpha channel to clipboard. When this setting is true, ShareX copies \"PNG\" and 32 bit \"DIB\" to clipboard in order to retain image transparency.")]
        public bool UseAlternativeClipboardCopyImage { get; set; }

        [Category("Clipboard"), DefaultValue(false), Description("Default .NET method can't get image with alpha channel from clipboard. When this setting is true, ShareX checks if clipboard contains \"PNG\" or 32 bit \"DIB\" in order to retain image transparency.")]
        public bool UseAlternativeClipboardGetImage { get; set; }

        [Category("Image"), DefaultValue(true), Description("If JPEG exif contains orientation data then rotate image accordingly.")]
        public bool RotateImageByExifOrientationData { get; set; }

        [Category("Image"), DefaultValue(false), Description("Strip color space information chunks from PNG image.")]
        public bool PNGStripColorSpaceInformation { get; set; }

        [Category("Upload"), DefaultValue(false), Description("Can be used to disable uploading application wide.")]
        public bool DisableUpload { get; set; }

        [Category("Upload"), DefaultValue(true), Description("Ignore emojis while URL encoding upload results.")]
        public bool URLEncodeIgnoreEmoji { get; set; }

        [Category("Upload"), DefaultValue(true), Description("Show more than 10 files upload warning.")]
        public bool ShowMultiUploadWarning { get; set; }

        [Category("Upload"), DefaultValue(100), Description("Large file size defined in MB. ShareX will warn before uploading large files. 0 disables this feature.")]
        public int ShowLargeFileSizeWarning { get; set; }

        [Category("Paths"), DefaultValue(false), Description("When enabled ShareX stores Uploaders configuration files per machine, e.g. UploadersConfig-MYPC.json.")]
        public bool UseMachineSpecificUploadersConfig { get; set; }

        [Category("Paths"), Description("Custom uploaders configuration path. If you have already configured this setting in another device and you are attempting to use the same location, then backup the file before configuring this setting and restore after exiting ShareX.")]
        [Editor(typeof(DirectoryNameEditor), typeof(UITypeEditor))]
        public string CustomUploadersConfigPath { get; set; }

        [Category("Paths"), Description("Custom hotkeys configuration path. If you have already configured this setting in another device and you are attempting to use the same location, then backup the file before configuring this setting and restore after exiting ShareX.")]
        [Editor(typeof(DirectoryNameEditor), typeof(UITypeEditor))]
        public string CustomHotkeysConfigPath { get; set; }

        [Category("Paths"), Description("Custom screenshot path (secondary location). If custom screenshot path is temporarily unavailable (e.g. network share), ShareX will use this location (recommended to be a local path).")]
        [Editor(typeof(DirectoryNameEditor), typeof(UITypeEditor))]
        public string CustomScreenshotsPath2 { get; set; }

        [Category("Drag and drop window"), DefaultValue(150), Description("Size of drop window.")]
        public int DropSize { get; set; }

        [Category("Drag and drop window"), DefaultValue(5), Description("Position offset of drop window.")]
        public int DropOffset { get; set; }

        [Category("Drag and drop window"), DefaultValue(ContentAlignment.BottomRight), Description("Where drop window will open.")]
        public ContentAlignment DropAlignment { get; set; }

        [Category("Drag and drop window"), DefaultValue(100), Description("Opacity of drop window.")]
        public int DropOpacity { get; set; }

        [Category("Drag and drop window"), DefaultValue(255), Description("When you drag file to drop window then opacity will change to this.")]
        public int DropHoverOpacity { get; set; }

        #endregion Advanced

        #endregion Settings Form

        #region AutoCapture Form

        public Rectangle AutoCaptureRegion = Rectangle.Empty;
        public decimal AutoCaptureRepeatTime = 60;
        public bool AutoCaptureMinimizeToTray = true;
        public bool AutoCaptureWaitUpload = true;

        #endregion AutoCapture Form

        #region ScreenRecord Form

        public Rectangle ScreenRecordRegion = Rectangle.Empty;

        #endregion ScreenRecord Form

        #region Color Picker Form

        public List<Color> RecentColors = new List<Color>();

        #endregion Color Picker Form

        #region Download tab

        /// <summary>
        /// Where every finished download goes.
        /// </summary>
        /// <remarks>
        /// Global rather than per queued item, which is what the owner asked for: the
        /// answer is the same every time somebody gives it, so asking once is asking once.
        /// </remarks>
        public DownloadDestination DownloadDestination = DownloadDestination.Disk;

        /// <summary>
        /// The folder downloads land in, or empty for "ask each time".
        /// </summary>
        /// <remarks>
        /// Optional on purpose. Empty is the honest default: guessing at somebody's
        /// Downloads folder would put videos there without ever having said so.
        /// </remarks>
        public string DownloadFolder = "";

        /// <summary>
        /// Whether a download is fetched as video or as sound only.
        /// </summary>
        /// <remarks>
        /// Global for the same reason the destination is: somebody who rips audio rips audio
        /// from everything, and the answer does not change between one link and the next.
        /// </remarks>
        public DownloadMediaKind DownloadMediaKind = DownloadMediaKind.Video;

        /// <summary>
        /// The ceiling on a video download, or <see cref="DownloadQuality.Best"/> for none.
        /// </summary>
        /// <remarks>
        /// Best is the honest default. Every other value is somebody choosing to take less
        /// than the site has - a real thing to want, for disk space or for a slow line, but
        /// not a thing to assume on their behalf.
        /// </remarks>
        public DownloadQuality DownloadVideoQuality = DownloadQuality.Best;

        /// <summary>
        /// What an audio-only download is turned into.
        /// </summary>
        /// <remarks>
        /// M4A rather than MP3, because YouTube already sends AAC: the stream is copied
        /// through untouched, so the default costs neither quality nor re-encoding time. MP3
        /// is there for the players that still speak nothing else.
        /// </remarks>
        public DownloadAudioFormat DownloadAudioFormat = DownloadAudioFormat.M4a;

        #endregion Download tab
    }
}
