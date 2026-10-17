#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using Avalonia.Interactivity;
using ShareX.AvaloniaUI.Theming;
using ShareX.Localization;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The three gates that can stand in front of a public link.
/// </summary>
/// <remarks>
/// The same three the web panel offers, in the same order and with the same words:
/// expiry, a view budget, and a password. Where this differs from the panel is that it
/// collects all three and sends ONE request on Save, while the panel applies each
/// section the moment its own button is pressed. That is not an oversight - a modal
/// window with a Save button is what the platform means by a settings dialog, and
/// Escape has to be able to leave without having changed anything. Three separate
/// applies would make Cancel a lie.
///
/// Every field distinguishes "leave alone" from "clear", because the server does: an
/// absent key means untouched and an explicit null means remove. That is why the state
/// here is three <c>bool</c> flags beside three nullable values rather than three
/// nullables on their own - C# spells both of those <c>null</c>, and collapsing them
/// would wipe an expiry every time somebody only wanted to set a password.
///
/// The current password is never read back, because the server does not store one that
/// could be: the box always starts empty, and leaving it empty leaves the password as it
/// was. Removing one is a button of its own.
/// </remarks>
public partial class ModlightLinkSettingsWindow : Window
{
    private bool hadPassword;

    private bool wantsExpiry;
    private DateTimeOffset? expiry;

    private bool wantsViewLimit;
    private int? viewLimit;

    private bool wantsPassword;
    private string? password;

    /// <summary>Set while the code is filling controls, so their events do not read as edits.</summary>
    private bool loading;

    public ModlightLinkSettingsWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
    }

    private bool Saved { get; set; }

    /// <summary>
    /// Ask for the gates, and answer with the change to make - or null for "nothing".
    /// </summary>
    /// <remarks>
    /// An edit with nothing in it is returned as null rather than as an empty one: the
    /// server refuses a body that names ids and changes nothing, correctly, because
    /// answering 200 to it would tell a client its edit landed.
    /// </remarks>
    internal static async Task<ModlightUploadEdit?> AskAsync(Window owner, CloudFileViewModel file)
    {
        ModlightLinkSettingsWindow window = new()
        {
            Title = Strings.MySpace_LinkSettings
        };

        window.loading = true;
        window.FileName.Text = file.Name;
        window.hadPassword = file.HasPassword;
        window.expiry = file.Row.ExpiresAt;
        window.viewLimit = file.Row.MaxViews;

        if (file.Row.MaxViews is int current)
        {
            window.ViewLimit.Value = current;
        }

        window.RemovePasswordButton.IsVisible = file.HasPassword;
        window.loading = false;

        window.ShowExpiry();
        window.ShowViewLimit();
        window.ShowPassword();

        await window.ShowDialog(owner);

        if (!window.Saved)
        {
            return null;
        }

        ModlightUploadEdit edit = new();

        if (window.wantsExpiry)
        {
            edit.SetExpiry(window.expiry);
        }

        if (window.wantsViewLimit)
        {
            edit.SetMaxViews(window.viewLimit);
        }

        if (window.wantsPassword)
        {
            edit.SetPassword(window.password);
        }

        return edit.IsEmpty ? null : edit;
    }

    /* -- expiry ------------------------------------------------------------- */

    private void OnExpiryPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours))
        {
            return;
        }

        wantsExpiry = true;

        // Zero hours is the "Never" chip, and it is the only way to CLEAR an expiry. It
        // has to be an explicit null on the wire rather than an absent key, or the server
        // would leave the old date in place.
        expiry = hours == 0 ? null : DateTimeOffset.Now.AddHours(hours);

        ShowExpiry();
    }

    private void ShowExpiry() =>
        ExpiryState.Text = expiry == null
            ? Strings.MySpace_LinkNeverExpires
            : string.Format(Strings.MySpace_LinkExpiresOn,
                expiry.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));

    /* -- view limit --------------------------------------------------------- */

    private void OnViewLimitChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loading)
        {
            return;
        }

        wantsViewLimit = true;
        viewLimit = e.NewValue == null ? null : Math.Max(1, (int)e.NewValue.Value);
        ShowViewLimit();
    }

    private void OnOneTimeClick(object? sender, RoutedEventArgs e) => SetViewLimit(1);

    private void OnNoLimitClick(object? sender, RoutedEventArgs e) => SetViewLimit(null);

    private void SetViewLimit(int? value)
    {
        loading = true;
        ViewLimit.Value = value;
        loading = false;

        wantsViewLimit = true;
        viewLimit = value;
        ShowViewLimit();
    }

    private void ShowViewLimit() =>
        ViewLimitState.Text = viewLimit == null
            ? Strings.MySpace_LinkNoViewLimit
            : string.Format(Strings.MySpace_LinkLimitedToViews, viewLimit.Value);

    /* -- password ----------------------------------------------------------- */

    private void OnPasswordChanged(object? sender, TextChangedEventArgs e)
    {
        if (loading)
        {
            return;
        }

        string typed = Password.Text ?? "";

        // An empty box is "leave the password as it is", not "remove it". Removing is a
        // button, because clearing a field is far too easy to do by accident on a control
        // that never showed what was in it.
        wantsPassword = typed.Length > 0;
        password = typed.Length > 0 ? typed : null;

        ShowPassword();
    }

    private void OnRemovePasswordClick(object? sender, RoutedEventArgs e)
    {
        loading = true;
        Password.Text = "";
        loading = false;

        wantsPassword = true;
        password = null;
        hadPassword = false;
        RemovePasswordButton.IsVisible = false;

        ShowPassword();
    }

    private void ShowPassword()
    {
        if (wantsPassword && password == null)
        {
            PasswordState.Text = Strings.MySpace_LinkPasswordWillBeRemoved;
            return;
        }

        if (wantsPassword)
        {
            PasswordState.Text = Strings.MySpace_LinkPasswordWillBeSet;
            return;
        }

        PasswordState.Text = hadPassword ? Strings.MySpace_LinkPasswordOn : Strings.MySpace_LinkPasswordOff;
    }

    /* -- footer ------------------------------------------------------------- */

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        Saved = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
