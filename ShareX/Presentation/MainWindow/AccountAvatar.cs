#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The little round picture that stands for the account, wherever one is drawn.
/// </summary>
/// <remarks>
/// <para>
/// One of these per place that shows the account, rather than one shared instance, because
/// the two places are different sizes and Avalonia's <see cref="Bitmap"/> is a surface a
/// control points at - sharing it would mean whichever window closed first disposing the
/// picture the other one is still drawing. The bytes are shared instead, in
/// <see cref="cachedBytes"/>, which is the expensive part; decoding twice costs microseconds.
/// </para>
/// <para>
/// THREE STATES, and the order matters. A picture when the account has one. Otherwise the
/// INITIALS, spelled exactly the way the website spells them - the first two characters of
/// the name, or of the e-mail when there is no name - so that the same account is the same
/// two letters in the panel and here. And nothing at all only when there is nobody signed
/// in. The address of the server is never one of the states: it is not an identity, and it
/// was being used as one.
/// </para>
/// <para>
/// ANIMATION comes free. The server re-encodes every avatar, and an animated one comes back
/// as animated WebP - the same format, and the same decoder, as the moving previews the
/// library grid already plays. So a GIF uploaded in the panel does animate here, because
/// what arrives is no longer a GIF. <see cref="AnimatedThumbnail"/> returns null for a
/// still, which is the signal to keep the single frame and leave the timer stopped.
/// </para>
/// </remarks>
public sealed class AccountAvatar : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// The last picture fetched, kept by its URL so a second view is free.
    /// </summary>
    /// <remarks>
    /// One entry, not a dictionary: there is one account signed in at a time, and its URL
    /// changes only when the picture does - the token in the query string is derived from
    /// the stored key, so a new avatar is a new URL and the old entry is simply replaced.
    /// That is also what makes this safe to keep forever: it cannot go stale, only unused.
    ///
    /// The strip asks every twenty seconds. Without this, so would the network.
    /// </remarks>
    private static string cachedUrl = "";

    private static byte[]? cachedBytes;

    private static readonly object cacheGate = new();

    /// <summary>The URL this instance is showing, or trying to.</summary>
    private string url = "";

    private CancellationTokenSource? loading;

    private AnimatedThumbnail? reel;

    private DispatcherTimer? frameTimer;

    private int frame;

    private Bitmap? still;

    private Bitmap? image;

    private string initials = "";

    /// <summary>The picture, or null when there is none to draw.</summary>
    public Bitmap? Image
    {
        get => image;
        private set
        {
            if (SetField(ref image, value))
            {
                OnPropertyChanged(nameof(HasImage));
                OnPropertyChanged(nameof(HasInitials));
                OnPropertyChanged(nameof(HasGlyph));
            }
        }
    }

    /// <summary>Two letters standing in for a picture. Empty when nobody is signed in.</summary>
    public string Initials
    {
        get => initials;
        private set
        {
            if (SetField(ref initials, value))
            {
                OnPropertyChanged(nameof(HasInitials));

                // Both of them, or the glyph underneath stays on screen behind the
                // letters. Caught by looking at a screenshot rather than at the code: the
                // two marks are the same colour and the same size, so the pile reads as a
                // slightly wrong icon rather than as two controls.
                OnPropertyChanged(nameof(HasGlyph));
            }
        }
    }

    public bool HasImage => image != null;

    /// <summary>
    /// Draw the letters. False the moment a picture exists, so the two never stack.
    /// </summary>
    public bool HasInitials => image == null && initials.Length > 0;

    /// <summary>
    /// Neither a picture nor letters: fall back to the person-shaped glyph.
    /// </summary>
    /// <remarks>
    /// A real property rather than a negated binding, for the reason spelled out on
    /// AccountStripViewModel.IsSignedOut: Avalonia's <c>!</c> prefix is not checked at
    /// compile time, and a binding that silently fails here draws two marks on top of
    /// each other.
    /// </remarks>
    public bool HasGlyph => image == null && initials.Length == 0;

    /// <summary>
    /// Show this account: its picture if it has one, its letters if it has not.
    /// </summary>
    /// <remarks>
    /// UI thread only, like the strip that calls it. Called on every refresh - four times
    /// a minute - so both halves are written to be cheap when nothing has changed: the
    /// letters are recomputed and compared, and the fetch is skipped outright when the URL
    /// is the one already on screen.
    /// </remarks>
    /// <param name="displayName">What the account is called. Only the first two characters are used.</param>
    /// <param name="avatarUrl">Absolute URL of the picture, or null/empty for none.</param>
    public void Show(string host, string token, string? avatarUrl, string displayName)
    {
        Initials = InitialsOf(displayName);

        string wanted = avatarUrl ?? "";

        if (string.Equals(wanted, url, StringComparison.Ordinal))
        {
            return;
        }

        url = wanted;
        Stop();

        if (wanted.Length == 0 || string.IsNullOrEmpty(host) || string.IsNullOrEmpty(token))
        {
            return;
        }

        byte[]? ready;

        lock (cacheGate)
        {
            ready = string.Equals(cachedUrl, wanted, StringComparison.Ordinal) ? cachedBytes : null;
        }

        if (ready != null)
        {
            Adopt(ready);
            return;
        }

        CancellationTokenSource source = new();
        loading = source;
        _ = FetchAsync(host, token, wanted, source);
    }

    /// <summary>Nobody is signed in. Forget the picture and the letters both.</summary>
    public void Clear()
    {
        url = "";
        Initials = "";
        Stop();
    }

    private async Task FetchAsync(string host, string token, string wanted, CancellationTokenSource source)
    {
        try
        {
            byte[]? bytes = await ModlightLibrary.GetThumbnailAsync(host, token, wanted, source.Token);

            if (!ReferenceEquals(loading, source) || source.IsCancellationRequested || bytes == null)
            {
                return;
            }

            lock (cacheGate)
            {
                cachedUrl = wanted;
                cachedBytes = bytes;
            }

            Adopt(bytes);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // A missing picture is not worth a word on screen: the letters are already
            // there and they say the same thing. Written down because a picture that
            // never appears is otherwise indistinguishable from an account that has none.
            DebugHelper.WriteException(exception, "Modlight: the account's avatar could not be fetched.");
        }
        finally
        {
            if (ReferenceEquals(loading, source))
            {
                loading = null;
            }

            source.Dispose();
        }
    }

    /// <summary>Turn bytes into something drawable, moving if they move.</summary>
    private void Adopt(byte[] bytes)
    {
        AnimatedThumbnail? animated = null;

        try
        {
            animated = AnimatedThumbnail.Decode(bytes);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Modlight: the account's avatar could not be decoded as animation.");
        }

        if (animated != null)
        {
            reel = animated;
            frame = 0;
            Image = animated.Frame(0);

            frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(animated.Delay(0)) };
            frameTimer.Tick += OnFrame;
            frameTimer.Start();
            return;
        }

        // Not animated, or not decodable as animation: the single frame is the answer, and
        // showing it is strictly better than showing nothing over a format we cannot play.
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            still = new Bitmap(stream);
            Image = still;
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Modlight: the account's avatar is not a picture we can draw.");
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        AnimatedThumbnail? playing = reel;

        if (playing == null)
        {
            return;
        }

        frame++;
        Image = playing.Frame(frame);

        TimeSpan next = TimeSpan.FromMilliseconds(playing.Delay(frame));

        // Only when it differs: assigning Interval restarts the timer, and a run of frames
        // with the same delay would then drift by however long this handler took.
        if (frameTimer != null && frameTimer.Interval != next)
        {
            frameTimer.Interval = next;
        }
    }

    /// <summary>
    /// Put everything down, in the order that leaves nothing pointing at a freed surface.
    /// </summary>
    private void Stop()
    {
        CancellationTokenSource? source = loading;
        loading = null;
        source?.Cancel();

        if (frameTimer != null)
        {
            frameTimer.Stop();
            frameTimer.Tick -= OnFrame;
            frameTimer = null;
        }

        // The property first: a control still bound to a bitmap that has just been
        // disposed is the one way this can take the window down with it.
        Image = null;

        reel?.Dispose();
        reel = null;

        still?.Dispose();
        still = null;

        frame = 0;
    }

    /// <summary>
    /// The two letters the website would draw.
    /// </summary>
    /// <remarks>
    /// Deliberately the same rule as components/app-shell.tsx: the first two characters of
    /// the name, upper-cased, with no cleverness about word boundaries. Matching it letter
    /// for letter is the whole point - an account whose circle says NI in the browser and
    /// something else here reads as two different accounts.
    /// </remarks>
    private static string InitialsOf(string? name)
    {
        string trimmed = (name ?? "").Trim();

        if (trimmed.Length == 0)
        {
            return "";
        }

        return trimmed.Substring(0, Math.Min(2, trimmed.Length)).ToUpperInvariant();
    }

    public void Dispose() => Stop();

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
