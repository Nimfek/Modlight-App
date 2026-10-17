#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

namespace ShareX.UploadersLib
{
    /// <summary>
    /// What the person asked for when they shortened one particular URL.
    /// </summary>
    /// <remarks>
    /// Everything here is optional, and every one of them is a field POST /api/shortlinks
    /// already accepts - the panel's own form sends exactly these. Nothing in this class
    /// decides whether a value is acceptable: the rules live in the server's
    /// lib/shortlinks.ts, which the panel and the HTTP endpoint share precisely so that
    /// there is one copy of them. A second copy in C# would drift, and the one that
    /// matters (refusing javascript: and data: targets) would become a hole the day it
    /// drifted.
    ///
    /// Held for the life of one task and never written to disk. The password in
    /// particular is a credential somebody typed for a single link; persisting it in
    /// ApplicationConfig.json to save re-typing would be storing a shared secret in
    /// plaintext for a convenience nobody asked for.
    /// </remarks>
    public sealed class ModlightShortLinkOptions
    {
        /// <summary>The name the link should have, or null to let the server generate one.</summary>
        public string CustomCode { get; set; }

        /// <summary>Days until the link stops resolving, or null for never.</summary>
        public int? ExpiresInDays { get; set; }

        /// <summary>A password visitors must enter before the redirect, or null for none.</summary>
        public string Password { get; set; }

        /// <summary>
        /// Put the account's tag in front of the code, giving it its own namespace.
        /// </summary>
        /// <remarks>
        /// True by default because that is the server's default and the unprivileged
        /// choice. Turning it off claims a name at the root of the shortener, which only
        /// an admin may do - and the server, not this class, is what says so: it answers
        /// <c>root-not-allowed</c>, which the shortener turns into a sentence.
        /// </remarks>
        public bool UseUserCode { get; set; } = true;

        /// <summary>Whether any of this differs from just sending the URL on its own.</summary>
        public bool HasAny =>
            !string.IsNullOrEmpty(CustomCode) ||
            ExpiresInDays.HasValue ||
            !string.IsNullOrEmpty(Password) ||
            !UseUserCode;
    }
}
