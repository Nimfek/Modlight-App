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

using Avalonia.Media;
using System;

namespace ShareX.AvaloniaUI.Theming
{
    /// <summary>
    /// Which colour to write text in on top of the accent.
    /// </summary>
    /// <remarks>
    /// Its own file rather than three private methods on <see cref="ThemeManager"/>,
    /// because the answer is checkable arithmetic and the old one was wrong by a factor
    /// of two: it chose between #D8DADB and #4E4E4E, two mid-greys, and against the
    /// dark-mode default accent (#7E88FB) the winner measured 2.20:1 - less than half the
    /// 4.5:1 that ordinary text has to clear. Two separate mistakes made that happen, and
    /// both are gone here:
    ///
    /// 1. The candidates were mid-greys. A grey can only be readable on an accent that is
    ///    far from it in luminance, and a mid-grey is far from nothing. The candidates are
    ///    now the palette's own near-white and near-black, passed in by the caller.
    /// 2. The tie-break demanded the dark candidate beat the light one by 75% before it
    ///    would be used. Against #7E88FB the dark grey scored 2.21 and the light grey
    ///    2.20, so the bias handed the job to the loser by a hair. There is no bias now:
    ///    the better number wins.
    ///
    /// The luminance itself was never the bug - it was already WCAG's sRGB formula, and it
    /// still is. What changed is what it gets asked about.
    /// </remarks>
    internal static class AccentForeground
    {
        /// <summary>WCAG AA for text below 18pt, which is all the text this applies to.</summary>
        public const double MinimumContrast = 4.5;

        /// <summary>
        /// The more readable of two candidates over an accent that runs from
        /// <paramref name="start"/> to <paramref name="end"/>.
        /// </summary>
        /// <remarks>
        /// Judged against BOTH ends because the accent is drawn as a gradient and one
        /// label lies across the whole of it: a colour that is fine at the light end and
        /// unreadable at the dark one has to lose to a colour that is merely good at both.
        /// </remarks>
        public static Color Choose(Color start, Color end, Color light, Color dark)
        {
            double lightContrast = Against(light, start, end);
            double darkContrast = Against(dark, start, end);

            Color best = darkContrast > lightContrast ? dark : light;
            double bestContrast = Math.Max(lightContrast, darkContrast);

            if (bestContrast >= MinimumContrast)
            {
                return best;
            }

            // Neither palette colour is readable on this accent, which a user who typed
            // their own hex can arrange. Pure white and pure black are the most contrast
            // any solid foreground can reach, so the palette is what gets given up here,
            // not the text.
            //
            // This does not always reach 4.5:1 and cannot: an accent whose relative
            // luminance sits near 0.2 is too dark for white and too light for black at
            // the same time, and no single foreground colour fixes that. Both of
            // Modlight's own accents are well clear of that band.
            double whiteContrast = Against(Colors.White, start, end);
            double blackContrast = Against(Colors.Black, start, end);

            if (Math.Max(whiteContrast, blackContrast) <= bestContrast)
            {
                return best;
            }

            return whiteContrast > blackContrast ? Colors.White : Colors.Black;
        }

        /// <summary>The worst contrast <paramref name="foreground"/> has anywhere on the gradient.</summary>
        public static double Against(Color foreground, Color start, Color end) =>
            Math.Min(ContrastRatio(foreground, start), ContrastRatio(foreground, end));

        /// <summary>WCAG 2.x contrast ratio, from 1 (identical) to 21 (black on white).</summary>
        public static double ContrastRatio(Color first, Color second)
        {
            double firstLuminance = RelativeLuminance(first);
            double secondLuminance = RelativeLuminance(second);

            return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
                (Math.Min(firstLuminance, secondLuminance) + 0.05);
        }

        /// <summary>
        /// WCAG relative luminance: each channel taken out of sRGB's gamma curve, then
        /// weighted by how much of it the eye actually sees.
        /// </summary>
        /// <remarks>
        /// The weights are the reason a naive brightness test gets colours like this
        /// wrong. Green carries 72% of perceived lightness and blue 7%, so a blue-violet
        /// accent is far darker than its numbers look: #7E88FB averages 60% per channel,
        /// which reads as "light", and measures 0.29 here, which is "dark enough that
        /// white text fails on it".
        /// </remarks>
        public static double RelativeLuminance(Color color)
        {
            static double Linearize(byte channel)
            {
                double value = channel / 255d;
                return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Linearize(color.R)) +
                (0.7152 * Linearize(color.G)) +
                (0.0722 * Linearize(color.B));
        }
    }
}
