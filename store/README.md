# Microsoft Store submission — EquatorSolver 1.0.0.0

Everything Partner Center asks for, in the order its pages appear. The package is built from the `v1.0.0.0` tag
with `.\tools\package.ps1` and lands in `AppPackages\Equator_1.0.0.0.msixupload` (not in git).

## Pricing and availability
Your choice of markets, price and release date. Nothing in the app restricts any market.

## Properties
- Category: **Education** (Productivity also fits).
- Privacy policy: not required. The app declares no internet capability, needs no account and collects no data;
  the only thing it stores is the interface language, locally in `%LOCALAPPDATA%\Equator\settings.json`. If
  Partner Center asks anyway, publish the statement at the end of this file at a public URL (the GitHub repository
  is private, so it cannot host it).
- System requirements: Windows 10 version 2004 (build 19041) or later, x64 or ARM64. Keyboard recommended.

## Age ratings
Answer the IARC questionnaire with **No** to every content question (no violence, sexual content, gambling,
user interaction, sharing of location or personal data, purchases or unrestricted internet). The expected result
is the lowest rating in every region (for example Everyone / PEGI 3).

## Packages
Upload `AppPackages\Equator_1.0.0.0.msixupload`. It holds x64 and ARM64 packages and declares eight languages:
en-US, zh-Hans, hi, es, fr, ar, bn, pt-BR. The Windows App Certification Kit passed on this build (the two
failures it reports, Win2D metadata and "blocked executables" strings inside .NET, are optional tests and known
false positives for every WinUI 3 / .NET app).

## Store listings
One listing per package language. Copy the fields from `listing/<language>.md` and upload the screenshots from
`screenshots/<language>/` in their numbered order (1920 × 1080 PNG); each listing file has the caption for each
screenshot.

| Partner Center language | Listing | Screenshots |
| --- | --- | --- |
| English (United States) | `listing/en-US.md` | `screenshots/en/` |
| Chinese (Simplified) | `listing/zh-Hans.md` | `screenshots/zh-Hans/` |
| Hindi | `listing/hi-IN.md` | `screenshots/hi/` |
| Spanish | `listing/es-ES.md` | `screenshots/es/` |
| French | `listing/fr-FR.md` | `screenshots/fr/` |
| Arabic | `listing/ar-SA.md` | `screenshots/ar/` |
| Bangla | `listing/bn-BD.md` | `screenshots/bn/` |
| Portuguese (Brazil) | `listing/pt-BR.md` | `screenshots/pt-BR/` |

The translations were drafted without a native-speaker review; have the listings (and the app's string tables in
`src/Equator.App/Strings`) checked before or soon after release.

To regenerate the screenshots after a UI change, the app renders itself when started with
`EQUATOR_SNAPSHOT=<file.png>`, `EQUATOR_WINDOW_SIZE=1942x1092`, `EQUATOR_ZOOM=1.15`, `EQUATOR_LANG=<code>`,
`EQUATOR_THEME=light|dark`, `EQUATOR_SIDE_TAB=guide|variables` and a notebook path as its argument (see the
Diagnostics section of the main README).

## Submission options
**Restricted capability `runFullTrust`** — justification:

> EquatorSolver is a WinUI 3 desktop application built on the Windows App SDK and packaged with MSIX. Full trust
> is the standard execution model for this app type. The app does not use elevation, services, drivers or
> system-wide changes; it only reads and writes notebook files the user chooses.

**Notes for certification** (optional):

> No account, sign-in or internet connection is needed. The app opens with an example worksheet that runs on
> start. To test: type an expression such as integrate(x^2*sin(x), x) in the last cell and press Shift+Enter;
> the Guide tab on the right inserts more examples with one click. The language can be changed from the "…"
> menu → Language.

## Privacy statement (only if a URL is requested)

> EquatorSolver does not collect, store or transmit personal data. It works without an internet connection and
> without an account. Worksheets are saved only where you choose, and the app keeps one local setting (the
> interface language) on your device. Contact: <your support address>.
