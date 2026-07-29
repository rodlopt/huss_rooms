using Sandbox.Audio;

namespace Hussrooms;

/// <summary>
/// Replaces PlayerController's footstep sounds with sets you pick, instead of whatever the
/// surface under your feet happens to define.
///
/// The built-in version reads <c>GroundSurface.SoundCollection.FootLeft/FootRight</c> and
/// gives up entirely if the surface has no sounds - which is why footsteps go silent on a lot
/// of map geometry. This listens to the same animation event and plays your sounds regardless
/// of what's underfoot.
///
/// Clark and Captain Clark get their own set, chosen from <see cref="HussPlayer.Team"/> - the
/// same thing that picks the model - so the footsteps swap the instant somebody transforms.
///
/// Put it on the player root, next to PlayerController.
/// </summary>
[Icon( "do_not_step" ), Group( "Hussrooms" ), Title( "Footstep Sounds" )]
public sealed class FootstepSounds : Component, PlayerController.IEvents
{
	/// <summary>
	/// One character's footsteps. Captain Clark wants something heavier and lower than Clark,
	/// which is what the volume and pitch here are for.
	/// </summary>
	public class FootstepSet
	{
		/// <summary>Picked from at random for every step.</summary>
		public List<SoundEvent> Steps { get; set; } = new();

		/// <summary>Left foot only, if you want a pair that alternates. Empty uses Steps.</summary>
		public List<SoundEvent> LeftFoot { get; set; } = new();

		/// <summary>Right foot only. Empty uses Steps.</summary>
		public List<SoundEvent> RightFoot { get; set; } = new();

		/// <summary>Played on touching down after a fall. Empty plays a step instead.</summary>
		public List<SoundEvent> Landing { get; set; } = new();

		public float Volume { get; set; } = 1.0f;
		public float MinPitch { get; set; } = 0.95f;
		public float MaxPitch { get; set; } = 1.05f;

		public bool HasSounds => Steps.Count > 0 || LeftFoot.Count > 0 || RightFoot.Count > 0;

		public SoundEvent Pick( int footId )
		{
			var list = footId == 0 ? LeftFoot : RightFoot;

			if ( list is null || list.Count == 0 )
				list = Steps;

			if ( list is null || list.Count == 0 )
				return null;

			return Random.Shared.FromList( list );
		}
	}

	[RequireComponent] public PlayerController Controller { get; set; }

	[Property, InlineEditor, Group( "Clark" ), Title( "Runner Footsteps" )]
	public FootstepSet Runner { get; set; } = new();

	[Property, InlineEditor, Group( "Captain Clark" ), Title( "Chaser Footsteps" )]
	public FootstepSet Chaser { get; set; } = new();

	/// <summary>
	/// Turn the controller's own surface-driven footsteps off while this is running, so you
	/// don't get both.
	/// </summary>
	/// <remarks>
	/// Ignored when neither set has any sounds - attaching an empty component shouldn't leave
	/// the player walking around in silence.
	/// </remarks>
	[Property, Group( "Sounds" )] public bool SilenceSurfaceSounds { get; set; } = true;

	[Property, Group( "Mix" )] public MixerHandle Mixer { get; set; }

	/// <summary>Shortest gap between two steps, to stop a fast run machine-gunning.</summary>
	[Property, Group( "Timing" )] public float MinInterval { get; set; } = 0.2f;

	/// <summary>Speed at which steps are at full volume. Slower is proportionally quieter.</summary>
	[Property, Group( "Timing" )] public float FullVolumeSpeed { get; set; } = 400.0f;

	/// <summary>Below this the step isn't worth playing at all.</summary>
	[Property, Group( "Timing" )] public float MinVolume { get; set; } = 0.1f;

	/// <summary>
	/// Whichever set matches the side we're currently on. Falls back to the runner set if this
	/// isn't a HussPlayer at all.
	/// </summary>
	FootstepSet ActiveSet => _player.IsValid() && _player.IsChaser ? Chaser : Runner;

	bool HasAnySounds => Runner.HasSounds || Chaser.HasSounds;

	HussPlayer _player;
	SkinnedModelRenderer _subscribed;
	bool _defaultWasEnabled;
	TimeSince _timeSinceStep;

	// Measured rather than read off the rigidbody, so remote players - whose bodies are driven
	// by network interpolation - get the same volume we do.
	Vector3 _lastPosition;
	float _speed;

	protected override void OnAwake()
	{
		_player = GetComponent<HussPlayer>( true );
	}

	protected override void OnEnabled()
	{
		_lastPosition = WorldPosition;

		if ( Controller.IsValid() )
		{
			_defaultWasEnabled = Controller.EnableFootstepSounds;

			// Decided once from whether the component is configured at all, rather than from
			// the active set - otherwise transforming would flip the engine's footsteps back
			// on for anyone who only filled in one side.
			if ( SilenceSurfaceSounds && HasAnySounds )
				Controller.EnableFootstepSounds = false;
		}
	}

	protected override void OnDisabled()
	{
		Unsubscribe();

		if ( Controller.IsValid() )
			Controller.EnableFootstepSounds = _defaultWasEnabled;
	}

	protected override void OnDestroy()
	{
		Unsubscribe();
	}

	protected override void OnUpdate()
	{
		var delta = (WorldPosition - _lastPosition).WithZ( 0 );
		_lastPosition = WorldPosition;

		// Ignore respawn teleports.
		if ( Time.Delta > 0 && delta.Length < 400.0f )
			_speed = delta.Length / Time.Delta;

		// The renderer can be swapped out from under us when a player changes team, so keep
		// the subscription pointed at whatever is current.
		var renderer = Controller.IsValid() ? Controller.Renderer : null;
		if ( renderer == _subscribed ) return;

		Unsubscribe();

		_subscribed = renderer;

		if ( _subscribed.IsValid() )
			_subscribed.OnFootstepEvent += OnFootstep;
	}

	void Unsubscribe()
	{
		if ( _subscribed.IsValid() )
			_subscribed.OnFootstepEvent -= OnFootstep;

		_subscribed = null;
	}

	/// <summary>
	/// Fires from the animation, so it already happens on every machine for every player -
	/// no networking needed here.
	/// </summary>
	void OnFootstep( SceneModel.FootstepEvent e )
	{
		var set = ActiveSet;
		if ( !set.HasSounds ) return;

		if ( !Controller.IsValid() || !Controller.IsOnGround ) return;
		if ( _timeSinceStep < MinInterval ) return;

		var volume = e.Volume * _speed.Remap( 0, FullVolumeSpeed, 0, 1, true );
		if ( volume <= MinVolume ) return;

		_timeSinceStep = 0;

		Play( set, set.Pick( e.FootId ), e.Transform.Position, volume );
	}

	void PlayerController.IEvents.OnLanded( float distance, Vector3 impactVelocity )
	{
		var set = ActiveSet;
		if ( !set.HasSounds ) return;

		var sound = set.Landing.Count > 0
			? Random.Shared.FromList( set.Landing )
			: set.Pick( 0 );

		// Louder the further you dropped, same idea as the engine's landing thump.
		Play( set, sound, WorldPosition, distance.Remap( 0, 200, 0.4f, 1.0f, true ) );

		_timeSinceStep = 0;
	}

	void Play( FootstepSet set, SoundEvent sound, Vector3 position, float volume )
	{
		if ( sound is null ) return;

		var handle = Sound.Play( sound, position );
		if ( !handle.IsValid() ) return;

		// Anchored where the foot landed rather than following the player around.
		handle.FollowParent = false;
		handle.TargetMixer = Mixer.GetOrDefault();
		handle.Volume *= volume * set.Volume;
		handle.Pitch *= Game.Random.Float( set.MinPitch, set.MaxPitch );
	}
}
