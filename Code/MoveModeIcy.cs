using Sandbox.Movement;

namespace Hussrooms;

/// <summary>
/// The original Roblox controller, but the floor is polished marble.
///
/// Roblox movement is blunt: you hit your speed almost immediately and you turn on a dime.
/// That responsiveness is the part worth keeping, so acceleration and steering are both very
/// quick. The slip lives in what happens when you stop asking for anything - you keep your
/// momentum and coast, and hard direction changes at full pelt still carry you wide.
/// </summary>
[Icon( "ac_unit" ), Group( "Hussrooms" ), Title( "MoveMode - Icy" )]
public sealed class MoveModeIcy : MoveModeWalk
{
	/// <summary>
	/// Speed you snap to the instant you push a direction.
	/// </summary>
	[Property, Group( "Ramp" )] public float BaseSpeed { get; set; } = 230.0f;

	/// <summary>
	/// The fastest ramping up can get you. Scaled by <see cref="SpeedScale"/>.
	/// </summary>
	[Property, Group( "Ramp" )] public float MaxSpeed { get; set; } = 340.0f;

	/// <summary>
	/// Units per second added to your speed each second while you're holding a direction.
	/// </summary>
	[Property, Group( "Ramp" )] public float Acceleration { get; set; } = 420.0f;

	/// <summary>
	/// Units per second bled off your speed each second once you let go.
	/// </summary>
	[Property, Group( "Ramp" )] public float Deceleration { get; set; } = 900.0f;

	/// <summary>
	/// How quickly velocity turns towards where you're pushing. High enough to feel direct,
	/// low enough that you still lean through fast corners.
	/// </summary>
	[Property, Group( "Ice" )] public float Grip { get; set; } = 11.0f;

	/// <summary>
	/// How quickly you bleed off velocity when you're not pushing anything. This is the slip.
	/// </summary>
	[Property, Group( "Ice" )] public float SlideDrag { get; set; } = 1.1f;

	/// <summary>
	/// Steering authority while airborne.
	/// </summary>
	[Property, Group( "Ice" )] public float AirGrip { get; set; } = 2.5f;

	/// <summary>
	/// Physical friction of the feet against the floor. Near zero so we actually slide.
	/// </summary>
	[Property, Group( "Ice" )] public float IceFriction { get; set; } = 0.05f;

	/// <summary>
	/// Damping applied to the body while grounded. The base mode slams this up the instant
	/// you release the keys, which would stop the slide dead.
	/// </summary>
	[Property, Group( "Ice" )] public float GroundDamping { get; set; } = 0.3f;

	/// <summary>
	/// How fast the body swings round to face where you're moving, in degrees per second.
	/// Deliberately slow enough to watch - the character leans into the turn rather than
	/// teleporting its facing.
	/// </summary>
	[Property, Group( "Turning" )] public float TurnSpeed { get; set; } = 420.0f;

	/// <summary>
	/// How fast the body swings round when it's pinned to the camera - shift lock, right
	/// mouse, first person. Much faster than <see cref="TurnSpeed"/>, because here the body
	/// is meant to track the mouse one-to-one; anything slower feels like input lag.
	/// </summary>
	[Property, Group( "Turning" )] public float CameraTurnSpeed { get; set; } = 2000.0f;

	/// <summary>
	/// Multiplies <see cref="BaseSpeed"/> and <see cref="MaxSpeed"/>. Driven by
	/// <see cref="HussPlayer"/> for sprinting and for the chaser's flat speed.
	/// </summary>
	public float SpeedScale { get; set; } = 1.0f;

	/// <summary>
	/// Where the ramp is right now. Used by the HUD.
	/// </summary>
	public float CurrentSpeed { get; private set; }

	/// <summary>
	/// The speed we're currently ramping towards.
	/// </summary>
	public float TopSpeed => MaxSpeed * SpeedScale;

	HussPlayer _player;

	protected override void OnAwake()
	{
		base.OnAwake();

		_player = GetComponent<HussPlayer>( true );
	}

	public override Vector3 UpdateMove( Rotation eyes, Vector3 input )
	{
		// ignore pitch when walking, same as the base walk mode
		eyes = eyes.Angles() with { pitch = 0 };

		input = input.ClampLength( 1 );

		var direction = eyes * input;
		var dt = Time.Delta;

		if ( direction.IsNearlyZero( 0.1f ) )
		{
			direction = 0;
			CurrentSpeed = CurrentSpeed.Approach( 0, Deceleration * dt );
		}
		else
		{
			// Off the line at BaseSpeed straight away - the ramp is a top-end bonus, not the
			// thing standing between you and moving.
			CurrentSpeed = MathF.Max( CurrentSpeed, BaseSpeed * SpeedScale );
			CurrentSpeed = CurrentSpeed.Approach( TopSpeed, Acceleration * dt );
		}

		return direction * CurrentSpeed;
	}

	public override void AddVelocity()
	{
		var body = Controller.Body;
		if ( !body.IsValid() ) return;

		// Walk mode never wants vertical wish velocity.
		Controller.WishVelocity = Controller.WishVelocity.WithZ( 0 );

		var wish = Controller.WishVelocity;
		var ground = Controller.GroundVelocity;

		// Gravity, jumping and step-up all live in z - leave it exactly as physics left it.
		var z = body.Velocity.z;
		var flat = (body.Velocity - ground).WithZ( 0 );

		// Never let steering speed us up past what we asked for, but don't let it slow us
		// down either - the coast has to be free to run on, otherwise there's no slide.
		var cap = MathF.Max( wish.Length, flat.Length );

		var rate = Controller.IsOnGround
			? (wish.IsNearZeroLength ? SlideDrag : Grip)
			: AirGrip;

		flat = flat.LerpTo( wish, (rate * Time.Delta).Clamp( 0, 1 ) );

		if ( flat.Length > cap )
			flat = flat.Normal * cap;

		body.Velocity = (flat + ground).WithZ( z );
	}

	public override void UpdateRigidBody( Rigidbody body )
	{
		base.UpdateRigidBody( body );

		// Replace the base braking damping with something that lets us glide.
		body.LinearDamping = Controller.IsOnGround ? GroundDamping : Controller.AirFriction;
	}

	public override void PrePhysicsStep()
	{
		base.PrePhysicsStep();

		// UpdateBody() re-derives feet friction from BrakePower every single step, so this
		// has to be stomped back down here (after it runs) rather than once on start.
		if ( Controller.FeetCollider.IsValid() )
			Controller.FeetCollider.Friction = IceFriction;
	}

	/// <summary>
	/// The base walk mode turns the body to face the camera. We don't want that - the whole
	/// point of the orbit camera is that you can look one way and run another, so the body
	/// follows the direction you're steering instead.
	///
	/// <see cref="HussPlayer.FaceCamera"/> flips it back to camera-facing for right mouse,
	/// shift lock and first person. It's synced, so remote players turn the same way.
	/// </summary>
	protected override void OnRotateRenderBody( SkinnedModelRenderer renderer )
	{
		if ( !renderer.IsValid() ) return;

		Rotation target;
		float rate;

		if ( _player.IsValid() && _player.FaceCamera )
		{
			target = Rotation.FromYaw( Controller.EyeAngles.yaw );
			rate = CameraTurnSpeed;
		}
		else
		{
			// Steer off the wish velocity rather than actual velocity, so the character points
			// where you're asking to go instead of where the ice is taking you.
			var heading = Controller.WishVelocity.WithZ( 0 );

			// The key is what turns you, not a heading the body chases on its own. Let go
			// half way round and it stays half way round - no finishing the turn while you're
			// standing still or sliding. Tapping forward nudges you towards forward; holding
			// it is what actually gets you there.
			if ( heading.Length <= 1.0f ) return;

			target = Rotation.LookAt( heading.Normal, Vector3.Up );
			rate = TurnSpeed;
		}

		// Turn at a constant angular rate rather than an exponential ease, so a 180 takes a
		// predictable amount of time instead of crawling the last few degrees.
		var remaining = renderer.WorldRotation.Distance( target );
		var frac = remaining <= 0.01f ? 1.0f : (rate * Time.Delta / remaining).Clamp( 0, 1 );

		renderer.WorldRotation = Rotation.Slerp( renderer.WorldRotation, target, frac );
	}
}
