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

using Microsoft.Win32;
using ShareX.HelpersLib;
using System;
using System.Windows.Forms;

namespace ShareX
{
    public static class StartupManager
    {
        /// <summary>
        /// The copy of Modlight that Windows will start - which is the one that
        /// asked, not necessarily the one the startup shortcut points at.
        /// </summary>
        public static string StartupTargetPath => Application.ExecutablePath;

        /// <summary>
        /// The OTHER copy of Modlight that the startup shortcut starts, or an
        /// empty string when there is no shortcut or it points at this one.
        /// </summary>
        /// <remarks>
        /// State compares the shortcut against StartupTargetPath, so it answers
        /// "does Windows start THIS build", not "does Windows start Modlight".
        /// Run a copy from anywhere but the installed folder - a build tree, a
        /// portable copy on a stick - and State is Disabled while Windows goes on
        /// starting the installed copy at every sign-in. The answer is correct
        /// and unreadable: the checkbox says off, the machine says on, and the
        /// setting looks broken.
        ///
        /// So the two ways of being off are kept apart. Nothing here changes what
        /// State returns; it gives the UI the one fact State cannot carry, which
        /// is WHICH copy is the one Windows starts.
        /// </remarks>
        public static string OtherCopyStartupTargetPath
        {
            get
            {
                string target = ShortcutHelpers.GetShortcutTarget(Environment.SpecialFolder.Startup, "Modlight");

                return !string.IsNullOrEmpty(target) && !target.Equals(StartupTargetPath, StringComparison.OrdinalIgnoreCase)
                    ? target
                    : string.Empty;
            }
        }

        public static StartupState State
        {
            get
            {
                if (ShortcutHelpers.CheckShortcut(Environment.SpecialFolder.Startup, "Modlight", StartupTargetPath))
                {
                    if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
                        "Modlight.lnk", null) is byte[] status && status.Length > 0 && status[0] == 3)
                    {
                        return StartupState.DisabledByUser;
                    }
                    else
                    {
                        return StartupState.Enabled;
                    }
                }
                else
                {
                    return StartupState.Disabled;
                }
            }
            set
            {
                if (value == StartupState.Enabled || value == StartupState.Disabled)
                {
                    ShortcutHelpers.SetShortcut(value == StartupState.Enabled, Environment.SpecialFolder.Startup, "Modlight", StartupTargetPath, "-silent");
                }
                else
                {
                    throw new NotSupportedException();
                }
            }
        }
    }
}