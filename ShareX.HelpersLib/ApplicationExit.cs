#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.AvaloniaUI.Integration;
using System;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Ends the application - whatever "the application" means in this build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because <c>System.Windows.Forms.Application.Exit()</c> does nothing here,
    /// and did nothing silently. That call ends WinForms message loops and closes the forms in
    /// <see cref="System.Windows.Forms.Application.OpenForms"/>; this fork has neither. There
    /// is no <c>Application.Run</c> anywhere in the tree - the loop is Avalonia's
    /// <c>desktop.Start(args)</c> - and <c>MainForm</c> never reaches <c>OpenForms</c>, because
    /// its <c>SetVisibleCore</c> override forces the form to stay invisible. Measured on a
    /// form built the same way: <c>OpenForms</c> is 0 before and after <c>Show()</c>, and
    /// <c>Application.Exit()</c> returns without firing <c>FormClosed</c>.
    /// </para>
    /// <para>
    /// The consequence was not cosmetic. <c>MainForm_FormClosed</c> is what runs
    /// <c>AvaloniaBootstrapper.Shutdown()</c>, so nothing shut down, the process stayed alive
    /// in the notification area, and it kept holding the single-instance mutex - the very
    /// mutex the installer's <c>AppMutex</c> tests. An update would download, verify, launch
    /// its installer and then stop at "Setup has detected that Modlight is currently running".
    /// </para>
    /// <para>
    /// A settable handler rather than a direct call, because the correct exit belongs to the
    /// application project (<c>ShareX.Program.Exit</c>, which marshals onto the thread that
    /// owns <c>MainForm</c> and runs the whole close sequence) and this assembly cannot see it.
    /// </para>
    /// </remarks>
    public static class ApplicationExit
    {
        /// <summary>
        /// What to run to end the application. Registered once, at startup.
        /// </summary>
        /// <remarks>
        /// Null is not "do nothing": <see cref="Request"/> falls back to shutting the Avalonia
        /// lifetime down, which is the honest answer for a host that got as far as putting a
        /// window on screen without registering anything.
        /// </remarks>
        public static Action? Handler { get; set; }

        /// <summary>Ask the application to end. Returns false only if nothing could be asked.</summary>
        public static bool Request(string reason)
        {
            DebugHelper.WriteLine("Application exit requested: " + reason);

            Action? handler = Handler;

            try
            {
                if (handler != null)
                {
                    handler();
                    return true;
                }

                // No host registered one. Still not Application.Exit(): shut the lifetime that
                // actually owns the message loop, so at worst the windows go and the process
                // stops holding its mutex.
                DebugHelper.WriteLine("Application exit: no handler registered, shutting the Avalonia lifetime down directly.");
                AvaloniaBootstrapper.Shutdown();
                return true;
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception, "Application exit handler failed.");
                return false;
            }
        }
    }
}
