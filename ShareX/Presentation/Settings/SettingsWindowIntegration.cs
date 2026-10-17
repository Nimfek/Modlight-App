#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Threading;
using System;

namespace ShareX;

/// <summary>
/// The single door in front of the settings window.
/// </summary>
/// <remarks>
/// There used to be three - one per dialog - and every caller had to know which of them
/// held the thing it wanted to show. Callers now name a SECTION and get the same window
/// every time; the three old integration classes still exist so their callers keep
/// compiling, and each is now one line pointing here.
/// </remarks>
internal static class SettingsWindowIntegration
{
    private static SettingsWindow? _window;

    /// <summary>Open the settings window, on <paramref name="sectionId"/> if one is named.</summary>
    /// <param name="service">
    /// A hotkey service the caller already built. Used when this call is what creates the
    /// window; disposed otherwise, because the standing window has one of its own and two
    /// live services would both hold the hotkey manager's toggle trigger.
    /// </param>
    public static void Show(string? sectionId = null, IHotkeySettingsService? service = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_window != null)
            {
                service?.Dispose();

                if (!_window.IsVisible)
                {
                    _window.Show();
                }

                _window.Activate();

                if (!string.IsNullOrEmpty(sectionId))
                {
                    _window.ShowSection(sectionId!);
                }

                return;
            }

            _window = new SettingsWindow(service ?? CreateHotkeyService(), OnClosed);
            _window.Closed += (_, _) => _window = null;

            if (!string.IsNullOrEmpty(sectionId))
            {
                _window.ShowSection(sectionId!);
            }

            _window.Show();
        });
    }

    /// <summary>
    /// The shortcut list needs a service, and the window is now reachable from doors that
    /// were never about shortcuts, so it builds one itself rather than being handed one.
    /// </summary>
    /// <remarks>
    /// Null when there is no hotkey manager - a run with hotkeys switched off. The section
    /// is then left out of the list rather than listed and dead.
    /// </remarks>
    private static IHotkeySettingsService? CreateHotkeyService()
    {
        if (Program.HotkeyManager == null || Program.MainForm == null)
        {
            return null;
        }

        return new HotkeySettingsAvaloniaService(Program.HotkeyManager, Program.MainForm, () =>
        {
            if (Program.MainForm is { IsDisposed: false })
            {
                MainWindowIntegration.RefreshMenus();
                SettingManager.SaveHotkeysConfigAsync();
            }
        });
    }

    /// <summary>
    /// What the task settings dialog used to do on close, now done once for the window.
    /// </summary>
    /// <remarks>
    /// The menus are rebuilt because the after-capture and after-upload defaults are drawn
    /// into them, and the configuration is written because nothing in these pages writes
    /// as it goes - they edit the live objects and rely on somebody saving afterwards.
    /// </remarks>
    private static void OnClosed()
    {
        if (Program.MainForm is { IsDisposed: false })
        {
            MainWindowIntegration.RefreshMenus();
            SettingManager.SaveApplicationConfigAsync();
        }
    }
}
