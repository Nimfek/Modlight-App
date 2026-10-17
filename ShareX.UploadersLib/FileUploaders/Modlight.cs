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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;

namespace ShareX.UploadersLib.FileUploaders
{
    public class ModlightFileUploaderService : FileUploaderService
    {
        public override FileDestination EnumValue { get; } = FileDestination.Modlight;

        public override bool CheckConfig(UploadersConfig config)
        {
            return ModlightAPI.IsSignedIn(config);
        }

        public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo)
        {
            return new Modlight(config.ModlightAccount)
            {
                FolderId = taskInfo?.ModlightFolderId,
                Reference = taskInfo
            };
        }
    }

    /// <summary>
    /// Uploads to a Modlight server: one POST /upload, streamed, no size limit and
    /// no timeout.
    /// </summary>
    /// <remarks>
    /// Deliberately a plain single-POST uploader and not something resumable. The
    /// server keeps this endpoint uncapped precisely for clients that cannot chunk,
    /// on the condition that the token arrives in a header - it can then decide who
    /// is uploading before it reads a byte of the body, which is what lets it drop
    /// the ceiling it has to impose on unauthenticated requests. The resumable
    /// protocol (tus) exists on the same server, but it belongs to the panel.
    /// </remarks>
    public sealed class Modlight : FileUploader
    {
        public ModlightAccount Account { get; private set; }

        /// <summary>
        /// The folder this upload is aimed at, or null to let the account decide.
        /// </summary>
        /// <remarks>
        /// Three states, and the difference between two of them is the whole reason this
        /// is a nullable string rather than a bool plus a value. Null means "expressed no
        /// preference", which the server answers with the account's own default folder -
        /// the behaviour every ShareX upload has always had. The root sentinel means "no
        /// folder, deliberately". A folder id means that folder.
        ///
        /// An id the account does not own is NOT a failed upload: the server stores the
        /// file in the default folder and says so in folder_warning, because by the time
        /// it can check, the bytes are already on its disk.
        /// </remarks>
        public string FolderId { get; set; }

        /// <summary>
        /// The task this upload belongs to, for writing one answer back onto. May be null.
        /// </summary>
        /// <remarks>
        /// The same object <see cref="FolderId"/> was read out of, kept rather than
        /// discarded so the folder the server CHOSE can travel back the way the folder we
        /// asked for travelled in. Null in the tests and anywhere an uploader is built
        /// without a task, so every write through it is guarded.
        /// </remarks>
        public TaskReferenceHelper Reference { get; set; }

        /// <summary>
        /// The server's answer to the last attempt, or null when it never answered.
        /// </summary>
        /// <remarks>
        /// Exposed for one caller and one question: the offline queue has to tell an upload
        /// that could not reach the server from one the server refused. The first is worth
        /// keeping and sending later; the second is a message the user needs NOW, and a 507
        /// put on a retry loop is a full account nobody is told about. Nothing else in the
        /// error path carries the status code - a refusal arrives as an exception, so
        /// UploadResult.ResponseInfo is left null - which is why this is the accessor rather
        /// than a new field.
        /// </remarks>
        public ResponseInfo LastAnswer => LastResponseInfo;

        public Modlight(ModlightAccount account)
        {
            Account = account;
        }

        public override UploadResult Upload(Stream stream, string fileName)
        {
            if (!ModlightAPI.IsSignedIn(Account))
            {
                Errors.Add(Localization.Strings.Modlight_Not_signed_in);
                return null;
            }

            string url = URLHelpers.CombineURL(ModlightAPI.BaseURL(Account), "upload");

            Dictionary<string, string> arguments = null;

            if (!string.IsNullOrEmpty(FolderId))
            {
                // A multipart field rather than a query parameter, which is what the
                // server reads and what its own folder-target module names. It is read
                // after the body has been parsed, so it does not matter that it lands
                // after the file part.
                arguments = new Dictionary<string, string> { { ModlightAPI.FolderFieldName, FolderId } };
            }

            UploadResult result = SendRequestFile(url, stream, fileName, ModlightAPI.FileFormName, arguments, ModlightAPI.AuthHeaders(Account));

            // Null is not a failure: TransferData returns it when the user stopped
            // the task mid-body, and there is nothing to report for that.
            if (result == null)
            {
                return null;
            }

            if (!result.IsSuccess)
            {
                ReportRefusal();
                return result;
            }

            ModlightUploadResponse response;

            try
            {
                response = JsonConvert.DeserializeObject<ModlightUploadResponse>(result.Response);
            }
            catch (JsonException)
            {
                response = null;
            }

            if (response == null || string.IsNullOrEmpty(response.URL))
            {
                Errors.Add(Localization.Strings.Modlight_Invalid_response);
                return result;
            }

            // copy_url, not url, is what belongs on the clipboard. Whether this
            // account wants the viewer page or the raw file is a preference its
            // owner already set in the panel, and the server answers it per upload.
            // Following it here keeps the app, ShareX and the website saying the
            // same thing without a fourth switch of our own to fall out of sync.
            result.URL = string.IsNullOrEmpty(response.CopyURL) ? response.URL : response.CopyURL;
            result.ThumbnailURL = response.RawURL;
            result.DeletionURL = response.DeletionURL;

            // Where it actually went, which is not always where it was sent. Every upload
            // that expressed no preference - a hotkey capture, a tray upload, a watched
            // folder - is answered with the account's default folder, and until this line
            // that answer was parsed and dropped: the window then heard "landed in no
            // folder" and left the receiving tile's count untouched.
            if (Reference != null)
            {
                Reference.ModlightLandedFolderId = string.IsNullOrEmpty(response.Folder) ? null : response.Folder;
            }

            if (!string.IsNullOrEmpty(response.FolderWarning))
            {
                // Not an error, and deliberately not in Errors: the file is stored,
                // just in the account's default folder because the folder it was
                // addressed to is gone. Putting it in Errors would report a
                // successful upload as a failed one.
                DebugHelper.WriteLine($"Modlight: {response.FolderWarning}. Stored in the account's default folder instead.");
            }

            return result;
        }

        /// <summary>
        /// Put one sentence a person can act on in front of the exception dump.
        /// </summary>
        /// <remarks>
        /// The dump stays underneath, because that is what a bug report needs, but
        /// the FIRST entry is the one the completion notification shows. Without
        /// this every refusal reads as a network problem, and the one that must not
        /// is 507: it means there is no room, which is the single case where trying
        /// again cannot possibly succeed.
        ///
        /// ShareX's own retry (Settings.MaxUploadFailRetry) is knowingly left alone.
        /// The only lever for suppressing it is TaskReferenceHelper.StopRequested,
        /// and a task that ends "Stopped" produces no notification at all - that
        /// would trade telling the user for saving one attempt, which is backwards.
        /// </remarks>
        private void ReportRefusal()
        {
            ResponseInfo info = LastResponseInfo;

            // No response at all - DNS, TLS, a socket that died. The exception that
            // ProcessError already recorded is the honest account of that, and
            // anything written here instead would be a guess.
            if (info == null)
            {
                return;
            }

            string message;

            switch ((int)info.StatusCode)
            {
                case 400:
                    message = Localization.Strings.Modlight_Request_rejected;
                    break;
                case 401:
                    message = Localization.Strings.Modlight_Account_disabled;
                    break;
                case 404:
                    message = Localization.Strings.Modlight_Token_not_recognized;
                    break;
                case 409:
                    message = Localization.Strings.Modlight_Address_taken;
                    break;
                case 413:
                    message = Localization.Strings.Modlight_File_too_large;
                    break;
                case 507:
                    message = Localization.Strings.Modlight_Out_of_space;
                    break;
                default:
                    message = string.Format(Localization.Strings.Modlight_Server_refused, (int)info.StatusCode);
                    break;
            }

            Errors.AddFirst(ModlightAPI.WithServerDetail(message, info));
        }

        private class ModlightUploadResponse
        {
            /// <summary>Viewer page for the upload.</summary>
            [JsonProperty("url")]
            public string URL { get; set; }

            /// <summary>The file itself, for embedding.</summary>
            [JsonProperty("raw_url")]
            public string RawURL { get; set; }

            /// <summary>Whichever of the two this account asked to be given.</summary>
            [JsonProperty("copy_url")]
            public string CopyURL { get; set; }

            [JsonProperty("deletionUrl")]
            public string DeletionURL { get; set; }

            /// <summary>Folder the upload actually landed in, or null for none.</summary>
            [JsonProperty("folder")]
            public string Folder { get; set; }

            /// <summary>Present when the requested folder could not be honoured.</summary>
            [JsonProperty("folder_warning")]
            public string FolderWarning { get; set; }
        }
    }

    /// <summary>
    /// The handful of things every Modlight client in this assembly agrees on.
    /// </summary>
    /// <remarks>
    /// Shared by the uploader and the URL shortener, which talk to the same server
    /// with the same credential. Kept in one place so a change to how this app
    /// authenticates cannot be made in one of the two and forgotten in the other.
    /// </remarks>
    internal static class ModlightAPI
    {
        /// <summary>The multipart field POST /upload reads the file from.</summary>
        public const string FileFormName = "upload";

        /// <summary>The multipart field POST /upload reads the target folder from.</summary>
        public const string FolderFieldName = "folderId";

        public static bool IsSignedIn(UploadersConfig config)
        {
            return config != null && IsSignedIn(config.ModlightAccount);
        }

        public static bool IsSignedIn(ModlightAccount account)
        {
            return account != null && account.IsSignedIn;
        }

        /// <summary>Origin to build request URLs from, with a scheme guaranteed.</summary>
        public static string BaseURL(ModlightAccount account)
        {
            string host = account?.Host;

            if (string.IsNullOrEmpty(host))
            {
                return ModlightAccount.DefaultHost;
            }

            return URLHelpers.FixPrefix(host);
        }

        /// <summary>
        /// The credential, as a header.
        /// </summary>
        /// <remarks>
        /// x-api-key rather than the legacy multipart "token" field that
        /// older .sxcu files used. ShareX may emit form fields after the file
        /// part, so a token sent that way can only be checked once the body is
        /// already on the server's disk - which is why the server caps uploads that
        /// authenticate that way and leaves header-authenticated ones unlimited.
        /// </remarks>
        public static NameValueCollection AuthHeaders(ModlightAccount account)
        {
            NameValueCollection headers = new NameValueCollection();
            headers.Add("x-api-key", account.Token);
            return headers;
        }

        /// <summary>Our sentence, plus whatever the server said, when it said anything.</summary>
        public static string WithServerDetail(string message, ResponseInfo info)
        {
            string detail = ServerText(info);

            if (string.IsNullOrEmpty(detail))
            {
                return message;
            }

            return message + Environment.NewLine + detail;
        }

        /// <summary>
        /// The server's own explanation for a refusal, or null.
        /// </summary>
        /// <remarks>
        /// Every JSON answer carries either "message" (POST /upload) or "reason"
        /// (the /api routes), and some of them are worth repeating verbatim - the
        /// 413 for an unauthenticated upload, for instance, names the exact fix.
        /// A 502 from the reverse proxy is HTML and parsing it simply fails, which
        /// is the correct outcome: there is nothing there worth showing.
        /// </remarks>
        public static string ServerText(ResponseInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.ResponseText))
            {
                return null;
            }

            try
            {
                JToken parsed = JToken.Parse(info.ResponseText);
                string text = (string)parsed.SelectToken("message") ?? (string)parsed.SelectToken("reason");

                // POST /upload answers "OK" on success; as an explanation of a
                // failure it is noise.
                return text == "OK" ? null : text;
            }
            catch (Exception)
            {
                // This runs while a failure is already being reported. A body that
                // is not the shape we expect must not turn one error into two.
                return null;
            }
        }
    }
}
