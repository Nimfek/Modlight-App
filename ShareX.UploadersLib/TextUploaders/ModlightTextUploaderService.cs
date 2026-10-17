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

using ShareX.UploadersLib.FileUploaders;

namespace ShareX.UploadersLib.TextUploaders
{
    /// <summary>
    /// Modlight as the text destination.
    /// </summary>
    /// <remarks>
    /// Returns the ordinary file uploader rather than a TextUploader subclass: the
    /// text is sent as a file, exactly as the "File uploader" text destination
    /// already does, and the server files it like any other upload. The panel's
    /// pastes are a richer thing (syntax highlighting, view limits) that a hotkey
    /// cannot reach today - when the app grows a paste path, it belongs there.
    ///
    /// Needed for the same reason as the image service: UploaderFactory would throw
    /// KeyNotFoundException on a TextDestination member with nothing behind it.
    /// </remarks>
    public class ModlightTextUploaderService : TextUploaderService
    {
        public override TextDestination EnumValue { get; } = TextDestination.Modlight;

        public override bool CheckConfig(UploadersConfig config)
        {
            return ModlightAPI.IsSignedIn(config);
        }

        public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo)
        {
            return new Modlight(config.ModlightAccount);
        }
    }
}
