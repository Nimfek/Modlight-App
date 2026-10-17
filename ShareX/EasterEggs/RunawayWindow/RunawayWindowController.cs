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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using FormsCursor = System.Windows.Forms.Cursor;

namespace ShareX;

/// <summary>
/// Ten clicks on the logo in the title bar and the window stops being furniture:
/// it bounces off the edges of its screen and shies away from the pointer.
/// </summary>
/// <remarks>
/// The whole thing is written around one rule - it must never be the reason somebody
/// cannot get at their own window. So it stops on any key, on any click anywhere in
/// the window, when the window loses focus, and on its own after
/// <see cref="MaxRun"/> whatever happens; and when it stops it puts the window back
/// exactly where it was picked up, because a joke that rearranges your desktop is
/// not a joke. It also never starts on a maximised window, on a window that does not
/// fit its own screen with room to move, or on a machine where "show animations in
/// Windows" is off - that switch is a request, and this is nothing but motion.
/// </remarks>
internal sealed class RunawayWindowController : IDisposable
{
    /// <summary>Clicks on the logo needed to set it off.</summary>
    private const int ClicksToStart = 10;

    /// <summary>How long a click waits for the next one before the count starts over.</summary>
    private static readonly TimeSpan ClickGap = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// The hard stop. Everything else that ends this is something the user does, and
    /// this is the case where they do nothing at all - walked away, or handed the
    /// keyboard to somebody who has no idea what is going on.
    /// </summary>
    private static readonly TimeSpan MaxRun = TimeSpan.FromSeconds(12);

    /// <summary>Physical pixels per second the window settles back to.</summary>
    private const double CruiseSpeed = 620;

    /// <summary>The ceiling a panic away from the pointer can push it to.</summary>
    private const double MaxSpeed = 1600;

    /// <summary>How much speed a second of calm takes back off the top.</summary>
    private const double SpeedDecay = 900;

    /// <summary>How close the pointer has to get, in physical pixels, to be noticed.</summary>
    private const double PanicRadius = 280;

    /// <summary>The shove away from a pointer that got that close.</summary>
    private const double PanicPush = 2800;

    private readonly Window _window;
    private readonly Action<bool>? _runningChanged;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _life = new();
    private readonly Random _random = new();

    private PixelPoint _origin;
    private PixelRect _area;
    private PixelSize _size;
    private double _x;
    private double _y;
    private double _velocityX;
    private double _velocityY;
    private long _lastTick;
    private int _clicks;
    private DateTime _lastClick = DateTime.MinValue;
    private bool _disposed;

    public RunawayWindowController(Window window, Action<bool>? runningChanged = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _runningChanged = runningChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;

        // Tunnelling, so these see the input before anything in the window can handle
        // and swallow it: "press a key" has to mean any key, including one a text box
        // would otherwise eat.
        _window.AddHandler(InputElement.KeyDownEvent, OnAnyKeyDown, RoutingStrategies.Tunnel);
        _window.AddHandler(InputElement.PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel);
        _window.Deactivated += OnWindowDeactivated;
    }

    public bool IsRunning { get; private set; }

    /// <summary>One click on the logo.</summary>
    public void Nudge()
    {
        if (_disposed)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        _clicks = now - _lastClick > ClickGap ? 1 : _clicks + 1;
        _lastClick = now;

        if (_clicks < ClicksToStart)
        {
            return;
        }

        _clicks = 0;
        Start();
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _timer.Stop();
        _life.Reset();
        IsRunning = false;

        // Cleared as well as counted down: otherwise the click that stopped it is also
        // the first click of the next ten, and the thing starts again on its own.
        _clicks = 0;

        if (!_disposed)
        {
            _window.Position = _origin;
        }

        _runningChanged?.Invoke(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _timer.Tick -= OnTick;
        _window.RemoveHandler(InputElement.KeyDownEvent, OnAnyKeyDown);
        _window.RemoveHandler(InputElement.PointerPressedEvent, OnAnyPointerPressed);
        _window.Deactivated -= OnWindowDeactivated;
    }

    private void Start()
    {
        // "Show animations in Windows" off is a request for no motion, and this is
        // nothing but motion. Read now rather than cached, the way the rest of the app
        // reads it - somebody who turns the switch off should not have to restart.
        if (IsRunning || !ClientAreaAnimation.Enabled)
        {
            return;
        }

        // A maximised window has nowhere to go, and dropping it out of maximised to make
        // room would be the app rearranging itself over a joke.
        if (_window.WindowState != WindowState.Normal || !_window.IsVisible)
        {
            return;
        }

        Screen? screen = _window.Screens?.ScreenFromWindow(_window) ?? _window.Screens?.Primary;
        if (screen == null)
        {
            return;
        }

        double scaling = _window.RenderScaling > 0 ? _window.RenderScaling : 1;
        _size = PixelSize.FromSize(_window.FrameSize ?? _window.ClientSize, scaling);
        _area = screen.WorkingArea;

        // A window with no room to move would sit in the corner flipping its own
        // direction every frame, which looks like a fault rather than an easter egg.
        if (_size.Width >= _area.Width || _size.Height >= _area.Height)
        {
            return;
        }

        _origin = _window.Position;
        _x = _origin.X;
        _y = _origin.Y;

        // Between 30 and 60 degrees off horizontal, mirrored into a random quadrant.
        // Anything closer to an axis and the first few seconds are a window sliding
        // along one edge, which reads as broken rather than playful.
        double angle = (Math.PI / 6) + (_random.NextDouble() * (Math.PI / 6));
        _velocityX = Math.Cos(angle) * CruiseSpeed * (_random.Next(2) == 0 ? -1 : 1);
        _velocityY = Math.Sin(angle) * CruiseSpeed * (_random.Next(2) == 0 ? -1 : 1);

        IsRunning = true;
        _lastTick = Stopwatch.GetTimestamp();
        _life.Restart();
        _timer.Start();
        _runningChanged?.Invoke(true);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsRunning)
        {
            return;
        }

        if (_life.Elapsed >= MaxRun)
        {
            Stop();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;

        // A frame that took a quarter of a second - a stalled UI thread, a laptop coming
        // back from sleep - would otherwise teleport the window across the desk.
        seconds = Math.Clamp(seconds, 0, 0.05);
        if (seconds <= 0)
        {
            return;
        }

        Flee(seconds);
        Move(seconds);

        _window.Position = new PixelPoint((int)Math.Round(_x), (int)Math.Round(_y));
    }

    private void Flee(double seconds)
    {
        System.Drawing.Point cursor = FormsCursor.Position;
        double awayX = _x + (_size.Width / 2.0) - cursor.X;
        double awayY = _y + (_size.Height / 2.0) - cursor.Y;
        double distance = Math.Sqrt((awayX * awayX) + (awayY * awayY));

        if (distance < PanicRadius)
        {
            // Dead centre under the pointer has no direction to run in; pick one rather
            // than dividing by zero.
            if (distance < 1)
            {
                awayX = 1;
                awayY = 0;
                distance = 1;
            }

            _velocityX += awayX / distance * PanicPush * seconds;
            _velocityY += awayY / distance * PanicPush * seconds;
        }

        double speed = Math.Sqrt((_velocityX * _velocityX) + (_velocityY * _velocityY));
        if (speed < 1)
        {
            _velocityX = CruiseSpeed;
            _velocityY = CruiseSpeed;
            return;
        }

        double eased = Math.Clamp(Math.Max(CruiseSpeed, speed - (SpeedDecay * seconds)), CruiseSpeed, MaxSpeed);
        _velocityX = _velocityX / speed * eased;
        _velocityY = _velocityY / speed * eased;
    }

    private void Move(double seconds)
    {
        _x += _velocityX * seconds;
        _y += _velocityY * seconds;

        double right = _area.Right - _size.Width;
        double bottom = _area.Bottom - _size.Height;

        if (_x <= _area.X)
        {
            _x = _area.X;
            _velocityX = Math.Abs(_velocityX);
        }
        else if (_x >= right)
        {
            _x = right;
            _velocityX = -Math.Abs(_velocityX);
        }

        if (_y <= _area.Y)
        {
            _y = _area.Y;
            _velocityY = Math.Abs(_velocityY);
        }
        else if (_y >= bottom)
        {
            _y = bottom;
            _velocityY = -Math.Abs(_velocityY);
        }
    }

    private void OnAnyKeyDown(object? sender, KeyEventArgs e) => Stop();

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e) => Stop();

    private void OnWindowDeactivated(object? sender, EventArgs e) => Stop();
}
