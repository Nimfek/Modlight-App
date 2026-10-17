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

#nullable enable

using ShareX.Localization;
using System;

namespace ShareX;

/// <summary>
/// Turning a queue reason into a sentence, at the moment it is read.
/// </summary>
/// <remarks>
/// One place, because there are two surfaces that have to say the same thing - the row in
/// the Upload tab and the notice that goes up when an item stops - and a queue whose list
/// and whose notification give different reasons for the same halt is worse than one that
/// gives none.
///
/// Built here rather than stored on the item, because the queue outlives the process and
/// the language can change between runs. A sentence written into Queue.json in August is
/// still in August's language in September.
///
/// <see cref="ModlightQueueItem.Detail"/> is whatever the reason needs and nothing more -
/// a number, a path, the server's own words - never a fragment of a sentence, so that the
/// two languages can put it wherever their grammar wants it.
/// </remarks>
internal static class ModlightQueueText
{
    /// <summary>Separates the two figures a quota message needs.</summary>
    public const char DetailSeparator = '|';

    public static string Sentence(ModlightQueueReason reason, string detail)
    {
        detail ??= "";

        switch (reason)
        {
            case ModlightQueueReason.NoNetwork:
                return Strings.UploadQueue_Reason_NoNetwork;

            case ModlightQueueReason.Unreachable:
                return Strings.UploadQueue_Reason_Unreachable;

            case ModlightQueueReason.ServerError:
                return detail.Length > 0
                    ? string.Format(Strings.UploadQueue_Reason_ServerError, detail)
                    : Strings.UploadQueue_Reason_Unreachable;

            case ModlightQueueReason.SignedOut:
                return Strings.UploadQueue_Reason_SignedOut;

            case ModlightQueueReason.AccountChanged:
                return string.Format(Strings.UploadQueue_Reason_AccountChanged, detail);

            case ModlightQueueReason.SourceMissing:
                return Strings.UploadQueue_Reason_SourceMissing;

            case ModlightQueueReason.OutOfSpace:
                return Quota(detail);

            case ModlightQueueReason.TokenRejected:
                return Strings.UploadQueue_Reason_TokenRejected;

            case ModlightQueueReason.TooLarge:
                return Strings.UploadQueue_Reason_TooLarge;

            case ModlightQueueReason.Refused:
                return detail.Length > 0
                    ? Strings.UploadQueue_Reason_Refused + " " + detail
                    : Strings.UploadQueue_Reason_Refused;

            case ModlightQueueReason.TooOld:
                return Strings.UploadQueue_Reason_TooOld;

            case ModlightQueueReason.GaveUp:
                return string.Format(Strings.UploadQueue_Reason_GaveUp, detail);

            default:
                return "";
        }
    }

    /// <summary>
    /// "There is no room: 1.4 GB needed, 300 MB free."
    /// </summary>
    /// <remarks>
    /// Both figures, because only one of them is actionable: knowing the account is full
    /// says nothing about whether deleting one screenshot will do or whether the whole
    /// thing is hopeless.
    /// </remarks>
    private static string Quota(string detail)
    {
        int split = detail.IndexOf(DetailSeparator);

        if (split <= 0 || split == detail.Length - 1)
        {
            return Strings.UploadQueue_Reason_OutOfSpaceShort;
        }

        return string.Format(Strings.UploadQueue_Reason_OutOfSpace,
            detail.Substring(0, split), detail.Substring(split + 1));
    }

    /// <summary>How long until the next attempt, in the same words the transfer rows use.</summary>
    public static string Countdown(DateTime nextAttemptUtc)
    {
        TimeSpan left = nextAttemptUtc - DateTime.UtcNow;

        if (left <= TimeSpan.Zero)
        {
            return Strings.UploadQueue_RetryImminent;
        }

        // The same ninety-second boundary the in-flight rows use, for the same reason:
        // "1 min" for something 61 seconds away rounds the wrong way every time. Its own
        // pair of strings rather than the transfer rows' "{0} s left", which already
        // carries a "left" that this sentence supplies itself.
        string when = left.TotalSeconds < 90
            ? string.Format(Strings.UploadQueue_InSeconds, (int)Math.Ceiling(left.TotalSeconds))
            : string.Format(Strings.UploadQueue_InMinutes, (int)Math.Ceiling(left.TotalMinutes));

        return string.Format(Strings.UploadQueue_RetryIn, when);
    }
}
