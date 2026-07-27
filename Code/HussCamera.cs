namespace Hussrooms;

/// <summary>
/// A Roblox-style third person camera. It orbits the character on the mouse, scrolls in and
/// out with the wheel, and goes first person if you scroll all the way in.
///
/// Who the character faces is decided here too, because it's a camera question:
///
/// <list type="bullet">
/// <item>Normally the body faces where you're moving, not where the camera is pointing.</item>
/// <item>Holding right mouse turns the body with the camera, so you can strafe and back up.</item>
/// <item>Shift lock pins the body to the camera permanently and shifts the view to the shoulder.</item>
/// <item>First person is always body-follows-camera - there's nothing else it could sensibly do.</item>
/// </list>
///
/// This replaces PlayerController's own camera, so <c>UseCameraControls</c> must be off on
/// the controller. Look controls stay on - EyeAngles is still the orbit angle.
/// </summary>
[Icon( "videocam" ), Group( "Hussrooms" ), Title( "Huss Camera" )]
public sealed class HussCamera : Component, ICameraModifier
{
	[RequireComponent] public PlayerController Controller { get; set; }

	[Property, Group( "Zoom" )] public float MinDistance { get; set; } = 0.0f;
	[Property, Group( "Zoom" )] public float MaxDistance { get; set; } = 520.0f;
	[Property, Group( "Zoom" )] public float StartDistance { get; set; } = 230.0f;

	/// <summary>How much one wheel notch moves the camera.</summary>
	[Property, Group( "Zoom" )] public float ZoomStep { get; set; } = 55.0f;

	/// <summary>How quickly the camera catches up to the zoom you asked for.</summary>
	[Property, Group( "Zoom" )] public float ZoomSmoothing { get; set; } = 16.0f;

	/// <summary>Anything closer than this counts as first person.</summary>
	[Property, Group( "Zoom" )] public float FirstPersonDistance { get; set; } = 40.0f;

	/// <summary>Nudges the orbit pivot off the exact eye position so the head isn't dead centre.</summary>
	[Property, Group( "Framing" )] public Vector3 PivotOffset { get; set; } = new Vector3( 0, 0, 6 );

	/// <summary>How far right the view slides in shift lock, putting the body on the left.</summary>
	[Property, Group( "Framing" )] public float ShoulderOffset { get; set; } = 22.0f;

	/// <summary>How quickly the shoulder offset slides in and out.</summary>
	[Property, Group( "Framing" )] public float ShoulderSmoothing { get; set; } = 10.0f;

	[Property, Group( "Framing" )] public float CollisionRadius { get; set; } = 8.0f;
	[Property, Group( "Framing" )] public TagSet CollisionIgnore { get; set; } = [];

	[Property, Group( "Controls" ), InputAction] public string ShiftLockButton { get; set; } = "ShiftLock";
	[Property, Group( "Controls" ), InputAction] public string RotateBodyButton { get; set; } = "Attack2";

	/// <summary>Shift lock is a toggle, same as Roblox.</summary>
	public bool ShiftLock { get; private set; }

	public bool IsFirstPerson => _distance <= FirstPersonDistance;

	/// <summary>
	/// True when the body should be pinned to the camera instead of following its own movement.
	/// </summary>
	public bool FaceCamera => ShiftLock || IsFirstPerson || _holdingRotate;

	float _wantedDistance;
	float _distance;
	float _shoulder;
	Vector3 _lastViewPosition;
	Rotation _lastViewRotation;
	float _lastViewFieldOfView;
	bool _holdingRotate;
	bool _hasLastView;
	HussPlayer _player;

	protected override void OnAwake()
	{
		_player = GetComponent<HussPlayer>( true );

		_wantedDistance = StartDistance.Clamp( MinDistance, MaxDistance );
		_distance = _wantedDistance;
	}

	protected override void OnUpdate()
	{
		if ( IsProxy ) return;

		var locked = _player.IsValid() && (_player.InputLocked || _player.IsDowned);

		if ( !locked )
		{
			// Wheel up pulls the camera in, same direction as every other game.
			var wheel = Input.MouseWheel.y;
			if ( wheel != 0 )
				_wantedDistance = (_wantedDistance - wheel * ZoomStep).Clamp( MinDistance, MaxDistance );

			if ( !string.IsNullOrWhiteSpace( ShiftLockButton ) && Input.Pressed( ShiftLockButton ) )
				ShiftLock = !ShiftLock;

			_holdingRotate = !string.IsNullOrWhiteSpace( RotateBodyButton ) && Input.Down( RotateBodyButton );

			_distance = _distance.LerpTo( _wantedDistance, (Time.Delta * ZoomSmoothing).Clamp( 0, 1 ) );
			_shoulder = _shoulder.LerpTo( ShiftLock && !IsFirstPerson ? ShoulderOffset : 0.0f,
				(Time.Delta * ShoulderSmoothing).Clamp( 0, 1 ) );
		}
		else
		{
			_holdingRotate = false;
		}

		// Published so the body rotation - which runs on every machine, not just ours - knows
		// whether we're pinned to the camera.
		if ( _player.IsValid() )
			_player.FaceCamera = FaceCamera;

		UpdateBodyVisibility();
	}

	/// <summary>
	/// PlayerController normally owns the "viewer" tag, but it skips that entirely when its own
	/// camera is turned off, so we do it. Tagging the body 'viewer' hides it from our own view.
	/// </summary>
	void UpdateBodyVisibility()
	{
		var body = Controller?.Renderer?.GameObject ?? GameObject;
		if ( !body.IsValid() ) return;

		body.Tags.Set( "viewer", !IsProxy && IsFirstPerson );
	}

	int ICameraModifier.CameraOrder => 0;

	void ICameraModifier.ModifyCamera( CameraComponent cam, ref CameraView view )
	{
		if ( IsProxy ) return;
		if ( Scene.Camera != cam ) return;
		if ( !Controller.IsValid() ) return;

		if ( !cam.RenderExcludeTags.Contains( "viewer" ) )
			cam.RenderExcludeTags.Add( "viewer" );

		if ( _player.IsValid() && _player.IsDowned && _hasLastView )
		{
			ApplyView( cam, ref view, _lastViewPosition, _lastViewRotation, _lastViewFieldOfView );
			return;
		}

		var rotation = Controller.EyeAngles.ToRotation();
		var pivot = Controller.EyeTransform.Position
			+ rotation.Up * PivotOffset.z
			+ rotation.Right * (PivotOffset.y + _shoulder)
			+ rotation.Forward * PivotOffset.x;

		var position = pivot;

		if ( _distance > 0.01f )
		{
			var wanted = pivot - rotation.Forward * _distance;

			var tr = Scene.Trace.FromTo( pivot, wanted )
				.IgnoreGameObjectHierarchy( GameObject.Root )
				.Radius( CollisionRadius )
				.WithoutTags( CollisionIgnore )
				.Run();

			position = tr.EndPosition;
		}

		var fieldOfView = Preferences.FieldOfView;
		ApplyView( cam, ref view, position, rotation, fieldOfView );

		_lastViewPosition = position;
		_lastViewRotation = rotation;
		_lastViewFieldOfView = fieldOfView;
		_hasLastView = true;
	}

	static void ApplyView(
		CameraComponent cam,
		ref CameraView view,
		Vector3 position,
		Rotation rotation,
		float fieldOfView
	)
	{
		cam.WorldTransform = cam.WorldTransform
			.WithPosition( position )
			.WithRotation( rotation );

		view.Position = position;
		view.Rotation = rotation;
		view.FieldOfView = fieldOfView;
	}
}
