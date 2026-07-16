#nullable enable

using HL2RP.V2.Runtime;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class ObjectHierarchyTests
{
	private sealed class Node
	{
		public Node? Parent { get; init; }
	}

	[TestMethod]
	public void ChildColliderResolvesToAuthoritativeRoot()
	{
		var root = new Node();
		var colliders = new Node { Parent = root };
		var capsule = new Node { Parent = colliders };
		Assert.IsTrue( HL2RPObjectHierarchy.Contains( root, capsule, node => node.Parent ) );
		Assert.IsFalse( HL2RPObjectHierarchy.Contains( root, new Node(), node => node.Parent ) );
	}

	[TestMethod]
	public void CyclesFailClosed()
	{
		var root = new Node();
		var cycle = new Node();
		Assert.IsFalse( HL2RPObjectHierarchy.Contains( root, cycle, _ => cycle ) );
	}
}
