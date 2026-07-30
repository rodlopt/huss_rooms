namespace Hussrooms;

[Icon( "speaker" ), Group( "Hussrooms" ), Title( "Music Box" )]
public sealed class MusicBox : Component, Component.IPressable
{
	public const string DevelopmentCobaltApiUrl = "http://localhost:8080/";
	public const string ProductionCobaltApiUrl = "https://cobalt.googers.xyz/";

	[Property, Range( 0.0f, 1.0f ), Group( "Playback" )]
	public float InitialVolume { get; set; } = 0.65f;

	[Property, Group( "Playback" )] public bool RepeatByDefault { get; set; }

	[Property, Group( "Online Sources" )] public string CobaltApiUrl { get; set; } = ProductionCobaltApiUrl;

	[Property, Range( 100.0f, 10000.0f ), Group( "Spatial Audio" )]
	public float HearingDistance { get; set; } = 1800.0f;

	[Property, Range( 100.0f, 500.0f ), Group( "Interaction" )]
	public float ControlDistance { get; set; } = 180.0f;

	MusicPlayer _player;
	float _volume;
	bool _repeat;
	bool _finished;
	bool _playbackStarted;
	bool _loadTimedOut;
	TimeSince _timeSinceLoad;

	public string CurrentUrl { get; private set; } = "";
	public string LastError { get; private set; } = "";
	public bool HasTrack => _player is not null;
	public bool HasStarted => _playbackStarted;
	public bool IsPlaying => HasTrack && HasStarted && !IsPaused && !IsFinished;
	public bool IsPaused => _player?.Paused ?? false;
	public bool IsFinished => _finished;
	public bool Repeat => _repeat;
	public float Volume => _volume;
	public float Duration => _player?.Duration ?? 0.0f;
	public float PlaybackTime => _player?.PlaybackTime ?? 0.0f;
	public float Amplitude => _player?.Amplitude ?? 0.0f;
	public bool CanSeek => Duration > 0.0f && float.IsFinite( Duration );

	public string TrackTitle
	{
		get
		{
			if ( _player is null ) return "";

			var title = _player.Title;
			return string.IsNullOrWhiteSpace( title ) ? "Online audio" : title;
		}
	}

	public string Status
	{
		get
		{
			if ( !string.IsNullOrWhiteSpace( LastError ) ) return "Playback error";
			if ( _player is null ) return "Ready";
			if ( _finished ) return "Finished";
			if ( _player.Paused ) return "Paused";
			return _playbackStarted ? "Playing" : "Buffering";
		}
	}

	protected override void OnAwake()
	{
		_volume = InitialVolume.Clamp( 0.0f, 1.0f );
		_repeat = RepeatByDefault;
	}

	protected override void OnUpdate()
	{
		if ( _player is null ) return;

		_player.Position = WorldPosition;
		_player.Distance = HearingDistance;

		if ( !_playbackStarted
		     && (_player.PlaybackTime > 0.02f || _player.Amplitude > 0.0001f) )
		{
			_playbackStarted = true;
			if ( _loadTimedOut )
				LastError = "";
			_loadTimedOut = false;
		}

		if ( !_playbackStarted
		     && !_player.Paused
		     && !_loadTimedOut
		     && _timeSinceLoad > 20.0f )
		{
			_loadTimedOut = true;
			LastError = "The stream opened, but no audio playback started within 20 seconds.";
			Log.Warning( $"MusicBox playback did not start for '{CurrentUrl}'." );
		}
	}

	bool IPressable.CanPress( IPressable.Event e )
	{
		return e.Source is PlayerController controller
		       && !controller.IsProxy
		       && HussPlayer.Local?.Controller == controller;
	}

	bool IPressable.Press( IPressable.Event e )
	{
		if ( !((IPressable)this).CanPress( e ) ) return false;

		return UI.MusicPlayerPanel.Open( this );
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e )
	{
		return new IPressable.Tooltip(
			"Music Player",
			"speaker",
			"Press E to choose a stream and control playback" );
	}

	public static bool IsSupportedUrl( string url )
	{
		if ( string.IsNullOrWhiteSpace( url ) || url.Length > 2048 ) return false;
		if ( !Uri.TryCreate( url, UriKind.Absolute, out var parsed ) ) return false;

		return parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps;
	}

	public static bool IsYouTubeUrl( string url )
	{
		if ( !IsSupportedUrl( url ) || !Uri.TryCreate( url, UriKind.Absolute, out var parsed ) )
			return false;

		var host = parsed.IdnHost.TrimEnd( '.' );

		return host.Equals( "youtu.be", StringComparison.OrdinalIgnoreCase )
		       || host.Equals( "youtube.com", StringComparison.OrdinalIgnoreCase )
		       || host.EndsWith( ".youtube.com", StringComparison.OrdinalIgnoreCase )
		       || host.Equals( "youtube-nocookie.com", StringComparison.OrdinalIgnoreCase )
		       || host.EndsWith( ".youtube-nocookie.com", StringComparison.OrdinalIgnoreCase );
	}

	public async Task<string> ResolvePlaybackUrlAsync( string sourceUrl )
	{
		sourceUrl = sourceUrl?.Trim();

		if ( !IsSupportedUrl( sourceUrl ) )
			throw new ArgumentException( "Enter a valid HTTP or HTTPS URL." );

		if ( !IsYouTubeUrl( sourceUrl ) )
			return sourceUrl;

		var endpoint = CobaltApiUrl?.Trim();

		if ( !IsSupportedUrl( endpoint ) )
			throw new InvalidOperationException( "This music player has no valid Cobalt API URL." );

		endpoint = $"{endpoint.TrimEnd( '/' )}/";

		var request = new CobaltRequest
		{
			url = sourceUrl,
			audioBitrate = "128",
			audioFormat = "best",
			downloadMode = "audio",
			alwaysProxy = true,
			localProcessing = "disabled",
			youtubeBetterAudio = true
		};

		using var content = Http.CreateJsonContent( request );
		var headers = new Dictionary<string, string> { ["Accept"] = "application/json" };
		var response = await Http.RequestJsonAsync<CobaltResponse>(
			endpoint,
			"POST",
			content,
			headers );

		if ( response is null )
			throw new InvalidOperationException( "Cobalt returned an empty response." );

		if ( string.Equals( response.status, "tunnel", StringComparison.OrdinalIgnoreCase ) )
		{
			if ( IsSupportedUrl( response.url ) )
				return AddCobaltTunnelExtension( response.url.Trim(), response.filename );

			throw new InvalidOperationException( "Cobalt returned an invalid playback URL." );
		}

		if ( string.Equals( response.status, "redirect", StringComparison.OrdinalIgnoreCase ) )
		{
			if ( IsSupportedUrl( response.url ) )
				return response.url.Trim();

			throw new InvalidOperationException( "Cobalt returned an invalid playback URL." );
		}

		if ( string.Equals( response.status, "error", StringComparison.OrdinalIgnoreCase ) )
		{
			var code = string.IsNullOrWhiteSpace( response.error?.code )
				? "unknown error"
				: response.error.code;

			throw new InvalidOperationException( $"Cobalt could not resolve this URL ({code})." );
		}

		if ( string.Equals( response.status, "picker", StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidOperationException( "Cobalt returned a playlist. Paste a single YouTube video URL." );

		throw new InvalidOperationException( $"Cobalt returned unsupported status '{response.status}'." );
	}

	static string AddCobaltTunnelExtension( string url, string filename )
	{
		if ( !Uri.TryCreate( url, UriKind.Absolute, out var parsed ) )
			return url;

		var extension = System.IO.Path.GetExtension( filename )?.ToLowerInvariant();

		if ( string.IsNullOrWhiteSpace( extension ) || extension.Length > 9 )
			extension = ".mp4";

		if ( extension == ".m4a" )
			extension = ".mp4";
		else if ( extension == ".opus" )
			extension = ".webm";

		var currentExtension = System.IO.Path.GetExtension( parsed.AbsolutePath );

		if ( string.Equals( currentExtension, extension, StringComparison.OrdinalIgnoreCase ) )
			return url;

		var path = string.IsNullOrWhiteSpace( currentExtension )
			? parsed.AbsolutePath
			: parsed.AbsolutePath[..^currentExtension.Length];

		var builder = new UriBuilder( parsed ) { Path = $"{path}{extension}" };

		return builder.Uri.AbsoluteUri;
	}

	[Rpc.Host]
	public void RequestPlayUrl( string playbackUrl, string sourceUrl )
	{
		playbackUrl = playbackUrl?.Trim();
		sourceUrl = sourceUrl?.Trim();

		if ( !CallerCanControl() || !IsSupportedUrl( playbackUrl ) ) return;
		if ( !IsSupportedUrl( sourceUrl ) ) sourceUrl = playbackUrl;

		BroadcastPlayUrl( playbackUrl, sourceUrl );
	}

	[Rpc.Host]
	public void RequestTogglePaused()
	{
		if ( !CallerCanControl() || _player is null ) return;

		if ( _finished )
		{
			BroadcastSeek( 0.0f );
			BroadcastSetPaused( false );
			return;
		}

		BroadcastSetPaused( !_player.Paused );
	}

	[Rpc.Host]
	public void RequestSeek( float time )
	{
		if ( !CallerCanControl() || !CanSeek ) return;

		BroadcastSeek( time.Clamp( 0.0f, Duration ) );
	}

	[Rpc.Host]
	public void RequestSetVolume( float volume )
	{
		if ( !CallerCanControl() ) return;

		BroadcastSetVolume( volume.Clamp( 0.0f, 1.0f ) );
	}

	[Rpc.Host]
	public void RequestSetRepeat( bool repeat )
	{
		if ( !CallerCanControl() ) return;

		BroadcastSetRepeat( repeat );
	}

	[Rpc.Host]
	public void RequestStop()
	{
		if ( !CallerCanControl() ) return;

		BroadcastStop();
	}

	bool CallerCanControl()
	{
		if ( !Networking.IsActive ) return true;
		if ( Rpc.Caller is not { } caller ) return false;

		var player = Scene.GetAllComponents<HussPlayer>()
			.FirstOrDefault( x => !x.IsBot && x.Network.Owner == caller );

		return player.IsValid()
		       && player.WorldPosition.Distance( WorldPosition ) <= ControlDistance;
	}

	[Rpc.Broadcast]
	void BroadcastPlayUrl( string playbackUrl, string sourceUrl )
	{
		if ( !IsSupportedUrl( playbackUrl ) ) return;

		DisposePlayer();

		try
		{
			_player = MusicPlayer.PlayUrl( playbackUrl );
			_player.Position = WorldPosition;
			_player.Distance = HearingDistance;
			_player.Volume = _volume;
			_player.Repeat = _repeat;
			_player.OnFinished = OnTrackFinished;

			CurrentUrl = IsSupportedUrl( sourceUrl ) ? sourceUrl : playbackUrl;
			LastError = "";
			_finished = false;
			_playbackStarted = false;
			_loadTimedOut = false;
			_timeSinceLoad = 0;
		}
		catch ( Exception exception )
		{
			LastError = exception.Message;
			Log.Warning( $"MusicBox could not play '{playbackUrl}': {exception.Message}" );
			DisposePlayer();
		}
	}

	[Rpc.Broadcast]
	void BroadcastSetPaused( bool paused )
	{
		if ( _player is null ) return;

		_player.Paused = paused;
		_finished = false;
		if ( !paused && !_playbackStarted )
			LastError = "";
		_loadTimedOut = false;
		_timeSinceLoad = 0;
	}

	[Rpc.Broadcast]
	void BroadcastSeek( float time )
	{
		if ( !CanSeek ) return;

		_player.Seek( time.Clamp( 0.0f, Duration ) );
		_finished = false;
		LastError = "";
		_loadTimedOut = false;
		_timeSinceLoad = 0;
	}

	[Rpc.Broadcast]
	void BroadcastSetVolume( float volume )
	{
		_volume = volume.Clamp( 0.0f, 1.0f );

		if ( _player is not null )
			_player.Volume = _volume;
	}

	[Rpc.Broadcast]
	void BroadcastSetRepeat( bool repeat )
	{
		_repeat = repeat;

		if ( _player is not null )
			_player.Repeat = repeat;
	}

	[Rpc.Broadcast]
	void BroadcastStop()
	{
		DisposePlayer();
		CurrentUrl = "";
		LastError = "";
		_finished = false;
		_playbackStarted = false;
		_loadTimedOut = false;
	}

	void OnTrackFinished()
	{
		_finished = true;
	}

	void DisposePlayer()
	{
		if ( _player is null ) return;

		_player.OnFinished = null;
		_player.Dispose();
		_player = null;
		_playbackStarted = false;
		_loadTimedOut = false;
	}

	protected override void OnDestroy()
	{
		DisposePlayer();
	}

	sealed class CobaltRequest
	{
		public string url { get; set; }
		public string audioBitrate { get; set; }
		public string audioFormat { get; set; }
		public string downloadMode { get; set; }
		public bool alwaysProxy { get; set; }
		public string localProcessing { get; set; }
		public bool youtubeBetterAudio { get; set; }
	}

	sealed class CobaltResponse
	{
		public string status { get; set; } = "";
		public string url { get; set; } = "";
		public string filename { get; set; } = "";
		public CobaltError error { get; set; }
	}

	sealed class CobaltError
	{
		public string code { get; set; } = "";
	}
}
