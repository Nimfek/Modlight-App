#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Newtonsoft.Json;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>The answer from GET and PUT /api/theme.</summary>
public sealed class ModlightThemeInfo
{
    /// <summary>The skin id the account holds, already resolved by the server.</summary>
    [JsonProperty("theme")]
    public string? Theme { get; set; }
}

/// <summary>
/// The account's skin, kept in step with the website.
/// </summary>
/// <remarks>
/// One column on the server (<c>user.theme</c>) decides what the panel looks like, what a
/// shared link looks like to whoever opens it, and - now - what this app looks like. The
/// owner asked for the sync to run both ways, and it does: a skin picked here is PUT
/// immediately, and a skin picked on the website is adopted the next time this app asks.
///
/// Which side wins when both changed is settled by <see cref="ApplicationThemeOptions.SkinConfirmed"/>
/// rather than by a timestamp. A clock comparison between a desktop and a server is a
/// question about two clocks; "did the last local change ever reach the server" is a
/// question about this installation, and it is the one that actually matters.
/// </remarks>
public static class ModlightTheme
{
    private static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Read the skin the account holds, or null if the server would not say.</summary>
    public static async Task<string?> GetAsync(string host, string token, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, host + "/api/theme");
        return await SendAsync(request, token, cancellationToken);
    }

    /// <summary>Tell the server which skin the account now holds.</summary>
    /// <returns>The skin the server ended up with, or null when it refused.</returns>
    public static async Task<string?> SetAsync(string host, string token, string skin, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, host + "/api/theme")
        {
            Content = new StringContent(
                JsonConvert.SerializeObject(new { theme = skin }),
                Encoding.UTF8,
                "application/json")
        };
        return await SendAsync(request, token, cancellationToken);
    }

    /// <remarks>
    /// The token goes in the <c>x-api-key</c> HEADER, never in the request line, for the
    /// same reason it does everywhere else in this client: a query string is written to
    /// the reverse proxy's access log.
    ///
    /// A refusal comes back as null rather than as an exception. There is exactly one
    /// caller and it treats "the server said no" and "the server said nothing" the same
    /// way - keep the skin we have - so distinguishing them here would only add a code
    /// path nobody reads.
    /// </remarks>
    private static async Task<string?> SendAsync(HttpRequestMessage request, string token, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);

        request.Headers.TryAddWithoutValidation("x-api-key", token);

        using HttpResponseMessage response = await HttpClientFactory.Create().SendAsync(request, deadline.Token);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string json = await response.Content.ReadAsStringAsync(deadline.Token);
        return JsonConvert.DeserializeObject<ModlightThemeInfo>(json)?.Theme;
    }

    /// <summary>
    /// Bring this installation's skin and the account's into agreement, once.
    /// </summary>
    /// <remarks>
    /// Called when a token lands (sign-in) and once when the app starts already signed in.
    /// Not on a timer and not on every account refresh: the account strip re-reads itself
    /// every twenty seconds while an upload is running, and a skin that could change under
    /// you mid-upload is a worse thing than a skin that is a session out of date.
    ///
    /// Direction is decided before anything is sent. If this installation is holding a
    /// change the server has never confirmed - picked while offline, or picked while the
    /// server was down - that change is pushed. Otherwise the server's value is adopted.
    /// The one thing this must never do is silently discard a choice somebody made.
    /// </remarks>
    public static async Task SyncAsync(CancellationToken cancellationToken)
    {
        ModlightAccount? account = ModlightSignIn.Current;
        ApplicationThemeOptions? options = Program.Settings?.ThemeOptions;

        if (account == null || options == null)
        {
            return;
        }

        try
        {
            if (!string.Equals(options.Skin, options.SkinConfirmed, StringComparison.OrdinalIgnoreCase))
            {
                string? saved = await SetAsync(account.Host, account.Token, options.Skin, cancellationToken);
                if (saved != null)
                {
                    options.SkinConfirmed = saved;
                    SettingManager.SaveApplicationConfigAsync();
                }

                return;
            }

            string? remote = await GetAsync(account.Host, account.Token, cancellationToken);
            if (remote == null)
            {
                return;
            }

            // Recorded even when this build cannot paint it. The id is what the server
            // holds; an older client's inability to draw a skin is not a reason to
            // report the account as out of sync forever, nor to push the old value back
            // over a choice the user made on the website.
            options.SkinConfirmed = remote;

            if (SkinVariants.IsKnown(remote))
            {
                // Repaints at once: ThemeManager listens to this object. Writing the id
                // is not enough on its own - without the save, a skin adopted from the
                // website would be forgotten by the next start, and a start with no
                // network would put the old one back.
                options.Skin = remote;
            }

            SettingManager.SaveApplicationConfigAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // A skin is not worth a dialog. The app keeps the one it has and tries again
            // next time it is started or signed in to.
            DebugHelper.WriteException(exception);
        }
    }

    /// <summary>
    /// Push the skin now on this installation, after the user picked it.
    /// </summary>
    /// <remarks>
    /// Failure is deliberately quiet AND deliberately remembered: <c>SkinConfirmed</c> is
    /// only advanced when the server answered, so an unreachable server turns into a push
    /// on the next start rather than into a choice that evaporates.
    /// </remarks>
    public static async Task PushAsync(CancellationToken cancellationToken)
    {
        ModlightAccount? account = ModlightSignIn.Current;
        ApplicationThemeOptions? options = Program.Settings?.ThemeOptions;

        if (account == null || options == null || string.Equals(options.Skin, options.SkinConfirmed, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            string? saved = await SetAsync(account.Host, account.Token, options.Skin, cancellationToken);
            if (saved != null)
            {
                options.SkinConfirmed = saved;
                SettingManager.SaveApplicationConfigAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }
}
