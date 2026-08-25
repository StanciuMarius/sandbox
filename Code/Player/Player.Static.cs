public sealed partial class Player : Component, Component.IDamageable, PlayerController.IEvents, Global.ISaveEvents, IKillSource
{
	private static Player LocalPlayer { get; set; }

	/// <summary>
	/// The player belonging to this client, or null in the menu.
	/// </summary>
	/// <remarks>
	/// <see cref="LocalPlayer"/> is a cache filled in from OnEnabled, and a static field survives a
	/// hotload while the instance it points at does not - so in the editor it can end up stale or
	/// null while the player is alive and well in the scene. Fall back to looking it up, and
	/// re-latch, so callers get an answer either way rather than a confusing "no local player".
	/// </remarks>
	public static Player FindLocalPlayer()
	{
		if ( LocalPlayer.IsValid() )
			return LocalPlayer;

		LocalPlayer = FindForConnection( Connection.Local );

		return LocalPlayer;
	}
	public static T FindLocalWeapon<T>() where T : BaseSandboxWeapon => FindLocalPlayer()?.GetComponentInChildren<T>( true );
	public static T FindLocalToolMode<T>() where T : ToolMode => FindLocalPlayer()?.GetComponentInChildren<T>( true );

	/// <summary>
	/// Find a player for this connection
	/// </summary>
	public static Player FindForConnection( Connection c )
	{
		return Game.ActiveScene.GetAll<Player>().FirstOrDefault( x => x.Network.Owner == c && !x.IsAgent );
	}

	/// <summary>
	/// Get player from a connection id
	/// </summary>
	public static Player For( Guid playerId )
	{
		return Game.ActiveScene.GetAll<Player>().FirstOrDefault( x => x.Network.Owner?.Id == playerId && !x.IsAgent );
	}
}
