#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ShareX;

/// <summary>One labelled line under "About": what this file is.</summary>
/// <remarks>
/// A list of pairs rather than a control per fact, because which facts a file HAS
/// depends on the file - not every one has dimensions, and a row reading
/// "Dimensions: -" spends a line to say nothing. Same reasoning the panel's own
/// details drawer applies when it omits the dimensions block.
/// </remarks>
internal sealed class DetailsFact
{
    public DetailsFact(string label, string value, bool mono = false)
    {
        Label = label;
        Value = value;
        IsMono = mono;
    }

    public string Label { get; }

    public string Value { get; }

    /// <summary>Drawn in the fixed-width face. For a MIME type and a code, never for prose.</summary>
    public bool IsMono { get; }
}

/// <summary>
/// One shape of link, with the button that copies it.
/// </summary>
/// <remarks>
/// It reports back that it was copied and then stops, which is the whole reason this
/// is a view model with change notification rather than a string: the panel turns the
/// copy glyph into a tick for a moment and that acknowledgement is the only feedback a
/// click on a link row produces. Without it the row is a button that appears to do
/// nothing, and people click it again.
/// </remarks>
internal sealed class DetailsLink : INotifyPropertyChanged
{
    /// <summary>How long the tick stays. Long enough to be seen, short enough not to be state.</summary>
    private static readonly TimeSpan CopiedFor = TimeSpan.FromMilliseconds(1400);

    private bool isCopied;

    public DetailsLink(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }

    public string Value { get; }

    public bool IsCopied
    {
        get => isCopied;
        private set
        {
            if (isCopied == value)
            {
                return;
            }

            isCopied = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Icon));
        }
    }

    public string Icon => isCopied ? LucideIcons.check : LucideIcons.copy;

    /// <summary>Show the tick, and take it away again on its own.</summary>
    /// <remarks>
    /// The timer is not cancelled when the row is copied twice in a row: the second call
    /// leaves the tick showing and merely queues a second removal, which is harmless
    /// because removing an already-removed tick does nothing. Cancelling would cost a
    /// field per row for a case nobody can see.
    /// </remarks>
    public void NoteCopied()
    {
        IsCopied = true;
        DispatcherTimer.RunOnce(() => IsCopied = false, CopiedFor);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>One restriction standing between the link and whoever follows it.</summary>
/// <remarks>
/// Read-only here on purpose. The gates are edited in one place - the link settings
/// window - and a chip that could also be cleared by clicking it would be a second
/// route to the same state, with none of that window's warnings attached to it.
/// </remarks>
internal sealed class DetailsGate
{
    public DetailsGate(string icon, string text)
    {
        Icon = icon;
        Text = text;
    }

    public string Icon { get; }

    public string Text { get; }
}
