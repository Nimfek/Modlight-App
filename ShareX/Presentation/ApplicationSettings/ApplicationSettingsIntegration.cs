#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

namespace ShareX;

/// <summary>
/// The old "Application settings" door, now a deep link into the one settings window.
/// </summary>
/// <remarks>
/// Kept as a class rather than deleted so its callers - a menu entry, a tray entry - do
/// not have to know the three dialogs became one window. What changed is where it lands:
/// the application group of the section list rather than a window of its own.
/// </remarks>
public static class ApplicationSettingsIntegration
{
    public static void Show() => SettingsWindowIntegration.Show(SettingsSections.ApplicationSectionId);
}
