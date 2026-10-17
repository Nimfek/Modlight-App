#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

namespace ShareX;

/// <summary>
/// Where a finished download ends up.
/// </summary>
/// <remarks>
/// One setting for the whole tab, not a choice per queued item - the owner asked for
/// exactly that (decision item 13). It is the right shape as well: somebody who wants
/// their downloads on the server wants ALL of them there, and a per-item switch would
/// be four extra clicks a day to answer the same question the same way.
///
/// Serialised into ApplicationConfig, so the order of these members is part of the file
/// format. Append, never insert.
/// </remarks>
public enum DownloadDestination
{
    /// <summary>Keep the file on this PC and do nothing else. The default.</summary>
    Disk,

    /// <summary>Upload it and put the link on the clipboard, the way a capture goes.</summary>
    Modlight,

    /// <summary>Both: the file stays here and a copy goes up.</summary>
    Both
}
