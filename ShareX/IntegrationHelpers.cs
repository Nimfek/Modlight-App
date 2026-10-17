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

using ShareX.Localization;
using ShareX.HelpersLib;
using System;
using System.IO;
using System.Windows.Forms;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;

namespace ShareX
{
    public static class IntegrationHelpers
    {
        private static readonly string ApplicationPath = $"\"{Application.ExecutablePath}\"";
        private static readonly string FileIconPath = $"\"{FileHelpers.GetAbsolutePath("ShareX_File_Icon.ico")}\"";

        private static readonly string ShellExtMenuName = "Modlight";
        private static readonly string ShellExtMenuFiles = $@"Software\Classes\*\shell\{ShellExtMenuName}";
        private static readonly string ShellExtMenuFilesCmd = $@"{ShellExtMenuFiles}\command";
        private static readonly string ShellExtMenuDirectory = $@"Software\Classes\Directory\shell\{ShellExtMenuName}";
        private static readonly string ShellExtMenuDirectoryCmd = $@"{ShellExtMenuDirectory}\command";
        private static readonly string ShellExtDesc = Strings.IntegrationHelpers_UploadWithShareX;
        private static readonly string ShellExtIcon = $"{ApplicationPath},0";
        private static readonly string ShellExtPath = $"{ApplicationPath} \"%1\"";

        private static readonly string ShellExtEditName = "ModlightImageEditor";
        private static readonly string ShellExtEditImage = $@"Software\Classes\SystemFileAssociations\image\shell\{ShellExtEditName}";
        private static readonly string ShellExtEditImageCmd = $@"{ShellExtEditImage}\command";
        private static readonly string ShellExtEditDesc = Strings.IntegrationHelpers_EditWithShareX;
        private static readonly string ShellExtEditIcon = $"{ApplicationPath},0";
        private static readonly string ShellExtEditPath = $"{ApplicationPath} -ImageEditor \"%1\"";

        private static readonly string ShellCustomUploaderExtensionPath = @"Software\Classes\.sxcu";
        private static readonly string ShellCustomUploaderExtensionValue = "Modlight.sxcu";
        private static readonly string ShellCustomUploaderAssociatePath = $@"Software\Classes\{ShellCustomUploaderExtensionValue}";

        private static readonly string ShellImageEffectExtensionPath = @"Software\Classes\.sxie";
        private static readonly string ShellImageEffectExtensionValue = "Modlight.sxie";
        private static readonly string ShellImageEffectAssociatePath = $@"Software\Classes\{ShellImageEffectExtensionValue}";
        private static readonly string ShellImageEffectAssociateValue = "Modlight image effect";
        private static readonly string ShellImageEffectIconPath = $@"{ShellImageEffectAssociatePath}\DefaultIcon";
        private static readonly string ShellImageEffectIconValue = $"{FileIconPath}";
        private static readonly string ShellImageEffectCommandPath = $@"{ShellImageEffectAssociatePath}\shell\open\command";
        private static readonly string ShellImageEffectCommandValue = $"{ApplicationPath} -ImageEffect \"%1\"";

        public static bool CheckShellContextMenuButton()
        {
            try
            {
                return RegistryHelpers.CheckStringValue(ShellExtMenuFilesCmd, null, ShellExtPath) &&
                    RegistryHelpers.CheckStringValue(ShellExtMenuDirectoryCmd, null, ShellExtPath);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            return false;
        }

        public static void CreateShellContextMenuButton(bool create)
        {
            try
            {
                if (create)
                {
                    UnregisterShellContextMenuButton();
                    RegisterShellContextMenuButton();
                }
                else
                {
                    UnregisterShellContextMenuButton();
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private static void RegisterShellContextMenuButton()
        {
            RegistryHelpers.CreateRegistry(ShellExtMenuFiles, ShellExtDesc);
            RegistryHelpers.CreateRegistry(ShellExtMenuFiles, "Icon", ShellExtIcon);
            RegistryHelpers.CreateRegistry(ShellExtMenuFilesCmd, ShellExtPath);

            RegistryHelpers.CreateRegistry(ShellExtMenuDirectory, ShellExtDesc);
            RegistryHelpers.CreateRegistry(ShellExtMenuDirectory, "Icon", ShellExtIcon);
            RegistryHelpers.CreateRegistry(ShellExtMenuDirectoryCmd, ShellExtPath);
        }

        private static void UnregisterShellContextMenuButton()
        {
            RegistryHelpers.RemoveRegistry(ShellExtMenuFiles);
            RegistryHelpers.RemoveRegistry(ShellExtMenuDirectory);
        }

        public static bool CheckEditShellContextMenuButton()
        {
            try
            {
                return RegistryHelpers.CheckStringValue(ShellExtEditImageCmd, null, ShellExtEditPath);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            return false;
        }

        public static void CreateEditShellContextMenuButton(bool create)
        {
            try
            {
                if (create)
                {
                    UnregisterEditShellContextMenuButton();
                    RegisterEditShellContextMenuButton();
                }
                else
                {
                    UnregisterEditShellContextMenuButton();
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private static void RegisterEditShellContextMenuButton()
        {
            RegistryHelpers.CreateRegistry(ShellExtEditImage, ShellExtEditDesc);
            RegistryHelpers.CreateRegistry(ShellExtEditImage, "Icon", ShellExtEditIcon);
            RegistryHelpers.CreateRegistry(ShellExtEditImageCmd, ShellExtEditPath);
        }

        private static void UnregisterEditShellContextMenuButton()
        {
            RegistryHelpers.RemoveRegistry(ShellExtEditImage);
        }

        /// <summary>
        /// Take the .sxcu file association back off the machine.
        /// </summary>
        /// <remarks>
        /// Only ever removes. Earlier builds registered .sxcu so that opening one ran
        /// "-CustomUploader", and that command is gone with the custom uploader editor,
        /// so a machine that upgrades would keep a file type whose double-click starts
        /// the app and does nothing. Called on every non-portable start, not just on
        /// uninstall, because the keys are already out there.
        /// </remarks>
        public static void RemoveCustomUploaderExtension()
        {
            try
            {
                RegistryHelpers.RemoveRegistry(ShellCustomUploaderExtensionPath);
                RegistryHelpers.RemoveRegistry(ShellCustomUploaderAssociatePath);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        public static bool CheckImageEffectExtension()
        {
            try
            {
                return RegistryHelpers.CheckStringValue(ShellImageEffectExtensionPath, null, ShellImageEffectExtensionValue) &&
                    RegistryHelpers.CheckStringValue(ShellImageEffectCommandPath, null, ShellImageEffectCommandValue);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            return false;
        }

        public static void CreateImageEffectExtension(bool create)
        {
            try
            {
                if (create)
                {
                    UnregisterImageEffectExtension();
                    RegisterImageEffectExtension();
                }
                else
                {
                    UnregisterImageEffectExtension();
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private static void RegisterImageEffectExtension()
        {
            RegistryHelpers.CreateRegistry(ShellImageEffectExtensionPath, ShellImageEffectExtensionValue);
            RegistryHelpers.CreateRegistry(ShellImageEffectAssociatePath, ShellImageEffectAssociateValue);
            RegistryHelpers.CreateRegistry(ShellImageEffectIconPath, ShellImageEffectIconValue);
            RegistryHelpers.CreateRegistry(ShellImageEffectCommandPath, ShellImageEffectCommandValue);

            NativeMethods.SHChangeNotify(HChangeNotifyEventID.SHCNE_ASSOCCHANGED, HChangeNotifyFlags.SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }

        private static void UnregisterImageEffectExtension()
        {
            RegistryHelpers.RemoveRegistry(ShellImageEffectExtensionPath);
            RegistryHelpers.RemoveRegistry(ShellImageEffectAssociatePath);
        }

        public static bool CheckSendToMenuButton()
        {
            return ShortcutHelpers.CheckShortcut(Environment.SpecialFolder.SendTo, "Modlight", Application.ExecutablePath);
        }

        public static bool CreateSendToMenuButton(bool create)
        {
            return ShortcutHelpers.SetShortcut(create, Environment.SpecialFolder.SendTo, "Modlight", Application.ExecutablePath);
        }

        public static void Uninstall()
        {
            StartupManager.State = StartupState.Disabled;
            CreateShellContextMenuButton(false);
            CreateEditShellContextMenuButton(false);
            RemoveCustomUploaderExtension();
            CreateImageEffectExtension(false);
            CreateSendToMenuButton(false);
        }
    }
}
