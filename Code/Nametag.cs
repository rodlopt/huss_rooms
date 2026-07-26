using Sandbox;
using Hussrooms;

/// <summary>
/// Puts the owning player's name above their head, in their team colour.
///
/// The name itself comes from <see cref="HussPlayer.DisplayName"/>, which the host fills in
/// from the owning connection and syncs out. Reading the connection locally wouldn't do:
/// every machine has to agree on what everyone else is called, and a client can't see the
/// tail end of a connection list it may not have been given.
///
/// Goes on a child object above the head, alongside a TextRenderer - the TextRenderer's own
/// billboard setting is what keeps the text turned towards the camera.
/// </summary>
[Icon( "badge" ), Group( "Hussrooms" ), Title( "Nametag" )]
public sealed class Nametag : Component
{
	[RequireComponent] public TextRenderer Text { get; set; }

	[Property, Group( "Colours" )] public Color RunnerColor { get; set; } = "White";
	[Property, Group( "Colours" )] public Color ChaserColor { get; set; } = "White";

	/// <summary>
	/// Past this the tag switches off. Stops distant names cluttering the screen.
	/// </summary>
	[Property, Group( "Visibility" )] public float MaxDistance { get; set; } = 1600.0f;

	/// <summary>
	/// Whether you see your own name. Roblox shows it, so this defaults on - it's hidden in
	/// first person regardless, where it would be sat on the lens.
	/// </summary>
	[Property, Group( "Visibility" )] public bool ShowOwnName { get; set; } = true;

	/// <summary>
	/// Shown before the host has told us who this is, or if the tag is used off a HussPlayer.
	/// </summary>
	[Property, Group( "Visibility" )] public string FallbackName { get; set; } = "Player";

	HussPlayer _player;

	// TextScope is a struct and its setter re-measures the text, so only write it on a change.
	string _appliedText;
	Color _appliedColor;

	protected override void OnAwake()
	{
		_player = GetComponentInParent<HussPlayer>( true );
	}

	protected override void OnUpdate()
	{
		if ( !Text.IsValid() ) return;

		var visible = ShouldShow();

		if ( Text.Enabled != visible )
			Text.Enabled = visible;

		if ( !visible ) return;

		var name = ResolveName();
		var color = _player.IsValid() && _player.IsChaser ? ChaserColor : RunnerColor;

		if ( name == _appliedText && color == _appliedColor ) return;

		var scope = Text.TextScope;
		scope.Text = name;
		scope.TextColor = color;
		Text.TextScope = scope;

		_appliedText = name;
		_appliedColor = color;
	}

	bool ShouldShow()
	{
		// While down the body is a ragdoll somewhere else - a tag floating over the parked
		// capsule would just be pointing at nothing.
		if ( _player.IsValid() && _player.IsDowned )
			return false;

		var isOurs = _player.IsValid() && !_player.IsProxy;

		if ( isOurs )
		{
			if ( !ShowOwnName ) return false;

			// First person puts our own head where the camera is.
			if ( _player.Camera.IsValid() && _player.Camera.IsFirstPerson )
				return false;
		}

		if ( MaxDistance > 0 && Scene.Camera.IsValid() )
		{
			if ( Scene.Camera.WorldPosition.Distance( WorldPosition ) > MaxDistance )
				return false;
		}

		return true;
	}

	string ResolveName()
	{
		if ( _player.IsValid() && !string.IsNullOrWhiteSpace( _player.DisplayName ) )
			return _player.DisplayName;

		// Either this isn't on a HussPlayer, or the host hasn't published a name yet.
		var owner = GameObject.Root.IsValid() ? GameObject.Root.Network.Owner : null;

		return string.IsNullOrWhiteSpace( owner?.DisplayName ) ? FallbackName : owner.DisplayName;
	}
}
