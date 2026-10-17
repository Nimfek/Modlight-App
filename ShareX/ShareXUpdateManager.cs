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

namespace ShareX
{
    internal class ShareXUpdateManager : GitHubUpdateManager
    {
        // Modlight reads its own release channel, and only its own.
        //
        // The inherited feed was github.com/ShareX/ShareX, and leaving it in place was not
        // survivable at this version number. While the fork still carried ShareX's
        // inherited 21.0.0 the comparison mostly came out "up to date" and hid the problem;
        // at 0.7.0 it cannot, because 0.7.0 is older than every ShareX release there is. So
        // EVERY launch would have found an "update", and accepting it would have downloaded
        // ShareX's own installer and run it over Modlight. That is why this was switched
        // off outright rather than left pointing somewhere plausible, and why turning it
        // back on had to wait for a checker that reads OUR manifest.
        //
        // There is no channel picker any more. The panel publishes exactly one release at
        // GET /api/releases/app - there is no pre-release feed and no dev feed to choose
        // between - so a combo box offering Release and Pre-release would have been a
        // control that changes nothing, which is worse than no control at all.
        // No release host means a build from source with no distribution configured: there is
        // nobody to ask, so there is no channel and the update UI stays hidden.
        public override bool HasReleaseChannel => !string.IsNullOrEmpty(ModlightUpdateChecker.ReleaseHost);

        public override UpdateChecker CreateUpdateChecker()
        {
            return new ModlightUpdateChecker
            {
                // A portable copy has nothing for a setup executable to update, so the
                // checker sends it to the download page instead of fetching an installer.
                IsPortable = IsPortable
            };
        }
    }
}