#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Integration;
using ShareX.AvaloniaUI.Theming;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShareX.HelpersLib;

public readonly record struct DownloaderWindowResult(DialogResult DialogResult, DownloaderWindowStatus Status);

public partial class DownloaderWindow : Window
{
    public delegate void DownloaderInstallEventHandler(string filePath);
    public event DownloaderInstallEventHandler? InstallRequested;

    public string URL { get; set; }
    public string FileName { get; set; }
    public string DownloadLocation { get; private set; } = string.Empty;
    public string AcceptHeader { get; set; } = string.Empty;
    public bool AutoStartDownload { get; set; } = true;
    public InstallType InstallType { get; set; } = InstallType.Silent;

    /// <summary>
    /// The SHA-256 the release feed published for this file, as hex.
    /// </summary>
    /// <remarks>
    /// Set it and the finished download is hashed before anything may be run, and thrown
    /// away if it disagrees. Leave it empty - which is what every caller that is not the
    /// updater does, ffmpeg included - and no hashing happens, exactly as before.
    ///
    /// The reason this exists is narrow and worth stating: <see cref="StartInstaller"/> starts
    /// the downloaded file as a process, and on a default install it does so through a "runas"
    /// verb. That is an arbitrary-code-execution path that ends with whatever privileges the
    /// user grants the elevation prompt, and the only thing standing between it and a
    /// tampered or truncated file is this string.
    /// </remarks>
    public string ExpectedSha256 { get; set; } = string.Empty;
    public bool AutoStartInstall { get; set; } = true;
    public DownloaderWindowStatus Status { get; private set; } = DownloaderWindowStatus.Waiting;
    public bool RunInstallerInBackground { get; set; } = true;

    /// <summary>ERROR_CANCELLED: what ShellExecute returns when the elevation prompt is declined.</summary>
    private const int ErrorCancelled = 1223;

    private FileDownloader? _fileDownloader;
    private DialogResult _result;

    public DownloaderWindow() : this(string.Empty, string.Empty)
    {
    }

    private DownloaderWindow(string url, string fileName)
    {
        URL = url;
        FileName = fileName;

        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        FileNameText.Text = Helpers.SafeStringFormat(Localization.Strings.DownloaderForm_DownloaderForm_Filename___0_, FileName);
        ActionButton.Content = Localization.Strings.DownloaderWindow_Download;
        ChangeStatus(Localization.Strings.DownloaderForm_DownloaderForm_Waiting_);

        Opened += OnOpened;
        Closing += OnClosing;
    }

    public static Task<DownloaderWindowResult> ShowAsync(
        string url,
        string fileName,
        Action<DownloaderWindow>? configure = null)
    {
        return ShowAsyncCore(() => new DownloaderWindow(url, fileName), configure);
    }

    public static Task<DownloaderWindowResult> ShowAsync(UpdateChecker updateChecker)
    {
        return ShowAsyncCore(() => new DownloaderWindow(updateChecker.DownloadURL, updateChecker.FileName), window =>
        {
            window.ExpectedSha256 = updateChecker.ExpectedSha256;

            if (updateChecker is GitHubUpdateChecker)
            {
                window.AcceptHeader = "application/octet-stream";
            }
        });
    }

    private static Task<DownloaderWindowResult> ShowAsyncCore(
        Func<DownloaderWindow> createWindow,
        Action<DownloaderWindow>? configure)
    {
        AvaloniaBootstrapper.EnsureInitialized();
        TaskCompletionSource<DownloaderWindowResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                DownloaderWindow window = createWindow();
                configure?.Invoke(window);
                window.Closed += (_, _) => completion.TrySetResult(new DownloaderWindowResult(window._result, window.Status));
                window.Show();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });

        return completion.Task;
    }

    /// <summary>
    /// Start what was downloaded, and call this a success only if that actually happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the whole point. It used to be: set InstallStarted, hand the launch to a
    /// background thread that slept a second, then Close(). So the window was gone, the caller
    /// had already been told "install started" and the application was already on its way out
    /// before Process.Start ran - and whatever Process.Start then did, nobody could hear it,
    /// because the catch block was empty and did not even log. On a default installation the
    /// launch carries the "runas" verb, which makes the single most common outcome in the
    /// field - somebody clicking No on the elevation prompt - a Win32Exception that went
    /// nowhere at all. "I pressed update and nothing happened", with not one line to point at.
    /// </para>
    /// <para>
    /// Now the process is started while this window is still on screen and the application is
    /// still running, and the status only becomes InstallStarted if the start succeeded. A
    /// refusal leaves the window open saying what happened, keeps the verified download, and
    /// leaves the app running - so UpdateChecker never exits to make way for an installer that
    /// is not there.
    /// </para>
    /// <para>
    /// Off the UI thread, because Process.Start with "runas" blocks until the elevation prompt
    /// is answered, and that is a person's decision rather than a computer's.
    /// </para>
    /// </remarks>
    public async Task InstallAsync()
    {
        if (Status != DownloaderWindowStatus.DownloadCompleted) return;

        ActionButton.IsEnabled = false;

        if (InstallType == InstallType.Event)
        {
            // Nothing is launched on this path - the caller takes the file from the event - so
            // there is no start that can be refused and the original shape is kept.
            Status = DownloaderWindowStatus.InstallStarted;
            _result = DialogResult.OK;
            RaiseInstallRequested();
            Close();
            return;
        }

        ChangeStatus(Localization.Strings.DownloaderWindow_StartingInstaller);

        InstallerLaunch launch = await Task.Run(StartInstaller);

        if (!launch.Started)
        {
            KeepInstallable(launch.Message);
            return;
        }

        Status = DownloaderWindowStatus.InstallStarted;
        _result = DialogResult.OK;
        Close();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (AutoStartDownload)
        {
            await StartDownloadAsync();
        }
    }

    private async void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if (Status == DownloaderWindowStatus.Waiting)
        {
            await StartDownloadAsync();
        }
        else if (Status == DownloaderWindowStatus.DownloadCompleted)
        {
            await InstallAsync();
        }
        else
        {
            // Covers Cancel while downloading and Close after a failure. Both leave with
            // DialogResult.Cancel, which is what stops UpdateChecker.DownloadUpdateAsync
            // from exiting the app to make way for an installer that is not going to run.
            _result = DialogResult.Cancel;
            Close();
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (Status == DownloaderWindowStatus.DownloadStarted)
        {
            _fileDownloader?.StopDownload();
        }
    }

    private async Task StartDownloadAsync()
    {
        if (string.IsNullOrEmpty(URL) || Status != DownloaderWindowStatus.Waiting) return;

        Status = DownloaderWindowStatus.DownloadStarted;
        ActionButton.Content = Localization.Strings.DownloaderForm_StartDownload_Cancel;
        DownloadProgress.Value = 0;

        string folderPath = Path.Combine(Path.GetTempPath(), "Modlight");
        FileHelpers.CreateDirectory(folderPath);
        DownloadLocation = Path.Combine(folderPath, FileName);

        if (await ReuseVerifiedDownloadAsync())
        {
            return;
        }

        DebugHelper.WriteLine($"Downloading: \"{URL}\" -> \"{DownloadLocation}\"");

        FileDownloader downloader = new FileDownloader(URL, DownloadLocation)
        {
            AcceptHeader = AcceptHeader
        };
        downloader.FileSizeReceived += OnFileSizeReceived;
        downloader.ProgressChanged += OnProgressChanged;
        _fileDownloader = downloader;

        ChangeStatus(Localization.Strings.DownloaderForm_StartDownload_Getting_file_size_);

        try
        {
            bool completed = await downloader.StartDownload();

            if (!completed)
            {
                // Cancelled means the window is already on its way out, so saying anything
                // would be talking to a closing dialog. Anything else is a download that
                // stopped short, and a half file must never be left looking like a whole one.
                if (!downloader.IsCanceled)
                {
                    Fail(Localization.Strings.DownloaderWindow_DownloadIncomplete);
                    ModlightUpdateFile.TryDelete(DownloadLocation);
                }

                return;
            }

            if (!await VerifyDownloadAsync())
            {
                return;
            }

            ChangeStatus(Localization.Strings.DownloaderForm_fileDownloader_DownloadCompleted_Download_completed_);
            Status = DownloaderWindowStatus.DownloadCompleted;
            ActionButton.Content = Localization.Strings.DownloaderForm_fileDownloader_DownloadCompleted_Install;

            if (AutoStartInstall)
            {
                await InstallAsync();
            }
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
            ModlightUpdateFile.TryDelete(DownloadLocation);
        }
    }

    /// <summary>
    /// Skip the download when the same release is already in temp and still hashes right.
    /// </summary>
    /// <remarks>
    /// The check runs on every launch and the installer is around eighty megabytes, so a
    /// person who says "later" three times should not pay for it three times. The digest is
    /// what makes reusing a file out of a world-writable temp directory safe, which is why
    /// this shortcut exists ONLY when there is one: with no expected hash there is no way to
    /// tell last week&apos;s download from something that merely has its name.
    /// </remarks>
    private async Task<bool> ReuseVerifiedDownloadAsync()
    {
        if (string.IsNullOrEmpty(ExpectedSha256) || !File.Exists(DownloadLocation))
        {
            return false;
        }

        ChangeStatus(Localization.Strings.DownloaderWindow_Verifying);

        ModlightVerification verification =
            await Task.Run(() => ModlightUpdateFile.VerifyOrDeleteAsync(DownloadLocation, ExpectedSha256, CancellationToken.None));

        if (!verification.Matches)
        {
            DebugHelper.WriteLine("Update: discarded stale download in temp ({0}).",
                verification.Error ?? "digest mismatch");
            return false;
        }

        DebugHelper.WriteLine($"Update: reusing verified download \"{DownloadLocation}\"");
        ChangeStatus(Localization.Strings.DownloaderWindow_AlreadyDownloaded);
        DownloadProgress.Value = 100;
        Status = DownloaderWindowStatus.DownloadCompleted;
        ActionButton.Content = Localization.Strings.DownloaderForm_fileDownloader_DownloadCompleted_Install;

        if (AutoStartInstall)
        {
            await InstallAsync();
        }

        return true;
    }

    /// <summary>
    /// Hash what was downloaded and compare it with what the feed promised.
    /// </summary>
    /// <returns>
    /// True when the file may be installed. False when it may not - and by then the file is
    /// already deleted and the window is in <see cref="DownloaderWindowStatus.Failed"/>.
    /// </returns>
    /// <remarks>
    /// Off the UI thread: hashing eighty megabytes takes long enough to freeze a window, and
    /// a frozen window that says "verifying" is indistinguishable from a hung one.
    /// </remarks>
    private async Task<bool> VerifyDownloadAsync()
    {
        if (string.IsNullOrEmpty(ExpectedSha256))
        {
            return true;
        }

        ChangeStatus(Localization.Strings.DownloaderWindow_Verifying);

        ModlightVerification verification = await Task.Run(() =>
            ModlightUpdateFile.VerifyOrDeleteAsync(DownloadLocation, ExpectedSha256, CancellationToken.None));

        if (verification.Matches)
        {
            DebugHelper.WriteLine("Update: checksum verified ({0}).", verification.Actual);
            return true;
        }

        DebugHelper.WriteLine("Update: REJECTED download. Expected {0}, got {1}. {2}",
            ExpectedSha256, string.IsNullOrEmpty(verification.Actual) ? "nothing" : verification.Actual,
            verification.Error ?? "");

        Fail(Localization.Strings.DownloaderWindow_VerificationFailed);
        return false;
    }

    /// <summary>
    /// Say what stopped the installer, and stay ready to try again.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Fail"/>. The bytes on disk are downloaded AND verified;
    /// what failed is the launch, and the commonest reason for that - a declined elevation
    /// prompt - is undone by pressing the button again. Going terminal here would make the
    /// only way back an eighty-megabyte re-download, or a wait for the next hourly check.
    /// </remarks>
    private void KeepInstallable(string message)
    {
        Status = DownloaderWindowStatus.DownloadCompleted;
        ChangeStatus(message);

        void Update()
        {
            ActionButton.Content = Localization.Strings.DownloaderForm_fileDownloader_DownloadCompleted_Install;
            ActionButton.IsEnabled = true;
        }

        if (Dispatcher.UIThread.CheckAccess()) Update();
        else Dispatcher.UIThread.Post(Update);
    }

    /// <summary>Put the window into its one terminal, non-installable state.</summary>
    private void Fail(string message)
    {
        Status = DownloaderWindowStatus.Failed;
        ChangeStatus(message);

        void Update()
        {
            DownloadProgress.Value = 0;
            ActionButton.Content = Localization.Strings.DownloaderWindow_Close;
            ActionButton.IsEnabled = true;
        }

        if (Dispatcher.UIThread.CheckAccess()) Update();
        else Dispatcher.UIThread.Post(Update);
    }

    private void OnFileSizeReceived()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ChangeStatus(Localization.Strings.DownloaderForm_StartDownload_Downloading_);
            UpdateProgress();
        });
    }

    private void OnProgressChanged() => Dispatcher.UIThread.Post(UpdateProgress);

    private void UpdateProgress()
    {
        if (_fileDownloader == null) return;

        DownloadProgress.Value = _fileDownloader.DownloadPercentage;
        ProgressText.Text = $@"{Localization.Strings.DownloaderForm_FileDownloader_ProgressChanged_Progress}: {_fileDownloader.DownloadPercentage:0.0}%
{Localization.Strings.DownloaderForm_FileDownloader_ProgressChanged_DownloadSpeed}: {((long)_fileDownloader.DownloadSpeed).ToSizeString()}/s
{Localization.Strings.DownloaderForm_FileDownloader_ProgressChanged_FileSize}: {_fileDownloader.DownloadedSize.ToSizeString()} / {_fileDownloader.FileSize.ToSizeString()}";
    }

    private void ChangeStatus(string status)
    {
        void Update() => StatusText.Text = Helpers.SafeStringFormat(Localization.Strings.DownloaderForm_ChangeStatus_Status___0_, status);
        if (Dispatcher.UIThread.CheckAccess()) Update();
        else Dispatcher.UIThread.Post(Update);
    }

    /// <summary>Hand the downloaded file to whoever asked for it. Only for <see cref="InstallType.Event"/>.</summary>
    /// <remarks>
    /// The delayed thread is kept here, and only here, because this path has a caller that
    /// wants it: FFmpegGitHubDownloader passes RunInstallerInBackground so the extraction does
    /// not run inside the click that started it. It is a foreground thread so a shutdown
    /// racing the timer cannot abandon the work half done.
    /// </remarks>
    private void RaiseInstallRequested(int delay = 1000)
    {
        if (RunInstallerInBackground)
        {
            Thread thread = new(() =>
            {
                Thread.Sleep(delay);
                InstallRequested?.Invoke(DownloadLocation);
            })
            {
                IsBackground = false
            };
            thread.Start();
        }
        else
        {
            Hide();
            InstallRequested?.Invoke(DownloadLocation);
        }
    }

    /// <summary>Whether the installer process was actually created, and what to say if not.</summary>
    private readonly record struct InstallerLaunch(bool Started, string Message);

    /// <summary>
    /// Start the downloaded installer, and report what happened instead of swallowing it.
    /// </summary>
    /// <remarks>
    /// Every failure here used to go into an empty <c>catch { }</c> that did not even log, so
    /// there was nothing to answer a report with. The three that happen in the field:
    /// the elevation prompt declined (ERROR_CANCELLED - a choice, not a fault, and worded as
    /// one), the file quarantined by antivirus between verification and launch
    /// (FileNotFoundException, a window of a few hundred milliseconds), and policy or a broken
    /// shell association refusing to run it at all.
    /// </remarks>
    private InstallerLaunch StartInstaller()
    {
        try
        {
            using Process process = new();
            ProcessStartInfo startInfo = new()
            {
                FileName = DownloadLocation,
                Arguments = "/UPDATE",
                UseShellExecute = true
            };

            if (InstallType == InstallType.Silent) startInfo.Arguments += " /SILENT";
            else if (InstallType == InstallType.VerySilent) startInfo.Arguments += " /VERYSILENT";

            if (Helpers.IsDefaultInstallDir() && !Helpers.IsMemberOfAdministratorsGroup())
            {
                startInfo.Verb = "runas";
            }

            process.StartInfo = startInfo;
            process.Start();

            DebugHelper.WriteLine($"Update: installer started - \"{DownloadLocation}\" {startInfo.Arguments}");
            return new InstallerLaunch(true, string.Empty);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            DebugHelper.WriteLine("Update: the elevation prompt for the installer was declined; nothing was installed.");
            return new InstallerLaunch(false, Localization.Strings.DownloaderWindow_InstallCancelled);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Update: the installer could not be started.");
            return new InstallerLaunch(false,
                Helpers.SafeStringFormat(Localization.Strings.DownloaderWindow_InstallFailed, exception.Message));
        }
    }
}
