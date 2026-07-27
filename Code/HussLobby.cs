namespace Hussrooms;

/// <summary>
/// Host-side settings and permissions for the lobby. Right now that's bots; there's room for
/// more later.
///
/// The settings don't use [Sync]. Sync properties need a NetworkObject, which only exists on
/// GameObjects the host has network spawned - a plain scene object like this one doesn't have
/// one. So the host holds the truth and pushes it out over RPC, including a private push to
/// each new arrival so late joiners aren't left with defaults.
///
/// Per player permission is different: <see cref="HussPlayer.CanSpawnBots"/> lives on the
/// pawn, which is network spawned, so that one is a normal host-authoritative sync.
/// </summary>
[Icon( "settings" ), Group( "Hussrooms" ), Title( "Huss Game Manager" )]
public sealed class HussLobby : Component, Component.INetworkListener
{
	/// <summary>
	/// The lobby for the running scene, if there is one.
	/// </summary>
	public static HussLobby Current
	{
		get
		{
			if ( _current.IsValid() ) return _current;

			_current = Game.ActiveScene?.GetAllComponents<HussLobby>().FirstOrDefault();
			return _current;
		}
	}

	static HussLobby _current;

	[Property, Group( "Bots" )] public GameObject BotPrefab { get; set; }

	/// <summary>Whether bots are allowed at all. Host can turn this off to lock it down.</summary>
	[Property, Group( "Bots" ), Title( "Bots Enabled By Default" )]
	public bool DefaultBotsEnabled { get; set; } = true;

	/// <summary>How many bots can be alive at once, so nobody can bury the server.</summary>
	[Property, Group( "Bots" ), Title( "Default Bot Limit" )]
	public int DefaultBotLimit { get; set; } = 4;

	/// <summary>The most the limit can ever be raised to.</summary>
	[Property, Group( "Bots" )]
	public int MaxBotLimit { get; set; } = 16;

	/// <summary>
	/// Bots put out automatically once the game is up, so a fresh lobby already has something
	/// hunting you instead of an empty map.
	/// </summary>
	[Property, Group( "Bots" )]
	public int StartingBots { get; set; } = 1;

	/// <summary>
	/// How far from the person asking a bot may be placed. Anything further falls back to a
	/// spawn point, so a request can't drop a chaser on the far side of the map.
	/// </summary>
	[Property, Group( "Bots" )]
	public float MaxSpawnDistance { get; set; } = 400.0f;

	/// <summary>Live on every machine. Host authoritative - change it through <see cref="RequestSettings"/>.</summary>
	public bool BotsEnabled { get; private set; } = true;

	/// <summary>Live on every machine. Host authoritative.</summary>
	public int BotLimit { get; private set; } = 4;

	public int BotCount => Scene.GetAllComponents<ChaserBot>().Count();

	public bool AtBotLimit => BotCount >= BotLimit;

	bool _startingBotsDone;

	protected override void OnStart()
	{
		_current = this;

		BotsEnabled = DefaultBotsEnabled;
		BotLimit = DefaultBotLimit.Clamp( 0, MaxBotLimit );
	}

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsHost || _startingBotsDone ) return;

		// Wait for somebody to actually be in the map. Spawning earlier risks doing it before
		// networking is up, and gives the bot nothing to chase anyway.
		if ( !Scene.GetAllComponents<HussPlayer>().Any( x => !x.IsBot ) ) return;

		_startingBotsDone = true;

		if ( !BotsEnabled ) return;

		for ( var i = 0; i < StartingBots && !AtBotLimit; i++ )
		{
			// Nobody's in particular, so no player can delete it - CLEAR ALL on the scoreboard
			// is how these go away.
			SpawnBotAt( FindSpawnPoint(), Guid.Empty );
		}
	}

	// -------------------------------------------------------------- permissions

	/// <summary>
	/// True if the local player is allowed to put bots in the game. Used to decide whether the
	/// spawn button is even offered - the host re-checks it properly before acting.
	/// </summary>
	public static bool LocalCanSpawnBots
	{
		get
		{
			if ( Current is not HussLobby lobby || !lobby.BotsEnabled ) return false;
			if ( Networking.IsHost ) return true;

			return HussPlayer.Local is HussPlayer player && player.CanSpawnBots;
		}
	}

	static bool IsHostConnection( Connection connection )
	{
		if ( connection is null ) return false;

		return connection == Connection.Host || connection.IsHost;
	}

	/// <summary>
	/// Finds the pawn belonging to a connection, so we can check what it's allowed to do.
	/// </summary>
	HussPlayer PlayerFor( Connection connection )
	{
		if ( connection is null ) return null;

		return Scene.GetAllComponents<HussPlayer>()
			.FirstOrDefault( x => !x.IsBot && x.Network.Owner == connection );
	}

	/// <summary>
	/// Who is trusted with bots at all - the host, plus anyone the host has promoted.
	/// </summary>
	bool CallerMayManageBots( Connection caller )
	{
		if ( IsHostConnection( caller ) ) return true;

		return PlayerFor( caller ) is HussPlayer player && player.CanSpawnBots;
	}

	/// <summary>
	/// Same people, but putting new ones out also needs bots to be switched on. Removing them
	/// deliberately doesn't - clearing up shouldn't stop working when they're disabled.
	/// </summary>
	bool CallerMaySpawnBots( Connection caller )
	{
		return BotsEnabled && CallerMayManageBots( caller );
	}

	// ---------------------------------------------------------------- settings

	/// <summary>
	/// Host only. Anyone else asking is ignored rather than trusted.
	/// </summary>
	[Rpc.Host]
	public void RequestSettings( bool botsEnabled, int botLimit )
	{
		if ( !IsHostConnection( Rpc.Caller ) ) return;

		BroadcastSettings( botsEnabled, botLimit.Clamp( 0, MaxBotLimit ) );

		// Turning bots off clears the ones already out there, otherwise "off" doesn't mean
		// much to the people currently being chased by one.
		if ( !botsEnabled )
			RemoveAllBots();
		else
			TrimBotsToLimit();
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	void BroadcastSettings( bool botsEnabled, int botLimit )
	{
		BotsEnabled = botsEnabled;
		BotLimit = botLimit;
	}

	/// <summary>
	/// Someone finished joining - send them the current settings privately, so they don't sit
	/// on the defaults until the host next changes something.
	/// </summary>
	void Component.INetworkListener.OnActive( Connection channel )
	{
		if ( !Networking.IsHost ) return;

		using ( Rpc.FilterInclude( channel ) )
		{
			BroadcastSettings( BotsEnabled, BotLimit );
		}
	}

	// ------------------------------------------------------------- promotions

	/// <summary>
	/// Host only. Lets a trusted player spawn bots without handing them the host's chair.
	/// </summary>
	[Rpc.Host]
	public void RequestSetCanSpawnBots( HussPlayer player, bool allowed )
	{
		if ( !IsHostConnection( Rpc.Caller ) ) return;
		if ( !player.IsValid() || player.IsBot ) return;

		player.CanSpawnBots = allowed;
	}

	// ------------------------------------------------------------ bot spawning

	/// <summary>
	/// Ask the host for a bot at a spot you're looking at - this is what the prop menu calls.
	/// The host is the one that checks whether you're allowed, and where it may go.
	/// </summary>
	[Rpc.Host]
	public void RequestSpawnBotAt( Vector3 position )
	{
		if ( !CallerMaySpawnBots( Rpc.Caller ) ) return;
		if ( AtBotLimit ) return;

		// Only honour the requested spot if the caller is standing next to it. Otherwise it's
		// a spawn point, so nobody can drop a chaser into somebody else's lap from range.
		var where = FindSpawnPoint();

		if ( PlayerFor( Rpc.Caller ) is HussPlayer caller &&
		     caller.WorldPosition.Distance( position ) <= MaxSpawnDistance )
		{
			where = new Transform( position, Rotation.Identity );
		}

		SpawnBotAt( where, Rpc.Caller?.Id ?? Guid.Empty );
	}

	/// <summary>
	/// Ask the host for a bot at a spawn point.
	/// </summary>
	[Rpc.Host]
	public void RequestSpawnBot()
	{
		if ( !CallerMaySpawnBots( Rpc.Caller ) ) return;
		if ( AtBotLimit ) return;

		SpawnBotAt( FindSpawnPoint(), Rpc.Caller?.Id ?? Guid.Empty );
	}

	void SpawnBotAt( Transform where, Guid spawnedBy )
	{
		if ( !BotPrefab.IsValid() ) return;

		var bot = BotPrefab.Clone( where.WithScale( 1 ), name: $"Bot {BotCount + 1}" );

		// Owned by the host, because the host is the one running its brain.
		bot.NetworkSpawn( Connection.Local );

		// After the spawn, not before - the sync only has somewhere to live once the object
		// is on the network.
		if ( bot.Components.Get<ChaserBot>( true ) is ChaserBot brain )
			brain.SpawnedBy = spawnedBy;
	}

	/// <summary>
	/// Remove the most recently spawned bot.
	/// </summary>
	[Rpc.Host]
	public void RequestRemoveBot()
	{
		if ( !CallerMayManageBots( Rpc.Caller ) ) return;

		var bot = Scene.GetAllComponents<ChaserBot>().LastOrDefault();
		bot?.GameObject.Destroy();
	}

	/// <summary>
	/// Take back the last bot you put out yourself.
	/// </summary>
	/// <remarks>
	/// No permission check on purpose. It only ever touches bots stamped with the caller's own
	/// connection, so the worst anyone can do is tidy up after themselves - and they should
	/// still be able to do that if the host has since revoked their ability to make new ones.
	/// </remarks>
	[Rpc.Host]
	public void RequestRemoveOwnBot()
	{
		if ( Rpc.Caller is not Connection caller ) return;

		var bot = Scene.GetAllComponents<ChaserBot>()
			.LastOrDefault( x => x.SpawnedBy == caller.Id );

		bot?.GameObject.Destroy();
	}

	/// <summary>
	/// True if the local player has a bot of their own out to remove.
	/// </summary>
	public static bool LocalHasOwnBot
	{
		get
		{
			if ( Current is not HussLobby lobby ) return false;
			if ( Connection.Local is not Connection local ) return false;

			return lobby.Scene.GetAllComponents<ChaserBot>().Any( x => x.SpawnedBy == local.Id );
		}
	}

	/// <summary>
	/// Clear the map of bots in one go.
	/// </summary>
	[Rpc.Host]
	public void RequestRemoveAllBots()
	{
		if ( !CallerMayManageBots( Rpc.Caller ) ) return;

		RemoveAllBots();
	}

	void RemoveAllBots()
	{
		foreach ( var bot in Scene.GetAllComponents<ChaserBot>().ToArray() )
		{
			bot.GameObject.Destroy();
		}
	}

	void TrimBotsToLimit()
	{
		var bots = Scene.GetAllComponents<ChaserBot>().ToArray();

		for ( var i = BotLimit; i < bots.Length; i++ )
		{
			bots[i].GameObject.Destroy();
		}
	}

	Transform FindSpawnPoint()
	{
		var points = Scene.GetAllComponents<SpawnPoint>().ToArray();

		if ( points.Length > 0 )
			return Random.Shared.FromArray( points ).WorldTransform.WithScale( 1 );

		return WorldTransform.WithScale( 1 );
	}
}
