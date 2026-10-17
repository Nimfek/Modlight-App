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
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Specialized;

namespace ShareX.UploadersLib.URLShorteners
{
    public class ModlightURLShortenerService : URLShortenerService
    {
        public override UrlShortenerType EnumValue { get; } = UrlShortenerType.Modlight;

        public override bool CheckConfig(UploadersConfig config)
        {
            return ModlightAPI.IsSignedIn(config);
        }

        public override URLShortener CreateShortener(UploadersConfig config, TaskReferenceHelper taskInfo)
        {
            return new ModlightURLShortener(config.ModlightAccount) { Options = taskInfo?.ModlightShortLink };
        }
    }

    /// <summary>
    /// Shortens through POST /api/shortlinks on the same Modlight server the files
    /// go to, with the same token.
    /// </summary>
    /// <remarks>
    /// No host or key of its own on purpose: a second copy of the credential is a
    /// second thing to keep in step with sign-in, and one of the two would end up
    /// stale. The link lands in the signed-in account's own namespace, so it also
    /// shows up in the panel next to everything else that account made.
    /// </remarks>
    public sealed class ModlightURLShortener : URLShortener
    {
        public ModlightAccount Account { get; private set; }

        /// <summary>
        /// Custom name, expiry, password and namespace for this one link, or null.
        /// </summary>
        /// <remarks>
        /// Null is the shape every path other than the dialog takes - the clipboard
        /// auto-shorten, an after-upload shorten, re-shortening from history - and it
        /// means "just the address". The server's own defaults then apply, which are the
        /// ones the panel shows: a generated name under the account's tag, no expiry, no
        /// password.
        /// </remarks>
        public ModlightShortLinkOptions Options { get; set; }

        public ModlightURLShortener(ModlightAccount account)
        {
            Account = account;
        }

        public override UploadResult ShortenURL(string url)
        {
            UploadResult result = new UploadResult { URL = url };

            result.ShortenedURL = Submit(url);

            // WorkerTask.DoAfterUploadJobs copies exactly two things out of what a
            // shortener returns - ShortenedURL and result.Errors - and never looks
            // at Uploader.Errors, where SendRequest puts everything it recorded. So
            // they are moved across here; without this line a refusal is a link
            // that silently did not appear.
            result.Errors.Add(Errors);

            return result;
        }

        private string Submit(string url)
        {
            if (!ModlightAPI.IsSignedIn(Account))
            {
                Errors.Add(Localization.Strings.Modlight_Not_signed_in);
                return null;
            }

            string requestURL = URLHelpers.CombineURL(ModlightAPI.BaseURL(Account), "api/shortlinks");

            // Whatever the dialog was asked for, sent as it was typed. Nothing here
            // decides whether a name is usable, whether an address is one, or how long an
            // expiry may be: those rules live in the server's lib/shortlinks.ts, which the
            // web panel and this endpoint share so that there is one copy of them. The
            // one that must not be duplicated is the refusal of javascript: and data:
            // targets - a shortener that redirects to those is an XSS vector on its own
            // domain, and a second implementation of that check is a second thing to fall
            // behind.
            ModlightShortenLinkBody body = new ModlightShortenLinkBody()
            {
                target = url
            };

            if (Options != null)
            {
                body.customCode = string.IsNullOrEmpty(Options.CustomCode) ? null : Options.CustomCode;
                body.expiresInDays = Options.ExpiresInDays;
                body.password = string.IsNullOrEmpty(Options.Password) ? null : Options.Password;

                // Sent only when it is being turned OFF. Absent means "the server's own
                // default", which is the prefixed, unprivileged choice - deciding it out
                // here would be this app having an opinion about a permission it cannot
                // see.
                body.useUserCode = Options.UseUserCode ? (bool?)null : false;
            }

            // NullValueHandling.Ignore, so an option nobody set is ABSENT rather than
            // null. The endpoint treats the two the same today, but "absent" is what its
            // contract is written in, and sending nulls would be relying on a leniency
            // that is not promised.
            string json = JsonConvert.SerializeObject(body, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });

            NameValueCollection headers = ModlightAPI.AuthHeaders(Account);

            string response = SendRequest(HttpMethod.POST, requestURL, json, RequestHelpers.ContentTypeJSON, headers: headers);

            if (string.IsNullOrEmpty(response))
            {
                ReportRefusal();
                return null;
            }

            ModlightShortenLinkResponse parsed = JsonConvert.DeserializeObject<ModlightShortenLinkResponse>(response);

            if (parsed == null || string.IsNullOrEmpty(parsed.url))
            {
                Errors.Add(Localization.Strings.Modlight_Invalid_response);
                return null;
            }

            return parsed.url;
        }

        /// <summary>
        /// Same job as the uploader's: a readable first line above the raw dump.
        /// </summary>
        /// <remarks>
        /// Driven by the server's own <c>reason</c> token first and the status code only
        /// as a fallback, because the reasons are the contract - the endpoint says so, and
        /// the web panel renders exactly these tokens out of its own message catalogue.
        /// Reading the status alone loses the difference between the three things that
        /// arrive as 400 ("that is not a URL", "that name cannot be used", "that name is
        /// too long"), and every one of them names a different fix.
        ///
        /// Nothing is invented for a reason this build has not heard of: an unknown token
        /// falls through to the status code, and the server's own sentence is appended
        /// underneath by WithServerDetail either way.
        /// </remarks>
        private void ReportRefusal()
        {
            ResponseInfo info = LastResponseInfo;

            if (info == null)
            {
                return;
            }

            string message = MessageForReason(ReasonOf(info)) ?? MessageForStatus((int)info.StatusCode);

            Errors.AddFirst(ModlightAPI.WithServerDetail(message, info));
        }

        /// <summary>The <c>reason</c> token out of the refusal body, or null.</summary>
        private static string ReasonOf(ResponseInfo info)
        {
            if (string.IsNullOrEmpty(info.ResponseText))
            {
                return null;
            }

            try
            {
                return (string)JToken.Parse(info.ResponseText).SelectToken("reason");
            }
            catch (Exception)
            {
                // A body that is not JSON at all - a proxy's error page. This runs while a
                // failure is already being reported and must not turn one error into two.
                return null;
            }
        }

        private static string MessageForReason(string reason)
        {
            switch (reason)
            {
                case "invalid-url":
                    return Localization.Strings.Modlight_Link_invalid_url;
                case "invalid-code":
                    return Localization.Strings.Modlight_Link_invalid_code;
                case "code-taken":
                    return Localization.Strings.Modlight_Address_taken;
                case "code-too-long":
                    return Localization.Strings.Modlight_Link_code_too_long;
                case "root-not-allowed":
                    return Localization.Strings.Modlight_Link_root_not_allowed;
                case "no-tag":
                    return Localization.Strings.Modlight_Link_no_tag;
                case "rate-limited":
                    return Localization.Strings.Modlight_Too_many_requests;
                default:
                    return null;
            }
        }

        /// <summary>
        /// The fallback, for a refusal that carried no reason this build knows.
        /// </summary>
        /// <remarks>
        /// The codes differ from POST /upload because this endpoint answers 401 for
        /// a token it does not know, where the upload path answers 404. Both mean
        /// the one thing the user can act on - sign in again - so they map to the
        /// same sentence. 403 is not that: the token is real and the account is
        /// known, and what is refused is this particular use of it.
        /// </remarks>
        private static string MessageForStatus(int status)
        {
            switch (status)
            {
                case 401:
                    return Localization.Strings.Modlight_Token_not_recognized;
                case 403:
                    return Localization.Strings.Modlight_Token_not_allowed;
                case 409:
                    return Localization.Strings.Modlight_Address_taken;
                case 429:
                    return Localization.Strings.Modlight_Too_many_requests;
                default:
                    return string.Format(Localization.Strings.Modlight_Server_refused, status);
            }
        }

        private class ModlightShortenLinkBody
        {
            /// <summary>The long URL. Refused by the server unless it is http(s).</summary>
            public string target { get; set; }

            /// <summary>The name the link should have, or absent for a generated one.</summary>
            public string customCode { get; set; }

            /// <summary>Days until it stops resolving, or absent for never.</summary>
            public int? expiresInDays { get; set; }

            /// <summary>A password for the gate page, or absent for none.</summary>
            public string password { get; set; }

            /// <summary>False to claim a name at the root; absent for the account's own namespace.</summary>
            public bool? useUserCode { get; set; }
        }

        private class ModlightShortenLinkResponse
        {
            /// <summary>The code on its own, without the host.</summary>
            public string code { get; set; }

            /// <summary>The absolute short link, already on this deployment's shortener domain.</summary>
            public string url { get; set; }
        }
    }
}
