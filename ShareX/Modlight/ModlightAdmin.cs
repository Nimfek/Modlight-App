#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using ShareX.UploadersLib.FileUploaders;
using System;

namespace ShareX;

/// <summary>
/// Whether the signed-in account administers this server.
/// </summary>
/// <remarks>
/// <para>
/// One place, because the plan is to build the panel's administration into this app and
/// the alternative is the answer being worked out again in every window that needs it -
/// which is how one of them ends up asking a slightly different question.
/// </para>
/// <para>
/// ADVISORY, AND ONLY FOR THE UI. Nothing here is a permission. The server decides what an
/// account may do and refuses everything else; this exists so the app can avoid showing a
/// person a door that will not open for them. A build that got this wrong would show an
/// administration screen full of 403s, not an administration screen that works.
/// </para>
/// <para>
/// FAILS CLOSED, AND IS NOT PERSISTED. It starts false on every launch and only becomes
/// true when a live <c>GET /api/me</c> says <c>role: "admin"</c> for the account this
/// installation is signed in to. Caching it in the config file would be the trap this
/// project has already fallen into three times - a saved value outliving the fact it
/// described - and here the stale value is "this person is an administrator", held for
/// months after the role was taken away. The cost of not saving it is that admin UI
/// appears a moment after launch instead of instantly, which is the right way round.
/// </para>
/// <para>
/// The answer is tied to WHO it was about, so signing out or signing in as somebody else
/// makes it false again without anything having to remember to clear it.
/// </para>
/// </remarks>
public static class ModlightAdmin
{
    /// <summary>The exact value the server uses. Compared as a whole, never as a substring.</summary>
    private const string AdminRole = "admin";

    private static readonly object sync = new object();

    /// <summary>Which account the answer below is about, or empty for "nobody asked yet".</summary>
    private static string subject = "";

    private static bool answer;

    /// <summary>Raised when the answer changes, so a menu built earlier can rebuild.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Whether the account signed in RIGHT NOW is an administrator.
    /// </summary>
    /// <remarks>
    /// The identity is re-checked on every read rather than cleared on sign-out, because
    /// "clear it on the way out" is a line somebody deletes while tidying and nothing
    /// fails visibly when they do. Comparing what the answer was about with who is signed
    /// in cannot be got wrong by omission: if they do not match there is no answer.
    /// </remarks>
    public static bool IsAdmin
    {
        get
        {
            ModlightAccount? account = ModlightSignIn.Current;

            if (account == null)
            {
                return false;
            }

            string key = ModlightSettingsPlan.AccountKey(account.Host, account.Tag);

            lock (sync)
            {
                return answer && string.Equals(subject, key, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// Write down what <c>GET /api/me</c> said about this account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internal, and called from exactly one place - <see cref="ModlightApi.GetAccountAsync"/>,
    /// which every caller of that endpoint goes through. There is no public setter, so
    /// there is no way for a window to talk itself into administrator mode; the only thing
    /// that can produce a true here is a server that said so.
    /// </para>
    /// <para>
    /// The comparison is exact equality with the string the panel itself tests for
    /// (<c>role === "admin"</c>, in lib/media/serve.ts and its neighbours). Being cleverer
    /// than the server - splitting a comma-separated list, matching a prefix - would mean
    /// showing administration UI to accounts the server will refuse. Being stricter than
    /// the server is safe; being looser is not.
    /// </para>
    /// </remarks>
    internal static void Record(string host, ModlightAccountInfo info)
    {
        Set(ModlightSettingsPlan.AccountKey(host, info.Tag), string.Equals(info.Role, AdminRole, StringComparison.Ordinal));
    }

    /// <summary>
    /// Forget the answer: the token was refused, or the account was signed out of.
    /// </summary>
    public static void Forget()
    {
        Set("", false);
    }

    private static void Set(string key, bool isAdmin)
    {
        bool raise;

        lock (sync)
        {
            raise = answer != isAdmin || !string.Equals(subject, key, StringComparison.Ordinal);
            subject = key;
            answer = isAdmin;
        }

        if (!raise)
        {
            return;
        }

        try
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            // A handler that throws must not take down the HTTP call that was only
            // reporting a fact on its way past.
            DebugHelper.WriteException(exception);
        }
    }
}
