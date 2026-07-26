namespace Hussrooms;

/// <summary>
/// Drives the Clark / Captain Clark animation graphs.
///
/// PlayerController's built-in animator speaks Citizen (move_x, b_grounded, and so on), which
/// these graphs don't have. It stays on because it also handles turning the body, which
/// <see cref="MoveModeIcy.OnRotateRenderBody"/> overrides. This fills in the parameters the
/// graphs actually expose: move, jump, and punching on the chaser.
/// </summary>
[Icon( "directions_walk" ), Group( "Hussrooms" ), Title( "Huss Animator" )]
public sealed class HussAnimator : Component
{
	[RequireComponent] public PlayerController Controller { get; set; }

	/// <summary>
	/// Speed at which the run animation is playing at full blend. Should be roughly a runner's
	/// top speed, or the legs cap out long before the character does.
	/// </summary>
	[Property] public float FullMoveSpeed { get; set; } = 340.0f;

	/// <summary>
	/// How quickly the move parameter catches up, so the legs don't snap between poses.
	/// </summary>
	[Property] public float MoveSmoothing { get; set; } = 12.0f;

	ChaserAttack _attack;
	float _move;
	bool _wasOnGround = true;
	Vector3 _lastPosition;

	protected override void OnAwake()
	{
		// includeDisabled - it's only enabled while we're a chaser.
		_attack = GetComponent<ChaserAttack>( true );
		_lastPosition = WorldPosition;
	}

	protected override void OnEnabled()
	{
		_lastPosition = WorldPosition;
		_move = 0;
	}

	protected override void OnUpdate()
	{
		var renderer = Controller?.Renderer;
		if ( !renderer.IsValid() || !renderer.Active ) return;

		// Measured from how far the body actually moved rather than from the rigidbody, so
		// remote players animate off their interpolated position instead of standing still.
		var travelled = (WorldPosition - _lastPosition).WithZ( 0 );
		var teleported = travelled.Length > 400.0f;

		_lastPosition = WorldPosition;

		var speed = !teleported && Time.Delta > 0 ? travelled.Length / Time.Delta : 0;

		// The graph clamps move to 0..1.
		var target = FullMoveSpeed <= 0 ? 0 : (speed / FullMoveSpeed).Clamp( 0, 1 );

		_move = _move.LerpTo( target, (Time.Delta * MoveSmoothing).Clamp( 0, 1 ) );

		renderer.Set( "move", _move );
		renderer.Set( "punching", _attack.IsValid() && _attack.Enabled && _attack.IsSwinging );

		// 'jump' auto-resets in the graph, so it just wants a single pulse on takeoff.
		if ( _wasOnGround && !Controller.IsOnGround && !teleported )
			renderer.Set( "jump", true );

		_wasOnGround = Controller.IsOnGround;
	}
}
