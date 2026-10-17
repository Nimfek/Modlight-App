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

using Newtonsoft.Json;
using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ShareX;

/// <summary>
/// Where the queue lives between runs.
/// </summary>
/// <remarks>
/// A file of its own rather than a section of ApplicationConfig.json, and that is the
/// whole design. The application config is written on a schedule and on exit; the queue
/// has to be right after a power cut, because "the laptop lid closed" is the case it
/// exists for. So it is written the instant anything about it changes, and it is written
/// somewhere a corrupt read costs nothing but the queue - a queue file that fails to
/// parse must never be able to take the user's settings, hotkeys or account down with it.
///
/// Written whole and swapped in, never edited in place: a process killed halfway through
/// a rewrite would otherwise leave a truncated file, and the next launch would read it as
/// "the queue is empty" - silently losing exactly the uploads this is meant to protect.
/// </remarks>
internal static class ModlightQueueStore
{
    private const string QueueFileName = "Queue.json";

    /// <summary>Where content that exists nowhere else is parked.</summary>
    private const string SpoolFolderName = "Queue";

    /// <summary>
    /// Bumped only when the shape changes in a way an older build cannot read.
    /// </summary>
    /// <remarks>
    /// An unrecognised version is treated as an empty queue rather than as an error: a
    /// downgrade must not be able to wedge the app, and the files it points at are still
    /// on disk either way.
    /// </remarks>
    private const int CurrentVersion = 1;

    private static readonly object fileLock = new();

    /// <summary>Null in sandbox mode, where nothing is written to disk at all.</summary>
    public static string? QueueFilePath =>
        Program.Sandbox ? null : Path.Combine(Program.PersonalFolder, QueueFileName);

    public static string? SpoolFolder =>
        Program.Sandbox ? null : Path.Combine(Program.PersonalFolder, SpoolFolderName);

    private sealed class QueueFile
    {
        public int Version { get; set; }
        public List<ModlightQueueItem>? Items { get; set; }
    }

    /// <summary>
    /// Read the queue back.
    /// </summary>
    /// <remarks>
    /// Every failure here answers "nothing queued". The alternative - refusing to start,
    /// or putting a dialog in front of somebody who has just double-clicked an icon - would
    /// make a damaged bookkeeping file into a broken application, and the bookkeeping file
    /// is the least valuable thing in the folder.
    /// </remarks>
    public static List<ModlightQueueItem> Load()
    {
        string? path = QueueFilePath;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return new List<ModlightQueueItem>();
        }

        try
        {
            string json;

            lock (fileLock)
            {
                json = File.ReadAllText(path);
            }

            QueueFile? file = JsonConvert.DeserializeObject<QueueFile>(json);

            if (file?.Items == null || file.Version != CurrentVersion)
            {
                return new List<ModlightQueueItem>();
            }

            List<ModlightQueueItem> items = file.Items.Where(item => item != null && item.IsUsable()).ToList();

            foreach (ModlightQueueItem item in items)
            {
                item.Revive();
            }

            return items;
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Modlight queue could not be read; starting with an empty one.");
            return new List<ModlightQueueItem>();
        }
    }

    /// <summary>Write the queue out, whole.</summary>
    public static void Save(IEnumerable<ModlightQueueItem> items)
    {
        string? path = QueueFilePath;

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            QueueFile file = new() { Version = CurrentVersion, Items = items.ToList() };
            string json = JsonConvert.SerializeObject(file, Formatting.Indented);

            lock (fileLock)
            {
                // Directory.CreateDirectory rather than the FileHelpers wrapper: that one
                // puts a modal dialog up when it cannot create the folder, and this runs on
                // a background thread that must never be able to stop and wait for somebody
                // to press OK. A failure here is already handled below - the queue is still
                // right in memory and tries again on the next change.
                string? folder = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string temporary = path + ".tmp";
                File.WriteAllText(temporary, json);

                // Move over the top rather than delete-then-write. The window between a
                // delete and a write is a window in which the queue does not exist, and a
                // machine that loses power in it comes back with the uploads gone.
                File.Move(temporary, path, true);
            }
        }
        catch (Exception exception)
        {
            // Nothing to tell the user here. The queue is still correct in memory and
            // will be written again on the next change; a notification per failed write
            // on a locked folder would be a stream of them.
            DebugHelper.WriteException(exception, "Modlight queue could not be written.");
        }
    }

    /// <summary>Park bytes that exist nowhere else, and answer with the path they now live at.</summary>
    public static string Spool(string itemId, string fileName, Stream data)
    {
        string? folder = SpoolFolder;

        if (string.IsNullOrEmpty(folder))
        {
            throw new IOException("No spool folder in sandbox mode.");
        }

        Directory.CreateDirectory(folder);

        // The id names the file, and the original extension is kept on the end. The id
        // is what makes it collision-proof; the extension is what makes the folder
        // readable by a person who finds it and wonders what these are.
        string extension = FileHelpers.GetFileNameExtension(fileName, true);
        string path = Path.Combine(folder, itemId + extension);

        using (FileStream target = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (data.CanSeek)
            {
                data.Position = 0;
            }

            data.CopyTo(target);
        }

        return path;
    }

    /// <summary>Delete one spooled file. Only ever called for files the queue created.</summary>
    public static void DiscardSpool(ModlightQueueItem item)
    {
        if (!item.OwnsSource || string.IsNullOrEmpty(item.SourcePath))
        {
            return;
        }

        try
        {
            if (File.Exists(item.SourcePath))
            {
                File.Delete(item.SourcePath);
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Modlight queue could not delete a spooled file.");
        }
    }

    /// <summary>
    /// Delete spooled files no item points at any more.
    /// </summary>
    /// <remarks>
    /// Run once at startup, against the queue as it was just read. A crash between
    /// spooling a file and writing the queue leaves a file nothing will ever look at, and
    /// there is no other moment at which that can be recognised: during a run, a file with
    /// no item is a file being spooled right now.
    /// </remarks>
    public static void SweepSpool(IEnumerable<ModlightQueueItem> items)
    {
        string? folder = SpoolFolder;

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            HashSet<string> keep = new(
                items.Where(item => item.OwnsSource && !string.IsNullOrEmpty(item.SourcePath))
                     .Select(item => item.SourcePath),
                StringComparer.OrdinalIgnoreCase);

            foreach (string path in Directory.GetFiles(folder))
            {
                if (keep.Contains(path))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                    DebugHelper.WriteLine("Modlight queue: removed an orphaned spooled file.");
                }
                catch (Exception exception)
                {
                    DebugHelper.WriteException(exception);
                }
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }
}
