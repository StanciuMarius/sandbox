/// <summary>
/// Drops named dots on things, for an agent to aim at later.
///
/// The problem this solves: a tool traces from the player's eyes, so anything an agent drives is
/// aimed by wherever the player happens to be looking at that instant. That means holding the
/// camera still and hoping - which is a miserable way to build, and impossible for anything that
/// needs two points on opposite sides of a contraption.
///
/// So the player marks the points up front, at their own pace, and then says what to do with them.
/// Dot two corners, walk away, and tell the agent to weld A to B.
/// </summary>
[Icon( "📍" )]
[Title( "#tool.name.marker" )]
[ClassName( "marker" )]
[Group( "#tool.group.tools" )]
public sealed class MarkerTool : ToolMode
{
	public override bool UseSnapGrid => true;

	/// <summary>Markers go on constraints and thrusters as happily as on props.</summary>
	public override IEnumerable<string> TraceIgnoreTags => ["player"];

	public override string Description => "#tool.hint.marker.description";

	protected override void RegisterActions()
	{
		RegisterAction( ToolInput.Primary, () => "#tool.hint.marker.place", OnPlace );
		RegisterAction( ToolInput.Secondary, () => "#tool.hint.marker.remove", OnRemove );
		RegisterAction( ToolInput.Reload, () => "#tool.hint.marker.clear", OnClear );
	}

	/// <summary>
	/// Hold E while placing to snap to the grid corner, the same gesture the weld tool uses. Worth
	/// having here because a marker is most often placed precisely so a weld can be.
	/// </summary>
	private ToolMode.SelectionPoint Snapped( ToolMode.SelectionPoint select )
	{
		if ( !select.IsValid() || SnapGrid is null || !Input.Down( "use" ) )
			return select;

		var snapPosition = SnapGrid.LastSnapWorldPos;
		var local = select.LocalTransform;
		local.Position = select.GameObject.WorldTransform.ToLocal( new Transform( snapPosition ) ).Position;
		select.LocalTransform = local;

		return select;
	}

	private void OnPlace()
	{
		var select = Snapped( TraceSelect() );
		if ( !select.IsValid() ) return;

		var marker = MarkerSystem.Current?.Place( select, Player?.Network.Owner );
		if ( marker is null ) return;

		ShootEffects( select );
	}

	private void OnRemove()
	{
		var select = TraceSelect();
		if ( !select.IsValid() ) return;

		var removed = MarkerSystem.Current?.RemoveNearest( select.WorldPosition(), Player?.Network.Owner );
		if ( removed is null ) return;

		ShootEffects( select );
	}

	private void OnClear()
	{
		MarkerSystem.Current?.Clear( Player?.Network.Owner );
	}

	/// <summary>
	/// Draw a ghost cross at the point the player is about to mark, so placing one is aimed rather
	/// than guessed.
	/// </summary>
	public override void OnControl()
	{
		base.OnControl();

		var select = Snapped( TraceSelect() );
		IsValidState = select.IsValid();

		if ( !IsValidState ) return;

		var position = select.WorldPosition();
		var color = Color.White.WithAlpha( 0.5f );

		DebugOverlay.Line( position - Vector3.Left * 2f, position + Vector3.Left * 2f, color );
		DebugOverlay.Line( position - Vector3.Forward * 2f, position + Vector3.Forward * 2f, color );
		DebugOverlay.Line( position - Vector3.Up * 2f, position + Vector3.Up * 2f, color );
	}
}
