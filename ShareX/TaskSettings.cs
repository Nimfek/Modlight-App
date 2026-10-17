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

using Newtonsoft.Json;
using ShareX.AvaloniaUI.Windows;
using ShareX.HelpersLib;
using ShareX.ImageEditor.Integration;
using ShareX.ImageEffectsLib;
using ShareX.ScreenCaptureLib;
using ShareX.Tools;
using ShareX.UploadersLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Drawing.Design;
using System.Linq;

namespace ShareX
{
    public class TaskSettings
    {
        [JsonIgnore]
        public TaskSettings TaskSettingsReference { get; private set; }

        [JsonIgnore]
        public bool IsSafeTaskSettings => TaskSettingsReference != null;

        public string Description = "";

        public HotkeyType Job = HotkeyType.None;

        /// <summary>
        /// The Modlight folder this task's file should land in, or null for the default.
        /// </summary>
        /// <remarks>
        /// Not serialised, because it is not a setting: it is where THIS upload is going,
        /// set by the caller a moment before the task is started - a folder tile that was
        /// dropped on, or the folder My space is showing. Persisting it would mean a
        /// preset silently pinning every future capture into a folder somebody once
        /// dropped a file on.
        ///
        /// GetSafeTaskSettings has to carry it across explicitly. See the comment there:
        /// a task using the default settings gets a COPY of the defaults, and everything
        /// not named in that copy is lost.
        /// </remarks>
        [JsonIgnore]
        public string ModlightFolderId = null;

        /// <summary>
        /// What the Shorten URL dialog was asked for, or null when nobody asked for
        /// anything beyond the address itself.
        /// </summary>
        /// <remarks>
        /// Not serialised, for the same reason as the folder above and one more: it can
        /// carry a password, and a password belongs in memory for the length of one task
        /// and nowhere else.
        ///
        /// GetSafeTaskSettings has to carry it across explicitly, exactly like the folder.
        /// </remarks>
        [JsonIgnore]
        public ModlightShortLinkOptions ModlightShortLink = null;

        public bool UseDefaultAfterCaptureJob = true;

        // UploadImageToHost is on, unlike upstream, because upstream is a screenshot
        // tool that can upload and this is a client for a server that holds the
        // screenshots. Without it a fresh install has a destination it never sends
        // anything to, an after-upload task ("copy URL to clipboard") that can never
        // run, and a sign-in nothing ever asks for. SaveImageToFile stays alongside it:
        // the file on disk is what survives a failed or refused upload.
        public AfterCaptureTasks AfterCaptureJob = AfterCaptureTasks.CopyImageToClipboard | AfterCaptureTasks.SaveImageToFile |
            AfterCaptureTasks.UploadImageToHost;

        public bool UseDefaultAfterUploadJob = true;
        public AfterUploadTasks AfterUploadJob = AfterUploadTasks.CopyURLToClipboard;

        public bool UseDefaultDestinations = true;

        // Modlight everywhere, because this app is a Modlight client: the account it
        // signs in to is the destination, and a first run that pointed screenshots at
        // Imgur would upload somewhere the user never chose. These are field
        // initialisers, so only a config that has never been written picks them up -
        // anyone who already set a destination keeps it.
        public ImageDestination ImageDestination = ImageDestination.Modlight;
        public FileDestination ImageFileDestination = FileDestination.Modlight;
        public TextDestination TextDestination = TextDestination.Modlight;
        public FileDestination TextFileDestination = FileDestination.Modlight;
        public FileDestination FileDestination = FileDestination.Modlight;
        public UrlShortenerType URLShortenerDestination = UrlShortenerType.Modlight;
        public URLSharingServices URLSharingServiceDestination = URLSharingServices.GoogleImageSearch;

        public bool OverrideScreenshotsFolder = false;
        public string ScreenshotsFolder = "";

        public bool UseDefaultGeneralSettings = true;
        public TaskSettingsGeneral GeneralSettings = new TaskSettingsGeneral();

        public bool UseDefaultImageSettings = true;
        public TaskSettingsImage ImageSettings = new TaskSettingsImage();

        [JsonIgnore]
        public TaskSettingsImage ImageSettingsReference
        {
            get
            {
                if (UseDefaultImageSettings)
                {
                    return Program.DefaultTaskSettings.ImageSettings;
                }

                return TaskSettingsReference.ImageSettings;
            }
        }

        public bool UseDefaultCaptureSettings = true;
        public TaskSettingsCapture CaptureSettings = new TaskSettingsCapture();

        [JsonIgnore]
        public TaskSettingsCapture CaptureSettingsReference
        {
            get
            {
                if (UseDefaultCaptureSettings)
                {
                    return Program.DefaultTaskSettings.CaptureSettings;
                }

                return TaskSettingsReference.CaptureSettings;
            }
        }

        public bool UseDefaultUploadSettings = true;
        public TaskSettingsUpload UploadSettings = new TaskSettingsUpload();

        public bool UseDefaultActions = true;
        public List<ExternalProgram> ExternalPrograms = new List<ExternalProgram>();

        public bool UseDefaultToolsSettings = true;
        public TaskSettingsTools ToolsSettings = new TaskSettingsTools();

        [JsonIgnore]
        public TaskSettingsTools ToolsSettingsReference
        {
            get
            {
                if (UseDefaultToolsSettings)
                {
                    return Program.DefaultTaskSettings.ToolsSettings;
                }

                return TaskSettingsReference.ToolsSettings;
            }
        }

        public bool UseDefaultAdvancedSettings = true;
        public TaskSettingsAdvanced AdvancedSettings = new TaskSettingsAdvanced();

        public bool WatchFolderEnabled = false;
        public List<WatchFolderSettings> WatchFolderList = new List<WatchFolderSettings>();

        public override string ToString()
        {
            return !string.IsNullOrEmpty(Description) ? Description : Job.GetLocalizedDescription();
        }

        public bool IsUsingDefaultSettings
        {
            get
            {
                return UseDefaultAfterCaptureJob && UseDefaultAfterUploadJob && UseDefaultDestinations &&
                    !OverrideScreenshotsFolder && UseDefaultGeneralSettings && UseDefaultImageSettings && UseDefaultCaptureSettings && UseDefaultUploadSettings &&
                    UseDefaultActions && UseDefaultToolsSettings && UseDefaultAdvancedSettings && !WatchFolderEnabled;
            }
        }

        public static TaskSettings GetDefaultTaskSettings()
        {
            TaskSettings taskSettings = new TaskSettings();
            taskSettings.SetDefaultSettings();
            taskSettings.TaskSettingsReference = Program.DefaultTaskSettings;
            return taskSettings;
        }

        public static TaskSettings GetSafeTaskSettings(TaskSettings taskSettings)
        {
            TaskSettings safeTaskSettings;

            if (taskSettings.IsUsingDefaultSettings && Program.DefaultTaskSettings != null)
            {
                safeTaskSettings = Program.DefaultTaskSettings.Copy();
                safeTaskSettings.Description = taskSettings.Description;
                safeTaskSettings.Job = taskSettings.Job;

                // A caller who has not overridden a single preference still gets a COPY of
                // the defaults here, so anything it asked for that is not named on this
                // list is silently dropped. The folder target is exactly such a request:
                // without this line, dropping files on a folder tile would upload them to
                // the account default and look like the server ignoring the folder.
                safeTaskSettings.ModlightFolderId = taskSettings.ModlightFolderId;

                // Same story for what the Shorten URL dialog collected. It reaches here
                // by a different road (the dialog writes it onto settings that are
                // already safe), so today this line is insurance rather than load
                // bearing - which is exactly what the folder id was until a caller
                // changed road and nobody remembered this list existed.
                safeTaskSettings.ModlightShortLink = taskSettings.ModlightShortLink;
            }
            else
            {
                safeTaskSettings = taskSettings.Copy();
                safeTaskSettings.SetDefaultSettings();
            }

            safeTaskSettings.TaskSettingsReference = taskSettings;
            return safeTaskSettings;
        }

        public void SetDefaultSettings()
        {
            if (Program.DefaultTaskSettings != null)
            {
                TaskSettings defaultTaskSettings = Program.DefaultTaskSettings.Copy();

                if (UseDefaultAfterCaptureJob)
                {
                    AfterCaptureJob = defaultTaskSettings.AfterCaptureJob;
                }

                if (UseDefaultAfterUploadJob)
                {
                    AfterUploadJob = defaultTaskSettings.AfterUploadJob;
                }

                if (UseDefaultDestinations)
                {
                    ImageDestination = defaultTaskSettings.ImageDestination;
                    ImageFileDestination = defaultTaskSettings.ImageFileDestination;
                    TextDestination = defaultTaskSettings.TextDestination;
                    TextFileDestination = defaultTaskSettings.TextFileDestination;
                    FileDestination = defaultTaskSettings.FileDestination;
                    URLShortenerDestination = defaultTaskSettings.URLShortenerDestination;
                    URLSharingServiceDestination = defaultTaskSettings.URLSharingServiceDestination;
                }

                if (UseDefaultGeneralSettings)
                {
                    GeneralSettings = defaultTaskSettings.GeneralSettings;
                }

                if (UseDefaultImageSettings)
                {
                    ImageSettings = defaultTaskSettings.ImageSettings;
                }

                if (UseDefaultCaptureSettings)
                {
                    CaptureSettings = defaultTaskSettings.CaptureSettings;
                }

                if (UseDefaultUploadSettings)
                {
                    UploadSettings = defaultTaskSettings.UploadSettings;
                }

                if (UseDefaultActions)
                {
                    ExternalPrograms = defaultTaskSettings.ExternalPrograms;
                }

                if (UseDefaultToolsSettings)
                {
                    ToolsSettings = defaultTaskSettings.ToolsSettings;
                }

                if (UseDefaultAdvancedSettings)
                {
                    AdvancedSettings = defaultTaskSettings.AdvancedSettings;
                }
            }
        }

        public void Cleanup()
        {
            if (UseDefaultGeneralSettings)
            {
                GeneralSettings = null;
            }

            if (UseDefaultImageSettings)
            {
                ImageSettings = null;
            }

            if (UseDefaultCaptureSettings)
            {
                CaptureSettings = null;
            }

            if (UseDefaultUploadSettings)
            {
                UploadSettings = null;
            }

            if (UseDefaultActions)
            {
                ExternalPrograms = null;
            }

            if (UseDefaultToolsSettings)
            {
                ToolsSettings = null;
            }

            if (UseDefaultAdvancedSettings)
            {
                AdvancedSettings = null;
            }
        }

        public FileDestination GetFileDestinationByDataType(EDataType dataType)
        {
            switch (dataType)
            {
                case EDataType.Image:
                    return ImageFileDestination;
                case EDataType.Text:
                    return TextFileDestination;
                default:
                case EDataType.File:
                    return FileDestination;
            }
        }
    }

    public class TaskSettingsGeneral
    {
        #region General / Notifications

        public bool PlaySoundAfterCapture = true;
        public bool PlaySoundAfterUpload = true;
        public bool PlaySoundAfterAction = true;
        public bool ShowToastNotificationAfterTaskCompleted = true;
        public float ToastWindowDuration = 3f;
        public float ToastWindowFadeDuration = 1f;
        public ContentAlignment ToastWindowPlacement = ContentAlignment.BottomRight;
        public Size ToastWindowSize = new Size(400, 300);
        public ToastClickAction ToastWindowLeftClickAction = ToastClickAction.OpenUrl;
        public ToastClickAction ToastWindowRightClickAction = ToastClickAction.CloseNotification;
        public ToastClickAction ToastWindowMiddleClickAction = ToastClickAction.AnnotateImage;
        public int ToastWindowButtonSize = 40;
        public List<NotificationActionButton> ToastWindowButtons = NotificationActionButton.CreateDefaultButtons();
        public bool ToastWindowAutoHide = true;
        public bool DisableNotificationsOnFullscreen = false;
        public bool UseCustomCaptureSound = false;
        public string CustomCaptureSoundPath = "";
        public bool UseCustomTaskCompletedSound = false;
        public string CustomTaskCompletedSoundPath = "";
        public bool UseCustomActionCompletedSound = false;
        public string CustomActionCompletedSoundPath = "";
        public bool UseCustomErrorSound = false;
        public string CustomErrorSoundPath = "";

        #endregion
    }

    public class TaskSettingsImage
    {
        #region Image / General

        public EImageFormat ImageFormat = EImageFormat.PNG;
        public PNGBitDepth ImagePNGBitDepth = PNGBitDepth.Default;
        public int ImageJPEGQuality = 90;
        public GIFQuality ImageGIFQuality = GIFQuality.Default;
        public bool ImageAutoUseJPEG = true;
        public int ImageAutoUseJPEGSize = 2048;
        public bool ImageAutoJPEGQuality = false;
        public FileExistAction FileExistAction = FileExistAction.Ask;

        #endregion Image / General

        #region Image / Effects

        public List<ImageEffectPreset> ImageEffectPresets = new List<ImageEffectPreset>() { ImageEffectPreset.GetDefaultPreset() };
        public int SelectedImageEffectPreset = 0;

        public bool ShowImageEffectsWindowAfterCapture = false;
        public bool ImageEffectOnlyRegionCapture = false;
        public bool UseRandomImageEffect = false;

        #endregion Image / Effects

        #region Image / Thumbnail

        public int ThumbnailWidth = 200;
        public int ThumbnailHeight = 0;
        public string ThumbnailName = "-thumbnail";
        public bool ThumbnailCheckSize = false;

        #endregion Image / Thumbnail
    }

    public class TaskSettingsCapture
    {
        #region Capture / General

        public bool ShowCursor = true;
        public decimal ScreenshotDelay = 0;
        public bool CaptureTransparent = false;
        public bool CaptureShadow = true;
        public int CaptureShadowOffset = 100;
        public bool CaptureClientArea = false;
        public bool CaptureAutoHideTaskbar = false;
        public bool CaptureAutoHideDesktopIcons = false;
        public bool HDRScreenshotColorCorrection = false;
        public Rectangle CaptureCustomRegion = new Rectangle(0, 0, 0, 0);
        public string CaptureCustomWindow = "";

        #endregion Capture / General

        #region Capture / Region capture

        public RegionCaptureOptions SurfaceOptions = new RegionCaptureOptions();

        #endregion Capture / Region capture

        #region Capture / Screen recorder

        public FFmpegOptions FFmpegOptions = new FFmpegOptions();
        public int ScreenRecordFPS = 30;
        public int GIFFPS = 15;
        public bool ScreenRecordShowCursor = true;
        public bool ScreenRecordShowTimer = true;
        public bool ScreenRecordShowButtonLabels = true;
        public bool ScreenRecordAutoStart = true;
        public float ScreenRecordStartDelay = 0f;
        public bool ScreenRecordFixedDuration = false;
        public float ScreenRecordDuration = 3f;
        public bool ScreenRecordTwoPassEncoding = false;
        public bool ScreenRecordAskConfirmationOnAbort = false;
        public bool ScreenRecordTransparentRegion = false;

        #endregion Capture / Screen recorder

        #region Capture / Scrolling capture

        public ScrollingCaptureOptions ScrollingCaptureOptions = new ScrollingCaptureOptions();

        #endregion Capture / Scrolling capture

        #region Capture / OCR

        public OCROptions OCROptions = new OCROptions();

        #endregion Capture / OCR
    }

    public class TaskSettingsUpload
    {
        #region Upload / Modlight

        /// <summary>
        /// The Modlight folder uploads made with these settings land in, or empty to
        /// follow the folder My space is showing.
        /// </summary>
        /// <remarks>
        /// This is the SETTING, and it is deliberately not the same thing as
        /// <see cref="TaskSettings.ModlightFolderId"/>, which is where one particular
        /// upload is going and is never written to disk. This one is persisted and it
        /// is per task settings, so a hotkey that overrides its upload settings can
        /// send its captures somewhere of its own while everything else follows the
        /// default - which is the whole request: a screenshot hotkey that always files
        /// into "Screenshots" without anyone having to remember to browse there first.
        ///
        /// Empty means "expressed no preference", and that is what keeps the ambient
        /// target alive for everybody who never opens this page. The root sentinel
        /// (<c>root</c>) is a preference like any other: it means "no folder,
        /// deliberately".
        ///
        /// An id whose folder has since been deleted in the panel is NOT a failed
        /// upload. The server stores the file in the account's default folder and says
        /// so in <c>folder_warning</c>, because by the time it can check, the bytes are
        /// already on its disk. The folder picker shows such an id as missing so it can
        /// be corrected; nothing here refuses to send it, and nothing may, because the
        /// only way to know it is gone is to ask the server on the capture path.
        /// </remarks>
        public string ModlightFolderId = "";

        /// <summary>
        /// The folder's name when it was picked, so the settings page can say which
        /// folder is set without a round trip - and can still say it offline.
        /// </summary>
        /// <remarks>
        /// A cache of a display label, never an input to an upload: the id is what is
        /// sent, and a stale name here cannot send a file anywhere. Refreshed whenever
        /// the picker manages to load the folder list.
        /// </remarks>
        public string ModlightFolderName = "";

        #endregion Upload / Modlight

        #region Upload / File naming

        public bool UseCustomTimeZone = false;
        public TimeZoneInfo CustomTimeZone = TimeZoneInfo.Utc;
        public string NameFormatPattern = "%ra{10}";
        public string NameFormatPatternActiveWindow = "%pn_%ra{10}";
        public bool FileUploadUseNamePattern = false;
        public bool FileUploadReplaceProblematicCharacters = false;
        public bool URLRegexReplace = false;
        public string URLRegexReplacePattern = "^https?://(.+)$";
        public string URLRegexReplaceReplacement = "https://$1";

        #endregion Upload / File naming

        #region Upload / Clipboard upload

        public bool ClipboardUploadURLContents = false;
        public bool ClipboardUploadShortenURL = false;
        public bool ClipboardUploadShareURL = false;
        public bool ClipboardUploadAutoIndexFolder = false;

        #endregion Upload / Clipboard upload
    }

    public class TaskSettingsTools
    {
        public ScreenColorPickerOptions ScreenColorPickerOptions = new ScreenColorPickerOptions();
        public PinToScreenOptions PinToScreenOptions = new PinToScreenOptions();
        public IndexerSettings IndexerSettings = new IndexerSettings();
        public ImageCombinerOptions ImageCombinerOptions = new ImageCombinerOptions();
        public VideoConverterOptions VideoConverterOptions = new VideoConverterOptions();
        public BorderlessWindowSettings BorderlessWindowSettings = new BorderlessWindowSettings();
        public AIOptions AIOptions = new AIOptions();
        public ImageEditorOptions ImageEditorOptions = new ImageEditorOptions();
        public BackgroundRemoverOptions BackgroundRemoverOptions = new BackgroundRemoverOptions();
        public bool UseLegacyImageEditor = false;
        public bool ShowImageEditorSelector = true;
    }

    public class TaskSettingsAdvanced
    {
        [Category("General"), DefaultValue(false), Description("Allow after capture tasks for image files by loading them as bitmap when files are handled during file upload, clipboard file upload, drag && drop file upload, watch folder and other image file tasks.")]
        public bool ProcessImagesDuringFileUpload { get; set; }

        [Category("General"), DefaultValue(false), Description("Use after capture tasks for clipboard image uploads.")]
        public bool ProcessImagesDuringClipboardUpload { get; set; }

        [Category("General"), DefaultValue(true), Description("Allows file related after capture tasks (\"Perform actions\", \"Copy file to clipboard\" etc.) to be used when doing file upload.")]
        public bool UseAfterCaptureTasksDuringFileUpload { get; set; }

        [Category("General"), DefaultValue(true), Description("Save text as file for tasks such as clipboard text upload, drag and drop text upload, index folder etc.")]
        public bool TextTaskSaveAsFile { get; set; }

        [Category("General"), DefaultValue(false), Description("If task contains upload job then this setting will clear clipboard when task start.")]
        public bool AutoClearClipboard { get; set; }

        [Category("Capture"), DefaultValue(false), Description("Disable annotation support in region capture.")]
        public bool RegionCaptureDisableAnnotation { get; set; }

        [Category("Upload"), Description("Files with these file extensions will be uploaded using image uploader."),
        Editor("System.Windows.Forms.Design.StringCollectionEditor,System.Design, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", typeof(UITypeEditor))]
        public List<string> ImageExtensions { get; set; }

        [Category("Upload"), Description("Files with these file extensions will be uploaded using text uploader."),
        Editor("System.Windows.Forms.Design.StringCollectionEditor,System.Design, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", typeof(UITypeEditor))]
        public List<string> TextExtensions { get; set; }

        [Category("Upload"), DefaultValue(false), Description("Copy URL before start upload. Only works for FTP, FTPS, SFTP, Amazon S3, Google Cloud Storage and Azure Storage.")]
        public bool EarlyCopyURL { get; set; }

        [Category("Upload text"), DefaultValue("txt"), Description("File extension when saving text to the local hard disk.")]
        public string TextFileExtension { get; set; }

        [Category("Upload text"), DefaultValue("text"), Description("Text format e.g. csharp, cpp, etc.")]
        public string TextFormat { get; set; }

        [Category("Upload text"), DefaultValue(""), Description("Custom text input. Use %input for text input. Example you can create web page with your text in it."),
        Editor(typeof(MultilineStringEditor), typeof(UITypeEditor))]
        public string TextCustom { get; set; }

        [Category("Upload text"), DefaultValue(true), Description("HTML encode custom text input.")]
        public bool TextCustomEncodeInput { get; set; }

        [Category("After upload"), DefaultValue(false), Description("If result URL starts with \"http://\" then replace it with \"https://\".")]
        public bool ResultForceHTTPS { get; set; }

        [Category("After upload"), DefaultValue("$result"),
        Description("Clipboard content format after uploading. Supported variables: $result, $url, $shorturl, $thumbnailurl, $deletionurl, $filepath, $filename, $filenamenoext, $folderpath, $foldername, $uploadtime and other variables such as %y-%mo-%d etc.")]
        public string ClipboardContentFormat { get; set; }

        [Category("After upload"), DefaultValue("$result"), Description("Balloon tip content format after uploading. Supported variables: $result, $url, $shorturl, $thumbnailurl, $deletionurl, $filepath, $filename, $filenamenoext, $folderpath, $foldername, $uploadtime and other variables such as %y-%mo-%d etc.")]
        public string BalloonTipContentFormat { get; set; }

        [Category("After upload"), DefaultValue("$result"), Description("After upload task \"Open URL\" format. Supported variables: $result, $url, $shorturl, $thumbnailurl, $deletionurl, $filepath, $filename, $filenamenoext, $folderpath, $foldername, $uploadtime and other variables such as %y-%mo-%d etc.")]
        public string OpenURLFormat { get; set; }

        [Category("After upload"), DefaultValue(0), Description("Automatically shorten URL if the URL is longer than the specified number of characters. 0 means automatic URL shortening is not active.")]
        public int AutoShortenURLLength { get; set; }

        [Category("After upload"), DefaultValue(false), Description("After upload form will be automatically closed after 60 seconds.")]
        public bool AutoCloseAfterUploadForm { get; set; }

        [Category("Name pattern"), DefaultValue(100), Description("Maximum name pattern length for file name.")]
        public int NamePatternMaxLength { get; set; }

        [Category("Name pattern"), DefaultValue(50), Description("Maximum name pattern title (%t) length for file name.")]
        public int NamePatternMaxTitleLength { get; set; }

        public TaskSettingsAdvanced()
        {
            this.ApplyDefaultPropertyValues();
            ImageExtensions = FileHelpers.ImageFileExtensions.ToList();
            TextExtensions = FileHelpers.TextFileExtensions.ToList();
        }
    }
}
