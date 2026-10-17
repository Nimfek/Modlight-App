#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using ShareX.UploadersLib;

namespace ShareX;

/// <summary>
/// The one place that decides whether a task on its way to Modlight has an account
/// to go to, and what to do about it when it has not.
/// </summary>
/// <remarks>
/// Sign-in is demanded here rather than at startup on purpose. The app registers
/// itself to run with Windows using -silent and comes up in the tray, so a window
/// that jumped in front of the user on every boot would be asking a question nobody
/// had posed yet. A task that is about to upload to Modlight is the moment the
/// question becomes real, and it is the only moment where the answer changes what
/// happens next.
///
/// Only tasks that actually target Modlight are gated. Somebody who has pointed
/// screenshots at Imgur needs nothing from an account here, and demanding one would
/// be a toll rather than a requirement.
/// </remarks>
internal static class ModlightUploadGate
{
    /// <summary>Whether anything about this task needs a Modlight account.</summary>
    public static bool TargetsModlight(TaskInfo? info)
    {
        if (info?.TaskSettings == null || !info.IsUploadJob)
        {
            return false;
        }

        return UsesUploader(info) || UsesShortener(info);
    }

    /// <summary>
    /// Whether the FILE itself is going to Modlight.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="TargetsModlight"/> on purpose, and the difference matters
    /// to anything that wants to re-send the bytes later. A task that uploads to Imgur and
    /// then shortens the result through Modlight targets Modlight, but its file does not -
    /// so the offline queue asks this one. Reading the wider question would have it
    /// quietly re-send that file to the wrong service.
    /// </remarks>
    public static bool UploadsToModlight(TaskInfo? info)
    {
        if (info?.TaskSettings == null || !info.IsUploadJob)
        {
            return false;
        }

        return UsesUploader(info);
    }

    /// <summary>Targets Modlight and there is no account yet - the case everything below exists for.</summary>
    /// <remarks>
    /// The uploaders config loads on its own thread at startup, so a task that begins
    /// during those first moments would otherwise read "no account" from a file that
    /// simply is not there yet, and treat a signed-in user as signed out. Waiting is
    /// only done for tasks that turn out to need the answer.
    /// </remarks>
    public static bool IsSignedOut(TaskInfo? info)
    {
        if (!TargetsModlight(info))
        {
            return false;
        }

        SettingManager.WaitUploadersConfig();

        return ModlightSignIn.Current == null;
    }

    /// <summary>
    /// Ask for sign-in, and answer whether the upload may go ahead.
    /// </summary>
    /// <remarks>
    /// Blocks the worker thread on the window, exactly like
    /// <see cref="BeforeUploadWindowIntegration"/> does one line further down in
    /// <c>DoUploadJob</c>. That is deliberate: captures taken while the window is up
    /// wait with it and all upload together once it is answered, instead of each one
    /// having to be re-taken. Closing the window without signing in ends the wait.
    /// </remarks>
    public static bool Allow(TaskInfo? info)
    {
        if (!IsSignedOut(info))
        {
            return true;
        }

        DebugHelper.WriteLine("Modlight: this upload needs an account. Asking to sign in.");

        bool signedIn = SignInWindowIntegration.ShowAsync().GetAwaiter().GetResult();

        if (!signedIn)
        {
            DebugHelper.WriteLine("Modlight: still signed out, so the upload is skipped and the file is kept locally.");
        }

        return signedIn;
    }

    /// <summary>
    /// Make sure this task leaves something behind on disk even if the upload never happens.
    /// </summary>
    /// <remarks>
    /// Called before the after-capture work runs, because that is the only point at
    /// which the decision to write the file is still open. A capture exists nowhere
    /// else in the world; losing it because nobody had signed in yet would be the
    /// worst possible answer to a question the app itself chose to ask.
    ///
    /// Text and file tasks are left alone on the save: their content already exists
    /// somewhere the user put it. They do get DeleteFile stripped, though - that flag
    /// means "delete it once it is safely uploaded", and an upload that may not happen
    /// cannot authorise a deletion.
    ///
    /// The cost is one screenshot on disk that a user who has turned local saving off
    /// did not ask for, in the window between installing the app and signing in. A
    /// stray file is recoverable; a lost capture is not.
    /// </remarks>
    public static void KeepLocally(TaskInfo info)
    {
        TaskSettings settings = info.TaskSettings;

        settings.AfterCaptureJob = settings.AfterCaptureJob.Remove(AfterCaptureTasks.DeleteFile);

        if (info.Job == TaskJob.Job)
        {
            settings.AfterCaptureJob = settings.AfterCaptureJob.Add(AfterCaptureTasks.SaveImageToFile);
        }
    }

    private static bool UsesUploader(TaskInfo info)
    {
        TaskSettings settings = info.TaskSettings;

        switch (info.UploadDestination)
        {
            case EDataType.Image:
                return settings.ImageDestination == ImageDestination.Modlight;
            case EDataType.Text:
                return settings.TextDestination == TextDestination.Modlight;
            case EDataType.File:
                return settings.GetFileDestinationByDataType(info.DataType) == FileDestination.Modlight;
            default:
                return false;
        }
    }

    /// <summary>
    /// The shortener counts too, whether it is the whole job or an after-upload step.
    /// </summary>
    /// <remarks>
    /// POST /api/shortlinks needs the same token as the upload does, so a task that
    /// uploads to Imgur and then shortens through Modlight still needs an account -
    /// and finding that out after the upload has already happened would leave the user
    /// with a completed task and an error on it.
    /// </remarks>
    private static bool UsesShortener(TaskInfo info)
    {
        if (info.Job == TaskJob.ShareURL || info.TaskSettings.URLShortenerDestination != UrlShortenerType.Modlight)
        {
            return false;
        }

        return info.Job == TaskJob.ShortenURL || info.TaskSettings.AfterUploadJob.HasFlag(AfterUploadTasks.UseURLShortener);
    }
}
