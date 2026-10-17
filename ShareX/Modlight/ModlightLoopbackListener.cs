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
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// What the authorize page posted to the loopback port: either a token or a refusal,
/// always carrying back the nonce the app sent it out with.
/// </summary>
internal sealed class ModlightHandoff
{
    [JsonProperty("state")]
    public string? State { get; set; }

    [JsonProperty("token")]
    public string? Token { get; set; }

    /// <summary>"access_denied" when the person said no in the browser.</summary>
    [JsonProperty("error")]
    public string? Error { get; set; }
}

/// <summary>
/// The desktop half of the RFC 8252 loopback redirect: a socket on 127.0.0.1 that
/// waits for the authorize page to hand over one JSON body, answers it, and stops.
/// </summary>
/// <remarks>
/// Raw TCP rather than HttpListener for two reasons. The port has to be assigned by
/// the OS, and HttpListener cannot bind port 0 - it would have to be told a number
/// found by binding a socket and letting it go again, which is a race that hands the
/// sign-in to whatever grabbed the number in between. And http.sys registration can
/// fail with "access denied" on a locked-down machine, which would turn signing in
/// into a support ticket. A single POST from a page we control is not enough HTTP to
/// justify either risk.
///
/// The listener deliberately knows nothing about which nonce is the right one: it
/// reports what arrived, and <see cref="ModlightSignIn"/> decides whether to believe it.
/// </remarks>
internal sealed class ModlightLoopbackListener : IDisposable
{
    /// <summary>The port the authorize URL has to point at.</summary>
    public int Port { get; }

    // Everything on this socket comes from a local process, which is exactly why
    // both are bounded: a body that never ends would otherwise be a way to make
    // the app eat memory until it dies.
    private const int MaxHeaderBytes = 8 * 1024;
    private const int MaxBodyBytes = 64 * 1024;

    // A connection that opens and then says nothing must not stall the wait for the
    // real one, so each is given its own short deadline.
    private static readonly TimeSpan connectionTimeout = TimeSpan.FromSeconds(10);

    private static readonly byte[] headerTerminator = Encoding.ASCII.GetBytes("\r\n\r\n");

    private readonly TcpListener listener;
    private bool disposed;

    public ModlightLoopbackListener()
    {
        // Port 0 means "any free port", and the socket stays bound from here until
        // Dispose - the port is never released and re-acquired.
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Wait for the page to deliver. Returns the first well-formed body that arrives;
    /// throws <see cref="OperationCanceledException"/> when the caller's deadline runs out.
    /// </summary>
    /// <remarks>
    /// Anything else that connects - a port scanner, a browser probing the port, a
    /// request that stops halfway - is answered and forgotten, and the wait continues.
    /// The one thing that ends the wait early is a body we can read.
    /// </remarks>
    public async Task<ModlightHandoff> WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken))
            {
                ModlightHandoff? handoff = null;

                try
                {
                    handoff = await ReadHandoffAsync(client, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // A connection that died mid-request says nothing about the sign-in.
                    DebugHelper.WriteException(exception);
                }

                if (handoff != null)
                {
                    return handoff;
                }
            }
        }
    }

    private static async Task<ModlightHandoff?> ReadHandoffAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(connectionTimeout);
        CancellationToken token = deadline.Token;

        NetworkStream stream = client.GetStream();

        using MemoryStream received = new MemoryStream();
        byte[] buffer = new byte[4096];
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(buffer, token);

            if (read <= 0)
            {
                return null;
            }

            received.Write(buffer, 0, read);

            if (received.Length > MaxHeaderBytes)
            {
                return null;
            }

            headerEnd = IndexOf(received.GetBuffer(), (int)received.Length, headerTerminator);
        }

        byte[] data = received.GetBuffer();
        int length = (int)received.Length;

        // Request line and headers are ASCII by definition; only the body may not be.
        string head = Encoding.ASCII.GetString(data, 0, headerEnd);
        string method = ReadMethod(head);

        if (method == "OPTIONS")
        {
            // The page sends a CORS-simple request, so no preflight is expected. This
            // answers one anyway because Chrome's private-network rules have been
            // moving for years, and the failure mode without it is invisible: the POST
            // is never sent and the app just sits there until it times out. Nothing is
            // read from a preflight, so allowing it costs nothing.
            await WriteAsync(stream, "HTTP/1.1 204 No Content\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: POST\r\n" +
                "Access-Control-Allow-Headers: content-type\r\n" +
                "Access-Control-Allow-Private-Network: true\r\n" +
                "Access-Control-Max-Age: 600\r\n" +
                "Connection: close\r\n\r\n", token);

            return null;
        }

        if (method != "POST")
        {
            await WriteAsync(stream, "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token);
            return null;
        }

        int contentLength = ReadContentLength(head);

        if (contentLength <= 0 || contentLength > MaxBodyBytes)
        {
            await WriteAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token);
            return null;
        }

        int bodyStart = headerEnd + headerTerminator.Length;
        byte[] body = new byte[contentLength];
        int copied = Math.Min(contentLength, length - bodyStart);
        Buffer.BlockCopy(data, bodyStart, body, 0, copied);

        while (copied < contentLength)
        {
            int read = await stream.ReadAsync(body.AsMemory(copied, contentLength - copied), token);

            if (read <= 0)
            {
                return null;
            }

            copied += read;
        }

        // 204 before parsing, and no Content-Length with it (RFC 9110 forbids one on a
        // 204): the page cannot read this reply anyway - it fetches with mode "no-cors"
        // - so all it learns is that the bytes arrived. Answering fast is the point.
        await WriteAsync(stream, "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n", token);

        try
        {
            return JsonConvert.DeserializeObject<ModlightHandoff>(Encoding.UTF8.GetString(body));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadMethod(string head)
    {
        int space = head.IndexOf(' ');
        return space > 0 ? head.Substring(0, space) : "";
    }

    private static int ReadContentLength(string head)
    {
        foreach (string line in head.Split('\n'))
        {
            int colon = line.IndexOf(':');

            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("content-length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line.AsSpan(colon + 1).Trim(), out int value))
            {
                return value;
            }
        }

        return 0;
    }

    private static async Task WriteAsync(NetworkStream stream, string response, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static int IndexOf(byte[] data, int length, byte[] pattern)
    {
        for (int i = 0; i <= length - pattern.Length; i++)
        {
            int match = 0;

            while (match < pattern.Length && data[i + match] == pattern[match])
            {
                match++;
            }

            if (match == pattern.Length)
            {
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        try
        {
            listener.Stop();
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }
}
