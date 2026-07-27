using System;
using System.Collections.Generic;
using Sandbox.Navigation;

namespace Hussrooms;

/// <summary>
/// A NavMesh-driven Captain Clark that hunts runners.
///
/// The host steers every bot and resolves its hits. Remote clients only animate the
/// replicated transform and attack broadcasts.
/// </summary>
[Icon( "smart_toy" ), Group( "Hussrooms" ), Title( "Captain Clark Bot" )]
public sealed class CaptainClarkBot : Component, Component.INetworkListener
{
	public const string PrefabPath = "prefabs/bots/captain_clark_bot.prefab";

	[RequireComponent] public NavMeshAgent Agent { get; set; }
	[RequireComponent] public CharacterController Controller { get; set; }

	[Property, Group( "References" )]
	public SkinnedModelRenderer Renderer { get; set; }

	/// <summary>The player whose Prop Menu created this bot. Assigned by the host spawner.</summary>
	[Sync( SyncFlags.FromHost )]
	public HussPlayer Spawner { get; set; }

	[Property, Group( "Awareness" ), Range( 128.0f, 10000.0f )]
	public float DetectionRange { get; set; } = 5000.0f;

	[Property, Group( "Awareness" ), Range( 128.0f, 12000.0f )]
	public float LoseTargetRange { get; set; } = 6000.0f;

	[Property, Group( "Awareness" ), Range( 0.1f, 2.0f )]
	public float TargetRefreshTime { get; set; } = 0.4f;

	[Property, Group( "Awareness" ), Range( 0.5f, 5.0f )]
	public float PathValidationTime { get; set; } = 1.5f;

	[Property, Group( "Awareness" ), Range( 1, 16 )]
	public int MaxPathCandidates { get; set; } = 4;

	[Property, Group( "Awareness" ), Range( 0.25f, 10.0f )]
	public float UnreachableRetryTime { get; set; } = 2.0f;

	[Property, Group( "Movement" ), Range( 1.0f, 1000.0f )]
	public float MoveSpeed { get; set; } = 320.0f;

	[Property, Group( "Movement" ), Range( 1.0f, 3000.0f )]
	public float MovementAcceleration { get; set; } = 900.0f;

	[Property, Group( "Movement" ), Range( 0.05f, 1.0f )]
	public float RepathTime { get; set; } = 0.25f;

	[Property, Group( "Movement" ), Range( 1.0f, 256.0f )]
	public float RepathDistance { get; set; } = 32.0f;

	[Property, Group( "Movement" ), Range( 0.0f, 1.0f )]
	public float PredictionTime { get; set; } = 0.3f;

	[Property, Group( "Movement" ), Range( 0.0f, 256.0f )]
	public float MaxPredictionDistance { get; set; } = 96.0f;

	[Property, Group( "Movement" ), Range( 1.0f, 30.0f )]
	public float TurnSpeed { get; set; } = 10.0f;

	[Property, Group( "Movement" ), Range( 0.0f, 2000.0f )]
	public float Gravity { get; set; } = 800.0f;

	[Property, Group( "Recovery" ), Range( 0.25f, 5.0f )]
	public float StuckCheckTime { get; set; } = 1.0f;

	[Property, Group( "Recovery" ), Range( 1.0f, 128.0f )]
	public float MinimumProgress { get; set; } = 18.0f;

	[Property, Group( "Attack" ), Range( 16.0f, 256.0f )]
	public float AttackRange { get; set; } = 105.0f;

	[Property, Group( "Attack" ), Range( 16.0f, 256.0f )]
	public float HitRange { get; set; } = 90.0f;

	[Property, Group( "Attack" ), Range( 0.05f, 2.0f )]
	public float WindupTime { get; set; } = 0.3f;

	[Property, Group( "Attack" ), Range( 0.1f, 5.0f )]
	public float AttackCooldown { get; set; } = 0.35f;

	[Property, Group( "Attack" ), Range( 0.1f, 5.0f )]
	public float AttackAnimationTime { get; set; } = 1.05f;

	[Property, Group( "Attack" ), Range( 0.1f, 5.0f )]
	public float AttackPlaybackRate { get; set; } = 2.2f;

	[Property, Group( "Attack" ), Range( -1.0f, 1.0f )]
	public float AttackFacingDot { get; set; } = 0.25f;

	[Property, Group( "Attack" ), Range( 0.0f, 128.0f )]
	public float HostRangeTolerance { get; set; } = 24.0f;

	[Property, Group( "Animation" ), Range( 1.0f, 1000.0f )]
	public float FullMoveSpeed { get; set; } = 320.0f;

	[Property, Group( "Animation" ), Range( 1.0f, 40.0f )]
	public float MoveSmoothing { get; set; } = 12.0f;

	[Property, Group( "Sound" )] public SoundEvent SwingSound { get; set; }
	[Property, Group( "Sound" )] public SoundEvent HitSound { get; set; }

	/// <summary>True during the attack wind-up. Used by the Captain Clark animation graph.</summary>
	public bool IsAttacking { get; private set; }

	HussPlayer _target;
	HussPlayer _attackTarget;

	TimeUntil _refreshTargetAt;
	TimeUntil _validatePathsAt;
	TimeUntil _repathAt;
	TimeUntil _resolveAttackAt;
	TimeUntil _finishAttackAt;
	TimeUntil _readyToAttackAt;
	TimeUntil _stuckCheckAt;
	TimeSince _timeSinceHit;

	Vector3 _lastProgressPosition;
	Vector3 _lastAnimationPosition;
	Vector3 _lastPathTarget;
	float _animationMove;
	float _normalPlaybackRate = 1.0f;
	bool _hasPathRequest;
	bool _hadSpawner;
	bool _hitResolved;

	readonly Dictionary<HussPlayer, TimeUntil> _unreachableTargets = new();

	protected override void OnAwake()
	{
		if ( !Renderer.IsValid() )
			Renderer = GetComponentInChildren<SkinnedModelRenderer>( true );

		if ( Renderer.IsValid() )
			_normalPlaybackRate = Renderer.PlaybackRate;

		_lastProgressPosition = WorldPosition;
		_lastAnimationPosition = WorldPosition;
	}

	void Component.INetworkListener.OnBecameHost( Connection previousHost )
	{
		// Menu-spawned bots always have a spawner. If that was the departing
		// host, remove the orphan instead of leaving an undeletable enemy behind.
		_hadSpawner = true;
		if ( !Spawner.IsValid() )
		{
			GameObject.Destroy();
			return;
		}

		Agent.Enabled = true;
		Agent.UpdatePosition = false;
		Agent.UpdateRotation = false;
		Agent.MaxSpeed = MoveSpeed;
		Agent.Acceleration = MovementAcceleration;

		var nearest = Scene.NavMesh.GetClosestPoint( WorldPosition, 256.0f );
		if ( nearest.HasValue )
			WorldPosition = nearest.Value;

		Agent.SetAgentPosition( WorldPosition );

		_target = null;
		_attackTarget = null;
		IsAttacking = false;
		_refreshTargetAt = 0;
		_validatePathsAt = 0;
		_repathAt = 0;
		_stuckCheckAt = StuckCheckTime;
		_readyToAttackAt = 0.2f;
		_timeSinceHit = AttackCooldown;
		_hasPathRequest = false;
		_hitResolved = false;
		_unreachableTargets.Clear();
		_lastProgressPosition = WorldPosition;
		_lastAnimationPosition = WorldPosition;

		if ( Renderer.IsValid() )
			Renderer.PlaybackRate = _normalPlaybackRate;
	}

	protected override void OnEnabled()
	{
		_target = null;
		_attackTarget = null;
		IsAttacking = false;

		_refreshTargetAt = 0;
		_validatePathsAt = 0;
		_repathAt = 0;
		_stuckCheckAt = StuckCheckTime;
		_readyToAttackAt = 0.2f;
		_timeSinceHit = AttackCooldown;

		_lastProgressPosition = WorldPosition;
		_lastAnimationPosition = WorldPosition;
		_animationMove = 0;
		_hasPathRequest = false;
		_hadSpawner = false;
		_hitResolved = false;
		_unreachableTargets.Clear();

		if ( Renderer.IsValid() )
			Renderer.PlaybackRate = _normalPlaybackRate;
	}

	protected override void OnStart()
	{
		// The agent supplies steering, while CharacterController owns the collision-
		// constrained transform. Keeping both update flags off is also important on
		// proxies, where the network interpolator owns the visible transform.
		Agent.UpdatePosition = false;
		Agent.UpdateRotation = false;
		Agent.MaxSpeed = MoveSpeed;
		Agent.Acceleration = MovementAcceleration;

		if ( !Networking.IsHost )
		{
			// A proxy's transform comes from network interpolation. Its own crowd
			// agent would stay behind at the spawn point and waste avoidance capacity.
			Agent.Enabled = false;
			return;
		}

		_hadSpawner = Spawner.IsValid();

		// Prop-menu items are spawned at eye height. Put Clark's feet on the nearest
		// walkable point immediately rather than waiting for gravity to settle him.
		var nearest = Scene.NavMesh.GetClosestPoint( WorldPosition, 256.0f );
		if ( nearest.HasValue )
			WorldPosition = nearest.Value;

		Agent.SetAgentPosition( WorldPosition );
		_lastProgressPosition = WorldPosition;
		_lastAnimationPosition = WorldPosition;
	}

	protected override void OnDisabled()
	{
		if ( Networking.IsHost && Agent.IsValid() )
			Agent.Stop();

		IsAttacking = false;
		if ( Renderer.IsValid() )
			Renderer.PlaybackRate = _normalPlaybackRate;
	}

	protected override void OnUpdate()
	{
		UpdateAnimation();

		if ( !Networking.IsHost )
		{
			if ( Agent.IsValid() && Agent.Enabled )
				Agent.Enabled = false;

			if ( IsAttacking && _finishAttackAt )
				FinishAttack();

			return;
		}

		if ( Spawner.IsValid() )
			_hadSpawner = true;
		else if ( _hadSpawner )
		{
			GameObject.Destroy();
			return;
		}

		UpdateTarget();

		if ( IsAttacking )
		{
			if ( _attackTarget.IsValid() )
				FaceDirection( _attackTarget.WorldPosition - WorldPosition );

			if ( !_hitResolved && _resolveAttackAt )
				ResolveAttack();

			if ( _finishAttackAt )
				FinishAttack();

			return;
		}

		if ( !_readyToAttackAt || !CanTarget( _target ) ) return;
		if ( DistanceTo( _target ) > AttackRange ) return;
		if ( !HasLineOfSight( _target ) ) return;

		var toward = (_target.WorldPosition - WorldPosition).WithZ( 0 );
		if ( toward.Length > 0.01f && WorldRotation.Forward.Dot( toward.Normal ) < AttackFacingDot )
			return;

		StartAttack( _target );
	}

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsHost || !Agent.IsValid() || !Controller.IsValid() ) return;

		if ( !CanTarget( _target ) )
		{
			Agent.Stop();
			MoveCharacter( Vector3.Zero );
			return;
		}

		var distance = DistanceTo( _target );

		var holdingHitDistance = distance <= HitRange && HasLineOfSight( _target );
		if ( IsAttacking || holdingHitDistance )
		{
			Agent.Stop();
			MoveCharacter( Vector3.Zero );
			FaceDirection( _target.WorldPosition - WorldPosition );
			return;
		}

		if ( _repathAt )
		{
			RequestPathToTarget();
			_repathAt = RepathTime;
		}

		var desiredVelocity = Agent.WishVelocity.WithZ( 0 );

		MoveCharacter( desiredVelocity );

		var facing = Agent.IsNavigating
			? Agent.GetLookAhead( 30.0f ) - WorldPosition
			: desiredVelocity;

		FaceDirection( facing );
		UpdateStuckRecovery( desiredVelocity, distance );
	}

	void UpdateTarget()
	{
		var currentIsUsable = CanTarget( _target )
			&& !IsTemporarilyUnreachable( _target )
			&& DistanceTo( _target ) <= LoseTargetRange;

		if ( !currentIsUsable && _target.IsValid() )
		{
			_target = null;
			_attackTarget = null;
			Agent.Stop();
			_refreshTargetAt = 0;
			_validatePathsAt = 0;
			_hasPathRequest = false;
		}

		if ( !_refreshTargetAt ) return;
		_refreshTargetAt = TargetRefreshTime;
		PruneUnreachableTargets();

		if ( currentIsUsable && !_validatePathsAt ) return;
		_validatePathsAt = PathValidationTime;

		var replacement = FindBestTarget();
		currentIsUsable &= !IsTemporarilyUnreachable( _target );

		// Keep pursuing the current runner unless the alternative is meaningfully
		// better. This prevents several nearby players from making Clark twitch
		// between paths every refresh.
		if ( currentIsUsable && replacement.IsValid() && replacement != _target )
		{
			var currentScore = TargetScore( _target );
			var replacementScore = TargetScore( replacement );

			if ( replacementScore > currentScore * 0.8f )
				replacement = _target;
		}
		else if ( currentIsUsable && !replacement.IsValid() )
		{
			replacement = _target;
		}

		if ( replacement == _target ) return;

		_target = replacement;
		_attackTarget = null;
		_repathAt = 0;
		_validatePathsAt = PathValidationTime;
		_hasPathRequest = false;
		_lastProgressPosition = WorldPosition;
		_stuckCheckAt = StuckCheckTime;

		if ( !_target.IsValid() )
			Agent.Stop();
	}

	HussPlayer FindBestTarget()
	{
		var candidates = new List<TargetCandidate>();

		foreach ( var player in Scene.GetAllComponents<HussPlayer>() )
		{
			if ( !CanTarget( player ) ) continue;
			if ( IsTemporarilyUnreachable( player ) ) continue;

			var distance = DistanceTo( player );
			var maximumRange = player == _target ? LoseTargetRange : DetectionRange;
			if ( distance > maximumRange ) continue;

			candidates.Add( new TargetCandidate(
				player,
				TargetScore( player, distance )
			) );
		}

		if ( candidates.Count == 0 ) return null;

		candidates.Sort( ( a, b ) => a.DirectScore.CompareTo( b.DirectScore ) );

		// During the first few frames the runtime NavMesh may still be initializing.
		// Keep a sensible target ready, but wait for the agent rather than walking
		// directly off the mesh.
		var navStart = Scene.NavMesh.GetClosestPoint( WorldPosition, 128.0f );
		if ( !navStart.HasValue )
			return candidates[0].Player;

		HussPlayer best = null;
		var bestPathScore = float.MaxValue;
		var checks = Math.Min( MaxPathCandidates, candidates.Count );
		var checkedCurrent = false;

		for ( var i = 0; i < checks; i++ )
		{
			var candidate = candidates[i];
			checkedCurrent |= candidate.Player == _target;
			ConsiderCandidate( candidate, ref best, ref bestPathScore );
		}

		// Always revalidate the runner already being chased, even if a crowded
		// server pushed them outside the nearest-candidate budget.
		if ( _target.IsValid() && !checkedCurrent )
		{
			foreach ( var candidate in candidates )
			{
				if ( candidate.Player != _target ) continue;

				ConsiderCandidate( candidate, ref best, ref bestPathScore );
				break;
			}
		}

		return best;
	}

	void ConsiderCandidate(
		TargetCandidate candidate,
		ref HussPlayer best,
		ref float bestPathScore
	)
	{
		if ( !TryGetPathLength( candidate.Player, out var pathLength ) )
		{
			MarkTemporarilyUnreachable( candidate.Player );
			return;
		}

		// Actual route length is the important part. DirectScore breaks close
		// ties in favor of a runner Clark can currently see.
		var pathScore = pathLength + candidate.DirectScore * 0.05f;
		if ( pathScore >= bestPathScore ) return;

		best = candidate.Player;
		bestPathScore = pathScore;
	}

	bool TryGetPathLength( HussPlayer player, out float length )
	{
		length = 0;

		var path = Scene.NavMesh.CalculatePath( new CalculatePathRequest
		{
			Start = WorldPosition,
			Target = player.WorldPosition,
			Agent = Agent
		} );

		if ( path.Status != NavMeshPathStatus.Complete || path.Points is null )
			return false;

		var previous = WorldPosition;
		foreach ( var point in path.Points )
		{
			length += previous.Distance( point.Position );
			previous = point.Position;
		}

		return true;
	}

	void PruneUnreachableTargets()
	{
		if ( _unreachableTargets.Count == 0 ) return;

		var remove = new List<HussPlayer>();
		foreach ( var pair in _unreachableTargets )
		{
			if ( !pair.Key.IsValid() || pair.Value )
				remove.Add( pair.Key );
		}

		foreach ( var player in remove )
			_unreachableTargets.Remove( player );
	}

	bool IsTemporarilyUnreachable( HussPlayer player )
	{
		if ( !player.IsValid() ) return false;
		if ( !_unreachableTargets.TryGetValue( player, out var retryAt ) ) return false;
		if ( !retryAt ) return true;

		_unreachableTargets.Remove( player );
		return false;
	}

	void MarkTemporarilyUnreachable( HussPlayer player )
	{
		if ( player.IsValid() )
			_unreachableTargets[player] = UnreachableRetryTime;
	}

	float TargetScore( HussPlayer player )
	{
		return TargetScore( player, DistanceTo( player ) );
	}

	float TargetScore( HussPlayer player, float distance )
	{
		// Prefer a visible runner when two targets are similarly close. This keeps
		// Clark decisive in doorways without abandoning a much nearer target.
		return HasLineOfSight( player ) ? distance * 0.75f : distance;
	}

	Vector3 PredictTargetPosition( HussPlayer player )
	{
		var predicted = player.WorldPosition;
		if ( !player.Controller.IsValid() ) return predicted;

		var lead = player.Controller.Velocity.WithZ( 0 ) * PredictionTime;
		if ( lead.Length > MaxPredictionDistance )
			lead = lead.Normal * MaxPredictionDistance;

		return predicted + lead;
	}

	void RequestPathToTarget()
	{
		var targetPosition = PredictTargetPosition( _target );
		var targetMoved = !_hasPathRequest
			|| targetPosition.Distance( _lastPathTarget ) >= RepathDistance;

		if ( !targetMoved && Agent.IsNavigating ) return;

		Agent.MoveTo( targetPosition );
		_lastPathTarget = targetPosition;
		_hasPathRequest = true;
	}

	void MoveCharacter( Vector3 desiredVelocity )
	{
		desiredVelocity = desiredVelocity.WithZ( 0 );
		if ( desiredVelocity.Length > MoveSpeed )
			desiredVelocity = desiredVelocity.Normal * MoveSpeed;

		var horizontal = Controller.Velocity.WithZ( 0 )
			.WithAcceleration( desiredVelocity, MovementAcceleration * Time.Delta );

		var vertical = Controller.Velocity.z;
		if ( Controller.IsOnGround )
			vertical = 0;
		else
			vertical -= Gravity * Time.Delta;

		Controller.Velocity = horizontal.WithZ( vertical );
		Controller.Move();

		// NavMeshAgent's crowd simulation has its own position. Feed the real,
		// collision-constrained position back into it after every move.
		Agent.SetAgentPosition( WorldPosition );
	}

	void UpdateStuckRecovery( Vector3 desiredVelocity, float targetDistance )
	{
		if ( !_stuckCheckAt ) return;

		_stuckCheckAt = StuckCheckTime;

		var travelled = (WorldPosition - _lastProgressPosition).WithZ( 0 ).Length;
		_lastProgressPosition = WorldPosition;

		if ( desiredVelocity.Length < 20.0f || targetDistance <= AttackRange * 1.25f )
			return;

		if ( travelled >= MinimumProgress ) return;

		// Re-seat the crowd agent and let target selection try another reachable
		// runner before this one is considered again.
		Agent.Stop();
		Agent.SetAgentPosition( WorldPosition );
		MarkTemporarilyUnreachable( _target );
		_refreshTargetAt = 0;
		_validatePathsAt = 0;
		_repathAt = 0;
		_hasPathRequest = false;
	}

	void FaceDirection( Vector3 direction )
	{
		direction = direction.WithZ( 0 );
		if ( direction.Length < 0.01f ) return;

		var targetRotation = Rotation.LookAt( direction.Normal );
		var fraction = (TurnSpeed * Time.Delta).Clamp( 0, 1 );
		WorldRotation = Rotation.Slerp( WorldRotation, targetRotation, fraction );
	}

	void StartAttack( HussPlayer target )
	{
		_attackTarget = target;
		_readyToAttackAt = Math.Max( AttackAnimationTime, WindupTime ) + AttackCooldown;
		BroadcastAttack();
	}

	void ResolveAttack()
	{
		_hitResolved = true;

		var target = _attackTarget;
		_attackTarget = null;

		if ( !CanTarget( target ) ) return;
		if ( DistanceTo( target ) > HitRange ) return;
		if ( !HasLineOfSight( target ) ) return;

		ApplyHit( target );
	}

	void FinishAttack()
	{
		IsAttacking = false;
		_attackTarget = null;

		if ( Renderer.IsValid() )
			Renderer.PlaybackRate = _normalPlaybackRate;
	}

	void ApplyHit( HussPlayer target )
	{
		if ( !Networking.IsHost ) return;
		if ( !CanTarget( target ) ) return;
		if ( _timeSinceHit < AttackCooldown * 0.75f ) return;
		if ( DistanceTo( target ) > HitRange + HostRangeTolerance ) return;
		if ( !HasLineOfSight( target ) ) return;

		_timeSinceHit = 0;

		if ( target.TakeHit( null ) )
			BroadcastHit( target.WorldPosition );
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	void BroadcastAttack()
	{
		IsAttacking = true;
		_hitResolved = false;
		_resolveAttackAt = WindupTime;
		_finishAttackAt = Math.Max( AttackAnimationTime, WindupTime );

		if ( Renderer.IsValid() )
			Renderer.PlaybackRate = AttackPlaybackRate;

		if ( SwingSound is not null )
			Sound.Play( SwingSound, WorldPosition );
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	void BroadcastHit( Vector3 position )
	{
		if ( HitSound is not null )
			Sound.Play( HitSound, position );
	}

	void UpdateAnimation()
	{
		if ( !Renderer.IsValid() || !Renderer.Active ) return;

		var travelled = (WorldPosition - _lastAnimationPosition).WithZ( 0 );
		var teleported = travelled.Length > 400.0f;
		_lastAnimationPosition = WorldPosition;

		var speed = !teleported && Time.Delta > 0
			? travelled.Length / Time.Delta
			: 0;

		var targetMove = FullMoveSpeed <= 0
			? 0
			: (speed / FullMoveSpeed).Clamp( 0, 1 );

		_animationMove = _animationMove.LerpTo(
			targetMove,
			(Time.Delta * MoveSmoothing).Clamp( 0, 1 )
		);

		Renderer.Set( "move", _animationMove );
		Renderer.Set( "punching", IsAttacking );
	}

	bool CanTarget( HussPlayer player )
	{
		if ( !player.IsValid() ) return false;
		if ( !player.IsRunner ) return false;
		if ( player.IsDowned || player.IsSafe ) return false;

		return true;
	}

	float DistanceTo( HussPlayer player )
	{
		var botWaist = WorldPosition + Vector3.Up * 36.0f;
		var playerWaist = player.WorldPosition + Vector3.Up * 36.0f;
		return botWaist.Distance( playerWaist );
	}

	bool HasLineOfSight( HussPlayer player )
	{
		var from = WorldPosition + Vector3.Up * 48.0f;
		var to = player.WorldPosition + Vector3.Up * 48.0f;

		var trace = Scene.Trace.Ray( from, to )
			.WithoutTags( HussTags.Runner, HussTags.Chaser, "trigger" )
			.Run();

		return !trace.Hit;
	}

	readonly record struct TargetCandidate( HussPlayer Player, float DirectScore );
}
