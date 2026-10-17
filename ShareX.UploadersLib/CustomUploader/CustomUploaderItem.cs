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
using System.Collections.Generic;

namespace ShareX.UploadersLib
{
    /// <summary>
    /// As much of a .sxcu file as a one-time import needs to read.
    /// </summary>
    /// <remarks>
    /// The custom uploader subsystem is gone - no editor, no destinations, no syntax
    /// parser - but the file format outlives it. Someone whose ShareX points at this
    /// server today has a .sxcu holding the two facts that would let this app sign in
    /// without them typing anything: which host to talk to and which token to use.
    ///
    /// <see cref="RequestURL"/> is the host, with "/upload" on the end.
    /// The token is in <see cref="Arguments"/> under "token" for an older config
    /// file, or in <see cref="Headers"/> under "x-api-key" for a newer one; some
    /// hosts put it in the query string, which is why <see cref="Parameters"/> is here
    /// too. Feed the pair to ModlightSignIn.UseTokenAsync and the import is done.
    ///
    /// Everything else the format carries - request method, body kind, the response
    /// templates, the $json:...$ parser expressions - described how to talk to a server
    /// this app no longer has to be told how to talk to, and went with the editor.
    /// </remarks>
    public class CustomUploaderItem
    {
        public string Version { get; set; }

        public string Name { get; set; }

        public string RequestURL { get; set; }

        public Dictionary<string, string> Parameters { get; set; }

        public Dictionary<string, string> Headers { get; set; }

        public Dictionary<string, string> Arguments { get; set; }

        public string FileFormName { get; set; }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(Name))
            {
                return Name;
            }

            string name = URLHelpers.GetHostName(RequestURL);

            return string.IsNullOrEmpty(name) ? "Name" : name;
        }
    }
}
