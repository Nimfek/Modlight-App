#nullable enable

using System;
using System.Linq;
using ShareX.HelpersLib;

namespace ShareX;

/// <summary>
/// One of the four big buttons on the Capture tab.
/// </summary>
/// <remarks>
/// The owner's complaint about ShareX was that "every option and every thing is
/// hidden behind something else". The capture menu is the clearest example: the
/// three things people actually do all day sat inside a dropdown, two of them
/// behind a further submenu.
///
/// So these are buttons, not menu entries. The tile also shows the REAL hotkey
/// for its job, read from the user's own configuration rather than written into
/// the layout - partly because a hard-coded shortcut becomes a lie the moment
/// anyone rebinds it, and partly because a button that quietly teaches its
/// keyboard shortcut is how someone stops needing the button.
/// </remarks>
internal sealed class CaptureTileViewModel
{
    public CaptureTileViewModel(string icon, string title, string subtitle, HotkeyType job, Action invoke)
    {
        Icon = icon;
        Title = title;
        Subtitle = subtitle;
        Invoke = invoke;
        Hotkey = HotkeyFor(job);
    }

    public string Icon { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public Action Invoke { get; }

    /// <summary>The configured shortcut, or empty when this job has none bound.</summary>
    public string Hotkey { get; }

    public bool HasHotkey => !string.IsNullOrEmpty(Hotkey);

    /// <summary>
    /// The shortcut currently bound to a job, as the user would type it.
    /// </summary>
    /// <remarks>
    /// Only REGISTERED hotkeys count. An entry can exist in the config and still
    /// have failed to register - Print Screen is routinely taken by the Snipping
    /// Tool, and this app logs exactly that at every startup. Printing a
    /// shortcut that does nothing when pressed would be worse than printing none.
    /// </remarks>
    private static string HotkeyFor(HotkeyType job)
    {
        try
        {
            HotkeySettings? match = Program.HotkeysConfig?.Hotkeys?
                .FirstOrDefault(x => x.TaskSettings?.Job == job
                    && x.HotkeyInfo != null
                    && x.HotkeyInfo.Status == HotkeyStatus.Registered
                    && x.HotkeyInfo.IsValidHotkey);

            return match?.HotkeyInfo.ToString() ?? string.Empty;
        }
        catch
        {
            // A malformed hotkey config must not take the whole tab down with it.
            return string.Empty;
        }
    }
}
