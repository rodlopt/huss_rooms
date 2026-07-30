namespace Hussrooms;

/// <summary>
/// The chaser doesn't aim - get near a runner and it swings on its own.
///
/// A swing isn't instant. There's a windup, and the hit is only resolved when the windup
/// ends, so a runner who is moving fast enough gets out of the way. That's also where the
/// speed penalty is applied: the faster the runner is going, the smaller the radius the
/// chaser actually has to connect inside.
///
/// Enabled and disabled by <see cref="HussPlayer.ApplyTeam"/>, so it's only ever running
/// on someone who is currently a chaser.
/// </summary>
[Icon( "sports_mma" ), Group( "Hussrooms" ), Title( "Chaser Attack" )]
public sealed class ChaserAttack : Component
{
	[RequireComponent] public HussPlayer Player { get; set; }

	/// <summary>How close a runner has to be before we start swinging.</summary>
	[Property, Group( "Attack" )] public float Range { get; set; } = 95.0f;

	/// <summary>How close they have to still be when the swing lands.</summary>
	[Property, Group( "Attack" )] public float HitRange { get; set; } = 75.0f;

	/// <summary>The window a runner has to get clear once we've committed to a swing.</summary>
	[Property, Group( "Attack" )] public float WindupTime { get; set; } = 0.35f;

	/// <summary>Time between swings, whether we connected or not.</summary>
	[Property, Group( "Attack" )] public float Cooldown { get; set; } = 1.1f;

	/// <summary>How far off dead ahead a runner can be and still get swung at. 1 is straight on, 0 is 90 degrees.</summary>
	[Property, Group( "Attack" ), Range( -1, 1 )] public float FacingDot { get; set; } = 0.2f;

	/// <summary>Runner speed at which the dodge penalty is at its maximum.</summary>
	[Property, Group( "Dodging" )] public float DodgeSpeed { get; set; } = 320.0f;

	/// <summary>How much of the hit radius a flat-out runner takes away.</summary>
	[Property, Group( "Dodging" ), Range( 0, 1 )] public float MaxDodge { get; set; } = 0.6f;

	[Property, Group( "Sound" )] public SoundEvent SwingSound { get; set; }
	[Property, Group( "Sound" )] public SoundEvent HitSound { get; set; }

	/// <summary>True while a swing is in the air. The animator uses this.</summary>
	public bool IsSwinging { get; private set; }

	HussPlayer _target;
	TimeUntil _resolveAt;
	TimeUntil _readyAt;

	protected override void OnEnabled()
	{
		IsSwinging = false;
		_target = null;
		_readyAt = Cooldown;
	}

	protected override void OnUpdate()
	{
		// Proxies only ever see the broadcast that starts a swing, so the windup timer is
		// what puts the arm back down for them.
		if ( IsProxy )
		{
			if ( IsSwinging && _resolveAt ) IsSwinging = false;
			return;
		}

		// Only the chaser's own machine decides when it swings. The host gets the final say
		// on whether the hit counted.
		if ( !Player.IsValid() || Player.IsChaser || Player.IsDowned ) return;

		if ( IsSwinging )
		{
			if ( _resolveAt ) ResolveSwing();
			return;
		}

		if ( !_readyAt ) return;

		var target = FindTarget();
		if ( !target.IsValid() ) return;

		StartSwing( target );
	}

	// --------------------------------------------------------------- swinging

	void StartSwing( HussPlayer target )
	{
		_target = target;
		_readyAt = WindupTime + Cooldown;

		// The broadcast is what actually starts the swing, on us as well as everyone else,
		// so there's one timer driving it rather than two that can drift apart.
		BroadcastSwing();
	}

	void ResolveSwing()
	{
		IsSwinging = false;

		var target = _target;
		_target = null;

		if ( !CanBeHit( target ) ) return;

		// The faster they're moving the harder they are to catch. This is the difference
		// between a runner who panics into a wall and one who keeps their speed up.
		var speed = target.Controller.IsValid() ? target.Controller.Velocity.WithZ( 0 ).Length : 0.0f;
		var dodge = (speed / DodgeSpeed).Clamp( 0, 1 ) * MaxDodge;
		var reach = HitRange * (1.0f - dodge);

		if ( DistanceTo( target ) > reach ) return;
		if ( !HasLineOfSight( target ) ) return;

		SubmitHit( target );
	}

	/// <summary>
	/// Tell the host we landed one. It re-checks the range itself rather than trusting us.
	/// </summary>
	[Rpc.Host]
	void SubmitHit( HussPlayer target )
	{
		if ( Network.Owner != Rpc.Caller ) return;
		if ( !target.IsValid() || !Player.IsValid() ) return;
		if ( !Player.IsChaser ) return;

		// Generous compared to the client's own check - it only exists to reject nonsense.
		if ( DistanceTo( target ) > Range * 2.0f ) return;

		if ( !target.TakeHit( Player ) ) return;

		BroadcastHit( target.WorldPosition );

		// TakeHit sets IsDowned synchronously via GoDown, so this is "that was the fatal one".
		if ( target.IsDowned )
			Player.AwardStat( HussPlayer.StatKills, 1 );
	}

	[Rpc.Broadcast]
	void BroadcastSwing()
	{
		IsSwinging = true;
		_resolveAt = WindupTime;

		if ( SwingSound is not null )
			Sound.Play( SwingSound, WorldPosition );
	}

	[Rpc.Broadcast]
	void BroadcastHit( Vector3 position )
	{
		if ( HitSound is not null )
			Sound.Play( HitSound, position );
	}

	// ---------------------------------------------------------------- finding

	HussPlayer FindTarget()
	{
		HussPlayer best = null;
		var bestDistance = float.MaxValue;

		var forward = Player.Controller.IsValid()
			? Player.Controller.EyeAngles.ToRotation().Forward.WithZ( 0 ).Normal
			: WorldRotation.Forward;

		foreach ( var other in Scene.GetAllComponents<HussPlayer>() )
		{
			if ( !CanBeHit( other ) ) continue;

			var distance = DistanceTo( other );
			if ( distance > Range || distance >= bestDistance ) continue;

			// Roughly in front of us. Anything behind gets ignored so you can't hit
			// someone you've already skidded past.
			var toward = (other.WorldPosition - WorldPosition).WithZ( 0 ).Normal;
			if ( forward.Dot( toward ) < FacingDot ) continue;

			if ( !HasLineOfSight( other ) ) continue;

			best = other;
			bestDistance = distance;
		}

		return best;
	}

	bool CanBeHit( HussPlayer other )
	{
		if ( !other.IsValid() ) return false;
		if ( other == Player ) return false;
		if ( other.IsDowned ) return false;

		return true;
	}

	float DistanceTo( HussPlayer other )
	{
		// Compare at waist height so a tall model isn't harder to hit than a short one.
		var a = WorldPosition + Vector3.Up * 36.0f;
		var b = other.WorldPosition + Vector3.Up * 36.0f;

		return a.Distance( b );
	}

	bool HasLineOfSight( HussPlayer other )
	{
		var from = WorldPosition + Vector3.Up * 48.0f;
		var to = other.WorldPosition + Vector3.Up * 48.0f;

		// Players don't block each other, and neither do triggers - only real geometry.
		var tr = Scene.Trace.Ray( from, to )
			.WithoutTags( HussTags.Runner, HussTags.Chaser )
			.Run();

		return !tr.Hit;
	}
}
