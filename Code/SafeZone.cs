namespace Hussrooms;

/// <summary>
/// A trigger volume that marks whoever is standing in it as safe. Chasers can't land a hit
/// on a runner inside one.
///
/// This is the bookkeeping half of a safe room. The half that physically keeps chasers out
/// is <see cref="TagFilterCollider"/>.
///
/// Put this on a GameObject with a collider that has IsTrigger set.
/// </summary>
[Icon( "shield" ), Group( "Hussrooms" ), Title( "Safe Zone" )]
public sealed class SafeZone : Component, Component.ITriggerListener
{
	public void OnTriggerEnter( Collider other )
	{
		// Overlap events fire on every machine, but only the host's answer counts -
		// HussPlayer.IsSafe is host authoritative.
		if ( !Networking.IsHost ) return;

		FindPlayer( other )?.SetInSafeZone( true );
	}

	public void OnTriggerExit( Collider other )
	{
		if ( !Networking.IsHost ) return;

		// Deliberately unconditional to mirror the enter above. If we filtered by team here,
		// someone who transformed while inside would leak a reference and never stop being safe.
		FindPlayer( other )?.SetInSafeZone( false );
	}

	/// <summary>
	/// The collider we get handed is the player's collider child, so walk up to the root.
	/// </summary>
	static HussPlayer FindPlayer( Collider other )
	{
		if ( !other.IsValid() ) return null;

		return other.Components.Get<HussPlayer>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
	}
}
