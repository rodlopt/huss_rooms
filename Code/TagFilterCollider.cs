namespace Hussrooms;

/// <summary>
/// Makes the colliders on this object only solid to things that don't carry
/// <see cref="PassThroughTag"/>. That's how the safe room works: runners walk straight
/// through the doorway, chasers hit a wall.
///
/// The actual filtering is done by the engine's collision matrix, which is the only place
/// it can be done at the contact level. This component owns the tag on our side of that
/// matrix, and shouts if the matching rule is missing from
/// ProjectSettings/Collision.config - because if it is, the barrier silently blocks
/// everybody and the safe room stops working with no obvious cause.
///
/// The default pair, already in the config, is (saferoom, runner) = Ignore.
/// </summary>
[Icon( "filter_alt" ), Group( "Hussrooms" ), Title( "Tag Filter Collider" )]
public sealed class TagFilterCollider : Component, Component.ExecuteInEditor
{
	/// <summary>
	/// The tag applied to this object. The collision matrix is keyed off it.
	/// </summary>
	[Property] public string BarrierTag { get; set; } = HussTags.SafeRoom;

	/// <summary>
	/// Anything carrying this tag passes through. Everything else is blocked.
	/// </summary>
	[Property] public string PassThroughTag { get; set; } = HussTags.Runner;

	protected override void OnEnabled()
	{
		Apply();
	}

	protected override void OnDisabled()
	{
		if ( !string.IsNullOrWhiteSpace( BarrierTag ) )
			GameObject.Tags.Remove( BarrierTag );
	}

	protected override void OnValidate()
	{
		Apply();
	}

	void Apply()
	{
		if ( string.IsNullOrWhiteSpace( BarrierTag ) ) return;
		if ( !GameObject.IsValid() ) return;

		GameObject.Tags.Add( BarrierTag );

		// OnValidate can fire before we're in a scene.
		if ( Scene is null || Scene.IsEditor ) return;

		WarnIfRuleMissing();
	}

	void WarnIfRuleMissing()
	{
		if ( string.IsNullOrWhiteSpace( PassThroughTag ) ) return;
		if ( Scene.PhysicsWorld is null ) return;

		var rule = Scene.PhysicsWorld.GetCollisionRule( BarrierTag, PassThroughTag );
		if ( rule == Sandbox.Physics.CollisionRules.Result.Ignore ) return;

		Log.Warning(
			$"{GameObject.Name}: collision rule ({BarrierTag}, {PassThroughTag}) is '{rule}', not 'Ignore'. " +
			$"'{PassThroughTag}' will be blocked by this barrier. Add the pair in ProjectSettings/Collision.config." );
	}
}
