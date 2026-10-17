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

namespace ShareX;

/// <summary>
/// The old "Hotkey settings" door, now a deep link into the one settings window.
/// </summary>
/// <remarks>
/// The service is handed on rather than dropped: the caller built it around the running
/// hotkey manager, and building a second one would leave two objects holding the same
/// toggle trigger. When a settings window is already open it has a service of its own, and
/// the one passed here is disposed instead.
/// </remarks>
public static class HotkeySettingsIntegration
{
    public static void Show(IHotkeySettingsService service) =>
        SettingsWindowIntegration.Show(SettingsSections.HotkeysSectionId, service);
}
