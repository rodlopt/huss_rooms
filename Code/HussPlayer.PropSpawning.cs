namespace Hussrooms;

public partial class HussPlayer
{
	[Property, Group( "Prop Spawner" )] public int MaxProps { get; set; } = 15;
	[Property, Group( "Prop Spawner" )] public float PropSpawnCooldown { get; set; } = 1.0f;

	[Property, Group( "Prop Spawner" )] public float SpawnRange { get; set; } = 40f;

	private List<GameObject> _spawnedProps = new();
	private TimeSince _timeSinceLastPropSpawn = 1.0f;

	public bool TrySpawnProp( GameObject prefab )
	{
		if ( !prefab.IsValid() ) return false;
		if ( IsProxy || IsDowned || InputLocked ) return false;

		if ( _timeSinceLastPropSpawn < PropSpawnCooldown )
		{
			Log.Info( $"Prop spawn on cooldown! Wait {PropSpawnCooldown - _timeSinceLastPropSpawn:F1}s" );
			return false;
		}

		_spawnedProps.RemoveAll( x => !x.IsValid() );
		if ( _spawnedProps.Count >= MaxProps )
		{
			Log.Warning( $"Prop limit reached! ({MaxProps} max)" );
			return false;
		}

		Vector3 spawnPos;
		Rotation spawnRot = Rotation.Identity;

		if ( Head.IsValid() )
		{
			var start = Head.WorldPosition;
			var forward = Head.WorldRotation.Forward;

			var tr = Scene.Trace.Ray( start, start + forward * SpawnRange )
				.WithoutTags( HussTags.Runner, HussTags.Chaser, "trigger" )
				.Run();

			spawnPos = tr.Hit ? tr.EndPosition : start + forward * SpawnRange;
			spawnRot = Rotation.From( 0, Head.WorldRotation.Yaw(), 0 );
		}
		else
		{
			spawnPos = WorldPosition + WorldRotation.Forward * 100f;
		}

		var spawned = prefab.Clone( spawnPos, spawnRot );
		spawned.NetworkSpawn();

		_spawnedProps.Add( spawned );
		_timeSinceLastPropSpawn = 0;
		return true;
	}
}
