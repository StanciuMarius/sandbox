/// <summary>
/// Holds the player's markers and draws them.
///
/// Markers are client-local and are not GameObjects. That is deliberate: they would otherwise turn
/// up in every prop listing, get welded to by accident, count against limits and need cleaning up.
/// A marker is an annotation on the scene, not a thing in it.
///
/// Drawing happens here rather than in <see cref="MarkerTool"/> so markers stay visible after the
/// player switches to another tool - the usual flow is to dot a few points and then go do something
/// else while the agent works.
/// </summary>
public sealed class MarkerSystem : GameObjectSystem<MarkerSystem>, Component.ISceneStage
{
	/// <summary>
	/// Cycled through as markers are placed so adjacent ones are tellable apart at a glance.
	/// </summary>
	private static readonly Color[] Palette =
	{
		new( 0.31f, 0.64f, 1.00f, 1f ),  // blue
		new( 1.00f, 0.71f, 0.28f, 1f ),  // amber
		new( 0.36f, 0.86f, 0.48f, 1f ),  // green
		new( 1.00f, 0.42f, 0.42f, 1f ),  // red
		new( 0.77f, 0.55f, 1.00f, 1f ),  // violet
		new( 0.25f, 0.85f, 0.82f, 1f )   // teal
	};

	/// <summary>
	/// Placement order. Markers get the first free label, so removing B and placing again reuses B
	/// rather than climbing to Z - names stay short over a long session.
	/// </summary>
	private readonly List<AgentMarker> _markers = new();

	/// <summary>Size of the drawn crosshair, in world units.</summary>
	private const float CrossSize = 3f;

	public MarkerSystem( Scene scene ) : base( scene )
	{
	}

	public IReadOnlyList<AgentMarker> All => _markers;

	/// <summary>
	/// Markers belonging to a connection, in placement order.
	/// </summary>
	public List<AgentMarker> For( Connection owner )
	{
		Prune();
		return _markers.Where( x => x.Owner == owner ).ToList();
	}

	/// <summary>
	/// Look a marker up by label, case-insensitively. Null if there isn't one.
	/// </summary>
	public AgentMarker Find( string label )
	{
		if ( string.IsNullOrWhiteSpace( label ) )
			return null;

		Prune();

		label = label.Trim();

		return _markers.FirstOrDefault( x => string.Equals( x.Label, label, StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>
	/// The most recently placed marker, which is what an agent means by "the pointer".
	/// </summary>
	public AgentMarker Latest( Connection owner = null )
	{
		Prune();

		for ( int i = _markers.Count - 1; i >= 0; i-- )
		{
			if ( owner is null || _markers[i].Owner == owner )
				return _markers[i];
		}

		return null;
	}

	/// <summary>
	/// Drop a marker on a selection point, replacing any marker already at effectively the same
	/// spot so repeated clicks on one face don't pile up.
	/// </summary>
	public AgentMarker Place( ToolMode.SelectionPoint point, Connection owner )
	{
		if ( !point.IsValid() )
			return null;

		Prune();

		var marker = new AgentMarker
		{
			Label = NextFreeLabel(),
			Target = point.GameObject,
			LocalTransform = point.LocalTransform,
			Owner = owner,
			Color = Palette[_markers.Count % Palette.Length]
		};

		_markers.Add( marker );

		return marker;
	}

	/// <summary>
	/// Remove the marker nearest a world position, within <paramref name="maxDistance"/> units.
	/// Returns the one that went, or null if nothing was close enough.
	/// </summary>
	public AgentMarker RemoveNearest( Vector3 position, Connection owner, float maxDistance = 16f )
	{
		Prune();

		AgentMarker best = null;
		var bestDistance = maxDistance;

		foreach ( var marker in _markers )
		{
			if ( marker.Owner != owner ) continue;

			var distance = marker.WorldPosition.Distance( position );
			if ( distance > bestDistance ) continue;

			best = marker;
			bestDistance = distance;
		}

		if ( best is not null )
			_markers.Remove( best );

		return best;
	}

	/// <summary>Remove one marker by label. True if it was there.</summary>
	public bool Remove( string label )
	{
		var marker = Find( label );
		if ( marker is null ) return false;

		_markers.Remove( marker );
		return true;
	}

	/// <summary>Drop every marker belonging to a connection. Returns how many went.</summary>
	public int Clear( Connection owner )
	{
		var removed = _markers.RemoveAll( x => owner is null || x.Owner == owner );
		return removed;
	}

	/// <summary>
	/// Lowest unused label - A, B, ... Z, then AA, AB and so on for the rare session that needs
	/// more than 26 live markers at once.
	/// </summary>
	private string NextFreeLabel()
	{
		for ( int i = 0; i < 26 * 27; i++ )
		{
			var label = LabelFor( i );
			if ( _markers.Any( x => x.Label == label ) ) continue;

			return label;
		}

		return LabelFor( _markers.Count );
	}

	private static string LabelFor( int index )
	{
		if ( index < 26 )
			return ((char)('A' + index)).ToString();

		var first = (char)('A' + (index / 26) - 1);
		var second = (char)('A' + (index % 26));

		return $"{first}{second}";
	}

	/// <summary>
	/// Forget markers whose object has been destroyed - cleaned up, removed, or blown apart.
	/// </summary>
	private void Prune() => _markers.RemoveAll( x => !x.IsValid() );

	void Component.ISceneStage.Start()
	{
	}

	/// <summary>
	/// Draw every marker each frame. A three-axis cross rather than a blob, because these are used
	/// to line welds up and the player needs to see the exact point, not roughly where it is.
	/// </summary>
	void Component.ISceneStage.End()
	{
		Prune();

		if ( _markers.Count == 0 )
			return;

		foreach ( var marker in _markers )
		{
			if ( marker.Owner != Connection.Local )
				continue;

			var position = marker.WorldPosition;
			var color = marker.Color;

			Scene.DebugOverlay.Line( position - Vector3.Left * CrossSize, position + Vector3.Left * CrossSize, color );
			Scene.DebugOverlay.Line( position - Vector3.Forward * CrossSize, position + Vector3.Forward * CrossSize, color );
			Scene.DebugOverlay.Line( position - Vector3.Up * CrossSize, position + Vector3.Up * CrossSize, color );

			Scene.DebugOverlay.Text( position + Vector3.Up * (CrossSize + 2f), marker.Label, color: color, duration: 0f );
		}
	}
}

