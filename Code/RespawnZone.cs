namespace Hussrooms;

[Icon( "u_turn_left" ), Group( "Hussrooms" ), Title( "Respawn Zone" )]
public sealed class RespawnZone : Component, Component.ITriggerListener
{
	public void OnTriggerEnter( Collider other )
	{
		// Overlap events fire on every machine, but only the host decides where anyone ends up.
		if ( !Networking.IsHost ) return;

		if ( FindPlayer( other ) is not HussPlayer player ) return;

		player.RespawnAtSpawnPoint();
	}

	public void OnTriggerExit( Collider other )
	{
		// Overlap events fire on every machine, but only the host decides where anyone ends up.
		if ( !Networking.IsHost ) return;

		if ( FindPlayer( other ) is not HussPlayer player ) return;

		player.RespawnAtSpawnPoint();
	}

	static HussPlayer FindPlayer( Collider other )
	{
		if ( !other.IsValid() ) return null;

		return other.Components.Get<HussPlayer>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
	}
}
