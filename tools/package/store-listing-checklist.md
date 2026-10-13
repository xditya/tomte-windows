# Microsoft Store listing checklist

Everything needed before a Store submission. Submitting is a separate step.

## Account and identity

- [ ] Partner Center developer account (individual or company). Check the current registration fee.
- [ ] Reserve the app name "Tomte" in Partner Center.
- [ ] Copy the Store identity (Product identity page) into `Package.appxmanifest`:
      `Identity Name`, `Identity Publisher`, `PublisherDisplayName`. The Store re-signs the package,
      so the Store build uses the Store publisher, not the dev certificate subject.
- [ ] Pass the Store publisher to the script: `New-TomtePackage.ps1 -CertSubject "CN=..."`.

## Package

- [ ] Real logos in `src/Tomte.Shell/Assets` (`StoreLogo.png` 50 px, `Square150x150Logo.png`,
      `Square44x44Logo.png`). The script warns while it still uses the magenta "TMP" placeholders.
- [ ] Version bumped (four parts; the last part must be 0 for Store uploads).
- [ ] Decide on architectures. Only x64 is built today; add arm64 if Windows on Arm is supported.
- [ ] Run the Windows App Certification Kit on the .msix and fix any failures.
- [ ] Description in the manifest (`uap:VisualElements Description`) is a real sentence.

## Restricted capabilities

Each one needs a written reason in the submission.

- [ ] `runFullTrust`: Tomte is a desktop app that uses Win32 windows, global hotkeys and raw input.
- [ ] Any later capability (for example `graphicsCaptureWithoutBorder` for screen capture) added to the
      manifest and explained here before it ships.

## Listing

- [ ] Short description and full description: what it does, plain words.
- [ ] At least one screenshot (1366 x 768 or larger). The app is hidden when idle, so show the
      island open: shelf, media controls, scratchpad.
- [ ] Store logo artwork.
- [ ] Category: Utilities and tools.
- [ ] Privacy policy URL. Required: the app reads the clipboard, can read the screen and stores
      API keys.
- [ ] Support contact (email or URL) and project website.
- [ ] Age rating questionnaire.
- [ ] Pricing: free. Check how the paid business licence fits Store policy (licensing sold outside
      the Store for a free Store app).
- [ ] Markets.
- [ ] System requirements: Windows 11 22H2 (build 22621) or later.

## Notes for certification testers

The app shows nothing when it starts, so testers need to be told how to open it.

- [ ] List the hotkeys and the top-edge gesture that open the island.
- [ ] Say that no account or API key is needed; the AI features are optional.
- [ ] Say where the settings are and how to quit.
