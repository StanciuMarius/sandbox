public sealed partial class GameManager
{
	/// <summary>
	/// The lobby this game hosts when it starts up.
	/// </summary>
	private static Sandbox.Network.LobbyConfig DefaultLobby => new()
	{
		Privacy = Sandbox.Network.LobbyPrivacy.Public,
		MaxPlayers = 32,
		Name = "Sandbox",
		DestroyWhenHostLeaves = true
	};

	/// <summary>
	/// Whether to create a lobby automatically when the game starts.
	/// Turn this off to start idle so you can <c>join</c> someone else's session instead —
	/// the editor always hosts otherwise, which is no good when you want to be a client.
	/// </summary>
	[ConVar( "sb.autohost", ConVarFlags.Saved, Help = "Host a lobby automatically when the game starts." )]
	public static bool AutoHost { get; set; } = true;

	/// <summary>
	/// Create the lobby we host. Does nothing if we're already in a session.
	/// </summary>
	internal static void HostLobby()
	{
		if ( Networking.IsActive ) return;

		Networking.CreateLobby( DefaultLobby );
	}

	/// <summary>
	/// Start hosting a lobby. Only useful if you started with sb.autohost off.
	/// </summary>
	[ConCmd( "hostgame", Help = "Start hosting a lobby. Only useful if you started with sb.autohost off." )]
	public static void HostCommand()
	{
		if ( Networking.IsActive )
		{
			Log.Warning( "Already in a session — run 'leave' first." );
			return;
		}

		HostLobby();
		Log.Info( "Hosting a lobby." );
	}

	/// <summary>
	/// Join a session, leaving the current one first. Accepts a lobby id (a bare number,
	/// from <c>gameservers</c>) or a network address like <c>1.2.3.4:27015</c>. With no
	/// argument, picks the best available lobby for this game.
	/// </summary>
	[ConCmd( "join", Help = "Join a session by lobby id or address. No argument picks the best lobby." )]
	public static void JoinCommand( string target = null )
	{
		if ( Networking.IsActive )
		{
			Log.Info( "Leaving the current session first." );
			Networking.Disconnect();
		}

		target = target?.Trim();

		if ( string.IsNullOrWhiteSpace( target ) )
		{
			Log.Info( "Looking for a lobby to join..." );
			Networking.JoinBestLobby( Game.Ident );
			return;
		}

		// A bare number is a lobby id. Anything else is a network address, which
		// Connect resolves as a host and defaults to port 27015.
		if ( ulong.TryParse( target, out var lobbyId ) )
		{
			Log.Info( $"Joining lobby {lobbyId}..." );
			Networking.Connect( lobbyId );
			return;
		}

		Log.Info( $"Connecting to address {target}..." );
		Networking.Connect( target );
	}

	/// <summary>
	/// Print this session's join code, to share with someone who wants to join.
	/// </summary>
	[ConCmd( "sessioninfo", Help = "Print this session's join code, to share with someone who wants to join." )]
	public static void SessionInfoCommand()
	{
		if ( !Networking.IsActive )
		{
			Log.Warning( "Not in a session - nothing to share." );
			return;
		}

		var host = Connection.Host?.SteamId;

		Log.Info( $"Server   : {Networking.ServerName} ({Connection.All.Count()}/{Networking.MaxPlayers})" );
		Log.Info( $"You are  : {(Networking.IsHost ? "the host" : "a client")}" );
		Log.Info( $"Map      : {Networking.MapName}" );
		Log.Info( $"Join code: {host}" );
		Log.Info( $"Others join with:  join {host}" );
	}

	/// <summary>
	/// Leave the current session.
	/// </summary>
	[ConCmd( "leave", Help = "Leave the current session." )]
	public static void LeaveCommand()
	{
		if ( !Networking.IsActive )
		{
			Log.Warning( "Not in a session." );
			return;
		}

		Networking.Disconnect();
		Log.Info( "Left the session." );
	}

	/// <summary>
	/// List the servers running this game. Note: only dedicated servers show up here —
	/// peer-to-peer lobbies (an editor hitting Play) are not discoverable this way.
	/// </summary>
	[ConCmd( "gameservers", Help = "List joinable dedicated servers running this game." )]
	public static async void ServersCommand()
	{
		var lobbies = await Networking.QueryLobbies( default );

		if ( lobbies is null || lobbies.Count == 0 )
		{
			Log.Info( "No servers found." );
			return;
		}

		Log.Info( $"{lobbies.Count} server(s):" );

		foreach ( var lobby in lobbies )
		{
			var map = string.IsNullOrEmpty( lobby.Map ) ? "?" : lobby.Map;
			Log.Info( $"  {lobby.LobbyId}  {lobby.Members}/{lobby.MaxMembers}  {lobby.Ping}ms  [{map}]  {lobby.Name}" );
		}

		Log.Info( "Join one with:  join <id>" );
	}
}
