<p align="center">
  <img src=".github/readme/tomte.png" alt="" width="88">
</p>

<h1 align="center">Tomte</h1>

<p align="center">
  A small Windows 11 app that lives in the top edge of your screen.<br>
  <a href="https://github.com/xditya/tomte-windows/releases/latest"><b>Download</b></a> · <a href="#build-from-source">Build from source</a> · <a href="#support">Support</a>
</p>

<p align="center">
  <img src=".github/readme/hero.gif" alt="The pointer pushes against the top edge, a pill shows the time, then the panels open and step through Shelf, Shortcuts, Media, Scratchpad and Clipboard" width="960">
</p>

When you're not using it, Tomte draws nothing, takes no screen space and never steals focus. Push the pointer against
the top edge, press a hotkey or drag a file up there, and it opens. Everything before the AI section works with no
account and no key.

## What's in it

- **Shelf.** Park files, text and links while you work, then drag them out into any app.
- **Shortcuts.** Tiles for apps, folders, web pages and scripts.
- **Media.** Play, pause, skip and seek whatever is playing, in any app that shows up in Windows' media controls.
- **Scratchpad.** One Markdown note, <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>N</kbd> away.
- **Clipboard history.** The last 50 bits of text and 50 images you copied. Anything a password manager marks as
  private is never kept.
- **Search.** <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>Space</kbd> finds apps, files and Windows settings pages. It uses
  [Everything](https://www.voidtools.com/) when that's running, and the Windows index when it isn't.
- **Timers** that count down in the pill, and an optional pill that shows the song while music plays.

It hides over fullscreen apps, games and the lock screen.

## AI, if you want it

Add your own key for Anthropic, Gemini or any OpenAI-compatible server, including a local Ollama. Nothing is sent
until you send it, and every answer says which model got it and what it cost.

**Circle to ask.** Press <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>C</kbd>, draw around anything on screen and ask about it.

<img src=".github/readme/circle.gif" alt="A loop drawn around an invoice, then the question 'does this add up? one line' and the answer: the line items sum to $3,720 but the total says $3,820" width="960">

**Voice.** Hold <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>V</kbd>, talk, let go. Insert types the words where you were
typing. Speech becomes text on your PC with whisper.cpp, so this part needs no AI at all.

<img src=".github/readme/voice.gif" alt="The spoken words show in a card at the top with Insert, Ask Tomte and Add to note, then appear in the Standup notes box" width="800">

**Drop zones.** Drag a file to the top edge and drop it on Summarize, Ask, Tables to CSV or Rename. The result lands
on the shelf.

<img src=".github/readme/drop.gif" alt="An album cover dragged to the top edge, dropped on Summarize, and the summary shown with a new file on the shelf" width="760">

It can also read PDF and Word files, change settings like dark mode or volume (it asks before each one), use MCP
servers and Agent Skills, and run jobs on a schedule you confirm. Closed an answer by mistake?
<kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>Z</kbd> brings it back for 10 seconds, without sending it again.

## Settings

<img src=".github/readme/settings.png" alt="The Tomte settings window on the AI page, with 'undo' typed in the Find a setting box and the result Undo after closing" width="640">

Every setting has a page, and <kbd>Ctrl</kbd>+<kbd>F</kbd> finds one by name or by what it does.

## Keyboard

| Hotkey | Opens |
|---|---|
| <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>I</kbd> | The panels |
| <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>Space</kbd> | Search |
| <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>A</kbd> | The AI box |
| <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>C</kbd> | Circle to ask |
| <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>V</kbd> (hold) | Voice |
| <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>N</kbd> | Scratchpad |

All of them can be changed in Settings. <kbd>Esc</kbd> closes whatever is open.

## Privacy

- No account, no telemetry, no update check.
- Your shelf, note, clipboard and AI history stay in one SQLite file in `%LOCALAPPDATA%\Tomte`. Keys are kept in
  Windows Credential Manager.
- Nothing goes to an AI provider until you send it.

## Install

Needs Windows 11 22H2 or later on x64. Download the `.msix` from the [latest release](https://github.com/xditya/tomte-windows/releases/latest), open it
and choose Install. Tomte starts hidden, with an icon in the notification area.

## Build from source

Needs the .NET 10 SDK.

```powershell
git clone https://github.com/xditya/tomte-windows.git
cd tomte-windows
dotnet build Tomte.sln
dotnet run --project src/Tomte.Shell
dotnet test Tomte.sln
```

## Support

Tomte is free and open source. If you'd like to help keep it going:

- [GitHub Sponsors](https://github.com/sponsors/xditya)
- [Buy Me a Coffee](https://buymeacoffee.com/xditya)
- [Ko-fi](https://ko-fi.com/xditya)
- UPI: `botzhub@ybl`
- USDT on BNB Smart Chain (BEP20): `0xc8cbfa55be28b0836a4ab830ea85c64516dffaf3`
- USDT on Tron (TRC20): `THDKzV4ZKeDBsrFRSbL5XZgqVQUEkPfWcW`

Send USDT only on the network named next to its address.

## License

[MIT](LICENSE)
