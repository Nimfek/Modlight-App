#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Threading;
using ShareX.HelpersLib;
using System;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The way in to <see cref="SignInWindow"/> from anywhere that is not already on the
/// Avalonia thread - the WinForms host, a menu item, the startup gate.
/// </summary>
public static class SignInWindowIntegration
{
    private static SignInWindow? window;

    /// <summary>
    /// Show the window and wait for it to close.
    /// </summary>
    /// <returns>
    /// Whether an account is signed in by then - which is the question a caller
    /// gating startup on sign-in is actually asking. It is true when the person had
    /// already signed in earlier and just closed the window again.
    /// </returns>
    public static Task<bool> ShowAsync()
    {
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => ShowCore(completion));
        return completion.Task;
    }

    /// <summary>Bring the window up without waiting for it. For menu items.</summary>
    public static void Show() => Dispatcher.UIThread.Post(() => ShowCore(null));

    private static void ShowCore(TaskCompletionSource<bool>? completion)
    {
        try
        {
            if (window != null)
            {
                // A second request while the window is up means "let me see it", not
                // "open another one" - two sign-in windows would race for the same
                // config entry and only one of them could win.
                if (!window.IsVisible)
                {
                    window.Show();
                }

                window.Activate();

                if (completion != null)
                {
                    SignInWindow existing = window;
                    existing.Closed += (_, _) => completion.TrySetResult(existing.IsSignedIn);
                }

                return;
            }

            SignInWindow created = new();
            window = created;

            created.Closed += (_, _) =>
            {
                window = null;

                // Signing in or out here changes what the account entry in the menus
                // says, and a menu that still reads "Sign in" after signing in is the
                // app disagreeing with itself. Rebuilt on close rather than on every
                // change inside the window, because closing is when the answer settles.
                MainWindowIntegration.RefreshMenus();

                completion?.TrySetResult(created.IsSignedIn);
            };

            created.Show();
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            completion?.TrySetResult(ModlightSignIn.Current != null);
        }
    }
}
