using System;
using System.Linq;

/// <summary>
/// Turns the short strings an agent writes - "A", "aim", "4f2c1a9b" - into the
/// <see cref="ToolMode.SelectionPoint"/> every tool in the game consumes.
///
/// Prop protection is enforced here rather than left to each caller. A point that came from a trace
/// has already been past <see cref="IToolgunEvent"/>, but one built from a marker or an object id
/// has not, and skipping it would hand agents a way to tool objects the player isn't allowed to
/// touch. So every resolved point goes through the same check a trace does.
/// </summary>
internal static class AgentTargets
{
	/// <summary>
	/// Resolve a target string. Accepted forms, in the order they're tried:
	///
	///   aim                  where the player is looking right now
	///   pointer / latest     the most recently placed marker
	///   A, B, marker:A       a marker by label
	///   at:x,y,z             a world position, resolved onto whatever surface is there
	///   4f2c1a9b, object:id  an object by id, or a unique prefix of one
	/// </summary>
	/// <param name="tool">
	/// The tool that will act on the point. Used for its trace, so tool-specific ignore tags apply.
	/// </param>
	public static ToolMode.SelectionPoint Resolve( string spec, ToolMode tool, string argName = "target" )
	{
		if ( string.IsNullOrWhiteSpace( spec ) )
			throw new ArgumentException( $"'{argName}' is required. Give a marker label like 'A', or 'aim' - call list_markers or list_objects to see what's available." );

		spec = spec.Trim();

		var player = Player.FindLocalPlayer();
		if ( !player.IsValid() )
			throw new InvalidOperationException( "No local player" );

		var (prefix, rest) = Split( spec );

		// An existing marker wins outright, before any keyword is considered. Labels run A..Z then
		// AA, AB, ... so the 46th marker is called AT - which would otherwise be read as a position.
		var point = MarkerSystem.Current?.Find( spec ) is AgentMarker named
			? named.ToSelectionPoint()
			: prefix switch
			{
				"aim" => FromAim( tool, player ),
				"pointer" or "latest" => FromMarker( MarkerSystem.Current?.Latest( player.Network.Owner ), "the most recent marker" ),
				"marker" => FromMarker( MarkerSystem.Current?.Find( rest ), $"marker '{rest}'" ),
				"at" => FromPosition( rest, tool, player, argName ),
				"object" => FromObjectId( rest, argName ),
				_ => Guess( spec, tool, player, argName )
			};

		if ( !point.IsValid() )
			throw new ArgumentException( $"'{argName}': could not resolve '{spec}' to anything in the world." );

		RequireAccess( point.GameObject, player.Network.Owner, spec );

		return point;
	}

	/// <summary>
	/// Same as <see cref="Resolve"/>, but falls back to the player's aim when nothing was given.
	/// </summary>
	public static ToolMode.SelectionPoint ResolveOrAim( string spec, ToolMode tool, string argName = "target" )
		=> string.IsNullOrWhiteSpace( spec ) ? Resolve( "aim", tool, argName ) : Resolve( spec, tool, argName );

	/// <summary>
	/// An unprefixed word that wasn't a marker label. A bare "x,y,z" is a position; anything else is
	/// an object id.
	/// </summary>
	private static ToolMode.SelectionPoint Guess( string spec, ToolMode tool, Player player, string argName )
	{
		if ( spec.Contains( ',' ) )
			return FromPosition( spec, tool, player, argName );

		return FromObjectId( spec, argName );
	}

	private static ToolMode.SelectionPoint FromAim( ToolMode tool, Player player )
	{
		var eyes = player.EyeTransform;

		return tool.TraceFromRay( eyes.ForwardRay, 4096, player.GameObject );
	}

	private static ToolMode.SelectionPoint FromMarker( AgentMarker marker, string description )
	{
		if ( marker is null )
			throw new ArgumentException( $"No such marker - {description} doesn't exist. Ask the player to place one with the Marker tool, or call list_markers." );

		return marker.ToSelectionPoint();
	}

	/// <summary>
	/// A raw position isn't a selection on its own - a tool needs to know what it hit. So trace from
	/// the player out to the point and use whatever is there.
	/// </summary>
	private static ToolMode.SelectionPoint FromPosition( string text, ToolMode tool, Player player, string argName )
	{
		if ( !TryVec( text, out var position ) )
			throw new ArgumentException( $"'{argName}': '{text}' isn't a position. Expected 'x,y,z'." );

		var eyes = player.EyeTransform;
		var direction = position - eyes.Position;

		// via a Transform rather than building a Ray directly, matching how the tools do it
		var ray = new Transform( eyes.Position, Rotation.LookAt( direction ) ).ForwardRay;

		var point = tool.TraceFromRay( ray, direction.Length + 8f, player.GameObject );

		if ( !point.IsValid() )
			throw new ArgumentException( $"'{argName}': nothing is at {text} - it's open space, or not in the player's line of sight. Place a marker there instead." );

		return point;
	}

	/// <summary>
	/// Aim at an object's own origin, facing up.
	/// </summary>
	/// <remarks>
	/// Naming a whole object says nothing about which face was meant, but tools read the point's
	/// rotation as the surface normal - a wheel or thruster is mounted along it. Left as identity
	/// that normal would point down the object's local +X, so "put a wheel on this crate" would
	/// bolt one to its side. World up is the useful guess, and matches clicking the top of it.
	///
	/// A marker carries the real normal from the trace that placed it, which is why targets meant
	/// to be precise should be markers.
	/// </remarks>
	private static ToolMode.SelectionPoint FromObjectId( string id, string argName )
	{
		var go = FindObject( id, argName );

		return new ToolMode.SelectionPoint
		{
			GameObject = go,
			LocalTransform = new Transform( Vector3.Zero, go.WorldRotation.Inverse * Rotation.LookAt( Vector3.Up ) )
		};
	}

	/// <summary>
	/// Find a GameObject by full id, or by a unique prefix of one - full guids are painful to pass
	/// around, and agents mostly copy these straight out of a listing.
	/// </summary>
	public static GameObject FindObject( string id, string argName = "target" )
	{
		if ( string.IsNullOrWhiteSpace( id ) )
			throw new ArgumentException( $"'{argName}' is required" );

		id = id.Trim();

		if ( Guid.TryParse( id, out var guid ) && Game.ActiveScene.Directory.FindByGuid( guid ) is GameObject exact )
			return exact;

		var matches = Game.ActiveScene.GetAllObjects( true )
			.Where( x => x.Id.ToString().StartsWith( id, StringComparison.OrdinalIgnoreCase ) )
			.Take( 5 )
			.ToList();

		if ( matches.Count == 1 )
			return matches[0];

		if ( matches.Count > 1 )
			throw new ArgumentException( $"'{argName}': '{id}' matches more than one object. Use more of the id." );

		throw new ArgumentException( $"'{argName}': no object with id '{id}'. Call list_objects to see what's there." );
	}

	/// <summary>
	/// Run the same permission event a toolgun trace does, so prop protection covers markers and
	/// object ids too.
	/// </summary>
	private static void RequireAccess( GameObject go, Connection caller, string spec )
	{
		var selectEvent = new IToolgunEvent.SelectEvent { User = caller };

		go.Root.RunEvent<IToolgunEvent>( x => x.OnToolgunSelect( selectEvent ) );

		if ( selectEvent.Cancelled )
			throw new InvalidOperationException( $"Not allowed to use tools on '{spec}' - it belongs to another player and prop protection is on." );
	}

	private static (string prefix, string rest) Split( string spec )
	{
		var colon = spec.IndexOf( ':' );

		if ( colon <= 0 )
			return (spec.ToLowerInvariant(), spec);

		return (spec[..colon].ToLowerInvariant(), spec[(colon + 1)..].Trim());
	}

	public static bool TryVec( string s, out Vector3 v )
	{
		v = default;

		if ( string.IsNullOrWhiteSpace( s ) )
			return false;

		var parts = s.Split( ',', StringSplitOptions.TrimEntries );
		if ( parts.Length != 3 )
			return false;

		if ( !float.TryParse( parts[0], out var x ) ) return false;
		if ( !float.TryParse( parts[1], out var y ) ) return false;
		if ( !float.TryParse( parts[2], out var z ) ) return false;

		v = new Vector3( x, y, z );
		return true;
	}
}
