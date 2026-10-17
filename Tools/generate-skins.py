#!/usr/bin/env python3
"""Turn the web panel's skins into Avalonia theme dictionaries.

The panel (``Modlight``, github.com/Nimfek/Modlight) is the source of truth for what a skin looks like. Each
skin there is one block of ``--l-*`` / ``--d-*`` custom-property pairs in
``app/globals.css``; the list and the display names live in ``lib/themes.ts`` and
``messages/{en,pl}/profile.json``. Adding a skin to the panel is one CSS block and
one array entry, and the panel gained two skins the day before this script was
written - so the app reads those files rather than restating them. Hand-copying
fourteen palettes would be wrong within a week.

What it writes (both files are generated; do not edit them by hand):

  ShareX.Avalonia/Theming/Skins.axaml        28 theme dictionaries (14 skins x 2 modes)
  ShareX.Avalonia/Theming/SkinVariants.g.cs  the ThemeVariant objects they are keyed by,
                                             plus the catalogue the picker lists

Usage (from anywhere):

    python Tools/generate-skins.py [--panel ..\\Modlight] [--check] [--contrast]

  --check     do not write; exit 1 if the files on disk differ from what would be
              written. For a build gate.
  --contrast  print the WCAG contrast table for every skin in both modes and exit 1
              if any text pairing falls below 4.5:1.

Three things about the conversion are worth knowing:

1. Avalonia does not understand OKLCH, so every colour is converted to sRGB here.
   ``win98`` is authored in hex because its point is a set of exact 1998 system
   colours; both syntaxes are handled, and a skin whose tokens could not be read
   at all is a hard error rather than a silently empty dictionary.

2. A skin only declares what it CHANGES - the ``default`` block is the fallback
   layer for all the others, exactly as the CSS cascade makes it. So tokens are
   resolved as ``default`` overlaid with the skin's own block, which is what the
   browser computes.

3. The theme dictionaries are COMPLETE rather than differential: every key
   ShareXTheme.axaml defines for a mode is emitted for every skin. Avalonia falls
   back along ``ThemeVariant.InheritVariant``, and each skin variant inherits the
   plain Dark/Light variant so Fluent's own resources resolve - the same one-level
   arrangement ShareXDark has always used, rather than a two-level chain nothing
   in this codebase has ever exercised.
"""

from __future__ import annotations

import argparse
import io
import json
import math
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

TEMPLATE_AXAML = os.path.join(REPO, "ShareX.Avalonia", "Theming", "ShareXTheme.axaml")
OUT_AXAML = os.path.join(REPO, "ShareX.Avalonia", "Theming", "Skins.axaml")
OUT_CS = os.path.join(REPO, "ShareX.Avalonia", "Theming", "SkinVariants.g.cs")


# --------------------------------------------------------------------------- #
# Colour                                                                      #
# --------------------------------------------------------------------------- #


def oklch_to_rgb(L: float, C: float, h_deg: float) -> tuple[int, int, int]:
    """OKLCH to sRGB 0-255.

    Same arithmetic as the panel's own tests/unit/skin-contrast.test.ts, so a
    colour measured there and a colour painted here are the same colour.
    """
    h = math.radians(h_deg)
    a = C * math.cos(h)
    b = C * math.sin(h)

    l_ = L + 0.3963377774 * a + 0.2158037573 * b
    m_ = L - 0.1055613458 * a - 0.0638541728 * b
    s_ = L - 0.0894841775 * a - 1.2914855480 * b

    l, m, s = l_**3, m_**3, s_**3

    lin = (
        4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
        -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
        -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s,
    )

    out = []
    for v in lin:
        v = min(1.0, max(0.0, v))
        enc = 12.92 * v if v <= 0.0031308 else 1.055 * v ** (1 / 2.4) - 0.055
        out.append(int(round(enc * 255)))
    return tuple(out)  # type: ignore[return-value]


class Colour:
    """An sRGB colour with straight alpha, kept until it is composited."""

    __slots__ = ("r", "g", "b", "a")

    def __init__(self, r: int, g: int, b: int, a: float = 1.0):
        self.r, self.g, self.b, self.a = r, g, b, a

    def over(self, base: "Colour") -> "Colour":
        if self.a >= 0.999:
            return Colour(self.r, self.g, self.b)
        t = self.a
        return Colour(
            int(round(self.r * t + base.r * (1 - t))),
            int(round(self.g * t + base.g * (1 - t))),
            int(round(self.b * t + base.b * (1 - t))),
        )

    def hex(self) -> str:
        return "#%02X%02X%02X" % (self.r, self.g, self.b)

    def luminance(self) -> float:
        def f(c: int) -> float:
            v = c / 255.0
            return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4

        return 0.2126 * f(self.r) + 0.7152 * f(self.g) + 0.0722 * f(self.b)

    def scaled(self, factor: float) -> "Colour":
        """Multiply the channels, the way ThemeManager.DarkenColor does."""
        return Colour(
            min(255, int(round(self.r * factor))),
            min(255, int(round(self.g * factor))),
            min(255, int(round(self.b * factor))),
            self.a,
        )

    def toward(self, other: "Colour", t: float) -> "Colour":
        return Colour(
            int(round(self.r + (other.r - self.r) * t)),
            int(round(self.g + (other.g - self.g) * t)),
            int(round(self.b + (other.b - self.b) * t)),
            self.a,
        )

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return "Colour(%s, a=%.2f)" % (self.hex(), self.a)


WHITE = Colour(255, 255, 255)
BLACK = Colour(0, 0, 0)


def contrast(x: Colour, y: Colour) -> float:
    a, b = x.luminance(), y.luminance()
    hi, lo = max(a, b), min(a, b)
    return (hi + 0.05) / (lo + 0.05)


HEX_RE = re.compile(r"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")
OKLCH_RE = re.compile(r"^oklch\(\s*([^)]+)\)$", re.IGNORECASE)
NUMBER_RE = re.compile(r"^-?\d*\.?\d+(%?)$")


def parse_alpha(text: str) -> float:
    text = text.strip()
    if text.endswith("%"):
        return float(text[:-1]) / 100.0
    return float(text)


def parse_colour(value: str) -> Colour | None:
    """One CSS colour value, or None when it is something we do not paint with."""
    value = value.strip()

    m = HEX_RE.match(value)
    if m:
        digits = m.group(1)
        if len(digits) == 3:
            digits = "".join(c * 2 for c in digits)
        n = int(digits[:6], 16)
        alpha = int(digits[6:8], 16) / 255.0 if len(digits) == 8 else 1.0
        return Colour((n >> 16) & 255, (n >> 8) & 255, n & 255, alpha)

    m = OKLCH_RE.match(value)
    if not m:
        return None

    body = m.group(1)
    alpha = 1.0
    if "/" in body:
        body, _, alpha_text = body.partition("/")
        alpha = parse_alpha(alpha_text)

    parts = [p for p in re.split(r"[\s,]+", body.strip()) if p]
    if not parts:
        return None
    while len(parts) < 3:
        parts.append("0")

    nums = []
    for i, p in enumerate(parts[:3]):
        if not NUMBER_RE.match(p):
            return None
        if p.endswith("%"):
            # L may be written as a percentage; C and H never are in this file.
            nums.append(float(p[:-1]) / 100.0 if i == 0 else float(p[:-1]))
        else:
            nums.append(float(p))

    r, g, b = oklch_to_rgb(nums[0], nums[1], nums[2])
    return Colour(r, g, b, alpha)


# --------------------------------------------------------------------------- #
# Reading the panel                                                           #
# --------------------------------------------------------------------------- #


def read_text(path: str) -> str:
    with io.open(path, encoding="utf-8") as fh:
        return fh.read()


def skin_ids_and_names(panel: str) -> list[dict]:
    """The skins the picker lists, in the panel's own order, with both languages.

    Names come from the panel's message catalogues rather than from resx. They are
    the panel's labels for the panel's skins: restating fourteen of them by hand in
    two more files would drift the moment the panel adds a fifteenth, which it does
    roughly every round. `--label` strings that the app itself authors DO live in
    resx; this is data pulled from the other side of the sync.
    """
    src = read_text(os.path.join(panel, "lib", "themes.ts"))
    body = src[src.index("export const THEMES") : src.index("] as const;")]
    ids = re.findall(r'\bid:\s*"([a-z0-9]+)"', body)
    if not ids:
        raise SystemExit("no skins found in lib/themes.ts - has its shape changed?")

    names = {}
    for locale in ("en", "pl"):
        with io.open(
            os.path.join(panel, "messages", locale, "profile.json"), encoding="utf-8"
        ) as fh:
            names[locale] = json.load(fh).get("themes", {})

    default_match = re.search(r'DEFAULT_THEME:\s*ThemeId\s*=\s*"([a-z0-9]+)"', src)
    default_id = default_match.group(1) if default_match else ids[0]

    out = []
    for skin_id in ids:
        fallback = re.search(
            r'id:\s*"%s"[^}]*?name:\s*"([^"]+)"' % skin_id, body, re.DOTALL
        )
        en = names["en"].get(skin_id, {}).get("name") or (
            fallback.group(1) if fallback else skin_id
        )
        pl = names["pl"].get(skin_id, {}).get("name") or en
        out.append({"id": skin_id, "en": en, "pl": pl, "default": skin_id == default_id})
    return out


DECL_RE = re.compile(r"--([ld])-([a-z0-9-]+)\s*:\s*([^;]+);")


def skin_block(css: str, skin_id: str) -> str:
    """The FIRST variable block a skin declares.

    Only the first: the later ones sit inside media queries and deliberately
    override a handful of values for a condition (reduced motion, forced colours)
    that a desktop palette cannot represent anyway. The panel's own contrast test
    reads exactly the same slice, so the two measurements are of the same thing.
    """
    selector = '[data-theme="default"],' if skin_id == "default" else '[data-theme="%s"] {' % skin_id
    at = css.find(selector)
    if at < 0:
        raise SystemExit("no CSS block for skin '%s'" % skin_id)
    open_at = css.index("{", at)
    close_at = css.index("\n}", open_at)
    return css[open_at:close_at]


def tokens_for(css: str, skin_id: str, base: dict[str, str]) -> dict[str, str]:
    """`default` overlaid with the skin's own declarations - the computed cascade."""
    resolved = dict(base)
    for m in DECL_RE.finditer(skin_block(css, skin_id)):
        resolved["%s-%s" % (m.group(1), m.group(2))] = m.group(3).strip()
    return resolved


VAR_RE = re.compile(r"^var\(\s*(--[a-z0-9-]+)\s*\)$")


def token_colour(tokens: dict[str, str], name: str, depth: int = 0) -> Colour | None:
    raw = tokens.get(name)
    if raw is None or depth > 4:
        return None
    m = VAR_RE.match(raw.strip())
    if m:
        return token_colour(tokens, m.group(1)[2:], depth + 1)
    return parse_colour(raw)


# --------------------------------------------------------------------------- #
# Panel token -> ShareX resource                                              #
# --------------------------------------------------------------------------- #

# 4.5:1 is the WCAG AA bar for body text - the same floor the panel's own
# tests/unit/skin-contrast.test.ts holds its skins to. Deliberately not "whatever the
# current values happen to reach": a threshold set at the status quo fails the next
# honest design decision rather than protecting a reader.
MIN_CONTRAST = 4.5


class Palette:
    """One skin in one mode, as the resource names the app's controls read.

    The mapping is docs/modlight-app-design.md's table, which was derived by hand
    for the `default` skin and verified against the values already in
    ShareXTheme.axaml. `--check-default` re-verifies that agreement on every run.
    """

    def __init__(self, tokens: dict[str, str], mode: str, semantic: dict[str, Colour]):
        p = mode  # 'l' or 'd'
        self.dark = mode == "d"
        self.semantic = semantic

        def tok(name: str, fallback: Colour | None = None) -> Colour:
            c = token_colour(tokens, "%s-%s" % (p, name))
            if c is None:
                if fallback is None:
                    raise SystemExit("token --%s-%s is missing or unreadable" % (p, name))
                return fallback
            return c

        # Resolved first: everything with alpha composites over one of these.
        ground = tok("background").over(WHITE if p == "l" else BLACK)
        self.card = tok("card").over(ground)

        # A card has to read as a card. Several skins - the default among them - give
        # the page and the card the SAME colour and let a 1px border do the separating,
        # which works in a browser where a card is also a shadow and a radius, and does
        # not work in a window whose whole chrome is flat fills. When the two collide,
        # the ground steps back rather than the card stepping forward: darkening the
        # page is invisible on its own, where lightening every card would change the
        # skin's actual look.
        self.background = (
            ground.toward(BLACK, 0.04)
            if (ground.r, ground.g, ground.b) == (self.card.r, self.card.g, self.card.b)
            else ground
        )
        self.sidebar = tok("sidebar", self.background).over(self.background)

        self.foreground = tok("foreground").over(self.background)
        self.card_foreground = tok("card-foreground", self.foreground).over(self.card)
        self.muted_foreground = tok("muted-foreground").over(self.background)
        self.sidebar_foreground = tok("sidebar-foreground", self.foreground).over(self.sidebar)

        self.primary = tok("primary").over(self.background)
        self.primary_foreground = tok("primary-foreground").over(self.primary)

        # Border is the one token routinely written with alpha (`oklch(1 0 0 / 12%)`),
        # because in the browser it sits on whatever is under it. A resource is a flat
        # colour, so it is composited over the page - the surface a border divides most
        # of the time, and the one the hand conversion in docs/modlight-app-design.md
        # used, which is what makes that table reproducible here.
        self.border = tok("border").over(self.background)
        self.accent = tok("accent").over(self.card)

    # -- derived ---------------------------------------------------------- #

    @property
    def accent_end(self) -> Colour:
        """The far end of the accent gradient.

        10% darker, matching ThemeManager.DarkenColor, so a skin accent and a user
        accent produce the same shape of gradient rather than two different looks.
        This value is decorative in practice: ApplyAccentColor recomputes it at
        startup from whichever accent wins and overwrites the resource.
        """
        return self.primary.scaled(0.90)

    # -- semantic --------------------------------------------------------- #
    #
    # The confirm/cancel pair does NOT follow the skin.
    #
    # Everything else in this palette is chrome, and chrome is what a skin is for.
    # These two are a claim about what a button DOES, and a "Cancel" that turns
    # magenta under Synthwave or a "Continue" that goes olive under Riso is a bug
    # wearing a feature's clothes. They are taken from ShareXTheme.axaml per mode -
    # a red that reads as red and a green that reads as green - which also keeps
    # the label colour's contrast a fixed, already-verified quantity instead of one
    # more thing every new skin could quietly break.

    @property
    def cancel_background(self) -> Colour:
        return self.semantic["ShareX.Color.TaskMode.Cancel.Background"]

    @property
    def cancel_border(self) -> Colour:
        return self.semantic["ShareX.Color.TaskMode.Cancel.Border"]

    @property
    def continue_background(self) -> Colour:
        return self.semantic["ShareX.Color.TaskMode.Continue.Background"]

    @property
    def continue_background_hover(self) -> Colour:
        """The success button's fill while the pointer is on it.

        Its own token rather than the border reused as a fill, which is what the
        style did until the label was measured on it: in dark mode the border is a
        LIGHTER green than the fill - it has to be, it doubles as the success colour
        drawn as text on dark panels - so hovering the button took white from 2.41:1
        to 1.87:1. Darkening on hover is what light mode was already doing.
        """
        return self.semantic["ShareX.Color.TaskMode.Continue.Background.Hover"]

    @property
    def continue_border(self) -> Colour:
        return self.semantic["ShareX.Color.TaskMode.Continue.Border"]

    @property
    def taskmode_foreground(self) -> Colour:
        return self.semantic["ShareX.Color.TaskMode.Foreground"]

    @property
    def hover(self) -> Colour:
        """The raised surface under a hovered or current control.

        The panel's `--accent` is the obvious source and is what thirteen of the
        fourteen skins want: a card lifted a shade. win98 is the exception, and it is
        not an oddity - `--accent: #000080` is the genuine 1998 SELECTION colour, and
        the web panel writes `--accent-foreground` on it. This app does not: the tab
        strip and the current library row draw ShareX.Brush.Text on this surface,
        which put black on navy at 1.14:1 - unreadable, and visible in the very first
        screenshot taken of the skin.

        Rather than teach every control a second foreground - ControlStyles.axaml is
        shared with a 75k-line image editor, and that is a wide change to make for one
        skin - the surface is mixed toward the accent only as far as the app's own text
        can still be read on it. A skin whose accent already passes gets its accent
        unchanged, so this costs the other thirteen nothing; win98 gets a navy-tinted
        grey instead of a flat grey, which is closer to what it was asking for than
        dropping the accent entirely would be.
        """
        step = 0.05
        t = 1.0
        while t > 0:
            candidate = self.card.toward(self.accent, t)
            if contrast(self.foreground, candidate) >= MIN_CONTRAST:
                return candidate
            t -= step

        # No amount of the accent is readable. Lift the card toward its own text
        # instead - enough to see, not enough to fight the text sitting on it.
        return self.card.toward(self.foreground, 0.12)

    @property
    def panel_foreground(self) -> Colour:
        """Body text written ON the side panel, not on the page.

        The app has exactly one text colour, and for twelve of the fourteen skins that
        is the right answer because the panel is a shade of the page. Two skins - deco
        and riso in light mode - give the sidebar a SATURATED colour, and the panel's
        own `--sidebar-foreground` is the reason they still read in a browser. Writing
        ShareX.Brush.Text there instead measured 1.04:1 for deco: twelve rows of dark
        text on navy, in the one pane whose whole job is orientation.

        Emitted for every skin so the settings window can name one resource rather than
        branch; where the skin has nothing to say it is the ordinary text colour, and
        the twenty-six unaffected combinations get exactly what they got before.
        """
        return self.sidebar_foreground

    @property
    def panel_muted_foreground(self) -> Colour:
        """The quiet text on the side panel, by the same argument.

        Measured before it is used: the muted colour is derived from the PAGE, so on a
        coloured panel it can fail on its own (riso light: 1.19:1) even when the body
        text above it passes. Where it does, the panel's own text steps back toward the
        panel instead - as far as it can go while still clearing the body-text floor.
        """
        if contrast(self.muted_foreground, self.sidebar) >= MIN_CONTRAST:
            return self.muted_foreground

        for t in (0.45, 0.40, 0.35, 0.30, 0.25, 0.20, 0.15, 0.10, 0.05):
            candidate = self.sidebar_foreground.toward(self.sidebar, t)
            if contrast(candidate, self.sidebar) >= MIN_CONTRAST:
                return candidate

        return self.sidebar_foreground

    @staticmethod
    def _ring(start: Colour, target: Colour, grounds: tuple[Colour, ...]) -> Colour:
        """The accent, moved toward `target` only as far as being seen requires.

        3.0 is WCAG's floor for a control boundary - the ring carries no letters, so the
        4.5 body-text bar would be the wrong instrument. Twenty steps because the search
        stops at the first pass: a skin whose accent is already visible keeps its accent,
        which is what makes this cost the other skins nothing.
        """
        best = start
        best_score = min(contrast(start, ground) for ground in grounds)

        for step in range(0, 21):
            candidate = start.toward(target, step / 20)
            score = min(contrast(candidate, ground) for ground in grounds)

            if score >= 3.0:
                return candidate
            if score > best_score:
                best, best_score = candidate, score

        return best

    @property
    def focus(self) -> Colour:
        """The ring that says where the keyboard is, on a card or on the page.

        A separate token because the border cannot do this job: focused and unfocused
        differed by one pixel of the SAME brush, and a border is quiet by design - under
        3:1 against the panel in twenty-four of the twenty-eight combinations, which is
        no indicator at all.
        """
        return self._ring(self.primary, self.foreground, (self.card, self.background))

    @property
    def focus_on_panel(self) -> Colour:
        """The same ring, drawn on the side panel.

        Two tokens rather than one, for the reason Text.OnPanel exists: deco and riso in
        light mode put a saturated navy behind the section list and a near-white behind
        the cards, and NO single colour clears 3:1 on both without landing in a narrow
        mid-grey band that reads as neither the accent nor anything else. Asking one
        value to answer two grounds is what produced the unreadable pane in the first
        place.
        """
        return self._ring(self.primary, self.panel_foreground, (self.sidebar,))

    @property
    def control_disabled(self) -> Colour:
        """A control that is off, and quieter than one under the pointer.

        It used to be the ground and the text met in the middle, which in dark mode is a
        MID GREY on a near-black card: on the sounds page the four biggest, brightest
        rectangles on screen were the four path fields you cannot use, at 2.71:1 above
        the card, while the search box you can type in sat at 1.4:1. "You cannot use
        this" was shouting over "you can".

        Direction is unchanged - a greyed field is what every toolkit draws - but the
        distance is now bounded by a number the skin itself chose: the hover fill, the
        loudest surface a resting control is allowed to take. Whatever a skin thinks
        "raised" looks like, "off" is never louder than that.
        """
        bound = contrast(self.hover, self.card)

        for t in (0.30, 0.25, 0.20, 0.15, 0.12, 0.10, 0.08, 0.06):
            candidate = self.card.toward(self.muted_foreground, t)
            if contrast(candidate, self.card) <= bound:
                return candidate

        # A skin whose hover is indistinguishable from its card leaves no room to work
        # in. The smallest visible step is still better than a mid grey.
        return self.card.toward(self.muted_foreground, 0.04)

    @property
    def checker_a(self) -> Colour:
        """Transparency checkerboard in the image editor, in the skin's own greys."""
        return self.card.toward(WHITE if not self.dark else BLACK, 0.06)

    @property
    def checker_b(self) -> Colour:
        return self.card.toward(WHITE if self.dark else BLACK, 0.06)

    def colours(self) -> dict[str, Colour]:
        return {
            "ShareX.Color.Accent.Start": self.primary,
            "ShareX.Color.Accent.End": self.accent_end,
            "ShareX.Color.Accent.Foreground": self.primary_foreground,
            # Read by ThemeManager, never overwritten by it - see the note there.
            "ShareX.Color.Skin.Accent": self.primary,
            "ShareX.Color.Skin.Accent.Foreground": self.primary_foreground,
            "ShareX.Color.Text": self.foreground,
            "ShareX.Color.Text.Secondary": self.muted_foreground,
            "ShareX.Color.Text.OnPanel": self.panel_foreground,
            "ShareX.Color.Text.Secondary.OnPanel": self.panel_muted_foreground,
            "ShareX.Color.Focus": self.focus,
            "ShareX.Color.Focus.OnPanel": self.focus_on_panel,
            "ShareX.Color.Background.Main": self.background,
            "ShareX.Color.Background.Panel": self.sidebar,
            "ShareX.Color.Background.Popup": self.card,
            "ShareX.Color.Background.Menu": self.card,
            "ShareX.Color.Background.Toolbar": self.sidebar,
            "ShareX.Color.Border": self.border,
            "ShareX.Color.Control.Background": self.card,
            "ShareX.Color.Control.Background.Hover": self.hover,
            "ShareX.Color.Control.Background.Disabled": self.control_disabled,
            "ShareX.Color.Control.Border": self.border,
            "ShareX.Color.Separator": self.border,
            "ShareX.Color.TaskMode.Cancel.Background": self.cancel_background,
            "ShareX.Color.TaskMode.Cancel.Border": self.cancel_border,
            "ShareX.Color.TaskMode.Continue.Background": self.continue_background,
            "ShareX.Color.TaskMode.Continue.Background.Hover": self.continue_background_hover,
            "ShareX.Color.TaskMode.Continue.Border": self.continue_border,
            "ShareX.Color.TaskMode.Foreground": self.taskmode_foreground,
            "ShareX.Color.Checker.A": self.checker_a,
            "ShareX.Color.Checker.B": self.checker_b,
        }


# --------------------------------------------------------------------------- #
# Emitting                                                                    #
# --------------------------------------------------------------------------- #

BANNER = """<!--
    GENERATED FILE - do not edit.

    Written by Tools/generate-skins.py from the web panel's app/globals.css.
    Fourteen skins x two modes, mapped onto the ShareX.Color.* / ShareX.Brush.*
    names every control in this app already reads, so a skin change repaints the
    whole application - image editor and capture windows included - without a
    single control being touched.

    Regenerate:  python Tools/generate-skins.py
    Verify:      python Tools/generate-skins.py (with the check and contrast switches)
-->"""


def brush_body(indent: str) -> str:
    """The brushes, identical in every dictionary because they all read Colors.

    Written once here rather than templated out of ShareXTheme.axaml: a StaticResource
    inside a theme dictionary resolves within that dictionary, so each variant needs its
    own copy of every brush, and a copy is only safe if it is mechanical.
    """
    lines = []
    solid = [
        ("ShareX.Brush.Accent.Start", "ShareX.Color.Accent.Start"),
        ("ShareX.Brush.Accent.Foreground", "ShareX.Color.Accent.Foreground"),
        ("ShareX.Brush.Text", "ShareX.Color.Text"),
        ("ShareX.Brush.Text.Secondary", "ShareX.Color.Text.Secondary"),
        ("ShareX.Brush.Text.OnPanel", "ShareX.Color.Text.OnPanel"),
        ("ShareX.Brush.Text.Secondary.OnPanel", "ShareX.Color.Text.Secondary.OnPanel"),
        ("ShareX.Brush.Focus", "ShareX.Color.Focus"),
        ("ShareX.Brush.Focus.OnPanel", "ShareX.Color.Focus.OnPanel"),
        ("ShareX.Brush.Background.Main", "ShareX.Color.Background.Main"),
        ("ShareX.Brush.Background.Panel", "ShareX.Color.Background.Panel"),
        ("ShareX.Brush.Background.Popup", "ShareX.Color.Background.Popup"),
        ("ShareX.Brush.Background.Menu", "ShareX.Color.Background.Menu"),
        ("MenuFlyoutPresenterBackground", "ShareX.Color.Background.Menu"),
        ("ShareX.Brush.Background.Toolbar", "ShareX.Color.Background.Toolbar"),
        ("ShareX.Brush.Border", "ShareX.Color.Border"),
        ("ShareX.Brush.Control.Background", "ShareX.Color.Control.Background"),
        ("ShareX.Brush.Control.Background.Hover", "ShareX.Color.Control.Background.Hover"),
        ("ShareX.Brush.Control.Background.Disabled", "ShareX.Color.Control.Background.Disabled"),
        ("ShareX.Brush.Control.Border", "ShareX.Color.Control.Border"),
        ("ShareX.Brush.Separator", "ShareX.Color.Separator"),
        ("ShareX.Brush.TaskMode.Cancel.Background", "ShareX.Color.TaskMode.Cancel.Background"),
        ("ShareX.Brush.TaskMode.Cancel.Border", "ShareX.Color.TaskMode.Cancel.Border"),
        ("ShareX.Brush.TaskMode.Continue.Background", "ShareX.Color.TaskMode.Continue.Background"),
        (
            "ShareX.Brush.TaskMode.Continue.Background.Hover",
            "ShareX.Color.TaskMode.Continue.Background.Hover",
        ),
        ("ShareX.Brush.TaskMode.Continue.Border", "ShareX.Color.TaskMode.Continue.Border"),
        ("ShareX.Brush.TaskMode.Foreground", "ShareX.Color.TaskMode.Foreground"),
    ]

    lines.append(
        '%s<LinearGradientBrush x:Key="ShareX.Brush.Accent" StartPoint="0%%,0%%" EndPoint="100%%,100%%">'
        % indent
    )
    lines.append(
        '%s    <GradientStop Color="{StaticResource ShareX.Color.Accent.Start}" Offset="0" />' % indent
    )
    lines.append(
        '%s    <GradientStop Color="{StaticResource ShareX.Color.Accent.End}" Offset="1" />' % indent
    )
    lines.append("%s</LinearGradientBrush>" % indent)

    for key, colour in solid:
        lines.append(
            '%s<SolidColorBrush x:Key="%s" Color="{StaticResource %s}" />' % (indent, key, colour)
        )

    lines.append('%s<SolidColorBrush x:Key="ShareX.Brush.Overlay.Soft" Color="#30000000" />' % indent)
    lines.append('%s<SolidColorBrush x:Key="ShareX.Brush.Overlay.Modal" Color="#80000000" />' % indent)

    for key, size in (("ShareX.Brush.Checkered", 30), ("ShareX.Brush.Checkered.Small", 8)):
        half = size // 2
        lines.append(
            '%s<DrawingBrush x:Key="%s" TileMode="Tile" SourceRect="0,0,%d,%d" DestinationRect="0,0,%d,%d">'
            % (indent, key, size, size, size, size)
        )
        lines.append("%s    <DrawingBrush.Drawing>" % indent)
        lines.append("%s        <DrawingGroup>" % indent)
        lines.append(
            '%s            <GeometryDrawing Brush="{StaticResource ShareX.Brush.Checker.A}">' % indent
        )
        lines.append("%s                <GeometryDrawing.Geometry>" % indent)
        lines.append(
            '%s                    <RectangleGeometry Rect="0,0,%d,%d" />' % (indent, size, size)
        )
        lines.append("%s                </GeometryDrawing.Geometry>" % indent)
        lines.append("%s            </GeometryDrawing>" % indent)
        lines.append(
            '%s            <GeometryDrawing Brush="{StaticResource ShareX.Brush.Checker.B}">' % indent
        )
        lines.append("%s                <GeometryDrawing.Geometry>" % indent)
        lines.append("%s                    <GeometryGroup>" % indent)
        lines.append(
            '%s                        <RectangleGeometry Rect="0,0,%d,%d" />' % (indent, half, half)
        )
        lines.append(
            '%s                        <RectangleGeometry Rect="%d,%d,%d,%d" />'
            % (indent, half, half, half, half)
        )
        lines.append("%s                    </GeometryGroup>" % indent)
        lines.append("%s                </GeometryDrawing.Geometry>" % indent)
        lines.append("%s            </GeometryDrawing>" % indent)
        lines.append("%s        </DrawingGroup>" % indent)
        lines.append("%s    </DrawingBrush.Drawing>" % indent)
        lines.append("%s</DrawingBrush>" % indent)

    return "\n".join(lines)


def field_name(skin_id: str, mode: str) -> str:
    return "%s_%s" % (skin_id[:1].upper() + skin_id[1:], "Dark" if mode == "d" else "Light")


def variant_key(skin_id: str, mode: str) -> str:
    return "Skin.%s.%s" % (skin_id, "Dark" if mode == "d" else "Light")


def build_axaml(skins: list[dict], palettes: dict[tuple[str, str], Palette]) -> str:
    out = []
    out.append('<ResourceDictionary xmlns="https://github.com/avaloniaui"')
    out.append('                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"')
    out.append('                    xmlns:helpers="clr-namespace:ShareX.AvaloniaUI.Theming">')
    out.append("")
    out.append(BANNER)
    out.append("")
    out.append("    <ResourceDictionary.ThemeDictionaries>")

    for skin in skins:
        for mode in ("d", "l"):
            palette = palettes[(skin["id"], mode)]
            out.append("")
            out.append(
                "        <!-- %s (%s) -->" % (skin["en"], "dark" if mode == "d" else "light")
            )
            out.append(
                '        <ResourceDictionary x:Key="{x:Static helpers:SkinVariants.%s}">'
                % field_name(skin["id"], mode)
            )
            for key, colour in palette.colours().items():
                out.append('            <Color x:Key="%s">%s</Color>' % (key, colour.hex()))
            # The checkerboard needs brushes, not colours, to sit in a DrawingBrush.
            out.append(
                '            <SolidColorBrush x:Key="ShareX.Brush.Checker.A" Color="{StaticResource ShareX.Color.Checker.A}" />'
            )
            out.append(
                '            <SolidColorBrush x:Key="ShareX.Brush.Checker.B" Color="{StaticResource ShareX.Color.Checker.B}" />'
            )
            out.append("")
            out.append(brush_body("            "))
            out.append("        </ResourceDictionary>")

    out.append("")
    out.append("    </ResourceDictionary.ThemeDictionaries>")
    out.append("</ResourceDictionary>")
    return "\n".join(out) + "\n"


CS_HEADER = """#region License Information (GPL v3)

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

// GENERATED FILE - do not edit.
//
// Written by Tools/generate-skins.py from the web panel's lib/themes.ts and
// messages/{en,pl}/profile.json. Regenerate with:
//
//     python Tools/generate-skins.py

// The .g.cs suffix makes Roslyn treat this as generated and switch the nullable
// context off, which turns every `string?` below into a warning. Say it explicitly.
#nullable enable

using Avalonia.Styling;
using System.Collections.Generic;

namespace ShareX.AvaloniaUI.Theming;

/// <summary>One skin, as the picker lists it.</summary>
/// <param name="Id">The panel's id for it - the value written to and read from /api/theme.</param>
/// <param name="NameEnglish">The panel's own label.</param>
/// <param name="NamePolish">The panel's Polish label.</param>
/// <param name="Light">The theme variant for this skin on a light ground.</param>
/// <param name="Dark">The theme variant for this skin on a dark ground.</param>
public sealed record SkinDefinition(
    string Id,
    string NameEnglish,
    string NamePolish,
    ThemeVariant Light,
    ThemeVariant Dark);

/// <summary>
/// The theme variants the generated Skins.axaml dictionaries are keyed by.
/// </summary>
/// <remarks>
/// Each one inherits the plain <see cref="ThemeVariant.Dark"/> or
/// <see cref="ThemeVariant.Light"/> rather than ShareXDark/ShareXLight, so Fluent's
/// own resources resolve through exactly the one-level fallback ShareXDark has
/// always used. The dictionaries are complete - every ShareX.Color.* and
/// ShareX.Brush.* key is present in all of them - so nothing depends on a longer
/// chain that this codebase has never exercised.
/// </remarks>
public static class SkinVariants
{
"""


def cs_literal(text: str) -> str:
    """A C# string literal that is pure ASCII.

    The Polish names carry accented letters, and a .cs file holding raw UTF-8 without
    a BOM is read with the machine's default codepage by some toolchains - which turns
    "Mgla,wica" into mojibake on exactly the machines that would notice. Escaping keeps
    the generated file byte-identical everywhere and matches the rest of this repo,
    where every .cs source is ASCII.
    """
    out = []
    for ch in text:
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif " " <= ch <= "~":
            out.append(ch)
        else:
            out.append("\\u%04X" % ord(ch))
    return "".join(out)


def build_cs(skins: list[dict]) -> str:
    out = [CS_HEADER]

    for skin in skins:
        for mode in ("l", "d"):
            out.append(
                "    public static readonly ThemeVariant %s = new(\"%s\", ThemeVariant.%s);"
                % (
                    field_name(skin["id"], mode),
                    variant_key(skin["id"], mode),
                    "Dark" if mode == "d" else "Light",
                )
            )

    default_id = next((s["id"] for s in skins if s["default"]), skins[0]["id"])

    out.append("")
    out.append("    /// <summary>What an account gets before it has chosen - the panel's own default.</summary>")
    out.append('    public const string DefaultSkinId = "%s";' % default_id)
    out.append("")
    out.append("    /// <summary>Every skin, in the order the panel's picker shows them.</summary>")
    out.append("    public static readonly IReadOnlyList<SkinDefinition> All = new SkinDefinition[]")
    out.append("    {")
    for skin in skins:
        out.append(
            '        new("%s", "%s", "%s", %s, %s),'
            % (
                skin["id"],
                cs_literal(skin["en"]),
                cs_literal(skin["pl"]),
                field_name(skin["id"], "l"),
                field_name(skin["id"], "d"),
            )
        )
    out.append("    };")
    out.append("")
    out.append("    /// <summary>The skin with this id, or the default when the id is unknown.</summary>")
    out.append("    /// <remarks>")
    out.append("    /// Unknown rather than invalid: the website can be a release ahead of this build,")
    out.append("    /// and a skin it has that we do not is a reason to fall back for the moment, never a")
    out.append("    /// reason to fail. The stored id is left alone so the next build picks it up.")
    out.append("    /// </remarks>")
    out.append("    public static SkinDefinition Resolve(string? id)")
    out.append("    {")
    out.append("        foreach (SkinDefinition skin in All)")
    out.append("        {")
    out.append("            if (string.Equals(skin.Id, id, System.StringComparison.OrdinalIgnoreCase))")
    out.append("            {")
    out.append("                return skin;")
    out.append("            }")
    out.append("        }")
    out.append("")
    out.append("        foreach (SkinDefinition skin in All)")
    out.append("        {")
    out.append("            if (skin.Id == DefaultSkinId)")
    out.append("            {")
    out.append("                return skin;")
    out.append("            }")
    out.append("        }")
    out.append("")
    out.append("        return All[0];")
    out.append("    }")
    out.append("")
    out.append("    /// <summary>Whether this id names a skin this build can paint.</summary>")
    out.append("    public static bool IsKnown(string? id)")
    out.append("    {")
    out.append("        foreach (SkinDefinition skin in All)")
    out.append("        {")
    out.append("            if (string.Equals(skin.Id, id, System.StringComparison.OrdinalIgnoreCase))")
    out.append("            {")
    out.append("                return true;")
    out.append("            }")
    out.append("        }")
    out.append("")
    out.append("        return false;")
    out.append("    }")
    out.append("}")
    return "\n".join(out) + "\n"


# --------------------------------------------------------------------------- #
# Contrast                                                                    #
# --------------------------------------------------------------------------- #

# What each measured pairing has to clear. Body text gets the AA floor; a ring or a
# fill that carries no letters gets 3.0, which is the same standard's floor for a
# non-text indicator. A pairing absent from here is printed and not enforced.
THRESHOLDS = {
    "text/bg": MIN_CONTRAST,
    "text/card": MIN_CONTRAST,
    "label/accent": MIN_CONTRAST,
    "text/panel": MIN_CONTRAST,
    "muted/panel": MIN_CONTRAST,
    "muted/card": MIN_CONTRAST,
    "text/hover": MIN_CONTRAST,
    "label/continue": MIN_CONTRAST,
    "label/cont:hov": MIN_CONTRAST,
    "focus/panel": 3.0,
    "focus/card": 3.0,
    "focus/bg": 3.0,
}


def contrast_rows(skins: list[dict], palettes: dict[tuple[str, str], Palette]):
    """Every pairing this table reports is one the application actually paints.

    That distinction is the whole point of the round it was rewritten in. `text/card`
    used to measure the panel's `--card-foreground` against `--card`; the app has no
    such token and writes ShareX.Brush.Text there, so the number agreed with the screen
    only by the accident of the two CSS values being equal in thirteen skins. Two rows
    reported 15.01 and 9.79 for panes that measured 1.04 and 1.68 in a screenshot.

    So: every pair below is (an EMITTED colour, an EMITTED colour), named the way the
    controls name them. A gate that measures the inputs to a mapping cannot see a bug in
    the mapping - which is exactly what it was asked to catch.
    """
    rows = []
    for skin in skins:
        for mode in ("l", "d"):
            p = palettes[(skin["id"], mode)]
            emitted = p.colours()
            pairs = {
                "text/bg": contrast(p.foreground, p.background),
                "text/card": contrast(
                    emitted["ShareX.Color.Text"], emitted["ShareX.Color.Control.Background"]
                ),
                "label/accent": contrast(p.primary_foreground, p.primary),
                # What the selected navigation row is now filled with. It is the START of
                # the accent, flat, precisely because ShareX.Color.Accent.Foreground was
                # chosen against that colour - see SettingsNavigation.axaml.
                "label/acc:end": contrast(p.primary_foreground, p.accent_end),
                "text/panel": contrast(
                    emitted["ShareX.Color.Text.OnPanel"],
                    emitted["ShareX.Color.Background.Panel"],
                ),
                "muted/panel": contrast(
                    emitted["ShareX.Color.Text.Secondary.OnPanel"],
                    emitted["ShareX.Color.Background.Panel"],
                ),
                "muted/bg": contrast(p.muted_foreground, p.background),
                # Cards carry secondary text - column headings, hints, the sentence under
                # a section title - so the pair has to be measured on the card, not only
                # on the page. It was not, and the settings window puts more of that text
                # on cards than anywhere else in the app.
                "muted/card": contrast(
                    emitted["ShareX.Color.Text.Secondary"],
                    emitted["ShareX.Color.Control.Background"],
                ),
                "text/hover": contrast(p.foreground, p.hover),
                "focus/panel": contrast(
                    emitted["ShareX.Color.Focus.OnPanel"],
                    emitted["ShareX.Color.Background.Panel"],
                ),
                "focus/card": contrast(
                    emitted["ShareX.Color.Focus"], emitted["ShareX.Color.Control.Background"]
                ),
                "focus/bg": contrast(
                    emitted["ShareX.Color.Focus"], emitted["ShareX.Color.Background.Main"]
                ),
                # Not a contrast requirement - a loudness one. See control_disabled: the
                # fill of a control you cannot use must not stand further off the card
                # than the fill of one under the pointer.
                "off/ctrl": contrast(
                    emitted["ShareX.Color.Control.Background.Disabled"],
                    emitted["ShareX.Color.Control.Background"],
                ),
                "hov/ctrl": contrast(
                    emitted["ShareX.Color.Control.Background.Hover"],
                    emitted["ShareX.Color.Control.Background"],
                ),
                "label/cancel": contrast(p.taskmode_foreground, p.cancel_background),
                "label/continue": contrast(p.taskmode_foreground, p.continue_background),
                "label/cont:hov": contrast(p.taskmode_foreground, p.continue_background_hover),
            }
            rows.append((skin["id"], "light" if mode == "l" else "dark", pairs))
    return rows


def loudness_problems(rows) -> list[str]:
    """Rows where "off" shouts louder than "under the pointer"."""
    return [
        "%s/%s: disabled fill %.2f:1 off the card, hover only %.2f:1"
        % (skin_id, mode, pairs["off/ctrl"], pairs["hov/ctrl"])
        for skin_id, mode, pairs in rows
        if pairs["off/ctrl"] > pairs["hov/ctrl"] + 0.01
    ]


def print_contrast(rows) -> bool:
    """The table, and whether every required pairing cleared the bar."""
    # label/continue and its hover state joined this list the round the button was
    # measured: white on the inherited #2CBE73 was 2.41:1, and on hover - which filled
    # with the border - 1.87:1. Both are enforced now so the pair cannot drift back.
    #
    # label/cancel is deliberately NOT here yet. It is 4.04:1 in dark mode, which is
    # also under the bar, but the cancel colours were not this round's to move and a
    # floor that fails on a value nobody is allowed to change teaches people to pass
    # --contrast and ignore the exit code.
    #
    # label/cancel is deliberately NOT enforced. It is 4.04:1 in dark mode, which is
    # also under the bar, but the cancel colours were not that round's to move and a
    # floor that fails on a value nobody is allowed to change teaches people to pass
    # --contrast and ignore the exit code. label/acc:end is unenforced for the same
    # reason and one more: nothing in the settings window paints on the far end of the
    # gradient any more, so a failure there would be a report about a checkbox glyph.
    header = ["skin", "mode"] + list(rows[0][2].keys())
    widths = [max(len(h), 9) for h in header]
    print("  ".join(h.ljust(w) for h, w in zip(header, widths)))
    print("  ".join("-" * w for w in widths))

    worst = None
    ok = True
    for skin_id, mode, pairs in rows:
        cells = [skin_id, mode] + ["%.2f" % v for v in pairs.values()]
        marks = []
        for name, value in pairs.items():
            floor = THRESHOLDS.get(name)
            if floor is None:
                continue
            if value < floor:
                ok = False
                marks.append("%s=%.2f" % (name, value))
            headroom = value / floor
            if worst is None or headroom < worst[2] / worst[4]:
                worst = (skin_id, mode, value, name, floor)
        line = "  ".join(c.ljust(w) for c, w in zip(cells, widths))
        print(line + ("   << FAIL " + ", ".join(marks) if marks else ""))

    loud = loudness_problems(rows)
    for problem in loud:
        print("LOUD " + problem)
    if loud:
        ok = False

    if worst:
        print("")
        print(
            "tightest required pairing: %s / %s / %s = %.2f:1 (floor %.1f)"
            % (worst[0], worst[1], worst[3], worst[2], worst[4])
        )
    return ok


# --------------------------------------------------------------------------- #
# Self-check against the hand-derived palette                                 #
# --------------------------------------------------------------------------- #


def template_colours(mode: str) -> dict[str, Colour]:
    """The Colors ShareXTheme.axaml declares for one mode."""
    template = read_text(TEMPLATE_AXAML)
    variant = "ShareXDark" if mode == "d" else "ShareXLight"
    at = template.index("ThemeManager.%s" % variant)
    block = template[at : template.index("</ResourceDictionary>", at)]
    out = {}
    for key, value in re.findall(r'<Color x:Key="([^"]+)">(#[0-9A-Fa-f]{6})</Color>', block):
        colour = parse_colour(value)
        if colour is not None:
            out[key] = colour
    return out


# Keys the derivation deliberately does not try to reproduce, and why.
SELF_CHECK_SKIP = {
    # ApplyAccentColor recomputes this at startup as DarkenColor(start, 0.10) and
    # overwrites the resource, so the value in the file is never the one on screen.
    # The generator writes what the runtime will compute; the template holds the
    # light-mode primary, which is a different (equally unused) choice.
    "ShareX.Color.Accent.End",
}

# Hand-converted values are rounded by hand. Agreement to within one step per channel
# is the honest bar; demanding equality would only teach the next person to "fix" the
# arithmetic until it matched a typo.
SELF_CHECK_TOLERANCE = 1


def check_default_matches_template(palettes: dict[tuple[str, str], Palette]) -> list[str]:
    """The `default` skin should reproduce what ShareXTheme.axaml already holds.

    Those values were converted by hand in docs/modlight-app-design.md and have been
    on screen for rounds. If this script disagrees with them, the arithmetic is wrong
    - a far more useful signal than any assertion written against the script's own
    output. Only the tokens the doc's table actually named are compared.
    """
    problems = []
    for mode, variant in (("d", "ShareXDark"), ("l", "ShareXLight")):
        want = template_colours(mode)
        got = palettes[("default", mode)].colours()
        for key, expected in want.items():
            if key not in got or key in SELF_CHECK_SKIP:
                continue
            actual = got[key]
            drift = max(
                abs(actual.r - expected.r), abs(actual.g - expected.g), abs(actual.b - expected.b)
            )
            if drift > SELF_CHECK_TOLERANCE:
                problems.append(
                    "%s / %s: template %s, generated %s (off by %d)"
                    % (variant, key, expected.hex(), actual.hex(), drift)
                )
    return problems


# --------------------------------------------------------------------------- #


def write_if_changed(path: str, text: str, check_only: bool) -> bool:
    """Write with LF endings, the way git stores every text file in this repo.

    The comparison ignores line endings, and has to: `.gitattributes` says
    `* text=auto`, so a checkout on Windows hands you CRLF while the blob is LF.
    A byte-for-byte check would call a freshly cloned, perfectly correct tree stale.
    """
    payload = text.encode("utf-8")
    existing = None
    if os.path.exists(path):
        with io.open(path, "rb") as fh:
            existing = fh.read()
    if existing is not None and existing.replace(b"\r\n", b"\n") == payload.replace(b"\r\n", b"\n"):
        return False
    if check_only:
        print("STALE: %s" % os.path.relpath(path, REPO))
        return True
    with io.open(path, "wb") as fh:
        fh.write(payload)
    print("wrote %s (%d bytes)" % (os.path.relpath(path, REPO), len(payload)))
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--panel",
        default=os.path.join(os.path.dirname(REPO), "Modlight"),
        help="path to the Modlight web panel checkout (default: ../Modlight)",
    )
    parser.add_argument("--check", action="store_true", help="do not write; fail if stale")
    parser.add_argument("--contrast", action="store_true", help="print the contrast table")
    args = parser.parse_args()

    panel = os.path.abspath(args.panel)
    css_path = os.path.join(panel, "app", "globals.css")
    if not os.path.exists(css_path):
        print("panel not found at %s (pass --panel)" % panel, file=sys.stderr)
        return 2

    css = read_text(css_path)
    skins = skin_ids_and_names(panel)

    base = {}
    for m in DECL_RE.finditer(skin_block(css, "default")):
        base["%s-%s" % (m.group(1), m.group(2))] = m.group(3).strip()

    semantic = {mode: template_colours(mode) for mode in ("l", "d")}

    palettes: dict[tuple[str, str], Palette] = {}
    for skin in skins:
        tokens = tokens_for(css, skin["id"], base)
        for mode in ("l", "d"):
            palettes[(skin["id"], mode)] = Palette(tokens, mode, semantic[mode])

    problems = check_default_matches_template(palettes)
    if problems:
        print("the `default` skin no longer reproduces ShareXTheme.axaml:", file=sys.stderr)
        for p in problems:
            print("  " + p, file=sys.stderr)
        return 1

    rows = contrast_rows(skins, palettes)
    contrast_ok = True
    if args.contrast:
        contrast_ok = print_contrast(rows)
    else:
        for skin_id, mode, pairs in rows:
            for name, floor in THRESHOLDS.items():
                if pairs[name] < floor:
                    print(
                        "contrast: %s/%s %s = %.2f:1 (floor %.1f)"
                        % (skin_id, mode, name, pairs[name], floor),
                        file=sys.stderr,
                    )
                    contrast_ok = False

        for problem in loudness_problems(rows):
            print("loudness: " + problem, file=sys.stderr)
            contrast_ok = False

    stale = write_if_changed(OUT_AXAML, build_axaml(skins, palettes), args.check)
    stale |= write_if_changed(OUT_CS, build_cs(skins), args.check)

    if args.check and stale:
        print("run `python Tools/generate-skins.py` and commit the result", file=sys.stderr)
        return 1
    if not contrast_ok:
        return 1
    print("%d skins x 2 modes" % len(skins))
    return 0


if __name__ == "__main__":
    sys.exit(main())
