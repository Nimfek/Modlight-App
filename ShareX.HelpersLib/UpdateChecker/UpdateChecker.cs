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

using System;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;

namespace ShareX.HelpersLib
{
    public abstract class UpdateChecker
    {
        /// <summary>For testing purposes.</summary>
        public static bool ForceUpdate { get; private set; } = false;

        public UpdateStatus Status { get; set; }
        public Version CurrentVersion { get; set; }
        public Version LatestVersion { get; set; }
        public ReleaseChannelType ReleaseType { get; set; }
        public bool IsDev { get; set; }
        public bool IsPortable { get; set; }
        public bool IgnoreRevision { get; set; }

        private string fileName;

        public string FileName
        {
            get
            {
                if (string.IsNullOrEmpty(fileName))
                {
                    return HttpUtility.UrlDecode(DownloadURL.Substring(DownloadURL.LastIndexOf('/') + 1));
                }

                return fileName;
            }
            set
            {
                fileName = value;
            }
        }

        public string DownloadURL { get; set; }

        /// <summary>
        /// The SHA-256 the feed promised for <see cref="DownloadURL"/>, as hex.
        /// </summary>
        /// <remarks>
        /// Empty when the feed does not publish one - GitHub does not - and non-empty is the
        /// signal that <see cref="DownloaderWindow"/> must hash the finished download and
        /// refuse to install anything whose bytes disagree. Set it, and an installer cannot
        /// run unverified; leave it, and the old behaviour is unchanged.
        /// </remarks>
        public string ExpectedSha256 { get; set; } = "";

        /// <summary>The latest version as the feed spelled it, for display. Empty falls back to <see cref="LatestVersion"/>.</summary>
        public string LatestVersionText { get; set; } = "";

        /// <summary>What the feed says is in the new release, or empty.</summary>
        public string ReleaseNotes { get; set; } = "";

        /// <summary>Download size in bytes as the feed stated it, or 0 when it did not.</summary>
        public long ReleaseSize { get; set; }

        /// <summary>Where "view changelog" goes for this feed.</summary>
        public string ChangelogURL { get; set; } = Links.Changelog;

        /// <summary>
        /// Where a portable install is sent instead of being handed an installer.
        /// </summary>
        /// <remarks>
        /// A page, not a file: a portable copy has nothing for a setup executable to update,
        /// and running one over it would install a second, different Modlight beside it.
        /// Empty means "open <see cref="DownloadURL"/>", which is what GitHub builds do
        /// because their portable asset is a zip that can be opened directly.
        /// </remarks>
        public string PortableDownloadURL { get; set; } = "";

        public void RefreshStatus()
        {
            if (CurrentVersion == null)
            {
                CurrentVersion = Version.Parse(Application.ProductVersion);
            }

            if (Status != UpdateStatus.UpdateCheckFailed && CurrentVersion != null && LatestVersion != null && !string.IsNullOrEmpty(DownloadURL) &&
                (ForceUpdate || Helpers.CompareVersion(CurrentVersion, LatestVersion, IgnoreRevision) < 0))
            {
                Status = UpdateStatus.UpdateAvailable;
            }
            else
            {
                Status = UpdateStatus.UpToDate;
            }
        }

        public abstract Task CheckUpdateAsync();

        public async Task DownloadUpdateAsync()
        {
            DebugHelper.WriteLine("Updating Modlight from version {0} to {1}", CurrentVersion, LatestVersion);

            if (IsPortable)
            {
                URLHelpers.OpenURL(string.IsNullOrEmpty(PortableDownloadURL) ? DownloadURL : PortableDownloadURL);
            }
            else
            {
                DownloaderWindowResult result = await DownloaderWindow.ShowAsync(this);

                if (result.Status == DownloaderWindowStatus.InstallStarted)
                {
                    // NOT Application.Exit(). That call is a no-op in this fork - no WinForms
                    // message loop, and MainForm never reaches Application.OpenForms - so the
                    // process used to keep running, and keep holding the single-instance
                    // mutex, while the installer it had just started sat on "Setup has
                    // detected that Modlight is currently running". The installer waits on
                    // that mutex; nothing else has to happen here except actually exiting.
                    ApplicationExit.Request("installer started, making way for it");
                }
            }
        }
    }
}
