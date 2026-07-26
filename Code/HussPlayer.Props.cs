using System;
using System.Collections.Generic;
using Sandbox;

namespace Hussrooms;

public partial class HussPlayer
{
	[Property, Group( "Prop Spawner" )] public int MaxProps { get; set; } = 15;
	[Property, Group( "Prop Spawner" )] public float PropSpawnCooldown { get; set; } = 1.0f;
	[Property, Group( "Prop Spawner" )] public float SpawnRange { get; set; } = 40f;

	[Property, Group( "Prop Interaction" )]
	public float GrabRange { get; set; } = 200f;

	[Property, Group( "Prop Interaction" )]
	public float HoldDistance { get; set; } = 80f;

	[Property, Group( "Prop Interaction" )]
	public float ThrowForce { get; set; } = 600f;

	private List<GameObject> _spawnedProps = new();
	private TimeSince _timeSinceLastPropSpawn = 1.0f;

	private GameObject _heldProp;
	private Rigidbody _heldRigidbody;

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

	private void UpdatePropInteraction()
	{
		if ( IsProxy || IsDowned || InputLocked )
		{
			DropHeldProp();
			return;
		}

		if ( Input.Pressed( "Reload" ) && _heldProp.IsValid() )
		{
			ThrowHeldProp();
			return;
		}

		if ( Input.Down( "attack1" ) )
		{
			if ( !_heldProp.IsValid() )
			{
				TryGrabProp();
			}
			else
			{
				UpdateHeldPropTransform();
			}
		}
		else if ( _heldProp.IsValid() )
		{
			DropHeldProp();
		}
	}

	private void TryGrabProp()
	{
		var cam = Scene.Camera;
		if ( !cam.IsValid() ) return;

		var eyePos = cam.WorldPosition;
		var eyeForward = cam.WorldRotation.Forward;

		var tr = Scene.Trace.Ray( eyePos, eyePos + eyeForward * GrabRange )
			.Radius( 12f )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.WithoutTags( HussTags.Runner, HussTags.Chaser, "trigger", "player" )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() ) return;

		var rb = tr.GameObject.Components.GetInParent<Rigidbody>()
		         ?? tr.GameObject.Root.Components.Get<Rigidbody>();

		if ( !rb.IsValid() ) return;

		_heldProp = rb.GameObject;
		_heldRigidbody = rb;

		_heldProp.Network.TakeOwnership();

		_heldRigidbody.Gravity = false;
	}

	private void UpdateHeldPropTransform()
	{
		if ( !_heldProp.IsValid() || !_heldRigidbody.IsValid() )
		{
			DropHeldProp();
			return;
		}

		var cam = Scene.Camera;
		if ( !cam.IsValid() )
		{
			DropHeldProp();
			return;
		}

		var eyePos = cam.WorldPosition;
		var eyeRot = cam.WorldRotation;

		var targetPos = eyePos + eyeRot.Forward * HoldDistance;
		var distance = targetPos - _heldRigidbody.WorldPosition;

		if ( distance.Length > GrabRange * 1.5f )
		{
			DropHeldProp();
			return;
		}

		_heldRigidbody.Velocity = distance * 25f;
		_heldRigidbody.AngularVelocity = Vector3.Zero;
	}

	private void ThrowHeldProp()
	{
		if ( !_heldProp.IsValid() || !_heldRigidbody.IsValid() ) return;

		Input.ReleaseAction( "attack1" );

		var cam = Scene.Camera;
		var throwDir = cam.IsValid() ? cam.WorldRotation.Forward : WorldRotation.Forward;

		_heldRigidbody.Gravity = true;
		_heldRigidbody.Velocity = throwDir * (ThrowForce * 2.0f);

		_heldProp = null;
		_heldRigidbody = null;
	}

	private void DropHeldProp()
	{
		if ( _heldRigidbody.IsValid() )
		{
			_heldRigidbody.Gravity = true;
		}

		_heldProp = null;
		_heldRigidbody = null;
	}
}
