using Sandbox.Services;

namespace Hussrooms;

/// <summary>
/// Achievement stats.
/// </summary>
/// <remarks>
/// The awkward bit is that <see cref="Stats"/> credits whoever's machine calls it - it resolves
/// against the local Steam account - while almost everything worth counting in this game is
/// decided on the host. So each one takes a hop back to the owning client before it's recorded,
/// otherwise the host quietly collects everybody's kills.
/// </remarks>
public partial class HussPlayer
{
	/// <summary>Runners put down. Sum.</summary>
	public const string StatKills = "clarks_killed";

	/// <summary>Chasers knocked over with a thrown prop. Sum.</summary>
	public const string StatTripped = "chasers_tripped";

	/// <summary>Longest single life as a runner, in seconds. Max.</summary>
	public const string StatSurvival = "longest_survival";

	/// <summary>Reached a safe room on the last hit. Sum.</summary>
	public const string StatEscapes = "close_escapes";

	/// <summary>Taunts pulled off. Sum.</summary>
	public const string StatTaunts = "taunts";

	/// <summary>
	/// How long this life has lasted. Only read on the host, which is where death happens.
	/// </summary>
	TimeSince _aliveSince;

	/// <summary>
	/// Start the survival clock over. Called when we spawn, respawn, or switch sides.
	/// </summary>
	internal void RestartSurvivalTimer()
	{
		_aliveSince = 0;
	}

	/// <summary>
	/// Add to one of this player's counters. Host calls it, the owner records it.
	/// </summary>
	/// <remarks>
	/// It's a public RPC, which looks loose - but stats are submitted by the client under its
	/// own account no matter what, so a modified client could already write whatever it liked
	/// by calling Stats.Increment directly. This doesn't open anything that wasn't open.
	/// </remarks>
	[Rpc.Owner]
	public void AwardStat( string name, double amount )
	{
		// Bots are owned by the host and have no account of their own - crediting them would
		// pile every bot kill onto whoever happens to be hosting.
		if ( IsBot ) return;

		Stats.Increment( name, amount );
	}

	/// <summary>
	/// Submit a value for a stat that aggregates by Max or Min, rather than counting up.
	/// </summary>
	[Rpc.Owner]
	public void SubmitStat( string name, double value )
	{
		if ( IsBot ) return;

		Stats.SetValue( name, value );
	}

	/// <summary>
	/// Called on the host the moment a runner goes down for good. The backend keeps the
	/// highest value it's seen, so every life gets submitted and the best one wins.
	/// </summary>
	internal void SubmitSurvivalTime()
	{
		if ( !Networking.IsHost ) return;
		if ( IsBot ) return;

		SubmitStat( StatSurvival, _aliveSince );
	}

	/// <summary>
	/// Credit whoever threw the prop that just knocked this chaser over.
	/// </summary>
	/// <remarks>
	/// Props are network spawned by the client that made them, and grabbing one takes
	/// ownership - so the prop's owner is the last person to have held it, which is exactly
	/// who we want.
	/// </remarks>
	internal void CreditPropTrip( GameObject prop )
	{
		if ( !Networking.IsHost ) return;
		if ( !prop.IsValid() ) return;

		var thrower = prop.Network.Owner;
		if ( thrower is null ) return;

		var player = Scene.GetAllComponents<HussPlayer>()
			.FirstOrDefault( x => !x.IsBot && x.Network.Owner == thrower );

		if ( !player.IsValid() ) return;

		// Tripping over your own prop isn't an achievement. Compare the pawns, not their
		// connections - a bot is owned by the host, so comparing connections would deny the
		// host credit for every bot they knocked over.
		if ( player == this ) return;

		player.AwardStat( StatTripped, 1 );
	}
}
