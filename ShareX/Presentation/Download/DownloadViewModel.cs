#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// One entry in the quality dropdown: a label, a sentence saying what it means, and the
/// thing it stands for.
/// </summary>
/// <remarks>
/// <para>
/// The lists are built once and their entries compared by reference, which is what makes
/// <c>SelectedItem</c> binding work without an equality override.
/// </para>
/// <para>
/// One type serves both halves of the picker, even though the two lists hold different
/// things - video resolutions and audio containers. The alternative was a second option
/// class and a second binding for a control that sits in the same place and answers the
/// same question ("how good, and as what"), which is two of everything for a difference
/// nobody looking at the tab can see.
/// </para>
/// </remarks>
internal sealed class DownloadFormatOption
{
    private DownloadFormatOption(string label, string note, string recipe)
    {
        Label = label;
        Note = note;
        Recipe = recipe;
    }

    /// <summary>What the dropdown shows: "Best available", "1080p", "MP3".</summary>
    public string Label { get; }

    /// <summary>One sentence beside the picker saying what picking this does.</summary>
    public string Note { get; }

    /// <summary>The same thing in yt-dlp's own words, for the tooltip.</summary>
    public string Recipe { get; }

    public DownloadQuality Quality { get; private init; }

    public DownloadAudioFormat AudioFormat { get; private init; }

    public static DownloadFormatOption ForVideo(DownloadQuality quality)
    {
        int resolution = DownloadFormat.ResolutionOf(quality);

        string label = resolution == 0
            ? Strings.Download_QualityBest
            : string.Format(Strings.Download_QualityHeight, resolution);

        string note = resolution == 0
            ? Strings.Download_FormatNoteBest
            : string.Format(Strings.Download_FormatNoteHeight, resolution);

        DownloadFormat format = new(DownloadMediaKind.Video, quality, DownloadAudioFormat.M4a);

        return new DownloadFormatOption(label, note, format.Recipe(isYouTube: true))
        {
            Quality = quality
        };
    }

    public static DownloadFormatOption ForAudio(DownloadAudioFormat audio)
    {
        bool mp3 = audio == DownloadAudioFormat.Mp3;

        DownloadFormat format = new(DownloadMediaKind.Audio, DownloadQuality.Best, audio);

        return new DownloadFormatOption(
            mp3 ? Strings.Download_AudioMp3 : Strings.Download_AudioM4a,
            mp3 ? Strings.Download_FormatNoteMp3 : Strings.Download_FormatNoteM4a,
            format.Recipe(isYouTube: true))
        {
            AudioFormat = audio
        };
    }
}

/// <summary>
/// The Download tab: an address box, one destination for everything, and the queue.
/// </summary>
/// <remarks>
/// UI thread only.
///
/// One download runs at a time. That is not a simplification - yt-dlp already fetches four
/// fragments at once, so a second job would not use a line that is not already full, and
/// running several is the reliable way to make a site start answering 429. A queue that
/// waits its turn is also the only shape in which "pause this one" has an obvious meaning.
/// </remarks>
internal sealed class DownloadViewModel : INotifyPropertyChanged
{
    /// <summary>How long a Modlight-only download's scratch copy is allowed to linger.</summary>
    /// <remarks>
    /// Long enough that it cannot be swept out from under an upload that is still running,
    /// short enough that a week of downloads is not sitting in the temp folder. The upload
    /// is handed to the task manager and not waited on, so the only safe rule is one based
    /// on age rather than on a completion we never see.
    /// </remarks>
    private static readonly TimeSpan scratchLifetime = TimeSpan.FromHours(24);

    private string urlText = "";
    private string urlError = "";
    private bool toolMissing;
    private bool ffmpegMissing;
    private string toolPath = "";

    private bool signedIn;
    private bool signingIn;
    private bool browserMissing;
    private string signInMessage = "";

    public DownloadViewModel()
    {
        Items.CollectionChanged += (_, _) => NotifyList();

        RefreshTool();
        RefreshSignIn();

        // Yesterday's Modlight-only downloads, whose uploads finished long ago. Off the UI
        // thread because it touches a directory that may be on a slow disk.
        Task.Run(SweepScratch);
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();

    /// <summary>What is in the address box.</summary>
    public string UrlText
    {
        get => urlText;
        set
        {
            if (SetField(ref urlText, value) && urlError.Length > 0)
            {
                // The complaint is about what was in the box, so it goes the moment the
                // box changes. Leaving it up would have somebody fixing a typo and still
                // reading that their address is wrong.
                UrlError = "";
            }
        }
    }

    /// <summary>Why the address in the box was refused, or empty.</summary>
    public string UrlError
    {
        get => urlError;
        private set
        {
            SetField(ref urlError, value);
            OnPropertyChanged(nameof(HasUrlError));
        }
    }

    public bool HasUrlError => urlError.Length > 0;

    /// <summary>yt-dlp is not on this PC. The tab says so in place of the queue.</summary>
    public bool ToolMissing
    {
        get => toolMissing;
        private set
        {
            SetField(ref toolMissing, value);
            OnPropertyChanged(nameof(ToolReady));
            NotifyList();
        }
    }

    public bool ToolReady => !toolMissing;

    /// <summary>Which yt-dlp is being used - the answer to "but I installed it".</summary>
    public string ToolPath
    {
        get => toolPath;
        private set => SetField(ref toolPath, value);
    }

    #region Destination

    public DownloadDestination Destination
    {
        get => Program.Settings?.DownloadDestination ?? DownloadDestination.Disk;
        set
        {
            if (Program.Settings == null || Program.Settings.DownloadDestination == value)
            {
                return;
            }

            Program.Settings.DownloadDestination = value;
            SettingManager.SaveApplicationConfigAsync();

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDestinationDisk));
            OnPropertyChanged(nameof(IsDestinationModlight));
            OnPropertyChanged(nameof(IsDestinationBoth));
            OnPropertyChanged(nameof(DestinationNote));
            OnPropertyChanged(nameof(ShowFolderRow));
        }
    }

    public bool IsDestinationDisk => Destination == DownloadDestination.Disk;
    public bool IsDestinationModlight => Destination == DownloadDestination.Modlight;
    public bool IsDestinationBoth => Destination == DownloadDestination.Both;

    /// <summary>One line saying what the chosen destination actually does.</summary>
    public string DestinationNote => Destination switch
    {
        DownloadDestination.Modlight => Strings.Download_NoteModlight,
        DownloadDestination.Both => Strings.Download_NoteBoth,
        _ => Strings.Download_NoteDisk
    };

    /// <summary>A Modlight-only download has nowhere to put on screen: its copy is scratch.</summary>
    public bool ShowFolderRow => Destination != DownloadDestination.Modlight;

    #endregion Destination

    #region Format

    /// <summary>
    /// Every video quality on offer, and every audio container, built once.
    /// </summary>
    /// <remarks>
    /// Once because the entries are compared by reference by the dropdown's SelectedItem
    /// binding, so a list rebuilt on every get would arrive with nothing selected.
    /// </remarks>
    private readonly List<DownloadFormatOption> videoOptions = BuildVideoOptions();

    private readonly List<DownloadFormatOption> audioOptions =
    [
        DownloadFormatOption.ForAudio(DownloadAudioFormat.M4a),
        DownloadFormatOption.ForAudio(DownloadAudioFormat.Mp3)
    ];

    private static List<DownloadFormatOption> BuildVideoOptions()
    {
        List<DownloadFormatOption> built = new();

        foreach (DownloadQuality quality in DownloadFormat.Qualities)
        {
            built.Add(DownloadFormatOption.ForVideo(quality));
        }

        return built;
    }

    /// <summary>
    /// Video or sound, as one choice rather than a consequence of the quality list.
    /// </summary>
    /// <remarks>
    /// Kept as a setting rather than asked per download, exactly like the destination beside
    /// it and for the same reason: somebody who rips audio does it for every link, and a
    /// question whose answer never changes is a question worth asking once. It is written
    /// through to disk immediately - a preference that survives only until the app is closed
    /// is a preference the user gets to set twice.
    /// </remarks>
    public DownloadMediaKind MediaKind
    {
        get => Program.Settings?.DownloadMediaKind ?? DownloadMediaKind.Video;
        set
        {
            if (Program.Settings == null || Program.Settings.DownloadMediaKind == value)
            {
                return;
            }

            Program.Settings.DownloadMediaKind = value;
            SettingManager.SaveApplicationConfigAsync();

            NotifyFormat();
        }
    }

    public bool IsVideoKind => MediaKind == DownloadMediaKind.Video;
    public bool IsAudioKind => MediaKind == DownloadMediaKind.Audio;

    /// <summary>The resolutions. The list behind the picker that is on screen in Video.</summary>
    /// <remarks>
    /// <para>
    /// One list per picker, and neither list is ever replaced. There used to be a single
    /// <c>QualityOptions</c> that returned whichever list matched the kind, with one ComboBox
    /// bound to it - and swapping a ComboBox's ItemsSource clears its selection. Measured on
    /// the built app: pressing "Dźwięk" left the box BLANK while the label and the sentence
    /// beside it changed correctly, and it stayed blank for the rest of the session, leaving
    /// the tab and coming back included. A restart with Audio already saved showed "M4A"
    /// straight away, which is the tell - on a fresh start the list is set once and never
    /// swapped.
    /// </para>
    /// <para>
    /// Re-asserting the selection afterwards was tried first, posted at Background priority
    /// so it would land after the list change. It did not hold. So the swap is gone rather
    /// than worked around: two controls in one cell, one visible at a time, each with a
    /// constant ItemsSource and a selection of its own. The layout is unchanged - a control
    /// with IsVisible false is not measured, so the Auto column still sizes to the one on
    /// screen - and there is no ordering left to get wrong.
    /// </para>
    /// </remarks>
    public IReadOnlyList<DownloadFormatOption> VideoQualityOptions => videoOptions;

    /// <summary>The containers. The list behind the picker that is on screen in Audio.</summary>
    public IReadOnlyList<DownloadFormatOption> AudioFormatOptions => audioOptions;

    /// <summary>"Quality" over a list of resolutions, "Save as" over a list of containers.</summary>
    public string QualityLabel => MediaKind == DownloadMediaKind.Audio
        ? Strings.Download_AudioFormatLabel
        : Strings.Download_QualityLabel;

    /// <summary>
    /// The entry showing in the Video picker.
    /// </summary>
    /// <remarks>
    /// Backed by the settings file rather than by a field, so the two halves of the picker
    /// cannot disagree: switching to Audio and back shows the resolution that was chosen
    /// before rather than resetting it, because the resolution was never overwritten. Each
    /// picker writes only its own setting, so the one that is off screen cannot reach the
    /// other's.
    /// </remarks>
    public DownloadFormatOption? SelectedVideoQuality
    {
        get
        {
            DownloadQuality quality = Program.Settings?.DownloadVideoQuality ?? DownloadQuality.Best;

            foreach (DownloadFormatOption option in videoOptions)
            {
                if (option.Quality == quality)
                {
                    return option;
                }
            }

            return videoOptions[0];
        }
        set
        {
            // Still guarded against null. A ComboBox writes one back as it is detached, and
            // taking that as a choice would overwrite the setting with nothing on the way out.
            if (value == null || Program.Settings == null ||
                Program.Settings.DownloadVideoQuality == value.Quality)
            {
                return;
            }

            Program.Settings.DownloadVideoQuality = value.Quality;
            SettingManager.SaveApplicationConfigAsync();
            NotifyFormat();
        }
    }

    /// <summary>The entry showing in the Audio picker.</summary>
    public DownloadFormatOption? SelectedAudioFormat
    {
        get
        {
            DownloadAudioFormat wanted = Program.Settings?.DownloadAudioFormat ?? DownloadAudioFormat.M4a;

            foreach (DownloadFormatOption option in audioOptions)
            {
                if (option.AudioFormat == wanted)
                {
                    return option;
                }
            }

            return audioOptions[0];
        }
        set
        {
            if (value == null || Program.Settings == null ||
                Program.Settings.DownloadAudioFormat == value.AudioFormat)
            {
                return;
            }

            Program.Settings.DownloadAudioFormat = value.AudioFormat;
            SettingManager.SaveApplicationConfigAsync();
            NotifyFormat();
        }
    }

    /// <summary>Whichever of the two is the choice in force right now.</summary>
    /// <remarks>
    /// Read-only: the pickers write their own settings, and this is what the sentence, the
    /// tooltip and a queued row's chip are worked out from.
    /// </remarks>
    public DownloadFormatOption? SelectedQuality =>
        MediaKind == DownloadMediaKind.Audio ? SelectedAudioFormat : SelectedVideoQuality;

    /// <summary>What the current choice does, in one sentence, under the picker.</summary>
    public string FormatNote => SelectedQuality?.Note ?? "";

    /// <summary>The same thing as arguments, on the tooltip. The owner asked to be told the mapping.</summary>
    public string FormatHint =>
        string.Format(Strings.Download_QualityHint, SelectedQuality?.Recipe ?? "");

    /// <summary>What a row queued right now would be asked to fetch.</summary>
    public DownloadFormat CurrentFormat => new(
        MediaKind,
        Program.Settings?.DownloadVideoQuality ?? DownloadQuality.Best,
        Program.Settings?.DownloadAudioFormat ?? DownloadAudioFormat.M4a);

    /// <summary>
    /// ffmpeg is missing, so the choice above cannot be honoured.
    /// </summary>
    /// <remarks>
    /// Stated before anybody presses Download rather than explained afterwards. This is the
    /// state behind the reported bug: without a merger the only reachable YouTube format is
    /// one pre-muxed 360p stream, and every earlier version of this tab took it, wrote the
    /// file and reported success.
    /// </remarks>
    public bool ShowFFmpegNotice => ffmpegMissing;

    public string FFmpegNoticeBody => MediaKind == DownloadMediaKind.Audio
        ? Strings.Download_FFmpegMissingAudio
        : Strings.Download_FFmpegMissingVideo;

    /// <summary>
    /// Make sure ffmpeg is there, offering to install it if it is not.
    /// </summary>
    /// <returns>
    /// True when it is there afterwards. False means the user was asked and said no, and the
    /// caller carries on anyway - a download that happens at a lower quality with a line
    /// saying why beats a button that refuses to do anything.
    /// </returns>
    /// <remarks>
    /// Asked at the moment a download is queued, not when the tab is opened. The components
    /// window is modal and a person who has not asked for anything yet has not agreed to be
    /// interrupted by it.
    /// </remarks>
    public bool EnsureFFmpeg()
    {
        if (!ffmpegMissing)
        {
            return true;
        }

        bool installed = ComponentsWindowIntegration.Require(
            ModlightComponentCatalog.FFmpeg, Strings.Components_FeatureDownload);

        // Whatever the answer, the cached tool paths are now a guess about a folder that has
        // just been written to. RefreshTool re-reads both of them.
        RefreshTool();

        return installed;
    }

    private void NotifyFormat()
    {
        OnPropertyChanged(nameof(MediaKind));
        OnPropertyChanged(nameof(IsVideoKind));
        OnPropertyChanged(nameof(IsAudioKind));
        OnPropertyChanged(nameof(QualityLabel));

        // Neither list changes, so neither picker can lose its selection - but the value in
        // each still has to be re-read, because the other one may have been what changed.
        OnPropertyChanged(nameof(SelectedVideoQuality));
        OnPropertyChanged(nameof(SelectedAudioFormat));
        OnPropertyChanged(nameof(SelectedQuality));

        OnPropertyChanged(nameof(FormatNote));
        OnPropertyChanged(nameof(FormatHint));
        OnPropertyChanged(nameof(FFmpegNoticeBody));
    }

    #endregion Format

    #region Folder

    /// <summary>The folder every download goes to, or empty for "ask me".</summary>
    public string FixedFolder => Program.Settings?.DownloadFolder ?? "";

    public bool HasFixedFolder => FixedFolder.Length > 0;

    public string FolderText => HasFixedFolder ? FixedFolder : Strings.Download_FolderAsk;

    /// <summary>
    /// Remember a folder, or forget it and go back to asking.
    /// </summary>
    /// <remarks>
    /// Optional on purpose (decision item 15). Somebody who files things as they download
    /// wants the question; somebody who does not wants it gone after the first time.
    /// </remarks>
    public void SetFixedFolder(string? path)
    {
        if (Program.Settings == null)
        {
            return;
        }

        string value = path?.Trim() ?? "";

        if (Program.Settings.DownloadFolder == value)
        {
            return;
        }

        Program.Settings.DownloadFolder = value;
        SettingManager.SaveApplicationConfigAsync();

        OnPropertyChanged(nameof(FixedFolder));
        OnPropertyChanged(nameof(HasFixedFolder));
        OnPropertyChanged(nameof(FolderText));
    }

    /// <summary>
    /// Whether starting a download needs the folder question asked first.
    /// </summary>
    /// <remarks>
    /// A Modlight-only download never asks: the file is on its way to the server and the
    /// copy on this PC is a temporary file the tab cleans up itself. Asking where to put
    /// it would be asking about something the user is not getting.
    /// </remarks>
    public bool NeedsFolderChoice => Destination != DownloadDestination.Modlight && !HasFixedFolder;

    #endregion Folder

    #region YouTube sign-in

    /// <summary>
    /// There is a signed-in YouTube session on this PC.
    /// </summary>
    /// <remarks>
    /// Held rather than read on every get, because a binding asks a property far more often
    /// than anybody signs in or out. What it holds is not a memory of the last click: every
    /// refresh goes to <see cref="YouTubeCookies.IsSignedIn"/>, which parses the jar, and
    /// every way the answer can change ends in one - signing in, signing out, and the tab
    /// being shown or its panel opened, which covers a jar rewritten by a download or
    /// deleted from outside the app.
    /// </remarks>
    public bool SignedIn
    {
        get => signedIn;
        private set
        {
            if (SetField(ref signedIn, value))
            {
                NotifySignIn();
            }
        }
    }

    /// <summary>A sign-in window is open. Two would share one browser profile.</summary>
    public bool SigningIn
    {
        get => signingIn;
        private set
        {
            if (SetField(ref signingIn, value))
            {
                NotifySignIn();
            }
        }
    }

    /// <summary>
    /// The offer to sign in, which is gone once there is a session.
    /// </summary>
    /// <remarks>
    /// The owner's complaint about the row this replaces was exactly this: he signed in, a
    /// line appeared saying so, and the button asking him to sign in stayed where it was.
    /// A control that offers something already done is a control that makes the person
    /// doubt the thing was done.
    /// </remarks>
    public bool ShowSignIn => !signedIn && !signingIn;

    /// <summary>
    /// Signing in again on top of a session that is already there.
    /// </summary>
    /// <remarks>
    /// Not the same offer as <see cref="ShowSignIn"/> and not a duplicate of it: it is the
    /// remedy the failure message names when YouTube refuses a download WITH a session, ie
    /// the session went stale. Without it, following our own advice would mean signing out
    /// first, which reads as throwing something away in order to get it back.
    /// </remarks>
    public bool ShowSignInAgain => signedIn && !signingIn;

    /// <summary>Signing out is only offered when there is something to sign out of.</summary>
    public bool ShowSignOut => signedIn && !signingIn;

    /// <summary>The state in two or three words, next to the dot that colours it.</summary>
    public string SignInStatus
    {
        get
        {
            if (signingIn)
            {
                return Strings.Download_SignInBusy;
            }

            return signedIn ? Strings.Download_SignInStatusIn : Strings.Download_SignInStatusOut;
        }
    }

    /// <summary>
    /// What that state means for downloads, in one line.
    /// </summary>
    /// <remarks>
    /// No branch for "signing in". The sentence explains what the session is worth, and
    /// that does not stop being true while a window is open on top of it - the headline
    /// above says what is happening right now.
    /// </remarks>
    public string SignInStateText =>
        signedIn ? Strings.Download_SignInStateIn : Strings.Download_SignInStateOut;

    /// <summary>The outcome of the last sign-in attempt, or empty.</summary>
    public string SignInMessage
    {
        get => signInMessage;
        private set
        {
            if (SetField(ref signInMessage, value))
            {
                OnPropertyChanged(nameof(HasSignInMessage));
            }
        }
    }

    public bool HasSignInMessage => signInMessage.Length > 0;

    /// <summary>
    /// This PC has no WebView2 runtime, so the tab offers the page that installs one.
    /// </summary>
    /// <remarks>
    /// Only shown after an attempt, never as a standing warning. The runtime arrives with
    /// Edge and is present on any current Windows, so a notice about it on every install
    /// would be a permanent warning about a problem almost nobody has.
    /// </remarks>
    public bool ShowBrowserMissing => browserMissing;

    /// <summary>
    /// Open the sign-in window and wait for it to close. True when a session was captured.
    /// </summary>
    /// <remarks>
    /// The wait is an await on the window's own closing, not a modal loop: the queue keeps
    /// running, a download in flight keeps reporting progress, and the rest of the app
    /// stays alive behind it.
    ///
    /// The return value is "a NEW session was captured just now", not "signed in" - it is
    /// what a caller needs to decide whether to try a failed download again, and somebody
    /// who opened the window and closed it unchanged has not asked for anything to retry.
    /// </remarks>
    public async Task<bool> SignInAsync()
    {
        if (signingIn)
        {
            return false;
        }

        SigningIn = true;
        SignInMessage = "";
        browserMissing = false;
        OnPropertyChanged(nameof(ShowBrowserMissing));

        bool captured = false;

        try
        {
            YouTubeSignInResult result = await YouTubeSignInForm.ShowAsync();

            switch (result)
            {
                case YouTubeSignInResult.Captured:
                    captured = true;
                    SignInMessage = Strings.Download_SignInDone;
                    break;

                case YouTubeSignInResult.RuntimeMissing:
                    browserMissing = true;
                    SignInMessage = Strings.Download_SignInNoBrowser;
                    break;

                case YouTubeSignInResult.Failed:
                    SignInMessage = Strings.Download_SignInFailed;
                    break;

                default:
                    // Closed without finishing. Not an error, and saying so would be
                    // telling somebody off for changing their mind.
                    break;
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            SignInMessage = Strings.Download_SignInFailed;
        }
        finally
        {
            SigningIn = false;
            OnPropertyChanged(nameof(ShowBrowserMissing));
            RefreshSignIn();
        }

        if (captured)
        {
            RetrySignInFailures();
        }

        return captured;
    }

    /// <summary>Throw the session away.</summary>
    public void SignOut()
    {
        YouTubeCookies.SignOut();

        SignInMessage = "";
        RefreshSignIn();
    }

    /// <summary>Ask the disk again whether there is a session.</summary>
    /// <remarks>
    /// The disk, every time. <see cref="YouTubeCookies.IsSignedIn"/> parses the jar rather
    /// than checking that a file is there, so this answers with what yt-dlp would find
    /// rather than with what the last button press implied.
    /// </remarks>
    public void RefreshSignIn() => SignedIn = YouTubeCookies.IsSignedIn;

    /// <summary>
    /// Re-read everything the tab states as fact. Called when the tab is shown.
    /// </summary>
    /// <remarks>
    /// Both of these can change while the tab is not on screen: yt-dlp can be installed
    /// from the notice's own link, and the jar is rewritten at the end of every run and can
    /// be deleted from outside the app entirely. A tab that stated either from memory would
    /// be stating it from a moment that has passed.
    /// </remarks>
    public void Refresh()
    {
        RefreshTool();
        RefreshSignIn();
    }

    /// <summary>
    /// Put every row that failed for want of a session back in the queue.
    /// </summary>
    /// <remarks>
    /// Called only after a session was actually captured, never merely after the window was
    /// opened - somebody who thought better of it and closed it again has not asked for
    /// anything to run a second time.
    ///
    /// Every such row rather than the one whose button was pressed, because that is a fact
    /// about the rows rather than about the click: they all failed for the one reason that
    /// has just stopped being true, and which button opened the panel says nothing about
    /// which downloads the person wants. Taken over a copy, since a retry moves the row out
    /// of the state being looked for.
    /// </remarks>
    private void RetrySignInFailures()
    {
        List<DownloadItemViewModel> waiting = new();

        foreach (DownloadItemViewModel item in Items)
        {
            if (item.CanSignIn)
            {
                waiting.Add(item);
            }
        }

        foreach (DownloadItemViewModel item in waiting)
        {
            item.Retry();
        }
    }

    /// <summary>Everything on screen that the two sign-in flags decide between them.</summary>
    private void NotifySignIn()
    {
        OnPropertyChanged(nameof(ShowSignIn));
        OnPropertyChanged(nameof(ShowSignInAgain));
        OnPropertyChanged(nameof(ShowSignOut));
        OnPropertyChanged(nameof(SignInStatus));
        OnPropertyChanged(nameof(SignInStateText));
    }

    #endregion YouTube sign-in

    public bool HasItems => Items.Count > 0;

    /// <summary>
    /// Nothing queued, and it is worth saying why the space is empty.
    /// </summary>
    /// <remarks>
    /// Shown even when yt-dlp is missing. The two facts are both true and neither replaces
    /// the other: the notice above says the tool is not installed, this says the queue is
    /// empty, and leaving a blank rectangle between them would say nothing at all.
    /// </remarks>
    public bool ShowEmptyPanel => Items.Count == 0;

    public bool HasFinishedItems
    {
        get
        {
            foreach (DownloadItemViewModel item in Items)
            {
                if (item.IsFinished)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Ask again whether yt-dlp is installed, and say which one was found.</summary>
    /// <remarks>
    /// ffmpeg is re-read in the same breath. One rescan answers both questions, and the two
    /// notices below the address box would otherwise be able to disagree about a folder they
    /// both looked at.
    /// </remarks>
    public void RefreshTool()
    {
        YtDlpTool.Rescan();

        string? found = YtDlpTool.YtDlpPath;

        ToolMissing = found == null;
        ToolPath = found == null ? "" : string.Format(Strings.Download_ToolPath, found);

        bool missing = YtDlpTool.FFmpegPath == null;

        if (ffmpegMissing != missing)
        {
            ffmpegMissing = missing;
            OnPropertyChanged(nameof(ShowFFmpegNotice));
        }
    }

    /// <summary>
    /// Whether the box holds an address worth acting on, complaining if it does not.
    /// </summary>
    /// <remarks>
    /// Asked before the folder picker rather than after. Opening a folder chooser and only
    /// then saying the address was never valid wastes the one decision the user was asked
    /// to make, and does it after they have made it.
    /// </remarks>
    public bool CheckUrl()
    {
        if (DownloadLink.TryParse(urlText, out _))
        {
            return true;
        }

        UrlError = Strings.Download_BadUrl;
        return false;
    }

    /// <summary>
    /// Take what is in the address box and put it in the queue.
    /// </summary>
    /// <param name="chosenFolder">
    /// The folder the view just asked for, or null when the tab already knows where things
    /// go - either because a fixed folder is set or because this download is not keeping one.
    /// </param>
    /// <returns>False when the box did not hold a usable address; the reason is on <see cref="UrlError"/>.</returns>
    public bool Enqueue(string? chosenFolder)
    {
        if (!DownloadLink.TryParse(urlText, out DownloadLink link))
        {
            UrlError = Strings.Download_BadUrl;
            return false;
        }

        bool scratch = Destination == DownloadDestination.Modlight;
        string folder;

        if (scratch)
        {
            folder = ScratchFolder;
        }
        else
        {
            folder = chosenFolder ?? FixedFolder;

            if (folder.Length == 0)
            {
                // The view is supposed to have asked. Refusing beats writing a video into
                // whatever the working directory happens to be.
                UrlError = Strings.Download_FolderMissing;
                return false;
            }
        }

        // The choice as it stands right now, handed over and never looked at again. See the
        // field it lands in for why a queued row must not be able to change shape later.
        DownloadItemViewModel item = new(link, folder, scratch, Destination,
            CurrentFormat, SelectedQuality?.Label ?? "");

        item.StateChanged = () =>
        {
            NotifyList();
            Pump();
        };

        // Newest first: this is a list somebody looks at to see what is happening now, and
        // what is happening now is what they just added.
        Items.Insert(0, item);

        UrlText = "";
        UrlError = "";

        Pump();
        return true;
    }

    public void Remove(DownloadItemViewModel item)
    {
        if (!Items.Remove(item))
        {
            return;
        }

        // Kills the process if there still is one. A row taken off the list must not leave
        // a download running that nothing on screen owns any more.
        item.Dispose();
        NotifyList();
        Pump();
    }

    public void ClearFinished()
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].IsFinished)
            {
                Items.RemoveAt(i);
            }
        }

        NotifyList();
    }

    /// <summary>Stop everything. Called when the window is going away for good.</summary>
    public void Shutdown()
    {
        foreach (DownloadItemViewModel item in Items)
        {
            item.Dispose();
        }

        // The sign-in window is a WinForms window and this tab is an Avalonia control, so
        // it is owned by nothing that is being torn down here. Without this it would be
        // left on screen after the app that opened it had gone.
        YouTubeSignInForm.CloseIfOpen();
    }

    /// <summary>
    /// Start the next waiting row, if nothing is running.
    /// </summary>
    /// <remarks>
    /// Safe to call after any change at all: it is a no-op while something is active and
    /// when there is nothing waiting, which is what lets every state change call it without
    /// anyone having to work out whether this particular one was the one that freed a slot.
    /// </remarks>
    private void Pump()
    {
        if (ToolMissing)
        {
            return;
        }

        DownloadItemViewModel? next = null;

        // Oldest waiting row first, which is the bottom of the list - the display is
        // newest-first but the queue is a queue.
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            DownloadItemViewModel item = Items[i];

            if (item.IsActive)
            {
                return;
            }

            if (next == null && item.State == DownloadState.Queued)
            {
                next = item;
            }
        }

        next?.Start();
    }

    #region Scratch folder

    /// <summary>Where a Modlight-only download's copy lives until the upload has had it.</summary>
    private static string ScratchFolder =>
        Path.Combine(Path.GetTempPath(), Program.AppName, "Downloads");

    private static void SweepScratch()
    {
        try
        {
            string folder = ScratchFolder;

            if (!Directory.Exists(folder))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow - scratchLifetime;

            foreach (string file in Directory.GetFiles(folder))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception)
                {
                    // In use, or read-only, or gone since the listing. One file that will
                    // not be deleted is not a reason to stop clearing the rest.
                }
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }

    #endregion Scratch folder

    private void NotifyList()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmptyPanel));
        OnPropertyChanged(nameof(HasFinishedItems));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
