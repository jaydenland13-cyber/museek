# Museek (C# / WPF port)

A C# rewrite of Museek.exe (with the glass UI from museek_ui_prototype.html), rebuilt from the program logic recovered from the
original PyInstaller/Python build. Always-on-top Spotify overlay with hotkeys,
voice commands, synced lyrics, like button, quick search and a Gemini chatbot.

## Build & run
Requires Windows 10/11 and the .NET 8 SDK.

    dotnet run
    # or a single-file exe:
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

## First-run setup
1. Create an app at https://developer.spotify.com/dashboard
2. Add this Redirect URI:  http://127.0.0.1:8888/callback
3. Paste the app's **Client ID** into the setup window (no client secret needed — PKCE flow).
4. Optional: paste a Gemini API key (https://aistudio.google.com/apikey) to enable the chatbot.

## Default hotkeys (editable in Settings)
| Action | Keys | Action | Keys |
|---|---|---|---|
| Play/pause | Ctrl+Alt+Space | Toggle overlay | Ctrl+Alt+M |
| Next / Previous | Ctrl+Alt+→ / ← | Chat panel | Ctrl+Alt+C |
| Volume up / down | Ctrl+Alt+↑ / ↓ | Lock (click-through) | Ctrl+Alt+L |
| Quick search | Ctrl+Alt+F | Like track | Ctrl+Alt+U |
| Lyrics | Ctrl+Alt+Y | | |

## Voice commands (say the wake word first, default "music")
play/pause · next/skip · previous/back · volume up/louder · volume down/quieter ·
play <song> / search <song> · like · show / hide · what's playing · ask <question>

## Files
settings.json, .spotify_cache, museek_log.txt and crash_log.txt are created next to the exe.

## Differences from the original
- Spotify auth uses PKCE (no client secret stored in the app).
- Requests the two library scopes the like button actually needs.
- Voice uses Windows' built-in speech recognizer (offline) instead of Google's web API.
- Settings dialog is condensed; "Edit layout" drag mode is not ported.
- Gemini key is stored DPAPI-encrypted in settings.json (a .env file / env var still works).

## Colours, sound presets, voice
- **Colours:** Settings tab → accent + background swatches, or type any hex colour.
- **Bass boost & presets:** Visuals tab. Spotify has no EQ API, so this uses the free *Equalizer APO* driver
  (system-wide, affects all audio). Install it, enable it for your headphones/speakers in its Configurator,
  and Museek writes its config. May need admin rights the first time.
- **Voice:** mic button in the title bar. Say the wake word then a command, e.g. "music next",
  "music play <song>", "music volume up". The status bar shows what it heard.

## The new look
Dark glass card, purple→cyan progress bar (click to seek), purple→pink play button,
traffic-light title bar (red = hide, yellow = compact, green = expand), and an ambient glow
that takes its colour from the album art. Tabs: **Up Next** (real Spotify queue + "add song"),
**Lyrics** (click a line to seek), **Chat** (Gemini), **Visuals** (animated bars + volume),
**Settings** (opacity, glow, hotkeys).

Not possible with Spotify's API, so not included: the equalizer presets and a real audio
spectrum (the visualizer is decorative).

## Performance notes
- No blur/shadow effects and no endless animations (the old pulsing dot forced a redraw every frame).
- Progress bar and visualizer use render-only transforms; visualizer runs only while the Visuals tab is open and music plays.
- Timers only run while the overlay is visible. Spotify polling is adaptive (5 s while playing, 8 s+ when paused/hidden),
  and the queue is fetched only when the Up Next tab is open.
- Speech engine is created only when you push-to-talk and freed after 30 s idle.
- All animations are capped at 20 FPS; memory is trimmed after start-up and when the overlay is hidden.

## Size / moving
- Drag the little triangle at the bottom-right corner, or use Settings → Overlay size (55 – 160 %).
- Yellow dot = mini view (no tabs/footer), green dot = full view, red dot = hide.
- Dragging is custom now, so the card can touch every edge of every monitor.

## Voice (changed)
- Default is **push-to-talk**: Ctrl+Alt+V or the mic button, then say one command (no wake word).
  In this mode you can also say "play <song>", "search <song>", "ask <question>".
- Always-listening (optional) needs the wake word ("hey music"), uses only the fixed commands and a strict confidence
  threshold. It can still be fooled by loud audio from your speakers, which is why push-to-talk is the default.

## Free resizing, widgets & the Customize tab
- **Resize** like any window: drag any edge or corner (width and height independent). In mini view only the width changes.
- **Customize** tab (or right-click → Customize…): collapsible sections for layout presets (Full / Mini / Minimal / Widgets mode),
  every part of the now-playing card, tabs, pop-out widgets, colours / theme / opacity / corners, behaviour, voice, hotkeys.
- **Pop-out widgets:** Up Next, Lyrics, Chat and Visuals & sound can each become their own floating window
  (Customize → Pop-out widgets, or right-click → Pop out panel). Each remembers its position and size; "Dock" puts it back.
- **Quality of life:** snap-to-edge dragging, double-click the title bar for mini view, scroll wheel over the card changes volume,
  right-click menu (copy song name, open in Spotify, always on top, lock…).

## Apple Music (and other players) via Windows media
Customize → **Music source** (or first-run setup, or Settings → Music source) lets you switch between
**Spotify** and **Apple Music / Windows media**.

Windows media follows whatever Windows' media controls report — the Apple Music app for Windows, Apple Music /
YouTube Music in Edge or Chrome, iTunes, VLC… (Apple Music is preferred when several apps are open; you can also
pin one app by name.) Works: now playing + cover art, progress, play/pause/next/previous, seek, lyrics, chat,
colours/layout/widgets, EQ presets, scroll-wheel volume (system volume). Not available (Windows doesn't expose them):
queue, search & play, likes. Shuffle/repeat only if the app supports them.

## Sharing Museek as a single .exe
1. On a PC with the .NET 8 SDK, double-click **Publish-Museek.bat** (or run the `dotnet publish` line inside it).
2. Send friends only `publish\Museek.exe` (+ `FRIENDS-README.txt`). It is self-contained: no .NET install needed.
3. NEVER send `settings.json`, `.spotify_cache` or `.env` — they contain your own tokens/keys.

## Performance notes (round 2)
- Nothing polls while idle: "always on top" is re-asserted by a Windows foreground-change event (a 15 s safety tick remains),
  Windows-media (Apple Music) updates arrive as push events (slow 4–10 s safety poll), and the progress timer exists only
  while a song is playing and the overlay is visible (0.5–1 s, matched to how fast the bar can visibly move).
- Visualizer runs at ~15 FPS and only while its panel is on screen.
- The Customize tab is built the first time you open it (faster start, less memory); settings writes are coalesced.
- **Efficiency mode** (Customize → Behaviour, on by default): below-normal priority + Windows EcoQoS.
- `runtimeconfig.template.json` makes the .NET GC return memory sooner; Publish-Museek.bat now uses ReadyToRun.

## New: updater, tour, accessibility, share card, mood requests, voice replies, command palette

### In-app updater (set it up once, before you publish)
1. Open `Services/AppInfo.cs` and set `DefaultUpdateUrl` to your GitHub repo's latest-release API URL, e.g.
   `https://api.github.com/repos/YOURNAME/Museek/releases/latest`
   (or to a small JSON file: `{"version":"1.1.0","url":"https://…/Museek.exe","sha256":"…","notes":"…"}`).
2. To release an update: raise `<Version>` in `Museek.csproj`, run `Publish-Museek.bat`, create a GitHub Release with the
   tag (e.g. `v1.1.0`) and attach `publish\Museek.exe`.
3. Friends' copies check once a day (20 s after start). When a newer version exists, the footer link says
   "Update available"; Customize → Updates & help → "Install update & restart" downloads it, verifies the SHA-256 when one
   is published, swaps the exe and restarts. Only https links are accepted and nothing installs without a click.
   It can't update when run from source (`dotnet run`) or from a folder it can't write to.

### Other additions
- **First-run tour** (5 short steps, once; replay from Customize → Updates & help or the command palette).
- **Accessibility** (Customize → Accessibility): high contrast (also follows Windows high-contrast mode), reduce motion
  (also follows Windows animation setting), easy-read font, text & interface size 70–160 %, screen-reader labels.
- **Share card:** right-click → Create share card (or the palette). 1080×1350 PNG with cover, title, progress and the
  current lyric line; copied to the clipboard and saved in Pictures\Museek.
- **Mood requests:** say or type "play something chill" / "play some jazz music" / "mood: rainy day". Gemini picks 5 songs;
  on Spotify the first plays and the rest are queued; with Apple Music / Windows media you get the list as ideas.
- **Voice replies:** Customize → Voice → "Chatbot speaks its answers" (Never / When I ask by voice / Always). Say "stop talking".
- **Custom wake word:** Customize → Voice (only used when push-to-talk is off).
- **Command palette:** Ctrl+Alt+P (or right-click → Command palette). Commands, layouts, widgets, accents, accessibility,
  source switching, plus typed actions such as `volume 40`, `seek 1:30`, `accent #FF5500`, "Play something: …",
  "Search & play: …", "Ask the chatbot: …".

## Looks, welcome screen & sign-in
- **Looks (Customize → Look):** eight complete styles — Aurora, Midnight Pro, Frost (light), Neon, Ember, Emerald, Mono,
  Crystal — each shown as a live mock-up (panel, cover, text, progress bar and the actual play-button shape). Click one to apply
  it instantly; "Undo last look change" brings the previous one back. A look sets colours, corner radii, border, progress-bar
  shape, play-button style (gradient / solid / square / outline / ghost), title-bar dots, glow and spacing; every detail stays
  adjustable in the other sections. "Spacing" switches between Comfortable and Compact. Looks are also in the command palette.
- **Welcome / sign-in screen (shown at start):**
  - First run: connect your music (Spotify sign-in with your Client ID, or Apple Music / Windows media with no account),
    choose a look from the previews, then optional extras (Gemini key, start with Windows).
  - Later starts: a short "Welcome back" screen that continues by itself after 3 seconds (click anywhere to stay).
    If Spotify needs a new login it asks you to sign in. Turn the screen off in Customize → Account & sign-in.
  - Customize → Account & sign-in: reopen the screen, sign out / switch Spotify account.
  - Saved Spotify logins are reused silently; the browser only opens when you press "Sign in with Spotify".
