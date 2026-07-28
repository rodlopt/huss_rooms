namespace Hussrooms;

/// <summary>
/// Everything that makes a player a runner or a chaser: which side they're on, their
/// stamina, how many hits they've taken, and the key that flips them between the two.
///
/// The team is host authoritative so nobody can put themselves on a side the host
/// didn't agree to. Stamina is owned by the player it belongs to, since it's driven
/// entirely by their own input.
/// </summary>
[Icon( "directions_run" ), Group( "Hussrooms" ), Title( "Huss Player" )]
public partial class HussPlayer : Component, Component.INetworkSpawn
{
	[Property] public GameObject Head { get; set; }

	[RequireComponent] public PlayerController Controller { get; set; }

	MoveModeIcy _move;
	ChaserAttack _attack;

	/// <summary>
	/// Our view. Only meaningful on the machine that owns this player.
	/// </summary>
	public HussCamera Camera { get; private set; }

	/// <summary>
	/// The player this machine is controlling, if there is one.
	/// </summary>
	public static HussPlayer Local
	{
		get
		{
			if ( _local.IsValid() && !_local.IsProxy ) return _local;

			_local = Game.ActiveScene?.GetAllComponents<HussPlayer>()
				.FirstOrDefault( x => !x.IsProxy );

			return _local;
		}
	}

	static HussPlayer _local;

	// ----------------------------------------------------------------- identity

	/// <summary>
	/// Who this player is, for <see cref="Nametag"/> and anything else that needs to name them.
	///
	/// Filled in by the host from the owning connection rather than by the owner, so every
	/// machine gets the same answer and nobody can name themselves whatever they like.
	/// </summary>
	[Sync( SyncFlags.FromHost )]
	public string DisplayName { get; set; }

	/// <summary>
	/// Runs on every machine as the pawn spawns, and hands us the connection it belongs to.
	/// </summary>
	void Component.INetworkSpawn.OnNetworkSpawn( Connection owner )
	{
		if ( Networking.IsHost )
			DisplayName = owner?.DisplayName;
	}

	/// <summary>
	/// True when this pawn is driven by <see cref="ChaserBot"/> rather than a person.
	///
	/// Bots are owned by the host, which means they are not proxies on the host's machine -
	/// so without this they'd happily read the host's own keyboard and mouse.
	/// </summary>
	[Sync( SyncFlags.FromHost )]
	public bool IsBot { get; set; }

	/// <summary>
	/// Whether this player is allowed to spawn bots. Only the host hands this out - see
	/// <see cref="HussLobby"/>.
	/// </summary>
	[Sync( SyncFlags.FromHost )]
	public bool CanSpawnBots { get; set; }

	/// <summary>
	/// Catches the cases OnNetworkSpawn doesn't: a pawn that was never network spawned
	/// (running the scene with no lobby), or one whose owner arrived late.
	/// </summary>
	void UpdateDisplayName()
	{
		if ( !string.IsNullOrWhiteSpace( DisplayName ) ) return;

		var owner = Network.Owner ?? Connection.Local;
		if ( owner is null ) return;

		DisplayName = owner.DisplayName;
	}

	// ------------------------------------------------------------------ team

	/// <summary>
	/// Which side we're on. Set through <see cref="RequestTeam"/> so the host stays in charge.
	/// </summary>
	[Sync( SyncFlags.FromHost ), Change( nameof(OnTeamChanged) )]
	public HussTeam Team { get; set; } = HussTeam.Runner;

	[Property, Group( "Team" )] public float TeamSwitchCooldown { get; set; } = 1.0f;

	TimeSince _timeSinceTeamSwitch;

	public bool IsChaser => Team == HussTeam.Chaser;
	public bool IsRunner => Team == HussTeam.Runner;

	[Property, Group( "Runner" )] public float RunnerBaseSpeed { get; set; } = 230.0f;
	[Property, Group( "Runner" )] public float RunnerMaxSpeed { get; set; } = 340.0f;

	[Property, Group( "Chaser" )] public float ChaserBaseSpeed { get; set; } = 260.0f;
	[Property, Group( "Chaser" )] public float ChaserMaxSpeed { get; set; } = 410.0f;

	[Property, Group( "Sound" )] public SoundEvent[] Taunts { get; set; } = Array.Empty<SoundEvent>();

	/// <summary>
	/// Played when this player is put down for good. Leave empty for silence.
	/// </summary>
	[Property, Group( "Sound" )] public SoundEvent DeathSound { get; set; }

	/// <summary>
	/// True while the body should be pinned to the camera instead of facing its own movement.
	/// Written by <see cref="HussCamera"/> on the owner; synced so remote players turn the
	/// same way we see ourselves turn.
	/// </summary>
	[Sync]
	public bool FaceCamera { get; set; }

	[Property, Group( "Looks" )] public Model RunnerModel { get; set; }
	[Property, Group( "Looks" )] public AnimationGraph RunnerAnimGraph { get; set; }
	[Property, Group( "Looks" )] public Model ChaserModel { get; set; }
	[Property, Group( "Looks" )] public AnimationGraph ChaserAnimGraph { get; set; }

	// --------------------------------------------------------------- stamina

	[Property, Group( "Stamina" )] public float MaxStamina { get; set; } = 100.0f;

	/// <summary>Stamina spent per second while sprinting.</summary>
	[Property, Group( "Stamina" )]
	public float StaminaDrain { get; set; } = 26.0f;

	/// <summary>Stamina recovered per second while not sprinting.</summary>
	[Property, Group( "Stamina" )]
	public float StaminaRegen { get; set; } = 16.0f;

	/// <summary>How long after sprinting before stamina starts coming back.</summary>
	[Property, Group( "Stamina" )]
	public float StaminaRegenDelay { get; set; } = 0.9f;

	/// <summary>Speed multiplier while sprinting. Runners sprint faster than a chaser can move.</summary>
	[Property, Group( "Stamina" )]
	public float SprintScale { get; set; } = 1.45f;

	/// <summary>Once you bottom out, this much stamina has to come back before you can sprint again.</summary>
	[Property, Group( "Stamina" )]
	public float ExhaustedRecovery { get; set; } = 30.0f;

	[Sync] public float Stamina { get; set; } = 100.0f;
	[Sync] public bool IsSprinting { get; set; }

	/// <summary>True once stamina hits zero, until it climbs back over <see cref="ExhaustedRecovery"/>.</summary>
	public bool IsExhausted { get; private set; }

	public float StaminaFraction => MaxStamina <= 0 ? 0 : (Stamina / MaxStamina).Clamp( 0, 1 );

	TimeSince _timeSinceSprint;

	// ---------------------------------------------------------------- health

	[Property, Group( "Health" )] public int HitsToKill { get; set; } = 3;
	[Property, Group( "Health" )] public float RespawnDelay { get; set; } = 3.0f;

	/// <summary>How much random tumble to give the ragdoll when it drops.</summary>
	[Property, Group( "Health" )]
	public float RagdollSpin { get; set; } = 220.0f;

	GameObject _ragdoll;

	[Sync( SyncFlags.FromHost )] public int Hits { get; set; }

	[Sync( SyncFlags.FromHost ), Change( nameof(OnDownedChanged) )]
	public bool IsDowned { get; set; }

	/// <summary>Hits left before this runner goes down.</summary>
	public int HitsRemaining => Math.Max( 0, HitsToKill - Hits );

	TimeUntil _respawnAt;

	// ------------------------------------------------------------- safe room

	/// <summary>
	/// How many <see cref="SafeZone"/> volumes we're currently standing in. Counted on the host.
	/// </summary>
	int _safeZones;

	/// <summary>
	/// True while inside a safe room. Chasers can't land a hit on us here.
	/// </summary>
	[Sync( SyncFlags.FromHost )]
	public bool IsSafe { get; set; }

	// ------------------------------------------------------------------ life

	protected override void OnAwake()
	{
		// includeDisabled, because ChaserAttack ships disabled - we're the thing that turns
		// it on - and because nothing is Active yet this early.
		_move = GetComponent<MoveModeIcy>( true );
		_attack = GetComponent<ChaserAttack>( true );
		Camera = GetComponent<HussCamera>( true );

		// Reset safe zone counter early to prevent stale values from previous play sessions
		_safeZones = 0;
	}

	protected override void OnStart()
	{
		if ( !IsProxy )
			Stamina = MaxStamina;

		IsSafe = false;

		ApplyTeam();
	}

	/// <summary>
	/// Set by the main menu while it's up. Kept separate from <see cref="IsDowned"/> so the
	/// two can't clobber each other's idea of whether the controls should be live.
	/// </summary>
	public bool InputLocked { get; set; }

	protected override void OnUpdate()
	{
		// Bots aren't proxies on the host, so they'd read the host's input if we let them
		// through here. ChaserBot drives them instead.
		if ( IsProxy || IsBot ) return;

		// Recomputed every frame from both reasons we'd take the controls away, so it can't
		// get stuck on if they overlap.
		if ( Controller.IsValid() )
		{
			var controlsEnabled = !IsDowned && !InputLocked;
			Controller.UseInputControls = controlsEnabled;
			Controller.UseLookControls = controlsEnabled;
		}

		UpdatePropInteraction();

		if ( IsDowned || InputLocked ) return;

		// Input.Pressed is per frame, so it has to be read here rather than in FixedUpdate.
		if ( Input.Pressed( "Taunt" ) )
			RequestTaunt();

		if ( Input.Pressed( "Undo" ) )
			DeleteLastProp();
			
		if ( Input.Pressed( "Transform" ) )
			RequestTeam( IsChaser ? HussTeam.Runner : HussTeam.Chaser );
	}

	protected override void OnFixedUpdate()
	{
		if ( Networking.IsHost )
		{
			UpdateDisplayName();
			UpdateRespawn();
		}

		if ( IsProxy || IsBot ) return;

		UpdateStamina();
		UpdateSpeed();
	}

	// ----------------------------------------------------------- team switch

	/// <summary>
	/// Ask the host to put us on a team. Called by the owning client.
	/// </summary>
	[Rpc.Host]
	public void RequestTeam( HussTeam team )
	{
		if ( IsSafe && team == HussTeam.Chaser ) return;
		if ( Network.Owner != Rpc.Caller ) return;
		if ( IsDowned ) return;

		if ( _timeSinceTeamSwitch < TeamSwitchCooldown ) return;

		Team = team;

		_timeSinceTeamSwitch = 0;
	}

	[Property, Group( "Taunts" )] public float TauntCooldown { get; set; } = 5f;

	private TimeSince timeSinceTaunted = 0;

	[Rpc.Host]
	public void RequestTaunt()
	{
		if ( IsChaser )
			return;
		if ( Network.Owner != Rpc.Caller ) return;
		if ( Taunts is null || Taunts.Length == 0 ) return;
		if ( timeSinceTaunted < TauntCooldown ) return;

		BroadcastTaunt( Random.Shared.Next( Taunts.Length ) );
	}

	[Rpc.Broadcast]
	void BroadcastTaunt( int tauntIndex )
	{
		if ( Taunts is null || tauntIndex < 0 || tauntIndex >= Taunts.Length ) return;
		var taunt = Taunts[tauntIndex];
		if ( taunt is null ) return;

		timeSinceTaunted = 0f;

		GameObject.PlaySound( taunt );
	}

	void OnTeamChanged( HussTeam before, HussTeam after )
	{
		ApplyTeam();
	}

	/// <summary>
	/// Push the current team out to everything that cares: physics tags, movement speeds,
	/// the model, and whether the attack component is running.
	/// </summary>
	void ApplyTeam()
	{
		var chaser = IsChaser;

		// Physics tags live on the root - GameTags walks ancestors, so the collider shapes
		// underneath pick these up automatically and safe room barriers can filter on them.
		GameObject.Tags.Set( HussTags.Runner, !chaser );
		GameObject.Tags.Set( HussTags.Chaser, chaser );

		if ( _move.IsValid() )
		{
			_move.BaseSpeed = chaser ? ChaserBaseSpeed : RunnerBaseSpeed;
			_move.MaxSpeed = chaser ? ChaserMaxSpeed : RunnerMaxSpeed;
		}

		if ( Controller.IsValid() && Controller.Renderer.IsValid() )
		{
			ApplyLook( Controller.Renderer,
				chaser ? ChaserModel : RunnerModel,
				chaser ? ChaserAnimGraph : RunnerAnimGraph );
		}

		if ( _attack.IsValid() )
			_attack.Enabled = chaser;

		if ( chaser )  Controller.BodyHeight = 112f; else Controller.BodyHeight = 72f; 
		
	}

	/// <summary>
	/// Swap the model and its animation graph together.
	/// </summary>
	/// <remarks>
	/// Assigning Model pushes the new model straight onto the existing SceneModel, and the
	/// native side rebinds that to the new model's own graph - which for these models is none
	/// at all. The renderer's AnimationGraph field still holds the graph we want, so setting
	/// it again is an equality no-op and never reaches the SceneModel. The result is a
	/// character stuck in its bind pose: the T-pose.
	///
	/// Clearing it first breaks that equality check so the reassignment actually lands.
	/// </remarks>
	static void ApplyLook( SkinnedModelRenderer renderer, Model model, AnimationGraph graph )
	{
		var changingModel = model is not null && renderer.Model != model;

		if ( changingModel )
		{
			renderer.AnimationGraph = null;
			renderer.Model = model;
		}

		if ( graph is null ) return;

		if ( changingModel || renderer.AnimationGraph != graph )
		{
			renderer.AnimationGraph = null;
			renderer.AnimationGraph = graph;
		}
	}

	// --------------------------------------------------------------- stamina

	void UpdateStamina()
	{
		// Chasers don't sprint - they get a flat, relentless speed instead.
		if ( IsChaser )
		{
			IsSprinting = false;
			// Do not forcibly reset stamina or exhaustion when switching to chaser; preserve
			// current stamina so team switches don't heal or refill the player.
			return;
		}

		var wantsSprint = Input.Down( "Run" ) && !IsDowned;
		var moving = Controller.IsValid() && Controller.WishVelocity.Length > 1.0f;

		IsSprinting = wantsSprint && moving && !IsExhausted;

		if ( IsSprinting )
		{
			Stamina = Math.Max( 0, Stamina - StaminaDrain * Time.Delta );
			_timeSinceSprint = 0;

			if ( Stamina <= 0 )
				IsExhausted = true;
		}
		else if ( _timeSinceSprint > StaminaRegenDelay )
		{
			Stamina = Math.Min( MaxStamina, Stamina + StaminaRegen * Time.Delta );
		}

		// Have to get a decent chunk back before you're allowed to bolt again.
		if ( IsExhausted && Stamina >= ExhaustedRecovery )
			IsExhausted = false;
	}

	void UpdateSpeed()
	{
		if ( !_move.IsValid() ) return;

		_move.SpeedScale = IsSprinting ? SprintScale : 1.0f;
	}

	// ---------------------------------------------------------------- damage

	/// <summary>
	/// Register a hit from a chaser. Host only - see <see cref="ChaserAttack"/> for who calls it.
	/// </summary>
	public bool TakeHit( HussPlayer attacker )
	{
		if ( !Networking.IsHost ) return false;

		Hits++;

		if ( Hits >= HitsToKill )
			GoDown();

		return true;
	}

	void GoDown()
	{
		// Ragdoll first: it copies its pose off the live renderer, and setting IsDowned takes
		// that renderer away.
		_recoverFromKnockdown = false;
		SpawnRagdoll( Controller.IsValid() ? Controller.Velocity : Vector3.Zero );

		// Pass the spot we died rather than letting each machine read it later - by the time
		// the message lands the pawn may already have been parked or moved.
		BroadcastDeathSound( WorldPosition + Vector3.Up * 36.0f );

		_respawnAt = RespawnDelay;
		IsDowned = true;
	}

	/// <summary>
	/// Everyone nearby should hear it, not just the two people involved.
	/// </summary>
	/// <remarks>
	/// Anchored to a fixed world point on purpose. GameObject.PlaySound parents the sound to
	/// the object and follows it, so the scream would ride along with the pawn - which gets
	/// parked on death and then teleported to a spawn point a few seconds later. It has to
	/// stay where the body fell.
	/// </remarks>
	[Rpc.Broadcast]
	void BroadcastDeathSound( Vector3 position )
	{
		if ( DeathSound is null ) return;

		Sound.Play( DeathSound, position );
	}

	/// <summary>
	/// Ragdolls are cosmetic, so each machine builds its own local copy rather than networking
	/// a whole jointed physics body. They only need to look roughly the same, not match exactly.
	/// </summary>
	[Rpc.Broadcast]
	void SpawnRagdoll( Vector3 velocity )
	{
		ClearRagdoll();

		var renderer = Controller.IsValid() ? Controller.Renderer : null;
		if ( !renderer.IsValid() ) return;

		_ragdoll = Controller.CreateRagdoll( $"{GameObject.Name} Ragdoll" );

		// Hand over from the animated body to the physics one only once the copy exists, so
		// the ragdoll inherits the pose we died in rather than a bind pose.
		renderer.Enabled = false;

		if ( !_ragdoll.IsValid() ) return;

		foreach ( var body in _ragdoll.GetComponentsInChildren<Rigidbody>() )
		{
			body.Velocity = velocity;
			body.AngularVelocity = Vector3.Random * RagdollSpin;
		}
	}

	void ClearRagdoll()
	{
		_ragdoll?.Destroy();
		_ragdoll = null;
	}

	protected override void OnDestroy()
	{
		ClearRagdoll();
	}

	void UpdateRespawn()
	{
		if ( !IsDowned ) return;
		if ( !_respawnAt ) return;

		if ( _recoverFromKnockdown )
		{
			_recoverFromKnockdown = false;
			IsDowned = false;
			return;
		}

		Hits = 0;
		IsDowned = false;

		var spawn = FindSpawnPoint();
		Respawn( spawn.Position, spawn.Rotation.Angles() );
	}

	/// <summary>
	/// Put this player back at a spawn point right now, without killing them. Host only -
	/// used by <see cref="RespawnZone"/> to catch anyone who has fallen out of the map.
	/// </summary>
	public void RespawnAtSpawnPoint()
	{
		if ( !Networking.IsHost ) return;

		var spawn = FindSpawnPoint();
		Respawn( spawn.Position, spawn.Rotation.Angles() );
	}

	Transform FindSpawnPoint()
	{
		var spawnPoints = Scene.GetAllComponents<SpawnPoint>().ToArray();

		if ( spawnPoints.Length > 0 )
			return Random.Shared.FromArray( spawnPoints ).WorldTransform;

		return WorldTransform;
	}

	/// <summary>
	/// Put the body back. Has to run on the owner because they're the one simulating it.
	/// </summary>
	[Rpc.Owner]
	void Respawn( Vector3 position, Angles angles )
	{
		WorldPosition = position;
		Controller.EyeAngles = angles with { pitch = 0, roll = 0 };

		if ( Controller.Body.IsValid() )
			Controller.Body.Velocity = 0;

		Transform.ClearInterpolation();
	}

	void OnDownedChanged( bool before, bool after )
	{
		if ( !Controller.IsValid() ) return;

		// Control flags are driven from OnUpdate - see InputLocked. Disable look
		// immediately on the owner so the downed camera cannot receive one last input tick.
		if ( !IsProxy && !IsBot )
			Controller.UseLookControls = !after && !InputLocked;

		// WishVelocity is owner authoritative - proxies must not write to it.
		if ( after && !IsProxy )
			Controller.WishVelocity = 0;

		// Park the capsule while we're down so it isn't an invisible wall people run into,
		// and so it can't drift off while the ragdoll does its thing.
		if ( Controller.Body.IsValid() )
		{
			Controller.Body.MotionEnabled = !after;
			Controller.Body.Velocity = 0;
		}

		if ( Controller.ColliderObject.IsValid() )
			Controller.ColliderObject.Enabled = !after;

		// Getting up: the ragdoll goes away and the animated body comes back. Going down is
		// handled in SpawnRagdoll, which has to do it in a specific order.
		if ( !after )
		{
			ClearRagdoll();

			if ( Controller.Renderer.IsValid() )
				Controller.Renderer.Enabled = true;
		}
	}

	// ------------------------------------------------------------- safe room

	/// <summary>
	/// Called by <see cref="SafeZone"/> on the host as we cross a safe room boundary.
	/// </summary>
	internal void SetInSafeZone( bool inside )
	{
		if ( !Networking.IsHost ) return;

		_safeZones = Math.Max( 0, _safeZones + (inside ? 1 : -1) );
		var wasSafe = IsSafe;
		IsSafe = _safeZones > 0;
	}
}
