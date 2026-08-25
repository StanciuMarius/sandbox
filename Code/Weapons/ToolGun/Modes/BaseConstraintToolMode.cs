public abstract class BaseConstraintToolMode : ToolMode
{
	protected SelectionPoint Point1;
	protected SelectionPoint Point2;
	protected int Stage = 0;

	public virtual bool CanConstraintToSelf => false;
	public override bool UseSnapGrid => true;

	protected override void OnDisabled()
	{
		base.OnDisabled();
		Stage = 0;
		Point1 = default;
		Point2 = default;
	}

	public override void OnControl()
	{
		base.OnControl();

		var select = TraceSelect();

		if ( Input.Pressed( "attack2" ) )
		{
			// Some constraint tools can one-shot with secondary, like sliders.
			if ( Stage == 0 && select.IsValid() && GetSecondaryPoint( select ) is SelectionPoint point2 )
			{
				if ( !UpdateValidity( select, point2 ) )
					return;

				if ( !FireToolAction( ToolInput.Secondary ) )
					return;

				Point1 = select;
				Point2 = point2;

				Create( Point1, Point2 );
				ShootEffects( select );

				FirePostToolAction( ToolInput.Secondary );

				return;
			}

			Stage = 0;
			IsValidState = false;
			return;
		}

		if ( !select.IsValid() )
			return;

		if ( Input.Pressed( "reload" ) )
		{
			if ( !FireToolAction( ToolInput.Reload ) )
				return;

			var go = select.GameObject.Network.RootGameObject ?? select.GameObject;
			RemoveConstraints( go );
			ShootEffects( select );

			FirePostToolAction( ToolInput.Reload );
		}

		IsValidState = true;

		if ( Stage == 0 )
		{
			IsValidState = UpdateValidity( select );
		}

		if ( Stage == 1 )
		{
			IsValidState = UpdateValidity( Point1, select );
		}

		if ( !IsValidState ) return;

		if ( Input.Pressed( "attack1" ) )
		{
			if ( Stage == 0 )
			{
				Point1 = select;
				Stage++;
				ShootEffects( select );
				return;
			}

			if ( Stage == 1 )
			{
				if ( !FireToolAction( ToolInput.Primary ) )
				{
					Stage = 0;
					return;
				}

				Point2 = select;

				Create( Point1, Point2 );
				ShootEffects( select );

				FirePostToolAction( ToolInput.Primary );
			}

			Stage = 0;
		}


	}

	bool UpdateValidity( SelectionPoint point1, SelectionPoint point2 )
	{
		if ( !point1.GameObject.IsValid() ) return false;
		if ( !point2.GameObject.IsValid() ) return false;

		if ( !CanConstraintToSelf )
		{
			if ( point1.GameObject == point2.GameObject )
			{
				return false;
			}
		}

		return true;
	}

	bool UpdateValidity( SelectionPoint point1 )
	{
		if ( !point1.GameObject.IsValid() ) return false;

		return true;
	}

	[Rpc.Host( NetFlags.OwnerOnly )]
	private void Create( SelectionPoint point1, SelectionPoint point2 )
	{
		if ( !UpdateValidity( point1, point2 ) )
		{
			Log.Warning( "Tried to create invalid constraint" );
			return;
		}

		CreateConstraint( point1, point2 );
		CheckContraptionStats( point1.GameObject );
	}

	[Rpc.Host( NetFlags.OwnerOnly )]
	private void RemoveConstraints( GameObject go )
	{
		var builder = new LinkedGameObjectBuilder();
		builder.AddConnected( go );

		var toRemove = new List<GameObject>();
		foreach ( var linked in builder.Objects )
			toRemove.AddRange( FindConstraints( linked, go ) );

		foreach ( var host in toRemove )
			host.Destroy();
	}

	/// <summary>
	/// Lets tools define what constraints should be removed when removing constraints from a game object.
	/// </summary>
	/// <param name="linked"></param>
	/// <param name="target"></param>
	/// <returns></returns>
	protected virtual IEnumerable<GameObject> FindConstraints( GameObject linked, GameObject target ) => [];

	protected abstract void CreateConstraint( SelectionPoint point1, SelectionPoint point2 );

	protected virtual SelectionPoint? GetSecondaryPoint( SelectionPoint select ) => default;

	/// <summary>
	/// Create this constraint between two given points in one go, instead of through the two-click
	/// flow in <see cref="OnControl"/>.
	/// </summary>
	/// <remarks>
	/// The agent bridge's way into the constraint tools. They can't go through
	/// <see cref="ToolMode.PerformAction"/> like the rest, because they read input directly here
	/// rather than registering actions - there is no callback for it to invoke. This raises the same
	/// pre- and post-action events a real second click does, so limits and undo still apply.
	/// </remarks>
	/// <returns>False if the two points don't make a valid constraint, or a limit refused it.</returns>
	public bool PerformConstraint( SelectionPoint point1, SelectionPoint point2 )
	{
		if ( IsProxy ) return false;
		if ( !UpdateValidity( point1, point2 ) ) return false;

		if ( !FireToolAction( ToolInput.Primary ) )
			return false;

		ClearTracked();

		Create( point1, point2 );

		FirePostToolAction( ToolInput.Primary );

		return true;
	}

	/// <summary>
	/// Strip the constraints this tool manages off an object and everything linked to it - the
	/// programmatic equivalent of the tool's reload action.
	/// </summary>
	public bool PerformRemoveConstraints( GameObject go )
	{
		if ( IsProxy ) return false;
		if ( !go.IsValid() ) return false;

		if ( !FireToolAction( ToolInput.Reload ) )
			return false;

		RemoveConstraints( go.Network.RootGameObject ?? go );

		FirePostToolAction( ToolInput.Reload );

		return true;
	}
}
