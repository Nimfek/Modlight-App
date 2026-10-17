#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ShareX.HelpersLib;
using ShareX.Localization;

namespace ShareX;

/// <summary>
/// The notice on the Capture tab that appears when hotkeys did not register.
/// </summary>
/// <remarks>
/// A hotkey that fails to register is invisible: the key still does something,
/// it just does not do it here. So the notice has one job, which is to name the
/// program that took the key - and it has to name the right one, because the two
/// answers need opposite fixes. Windows 11 gives the Print Screen family to the
/// Snipping Tool, and you take it back in Windows' own keyboard settings. A
/// running ShareX takes whatever it was configured with, including shortcuts
/// that have nothing to do with Print Screen, and you take those back by closing
/// ShareX. Blaming the Snipping Tool for the second case sends the user to a
/// setting that will not help.
/// </remarks>
internal static class HotkeyProblemViewModel
{
    /// <summary>
    /// Upstream ShareX's single instance mutex, which is also its installer's
    /// AppMutex and its key in Add/Remove Programs.
    /// </summary>
    /// <remarks>
    /// Ours is deliberately a different GUID (see <see cref="Program.MutexName"/>),
    /// which is what makes this a test for THEIR app and only theirs.
    /// </remarks>
    private const string ShareXMutexName = "82E6AC09-0FEF-4390-AD9F-0DD3F5561EFC";

    /// <summary>
    /// What to tell the user about the hotkeys that could not be registered.
    /// Empty when there are none.
    /// </summary>
    public static string Describe(IReadOnlyList<HotkeySettings> failed)
    {
        if (failed == null || failed.Count == 0)
        {
            return string.Empty;
        }

        string keys = string.Join(", ", failed.Select(x => x.HotkeyInfo.ToString()));

        // ShareX first: it explains failures the Snipping Tool cannot, since it
        // holds whatever its own config asked for rather than one family of keys.
        return string.Format(IsShareXRunning()
            ? Strings.MainWindow_HotkeyProblemShareX
            : Strings.MainWindow_HotkeyProblem, keys);
    }

    /// <summary>Whether a ShareX instance is up right now.</summary>
    /// <remarks>
    /// The mutex rather than a process name: a portable ShareX can be called
    /// anything, and holding that mutex is exactly what "an instance is running,
    /// and has therefore already claimed its hotkeys" means.
    ///
    /// Read every time the notice is built rather than cached, because the whole
    /// point of the message is that the user can act on it - and the moment they
    /// do, the answer changes.
    /// </remarks>
    private static bool IsShareXRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(ShareXMutexName, out Mutex? mutex))
            {
                mutex?.Dispose();
                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex is there; this process merely may not open it with the
            // rights TryOpenExisting asks for. Existing is the entire question.
            return true;
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e);
        }

        return false;
    }
}
