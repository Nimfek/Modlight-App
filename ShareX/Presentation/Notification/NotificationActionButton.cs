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

using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using System.Collections.Generic;
using System.Linq;

namespace ShareX;

/// <summary>
/// Describes an action shown in the notification's action row. The order of
/// items in <see cref="NotificationWindowConfig.ActionButtons"/> is the display order.
/// </summary>
public sealed class NotificationActionButton
{
    public ToastClickAction Action { get; set; }
    public string? Label { get; set; }
    public string? Icon { get; set; }
    public bool DismissNotification { get; set; } = true;

    public NotificationActionButton()
    {
    }

    public NotificationActionButton(ToastClickAction action)
    {
        Action = action;
    }

    public NotificationActionButton(ToastClickAction action, string? label, string? icon = null)
    {
        Action = action;
        Label = label;
        Icon = icon;
    }

    public NotificationActionButton Clone() => new(Action, Label, Icon)
    {
        DismissNotification = DismissNotification
    };

    /// <summary>
    /// What a completion notification offers: copy, and copy the link.
    /// </summary>
    /// <remarks>
    /// Two entries, and which of them survives is decided per notification by
    /// <c>NotificationWindow.CanExecute</c> from the content rather than from a
    /// second list here - an upload with an image behind it gets both, anything
    /// else gets the link on its own, because there is no picture to copy.
    ///
    /// ShareX's four - copy image, annotate, pin to screen, upload - are gone at
    /// the owner's instruction. Anyone who wants one back can still add it in the
    /// notification button editor; this is only where a config starts.
    ///
    /// "Open link" is deliberately absent. Clicking the notification body already
    /// opens it, so a button for it would be a second control for one action.
    /// </remarks>
    public static List<NotificationActionButton> CreateDefaultButtons() =>
    [
        new(ToastClickAction.CopyImageToClipboard),
        new(ToastClickAction.CopyUrl)
    ];

    /// <summary>
    /// True when <paramref name="buttons"/> is still the set inherited from
    /// ShareX, which is the only set a migration may replace without asking.
    /// </summary>
    /// <remarks>
    /// A changed default never reaches a config that already has the old value
    /// saved, so <c>SettingManager</c> moves it over once. It checks against this
    /// exact list first: a set the user assembled themselves is theirs, and being
    /// silently reset on the launch after an update is how people stop trusting
    /// a settings screen.
    /// </remarks>
    public static bool IsInheritedShareXSet(IEnumerable<NotificationActionButton>? buttons)
    {
        ToastClickAction[] inherited =
        [
            ToastClickAction.CopyImageToClipboard,
            ToastClickAction.AnnotateImage,
            ToastClickAction.PinToScreen,
            ToastClickAction.Upload
        ];

        return buttons != null && buttons.Where(button => button != null)
            .Select(button => button.Action).SequenceEqual(inherited);
    }

    public static List<NotificationActionButton> CloneButtons(IEnumerable<NotificationActionButton>? buttons) =>
        buttons?.Where(button => button != null).Select(button => button.Clone()).ToList() ?? [];

    /// <summary>
    /// The label shown on a button that did not bring one of its own.
    /// </summary>
    /// <remarks>
    /// Resolved here at display time and never written onto the definition: the
    /// button list is persisted, so a label stored in it would still be in the
    /// language it was created in after the user switches. The fallback is the
    /// enum's own localized description, which for this enum is currently the
    /// bare member name - one more reason the two buttons that ship by default
    /// name themselves properly.
    /// </remarks>
    public static string GetDefaultLabel(ToastClickAction action) => action switch
    {
        ToastClickAction.CopyImageToClipboard => Strings.MainWindow_Copy,
        ToastClickAction.CopyUrl => Strings.Download_ToastCopyLink,
        _ => action.GetLocalizedDescription()
    };

    public static string GetDefaultIcon(ToastClickAction action) => action switch
    {
        ToastClickAction.AnnotateImage => LucideIcons.pen_line,
        ToastClickAction.CopyImageToClipboard => LucideIcons.copy,
        ToastClickAction.CopyFile => LucideIcons.files,
        ToastClickAction.CopyFilePath => LucideIcons.clipboard,
        ToastClickAction.CopyUrl => LucideIcons.link,
        ToastClickAction.OpenFile => LucideIcons.external_link,
        ToastClickAction.OpenFolder => LucideIcons.folder_open,
        ToastClickAction.OpenUrl => LucideIcons.external_link,
        ToastClickAction.Upload => LucideIcons.upload,
        ToastClickAction.PinToScreen => LucideIcons.pin,
        ToastClickAction.DeleteFile => LucideIcons.trash_2,
        _ => LucideIcons.x
    };
}
