Prerequisites
-------------
- BlackHole 2ch installed (or another loopback audio device).
- Spotify desktop app running with playback device set to This Computer.
- System audio output set to BlackHole 2ch (or whichever device is configured).
- recorder binary placed at Tools/recorder alongside the app executable.

Configuration (appsettings.json)
---------------------------------
Recording:AudioDevice   — audio device name passed to recorder.

Running
-------
Start the app. Open a terminal window and cd into the app's folder. Type: ./SpotifyRemote
A browser window opens automatically at the player page. Authenticate with Spotify on
first run.

Using the queue
---------------
Open a playlist, check the tracks to record, then click Play Selected.
Tracks play one at a time under app control. Only Abort is available during
playback — no skip or pause.

There is a 3-second gap between tracks to allow the recorder to finalise
each file before the next one starts.

The app waits up to 10 seconds for the recorder to signal readiness before
starting Spotify playback. If the signal does not appear within that window,
playback starts anyway.

While a queue is running:
- The display and system are prevented from sleeping.

Output
------
Files are saved to ~/ripped/[playlist name]/[artists] - [track name].[wav|flac]
Format is selected per session via the toggle in the UI before starting the queue.

Things to consider
------------------
Send system sound effects through the computer rather than selected audio device,
set this option in System Settings => Sound. Any sounds played while recording
will be captured.
