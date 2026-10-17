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
using System;

namespace ShareX.UploadersLib.FileUploaders
{
    /// <summary>
    /// The Modlight account this installation uploads to.
    /// </summary>
    /// <remarks>
    /// Written by the sign-in flow, read by the uploader. Everything here except
    /// the token is a copy of what GET /api/me answered at sign-in time, kept so
    /// the app can say whose account it is holding without a round trip - and
    /// deliberately not more than that. Quota is not cached: it changes with every
    /// upload, and a stale number on screen is worse than no number.
    /// </remarks>
    public class ModlightAccount
    {
        /// <summary>Origin of the server, without a trailing slash, e.g. https://files.example.com</summary>
        public string Host { get; set; } = DefaultHost;

        /// <summary>
        /// The API token, sent as the x-api-key header. Encrypted at rest through
        /// DPAPI like every other credential in this file: it is a bearer key to
        /// somebody's whole library, and it never expires on its own.
        /// </summary>
        [JsonEncrypt]
        public string Token { get; set; } = "";

        /// <summary>The account's short code (users.user_code), which its links are built from.</summary>
        public string Tag { get; set; } = "";

        public string Name { get; set; } = "";
        public string Email { get; set; } = "";

        /// <summary>
        /// URL of the account's picture, or empty for none.
        /// </summary>
        /// <remarks>
        /// Kept so the window has something to draw on the first frame after a cold start,
        /// before /api/me has answered. It is refreshed from that answer, so a picture
        /// changed in the panel reaches this machine within a refresh rather than only at
        /// the next sign-in.
        /// </remarks>
        public string Avatar { get; set; } = "";

        /// <summary>When the token was issued, so a stale one can be recognised later.</summary>
        public DateTime SignedInAt { get; set; }

        // From ModlightDefaultHost in Directory.Build.props; empty unless a distribution sets it.
        public const string DefaultHost = ModlightBuildInfo.DefaultHost;

        public bool IsSignedIn => !string.IsNullOrEmpty(Host) && !string.IsNullOrEmpty(Token);
    }
}
