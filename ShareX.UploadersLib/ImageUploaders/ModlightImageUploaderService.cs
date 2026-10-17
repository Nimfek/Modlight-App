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

namespace ShareX.UploadersLib.ImageUploaders
{
    /// <summary>
    /// Modlight as the image destination.
    /// </summary>
    /// <remarks>
    /// The service sits here with the other image destinations while the uploader
    /// itself stays in FileUploaders, the same split Pushbullet uses to be a file
    /// destination and a sharing service from one class. Modlight sends every kind
    /// of file through the same POST, so there is nothing image-specific to add.
    ///
    /// It has to exist at all because UploaderFactory keys a dictionary on the enum:
    /// an ImageDestination member without a matching service is a
    /// KeyNotFoundException the moment somebody selects it.
    /// </remarks>
    public class ModlightImageUploaderService : ImageUploaderService
    {
        public override ImageDestination EnumValue { get; } = ImageDestination.Modlight;

        public override bool CheckConfig(UploadersConfig config)
        {
            return ModlightAPI.IsSignedIn(config);
        }

        public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo)
        {
            // The folder target is honoured here too. Leaving it out would fail in a way
            // that is invisible to the obvious test: a dropped .txt would land in the
            // folder it was dropped on and a dropped .png would not, because ShareX routes
            // images through the image destination and everything else through the file
            // one, and both end up in the same uploader class by two different doors.
            return new Modlight(config.ModlightAccount) { FolderId = taskInfo?.ModlightFolderId };
        }
    }
}
