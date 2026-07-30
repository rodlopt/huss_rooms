namespace Hussrooms;

[Icon( "directions_run" ), Group( "Hussrooms" ), Title( "Runner Spawn Point" )]
[EditorHandle( "materials/gizmo/spawnpoint.png" )]
public sealed class RunnerSpawnPoint : Component
{
	[Property] public Color Color { get; set; } = "#E3510D";

	protected override void DrawGizmos()
	{
		base.DrawGizmos();

		var spawnpointModel = Model.Load( "models/editor/spawnpoint.vmdl" );

		Gizmo.Hitbox.Model( spawnpointModel );
		Gizmo.Draw.Color = Color.WithAlpha( (Gizmo.IsHovered || Gizmo.IsSelected) ? 0.7f : 0.5f );
		var so = Gizmo.Draw.Model( spawnpointModel );
		if ( so is not null )
		{
			so.Flags.CastShadows = true;
		}
	}
}
