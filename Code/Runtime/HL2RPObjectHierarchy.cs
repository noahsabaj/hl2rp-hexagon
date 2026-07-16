#nullable enable

using System;
using System.Collections.Generic;

namespace HL2RP.V2.Runtime;

/// <summary>Engine-neutral ancestor walk used by authority hit resolution.</summary>
public static class HL2RPObjectHierarchy
{
	public static bool Contains<T>( T root, T? candidate, Func<T, T?> parent ) where T : class
	{
		ArgumentNullException.ThrowIfNull( root );
		ArgumentNullException.ThrowIfNull( parent );
		var visited = new HashSet<T>( ReferenceEqualityComparer.Instance );
		for ( var current = candidate; current is not null && visited.Add( current ); current = parent( current ) )
			if ( ReferenceEquals( current, root ) ) return true;
		return false;
	}
}
