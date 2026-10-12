# Stremio UWP VLC

Stremio for **Xbox Developer Mode** with **VLC built in**. Streams that fail in the Edge browser, or in apps that use the Edge engine, such as MKV files, HEVC/4K, and AC3/DTS/TrueHD audio, play here because playback is handled by VLC.

> **VLC is wrapped into the app.** The app ships with LibVLC (the engine behind VLC media player) inside it. You don't need to install VLC or anything else; everything is in the one package.

![Banner](https://github.com/Misunderstood-Wookiee/Stremio-UWP/assets/22002023/a7291857-0935-4067-a2db-03fb7adaae33)

## Features
- The normal Stremio interface (web.stremio.com): your account, addons, library and Continue Watching all work as usual.
- Playback through the built-in VLC, using Stremio's own player controls, seek bar, audio and subtitle menus, and addon subtitles.
- Watch progress is saved, and next-episode works.
- If a stream's connection drops partway through, playback reconnects and carries on.
- Full controller support on Xbox, including typing your email and password with the on-screen keyboard.
- subtitles work yipee

## Install on Xbox
This app is made for Xbox consoles in **Developer Mode**, which is how Xbox lets you install apps from outside the Store.

1. **Turn on Developer Mode.** Follow Microsoft's guide: [Xbox Developer Mode activation](https://learn.microsoft.com/windows/uwp/xbox-apps/devkit-activation). It needs a Microsoft Partner Center developer account, which has a one-time fee.
2. **Download the app.** On your PC, open this repository's [Releases](../../releases/latest) page, download `StremioUWPVLC-x64.zip` and unzip it. Inside is the app (`Stremio_…_x64.msix`) and a `Dependencies` folder with its 5 dependency packages (`.appx`).
3. **Open the Device Portal.** On the Xbox, in **Dev Home**, open **Remote Access Settings**, turn on the Xbox Device Portal and set a username and password. On your PC, open the address it shows, e.g. `https://192.168.1.20:11443`. If your browser warns about the certificate, that's expected; continue anyway.
4. **Install.** In the Device Portal, under **My games & apps**, choose **Add**:
   - Choose the `.msix` file and select **Next**.
   - Add the 5 files from the `Dependencies` folder and select **Next**.
   - Select **Start** and wait for the install to finish.
5. **Open it.** Back in Dev Home, the app appears in the list as **Stremio**. Select it to launch.

**Updating:** install a newer release the same way. It replaces the old version and keeps your sign-in.

## Getting streams to play
- **Debrid services (recommended):** set up your addon (e.g. Torrentio, AIOStreams) with Real-Debrid, AllDebrid, TorBox or similar. Those give direct video links, which play straight away with no extra setup.
- **Torrents** need a Stremio streaming server, which the Xbox can't run itself. Run Stremio on a PC on the same network, then in the app go to **Settings → Streaming** and add your PC's address, e.g. `http://192.168.1.10:11470`. Leave the default address (`http://127.0.0.1:11470`) selected if you only use debrid.


## Troubleshooting
The app writes a log you can download from the Device Portal: **File explorer → LocalAppData →** the folder starting with `311105be-` **→ LocalState → `stremio-vlc.log`**. Include it when reporting a problem.


## How it works
- web.stremio.com runs in a WebView2. When it runs inside a native app, Stremio sends its playback commands to the app; this is the same protocol the official Windows app uses with mpv. The app answers them with LibVLC (`Stremio/Shell/ShellHost.cs`, `Stremio/Playback/VlcShellPlayer.cs`).
- VLC draws the video underneath the page, and the page turns transparent while a stream plays, so Stremio's own controls appear on top.
- The app downloads https streams with Windows' own HTTP stack and feeds them to VLC (`Stremio/Playback/HttpMediaInput.cs`), and it answers Stremio's streaming server checks for direct links itself (`Stremio/Shell/BuiltInStreamingServer.cs`).
- The controller is translated into the keyboard navigation Stremio already supports (`Stremio/Input/ControllerBridge.cs`).


## Credits and license
- Based on [Stremio-UWP](https://github.com/Misunderstood-Wookiee/Stremio-UWP) by Misunderstood-Wookiee.
- Video playback by [LibVLC](https://www.videolan.org/vlc/libvlc.html) and [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) from VideoLAN (LGPL-2.1-or-later), bundled in the app.
- Licensed under [GPLv3](LICENSE).
- Unofficial. This project is not affiliated with Stremio (www.stremio.com) or VideoLAN.
