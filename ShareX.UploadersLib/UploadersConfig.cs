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

using ShareX.HelpersLib;
using ShareX.UploadersLib.FileUploaders;

namespace ShareX.UploadersLib
{
    public class UploadersConfig : SettingsBase<UploadersConfig>
    {
        #region Modlight

        /// <summary>The account this installation uploads to. Filled in by signing in.</summary>
        /// <remarks>
        /// The only destination there is, so the only thing left in here. Everything
        /// that used to sit alongside it - a hundred API keys, bucket names, OAuth
        /// tokens and FTP passwords for services this build cannot reach - went with
        /// the uploaders that read them. Old config files keep those keys on disk;
        /// the deserializer ignores members it has no property for, so they are inert
        /// until the file is next written, which drops them.
        /// </remarks>
        public ModlightAccount ModlightAccount { get; set; } = new ModlightAccount();

        #endregion Modlight
    }
}
