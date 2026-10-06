MUSEEK - quick start
====================

1) Put Museek.exe in its own folder (e.g. Documents\Museek) and double-click it.
   (It saves its settings next to the exe.)

2) Windows may say "Windows protected your PC" because the app isn't code-signed.
   Click "More info" -> "Run anyway".

3) First launch asks where you listen:

   * Apple Music / other players  ->  nothing to set up.
     Open the Apple Music app (or music.apple.com / YouTube Music in your browser), play a song, done.
     (No queue, search or likes with this source.)

   * Spotify  ->  needs YOUR OWN free developer app:
       - Spotify now requires the app owner to have Spotify Premium.
       - Go to developer.spotify.com/dashboard, create an app,
         add the Redirect URI  http://127.0.0.1:8888/callback  , copy its Client ID into Museek.
       - A browser window opens once to log in.

4) Optional chatbot: paste a free Gemini key from aistudio.google.com/apikey
   (use your own - don't share keys).

Handy:
  Right-click the overlay = menu      Double-click the title bar = mini view
  Scroll over the song card = volume  Drag any edge/corner to resize
  Customize tab = colours, layout, pop-out widgets, voice (push-to-talk: Ctrl+Alt+V)
  Ctrl+Alt+M = show/hide

Bass boost presets need the free "Equalizer APO" app (optional).

NEW
---
* Ctrl+Alt+P opens the command palette: type what you want ("accent blue", "mini view", "play something chill").
* Updates: when the footer says "Update available", open Customize -> Updates & help -> Install update.
* Accessibility options (high contrast, bigger text, reduced motion) are in Customize -> Accessibility.
