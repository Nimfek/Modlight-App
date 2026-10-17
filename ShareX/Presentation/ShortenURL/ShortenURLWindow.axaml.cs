#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.Localization;
using ShareX.UploadersLib;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// What the person asked for in the Shorten URL dialog: the address, and how the link
/// should behave.
/// </summary>
/// <remarks>
/// The dialog used to hand back a bare string, which was the whole of item I: the panel
/// can name a link, expire it and put a password on it, and the app could do none of the
/// three because there was nowhere to put the answer.
/// </remarks>
public sealed class ShortenURLRequest
{
    public ShortenURLRequest(string url, ModlightShortLinkOptions options)
    {
        URL = url;
        Options = options;
    }

    public string URL { get; }

    public ModlightShortLinkOptions Options { get; }
}

public partial class ShortenURLWindow : Window
{
    private ShortenURLRequest? _submitted;
    private readonly bool _selectInitialURL;

    /// <summary>One entry in the expiry drop-down.</summary>
    /// <remarks>
    /// The same ladder the panel offers, and for the same reason: an expiry is a decision
    /// someone makes in one second or not at all, and a free-form number of days turns it
    /// into arithmetic. The server takes any number it is given (clamped at ten years),
    /// so this list is a convenience and not a rule.
    /// </remarks>
    private sealed class ExpiryChoice
    {
        public ExpiryChoice(int? days, string label)
        {
            Days = days;
            Label = label;
        }

        public int? Days { get; }

        public string Label { get; }

        public override string ToString() => Label;
    }

    public ShortenURLWindow() : this(null)
    {
    }

    public ShortenURLWindow(string? initialURL)
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();

        List<ExpiryChoice> expiries =
        [
            new ExpiryChoice(null, Strings.ShortenURLWindow_ExpiryNever),
            new ExpiryChoice(1, Strings.ShortenURLWindow_Expiry1Day),
            new ExpiryChoice(7, Strings.ShortenURLWindow_Expiry7Days),
            new ExpiryChoice(30, Strings.ShortenURLWindow_Expiry30Days),
            new ExpiryChoice(90, Strings.ShortenURLWindow_Expiry90Days),
            new ExpiryChoice(365, Strings.ShortenURLWindow_Expiry1Year)
        ];

        ExpiryComboBox.ItemsSource = expiries;
        ExpiryComboBox.SelectedIndex = 0;

        if (!string.IsNullOrEmpty(initialURL))
        {
            URLTextBox.Text = initialURL;
            _selectInitialURL = true;
        }

        UpdateCanSubmit();
        Opened += OnOpened;
    }

    public static Task<ShortenURLRequest?> ShowAsync(string? initialURL = null)
    {
        TaskCompletionSource<ShortenURLRequest?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                ShortenURLWindow window = new(initialURL);
                window.Closed += (_, _) => completion.TrySetResult(window._submitted);
                window.Show();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });

        return completion.Task;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Activate();
        URLTextBox.Focus();

        if (_selectInitialURL)
        {
            URLTextBox.SelectAll();
        }
    }

    private void OnURLChanged(object? sender, TextChangedEventArgs e) => UpdateCanSubmit();

    /// <summary>
    /// Enable the button when there is something to send. That is the whole test.
    /// </summary>
    /// <remarks>
    /// It used to also require a well-formed absolute http/https/ftp URL, which disagreed
    /// with the server in both directions: the server accepts a bare "example.com" and
    /// promotes it to https, and it refuses ftp outright. Re-stating its rules here is
    /// how the two drift, and the rule that matters - no javascript: or data: targets -
    /// is a SECURITY control that lives in the server's lib/shortlinks.ts, shared by the
    /// panel and the API so there is exactly one copy of it. A copy in C# would be a
    /// second copy to keep in step, and the day it fell behind it would be the hole.
    /// </remarks>
    private void UpdateCanSubmit() =>
        ShortenButton.IsEnabled = !string.IsNullOrWhiteSpace(URLTextBox.Text);

    private void OnShortenClick(object? sender, RoutedEventArgs e)
    {
        string url = URLTextBox.Text?.Trim() ?? string.Empty;

        if (url.Length == 0)
        {
            UpdateCanSubmit();
            return;
        }

        string customCode = CustomCodeTextBox.Text?.Trim() ?? string.Empty;

        // Not trimmed: a password is a literal, and silently dropping a space somebody
        // typed on purpose would produce a link whose password nobody can guess.
        string password = PasswordTextBox.Text ?? string.Empty;

        _submitted = new ShortenURLRequest(url, new ModlightShortLinkOptions
        {
            CustomCode = customCode.Length == 0 ? null : customCode,
            ExpiresInDays = (ExpiryComboBox.SelectedItem as ExpiryChoice)?.Days,
            Password = password.Length == 0 ? null : password,
            UseUserCode = UseTagCheckBox.IsChecked != false
        });

        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
