namespace Hussrooms;

/// <summary>
/// A Captain Clark that hunts on its own.
///
/// It's the same pawn a player drives - same prefab, same icy movement, same
/// <see cref="ChaserAttack"/> - with this steering it instead of a keyboard. That's
/// deliberate: the bot slips round corners exactly like you do, and anything tuned for
/// players applies to it for free.
///
/// Only the machine that owns the bot thinks. Bots are spawned by the host, so that's the
/// host, and everyone else just watches the networked result.
/// </summary>
[Icon( "smart_toy" ), Group( "Hussrooms" ), Title( "Chaser Bot" )]
public sealed class ChaserBot : Component
{
	[RequireComponent] public HussPlayer Player { get; set; }

	/// <summary>Shown on the nametag.</summary>
	[Property, Group( "Identity" )] public string BotName { get; set; } = "Captain Clark";

	/// <summary>How often we look for a better victim.</summary>
	[Property, Group( "Thinking" )] public float RetargetInterval { get; set; } = 0.5f;

	/// <summary>How often the route gets recalculated while chasing.</summary>
	[Property, Group( "Thinking" )] public float RepathInterval { get; set; } = 0.4f;

	/// <summary>Runners further away than this aren't worth chasing.</summary>
	[Property, Group( "Thinking" )] public float ChaseRange { get; set; } = 4000.0f;

	/// <summary>How fast it swings its aim round, in degrees per second.</summary>
	[Property, Group( "Thinking" )] public float LookSpeed { get; set; } = 400.0f;

	/// <summary>How close it has to get to a path corner before moving on to the next.</summary>
	[Property, Group( "Pathing" )] public float CornerRadius { get; set; } = 40.0f;

	/// <summary>How far ahead it checks for walls when there's no path to follow.</summary>
	[Property, Group( "Pathing" )] public float ProbeDistance { get; set; } = 90.0f;

	/// <summary>How far it roams from where it's standing when nobody's around.</summary>
	[Property, Group( "Pathing" )] public float WanderRadius { get; set; } = 900.0f;

	/// <summary>Who it's currently after. Null while wandering.</summary>
	public HussPlayer Target { get; private set; }

	MoveModeIcy _move;

	TimeUntil _retarget;
	TimeUntil _repath;
	TimeUntil _rewander;

	readonly List<Vector3> _path = new();
	int _corner;
	Vector3 _wanderTo;
	bool _hasWanderTarget;

	protected override void OnAwake()
	{
		_move = GetComponent<MoveModeIcy>( true );
	}

	protected override void OnStart()
	{
		// Take the human controls away on every machine. The bot prefab has no HussCamera,
		// but the controller would still be reading move and look input on the host.
		if ( Player.Controller.IsValid() )
		{
			Player.Controller.UseInputControls = false;
			Player.Controller.UseLookControls = false;
			Player.Controller.UseCameraControls = false;
		}

		if ( !Networking.IsHost ) return;

		Player.IsBot = true;
		Player.Team = HussTeam.Chaser;

		// Set before HussPlayer's own fallback runs, otherwise the bot would inherit the
		// host's name from the connection that owns it.
		Player.DisplayName = BotName;
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy ) return;
		if ( !Player.IsValid() || !Player.Controller.IsValid() ) return;

		var controller = Player.Controller;

		if ( Player.IsDowned )
		{
			controller.WishVelocity = 0;
			return;
		}

		if ( _retarget )
		{
			var previous = Target;
			Target = FindTarget();
			_retarget = RetargetInterval;

			// New victim, new route.
			if ( Target != previous ) _path.Clear();
		}

		var direction = Target.IsValid() ? ChaseDirection() : WanderDirection();

		FaceTowards( direction );

		controller.WishVelocity = _move.IsValid()
			? _move.UpdateMove( Rotation.Identity, direction )
			: direction * Player.ChaserMaxSpeed;
	}

	// ---------------------------------------------------------------- targeting

	HussPlayer FindTarget()
	{
		HussPlayer best = null;
		var bestDistance = float.MaxValue;

		foreach ( var other in Scene.GetAllComponents<HussPlayer>() )
		{
			if ( other == Player ) continue;
			if ( !other.IsRunner ) continue;
			if ( other.IsDowned ) continue;

			// No point queuing up outside a safe room - it can't be touched in there.
			if ( other.IsSafe ) continue;

			var distance = WorldPosition.Distance( other.WorldPosition );
			if ( distance > ChaseRange || distance >= bestDistance ) continue;

			best = other;
			bestDistance = distance;
		}

		return best;
	}

	// ------------------------------------------------------------------ steering

	Vector3 ChaseDirection()
	{
		var destination = Target.WorldPosition;

		if ( _repath || _path.Count == 0 )
		{
			BuildPath( destination );
			_repath = RepathInterval;
		}

		// Walk the corner list, dropping the ones we've already reached.
		while ( _corner < _path.Count &&
				WorldPosition.WithZ( 0 ).Distance( _path[_corner].WithZ( 0 ) ) <= CornerRadius )
		{
			_corner++;
		}

		if ( _corner < _path.Count )
			return Flatten( _path[_corner] - WorldPosition );

		// No usable route - head straight at them and feel our way past anything solid.
		return Avoid( Flatten( destination - WorldPosition ) );
	}

	Vector3 WanderDirection()
	{
		if ( !_hasWanderTarget || _rewander ||
			 WorldPosition.WithZ( 0 ).Distance( _wanderTo.WithZ( 0 ) ) <= CornerRadius * 2 )
		{
			// Vector3.Random is a point in a sphere, so flattening it can land on zero.
			var away = Vector3.Random.WithZ( 0 );
			away = away.IsNearZeroLength ? Vector3.Forward : away.Normal;

			_wanderTo = WorldPosition + away * WanderRadius;
			_hasWanderTarget = true;
			_rewander = 6.0f;

			BuildPath( _wanderTo );
		}

		while ( _corner < _path.Count &&
				WorldPosition.WithZ( 0 ).Distance( _path[_corner].WithZ( 0 ) ) <= CornerRadius )
		{
			_corner++;
		}

		if ( _corner < _path.Count )
			return Flatten( _path[_corner] - WorldPosition );

		return Avoid( Flatten( _wanderTo - WorldPosition ) );
	}

	/// <summary>
	/// Ask the navmesh for a route. Comes back empty if the scene has no navmesh, which is
	/// what the direct steering below is for.
	/// </summary>
	void BuildPath( Vector3 destination )
	{
		_path.Clear();
		_corner = 0;

		var navmesh = Scene.NavMesh;
		if ( navmesh is null || !navmesh.IsEnabled ) return;

		// Partial counts: if the runner has gone somewhere unreachable we still want to close
		// as much of the gap as we can rather than standing still.
		var result = navmesh.CalculatePath( new() { Start = WorldPosition, Target = destination } );
		if ( !result.IsValid || result.Points is null ) return;

		foreach ( var point in result.Points )
		{
			_path.Add( point.Position );
		}

		if ( _path.Count == 0 ) return;

		// The first corner is usually where we're already standing.
		if ( _path.Count > 1 && WorldPosition.WithZ( 0 ).Distance( _path[0].WithZ( 0 ) ) <= CornerRadius )
			_corner = 1;
	}

	static Vector3 Flatten( Vector3 v )
	{
		v = v.WithZ( 0 );
		return v.IsNearZeroLength ? Vector3.Zero : v.Normal;
	}

	/// <summary>
	/// Crude wall dodging for when there's no navmesh: if we can't go straight ahead, fan out
	/// to either side until something is clear. Enough to get round a corner, not enough to
	/// solve a maze - that's what the navmesh is for.
	/// </summary>
	Vector3 Avoid( Vector3 direction )
	{
		if ( direction.IsNearZeroLength ) return direction;
		if ( IsClear( direction ) ) return direction;

		for ( var angle = 25.0f; angle <= 90.0f; angle += 25.0f )
		{
			var left = Rotation.FromYaw( angle ) * direction;
			if ( IsClear( left ) ) return left;

			var right = Rotation.FromYaw( -angle ) * direction;
			if ( IsClear( right ) ) return right;
		}

		return direction;
	}

	bool IsClear( Vector3 direction )
	{
		var from = WorldPosition + Vector3.Up * 36.0f;

		var tr = Scene.Trace.Ray( from, from + direction * ProbeDistance )
			.Radius( 12.0f )
			.WithoutTags( HussTags.Runner, HussTags.Chaser )
			.Run();

		return !tr.Hit;
	}

	/// <summary>
	/// Turn our aim towards where we're going. ChaserAttack only swings at things roughly in
	/// front of the eye angles, so this is what lets the bot actually connect.
	/// </summary>
	void FaceTowards( Vector3 direction )
	{
		if ( direction.IsNearZeroLength ) return;

		var wanted = Rotation.LookAt( direction, Vector3.Up ).Angles().yaw;
		var current = Player.Controller.EyeAngles;

		Player.Controller.EyeAngles = current with
		{
			pitch = 0,
			roll = 0,
			yaw = current.yaw.LerpDegreesTo( wanted, (LookSpeed * Time.Delta / 180.0f).Clamp( 0, 1 ) ),
		};
	}
}
