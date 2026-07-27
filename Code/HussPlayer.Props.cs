using System;
using System.Collections.Generic;
using Sandbox;

namespace Hussrooms;

public partial class HussPlayer
{
	[Property, Group( "Prop Spawner" )] public int MaxProps { get; set; } = 15;
	[Property, Group( "Prop Spawner" )] public float PropSpawnCooldown { get; set; } = 1.0f;
	[Property, Group( "Prop Spawner" )] public float DeleteCooldown { get; set; } = 1.0f;
	[Property, Group( "Prop Spawner" )] public float SpawnRange { get; set; } = 40f;

	[Property, Group( "Prop Interaction" )]
	public float GrabRange { get; set; } = 200f;

	[Property, Group( "Prop Interaction" )]
	public float HoldDistance { get; set; } = 80f;

	[Property, Group( "Prop Interaction" )]
	public float ThrowForce { get; set; } = 600f;

	public List<GameObject> SpawnedProps = new();
	private TimeSince _timeSinceLastPropSpawn = 1.0f;
	private TimeSince _timeSinceDelete = 1.0f;

	private GameObject _heldProp;
	private Rigidbody _heldRigidbody;

	public bool DeleteLastProp()
	{
		if ( IsProxy ) return false;

		if ( _timeSinceDelete < DeleteCooldown )
		{
			Log.Info( "Can't delete yet" );
			return false;
		}

		SpawnedProps.RemoveAll( x => !x.IsValid() );

		if ( SpawnedProps.Count == 0 )
			return false;

		var lastPropIndex = SpawnedProps.Count - 1;
		var propToDestroy = SpawnedProps[lastPropIndex];

		if ( _heldProp == propToDestroy )
		{
			DropHeldProp();
		}

		if ( propToDestroy.IsValid() )
		{
			propToDestroy.Destroy();
		}

		_timeSinceDelete = 0;

		SpawnedProps.RemoveAt( lastPropIndex );
		return true;
	}

	public bool TrySpawnProp( GameObject prefab )
	{
		if ( !prefab.IsValid() ) return false;
		if ( IsProxy || IsDowned || InputLocked ) return false;

		if ( _timeSinceLastPropSpawn < PropSpawnCooldown )
		{
			Log.Info( $"Prop spawn on cooldown! Wait {PropSpawnCooldown - _timeSinceLastPropSpawn:F1}s" );
			return false;
		}

		SpawnedProps.RemoveAll( x => !x.IsValid() );
		if ( SpawnedProps.Count >= MaxProps )
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

		SpawnedProps.Add( spawned );
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

		var interactionOrigin = GetPropInteractionOrigin();
		var cameraPosition = cam.WorldPosition;
		var cameraForward = cam.WorldRotation.Forward;
		var cameraDistance = cameraPosition.Distance( interactionOrigin );

		var tr = Scene.Trace.Ray(
				cameraPosition,
				cameraPosition + cameraForward * (cameraDistance + GrabRange) )
			.Radius( 12f )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.WithoutTags( HussTags.Runner, HussTags.Chaser, "trigger", "player" )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() ) return;

		var rb = tr.GameObject.Components.GetInParent<Rigidbody>()
		         ?? tr.GameObject.Root.Components.Get<Rigidbody>();

		if ( !rb.IsValid() ) return;
		if ( tr.EndPosition.Distance( interactionOrigin ) > GrabRange ) return;

		var reachTrace = Scene.Trace.Ray( interactionOrigin, tr.EndPosition )
			.Radius( 8f )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.WithoutTags( HussTags.Runner, HussTags.Chaser, "trigger", "player" )
			.Run();

		if ( reachTrace.Hit && reachTrace.GameObject.Root != rb.GameObject.Root ) return;

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

		var interactionOrigin = GetPropInteractionOrigin();

		if ( _heldRigidbody.WorldPosition.Distance( interactionOrigin ) > GrabRange * 1.5f )
		{
			DropHeldProp();
			return;
		}

		var aimDirection = GetPropAimDirection( interactionOrigin );
		var wantedPosition = interactionOrigin + aimDirection * HoldDistance;

		var holdTrace = Scene.Trace.Ray( interactionOrigin, wantedPosition )
			.Radius( 12f )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.IgnoreGameObjectHierarchy( _heldProp.Root )
			.WithoutTags( HussTags.Runner, HussTags.Chaser, "trigger", "player" )
			.Run();

		var distance = holdTrace.EndPosition - _heldRigidbody.WorldPosition;
		_heldRigidbody.Velocity = distance * 25f;
		_heldRigidbody.AngularVelocity = Vector3.Zero;
	}

	private void ThrowHeldProp()
	{
		if ( !_heldProp.IsValid() || !_heldRigidbody.IsValid() ) return;

		Input.ReleaseAction( "attack1" );

		var throwDir = GetPropAimDirection( GetPropInteractionOrigin() );

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

	private Vector3 GetPropInteractionOrigin()
	{
		if ( Head.IsValid() )
			return Head.WorldPosition;

		if ( Controller.IsValid() )
			return Controller.EyeTransform.Position;

		return WorldPosition + Vector3.Up * 64.0f;
	}

	private Vector3 GetPropAimDirection( Vector3 interactionOrigin )
	{
		var cam = Scene.Camera;
		if ( !cam.IsValid() )
			return WorldRotation.Forward;

		var cameraPosition = cam.WorldPosition;
		var projectionDistance = cameraPosition.Distance( interactionOrigin )
			+ MathF.Max( GrabRange, HoldDistance );
		var aimPoint = cameraPosition + cam.WorldRotation.Forward * projectionDistance;

		return (aimPoint - interactionOrigin).Normal;
	}
}
