#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System.Runtime.InteropServices;

namespace ShareX;

/// <summary>
/// "Show animations in Windows" - the system's own prefers-reduced-motion.
/// </summary>
/// <remarks>
/// Avalonia does not surface this, so it is asked of Windows directly, and a call that
/// fails is read as yes: the setting exists to take motion away from people who asked
/// for that, and nobody asked by having a P/Invoke fail.
///
/// Read at the moment it matters rather than cached at startup. The call is a few
/// microseconds against a network request or a decode, and caching it would mean the
/// switch in Settings did nothing until the app was restarted - which is the one thing
/// somebody toggling it is trying to check.
/// </remarks>
internal static class ClientAreaAnimation
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    public static bool Enabled
    {
        get
        {
            int enabled = 1;

            return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0) || enabled != 0;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint update);
}
