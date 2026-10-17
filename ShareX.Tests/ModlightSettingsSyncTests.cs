#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;

namespace ShareX.Tests;

/// <summary>
/// Who wins when this machine's settings and the account's disagree.
/// </summary>
/// <remarks>
/// <para>
/// Worth a suite because every failure here is silent and destroys work. A rule that lets
/// a fresh install push replaces a configuration built up over months with factory
/// defaults, on every machine on the account at once, and the person finds out when a
/// hotkey stops working. A rule that always adopts throws away whatever somebody just
/// changed at this keyboard. Neither produces an error message; both are only visible as a
/// decision, which is exactly what this checks.
/// </para>
/// <para>
/// <see cref="ModlightSettingsPlan"/> is compiled into this assembly rather than referenced
/// so the decision can be exercised with no network, no config files and no Avalonia - and
/// so that it stays free of all three. If somebody reaches for an HTTP call or a settings
/// object inside it, this project stops compiling and says so.
/// </para>
/// </remarks>
internal static class ModlightSettingsSyncTests
{
    private static int failures;

    /// <summary>An account key, as the real code builds one.</summary>
    private static readonly string Account = ModlightSettingsPlan.AccountKey("https://host.example.com", "ToqOse");

    private static readonly string Other = ModlightSettingsPlan.AccountKey("https://host.example.com", "someone-else");

    private const string Ours = "aaaa";
    private const string Theirs = "bbbb";
    private const string Older = "cccc";

    internal static int Run()
    {
        failures = 0;

        Console.WriteLine("Settings sync decisions");

        FreshInstallNeverPushes();
        FirstMachineSeeds();
        QuietMachineAdopts();
        ChangedMachinePushes();
        AgreementIsCheap();
        AnotherAccountsMarkerIsNotEvidence();

        Console.WriteLine(failures == 0
            ? "  all settings sync checks passed"
            : "  " + failures + " settings sync check(s) FAILED");

        return failures;
    }

    /// <summary>
    /// The one that matters most: defaults must never replace a real configuration.
    /// </summary>
    /// <remarks>
    /// A machine that has just been installed holds whatever the installer wrote, and it
    /// cannot tell that from settings somebody chose - it has no marker to compare
    /// against. It differs from the server, so a naive "we differ, so send ours" would
    /// upload the defaults over the account's real configuration on first launch.
    /// </remarks>
    private static void FreshInstallNeverPushes()
    {
        ModlightSyncPlan plan = ModlightSettingsPlan.Decide(
            Account, confirmedAccount: "", confirmedVersion: 0, confirmedDigest: "",
            localDigest: Ours, remoteVersion: 7, remoteDigest: Theirs);

        Check(plan.Action == ModlightSyncAction.Adopt, "a never-confirmed installation adopts instead of pushing");
        Check(plan.Version == 7, "and adopts the version the server actually reported");
    }

    /// <summary>But it does seed an account that has nothing stored yet.</summary>
    /// <remarks>
    /// Nothing can be lost by going first, and somebody has to. The expected version of 0
    /// is the assertion "and there had better still be nothing", so two machines seeding at
    /// the same moment end with one refused rather than one silently overwritten.
    /// </remarks>
    private static void FirstMachineSeeds()
    {
        ModlightSyncPlan plan = ModlightSettingsPlan.Decide(
            Account, confirmedAccount: "", confirmedVersion: 0, confirmedDigest: "",
            localDigest: Ours, remoteVersion: 0, remoteDigest: "");

        Check(plan.Action == ModlightSyncAction.Seed, "an empty account is seeded by whoever gets there first");
        Check(plan.Version == 0, "and the write asserts that nothing was stored");
    }

    /// <summary>A machine holding no change of its own takes what the account has.</summary>
    private static void QuietMachineAdopts()
    {
        ModlightSyncPlan plan = ModlightSettingsPlan.Decide(
            Account, Account, confirmedVersion: 4, confirmedDigest: Ours,
            localDigest: Ours, remoteVersion: 5, remoteDigest: Theirs);

        Check(plan.Action == ModlightSyncAction.Adopt, "a clean machine adopts the newer document");
        Check(plan.Version == 5, "at the version it just read");
    }

    /// <summary>
    /// A machine holding an unsent change wins, even against a server that moved.
    /// </summary>
    /// <remarks>
    /// Asymmetry of loss: what this machine holds has never left it, so adopting destroys
    /// the only copy. What it replaces was pushed by an installation that still has it.
    /// The expected version is the one just READ, so a third machine writing in between is
    /// still refused - that write is one nobody here has looked at.
    /// </remarks>
    private static void ChangedMachinePushes()
    {
        ModlightSyncPlan quiet = ModlightSettingsPlan.Decide(
            Account, Account, confirmedVersion: 5, confirmedDigest: Older,
            localDigest: Ours, remoteVersion: 5, remoteDigest: Older);

        Check(quiet.Action == ModlightSyncAction.Push, "an unsent local change is pushed");
        Check(quiet.Version == 5, "replacing the version it was based on");

        ModlightSyncPlan contested = ModlightSettingsPlan.Decide(
            Account, Account, confirmedVersion: 5, confirmedDigest: Older,
            localDigest: Ours, remoteVersion: 9, remoteDigest: Theirs);

        Check(contested.Action == ModlightSyncAction.Push, "and still pushed when the server moved as well");
        Check(contested.Version == 9, "against the version just read, not the one confirmed");
    }

    /// <summary>Two sides holding the same document exchange nothing.</summary>
    private static void AgreementIsCheap()
    {
        ModlightSyncPlan behind = ModlightSettingsPlan.Decide(
            Account, Account, confirmedVersion: 4, confirmedDigest: Older,
            localDigest: Ours, remoteVersion: 6, remoteDigest: Ours);

        Check(behind.Action == ModlightSyncAction.Confirm, "an identical document only moves the marker");
        Check(behind.Version == 6, "to the version the server holds");

        ModlightSyncPlan settled = ModlightSettingsPlan.Decide(
            Account, Account, confirmedVersion: 6, confirmedDigest: Ours,
            localDigest: Ours, remoteVersion: 6, remoteDigest: Ours);

        Check(settled.Action == ModlightSyncAction.Nothing, "and nothing at all once the marker agrees");
    }

    /// <summary>
    /// A marker written for another account cannot vouch for this one.
    /// </summary>
    /// <remarks>
    /// Signing out and in as somebody else leaves a version number and a digest behind. If
    /// those still counted, the new account's settings would look like something this
    /// machine had already confirmed - and the previous person's configuration would be
    /// pushed into it.
    /// </remarks>
    private static void AnotherAccountsMarkerIsNotEvidence()
    {
        ModlightSyncPlan plan = ModlightSettingsPlan.Decide(
            Account, Other, confirmedVersion: 12, confirmedDigest: Ours,
            localDigest: Ours, remoteVersion: 3, remoteDigest: Theirs);

        Check(plan.Action == ModlightSyncAction.Adopt, "a marker from another account is discarded, so the cloud wins");

        ModlightSyncPlan sameAccount = ModlightSettingsPlan.Decide(
            Account, Account, confirmedVersion: 12, confirmedDigest: Ours,
            localDigest: Theirs, remoteVersion: 3, remoteDigest: Older);

        Check(sameAccount.Action == ModlightSyncAction.Push, "while the same account's marker still counts");
    }

    private static void Check(bool condition, string what)
    {
        if (condition)
        {
            Console.WriteLine("  ok   " + what);
            return;
        }

        failures++;
        Console.WriteLine("  FAIL " + what);
    }
}
