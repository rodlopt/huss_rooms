namespace Hussrooms;

/// <summary>
/// Tag names used by the game. These are physics tags, so they're mirrored in
/// ProjectSettings/Collision.config - if you rename one here, rename it there too.
/// </summary>
public static class HussTags
{
	/// <summary>
	/// On a runner's collider object. Runners pass straight through <see cref="SafeRoom"/> barriers.
	/// </summary>
	public const string Runner = "runner";

	/// <summary>
	/// On a chaser's collider object. Chasers are blocked by <see cref="SafeRoom"/> barriers.
	/// </summary>
	public const string Chaser = "chaser";

	/// <summary>
	/// On a safe room barrier. Collision.config maps (saferoom, runner) to Ignore.
	/// </summary>
	public const string SafeRoom = "saferoom";
}

/// <summary>
/// Which side a player is currently on.
/// </summary>
public enum HussTeam
{
	Runner,
	Chaser
}
