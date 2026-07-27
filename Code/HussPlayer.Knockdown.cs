namespace Hussrooms;

public partial class HussPlayer : Component.ICollisionListener
{
	[Property, Group( "Knockdown" ), Range( 0.1f, 10.0f )]
	public float KnockdownRecoveryTime { get; set; } = 2.5f;

	[Property, Group( "Knockdown" )]
	public float SlipSpeedThreshold { get; set; } = 250.0f;

	[Property, Group( "Knockdown" )]
	public float ThrownPropSpeedThreshold { get; set; } = 400.0f;

	bool _recoverFromKnockdown;
	TimeSince _timeSinceKnockdownRequest;

	void Component.ICollisionListener.OnCollisionStart( Collision collision )
	{
		if ( IsProxy || !IsChaser || IsDowned ) return;
		if ( _timeSinceKnockdownRequest < 0.25f ) return;

		var prop = collision.Other.GameObject;
		if ( !prop.IsValid() || !prop.Tags.Has( "prop" ) ) return;

		var playerVelocity = collision.Self.Body.IsValid()
			? collision.Self.Body.Velocity
			: Controller.Velocity;

		var propVelocity = collision.Other.Body.IsValid()
			? collision.Other.Body.Velocity
			: Vector3.Zero;

		var slipped = playerVelocity.WithZ( 0 ).Length >= SlipSpeedThreshold;
		var wasHit = propVelocity.Length >= ThrownPropSpeedThreshold;
		if ( !slipped && !wasHit ) return;

		_timeSinceKnockdownRequest = 0;

		var ragdollVelocity = playerVelocity;
		if ( wasHit )
			ragdollVelocity += propVelocity * 0.35f;

		RequestPropKnockdown( prop, ragdollVelocity );
	}

	[Rpc.Host]
	void RequestPropKnockdown( GameObject prop, Vector3 ragdollVelocity )
	{
		if ( Network.Owner != Rpc.Caller ) return;
		if ( !IsChaser || IsDowned ) return;
		if ( !prop.IsValid() || !prop.Tags.Has( "prop" ) ) return;
		if ( prop.WorldPosition.Distance( WorldPosition ) > 256.0f ) return;

		_recoverFromKnockdown = true;
		SpawnRagdoll( ragdollVelocity );
		_respawnAt = KnockdownRecoveryTime;
		IsDowned = true;
	}
}
