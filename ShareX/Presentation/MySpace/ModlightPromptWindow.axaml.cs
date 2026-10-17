#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.Localization;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// One line of text, asked for properly.
/// </summary>
/// <remarks>
/// Naming a folder, renaming one and choosing a custom address are the same gesture with
/// three different sentences around it, so they are one window. The alternative in a
/// WinForms-descended codebase is <c>InputBox</c>, which this fork does not have and
/// should not grow: it cannot carry the explanation, and every one of these three needs
/// one - a rename moves the folder's public gallery address, and a custom address has
/// rules about what characters are allowed.
///
/// The window validates only what it can know on its own, which is emptiness. Everything
/// else - a name already taken, a tree too deep, a reserved word - is the server's to
/// judge, and its sentence is what the caller shows. A client that second-guessed those
/// rules would drift from them silently.
/// </remarks>
public partial class ModlightPromptWindow : Window
{
    public ModlightPromptWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
    }

    /// <summary>The text that was accepted, or null when the window was dismissed.</summary>
    public string? Result { get; private set; }

    /// <summary>
    /// Ask, and answer with what was typed or null.
    /// </summary>
    /// <param name="prefix">
    /// Shown to the left of the box, unselectable. For a custom address that is the part
    /// of the URL the person does not get to choose, which is worth showing precisely
    /// because it makes the length of what they are choosing obvious.
    /// </param>
    public static async Task<string?> AskAsync(Window owner, string heading, string description,
        string fieldLabel, string confirmText, string initialValue = "", string prefix = "", int maxLength = 120)
    {
        ModlightPromptWindow window = new()
        {
            Title = heading
        };

        window.Heading.Text = heading;
        window.Description.Text = description;
        window.Description.IsVisible = description.Length > 0;
        window.FieldLabel.Text = fieldLabel;
        window.ConfirmButton.Content = confirmText;
        window.Field.Text = initialValue;
        window.Field.MaxLength = maxLength;

        if (prefix.Length > 0)
        {
            window.Prefix.Text = prefix;
            window.Prefix.IsVisible = true;
        }

        // After the window is up, not before: focusing a control that has not been
        // attached to a visual tree does nothing, and the caret would land nowhere.
        window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            window.Field.Focus();
            window.Field.SelectAll();
        });

        await window.ShowDialog(owner);
        return window.Result;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Confirm();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void Confirm()
    {
        string value = (Field.Text ?? "").Trim();

        if (value.Length == 0)
        {
            // Said in the window rather than by disabling the button: a button that is
            // simply dead does not explain itself, and this is the one refusal the client
            // can make without guessing at the server's rules.
            Problem.Text = Strings.MySpace_NameRequired;
            Problem.IsVisible = true;
            Field.Focus();
            return;
        }

        Result = value;
        Close();
    }
}
