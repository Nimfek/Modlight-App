#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ShareX;

/// <summary>
/// Where the YouTube sign-in lives on this PC, and what "signed in" means.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the file is.</b> A Netscape cookie jar holding the session of a real Google
/// account. Anyone who reads it is signed in as the owner until the account signs out
/// everywhere. It is a password in every way that matters, and the rules that follow are
/// not caution, they are the minimum for keeping one.
/// </para>
/// <para>
/// It lives under the app's personal folder and nowhere else. It is never written into
/// <c>ApplicationConfig.json</c>, never named in a setting, never attached to a task,
/// never uploaded, and its contents are never logged - the debug log has an upload button
/// on it, so a single line of it in there would be a credential in a paste bin. What this
/// file logs is how many cookies were captured and whether the jar exists, both of which
/// are facts about the file rather than parts of it. The backup exporter cannot reach it
/// either: <c>SettingManager.Export</c> zips three named config files and the history, not
/// the folder.
/// </para>
/// <para>
/// <b>Why a jar and not the user's browser.</b> yt-dlp can read Chrome's cookie database
/// directly, and that would be both fragile and rude: the database is locked while the
/// browser is running, the encryption key moves between Chrome versions, and it hands this
/// app every cookie the person has for every site they use. A window they deliberately
/// signed into gives exactly one account, in a profile of ours, that they can throw away
/// with one button.
/// </para>
/// <para>
/// <b>The wipe rule.</b> Signing out deletes the jar first and the browser profile second,
/// because the profile can be held open by a browser process that has not finished exiting.
/// If that delete loses the race the jar is still gone, and the invariant below closes the
/// gap: a sign-in that finds NO jar wipes the profile before opening it. So "sign out"
/// always ends with a signed-out browser, even when the folder could not be removed at the
/// time it was asked for.
/// </para>
/// </remarks>
internal static class YouTubeCookies
{
    /// <summary>
    /// Where Google's sign-in starts.
    /// </summary>
    /// <remarks>
    /// Carried over from the tool this replaces, where it was arrived at by trial:
    /// <c>service=youtube</c> and a <c>continue</c> pointing at YouTube are what make the
    /// flow end on youtube.com rather than on a Google account page, which is what the
    /// completion check below watches for. <c>uilel=3</c> asks for the compact sign-in
    /// chrome, <c>passive=true</c> stops it from bouncing an already-valid session.
    /// </remarks>
    private const string LoginUrlFormat =
        "https://accounts.google.com/ServiceLogin" +
        "?service=youtube&hl={0}&uilel=3&passive=true" +
        "&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin";

    /// <summary>Where the WebView2 runtime is downloaded from, for the PC that has none.</summary>
    public const string RuntimePage = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>
    /// Any one of these in the jar means the browser is signed in to Google.
    /// </summary>
    /// <remarks>
    /// Carried over verbatim, and it is the completion signal rather than a guess about
    /// what the page looks like. Reading the DOM to decide whether a sign-in worked is a
    /// check that breaks the next time Google redesigns; a cookie named
    /// <c>__Secure-1PSID</c> either exists or does not.
    /// </remarks>
    private static readonly HashSet<string> LoginCookies = new(StringComparer.Ordinal)
    {
        "__Secure-1PSID",
        "__Secure-3PSID",
        "SID",
        "LOGIN_INFO"
    };

    /// <summary>Everything to do with this sign-in, in one folder, so signing out is one delete.</summary>
    private static string Folder => Path.Combine(PersonalFolder(), "YouTubeSignIn");

    /// <summary>The jar yt-dlp is pointed at.</summary>
    public static string FilePath => Path.Combine(Folder, "cookies.txt");

    /// <summary>
    /// The browser profile, ours alone.
    /// </summary>
    /// <remarks>
    /// A user data folder of our own rather than the shared default, for two reasons that
    /// point the same way: the session has to survive a restart, so it cannot be a private
    /// window, and it must not touch the profile the person browses with, so it cannot be
    /// theirs. What is in here is one Google sign-in and the pages that sign-in visited.
    /// </remarks>
    public static string ProfileFolder => Path.Combine(Folder, "Browser");

    /// <summary>
    /// Whether the jar on disk holds a session yt-dlp can actually be signed in with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the file every time it changes, and never from what happened the last time
    /// somebody pressed a button. This used to be "the file exists and is not empty", which
    /// is a weaker fact than it looks: the jar's two header lines are already a non-empty
    /// file, and yt-dlp REWRITES the jar when a run ends - so a session Google stopped
    /// accepting leaves a valid file behind holding nothing but the cookies YouTube hands
    /// out to strangers. The dot on the tab would have stayed green through every refusal.
    /// </para>
    /// <para>
    /// Expiry is deliberately NOT part of the answer. yt-dlp loads a jar with
    /// <c>ignore_expires</c>, so it sends a cookie whose date has passed regardless; a
    /// check that called that "signed out" would be the same lie told the other way round,
    /// on a session that is still working.
    /// </para>
    /// <para>
    /// The result is cached against the file's length and write time, because bindings ask
    /// far more often than a jar changes. The cache cannot go stale without the file having
    /// been written, and a write is exactly what makes the answer different.
    /// </para>
    /// </remarks>
    public static bool IsSignedIn
    {
        get
        {
            try
            {
                FileInfo file = new(FilePath);

                if (!file.Exists || file.Length == 0)
                {
                    return false;
                }

                long length = file.Length;
                long stamp = file.LastWriteTimeUtc.Ticks;

                lock (stateLock)
                {
                    if (length == stateLength && stamp == stateStamp)
                    {
                        return stateSignedIn;
                    }
                }

                bool signedIn = IsSignedInJar(NetscapeCookieFile.ReadNames(FilePath));

                lock (stateLock)
                {
                    stateLength = length;
                    stateStamp = stamp;
                    stateSignedIn = signedIn;
                }

                return signedIn;
            }
            catch (Exception)
            {
                // Unreadable is not signed in. Something that cannot be handed to yt-dlp
                // cannot sign anybody in, whatever the reason it cannot be read.
                return false;
            }
        }
    }

    /// <summary>
    /// Guards the three fields below, which are read from the UI thread and written from
    /// whichever thread a download happened to finish on.
    /// </summary>
    private static readonly object stateLock = new();

    private static long stateLength = -1;
    private static long stateStamp = -1;
    private static bool stateSignedIn;

    /// <summary>Make the next read go to the disk, whatever the timestamps say.</summary>
    private static void ForgetState()
    {
        lock (stateLock)
        {
            stateLength = -1;
            stateStamp = -1;
            stateSignedIn = false;
        }
    }

    /// <summary>The address the sign-in window opens, in the language the app is running in.</summary>
    public static string LoginUrl => string.Format(CultureInfo.InvariantCulture, LoginUrlFormat, Language());

    /// <summary>
    /// The jar to pass to yt-dlp for this link, or null.
    /// </summary>
    /// <remarks>
    /// YouTube only, and that is on purpose. A cookie jar handed to a run is a jar the
    /// child process can read in full; a TikTok download has no use for a Google session
    /// and no business being given the file that holds one. yt-dlp would match by domain
    /// and send nothing, but "it would not have sent it anyway" is not the same promise as
    /// "it was never given it".
    /// </remarks>
    public static string? FileFor(DownloadLink link)
    {
        if (!link.IsYouTube)
        {
            return null;
        }

        return IsSignedIn ? FilePath : null;
    }

    /// <summary>Whether these cookie names amount to a completed sign-in.</summary>
    public static bool IsSignedInJar(IEnumerable<string> cookieNames)
    {
        foreach (string name in cookieNames)
        {
            if (LoginCookies.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Write the captured jar. Returns how many cookies it holds.
    /// </summary>
    /// <remarks>
    /// The count is logged and the contents are not, ever.
    /// </remarks>
    public static int Save(IEnumerable<NetscapeCookie> cookies)
    {
        // Before the write, so the file is born inside the tightened permissions rather
        // than being tightened a moment after it exists.
        Prepare();

        int written = NetscapeCookieFile.Write(FilePath, cookies);

        ProtectFile();
        ForgetState();

        DebugHelper.WriteLine("Download: captured " + written.ToString(CultureInfo.InvariantCulture) +
            " YouTube cookies.");

        return written;
    }

    /// <summary>
    /// Throw the sign-in away: the jar, then the browser profile.
    /// </summary>
    /// <remarks>
    /// In that order, and the order is the point. The jar is the credential, so it goes
    /// first and it goes synchronously; the profile is a folder a browser process may still
    /// be letting go of, so it is best effort. See the type remarks for what closes the gap
    /// when the second delete does not succeed.
    /// </remarks>
    public static void SignOut()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }

        WipeProfile();
        ForgetState();

        DebugHelper.WriteLine("Download: YouTube sign-in cleared.");
    }

    /// <summary>
    /// Delete the browser profile if it is not in use.
    /// </summary>
    /// <remarks>
    /// Called from sign-out, and again before a sign-in that starts with no jar - which is
    /// what makes a sign-out that lost the race to a still-closing browser process turn
    /// into a real one before anybody can sign in on top of it.
    ///
    /// That race is not hypothetical. Measured: a WebView2 host that closed its window and
    /// returned from its message loop still could not delete its own user data folder -
    /// <c>IOException</c>, the browser process had not finished letting go. Anything that
    /// treated this delete as reliable would leave a signed-in profile behind on the one
    /// occasion it matters.
    /// </remarks>
    public static void WipeProfile()
    {
        try
        {
            if (Directory.Exists(ProfileFolder))
            {
                Directory.Delete(ProfileFolder, recursive: true);
            }
        }
        catch (Exception exception)
        {
            // Held open by a browser process that has not finished exiting. The jar is
            // already gone, so nothing can be downloaded with this session; the next
            // sign-in tries again before it opens the profile.
            DebugHelper.WriteException(exception);
        }
    }

    /// <summary>
    /// Make the folder, and cut it out of its inherited permissions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called BEFORE the browser opens as well as before the jar is written, and the order
    /// matters: WebView2 creates its user data folder lazily, on first use rather than
    /// when the environment is made (measured - the folder is still absent immediately
    /// after <c>CreateAsync</c> returns). Left until save time, the whole browser profile,
    /// which holds the same session as the jar, would be created and filled under the
    /// folder's inherited permissions and only tightened afterwards.
    /// </para>
    /// <para>
    /// The FOLDER and not only the file, and that is not belt and braces. yt-dlp REPLACES
    /// the jar when a run ends - it saves the refreshed session back over
    /// <c>--cookies</c>, through a temporary file and a rename - so any permission set on
    /// the file itself is gone after the first download. What the replacement inherits is
    /// the folder's, so the folder is where the rule has to live.
    /// </para>
    /// <para>
    /// On an ordinary single-account PC this changes nothing, because the personal folder
    /// is already inside the user's own profile. It earns its keep on the machine with a
    /// second administrator, a roaming profile, or a backup agent running as SYSTEM, where
    /// "inside my Documents" is not by itself a boundary.
    /// </para>
    /// <para>
    /// Best effort throughout. A failure here is worth a line in the log and nothing more:
    /// the alternative is refusing to sign in over a permission the user may have no way
    /// to grant.
    /// </para>
    /// </remarks>
    public static void Prepare()
    {
        try
        {
            Directory.CreateDirectory(Folder);

            SecurityIdentifier? owner = WindowsIdentity.GetCurrent().User;

            if (owner == null)
            {
                return;
            }

            DirectorySecurity security = new();

            // Protected and NOT preserving what was inherited: copying the inherited rules
            // in and then adding ours would leave everyone who could already read the
            // personal folder able to read this, which is the thing being prevented.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(owner);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));

            new DirectoryInfo(Folder).SetAccessControl(security);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }

    /// <summary>The same rule on the jar itself, for the copy that exists right now.</summary>
    private static void ProtectFile()
    {
        try
        {
            SecurityIdentifier? owner = WindowsIdentity.GetCurrent().User;

            if (owner == null)
            {
                return;
            }

            FileSecurity security = new();

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(owner);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                AccessControlType.Allow));

            new FileInfo(FilePath).SetAccessControl(security);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }

    /// <summary>
    /// Two letters for Google's <c>hl</c>, or English.
    /// </summary>
    /// <remarks>
    /// Validated rather than trusted: this ends up in a URL, and a culture name is not
    /// something to concatenate into one unchecked.
    /// </remarks>
    private static string Language()
    {
        string code;

        try
        {
            code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        }
        catch (Exception)
        {
            return "en";
        }

        if (code.Length != 2)
        {
            return "en";
        }

        foreach (char c in code)
        {
            if (c is < 'a' or > 'z')
            {
                return "en";
            }
        }

        return code;
    }

    /// <summary>
    /// The personal folder, or somewhere inside this user's profile.
    /// </summary>
    /// <remarks>
    /// Same guard as <see cref="YtDlpTool"/> uses and for the same reason: this is read
    /// while the tab is being drawn, and a config file that will not load is not a reason
    /// to take the window down. Unlike a tool search, though, the answer cannot be allowed
    /// to be empty - <c>Path.Combine("", "YouTubeSignIn")</c> is a RELATIVE path, and a
    /// credential written relative to whatever the working directory happens to be is a
    /// credential in an unknown place. The fallback stays inside the user's profile.
    /// </remarks>
    private static string PersonalFolder()
    {
        string folder;

        try
        {
            folder = Program.PersonalFolder ?? "";
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            folder = "";
        }

        if (folder.Length > 0)
        {
            return folder;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Program.AppName);
    }
}
