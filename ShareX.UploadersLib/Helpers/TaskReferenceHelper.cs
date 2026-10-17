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

namespace ShareX.UploadersLib
{
    public class TaskReferenceHelper
    {
        public EDataType DataType { get; set; }
        public bool StopRequested { get; set; }
        public string TextFormat { get; set; }

        /// <summary>
        /// The Modlight folder this one task is aimed at, or null for the account default.
        /// </summary>
        /// <remarks>
        /// Carried per task rather than on the account, because it is an instruction for a
        /// single upload and not a preference: it comes from the folder tile the files
        /// were dropped on, or from the folder open in My space when the picker was used.
        /// A field on ModlightAccount would be a global that two concurrent uploads would
        /// fight over.
        /// </remarks>
        public string ModlightFolderId { get; set; }

        /// <summary>
        /// The folder the server says the upload ACTUALLY landed in, or null if it never said.
        /// </summary>
        /// <remarks>
        /// The other direction of the same wire, and it is the only one that can be trusted
        /// to name a folder tile. <see cref="ModlightFolderId"/> is the request, and for a
        /// hotkey capture, a tray upload or anything ShareX sends, the request is null -
        /// "you decide" - while the server decides on the account's default folder and says
        /// which one in the 201. Without this the window is told an upload landed in "no
        /// folder", so the tile that received it keeps its old count and never flashes,
        /// which is exactly the half of the library that was still going stale.
        ///
        /// Written by the uploader after the answer is parsed; read once by WorkerTask.
        /// Null stays null: "the server did not say" and "it went nowhere in particular"
        /// are the same thing to a caller that can only skip the correction either way.
        /// </remarks>
        public string ModlightLandedFolderId { get; set; }

        /// <summary>
        /// Custom code, expiry and password for this one short link, or null for none.
        /// </summary>
        /// <remarks>
        /// Per task for the same reason as the folder: two links being shortened at once
        /// are two different requests, and a field on the account would give them one
        /// password between them.
        /// </remarks>
        public ModlightShortLinkOptions ModlightShortLink { get; set; }
    }
}