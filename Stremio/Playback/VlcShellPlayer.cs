using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Stremio.Diagnostics;
using Stremio.Shell;
using Windows.Data.Json;
using Windows.UI.Core;

namespace Stremio.Playback
{
    /// <summary>
    /// Plays streams with LibVLC while speaking the subset of libmpv's property/command protocol
    /// that stremio-video's ShellVideo sends to a native shell. This lets the Stremio web UI drive
    /// VLC exactly the way it drives mpv in the official desktop app: its own controls, subtitles,
    /// next-episode handling and watch progress keep working.
    /// </summary>
    /// <remarks>
    /// Public members must be called on the UI thread. LibVLC is only touched on a private serial
    /// worker, because calls like Stop() block until VLC's threads exit, and LibVLC must never be
    /// called from inside one of its own event callbacks.
    /// </remarks>
    internal sealed class VlcShellPlayer : IDisposable
    {
        // ShellVideo waits for mpv-version before loading and picks its loadfile syntax from it.
        private const string ReportedMpvVersion = "0.39.0";
        private const int NetworkCachingMs = 3000;
        private const int MaxReconnects = 5;
        // Same tolerance ShellVideo uses to tell a dropped connection from a real end of file.
        private const long EarlyEndToleranceMs = 60000;
        private const string PlaybackErrorMessage = "VLC could not play this stream.";
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan TimeReportInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan ReadyFallbackDelay = TimeSpan.FromSeconds(2);
        private static readonly string[] AllowedSchemes = { "http", "https", "rtsp", "rtmp", "rtp", "udp", "mms", "ftp", "smb" };

        // Props describing the loaded media; they are forgotten on every load so they get re-sent.
        private static readonly string[] MediaProps =
        {
            "path", "time-pos", "duration", "paused-for-cache", "cache-buffering-state",
            "track-list", "aid", "sid", "video-params",
        };

        private readonly CoreDispatcher _dispatcher;
        private readonly object _queueLock = new object();
        private Task _queue = Task.CompletedTask;

        // UI thread state.
        private readonly HashSet<string> _observed = new HashSet<string>();
        private readonly Dictionary<string, IJsonValue> _props = new Dictionary<string, IJsonValue>();
        private long _loadId;
        private bool _hasMedia;
        private bool _readySent;
        private bool _durationSent;
        private DateTime _lastTimeReport = DateTime.MinValue;

        // Set once LibVLC has started or failed to; _libVlc, _mediaPlayer and _initError are only
        // written before that.
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private int _initStarted;
        private LibVLC _libVlc;
        private MediaPlayer _mediaPlayer;
        private string _initError;

        // Worker state.
        private string _url;
        private List<string> _mediaOptions = new List<string>();
        private HttpMediaInput _input;
        private int _vlcLogLines;
        private bool _hardwareDecoding = true;
        private bool _hasPlayed;
        private bool _inputStarted;
        private int _reconnects;
        private bool _wantPaused;
        private int _wantVolume = 100;
        private bool _wantMute;
        private float _wantRate = 1f;
        private int? _wantAudioTrack;
        private int? _wantSpuTrack;
        private long _pendingSeekMs = -1;
        private long _spuDelayUs;
        private bool _keepAspect = true;
        private double _panscan;
        private int _viewWidth;
        private int _viewHeight;

        // Shared between VLC callback threads, the worker and the UI thread.
        private long _workerLoadId;
        private long _lastTimeMs;
        private long _lengthMs;
        private int _trackRefreshQueued;

        public VlcShellPlayer(CoreDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _props["mpv-version"] = JsonValue.CreateStringValue(ReportedMpvVersion);
            _props["ffmpeg-version"] = JsonValue.CreateStringValue("LibVLC");
            _props["pause"] = JsonValue.CreateBooleanValue(false);
            _props["volume"] = JsonValue.CreateNumberValue(100);
            _props["mute"] = JsonValue.CreateBooleanValue(false);
            _props["speed"] = JsonValue.CreateNumberValue(1);
        }

        /// <summary>Raised with a shell signal name and its payload, e.g. "mpv-prop-change".</summary>
        public event Action<string, IJsonValue> SignalRaised;

        /// <summary>Raised once LibVLC exists so the VideoView can be attached to it.</summary>
        public event Action<MediaPlayer> MediaPlayerCreated;

        /// <summary>Raised when frames of the current stream start or stop being shown.</summary>
        public event Action<bool> VideoVisibleChanged;

        public void Initialize(string[] swapChainOptions)
        {
            var options = new List<string>(swapChainOptions)
            {
                "--no-video-title-show",
                "--no-osd",
                "--no-sub-autodetect-file",
                "--http-reconnect",
                "--network-caching=" + NetworkCachingMs,
            };
            if (Interlocked.Exchange(ref _initStarted, 1) == 1)
            {
                return;
            }
            // Not on the worker queue: a load that arrives first waits on the worker for this.
            Task.Run(() =>
            {
                try
                {
                    Log.Write("Starting LibVLC");
                    Core.Initialize();
#if DEBUG
                    _libVlc = new LibVLC(true, options.ToArray());
#else
                    _libVlc = new LibVLC(false, options.ToArray());
#endif
                    _mediaPlayer = new MediaPlayer(_libVlc);
                    Subscribe(_mediaPlayer);
                    _libVlc.Log += OnLibVlcLog;
                    Log.Write("LibVLC " + _libVlc.Version + " ready");
                }
                catch (Exception ex)
                {
                    Log.Error("LibVLC failed to start", ex);
                    _initError = ex.Message;
                    _mediaPlayer = null;
                    _libVlc = null;
                }
                finally
                {
                    _ready.Set();
                }
                var mediaPlayer = _mediaPlayer;
                if (mediaPlayer != null)
                {
                    var version = "LibVLC " + _libVlc.Version;
                    RunOnUi(() =>
                    {
                        _props["ffmpeg-version"] = JsonValue.CreateStringValue(version);
                        MediaPlayerCreated?.Invoke(mediaPlayer);
                    });
                }
            });
        }

        /// <summary>Marks VLC as unusable so loads fail with a message instead of waiting.</summary>
        public void SetUnavailable(string reason)
        {
            if (Interlocked.Exchange(ref _initStarted, 1) == 1)
            {
                return;
            }
            _initError = reason;
            _ready.Set();
        }

        /// <summary>Handles an "mpv-*" method sent by stremio-web.</summary>
        public void HandleShellMethod(string method, IJsonValue args)
        {
            if (args == null)
            {
                return;
            }
            switch (method)
            {
                case "mpv-observe-prop":
                    if (args.ValueType == JsonValueType.String)
                    {
                        Observe(args.GetString());
                    }
                    break;
                case "mpv-set-prop":
                    if (args.ValueType == JsonValueType.Array)
                    {
                        var setArgs = args.GetArray();
                        if (setArgs.Count >= 2 && setArgs[0].ValueType == JsonValueType.String)
                        {
                            SetProperty(setArgs[0].GetString(), setArgs[1]);
                        }
                    }
                    break;
                case "mpv-command":
                    if (args.ValueType == JsonValueType.Array)
                    {
                        RunCommand(args.GetArray());
                    }
                    break;
            }
        }

        public void SetViewSize(double width, double height)
        {
            var w = (int)Math.Round(width);
            var h = (int)Math.Round(height);
            if (w <= 0 || h <= 0)
            {
                return;
            }
            Enqueue(() =>
            {
                _viewWidth = w;
                _viewHeight = h;
                if (!_keepAspect || _panscan > 0)
                {
                    ApplyVideoScale();
                }
            });
        }

        public void Dispose()
        {
            SignalRaised = null;
            MediaPlayerCreated = null;
            VideoVisibleChanged = null;
            Enqueue(() =>
            {
                StopPlayer();
                _mediaPlayer?.Dispose();
                _mediaPlayer = null;
                _libVlc?.Dispose();
                _libVlc = null;
            });
        }

        private void Observe(string name)
        {
            _observed.Add(name);
            IJsonValue value;
            if (_props.TryGetValue(name, out value))
            {
                RaisePropChange(name, value);
            }
        }

        private void RunCommand(JsonArray args)
        {
            var parts = new List<string>();
            foreach (var arg in args)
            {
                parts.Add(arg.ValueType == JsonValueType.String ? arg.GetString() : arg.Stringify());
            }
            if (parts.Count == 0)
            {
                return;
            }
            switch (parts[0])
            {
                case "loadfile":
                    if (parts.Count >= 2)
                    {
                        Load(parts[1], ParseStartSeconds(parts));
                    }
                    break;
                case "stop":
                    Stop();
                    break;
            }
        }

        private void SetProperty(string name, IJsonValue value)
        {
            switch (name)
            {
                case "pause":
                {
                    var paused = AsBool(value);
                    if (paused.HasValue)
                    {
                        var p = paused.Value;
                        Enqueue(() =>
                        {
                            _wantPaused = p;
                            if (_hasPlayed)
                            {
                                _mediaPlayer.SetPause(p);
                            }
                        });
                    }
                    break;
                }
                case "time-pos":
                {
                    var seconds = AsNumber(value);
                    if (seconds.HasValue && seconds.Value >= 0)
                    {
                        var ms = (long)(seconds.Value * 1000);
                        Enqueue(() =>
                        {
                            if (_hasPlayed && _mediaPlayer.IsSeekable)
                            {
                                _mediaPlayer.Time = ms;
                            }
                            else if (!_hasPlayed)
                            {
                                _pendingSeekMs = ms;
                            }
                        });
                    }
                    break;
                }
                case "speed":
                {
                    var speed = AsNumber(value);
                    if (speed.HasValue && speed.Value > 0)
                    {
                        var rate = (float)speed.Value;
                        Enqueue(() =>
                        {
                            _wantRate = rate;
                            if (_hasPlayed)
                            {
                                _mediaPlayer.SetRate(rate);
                            }
                        });
                        Publish(name, JsonValue.CreateNumberValue(speed.Value));
                    }
                    break;
                }
                case "volume":
                {
                    var volume = AsNumber(value);
                    if (volume.HasValue)
                    {
                        // stremio-web allows boosting up to 200% when it runs inside a shell; so does VLC.
                        var v = (int)Math.Round(Math.Max(0, Math.Min(200, volume.Value)));
                        Enqueue(() =>
                        {
                            _wantVolume = v;
                            if (_hasPlayed)
                            {
                                _mediaPlayer.Volume = v;
                            }
                        });
                        Publish(name, JsonValue.CreateNumberValue(v));
                    }
                    break;
                }
                case "mute":
                {
                    var muted = AsBool(value);
                    if (muted.HasValue)
                    {
                        var m = muted.Value;
                        Enqueue(() =>
                        {
                            _wantMute = m;
                            if (_hasPlayed)
                            {
                                _mediaPlayer.Mute = m;
                            }
                        });
                        Publish(name, JsonValue.CreateBooleanValue(m));
                    }
                    break;
                }
                case "aid":
                {
                    var track = ParseTrackId(value);
                    Enqueue(() =>
                    {
                        _wantAudioTrack = track;
                        ApplyTrackPreferences();
                    });
                    break;
                }
                case "sid":
                {
                    var track = ParseTrackId(value);
                    Enqueue(() =>
                    {
                        _wantSpuTrack = track;
                        ApplyTrackPreferences();
                    });
                    break;
                }
                case "sub-delay":
                {
                    var seconds = AsNumber(value);
                    if (seconds.HasValue)
                    {
                        var us = (long)(seconds.Value * 1000000);
                        Enqueue(() =>
                        {
                            _spuDelayUs = us;
                            if (_hasPlayed)
                            {
                                _mediaPlayer.SetSpuDelay(us);
                            }
                        });
                        Publish(name, value);
                    }
                    break;
                }
                case "keepaspect":
                {
                    var keep = AsBool(value);
                    if (keep.HasValue)
                    {
                        var k = keep.Value;
                        Enqueue(() =>
                        {
                            _keepAspect = k;
                            ApplyVideoScale();
                        });
                    }
                    break;
                }
                case "panscan":
                {
                    var panscan = AsNumber(value);
                    if (panscan.HasValue)
                    {
                        var p = panscan.Value;
                        Enqueue(() =>
                        {
                            _panscan = p;
                            ApplyVideoScale();
                        });
                    }
                    break;
                }
                case "hwdec":
                {
                    var enabled = !(value.ValueType == JsonValueType.String && value.GetString() == "no");
                    Enqueue(() => _hardwareDecoding = enabled);
                    break;
                }
                case "sub-scale":
                case "sub-pos":
                    // LibVLC 3 cannot restyle subtitles mid-playback. Echo the value so the web UI
                    // still shows what the user picked.
                    Publish(name, value);
                    break;
                // vo, osc, sub-ass-override, sub colors, etc. have no LibVLC equivalent.
            }
        }

        private void Load(string url, double startSeconds)
        {
            BeginTransition(true);
            var id = _loadId;
            Publish("path", JsonValue.CreateStringValue(url));
            Publish("paused-for-cache", JsonValue.CreateBooleanValue(true));

            var mediaOptions = new List<string>();
            string error;
            var mediaUrl = BuiltInStreamingServer.ResolveMediaUrl(url, mediaOptions, out error);
            if (mediaUrl == null || !IsAllowedUrl(mediaUrl))
            {
                Log.Write("Can't play " + Log.DescribeUrl(url) + ": " + (error ?? "unsupported scheme"));
                Enqueue(() =>
                {
                    StopPlayer();
                    Interlocked.Exchange(ref _workerLoadId, id);
                    _url = null;
                    _hasPlayed = false;
                });
                RaiseEnded("error", error ?? "This stream type is not supported.");
                return;
            }

            Log.Write("Load " + Log.DescribeUrl(mediaUrl) + (startSeconds > 0 ? " at " + startSeconds + "s" : ""));
            Enqueue(() =>
            {
                if (!_ready.Wait(StartupTimeout) || _mediaPlayer == null)
                {
                    var message = "VLC failed to start" + (_initError != null ? ": " + _initError : " (timed out).");
                    Log.Write(message);
                    RunOnUi(() =>
                    {
                        if (id == _loadId)
                        {
                            RaiseEnded("error", message);
                        }
                    });
                    return;
                }
                StopPlayer();
                Interlocked.Exchange(ref _workerLoadId, id);
                Interlocked.Exchange(ref _lengthMs, 0);
                _url = mediaUrl;
                _mediaOptions = mediaOptions;
                _hasPlayed = false;
                _reconnects = 0;
                _pendingSeekMs = -1;
                _wantAudioTrack = null;
                _wantSpuTrack = null;
                StartPlayback(startSeconds);
            });
        }

        private void Stop()
        {
            if (_hasMedia)
            {
                Log.Write("Stop");
            }
            BeginTransition(false);
            var id = _loadId;
            Enqueue(() =>
            {
                StopPlayer();
                Interlocked.Exchange(ref _workerLoadId, id);
                _url = null;
                _hasPlayed = false;
            });
        }

        private void BeginTransition(bool hasMedia)
        {
            _loadId++;
            _readySent = false;
            _durationSent = false;
            foreach (var prop in MediaProps)
            {
                _props.Remove(prop);
            }
            RaiseVideoReady(_loadId, false);
            VideoVisibleChanged?.Invoke(false);
            _hasMedia = hasMedia;
        }

        // Worker: (re)opens _url, optionally resuming at startSeconds.
        private void StartPlayback(double startSeconds)
        {
            StopPlayer();
            _inputStarted = false;
            Interlocked.Exchange(ref _lastTimeMs, (long)(startSeconds * 1000));
            var options = new List<string>
            {
                ":network-caching=" + NetworkCachingMs,
                ":http-reconnect",
            };
            if (startSeconds > 0)
            {
                options.Add(":start-time=" + startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            }
            if (!_hardwareDecoding)
            {
                options.Add(":avcodec-hw=none");
            }
            options.AddRange(_mediaOptions);
            Media media;
            if (UseWindowsHttp(_url))
            {
                // VLC's own https support failed on the Xbox; download with Windows' HTTP stack.
                options.Add(":file-caching=" + NetworkCachingMs);
                _input = new HttpMediaInput(_url, GetMediaOption(":http-user-agent="), GetMediaOption(":http-referrer="));
                media = new Media(_libVlc, _input, options.ToArray());
            }
            else
            {
                media = new Media(_libVlc, _url, FromType.FromLocation, options.ToArray());
            }
            using (media)
            {
                _mediaPlayer.Play(media);
            }
        }

        // Worker. Stops playback; a pending download is broken off first so Stop doesn't wait on it.
        private void StopPlayer()
        {
            _input?.Abort();
            _mediaPlayer?.Stop();
            _input?.Dispose();
            _input = null;
        }

        // HLS playlists need VLC to fetch the segments itself, so they stay on VLC's own HTTP code.
        private static bool UseWindowsHttp(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                uri.Scheme == Uri.UriSchemeHttps &&
                !uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        private string GetMediaOption(string prefix)
        {
            foreach (var option in _mediaOptions)
            {
                if (option.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return option.Substring(prefix.Length);
                }
            }
            return null;
        }

        // Called on VLC threads. Only warnings and errors, and only so many, to keep the log small.
        private void OnLibVlcLog(object sender, LogEventArgs e)
        {
            try
            {
                if (e.Level < LogLevel.Warning || Interlocked.Increment(ref _vlcLogLines) > 400)
                {
                    return;
                }
                Log.Write("vlc " + e.Level.ToString().ToLowerInvariant() + " [" + e.Module + "] " + e.Message);
            }
            catch
            {
                // Never let logging break VLC's thread.
            }
        }

        private void Subscribe(MediaPlayer mediaPlayer)
        {
            mediaPlayer.Playing += (s, e) => QueueFromVlc(OnPlaying);
            mediaPlayer.Paused += (s, e) =>
            {
                var time = Interlocked.Read(ref _lastTimeMs);
                PostFromVlc(id =>
                {
                    Publish("pause", JsonValue.CreateBooleanValue(true));
                    Publish("time-pos", JsonValue.CreateNumberValue(time / 1000.0));
                });
            };
            mediaPlayer.Buffering += (s, e) =>
            {
                var cache = e.Cache;
                PostFromVlc(id =>
                {
                    Publish("cache-buffering-state", JsonValue.CreateNumberValue(Math.Round(cache)));
                    Publish("paused-for-cache", JsonValue.CreateBooleanValue(cache < 100f));
                });
            };
            mediaPlayer.TimeChanged += (s, e) =>
            {
                var time = e.Time;
                Interlocked.Exchange(ref _lastTimeMs, time);
                PostFromVlc(id => OnTimeChanged(time));
            };
            mediaPlayer.LengthChanged += (s, e) =>
            {
                var length = e.Length;
                Interlocked.Exchange(ref _lengthMs, length);
                PostFromVlc(id => OnLengthChanged(length));
            };
            mediaPlayer.Vout += (s, e) =>
            {
                if (e.Count > 0)
                {
                    QueueFromVlc(OnVideoOutput);
                }
            };
            mediaPlayer.ESAdded += (s, e) => QueueTrackRefresh();
            mediaPlayer.ESDeleted += (s, e) => QueueTrackRefresh();
            mediaPlayer.ESSelected += (s, e) => QueueTrackRefresh();
            mediaPlayer.EndReached += (s, e) => QueueFromVlc(OnEndReached);
            mediaPlayer.EncounteredError += (s, e) => QueueFromVlc(OnEncounteredError);
        }

        // Worker.
        private void OnPlaying(long id)
        {
            _hasPlayed = true;
            if (!_inputStarted)
            {
                _inputStarted = true;
                Log.Write("Playing");
                if (_pendingSeekMs >= 0 && _mediaPlayer.IsSeekable)
                {
                    _mediaPlayer.Time = _pendingSeekMs;
                }
                _pendingSeekMs = -1;
                if (_spuDelayUs != 0)
                {
                    _mediaPlayer.SetSpuDelay(_spuDelayUs);
                }
                ApplyVideoScale();
            }
            _mediaPlayer.Volume = _wantVolume;
            _mediaPlayer.Mute = _wantMute;
            if (Math.Abs(_mediaPlayer.Rate - _wantRate) > 0.001f)
            {
                _mediaPlayer.SetRate(_wantRate);
            }
            if (_wantPaused)
            {
                _mediaPlayer.SetPause(true);
            }
            ApplyTrackPreferences();
            RefreshTracks(id);

            var hasVideoOutput = _mediaPlayer.VoutCount > 0;
            var paused = _wantPaused;
            RunOnUi(() =>
            {
                if (id != _loadId)
                {
                    return;
                }
                Publish("pause", JsonValue.CreateBooleanValue(paused));
                Publish("paused-for-cache", JsonValue.CreateBooleanValue(false));
                if (hasVideoOutput)
                {
                    MarkVideoReady(id);
                }
                else
                {
                    // Audio-only streams never create a video output.
                    ScheduleReadyFallback(id);
                }
            });
        }

        // Worker.
        private void OnVideoOutput(long id)
        {
            uint width = 0;
            uint height = 0;
            _mediaPlayer.Size(0, ref width, ref height);
            RunOnUi(() =>
            {
                if (id != _loadId)
                {
                    return;
                }
                if (width > 0 && height > 0)
                {
                    var videoParams = new JsonObject();
                    videoParams.SetNamedValue("w", JsonValue.CreateNumberValue(width));
                    videoParams.SetNamedValue("h", JsonValue.CreateNumberValue(height));
                    videoParams.SetNamedValue("dw", JsonValue.CreateNumberValue(width));
                    videoParams.SetNamedValue("dh", JsonValue.CreateNumberValue(height));
                    Publish("video-params", videoParams);
                }
                MarkVideoReady(id);
            });
        }

        // Worker.
        private void OnEndReached(long id)
        {
            // VLC reports a dropped HTTP connection as a normal end of stream. If that happens well
            // before the end, pick the stream back up where it stopped instead of ending playback.
            var length = Interlocked.Read(ref _lengthMs);
            var time = Interlocked.Read(ref _lastTimeMs);
            if (_hasPlayed && length > 0 && time + EarlyEndToleranceMs < length && TryReconnect(time / 1000.0))
            {
                return;
            }
            // A stream that ends before it ever played failed to open; reporting it as finished would
            // make Stremio skip to the next episode.
            var failedToOpen = !_hasPlayed;
            Log.Write(failedToOpen ? "Stream ended before it played" : "Stream finished");
            RunOnUi(() =>
            {
                if (id == _loadId)
                {
                    RaiseEnded(failedToOpen ? "error" : "eof", failedToOpen ? PlaybackErrorMessage : null);
                }
            });
        }

        // Worker.
        private void OnEncounteredError(long id)
        {
            var length = Interlocked.Read(ref _lengthMs);
            var time = Interlocked.Read(ref _lastTimeMs);
            if (_hasPlayed && TryReconnect(length > 0 ? time / 1000.0 : 0))
            {
                return;
            }
            Log.Write("VLC could not play the stream");
            RunOnUi(() =>
            {
                if (id == _loadId)
                {
                    RaiseEnded("error", PlaybackErrorMessage);
                }
            });
        }

        // Worker.
        private bool TryReconnect(double resumeSeconds)
        {
            if (_url == null || _reconnects >= MaxReconnects)
            {
                return false;
            }
            _reconnects++;
            Log.Write("Stream dropped, reconnecting at " + Math.Round(resumeSeconds) + "s (attempt " + _reconnects + ")");
            var id = Interlocked.Read(ref _workerLoadId);
            RunOnUi(() =>
            {
                if (id == _loadId)
                {
                    Publish("paused-for-cache", JsonValue.CreateBooleanValue(true));
                }
            });
            StartPlayback(resumeSeconds);
            return true;
        }

        // Worker.
        private void ApplyTrackPreferences()
        {
            if (_mediaPlayer == null)
            {
                return;
            }
            if (_wantAudioTrack.HasValue && _mediaPlayer.AudioTrack != _wantAudioTrack.Value)
            {
                _mediaPlayer.SetAudioTrack(_wantAudioTrack.Value);
            }
            if (_wantSpuTrack.HasValue && _mediaPlayer.Spu != _wantSpuTrack.Value)
            {
                _mediaPlayer.SetSpu(_wantSpuTrack.Value);
            }
        }

        // Worker. Maps stremio-web's video scale modes (contain / cover / fill), which it sends as
        // mpv keepaspect + panscan, onto VLC's aspect ratio and crop settings.
        private void ApplyVideoScale()
        {
            if (_mediaPlayer == null)
            {
                return;
            }
            var viewAspect = _viewWidth > 0 && _viewHeight > 0 ? _viewWidth + ":" + _viewHeight : null;
            if (!_keepAspect && viewAspect != null)
            {
                _mediaPlayer.CropGeometry = null;
                _mediaPlayer.AspectRatio = viewAspect;
            }
            else if (_panscan > 0 && viewAspect != null)
            {
                _mediaPlayer.AspectRatio = null;
                _mediaPlayer.CropGeometry = viewAspect;
            }
            else
            {
                _mediaPlayer.AspectRatio = null;
                _mediaPlayer.CropGeometry = null;
            }
        }

        private void QueueTrackRefresh()
        {
            // A file with many tracks raises a burst of ES events; refresh once per burst.
            if (Interlocked.Exchange(ref _trackRefreshQueued, 1) == 1)
            {
                return;
            }
            Enqueue(() =>
            {
                Interlocked.Exchange(ref _trackRefreshQueued, 0);
                if (_mediaPlayer == null || _url == null)
                {
                    return;
                }
                ApplyTrackPreferences();
                RefreshTracks(Interlocked.Read(ref _workerLoadId));
            });
        }

        // Worker. Publishes the tracks in mpv's track-list shape. Track ids are VLC ES ids + 1,
        // because ShellVideo treats an id of 0 as "no track".
        private void RefreshTracks(long id)
        {
            MediaTrack[] mediaTracks;
            using (var media = _mediaPlayer.Media)
            {
                mediaTracks = media != null ? media.Tracks : new MediaTrack[0];
            }
            var currentAudio = _mediaPlayer.AudioTrack;
            var currentSpu = _mediaPlayer.Spu;
            var tracks = new List<TrackInfo>();
            AddTracks(tracks, "audio", TrackType.Audio, _mediaPlayer.AudioTrackDescription, mediaTracks, currentAudio);
            AddTracks(tracks, "sub", TrackType.Text, _mediaPlayer.SpuDescription, mediaTracks, currentSpu);

            RunOnUi(() =>
            {
                if (id != _loadId)
                {
                    return;
                }
                Publish("track-list", TracksToJson(tracks));
                Publish("aid", ToMpvTrackId(currentAudio));
                Publish("sid", ToMpvTrackId(currentSpu));
            });
        }

        private static void AddTracks(List<TrackInfo> tracks, string type, TrackType trackType,
            LibVLCSharp.Shared.Structures.TrackDescription[] descriptions, MediaTrack[] mediaTracks, int selectedId)
        {
            if (descriptions == null)
            {
                return;
            }
            foreach (var description in descriptions)
            {
                if (description.Id < 0)
                {
                    continue; // "Disable"
                }
                var track = new TrackInfo
                {
                    Id = description.Id + 1,
                    Type = type,
                    Selected = description.Id == selectedId,
                };
                var found = false;
                foreach (var mediaTrack in mediaTracks)
                {
                    if (mediaTrack.Id == description.Id && mediaTrack.TrackType == trackType)
                    {
                        track.Lang = mediaTrack.Language;
                        track.Title = mediaTrack.Description;
                        track.Codec = FourCcToString(mediaTrack.Codec);
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    // VLC names tracks like "Track 2 - [English]".
                    var name = description.Name ?? string.Empty;
                    var open = name.LastIndexOf('[');
                    var close = name.LastIndexOf(']');
                    if (open >= 0 && close > open + 1)
                    {
                        track.Lang = name.Substring(open + 1, close - open - 1);
                    }
                    track.Title = name;
                }
                tracks.Add(track);
            }
        }

        private void OnLengthChanged(long lengthMs)
        {
            // ShellVideo treats a duration that changes as a live stream and hides the seek bar.
            // VLC sometimes refines the length after its first estimate, so only report the first one.
            if (_durationSent)
            {
                return;
            }
            _durationSent = lengthMs > 0;
            Publish("duration", JsonValue.CreateNumberValue(Math.Max(0, lengthMs) / 1000.0));
        }

        private void OnTimeChanged(long timeMs)
        {
            var now = DateTime.UtcNow;
            if (now - _lastTimeReport < TimeReportInterval)
            {
                return;
            }
            _lastTimeReport = now;
            Publish("time-pos", JsonValue.CreateNumberValue(Math.Max(0, timeMs) / 1000.0));
        }

        private void MarkVideoReady(long id)
        {
            if (id != _loadId || _readySent)
            {
                return;
            }
            _readySent = true;
            RaiseVideoReady(id, true);
            VideoVisibleChanged?.Invoke(true);
        }

        private async void ScheduleReadyFallback(long id)
        {
            await Task.Delay(ReadyFallbackDelay);
            MarkVideoReady(id);
        }

        private void Publish(string name, IJsonValue value)
        {
            IJsonValue previous;
            if (_props.TryGetValue(name, out previous) && previous.Stringify() == value.Stringify())
            {
                return;
            }
            _props[name] = value;
            if (_observed.Contains(name))
            {
                RaisePropChange(name, value);
            }
        }

        private void RaisePropChange(string name, IJsonValue value)
        {
            var payload = new JsonObject();
            payload.SetNamedValue("name", JsonValue.CreateStringValue(name));
            payload.SetNamedValue("data", value);
            SignalRaised?.Invoke("mpv-prop-change", payload);
        }

        private void RaiseVideoReady(long id, bool ready)
        {
            var payload = new JsonObject();
            payload.SetNamedValue("loadId", JsonValue.CreateNumberValue(id));
            payload.SetNamedValue("ready", JsonValue.CreateBooleanValue(ready));
            SignalRaised?.Invoke("mpv-event-video-ready", payload);
        }

        private void RaiseEnded(string reason, string errorMessage)
        {
            var payload = new JsonObject();
            payload.SetNamedValue("reason", JsonValue.CreateStringValue(reason));
            if (errorMessage != null)
            {
                var error = new JsonObject();
                error.SetNamedValue("message", JsonValue.CreateStringValue(errorMessage));
                error.SetNamedValue("critical", JsonValue.CreateBooleanValue(true));
                payload.SetNamedValue("error", error);
            }
            SignalRaised?.Invoke("mpv-event-ended", payload);
        }

        // Runs work on the worker, if the stream VLC raised the event for is still the loaded one.
        private void QueueFromVlc(Action<long> work)
        {
            var id = Interlocked.Read(ref _workerLoadId);
            Enqueue(() =>
            {
                if (id == Interlocked.Read(ref _workerLoadId))
                {
                    work(id);
                }
            });
        }

        // Runs work on the UI thread, if the stream VLC raised the event for is still the loaded one.
        private void PostFromVlc(Action<long> work)
        {
            var id = Interlocked.Read(ref _workerLoadId);
            RunOnUi(() =>
            {
                if (id == _loadId)
                {
                    work(id);
                }
            });
        }

        private void Enqueue(Action action)
        {
            lock (_queueLock)
            {
                _queue = _queue.ContinueWith(_ =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("VLC worker", ex);
                    }
                }, TaskScheduler.Default);
            }
        }

        private void RunOnUi(Action action)
        {
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error("VLC UI callback", ex);
                }
            });
        }

        private static bool IsAllowedUrl(string url)
        {
            var colon = url.IndexOf(':');
            return colon > 0 && Array.IndexOf(AllowedSchemes, url.Substring(0, colon).ToLowerInvariant()) >= 0;
        }

        private static double ParseStartSeconds(List<string> loadfileArgs)
        {
            foreach (var arg in loadfileArgs)
            {
                double seconds;
                if (arg.StartsWith("start=+", StringComparison.Ordinal) &&
                    double.TryParse(arg.Substring("start=+".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) &&
                    seconds > 0 && !double.IsInfinity(seconds))
                {
                    return seconds;
                }
            }
            return 0;
        }

        // null means "let VLC pick", -1 means "off", otherwise a VLC ES id.
        private static int? ParseTrackId(IJsonValue value)
        {
            if (value.ValueType == JsonValueType.Boolean && !value.GetBoolean())
            {
                return -1;
            }
            if (value.ValueType == JsonValueType.String)
            {
                var text = value.GetString();
                if (text == "no")
                {
                    return -1;
                }
                if (text == "auto")
                {
                    return null;
                }
            }
            var number = AsNumber(value);
            return number.HasValue && number.Value >= 1 ? (int)number.Value - 1 : (int?)null;
        }

        private static IJsonValue ToMpvTrackId(int vlcId)
        {
            return vlcId >= 0 ? JsonValue.CreateNumberValue(vlcId + 1) : JsonValue.CreateBooleanValue(false);
        }

        private static bool? AsBool(IJsonValue value)
        {
            switch (value.ValueType)
            {
                case JsonValueType.Boolean:
                    return value.GetBoolean();
                case JsonValueType.Number:
                    return value.GetNumber() != 0;
                case JsonValueType.String:
                    var text = value.GetString();
                    if (text == "yes" || text == "true")
                    {
                        return true;
                    }
                    if (text == "no" || text == "false")
                    {
                        return false;
                    }
                    return null;
                default:
                    return null;
            }
        }

        private static double? AsNumber(IJsonValue value)
        {
            double number;
            if (value.ValueType == JsonValueType.Number)
            {
                number = value.GetNumber();
            }
            else if (value.ValueType != JsonValueType.String ||
                !double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return null;
            }
            return double.IsNaN(number) || double.IsInfinity(number) ? (double?)null : number;
        }

        private static string FourCcToString(uint fourcc)
        {
            if (fourcc == 0)
            {
                return null;
            }
            var chars = new char[4];
            for (var i = 0; i < 4; i++)
            {
                chars[i] = (char)((fourcc >> (8 * i)) & 0xFF);
            }
            var text = new string(chars).Trim(' ', '\0');
            return text.Length == 0 ? null : text.ToLowerInvariant();
        }

        private static JsonArray TracksToJson(List<TrackInfo> tracks)
        {
            var array = new JsonArray();
            foreach (var track in tracks)
            {
                var item = new JsonObject();
                item.SetNamedValue("id", JsonValue.CreateNumberValue(track.Id));
                item.SetNamedValue("type", JsonValue.CreateStringValue(track.Type));
                item.SetNamedValue("selected", JsonValue.CreateBooleanValue(track.Selected));
                item.SetNamedValue("external", JsonValue.CreateBooleanValue(false));
                if (!string.IsNullOrEmpty(track.Lang))
                {
                    item.SetNamedValue("lang", JsonValue.CreateStringValue(track.Lang));
                }
                if (!string.IsNullOrEmpty(track.Title))
                {
                    item.SetNamedValue("title", JsonValue.CreateStringValue(track.Title));
                }
                if (!string.IsNullOrEmpty(track.Codec))
                {
                    item.SetNamedValue("codec", JsonValue.CreateStringValue(track.Codec));
                }
                array.Add(item);
            }
            return array;
        }

        private struct TrackInfo
        {
            public int Id;
            public string Type;
            public string Lang;
            public string Title;
            public string Codec;
            public bool Selected;
        }
    }
}
