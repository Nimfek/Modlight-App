using System;
using System.Globalization;

namespace ShareX
{
    /// <summary>
    /// Formatting for numbers that come from the Modlight server and are also
    /// shown in the web panel.
    /// </summary>
    /// <remarks>
    /// This exists because of a real mismatch the owner spotted: the app said
    /// "5.58 GB used" while the panel said "5.2 GB" for the same account. Neither
    /// was wrong on its own - the app divided by 1000 and the panel by 1024 - but
    /// one number shown two ways in two places is a bug regardless of which
    /// convention is nicer, because the only thing a person can conclude is that
    /// one of them is lying.
    ///
    /// The panel wins: it is where the quota is administered, and it is the number
    /// the user sees first. So this mirrors lib/format.ts exactly - 1024 steps,
    /// decimal labels, one decimal place.
    ///
    /// Local file sizes are NOT formatted through here. Those are the operating
    /// system's numbers, they follow the user's BinaryUnits preference, and they
    /// have no counterpart on a web page to disagree with.
    /// </remarks>
    public static class ModlightFormat
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>A byte count, spelled the way the panel spells it.</summary>
        public static string CloudSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            double n = bytes;
            int i = 0;

            // Deliberately 1024 with a decimal label. That pairing is what the
            // panel does; copying the arithmetic but "fixing" the label to GiB
            // would leave the two screens disagreeing again, in the other
            // direction.
            while (n >= 1024 && i < Units.Length - 1)
            {
                n /= 1024;
                i++;
            }

            // Whole bytes have no fractional part worth showing; everything above
            // gets one decimal, like the panel.
            string number = i == 0
                ? n.ToString("0", CultureInfo.CurrentCulture)
                : n.ToString("0.0", CultureInfo.CurrentCulture);

            return number + " " + Units[i];
        }
    }
}
