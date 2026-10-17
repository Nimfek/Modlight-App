#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Threading;
using System;

namespace ShareX;

/// <summary>
/// The old "Task settings" door, which is now two different things.
/// </summary>
/// <remarks>
/// The DEFAULTS - what happens to the next capture - are settings like any other, so they
/// are sections of the one settings window. ONE HOTKEY'S OVERRIDES are not: they belong to
/// that hotkey, they are opened from the shortcut list, and several may be open at once
/// while the settings window stands behind them. That is a separate window using the same
/// shell, with a section list holding only the pages a hotkey can override.
/// </remarks>
public static class TaskSettingsIntegration
{
    private static SettingsWindow? _overrides;

    /// <param name="closed">
    /// Ignored for the defaults: the settings window already runs the same two steps for
    /// itself when it closes - rebuild the menus, write the configuration - and running
    /// them twice would only write the same file twice.
    /// </param>
    public static void Show(TaskSettings settings, bool isDefault, Action? closed = null)
    {
        if (isDefault)
        {
            SettingsWindowIntegration.Show(SettingsSections.TaskSectionId);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_overrides != null)
            {
                _overrides.Activate();
                return;
            }

            settings.SetDefaultSettings();

            _overrides = new SettingsWindow(settings, closed);
            _overrides.Closed += (_, _) => _overrides = null;
            _overrides.Show();
        });
    }
}
