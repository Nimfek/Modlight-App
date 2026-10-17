#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Newtonsoft.Json;
using ShareX.HelpersLib;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>How much room is left on the account. <c>Max</c> and <c>Remaining</c> are null when there is no limit.</summary>
public sealed class ModlightQuota
{
    [JsonProperty("used")]
    public long Used { get; set; }

    [JsonProperty("max")]
    public long? Max { get; set; }

    [JsonProperty("remaining")]
    public long? Remaining { get; set; }

    [JsonProperty("unlimited")]
    public bool Unlimited { get; set; }
}

/// <summary>The answer to GET /api/me: who a token belongs to.</summary>
public sealed class ModlightAccountInfo
{
    /// <summary>The account's short code, which its links are built from.</summary>
    [JsonProperty("tag")]
    public string? Tag { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("email")]
    public string? Email { get; set; }

    /// <summary>The hostname this account's links are built on, which need not be the one we asked.</summary>
    /// <summary>
    /// Absolute URL of the account's picture, or null when it has none.
    /// </summary>
    /// <remarks>
    /// Absolute because the server builds it that way - the panel answers on several
    /// domains and a root-relative avatar would be a cross-origin request on the others -
    /// but it is built on the LINK host, which for this account can be a vanity domain
    /// that this client has no token for. Every fetch is rewritten onto the origin we
    /// signed in to before it goes out; see ModlightLibrary.GetThumbnailAsync.
    /// </remarks>
    [JsonProperty("avatar")]
    public string? Avatar { get; set; }

    [JsonProperty("host")]
    public string? Host { get; set; }

    [JsonProperty("quota")]
    public ModlightQuota? Quota { get; set; }

    [JsonProperty("defaultFolderId")]
    public string? DefaultFolderId { get; set; }

    /// <summary>Whether new uploads are private until the account publishes them.</summary>
    /// <remarks>
    /// Shown by the account section and never acted on: the panel is the only thing that can
    /// change it, so a client that treated this as a local preference would be inventing one.
    /// False on a server too old to send it, which reads as "public", the panel default.
    /// </remarks>
    [JsonProperty("hideUploads")]
    public bool HideUploads { get; set; }

    /// <summary>Whether the link this account copies is the raw file rather than its page.</summary>
    /// <remarks>
    /// For DISPLAY only. What actually gets copied comes back per upload in <c>copy_url</c>,
    /// which the server builds from this same preference - so the two cannot drift, and this
    /// one must never be used to decide a link on the client.
    /// </remarks>
    [JsonProperty("copyRaw")]
    public bool CopyRaw { get; set; }

    /// <summary>
    /// What this account is on the server: <c>"admin"</c>, or whatever else it stored.
    /// </summary>
    /// <remarks>
    /// Read by <see cref="ModlightAdmin"/> and used for one thing only - deciding whether
    /// to OFFER administration UI. It is not a permission and cannot be used as one; the
    /// server checks the role again on every call, and a client that lied to itself here
    /// would earn a screen full of refusals rather than access to anything.
    ///
    /// Null when the server is older than this field. That reads as "not an administrator",
    /// which is the right way for an unknown to fail.
    /// </remarks>
    [JsonProperty("role")]
    public string? Role { get; set; }
}

/// <summary>Talking to a Modlight server with an API token.</summary>
public static class ModlightApi
{
    /// <summary>Long enough for a cold server, short enough that nobody wonders whether it hung.</summary>
    private static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Turn whatever the user typed into an origin: scheme and authority, nothing else.
    /// </summary>
    /// <remarks>
    /// Every endpoint in the contract hangs off the root, so a pasted "/dashboard" or a
    /// trailing slash has to be dropped rather than concatenated into a 404. A bare
    /// "files.example.com" gets https, because the alternative is sending somebody's token
    /// over plaintext for the sake of two saved keystrokes.
    /// </remarks>
    public static bool TryNormalizeHost(string? input, out string host)
    {
        host = "";
        string value = input?.Trim() ?? "";

        if (value.Length == 0)
        {
            return false;
        }

        if (!value.Contains("://"))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        // Authority, not the original string: it drops any userinfo, path, query and
        // fragment, and normalises the default port away.
        host = uri.Scheme + "://" + uri.Authority;
        return true;
    }

    /// <summary>
    /// Whether a string can be sent as a header value at all.
    /// </summary>
    /// <remarks>
    /// The manual paste box is the one place a token arrives as free text, and a value
    /// with a newline in it is a request header of somebody else's choosing. Visible
    /// ASCII only - which every token this server has ever issued already is.
    /// </remarks>
    public static bool IsPlausibleToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token!.Length > 512)
        {
            return false;
        }

        foreach (char c in token)
        {
            if (c <= ' ' || c > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Ask the server who this token belongs to.
    /// </summary>
    /// <returns>
    /// The account, or null when the server answered but refused the token. Anything
    /// that stopped the request from completing - no route, no DNS, bad certificate -
    /// comes back as an exception, because "your token is wrong" and "your server is
    /// unreachable" are different things to tell a person.
    /// </returns>
    public static async Task<ModlightAccountInfo?> GetAccountAsync(string host, string token, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, host + "/api/me");
        request.Headers.TryAddWithoutValidation("x-api-key", token);

        using HttpResponseMessage response = await HttpClientFactory.Create().SendAsync(request, deadline.Token);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            // The credential is no longer good for anything, which includes whatever this
            // token was last told about its own role.
            ModlightAdmin.Forget();
            return null;
        }

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(deadline.Token);
        ModlightAccountInfo? account = JsonConvert.DeserializeObject<ModlightAccountInfo>(json);

        if (account != null)
        {
            // Recorded HERE because this method is the only way anything in the app reaches
            // /api/me - the sign-in flow, the account strip's twenty-second refresh and the
            // connection test all come through it. One choke point means the answer cannot
            // be a session out of date on one screen and current on another, and it means a
            // role taken away on the website is dropped by the next refresh rather than
            // living until the app is restarted.
            ModlightAdmin.Record(host, account);
        }

        return account;
    }
}
