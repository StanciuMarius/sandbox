/// <summary>
/// A point the player has dotted with the <see cref="MarkerTool"/>, so an agent can refer to it
/// later by name.
///
/// This is deliberately the same shape as a <see cref="ToolMode.SelectionPoint"/> - an object plus
/// a transform local to it - because that is what every tool in the game already consumes. A marker
/// is not a new concept bolted onto the toolgun; it is the toolgun's own selection, kept around and
/// given a name. That is what lets "weld A to B" turn into a real tool action with no translation.
///
/// Being parented in spirit to <see cref="Target"/> rather than to a world position means a marker
/// follows the thing it was placed on. Dot a prop, shove the prop across the map, and the marker is
/// still on the same corner of it.
/// </summary>
public sealed class AgentMarker
{
	/// <summary>
	/// Short name the player sees and the agent uses - "A", "B", "C". Letters rather than numbers
	/// because these get spoken out loud ("weld A to B") far more than they get computed with.
	/// </summary>
	public string Label { get; init; }

	/// <summary>What the marker is stuck to. The map itself counts, and is tagged "world".</summary>
	public GameObject Target { get; init; }

	/// <summary>Where on <see cref="Target"/> the marker sits, in that object's local space.</summary>
	public Transform LocalTransform { get; init; }

	/// <summary>Who placed it. Markers are personal - you only see and use your own.</summary>
	public Connection Owner { get; init; }

	/// <summary>Colour used to draw it, so several markers stay tellable apart.</summary>
	public Color Color { get; init; }

	/// <summary>False once the thing it was placed on has been destroyed.</summary>
	public bool IsValid() => Target.IsValid();

	/// <summary>True when this is stuck to the map rather than to a spawned object.</summary>
	public bool IsWorld => Target.IsValid() && Target.Tags.Has( "world" );

	public Vector3 WorldPosition => Target.IsValid()
		? Target.WorldTransform.PointToWorld( LocalTransform.Position )
		: Vector3.Zero;

	public Transform WorldTransform => Target.IsValid()
		? Target.WorldTransform.ToWorld( LocalTransform )
		: global::Transform.Zero;

	/// <summary>
	/// Hand this to any tool that wants a target. The whole point of the marker system.
	/// </summary>
	public ToolMode.SelectionPoint ToSelectionPoint() => new()
	{
		GameObject = Target,
		LocalTransform = LocalTransform
	};
}
